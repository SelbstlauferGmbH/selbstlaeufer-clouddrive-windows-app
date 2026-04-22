using System.Diagnostics;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;

namespace CloudDrive.App.Services;

public sealed class FirstRunInstallCleanupService
{
    private readonly LocalStateCleanupService _cleanupService = new();

    public bool PrepareForFirstRunAfterInstall()
    {
        var settings = AppSettings.Load();
        var localizer = AppLocalizer.Instance;
        localizer.Initialize(settings.Language);

        if (!LocalStateCleanupService.HasTransientState(settings))
            return true;

        if (LocalStateCleanupService.SyncRootContainsEntries(settings.SyncRootPath))
        {
            var confirmation = System.Windows.MessageBox.Show(
                localizer.Format("Install_CleanupWarning_Message", settings.SyncRootPath),
                localizer.GetString("Install_CleanupWarning_Title"),
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Cancel);

            if (confirmation != System.Windows.MessageBoxResult.OK)
                return false;
        }

        try
        {
            var cleanupResult = _cleanupService
                .CleanupAsync(
                    settings,
                    new LocalStateCleanupOptions(
                        ClearSavedConfiguration: false,
                        ClearCredentials: false))
                .GetAwaiter()
                .GetResult();

            LocalStateCleanupService.DeleteLogs();

            if (!cleanupResult.RequiresReboot)
                return true;

            var rebootResult = System.Windows.MessageBox.Show(
                localizer.GetString("Reset_RestartRequired_Message"),
                localizer.GetString("Reset_RestartRequired_Title"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information,
                System.Windows.MessageBoxResult.No);

            if (rebootResult == System.Windows.MessageBoxResult.Yes)
            {
                Process.Start("shutdown", "/r /t 5 /c \"CloudDrive reinstall cleanup: restarting to complete cleanup\"");
            }
        }
        catch
        {
            System.Windows.MessageBox.Show(
                localizer.GetString("Install_CleanupFailed_Message"),
                localizer.GetString("Install_CleanupFailed_Title"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }

        return false;
    }
}
