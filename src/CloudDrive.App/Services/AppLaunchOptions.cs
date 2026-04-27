using CloudDrive.Core.Configuration;

namespace CloudDrive.App.Services;

public sealed class AppLaunchOptions
{
    public static AppLaunchOptions Default { get; } = new();

    public bool IsAutoStartLaunch { get; init; }
    public ShellCommand? ShellCommand { get; init; }

    public static AppLaunchOptions Parse(IEnumerable<string> args)
    {
        var argList = args.ToList();
        return new AppLaunchOptions
        {
            IsAutoStartLaunch = argList.Any(arg =>
                string.Equals(arg, WindowsAutoStartRegistration.AutoStartArgument, StringComparison.OrdinalIgnoreCase)),
            ShellCommand = ParseShellCommand(argList)
        };
    }

    private static ShellCommand? ParseShellCommand(IReadOnlyList<string> args)
    {
        var command = ReadOption(args, "--clouddrive-shell-command");
        if (string.IsNullOrWhiteSpace(command))
        {
            var protocolUri = args.FirstOrDefault(arg =>
                arg.StartsWith("clouddrive://", StringComparison.OrdinalIgnoreCase));
            return ParseProtocolCommand(protocolUri);
        }

        return new ShellCommand(command, ReadOption(args, "--path"));
    }

    private static ShellCommand? ParseProtocolCommand(string? protocolUri)
    {
        if (string.IsNullOrWhiteSpace(protocolUri) ||
            !Uri.TryCreate(protocolUri, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return string.Equals(uri.Host, "problems", StringComparison.OrdinalIgnoreCase)
            ? new ShellCommand("open-problems", null)
            : null;
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                continue;

            return i + 1 < args.Count ? args[i + 1] : null;
        }

        return null;
    }
}
