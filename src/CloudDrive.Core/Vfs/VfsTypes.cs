using System.Text;

namespace CloudDrive.Core.Vfs;

public enum VfsErrorCode
{
    None = 0,
    NotFound = 1,
    AlreadyExists = 2,
    IoError = 3,
    InvalidOperation = 4,
    RegistrationFailed = 5,
    PlatformError = 6,
    Cancelled = 7
}

public enum PinState
{
    Unspecified = 0,
    Pinned = 1,
    Unpinned = 2,
    Excluded = 3,
    Inherit = 4
}

public enum PinDescent
{
    ItemOnly = 0,
    Recursive = 1,
    RecursiveOnly = 2
}

public enum VfsHydrationState
{
    Unknown = 0,
    Hydrated = 1,
    Dehydrated = 2
}

public sealed record VfsRegistration(
    string SyncRootPath,
    string AccountId);

public sealed record VfsMetadata(
    string FileId,
    string RemotePath,
    string? ETag,
    long LogicalSize,
    DateTime MTimeUtc,
    bool IsDirectory,
    PinState PinState = PinState.Unspecified,
    bool InSync = true);

public sealed record VfsTransferProgress(
    long BytesTransferred,
    long TotalBytes);

public sealed record PlaceholderInfo(
    string LocalPath,
    string FileId,
    string RemotePath,
    string? ETag,
    long LogicalSize,
    DateTime MTimeUtc,
    bool IsDirectory,
    bool InSync,
    PinState PinState,
    VfsHydrationState HydrationState);

public sealed record VfsEntry(
    string LocalPath,
    string Name,
    bool IsDirectory,
    PlaceholderInfo? Placeholder);

public sealed record VfsPinChangedEvent(
    string LocalPath,
    PinState PinState,
    PinDescent Descent);

public class VfsResult
{
    protected VfsResult(bool succeeded, VfsErrorCode errorCode, string? errorMessage, Exception? exception)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    public bool Succeeded { get; }
    public bool Failed => !Succeeded;
    public VfsErrorCode ErrorCode { get; }
    public string? ErrorMessage { get; }
    public Exception? Exception { get; }

    public static VfsResult Ok() => new(true, VfsErrorCode.None, null, null);

    public static VfsResult Fail(VfsErrorCode errorCode, string errorMessage, Exception? exception = null) =>
        new(false, errorCode, errorMessage, exception);
}

public sealed class VfsResult<T> : VfsResult
{
    private VfsResult(T? value, bool succeeded, VfsErrorCode errorCode, string? errorMessage, Exception? exception)
        : base(succeeded, errorCode, errorMessage, exception)
    {
        Value = value;
    }

    public T? Value { get; }

    public static VfsResult<T> Ok(T value) => new(value, true, VfsErrorCode.None, null, null);

    public new static VfsResult<T> Fail(VfsErrorCode errorCode, string errorMessage, Exception? exception = null) =>
        new(default, false, errorCode, errorMessage, exception);
}

public sealed record VfsIdentity(
    string FileId,
    string? ETag,
    string RemotePath);

public static class VfsIdentityCodec
{
    private const string Magic = "CDRV1";
    private const char Separator = '\u001f';

    public static byte[] Pack(VfsIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.FileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.RemotePath);

        var payload = string.Join(
            Separator,
            Magic,
            Escape(identity.FileId),
            Escape(identity.ETag ?? string.Empty),
            Escape(identity.RemotePath));

        return Encoding.UTF8.GetBytes(payload);
    }

    public static bool TryUnpack(ReadOnlySpan<byte> bytes, out VfsIdentity identity)
    {
        identity = new VfsIdentity(string.Empty, null, string.Empty);
        if (bytes.IsEmpty)
            return false;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return false;
        }

        var parts = text.TrimEnd('\0').Split(Separator);
        if (parts.Length != 4 || !string.Equals(parts[0], Magic, StringComparison.Ordinal))
            return false;

        var fileId = Unescape(parts[1]);
        var etag = Unescape(parts[2]);
        var remotePath = Unescape(parts[3]);
        if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(remotePath))
            return false;

        identity = new VfsIdentity(fileId, string.IsNullOrEmpty(etag) ? null : etag, remotePath);
        return true;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(Separator.ToString(), "\\u001f", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                if (value.AsSpan(i + 1).StartsWith("u001f", StringComparison.Ordinal))
                {
                    builder.Append(Separator);
                    i += 5;
                    continue;
                }

                if (value[i + 1] == '\\')
                {
                    builder.Append('\\');
                    i++;
                    continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
