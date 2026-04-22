using CloudDrive.Core.Configuration;

namespace CloudDrive.App.Services;

public sealed class AppLaunchOptions
{
    public static AppLaunchOptions Default { get; } = new();

    public bool IsAutoStartLaunch { get; init; }

    public static AppLaunchOptions Parse(IEnumerable<string> args)
    {
        return new AppLaunchOptions
        {
            IsAutoStartLaunch = args.Any(arg =>
                string.Equals(arg, WindowsAutoStartRegistration.AutoStartArgument, StringComparison.OrdinalIgnoreCase))
        };
    }
}
