using System.Runtime.InteropServices;
using System.Text;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncEngine;

public class PlaceholderManager
{
    private readonly IWebDavService _webDav;
    private readonly ISyncItemStateService _stateService;
    private readonly PathMapper _pathMapper;
    private readonly ILogger<PlaceholderManager> _logger;
    private CF_CONNECTION_KEY _connectionKey;
    private volatile bool _webDavReady;

    // Throttle repeated failures to prevent tight callback retry loops
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentErrors = new();
    private static readonly TimeSpan ErrorCooldown = TimeSpan.FromSeconds(30);

    public PlaceholderManager(
        IWebDavService webDav,
        ISyncItemStateService stateService,
        PathMapper pathMapper,
        ILogger<PlaceholderManager> logger)
    {
        _webDav = webDav;
        _stateService = stateService;
        _pathMapper = pathMapper;
        _logger = logger;
    }

    public void SetConnectionKey(CF_CONNECTION_KEY key) => _connectionKey = key;
    public void SetWebDavReady(bool ready) => _webDavReady = ready;

    private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTime(DateTime dt)
    {
        long ft = dt.ToFileTimeUtc();
        return new System.Runtime.InteropServices.ComTypes.FILETIME
        {
            dwLowDateTime = (int)(ft & 0xFFFFFFFF),
            dwHighDateTime = (int)(ft >> 32)
        };
    }

    public async Task HandleFetchPlaceholdersAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var activityId = Guid.NewGuid().ToString("N")[..8];
        var normalizedPath = callbackInfo.NormalizedPath;

        if (!_webDavReady)
        {
            _logger.LogDebug("PlaceholderManager[Fetch] ActivityId={ActivityId} WebDAV not ready, rejecting: {Path}",
                activityId, normalizedPath);
            ReportError(callbackInfo);
            return;
        }

        // Throttle: if this path failed recently, report error immediately to avoid tight retry loop
        if (_recentErrors.TryGetValue(normalizedPath, out var lastError) &&
            DateTime.UtcNow - lastError < ErrorCooldown)
        {
            _logger.LogDebug("PlaceholderManager[Fetch] ActivityId={ActivityId} Throttled={Path} FailedAgo={Seconds}s",
                activityId, normalizedPath, (DateTime.UtcNow - lastError).TotalSeconds);
            ReportError(callbackInfo);
            return;
        }

        _logger.LogInformation("PlaceholderManager[Fetch] ActivityId={ActivityId} NormalizedPath={Path}", 
            activityId, normalizedPath);

        try
        {
            var remotePath = _pathMapper.NormalizedPathToRemotePath(normalizedPath);
            _logger.LogDebug("PlaceholderManager[Fetch] ActivityId={ActivityId} RemotePath={RemotePath}", 
                activityId, remotePath);

            var items = (await _webDav.ListDirectoryAsync(remotePath))
                .Where(item => !TransientFilePolicy.ShouldIgnoreRemotePath(item.RemotePath, item.IsDirectory))
                .ToList();
            _logger.LogInformation("PlaceholderManager[Fetch] ActivityId={ActivityId} ItemsFetched={Count}", 
                activityId, items.Count);

            if (items.Count == 0)
            {
                _logger.LogInformation("PlaceholderManager[Fetch] ActivityId={ActivityId} EmptyDirectory={Path}", 
                    activityId, normalizedPath);
                TransferPlaceholders(callbackInfo, [], 0);
                return;
            }

            var createInfos = new CF_PLACEHOLDER_CREATE_INFO[items.Count];
            var handles = new List<GCHandle>();

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var fileIdentity = Encoding.UTF8.GetBytes(item.RemotePath);
                    var handle = GCHandle.Alloc(fileIdentity, GCHandleType.Pinned);
                    handles.Add(handle);

                    var ft = ToFileTime(item.LastModified);

                    createInfos[i] = new CF_PLACEHOLDER_CREATE_INFO
                    {
                        RelativeFileName = item.Name,
                        Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                        FsMetadata = new CF_FS_METADATA
                        {
                            FileSize = item.Size,
                            BasicInfo = new Kernel32.FILE_BASIC_INFO
                            {
                                FileAttributes = item.IsDirectory
                                    ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY
                                    : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                                CreationTime = ft,
                                LastWriteTime = ft,
                                LastAccessTime = ft,
                                ChangeTime = ft
                            }
                        },
                        FileIdentity = handle.AddrOfPinnedObject(),
                        FileIdentityLength = (uint)fileIdentity.Length
                    };

                    var localPath = _pathMapper.ToLocalPath(item.RemotePath);
                    _stateService.Upsert(new SyncItem
                    {
                        LocalPath = localPath,
                        RemotePath = item.RemotePath,
                        IsDirectory = item.IsDirectory,
                        FileSize = item.Size,
                        RemoteETag = item.ETag,
                        RemoteLastModified = item.LastModified,
                        SyncStatus = SyncStatus.CloudOnly
                    });
                }

