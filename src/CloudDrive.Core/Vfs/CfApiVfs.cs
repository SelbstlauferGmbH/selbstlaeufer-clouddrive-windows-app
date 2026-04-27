using System.Runtime.InteropServices;
using CloudDrive.Core.SyncRoot;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.Vfs;

public sealed class CfApiVfs : IVfs
{
    private readonly SyncRootRegistrar _registrar;
    private readonly SyncRootConnector _connector;
    private readonly ILogger<CfApiVfs> _logger;
    private readonly Func<string, string>? _remotePathFallback;
    private string? _syncRootPath;
    private string? _accountId;

    public CfApiVfs(
        SyncRootRegistrar registrar,
        SyncRootConnector connector,
        ILogger<CfApiVfs> logger,
        Func<string, string>? remotePathFallback = null)
    {
        _registrar = registrar;
        _connector = connector;
        _logger = logger;
        _remotePathFallback = remotePathFallback;
    }

    public event EventHandler<VfsPinChangedEvent>? PinStateChanged;

    public async Task<VfsResult> RegisterAsync(VfsRegistration registration, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            _syncRootPath = registration.SyncRootPath;
            _accountId = registration.AccountId;
            await _registrar.RegisterAsync(registration.SyncRootPath, registration.AccountId);
            return VfsResult.Ok();
        }
        catch (Exception ex)
        {
            return VfsResult.Fail(VfsErrorCode.RegistrationFailed, ex.Message, ex);
        }
    }

    public Task<VfsResult> ConnectAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_syncRootPath))
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.InvalidOperation, "CfApiVfs has not been registered."));

            _connector.Connect(_syncRootPath);
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public Task<VfsResult> DisconnectAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            _connector.Disconnect();
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public Task<VfsResult> CreatePlaceholderAsync(string localPath, VfsMetadata metadata, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            var identity = VfsIdentityCodec.Pack(new VfsIdentity(metadata.FileId, metadata.ETag, metadata.RemotePath));
            var identityHandle = GCHandle.Alloc(identity, GCHandleType.Pinned);
            try
            {
                var fileName = Path.GetFileName(localPath);
                var createInfo = new[]
                {
                    new CF_PLACEHOLDER_CREATE_INFO
                    {
                        RelativeFileName = fileName,
                        Flags = metadata.InSync
                            ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                            : CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_NONE,
                        FsMetadata = ToFsMetadata(metadata),
                        FileIdentity = identityHandle.AddrOfPinnedObject(),
                        FileIdentityLength = (uint)identity.Length
                    }
                };

                var hr = CfCreatePlaceholders(
                    Path.GetDirectoryName(localPath)!,
                    createInfo,
                    1,
                    CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                    out _);

                if (hr.Failed)
                    return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, $"CfCreatePlaceholders failed: {hr}"));
            }
            finally
            {
                identityHandle.Free();
            }

            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public Task<VfsResult> ConvertToPlaceholderAsync(string localPath, VfsMetadata metadata, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var handle = OpenHandle(
                localPath,
                Kernel32.FileAccess.FILE_WRITE_DATA,
                metadata.IsDirectory);

            if (handle.IsInvalid)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"Cannot open path: {localPath}"));

            var identity = VfsIdentityCodec.Pack(new VfsIdentity(metadata.FileId, metadata.ETag, metadata.RemotePath));
            var identityHandle = GCHandle.Alloc(identity, GCHandleType.Pinned);
            try
            {
                var flags = metadata.InSync
                    ? CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC
                    : CF_CONVERT_FLAGS.CF_CONVERT_FLAG_NONE;

                var hr = CfConvertToPlaceholder(
                    handle,
                    identityHandle.AddrOfPinnedObject(),
                    (uint)identity.Length,
                    flags,
                    out _,
                    IntPtr.Zero);

                if (hr.Failed)
                    return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, $"CfConvertToPlaceholder failed: {hr}"));
            }
            finally
            {
                identityHandle.Free();
            }

            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public async Task<VfsResult> HydrateAsync(
        string localPath,
        Stream content,
        long expectedSize,
        IProgress<VfsTransferProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var tempPath = Path.Combine(Path.GetDirectoryName(localPath)!, $".clouddrive-download-{Guid.NewGuid():N}.tmp");
            long copied = 0;
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read == 0)
                        break;

                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;
                    progress?.Report(new VfsTransferProgress(copied, expectedSize));
                }
            }

            if (expectedSize >= 0 && copied != expectedSize)
            {
                File.Delete(tempPath);
                return VfsResult.Fail(VfsErrorCode.IoError, $"Hydration size mismatch for {localPath}.");
            }

            File.Move(tempPath, localPath, overwrite: true);
            return VfsResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return VfsResult.Fail(VfsErrorCode.Cancelled, "Hydration cancelled.");
        }
        catch (Exception ex)
        {
            return VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex);
        }
    }

    public Task<VfsResult> DehydrateAsync(string localPath, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var handle = OpenHandle(localPath, Kernel32.FileAccess.FILE_WRITE_DATA, isDirectory: false);
            if (handle.IsInvalid)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"Cannot open path: {localPath}"));

            var hr = CfDehydratePlaceholder(handle, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, IntPtr.Zero);
            return Task.FromResult(hr.Succeeded
                ? VfsResult.Ok()
                : VfsResult.Fail(VfsErrorCode.PlatformError, $"CfDehydratePlaceholder failed: {hr}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public Task<VfsResult> UpdateMetadataAsync(string localPath, VfsMetadata metadata, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var handle = OpenHandle(localPath, Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES, metadata.IsDirectory);
            if (handle.IsInvalid)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"Cannot open path: {localPath}"));

            var identity = VfsIdentityCodec.Pack(new VfsIdentity(metadata.FileId, metadata.ETag, metadata.RemotePath));
            var identityHandle = GCHandle.Alloc(identity, GCHandleType.Pinned);
            try
            {
                long usn = 0;
                var flags = metadata.InSync
                    ? CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC
                    : CF_UPDATE_FLAGS.CF_UPDATE_FLAG_NONE;
                var hr = CfUpdatePlaceholder(
                    handle,
                    ToFsMetadata(metadata),
                    identityHandle.AddrOfPinnedObject(),
                    (uint)identity.Length,
                    null,
                    0,
                    flags,
                    ref usn,
                    IntPtr.Zero);

                if (hr.Failed)
                    return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, $"CfUpdatePlaceholder failed: {hr}"));
            }
            finally
            {
                identityHandle.Free();
            }

            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public Task<VfsResult<PinState>> GetPinStateAsync(string localPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(!Helpers.CloudFilePlaceholderHelper.TryGetPlaceholderState(localPath, out var state) || state is null
            ? VfsResult<PinState>.Fail(VfsErrorCode.NotFound, $"Placeholder state unavailable: {localPath}")
            : VfsResult<PinState>.Ok(MapPinState(state.PinState)));
    }

    public Task<VfsResult> SetPinStateAsync(string localPath, PinState state, PinDescent descent, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var handle = OpenHandle(localPath, Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES, Directory.Exists(localPath));
            if (handle.IsInvalid)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"Cannot open path: {localPath}"));

            var hr = CfSetPinState(handle, MapPinState(state), MapPinDescent(descent), IntPtr.Zero);
            if (hr.Failed)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, $"CfSetPinState failed: {hr}"));

            PinStateChanged?.Invoke(this, new VfsPinChangedEvent(localPath, state, descent));
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public Task<VfsResult> SetInSyncAsync(string localPath, bool inSync, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var handle = OpenHandle(localPath, Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES, Directory.Exists(localPath));
            if (handle.IsInvalid)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"Cannot open path: {localPath}"));

            var hr = CfSetInSyncState(
                handle,
                inSync ? CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC : CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC,
                CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                IntPtr.Zero);

            return Task.FromResult(hr.Succeeded
                ? VfsResult.Ok()
                : VfsResult.Fail(VfsErrorCode.PlatformError, $"CfSetInSyncState failed: {hr}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.PlatformError, ex.Message, ex));
        }
    }

    public Task<PlaceholderInfo?> GetPlaceholderInfoAsync(string localPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Helpers.CloudFilePlaceholderHelper.TryGetPlaceholderState(localPath, out var state) || state is null)
            return Task.FromResult<PlaceholderInfo?>(null);

        var remotePath = _remotePathFallback?.Invoke(localPath) ?? localPath;
        var fileId = SyncEngine.SyncIdentity.RemotePathFallbackId(remotePath);
        string? etag = null;
        if (TryReadPlaceholderIdentity(localPath, state.IsDirectory, out var identity))
        {
            fileId = identity.FileId;
            remotePath = identity.RemotePath;
            etag = identity.ETag;
        }

        var info = new PlaceholderInfo(
            localPath,
            FileId: fileId,
            RemotePath: remotePath,
            ETag: etag,
            LogicalSize: state.FileSize,
            MTimeUtc: Directory.Exists(localPath)
                ? Directory.GetLastWriteTimeUtc(localPath)
                : File.GetLastWriteTimeUtc(localPath),
            IsDirectory: state.IsDirectory,
            InSync: state.IsInSync,
            PinState: MapPinState(state.PinState),
            HydrationState: state.NeedsHydration ? VfsHydrationState.Dehydrated : VfsHydrationState.Hydrated);

        return Task.FromResult<PlaceholderInfo?>(info);
    }

    public async Task<IReadOnlyList<VfsEntry>> EnumerateChildrenAsync(string localDirectoryPath, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth < 1 || !Directory.Exists(localDirectoryPath))
            return [];

        var entries = new List<VfsEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(localDirectoryPath))
        {
            ct.ThrowIfCancellationRequested();
            var isDirectory = Directory.Exists(path);
            entries.Add(new VfsEntry(
                path,
                Path.GetFileName(path),
                isDirectory,
                await GetPlaceholderInfoAsync(path, ct)));

            if (isDirectory && depth > 1)
                entries.AddRange(await EnumerateChildrenAsync(path, depth - 1, ct));
        }

        return entries;
    }

    public Task<VfsResult> DeleteAsync(string localPath, bool recursive, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(localPath))
                Directory.Delete(localPath, recursive);
            else if (File.Exists(localPath))
                File.Delete(localPath);

            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
        }
    }

    public ValueTask DisposeAsync()
    {
        _connector.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CF_FS_METADATA ToFsMetadata(VfsMetadata metadata)
    {
        var ft = ToFileTime(metadata.MTimeUtc == default ? DateTime.UtcNow : metadata.MTimeUtc);
        return new CF_FS_METADATA
        {
            FileSize = metadata.IsDirectory ? 0 : metadata.LogicalSize,
            BasicInfo = new Kernel32.FILE_BASIC_INFO
            {
                FileAttributes = metadata.IsDirectory
                    ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY
                    : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                CreationTime = ft,
                LastWriteTime = ft,
                LastAccessTime = ft,
                ChangeTime = ft
            }
        };
    }

    private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTime(DateTime dt)
    {
        var ft = dt.ToUniversalTime().ToFileTimeUtc();
        return new System.Runtime.InteropServices.ComTypes.FILETIME
        {
            dwLowDateTime = (int)(ft & 0xFFFFFFFF),
            dwHighDateTime = (int)(ft >> 32)
        };
    }

    private static Kernel32.SafeHFILE OpenHandle(string path, Kernel32.FileAccess access, bool isDirectory)
    {
        var flags = FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT;
        if (isDirectory)
            flags |= FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS;

        return Kernel32.CreateFile(
            path,
            access,
            FileShare.ReadWrite | FileShare.Delete,
            null,
            FileMode.Open,
            flags,
            IntPtr.Zero);
    }

    private static bool TryReadPlaceholderIdentity(string path, bool isDirectory, out VfsIdentity identity)
    {
        identity = new VfsIdentity(string.Empty, null, string.Empty);

        try
        {
            using var handle = OpenHandle(path, Kernel32.FileAccess.FILE_READ_ATTRIBUTES, isDirectory);
            if (handle.IsInvalid)
                return false;

            var info = CfGetPlaceholderInfo<CF_PLACEHOLDER_STANDARD_INFO>(handle);
            if (info.FileIdentity is null || info.FileIdentity.Length == 0 || info.FileIdentityLength == 0)
                return false;

            var length = Math.Min(checked((int)info.FileIdentityLength), info.FileIdentity.Length);
            return VfsIdentityCodec.TryUnpack(info.FileIdentity.AsSpan(0, length), out identity);
        }
        catch
        {
            return false;
        }
    }

    private static PinState MapPinState(CF_PIN_STATE state) => state switch
    {
        CF_PIN_STATE.CF_PIN_STATE_PINNED => PinState.Pinned,
        CF_PIN_STATE.CF_PIN_STATE_UNPINNED => PinState.Unpinned,
        CF_PIN_STATE.CF_PIN_STATE_EXCLUDED => PinState.Excluded,
        CF_PIN_STATE.CF_PIN_STATE_INHERIT => PinState.Inherit,
        _ => PinState.Unspecified
    };

    private static CF_PIN_STATE MapPinState(PinState state) => state switch
    {
        PinState.Pinned => CF_PIN_STATE.CF_PIN_STATE_PINNED,
        PinState.Unpinned => CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
        PinState.Excluded => CF_PIN_STATE.CF_PIN_STATE_EXCLUDED,
        PinState.Inherit => CF_PIN_STATE.CF_PIN_STATE_INHERIT,
        _ => CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED
    };

    private static CF_SET_PIN_FLAGS MapPinDescent(PinDescent descent) => descent switch
    {
        PinDescent.Recursive => CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_RECURSE,
        PinDescent.RecursiveOnly => CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_RECURSE_ONLY,
        _ => CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE
    };
}
