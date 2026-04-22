using System.Diagnostics;
using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.Watchdog;

public class WatchdogTaskRegistrarTests
{
    [Fact]
    public async Task EnsureRegisteredAsync_UsesSchtasksWithXmlRegistration()
    {
        var processRunner = new CapturingProcessRunner();
        var registrar = new WatchdogTaskRegistrar(
            new WatchdogScheduledTaskXmlBuilder(),
            processRunner,
            NullLogger<WatchdogTaskRegistrar>.Instance,
            new FixedTimeProvider());

        var tempDir = Path.Combine(Path.GetTempPath(), $"clouddrive-watchdog-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var executablePath = Path.Combine(tempDir, "CloudDrive.Watchdog.exe");
        await File.WriteAllTextAsync(executablePath, "stub");

        try
        {
            await registrar.EnsureRegisteredAsync(executablePath);

            processRunner.StartInfo.ShouldNotBeNull();
            processRunner.StartInfo!.FileName.ShouldBe("schtasks.exe");
            processRunner.StartInfo.Arguments.ShouldContain("/Create");
            processRunner.StartInfo.Arguments.ShouldContain(WatchdogTaskConstants.TaskName);
            processRunner.TaskXml.ShouldNotBeNull();
            processRunner.TaskXml.ShouldContain("<Hidden>true</Hidden>");
            processRunner.TaskXml.ShouldContain("<Interval>PT1M</Interval>");
            processRunner.TaskXml.ShouldNotContain("--service");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class CapturingProcessRunner : IWatchdogProcessRunner
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public string? TaskXml { get; private set; }

        public Task<WatchdogProcessRunnerResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct = default)
        {
            StartInfo = startInfo;
            var xmlPath = startInfo.Arguments
                .Split('"')
                .First(part => part.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            TaskXml = File.ReadAllText(xmlPath);

            return Task.FromResult(new WatchdogProcessRunnerResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 4, 8, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
