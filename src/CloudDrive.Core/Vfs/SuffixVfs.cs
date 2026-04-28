using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Vfs;

public sealed class SuffixVfs : IVfs
{
    public const string PlaceholderSuffix = ".clouddrive";
    public const string MetadataSuffix = ".clouddrive-meta";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<SuffixVfs>? _logger;
    private string? _rootPath;

    public SuffixVfs(string? rootPath = null, ILogger<SuffixVfs>? logger = null)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath) ? null : Normalize(rootPath);
        _logger = logger;
    }

    public event EventHandler<VfsPinChangedEvent>? PinStateChanged;

    public Task<VfsResult> RegisterAsync(VfsRegistration registration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            _rootPath = Normalize(registration.SyncRootPath);
            Directory.CreateDirectory(_rootPath);
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.RegistrationFailed, ex.Message, ex));
        }
    }

    public Task<VfsResult> ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_rootPath))
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.InvalidOperation, "SuffixVfs has not been registered."));

        Directory.CreateDirectory(_rootPath);
        return Task.FromResult(VfsResult.Ok());
    }

    public Task<VfsResult> DisconnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(VfsResult.Ok());
    }

    public Task<VfsResult> CreatePlaceholderAsync(string localPath, VfsMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            EnsureInsideRoot(localPath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            if (metadata.IsDirectory)
            {
                Directory.CreateDirectory(localPath);
            }
            else
            {
                if (File.Exists(localPath))
                    File.Delete(localPath);

                using (File.Create(GetStubPath(localPath))) { }
            }

            WriteMetadata(localPath, metadata, metadata.IsDirectory ? VfsHydrationState.Hydrated : VfsHydrationState.Dehydrated);
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
        }
    }

    public Task<VfsResult> ConvertToPlaceholderAsync(string localPath, VfsMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            EnsureInsideRoot(localPath);
            if (metadata.IsDirectory)
            {
                Directory.CreateDirectory(localPath);
            }
            else if (!File.Exists(localPath))
            {
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"File does not exist: {localPath}"));
            }

            WriteMetadata(localPath, metadata, metadata.IsDirectory ? VfsHydrationState.Hydrated : VfsHydrationState.Hydrated);
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
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
            EnsureInsideRoot(localPath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var tempPath = Path.Combine(Path.GetDirectoryName(localPath)!, $".clouddrive-tmp-{Guid.NewGuid():N}");
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
                return VfsResult.Fail(
                    VfsErrorCode.IoError,
                    $"Hydration size mismatch for {localPath}. Expected {expectedSize}, copied {copied}.");
            }

            File.Move(tempPath, localPath, overwrite: true);
            var stubPath = GetStubPath(localPath);
            if (File.Exists(stubPath))
                File.Delete(stubPath);

            var metadata = ReadMetadata(localPath);
            if (metadata != null)
            {
                metadata.LogicalSize = copied;
                metadata.HydrationState = VfsHydrationState.Hydrated;
                metadata.MTimeUtc = File.GetLastWriteTimeUtc(localPath);
                WriteMetadata(localPath, metadata);
            }

            return VfsResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return VfsResult.Fail(VfsErrorCode.Cancelled, "Hydration cancelled.");
        }
        catch (Exception ex)
        {
            return VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex);
        }
    }

    public Task<VfsResult> DehydrateAsync(string localPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            EnsureInsideRoot(localPath);
            var metadata = ReadMetadata(localPath);
            if (metadata?.IsDirectory == true)
                return Task.FromResult(VfsResult.Ok());

            if (File.Exists(localPath))
                File.Delete(localPath);

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            using (File.Create(GetStubPath(localPath))) { }

            if (metadata != null)
            {
                metadata.HydrationState = VfsHydrationState.Dehydrated;
                WriteMetadata(localPath, metadata);
            }

            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
        }
    }

    public Task<VfsResult> UpdateMetadataAsync(string localPath, VfsMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            EnsureInsideRoot(localPath);
            var current = ReadMetadata(localPath);
            var hydrationState = current?.HydrationState
                ?? (File.Exists(GetStubPath(localPath)) ? VfsHydrationState.Dehydrated : VfsHydrationState.Hydrated);
            WriteMetadata(localPath, metadata, hydrationState);
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
        }
    }

    public Task<VfsResult<PinState>> GetPinStateAsync(string localPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var metadata = ReadMetadata(localPath);
        return Task.FromResult(metadata == null
            ? VfsResult<PinState>.Fail(VfsErrorCode.NotFound, $"Metadata does not exist: {localPath}")
            : VfsResult<PinState>.Ok(metadata.PinState));
    }

    public Task<VfsResult> SetPinStateAsync(string localPath, PinState state, PinDescent descent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var metadata = ReadMetadata(localPath);
            if (metadata == null)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"Metadata does not exist: {localPath}"));

            metadata.PinState = state;
            WriteMetadata(localPath, metadata);
            PinStateChanged?.Invoke(this, new VfsPinChangedEvent(localPath, state, descent));
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
        }
    }

    public Task<VfsResult> SetInSyncAsync(string localPath, bool inSync, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var metadata = ReadMetadata(localPath);
            if (metadata == null)
                return Task.FromResult(VfsResult.Fail(VfsErrorCode.NotFound, $"Metadata does not exist: {localPath}"));

            metadata.InSync = inSync;
            WriteMetadata(localPath, metadata);
            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
        }
    }

    public Task<PlaceholderInfo?> GetPlaceholderInfoAsync(string localPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var metadata = ReadMetadata(localPath);
        return Task.FromResult(metadata?.ToPlaceholderInfo(localPath));
    }

    public Task<IReadOnlyList<VfsEntry>> EnumerateChildrenAsync(string localDirectoryPath, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth < 1)
            return Task.FromResult<IReadOnlyList<VfsEntry>>([]);

        EnsureInsideRoot(localDirectoryPath);
        if (!Directory.Exists(localDirectoryPath))
            return Task.FromResult<IReadOnlyList<VfsEntry>>([]);

        var entries = new List<VfsEntry>();
        EnumerateCore(localDirectoryPath, depth, entries, ct);
        return Task.FromResult<IReadOnlyList<VfsEntry>>(entries);
    }

    public Task<VfsResult> DeleteAsync(string localPath, bool recursive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            EnsureInsideRoot(localPath);
            var metaPath = GetMetaPath(localPath);
            var stubPath = GetStubPath(localPath);

            if (Directory.Exists(localPath))
                Directory.Delete(localPath, recursive);
            if (File.Exists(localPath))
                File.Delete(localPath);
            if (File.Exists(stubPath))
                File.Delete(stubPath);
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            return Task.FromResult(VfsResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(VfsResult.Fail(VfsErrorCode.IoError, ex.Message, ex));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void EnumerateCore(string directoryPath, int remainingDepth, List<VfsEntry> entries, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(directoryPath))
        {
            ct.ThrowIfCancellationRequested();
            if (directory.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            seen.Add(directory);
            var info = ReadMetadata(directory)?.ToPlaceholderInfo(directory);
            entries.Add(new VfsEntry(directory, Path.GetFileName(directory), IsDirectory: true, info));

            if (remainingDepth > 1)
                EnumerateCore(directory, remainingDepth - 1, entries, ct);
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath))
        {
            ct.ThrowIfCancellationRequested();
            if (file.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var logicalPath = file.EndsWith(PlaceholderSuffix, StringComparison.OrdinalIgnoreCase)
                ? file[..^PlaceholderSuffix.Length]
                : file;

            if (!seen.Add(logicalPath))
                continue;

            var info = ReadMetadata(logicalPath)?.ToPlaceholderInfo(logicalPath);
            entries.Add(new VfsEntry(logicalPath, Path.GetFileName(logicalPath), IsDirectory: false, info));
        }
    }

    private void EnsureInsideRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(_rootPath))
            return;

        var normalized = Normalize(path);
        if (!normalized.Equals(_rootPath, StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path is outside the VFS root: {path}");
        }
    }

    private static string GetStubPath(string localPath) => localPath + PlaceholderSuffix;

    private static string GetMetaPath(string localPath) => localPath + MetadataSuffix;

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void WriteMetadata(string localPath, VfsMetadata metadata, VfsHydrationState hydrationState)
    {
        WriteMetadata(localPath, SuffixMetadata.From(metadata, hydrationState));
    }

    private void WriteMetadata(string localPath, SuffixMetadata metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var tempPath = GetMetaPath(localPath) + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(metadata, JsonOptions));
        File.Move(tempPath, GetMetaPath(localPath), overwrite: true);
        _logger?.LogDebug("SuffixVfs metadata written: {Path}", localPath);
    }

    private static SuffixMetadata? ReadMetadata(string localPath)
    {
        var metaPath = GetMetaPath(localPath);
        if (!File.Exists(metaPath))
            return null;

        var json = File.ReadAllText(metaPath);
        return JsonSerializer.Deserialize<SuffixMetadata>(json, JsonOptions);
    }

    private sealed class SuffixMetadata
    {
        public string FileId { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
        public string? ETag { get; set; }
        public long LogicalSize { get; set; }
        public DateTime MTimeUtc { get; set; }
        public bool IsDirectory { get; set; }
        public bool InSync { get; set; } = true;
        public PinState PinState { get; set; }
        public VfsHydrationState HydrationState { get; set; }

        public static SuffixMetadata From(VfsMetadata metadata, VfsHydrationState hydrationState) => new()
        {
            FileId = metadata.FileId,
            RemotePath = metadata.RemotePath,
            ETag = metadata.ETag,
            LogicalSize = metadata.LogicalSize,
            MTimeUtc = metadata.MTimeUtc,
            IsDirectory = metadata.IsDirectory,
            InSync = metadata.InSync,
            PinState = metadata.PinState,
            HydrationState = hydrationState
        };

        public PlaceholderInfo ToPlaceholderInfo(string localPath) => new(
            localPath,
            FileId,
            RemotePath,
            ETag,
            LogicalSize,
            MTimeUtc,
            IsDirectory,
            InSync,
            PinState,
            HydrationState);
    }
}
