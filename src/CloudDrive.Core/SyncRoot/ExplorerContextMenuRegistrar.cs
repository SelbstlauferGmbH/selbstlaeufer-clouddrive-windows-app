using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudDrive.Core.SyncRoot;

public sealed class ExplorerContextMenuRegistrar
{
    private const string CommandPrefix = "CloudDrive";
    private static readonly string[] FileAndDirectoryRoots =
    [
        @"Software\Classes\*\shell",
        @"Software\Classes\Directory\shell"
    ];

    private static readonly string[] BackgroundRoots =
    [
        @"Software\Classes\Directory\Background\shell"
    ];

    private readonly ILogger<ExplorerContextMenuRegistrar> _logger;

    public ExplorerContextMenuRegistrar(ILogger<ExplorerContextMenuRegistrar> logger)
    {
        _logger = logger;
    }

    public void EnsureRegistered(string appExecutablePath, string syncRootPath)
    {
        if (string.IsNullOrWhiteSpace(appExecutablePath) || !File.Exists(appExecutablePath))
        {
            _logger.LogDebug("Skipping Explorer context menu registration because the app executable is missing: {Path}", appExecutablePath);
            return;
        }

        if (string.IsNullOrWhiteSpace(syncRootPath))
            return;

        try
        {
            foreach (var root in FileAndDirectoryRoots)
            {
                RegisterCommand(root, "RetrySync", "CloudDrive: Retry sync", appExecutablePath, "retry", "%1", syncRootPath);
                RegisterCommand(root, "ResolveConflict", "CloudDrive: Resolve conflict...", appExecutablePath, "resolve-conflict", "%1", syncRootPath);
                RegisterCommand(root, "OpenProblems", "CloudDrive: Open problems", appExecutablePath, "open-problems", "%1", syncRootPath);
                RegisterCommand(root, "DismissError", "CloudDrive: Dismiss error", appExecutablePath, "dismiss-error", "%1", syncRootPath);
            }

            foreach (var root in BackgroundRoots)
            {
                RegisterCommand(root, "RetrySync", "CloudDrive: Retry sync", appExecutablePath, "retry", "%V", syncRootPath);
                RegisterCommand(root, "OpenProblems", "CloudDrive: Open problems", appExecutablePath, "open-problems", "%V", syncRootPath);
            }

            RegisterProtocol(appExecutablePath);

            _logger.LogInformation("Explorer context menu registered for sync root {SyncRootPath}", syncRootPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Explorer context menu registration failed");
        }
    }

    public void Unregister()
    {
        try
        {
            foreach (var root in FileAndDirectoryRoots.Concat(BackgroundRoots))
            {
                foreach (var verb in new[] { "RetrySync", "ResolveConflict", "OpenProblems", "DismissError" })
                    Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{CommandPrefix}.{verb}", throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Explorer context menu unregistration failed");
        }
    }

    private static void RegisterCommand(
        string root,
        string verb,
        string label,
        string appExecutablePath,
        string command,
        string pathToken,
        string syncRootPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{CommandPrefix}.{verb}");
        if (key == null)
            return;

        key.SetValue("MUIVerb", label, RegistryValueKind.String);
        key.SetValue("Icon", $"\"{appExecutablePath}\",0", RegistryValueKind.String);
        key.SetValue("AppliesTo", BuildAppliesTo(syncRootPath), RegistryValueKind.String);

        using var commandKey = key.CreateSubKey("command");
        commandKey?.SetValue(
            null,
            $"\"{appExecutablePath}\" --clouddrive-shell-command {command} --path \"{pathToken}\"",
            RegistryValueKind.String);
    }

    private static string BuildAppliesTo(string syncRootPath)
    {
        var normalized = Path.GetFullPath(syncRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"System.ItemPathDisplay:~=\"{normalized}\"";
    }

    private static void RegisterProtocol(string appExecutablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\clouddrive");
        if (key == null)
            return;

        key.SetValue(null, "URL:CloudDrive Protocol", RegistryValueKind.String);
        key.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

        using var commandKey = key.CreateSubKey(@"shell\open\command");
        commandKey?.SetValue(null, $"\"{appExecutablePath}\" \"%1\"", RegistryValueKind.String);
    }
}
