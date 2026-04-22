using System.Text.Json;

namespace CloudDrive.Core.Configuration;

/// <summary>
/// Marker file that signals a pending folder cleanup after reboot.
/// Written by the reset flow when folder deletion fails due to cldflt.sys locks.
/// Read by the Watchdog (at logon / every 5 min) and the main app on startup.
/// </summary>
public class PendingCleanup
{
    private static readonly string FilePath = Path.Combine(
        AppSettings.GetDataDirectory(), "pending-cleanup.json");

    public string FolderPath { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Writes the marker file. Called by the reset flow when folder deletion fails.
    /// </summary>
    public static void Create(string folderPath)
    {
        var marker = new PendingCleanup
        {
            FolderPath = folderPath,
            CreatedUtc = DateTime.UtcNow
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    /// <summary>
    /// Loads the marker if it exists, returns null otherwise.
    /// </summary>
    public static PendingCleanup? Load()
    {
        if (!File.Exists(FilePath))
            return null;
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<PendingCleanup>(json);
        }
        catch
        {
            return null;
        }
    }

    public static bool Exists() => File.Exists(FilePath);

    /// <summary>
    /// Deletes the marker file. Called after successful cleanup.
    /// </summary>
    public static void Remove()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* best effort */ }
    }
}
