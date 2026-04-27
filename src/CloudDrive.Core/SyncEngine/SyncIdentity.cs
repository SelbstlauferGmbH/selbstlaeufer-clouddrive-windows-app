using System.Security.Cryptography;
using System.Text;

namespace CloudDrive.Core.SyncEngine;

public static class SyncIdentity
{
    public static string NewLocalId() => $"local:{Guid.NewGuid():N}";

    public static string RemotePathFallbackId(string remotePath)
    {
        var normalized = remotePath.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"remote-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
