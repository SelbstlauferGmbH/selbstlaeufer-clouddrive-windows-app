using CloudDrive.App.Services;
using CloudDrive.Core.Configuration;
using Velopack;

namespace CloudDrive.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var launchOptions = AppLaunchOptions.Parse(args);
        var firstRunCleanupService = new FirstRunInstallCleanupService();
        var continueStartup = true;

        VelopackApp.Build()
            .OnFirstRun(_ => continueStartup = firstRunCleanupService.PrepareForFirstRunAfterInstall())
            .OnAfterInstallFastCallback(_ => SyncAutoStartRegistration())
            .OnAfterUpdateFastCallback(_ => SyncAutoStartRegistration())
            .OnBeforeUninstallFastCallback(_ => new WindowsAutoStartRegistration().Apply(false))
            .Run();

        if (!continueStartup)
            return;

        var app = new App(launchOptions);
        app.InitializeComponent();
        app.Run();
    }

    private static void SyncAutoStartRegistration()
    {
        try
        {
            var settings = AppSettings.Load();
            var shouldEnable = settings.LaunchOnStartup &&
                               settings.HasCompleteAccountConfiguration(CredentialManager.HasPassword());
            new WindowsAutoStartRegistration().Apply(shouldEnable);
        }
        catch
        {
            // Fast callback hooks must remain best-effort and return quickly.
        }
    }
}
