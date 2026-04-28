using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CloudDrive.Core.SyncEngine;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncRoot;

public class SyncRootConnector : IDisposable
{
    public static readonly NTStatus NetworkUnavailableStatus = new(unchecked((int)0xC000CF06));

    private CF_CONNECTION_KEY _connectionKey;
    private CF_CALLBACK_REGISTRATION[]? _callbackTable;
    private bool _connected;
    private readonly ILogger<SyncRootConnector> _logger;
    private readonly ActiveCloudRequestTracker? _requestTracker;

    // Track in-flight and recently-completed FETCH_PLACEHOLDERS operations per path.
    // cfapi fires callbacks repeatedly until it sees DISABLE_ON_DEMAND_POPULATION.
    // This dictionary blocks both concurrent AND rapid sequential re-fetches by
    // keeping the path locked for a cooldown period after completion.
    private readonly ConcurrentDictionary<string, DateTime> _fetchPlaceholdersCooldown = new();
    private static readonly TimeSpan FetchCooldown = TimeSpan.FromSeconds(5);

    public event Func<CF_CALLBACK_INFO, CF_CALLBACK_PARAMETERS, Task>? FetchPlaceholdersRequested;
    public event Func<CF_CALLBACK_INFO, CF_CALLBACK_PARAMETERS, Task>? FetchDataRequested;
    public event Func<CF_CALLBACK_INFO, CF_CALLBACK_PARAMETERS, Task>? CancelFetchDataRequested;
    public event Func<CF_CALLBACK_INFO, CF_CALLBACK_PARAMETERS, Task>? FileOpenCompleted;
    public event Func<CF_CALLBACK_INFO, CF_CALLBACK_PARAMETERS, Task>? FileCloseCompleted;

    public SyncRootConnector(ILogger<SyncRootConnector> logger, ActiveCloudRequestTracker? requestTracker = null)
    {
        _logger = logger;
        _requestTracker = requestTracker;
    }

    public CF_CONNECTION_KEY ConnectionKey => _connectionKey;

    public void Connect(string syncRootPath)
    {
        if (_connected)
            return;

        // Store as field to prevent GC of callback delegates while connected.
        // cfapi stores pointers to these delegates — if the array is a local variable,
        // the GC can collect the delegates after Connect() returns.
        _callbackTable = new CF_CALLBACK_REGISTRATION[]
        {
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = OnFetchPlaceholders
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = OnFetchData
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,
                Callback = OnCancelFetchData
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION,
                Callback = OnFileOpenCompletion
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION,
                Callback = OnFileCloseCompletion
            },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        };

        var hr = CfConnectSyncRoot(
            syncRootPath,
            _callbackTable,
            IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO
            | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out _connectionKey);

