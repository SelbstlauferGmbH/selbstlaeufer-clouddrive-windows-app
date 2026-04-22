using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.Watchdog;

public class FileWatchdogStatusStoreTests
{
    [Fact]
    public void SaveAndLoad_NormalizesNewestFiveImportantMessages()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"clouddrive-watchdog-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var statusFilePath = Path.Combine(tempDirectory, "watchdog-status.json");
        var store = new FileWatchdogStatusStore(NullLogger<FileWatchdogStatusStore>.Instance, statusFilePath);
        var baseline = new DateTimeOffset(2026, 4, 8, 10, 0, 0, TimeSpan.Zero);

        try
        {
            store.Save(new WatchdogStatusSnapshot
            {
                ConnectionStatus = WatchdogConnectionStatus.Disconnected,
                DisconnectedReason = WatchdogDisconnectedReason.ServerUnreachable,
                LastCheckUtc = baseline,
                ImportantMessages =
                [
                    new(baseline.AddMinutes(-6), "msg-1"),
                    new(baseline.AddMinutes(-1), "msg-6"),
                    new(baseline.AddMinutes(-4), "msg-3"),
                    new(baseline.AddMinutes(-2), "msg-5"),
                    new(baseline.AddMinutes(-5), "msg-2"),
                    new(baseline.AddMinutes(-3), "msg-4")
                ]
            });

            var snapshot = store.Load();

            snapshot.ShouldNotBeNull();
            snapshot.ImportantMessages.Count.ShouldBe(5);
            snapshot.ImportantMessages.Select(message => message.Message).ShouldBe(["msg-6", "msg-5", "msg-4", "msg-3", "msg-2"]);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
