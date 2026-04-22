using System.IO;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.SyncRoot;
using CloudDrive.Core.Watchdog;
using Serilog;

namespace CloudDrive.App.Services;

public sealed record LocalStateCleanupOptions(
    bool ClearSavedConfiguration,
    bool ClearCredentials);

public sealed record LocalStateCleanupResult(
    bool RequiresReboot);

public sealed class LocalStateCleanupService
{
    public async Task<LocalStateCleanupResult> CleanupAsync(
        AppSettings settings,
        LocalStateCleanupOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (Directory.Exists(settings.SyncRootPath))
        {
            try
            {
                var (reverted, failed) = PlaceholderReverter.RevertAll(settings.SyncRootPath);
                Log.Information("Cleanup: Reverted {Reverted} placeholders ({Failed} failed)", reverted, failed);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cleanup: Placeholder revert encountered errors");
            }
        }

        if (SyncRootAccountIdResolver.TryResolve(settings, out var accountId))
        {
            try
            {
                var syncRootId = SyncRootRegistrar.GetSyncRootId(accountId!);
                global::Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(syncRootId);
                Log.Information("Cleanup: Unregistered sync root {SyncRootId}", syncRootId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cleanup: Failed to unregister sync root");
            }
        }

        await Task.Delay(500, ct);

        try
        {
            SyncRootConnector.ClearSyncStatus(settings.SyncRootPath);
        }
        catch
        {
            // Best effort.
        }

        if (Directory.Exists(settings.SyncRootPath))
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    Directory.Delete(settings.SyncRootPath, recursive: true);
                    Log.Information("Cleanup: Deleted sync root folder {Path}", settings.SyncRootPath);
                    break;
                }
                catch (Exception ex) when (attempt < 2)
                {
                    Log.Warning(ex, "Cleanup: Delete attempt {Attempt}/3 failed, retrying...", attempt + 1);
                    await Task.Delay(1000 * (attempt + 1), ct);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Cleanup: Failed to delete sync root folder after 3 attempts");
                }
            }
        }

        var requiresReboot = Directory.Exists(settings.SyncRootPath);
        if (requiresReboot)
        {
            PendingCleanup.Create(settings.SyncRootPath);
            Log.Information("Cleanup: Pending cleanup marker written for {Path}", settings.SyncRootPath);
        }
        else
        {
            PendingCleanup.Remove();
        }

        var dataDir = AppSettings.GetDataDirectory();
        var dbPath = Path.Combine(dataDir, "syncstate.db");
        DeleteFileIfExists(dbPath);
        DeleteFileIfExists(dbPath + "-wal");
        DeleteFileIfExists(dbPath + "-shm");

        DeleteFileIfExists(WatchdogTaskConstants.GetStatusFilePath());

        if (options.ClearSavedConfiguration)
        {
            new WindowsAutoStartRegistration().Apply(false);

            if (options.ClearCredentials)
            {
                try
                {
                    CredentialManager.DeletePassword();
                    Log.Information("Cleanup: Deleted stored password");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Cleanup: Failed to delete stored password");
                }
            }

            new AppSettings().Save();
            Log.Information("Cleanup: Saved configuration reset to defaults");
        }
        else
        {
            Log.Information("Cleanup: Saved configuration preserved");
        }

        return new LocalStateCleanupResult(requiresReboot);
    }

    public static bool HasTransientState(AppSettings settings)
    {
        var dataDir = AppSettings.GetDataDirectory();
        var dbPath = Path.Combine(dataDir, "syncstate.db");

        return Directory.Exists(settings.SyncRootPath) ||
               File.Exists(dbPath) ||
               File.Exists(dbPath + "-wal") ||
               File.Exists(dbPath + "-shm") ||
               File.Exists(WatchdogTaskConstants.GetStatusFilePath()) ||
               PendingCleanup.Exists() ||
               HasLogs();
    }

    public static bool SyncRootContainsEntries(string? syncRootPath)
    {
        if (string.IsNullOrWhiteSpace(syncRootPath) || !Directory.Exists(syncRootPath))
            return false;

        try
        {
            return Directory.EnumerateFileSystemEntries(syncRootPath).Any();
        }
        catch
        {
            return true;
        }
    }

    public static bool HasLogs()
    {
        var logsDir = GetLogsDirectory();
        if (!Directory.Exists(logsDir))
            return false;

        try
        {
            return Directory.EnumerateFileSystemEntries(logsDir).Any();
        }
        catch
        {
            return true;
        }
    }

    public static void DeleteLogs()
    {
        DeleteDirectoryIfExists(GetLogsDirectory());
    }

    public static string GetLogsDirectory() =>
        Path.Combine(AppSettings.GetDataDirectory(), "logs");

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Log.Information("Cleanup: Deleted file {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cleanup: Failed to delete file {Path}", path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                Log.Information("Cleanup: Deleted directory {Path}", path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cleanup: Failed to delete directory {Path}", path);
        }
    }
}
