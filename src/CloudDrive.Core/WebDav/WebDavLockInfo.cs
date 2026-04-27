namespace CloudDrive.Core.WebDav;

public sealed record WebDavLockRequest(string Owner, TimeSpan Timeout);

public sealed record WebDavLockInfo(string RemotePath, string Token, DateTimeOffset ExpiresAtUtc);
