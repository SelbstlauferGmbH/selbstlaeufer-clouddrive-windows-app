using CloudDrive.Core.Watchdog;
using Shouldly;

namespace CloudDrive.Core.Tests.Watchdog;

public class WatchdogScheduledTaskXmlBuilderTests
{
    [Fact]
    public void Build_IncludesHiddenInteractiveOneMinuteTaskDefinition()
    {
        var builder = new WatchdogScheduledTaskXmlBuilder();

        var xml = builder.Build(@"C:\Apps\CloudDrive\CloudDrive.Watchdog.exe", new DateTimeOffset(2026, 4, 8, 12, 0, 0, TimeSpan.Zero));

        xml.ShouldContain("<LogonTrigger>");
        xml.ShouldContain("<CalendarTrigger>");
        xml.ShouldContain("<Interval>PT1M</Interval>");
        xml.ShouldContain("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        xml.ShouldContain("<Hidden>true</Hidden>");
        xml.ShouldContain("<LogonType>InteractiveToken</LogonType>");
        xml.ShouldContain(@"C:\Apps\CloudDrive\CloudDrive.Watchdog.exe");
        xml.ShouldNotContain("--service");
        xml.ShouldNotContain("CloudDriveWatchdog");
    }
}
