using CloudDrive.App.Services;
using Shouldly;

namespace CloudDrive.Core.Tests.App;

public class AppLaunchOptionsTests
{
    public static TheoryData<string[], bool> ParseCases =>
        new()
        {
            { ["--autostart"], true },
            { ["--AUTOSTART"], true },
            { ["--background"], false },
            { Array.Empty<string>(), false }
        };

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_DetectsAutoStartLaunch(string[] args, bool expected)
    {
        var options = AppLaunchOptions.Parse(args);

        options.IsAutoStartLaunch.ShouldBe(expected);
    }
}
