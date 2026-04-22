using System.Security.Cryptography;

namespace CloudDrive.Core.Helpers;

public static class FileHasher
{
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
