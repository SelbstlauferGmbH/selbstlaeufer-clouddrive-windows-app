namespace CloudDrive.Core.WebDav;

public sealed record WebDavLockSupport(
    WebDavLockSupportState State,
    string Detail,
    DateTimeOffset? CheckedAtUtc = null)
{
    public bool IsSupported => State == WebDavLockSupportState.Supported;

    public static WebDavLockSupport NotChecked() =>
        new(WebDavLockSupportState.NotChecked, string.Empty);

    public static WebDavLockSupport Supported(string detail) =>
        new(WebDavLockSupportState.Supported, detail, DateTimeOffset.UtcNow);

    public static WebDavLockSupport Unsupported(string detail) =>
        new(WebDavLockSupportState.Unsupported, detail, DateTimeOffset.UtcNow);

    public static WebDavLockSupport ProbeFailed(string detail) =>
        new(WebDavLockSupportState.ProbeFailed, detail, DateTimeOffset.UtcNow);
}

public enum WebDavLockSupportState
{
    NotChecked,
    Supported,
    Unsupported,
    ProbeFailed
}