        hr.ThrowIfFailed("CfConnectSyncRoot failed");
        _connected = true;
        _logger.LogInformation("Connected to sync root: {Path}", syncRootPath);
    }

    public void Disconnect()
    {
        if (!_connected) return;

        CfDisconnectSyncRoot(_connectionKey);
        _callbackTable = null;
        _fetchPlaceholdersCooldown.Clear();
        _connected = false;
        _logger.LogInformation("Disconnected from sync root");
    }

    // --- FETCH_PLACEHOLDERS: async with per-path deduplication ---
    // If a fetch is already in-flight for this path, duplicate callbacks are dropped
    // without calling CfExecute. The in-flight handler will acknowledge with the real data.

    private void OnFetchPlaceholders(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var path = callbackInfo.NormalizedPath;

        // Block if a fetch is in-flight OR completed within the cooldown window.
        // This prevents both concurrent duplicates and rapid sequential re-fetches
        // that cfapi fires before it processes DISABLE_ON_DEMAND_POPULATION.
        if (_fetchPlaceholdersCooldown.TryGetValue(path, out var lastTime))
        {
            if (lastTime == DateTime.MaxValue || (DateTime.UtcNow - lastTime) < FetchCooldown)
            {
                _logger.LogDebug("FETCH_PLACEHOLDERS deduplicated (cooldown): {Path}", path);
                return;
            }
        }

        // Mark as in-flight (MaxValue = currently running)
        _fetchPlaceholdersCooldown[path] = DateTime.MaxValue;
        _requestTracker?.BeginFetchPlaceholders(callbackInfo);

        _logger.LogDebug("FETCH_PLACEHOLDERS callback: {Path}", path);
        var info = callbackInfo;
        var parms = callbackParameters;
        _ = Task.Run(async () =>
        {
            try
            {
                if (FetchPlaceholdersRequested != null)
                    await FetchPlaceholdersRequested(info, parms);
                else
                    _logger.LogWarning("FETCH_PLACEHOLDERS: no handler registered for {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FETCH_PLACEHOLDERS handler for {Path}", path);
                ReportFetchPlaceholdersError(info, NetworkUnavailableStatus, _logger);
            }
            finally
            {
                // Keep the entry with completion timestamp for cooldown
                _fetchPlaceholdersCooldown[path] = DateTime.UtcNow;
                _requestTracker?.CompleteFetchPlaceholders(path, "completed");
            }
        });
    }

    // --- Other FETCH callbacks: async fire-and-forget ---

    private void OnFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        _requestTracker?.BeginFetchData(callbackInfo, callbackParameters);
        _logger.LogInformation("FETCH_DATA callback: {Path}", callbackInfo.NormalizedPath);

        var info = callbackInfo;
        var parms = callbackParameters;
        _ = Task.Run(async () =>
        {
            try
            {
                if (FetchDataRequested != null)
                    await FetchDataRequested(info, parms);
                else
                    _logger.LogWarning("FETCH_DATA: no handler registered for {Path}", info.NormalizedPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FETCH_DATA handler for {Path}", info.NormalizedPath);
                ReportTransferFailure(info);
            }
        });
    }

    private void OnCancelFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        _requestTracker?.MarkCancelFetchData(callbackInfo);
        _logger.LogDebug("CANCEL_FETCH_DATA callback: {Path}", callbackInfo.NormalizedPath);
        var info = callbackInfo;
        var parms = callbackParameters;
        _ = Task.Run(async () =>
        {
            try
            {
                if (CancelFetchDataRequested != null)
                    await CancelFetchDataRequested(info, parms);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CANCEL_FETCH_DATA handler");
            }
        });
    }

    private void OnFileOpenCompletion(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        _logger.LogDebug("NOTIFY_FILE_OPEN_COMPLETION callback: {Path}", callbackInfo.NormalizedPath);
        var info = callbackInfo;
        var parms = callbackParameters;
        _ = Task.Run(async () =>
        {
            try
            {
                if (FileOpenCompleted != null)
                    await FileOpenCompleted(info, parms);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in NOTIFY_FILE_OPEN_COMPLETION handler for {Path}", info.NormalizedPath);
            }
        });
    }

    private void OnFileCloseCompletion(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        _logger.LogDebug("NOTIFY_FILE_CLOSE_COMPLETION callback: {Path}", callbackInfo.NormalizedPath);
        var info = callbackInfo;
        var parms = callbackParameters;
        _ = Task.Run(async () =>
        {
            try
            {
                if (FileCloseCompleted != null)
                    await FileCloseCompleted(info, parms);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in NOTIFY_FILE_CLOSE_COMPLETION handler for {Path}", info.NormalizedPath);
            }
        });
    }

    /// <summary>
    /// Signals the provider's current status to Windows (IDLE, CONNECTIVITY_LOST, DISCONNECTED, etc.).
    /// Connection-scoped — auto-clears when the process dies.
    /// </summary>
    public void UpdateSyncProviderStatus(CF_SYNC_PROVIDER_STATUS status)
    {
        if (!_connected)
        {
            _logger.LogDebug("UpdateSyncProviderStatus skipped — not connected");
            return;
        }

        var hr = CfUpdateSyncProviderStatus(_connectionKey, status);
        if (hr.Succeeded)
            _logger.LogDebug("CfUpdateSyncProviderStatus set to {Status}", status);
        else
            _logger.LogWarning("CfUpdateSyncProviderStatus failed: {HR}", hr);
    }

    /// <summary>
    /// Sets a persistent sync status message on the sync root path (visible in Explorer even after crash).
    /// </summary>
    public static void ReportSyncStatus(string syncRootPath, string description)
    {
        // CF_SYNC_STATUS is a variable-length struct with embedded string.
        // We must allocate and marshal it manually.
        var descBytes = System.Text.Encoding.Unicode.GetBytes(description);
        var structSize = Marshal.SizeOf<CF_SYNC_STATUS>() + descBytes.Length;

        var ptr = Marshal.AllocHGlobal(structSize);
        try
        {
            // Zero out the memory
            for (int i = 0; i < structSize; i++)
                Marshal.WriteByte(ptr, i, 0);

            var syncStatus = new CF_SYNC_STATUS
            {
                StructSize = (uint)structSize,
                Code = 0, // Custom
                DescriptionOffset = (uint)Marshal.SizeOf<CF_SYNC_STATUS>(),
                DescriptionLength = (uint)descBytes.Length
            };

            Marshal.StructureToPtr(syncStatus, ptr, false);
            Marshal.Copy(descBytes, 0, ptr + Marshal.SizeOf<CF_SYNC_STATUS>(), descBytes.Length);

            CfReportSyncStatus(syncRootPath, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Clears any persistent sync status on the sync root path.
    /// </summary>
    public static void ClearSyncStatus(string syncRootPath)
    {
        CfReportSyncStatus(syncRootPath, IntPtr.Zero);
    }

    public static void ReportFetchPlaceholdersError(
        CF_CALLBACK_INFO callbackInfo,
        NTStatus status,
        ILogger? logger = null)
    {
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = callbackInfo.ConnectionKey,
                TransferKey = callbackInfo.TransferKey,
                RequestKey = callbackInfo.RequestKey
            };

            var transferPlaceholders = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
            {
                CompletionStatus = status,
                PlaceholderArray = IntPtr.Zero,
                PlaceholderCount = 0,
                PlaceholderTotalCount = 0
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(transferPlaceholders);
            var hr = CfExecute(opInfo, ref opParams);
            if (hr.Failed)
                logger?.LogWarning("ReportFetchPlaceholdersError CfExecute failed: {Hr}", hr);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "ReportFetchPlaceholdersError itself failed");
        }
    }

    public static void ReportFetchDataError(
        CF_CALLBACK_INFO callbackInfo,
        NTStatus status,
        long offset = 0,
        long? length = null,
        ILogger? logger = null)
    {
        try
        {
            var validOffset = Math.Max(offset, 0);
            var validLength = length.GetValueOrDefault();
            if (validLength <= 0)
                validLength = Math.Max(callbackInfo.FileSize - validOffset, 1);

            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = callbackInfo.ConnectionKey,
                TransferKey = callbackInfo.TransferKey,
                RequestKey = callbackInfo.RequestKey
            };

            var transferData = new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                CompletionStatus = status,
                Buffer = IntPtr.Zero,
                Length = validLength,
                Offset = validOffset
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(transferData);
            var hr = CfExecute(opInfo, ref opParams);
            if (hr.Failed)
                logger?.LogWarning("ReportFetchDataError CfExecute failed: {Hr}", hr);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "ReportFetchDataError itself failed");
        }
    }

    public static void AcknowledgeDelete(
        CF_CALLBACK_INFO callbackInfo,
        NTStatus status,
        ILogger? logger = null)
    {
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
                ConnectionKey = callbackInfo.ConnectionKey,
                TransferKey = callbackInfo.TransferKey,
                RequestKey = callbackInfo.RequestKey
            };

            var acknowledgeDelete = new CF_OPERATION_PARAMETERS.ACKDELETE
            {
                Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE,
                CompletionStatus = status
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(acknowledgeDelete);
            var hr = CfExecute(opInfo, ref opParams);
            if (hr.Failed)
                logger?.LogWarning("AcknowledgeDelete CfExecute failed: {Hr}", hr);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "AcknowledgeDelete itself failed");
        }
    }

    public static void AcknowledgeRename(
        CF_CALLBACK_INFO callbackInfo,
        NTStatus status,
        ILogger? logger = null)
    {
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME,
                ConnectionKey = callbackInfo.ConnectionKey,
                TransferKey = callbackInfo.TransferKey,
                RequestKey = callbackInfo.RequestKey
            };

            var acknowledgeRename = new CF_OPERATION_PARAMETERS.ACKRENAME
            {
                Flags = CF_OPERATION_ACK_RENAME_FLAGS.CF_OPERATION_ACK_RENAME_FLAG_NONE,
                CompletionStatus = status
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(acknowledgeRename);
            var hr = CfExecute(opInfo, ref opParams);
            if (hr.Failed)
                logger?.LogWarning("AcknowledgeRename CfExecute failed: {Hr}", hr);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "AcknowledgeRename itself failed");
        }
    }

    /// <summary>
    /// Last-resort failure report to Windows when a callback handler crashes unexpectedly.
    /// Sends TRANSFER_DATA with a failure CompletionStatus so the placeholder doesn't get stuck.
    /// </summary>
    private void ReportTransferFailure(CF_CALLBACK_INFO callbackInfo)
    {
        ReportFetchDataError(callbackInfo, NetworkUnavailableStatus, logger: _logger);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