                TransferPlaceholders(callbackInfo, createInfos, (uint)createInfos.Length);
                _recentErrors.TryRemove(normalizedPath, out _);
                _logger.LogInformation("PlaceholderManager[Fetch] ActivityId={ActivityId} TRANSFER_PLACEHOLDERS complete: {Count} items for {Path}",
                    activityId, createInfos.Length, normalizedPath);
            }
            finally
            {
                foreach (var h in handles)
                    h.Free();
            }
        }
        catch (Exception ex)
        {
            _recentErrors[normalizedPath] = DateTime.UtcNow;
            _logger.LogError(ex, "PlaceholderManager[Fetch] ActivityId={ActivityId} Failed={Path}", 
                activityId, normalizedPath);
            ReportError(callbackInfo);
        }
    }

    public bool UpdatePlaceholderMetadata(string localPath, long fileSize, DateTime lastModified, string? remotePath = null)
    {
        try
        {
            var isDirectory = Directory.Exists(localPath);
            if (!isDirectory && !File.Exists(localPath))
            {
                _logger.LogWarning("Cannot update placeholder metadata because the path does not exist: {Path}", localPath);
                return false;
            }

            var ft = ToFileTime(lastModified);
            var fsMetadata = new CF_FS_METADATA
            {
                FileSize = isDirectory ? 0 : fileSize,
                BasicInfo = new Kernel32.FILE_BASIC_INFO
                {
                    FileAttributes = isDirectory
                        ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY
                        : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                    LastWriteTime = ft,
                    LastAccessTime = ft,
                    ChangeTime = ft
                }
            };

            var handleFlags = FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT;
            if (isDirectory)
                handleFlags |= FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS;

            using var handle = Kernel32.CreateFile(localPath,
                Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete,
                null,
                System.IO.FileMode.Open,
                handleFlags);

            if (handle.IsInvalid)
            {
                _logger.LogWarning("Cannot open placeholder for metadata update: {Path}", localPath);
                return false;
            }

            byte[]? fileIdentity = null;
            GCHandle fileIdentityHandle = default;
            long usn = 0;
            try
            {
                var fileIdentityPtr = IntPtr.Zero;
                uint fileIdentityLength = 0;
                if (!string.IsNullOrWhiteSpace(remotePath))
                {
                    fileIdentity = Encoding.UTF8.GetBytes(remotePath);
                    fileIdentityHandle = GCHandle.Alloc(fileIdentity, GCHandleType.Pinned);
                    fileIdentityPtr = fileIdentityHandle.AddrOfPinnedObject();
                    fileIdentityLength = (uint)fileIdentity.Length;
                }

                var hr = CfUpdatePlaceholder(handle, fsMetadata, fileIdentityPtr, fileIdentityLength, null,
                    0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, ref usn, IntPtr.Zero);

                if (hr.Failed)
                {
                    _logger.LogWarning("CfUpdatePlaceholder failed for {Path}: {Hr}", localPath, hr);
                    return false;
                }
            }
            finally
            {
                if (fileIdentityHandle.IsAllocated)
                    fileIdentityHandle.Free();
            }

            _logger.LogDebug(
                "Updated placeholder metadata: {Path} Size={Size} Modified={Modified} RemotePath={RemotePath}",
                localPath,
                fileSize,
                lastModified,
                remotePath ?? "<unchanged>");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update placeholder metadata: {Path}", localPath);
            return false;
        }
    }

    public void CreatePlaceholdersInDirectory(string localDirectoryPath, IReadOnlyList<RemoteItem> items)
    {
        foreach (var item in items.Where(item => !TransientFilePolicy.ShouldIgnoreRemotePath(item.RemotePath, item.IsDirectory)))
        {
            var fileIdentity = Encoding.UTF8.GetBytes(item.RemotePath);
            var handle = GCHandle.Alloc(fileIdentity, GCHandleType.Pinned);

            try
            {
                var ft = ToFileTime(item.LastModified);

                var createInfo = new CF_PLACEHOLDER_CREATE_INFO[]
                {
                    new()
                    {
                        RelativeFileName = item.Name,
                        Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                        FsMetadata = new CF_FS_METADATA
                        {
                            FileSize = item.Size,
                            BasicInfo = new Kernel32.FILE_BASIC_INFO
                            {
                                FileAttributes = item.IsDirectory
                                    ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY
                                    : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                                CreationTime = ft,
                                LastWriteTime = ft,
                                LastAccessTime = ft,
                                ChangeTime = ft
                            }
                        },
                        FileIdentity = handle.AddrOfPinnedObject(),
                        FileIdentityLength = (uint)fileIdentity.Length
                    }
                };

                var hr = CfCreatePlaceholders(localDirectoryPath, createInfo, 1, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _);
                if (hr.Failed)
                    _logger.LogWarning("Failed to create placeholder for {Name}: {Hr}", item.Name, hr);

                var localPath = Path.Combine(localDirectoryPath, item.Name);
                _stateService.Upsert(new SyncItem
                {
                    LocalPath = localPath,
                    RemotePath = item.RemotePath,
                    IsDirectory = item.IsDirectory,
                    FileSize = item.Size,
                    RemoteETag = item.ETag,
                    RemoteLastModified = item.LastModified,
                    SyncStatus = SyncStatus.CloudOnly
                });
            }
            finally
            {
                handle.Free();
            }
        }
    }

    private void TransferPlaceholders(CF_CALLBACK_INFO callbackInfo, CF_PLACEHOLDER_CREATE_INFO[] placeholders, uint count)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            ConnectionKey = _connectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey
        };

        // CF_PLACEHOLDER_CREATE_INFO contains managed references (strings) and cannot be
        // pinned directly. Marshal each element into a contiguous unmanaged memory block.
        var elementSize = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
        var arrayPtr = count > 0 ? Marshal.AllocHGlobal(elementSize * (int)count) : IntPtr.Zero;
        try
        {
            for (int i = 0; i < count; i++)
            {
                Marshal.StructureToPtr(placeholders[i], arrayPtr + elementSize * i, false);
            }

            var tp = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
            {
                Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION,
                CompletionStatus = new NTStatus(0),
                PlaceholderArray = arrayPtr,
                PlaceholderCount = count,
                PlaceholderTotalCount = count,
                EntriesProcessed = 0
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(tp);

            var hr = CfExecute(opInfo, ref opParams);
            if (hr.Failed)
                _logger.LogError("CfExecute TRANSFER_PLACEHOLDERS failed: {Hr}", hr);
        }
        finally
        {
            if (arrayPtr != IntPtr.Zero)
            {
                for (int i = 0; i < count; i++)
                    Marshal.DestroyStructure<CF_PLACEHOLDER_CREATE_INFO>(arrayPtr + elementSize * i);
                Marshal.FreeHGlobal(arrayPtr);
            }
        }
    }

    private void ReportError(CF_CALLBACK_INFO callbackInfo)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            ConnectionKey = _connectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey
        };

        var tp = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
        {
            CompletionStatus = new NTStatus(unchecked((int)0xC000CF06)),
            PlaceholderArray = IntPtr.Zero,
            PlaceholderCount = 0,
            PlaceholderTotalCount = 0
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(tp);
        CfExecute(opInfo, ref opParams);
    }
}
