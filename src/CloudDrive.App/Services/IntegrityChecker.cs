using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudDrive.App.Services;

/// <summary>
/// Pre-startup integrity checker that validates all DLL/EXE files against
/// a build-time SHA256 hash manifest. Designed to detect files deleted or
/// corrupted by antivirus software before the sync engine attempts to start.
///
/// This class uses ONLY .NET BCL types — no NuGet dependencies — so it works
/// even when third-party DLLs are missing.
/// </summary>
public static class IntegrityChecker
{
    private const string ProductName = "Selbstläufer CloudDrive";
    private const string ManifestFileName = "integrity-manifest.json";

    /// <summary>
    /// Reads the integrity manifest and verifies every listed file exists
    /// and has the expected SHA256 hash.
    /// </summary>
    public static IntegrityResult Verify()
    {
        var baseDir = AppContext.BaseDirectory;
        var manifestPath = Path.Combine(baseDir, ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            return new IntegrityResult
            {
                IsValid = false,
                ManifestMissing = true,
                AppDirectory = baseDir,
                Failures = new List<FileCheckResult>(),
                TotalFilesChecked = 0
            };
        }

        IntegrityManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            manifest = JsonSerializer.Deserialize<IntegrityManifest>(json);
        }
        catch
        {
            // Manifest exists but is corrupt/unreadable
            return new IntegrityResult
            {
                IsValid = false,
                ManifestMissing = true, // Treat unreadable manifest like missing
                AppDirectory = baseDir,
                Failures = new List<FileCheckResult>(),
                TotalFilesChecked = 0
            };
        }

        if (manifest?.Files == null || manifest.Files.Count == 0)
        {
            return new IntegrityResult
            {
                IsValid = false,
                ManifestMissing = true,
                AppDirectory = baseDir,
                Failures = new List<FileCheckResult>(),
                TotalFilesChecked = 0
            };
        }

        var failures = new List<FileCheckResult>();

        using var sha = SHA256.Create();

        foreach (var entry in manifest.Files)
        {
            // Convert forward slashes in manifest to OS path separator
            var relativePath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(baseDir, relativePath);

            if (!File.Exists(fullPath))
            {
                failures.Add(new FileCheckResult
                {
                    RelativePath = entry.Path,
                    Status = FileCheckStatus.Missing,
                    ExpectedHash = entry.Hash
                });
                continue;
            }

            try
            {
                byte[] hashBytes;
                using (var stream = File.OpenRead(fullPath))
                {
                    hashBytes = sha.ComputeHash(stream);
                }

                var actualHash = BitConverter.ToString(hashBytes)
                    .Replace("-", "")
                    .ToLowerInvariant();

                if (!string.Equals(actualHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(new FileCheckResult
                    {
                        RelativePath = entry.Path,
                        Status = FileCheckStatus.HashMismatch,
                        ExpectedHash = entry.Hash,
                        ActualHash = actualHash
                    });
                }
            }
            catch
            {
                // File exists but can't be read — treat as corrupted
                failures.Add(new FileCheckResult
                {
                    RelativePath = entry.Path,
                    Status = FileCheckStatus.HashMismatch,
                    ExpectedHash = entry.Hash,
                    ActualHash = "(unreadable)"
                });
            }
        }

        return new IntegrityResult
        {
            IsValid = failures.Count == 0,
            ManifestMissing = false,
            AppDirectory = baseDir,
            Failures = failures,
            TotalFilesChecked = manifest.Files.Count
        };
    }

