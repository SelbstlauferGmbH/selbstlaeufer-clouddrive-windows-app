using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Watchdog;

public sealed class FileWatchdogStatusStore : IWatchdogStatusStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<FileWatchdogStatusStore> _logger;
    private readonly string _filePath;

    public FileWatchdogStatusStore(ILogger<FileWatchdogStatusStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? WatchdogTaskConstants.GetStatusFilePath();
    }

    public WatchdogStatusSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            var json = File.ReadAllText(_filePath);
            var snapshot = JsonSerializer.Deserialize<WatchdogStatusSnapshot>(json, SerializerOptions);
            if (snapshot == null)
                return null;

            Normalize(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load watchdog status from {Path}", _filePath);
            return null;
        }
    }

    public void Save(WatchdogStatusSnapshot snapshot)
    {
        try
        {
            Normalize(snapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save watchdog status to {Path}", _filePath);
        }
    }

    private static void Normalize(WatchdogStatusSnapshot snapshot)
    {
        snapshot.ImportantMessages = (snapshot.ImportantMessages ?? [])
            .OrderByDescending(message => message.TimestampUtc)
            .Take(5)
            .ToList();
    }
}