    /// <summary>
    /// Writes a detailed diagnostic log when integrity verification fails.
    /// Uses only System.IO — no Serilog dependency.
    /// </summary>
    public static void WriteIntegrityLog(IntegrityResult result)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudDrive", "logs");
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir,
                $"integrity-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"{ProductName} Integrity Check Failed");
            sb.AppendLine("==================================");
            sb.AppendLine($"Time: {DateTime.Now:O}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($".NET: {Environment.Version}");
            sb.AppendLine($"App Directory: {result.AppDirectory}");
            sb.AppendLine();

            if (result.ManifestMissing)
            {
                sb.AppendLine("MANIFEST MISSING OR CORRUPT");
                sb.AppendLine("The integrity-manifest.json file was not found or could not be read.");
                sb.AppendLine("This indicates a severely corrupted installation.");
            }
            else
            {
                sb.AppendLine($"Total files in manifest: {result.TotalFilesChecked}");
                sb.AppendLine($"Failures: {result.Failures.Count}");
                sb.AppendLine();

                var missing = result.Failures
                    .Where(f => f.Status == FileCheckStatus.Missing)
                    .ToList();
                var corrupted = result.Failures
                    .Where(f => f.Status == FileCheckStatus.HashMismatch)
                    .ToList();

                if (missing.Count > 0)
                {
                    sb.AppendLine("MISSING FILES:");
                    foreach (var f in missing)
                        sb.AppendLine($"  {f.RelativePath}");
                    sb.AppendLine();
                }

                if (corrupted.Count > 0)
                {
                    sb.AppendLine("CORRUPTED FILES (hash mismatch):");
                    foreach (var f in corrupted)
                    {
                        sb.AppendLine($"  {f.RelativePath}");
                        sb.AppendLine($"    Expected: {f.ExpectedHash}");
                        sb.AppendLine($"    Actual:   {f.ActualHash}");
                    }
                }
            }

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Last resort — can't do much if logging itself fails
        }
    }

    /// <summary>
    /// Formats a user-friendly error message for the WPF MessageBox dialog.
    /// </summary>
    public static string GetUserFriendlyMessage(IntegrityResult result)
    {
        var sb = new StringBuilder();

        if (result.ManifestMissing)
        {
            sb.AppendLine($"{ProductName} cannot start because its installation is incomplete.");
            sb.AppendLine("The integrity manifest file is missing or corrupted.");
            sb.AppendLine();
            sb.AppendLine($"Please reinstall {ProductName} to fix this issue.");
        }
        else
        {
            sb.AppendLine($"{ProductName} cannot start because required files are missing or corrupted:");
            sb.AppendLine();

            var missing = result.Failures
                .Where(f => f.Status == FileCheckStatus.Missing)
                .ToList();
            var corrupted = result.Failures
                .Where(f => f.Status == FileCheckStatus.HashMismatch)
                .ToList();

            if (missing.Count > 0)
            {
                sb.AppendLine("  Missing:");
                foreach (var f in missing)
                    sb.AppendLine($"    • {f.RelativePath}");
                sb.AppendLine();
            }

            if (corrupted.Count > 0)
            {
                sb.AppendLine("  Corrupted:");
                foreach (var f in corrupted)
                    sb.AppendLine($"    • {f.RelativePath}");
                sb.AppendLine();
            }

            sb.AppendLine("This is usually caused by antivirus software quarantining");
            sb.AppendLine("or modifying application files.");
            sb.AppendLine();
            sb.AppendLine("To fix this:");
            sb.AppendLine("  1. Check your antivirus quarantine and restore the files above");
            sb.AppendLine($"  2. Add the {ProductName} folder to your antivirus exclusion list");
            sb.AppendLine($"  3. Reinstall {ProductName} if the files cannot be restored");
        }

        sb.AppendLine();
        sb.AppendLine($"Installation folder:");
        sb.AppendLine($"  {result.AppDirectory}");
        sb.AppendLine();
        sb.AppendLine("A detailed log has been written to:");
        sb.AppendLine($"  {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudDrive", "logs")}");

        return sb.ToString();
    }
}

#region Result Models

public class IntegrityResult
{
    public bool IsValid { get; set; }
    public bool ManifestMissing { get; set; }
    public string AppDirectory { get; set; } = string.Empty;
    public List<FileCheckResult> Failures { get; set; } = new();
    public int TotalFilesChecked { get; set; }
}

public class FileCheckResult
{
    public string RelativePath { get; set; } = string.Empty;
    public FileCheckStatus Status { get; set; }
    public string? ExpectedHash { get; set; }
    public string? ActualHash { get; set; }
}

public enum FileCheckStatus
{
    Missing,
    HashMismatch
}

#endregion

#region Manifest Model (for JSON deserialization)

internal class IntegrityManifest
{
    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }

    [JsonPropertyName("files")]
    public List<ManifestFileEntry>? Files { get; set; }
}

internal class ManifestFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;
}

#endregion
