using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace CloudDrive.Core.SyncRoot;

public class SyncRootRegistrar
{
    private const string ProviderId = "CloudDrive";
    private const string ProviderDisplayName = "CloudDrive";
    private const string LegacyProviderDisplayName = "Selbstl\u00E4ufer CloudDrive";
    private const string LegacyMojibakeProviderDisplayName = "Selbstl\u00C3\u00A4ufer CloudDrive";
    private const string DesktopNamespaceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    private const string ClassesClsidKeyPath = @"Software\Classes\CLSID";
    private const string Wow6432ClassesClsidKeyPath = @"Software\Classes\WOW6432Node\CLSID";

    private readonly ILogger<SyncRootRegistrar> _logger;
    private DateTime _lastIconRegistration = DateTime.MinValue;
    private static readonly TimeSpan IconRegistrationDebounce = TimeSpan.FromSeconds(5);

    public SyncRootRegistrar(ILogger<SyncRootRegistrar> logger)
    {
        _logger = logger;
    }

    public static string GetSyncRootId(string accountId)
    {
        return $"{ProviderId}!{Environment.UserName}!{accountId}";
    }

    public async Task RegisterAsync(string syncRootPath, string accountId)
    {
        var syncRootId = GetSyncRootId(accountId);
        var existingRegistration = GetCurrentSyncRoot(syncRootId);

        // If already registered with the correct metadata, skip re-registration.
        if (existingRegistration != null && !NeedsRegistrationRefresh(existingRegistration, syncRootPath))
        {
            _logger.LogInformation("Sync root already registered: {SyncRootId} - skipping registration", syncRootId);
            return;
        }

        if (existingRegistration != null)
        {
            _logger.LogInformation(
                "Refreshing sync root registration: {SyncRootId} (displayName={DisplayName}, path={Path})",
                syncRootId,
                existingRegistration.DisplayNameResource,
                existingRegistration.Path?.Path);
        }

        // Ensure directory exists
        Directory.CreateDirectory(syncRootPath);

        // Validate NTFS
        var driveInfo = new DriveInfo(Path.GetPathRoot(syncRootPath)!);
        if (!string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Sync root must be on an NTFS volume. '{syncRootPath}' is on {driveInfo.DriveFormat}.");
        }

        var storageFolder = await StorageFolder.GetFolderFromPathAsync(syncRootPath);

        var syncRootInfo = new StorageProviderSyncRootInfo
        {
            Id = syncRootId,
            Path = storageFolder,
            DisplayNameResource = ProviderDisplayName,
            IconResource = "%SystemRoot%\\system32\\imageres.dll,-1043",
            Version = "1.0",
            RecycleBinUri = null,
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime
                         | StorageProviderInSyncPolicy.DirectoryCreationTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            Context = CryptographicBuffer.ConvertStringToBinary(syncRootId, BinaryStringEncoding.Utf8),
        };
        AddItemPropertyDefinitions(syncRootInfo);

        StorageProviderSyncRootManager.Register(syncRootInfo);
        _logger.LogInformation("Sync root registered: {SyncRootId} at {Path}", syncRootId, syncRootPath);
    }

    public void Unregister(string accountId)
    {
        var syncRootId = GetSyncRootId(accountId);
        try
        {
            StorageProviderSyncRootManager.Unregister(syncRootId);
            _logger.LogInformation("Sync root unregistered: {SyncRootId}", syncRootId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister sync root: {SyncRootId}", syncRootId);
        }
    }

    public bool IsRegistered(string syncRootId)
    {
        try
        {
            var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            return roots.Any(r => r.Id == syncRootId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether a sync root is already registered for the given account.
    /// </summary>
    public bool IsRegisteredForAccount(string accountId)
    {
        return IsRegistered(GetSyncRootId(accountId));
    }

    /// <summary>
    /// Finds stale sync root registrations from previous sessions by scanning all registered
    /// sync roots that match our provider ID prefix and reading their ProviderStatus.
    /// </summary>
    public List<StaleRegistrationInfo> GetStaleRegistrations()
    {
        var staleList = new List<StaleRegistrationInfo>();
        var prefix = GetSyncRootIdPrefix();

        try
        {
            var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            foreach (var root in roots)
            {
                if (!root.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = root.Path?.Path;
                if (string.IsNullOrEmpty(path))
                    continue;

                uint providerStatus = 0;
                try
                {
                    var info = CfGetSyncRootInfoByPath<CF_SYNC_ROOT_STANDARD_INFO>(path);
                    providerStatus = (uint)info.ProviderStatus;
                }
                catch (Exception ex)
                {
                    // Path may no longer exist on disk — still treat as stale
                    _logger.LogDebug(ex, "CfGetSyncRootInfoByPath failed for {Path} — treating as stale", path);
                    providerStatus = 0x00000000; // DISCONNECTED
                }

                staleList.Add(new StaleRegistrationInfo(
                    root.Id,
                    path,
                    providerStatus,
                    DateTimeOffset.UtcNow));

                _logger.LogDebug("Found sync root registration: {Id} at {Path} (status=0x{Status:X8})",
                    root.Id, path, providerStatus);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate sync root registrations");
        }

        return staleList;
    }

    public List<ShellNamespaceRegistrationInfo> GetOrphanedShellNamespaceRegistrations(
        string currentSyncRootId,
        string currentSyncRootPath)
    {
        var orphaned = new List<ShellNamespaceRegistrationInfo>();
        var shellRegistrations = GetShellNamespaceRegistrations();
        var activeSyncRootIds = GetActiveSyncRootIds(out var activeSyncRootIdsKnown);

        foreach (var registration in shellRegistrations)
        {
            if (!ShouldCleanupShellNamespaceRegistration(
                    registration,
                    currentSyncRootId,
                    currentSyncRootPath,
                    activeSyncRootIds,
                    activeSyncRootIdsKnown))
            {
                continue;
            }

            orphaned.Add(registration);
        }

        return orphaned;
    }

    public List<ShellNamespaceRegistrationInfo> GetRedundantCurrentShellNamespaceRegistrations(
        string currentSyncRootId,
        string currentSyncRootPath)
    {
        return GetRedundantCurrentShellNamespaceRegistrations(
            GetShellNamespaceRegistrations(),
            currentSyncRootId,
            currentSyncRootPath);
    }

    public int CleanupShellNamespaceRegistrations(
        IEnumerable<ShellNamespaceRegistrationInfo> registrations,
        CancellationToken ct)
    {
        var cleanedCount = 0;

        foreach (var registration in registrations)
        {
            ct.ThrowIfCancellationRequested();
            DeleteShellNamespaceRegistration(registration);
            cleanedCount++;
        }

        return cleanedCount;
    }

    /// <summary>
    /// Attempts to unregister a sync root with retry logic for stale cleanup.
    /// Retries up to 3 times with delays 1s, 3s, 5s.
    /// </summary>
    public async Task<bool> UnregisterWithRetry(string syncRootId, CancellationToken ct)
    {
        int[] delays = [1000, 3000, 5000];

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                StorageProviderSyncRootManager.Unregister(syncRootId);
                _logger.LogInformation("Unregistered stale sync root: {SyncRootId} (attempt {Attempt}/3)",
                    syncRootId, attempt + 1);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unregister stale sync root: {SyncRootId} (attempt {Attempt}/3)",
                    syncRootId, attempt + 1);

                if (attempt < 2)
                {
                    try { await Task.Delay(delays[attempt], ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }

        _logger.LogError("Failed to unregister stale sync root after 3 attempts: {SyncRootId}", syncRootId);
        return false;
    }

    /// <summary>
    /// Re-registers the sync root with a different icon resource (Layer 3 — experimental).
    /// Debounced to at most once per 5 seconds to avoid shell cache thrashing.
    /// </summary>
    public async Task ReRegisterWithIconAsync(string syncRootPath, string accountId, string iconResource)
    {
        if (DateTime.UtcNow - _lastIconRegistration < IconRegistrationDebounce)
        {
            _logger.LogDebug("ReRegisterWithIconAsync debounced — skipping");
            return;
        }

        var syncRootId = GetSyncRootId(accountId);
        if (!IsRegistered(syncRootId))
        {
            _logger.LogDebug("ReRegisterWithIconAsync skipped — sync root not registered");
            return;
        }

        try
        {
            var storageFolder = await StorageFolder.GetFolderFromPathAsync(syncRootPath);

            var syncRootInfo = new StorageProviderSyncRootInfo
            {
                Id = syncRootId,
                Path = storageFolder,
                DisplayNameResource = ProviderDisplayName,
                IconResource = iconResource,
                Version = "1.0",
                RecycleBinUri = null,
                HydrationPolicy = StorageProviderHydrationPolicy.Full,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
                PopulationPolicy = StorageProviderPopulationPolicy.Full,
                InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime
                             | StorageProviderInSyncPolicy.DirectoryCreationTime,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                ShowSiblingsAsGroup = false,
                Context = CryptographicBuffer.ConvertStringToBinary(syncRootId, BinaryStringEncoding.Utf8),
            };
            AddItemPropertyDefinitions(syncRootInfo);

            StorageProviderSyncRootManager.Register(syncRootInfo);
            _lastIconRegistration = DateTime.UtcNow;
            _logger.LogInformation("Re-registered sync root with icon: {Icon}", iconResource);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReRegisterWithIconAsync failed for {SyncRootId}", syncRootId);
        }
    }

    internal static bool ShouldCleanupShellNamespaceRegistration(
        ShellNamespaceRegistrationInfo registration,
        string currentSyncRootId,
        string currentSyncRootPath,
        IReadOnlySet<string> activeSyncRootIds,
        bool activeSyncRootIdsKnown)
    {
        if (!LooksLikeManagedShellNamespaceRegistration(registration, currentSyncRootPath))
            return false;

        if (MatchesCurrentShellNamespaceRegistration(registration, currentSyncRootId, currentSyncRootPath))
            return false;

        if (activeSyncRootIdsKnown &&
            !string.IsNullOrWhiteSpace(registration.RegistrationValue) &&
            activeSyncRootIds.Contains(registration.RegistrationValue))
        {
            return false;
        }

        if (activeSyncRootIdsKnown)
            return true;

        var normalizedTargetPath = NormalizePath(registration.TargetFolderPath);
        var normalizedCurrentPath = NormalizePath(currentSyncRootPath);

        if (string.IsNullOrEmpty(normalizedTargetPath))
            return true;

        if (IsTempTestSyncRootPath(normalizedTargetPath))
            return true;

        if (!Directory.Exists(normalizedTargetPath))
            return true;

        return !string.Equals(normalizedTargetPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<ShellNamespaceRegistrationInfo> GetRedundantCurrentShellNamespaceRegistrations(
        IEnumerable<ShellNamespaceRegistrationInfo> registrations,
        string currentSyncRootId,
        string currentSyncRootPath)
    {
        var currentRegistrations = registrations
            .Where(registration => MatchesCurrentShellNamespaceRegistration(
                registration,
                currentSyncRootId,
                currentSyncRootPath))
            .ToList();

        if (currentRegistrations.Count == 0)
            return [];

        var normalizedCurrentPath = NormalizePath(currentSyncRootPath);
        var keepIndex = -1;

        if (!string.IsNullOrEmpty(normalizedCurrentPath))
        {
            keepIndex = currentRegistrations.FindIndex(registration =>
                string.Equals(
                    NormalizePath(registration.TargetFolderPath),
                    normalizedCurrentPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (keepIndex < 0)
        {
            keepIndex = currentRegistrations.FindIndex(registration =>
                string.Equals(
                    registration.RegistrationValue,
                    currentSyncRootId,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (keepIndex < 0)
            keepIndex = 0;

        var redundant = new List<ShellNamespaceRegistrationInfo>();
        for (int i = 0; i < currentRegistrations.Count; i++)
        {
            if (i == keepIndex)
                continue;

            redundant.Add(currentRegistrations[i]);
        }

        return redundant;
    }

    internal static bool IsTempTestSyncRootPath(string? path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return false;

        var normalizedTempRoot = NormalizePath(Path.GetTempPath());
        if (string.IsNullOrEmpty(normalizedTempRoot))
            return false;

        var tempPrefix = Path.Combine(normalizedTempRoot, "clouddrive-e2e-");
        return normalizedPath.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksLikeManagedShellNamespaceRegistration(
        ShellNamespaceRegistrationInfo registration,
        string? currentSyncRootPath)
    {
        if (!string.IsNullOrWhiteSpace(registration.RegistrationValue) &&
            registration.RegistrationValue.StartsWith(GetSyncRootIdPrefix(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (MatchesKnownProviderDisplayName(registration.RegistrationValue) ||
            MatchesKnownProviderDisplayName(registration.DisplayName))
        {
            return true;
        }

        var normalizedTargetPath = NormalizePath(registration.TargetFolderPath);
        var normalizedCurrentPath = NormalizePath(currentSyncRootPath);

        return !string.IsNullOrEmpty(normalizedTargetPath) &&
               !string.IsNullOrEmpty(normalizedCurrentPath) &&
               string.Equals(normalizedTargetPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesCurrentShellNamespaceRegistration(
        ShellNamespaceRegistrationInfo registration,
        string currentSyncRootId,
        string currentSyncRootPath)
    {
        if (!string.IsNullOrWhiteSpace(registration.RegistrationValue) &&
            string.Equals(registration.RegistrationValue, currentSyncRootId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedTargetPath = NormalizePath(registration.TargetFolderPath);
        var normalizedCurrentPath = NormalizePath(currentSyncRootPath);

        return !string.IsNullOrEmpty(normalizedTargetPath) &&
               !string.IsNullOrEmpty(normalizedCurrentPath) &&
               string.Equals(normalizedTargetPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase);
    }

    private List<ShellNamespaceRegistrationInfo> GetShellNamespaceRegistrations()
    {
        var registrations = new List<ShellNamespaceRegistrationInfo>();

        try
        {
            using var namespaceRoot = Registry.CurrentUser.OpenSubKey(DesktopNamespaceKeyPath);
            if (namespaceRoot == null)
                return registrations;

            foreach (var clsid in namespaceRoot.GetSubKeyNames())
            {
                using var namespaceEntry = namespaceRoot.OpenSubKey(clsid);
                var registrationValue = namespaceEntry?.GetValue(null) as string;
                var displayName = GetShellDisplayName(clsid);
                var targetFolderPath = GetShellTargetFolderPath(clsid);

                if (!LooksLikeManagedShellNamespaceRegistration(
                        new ShellNamespaceRegistrationInfo(clsid, registrationValue, displayName, targetFolderPath),
                        currentSyncRootPath: null))
                {
                    continue;
                }

                registrations.Add(new ShellNamespaceRegistrationInfo(clsid, registrationValue, displayName, targetFolderPath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Explorer shell namespace registrations");
        }

        return registrations;
    }

    private IReadOnlySet<string> GetActiveSyncRootIds(out bool succeeded)
    {
        try
        {
            succeeded = true;
            return StorageProviderSyncRootManager.GetCurrentSyncRoots()
                .Where(root => root.Id.StartsWith(GetSyncRootIdPrefix(), StringComparison.OrdinalIgnoreCase))
                .Select(root => root.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            succeeded = false;
            _logger.LogWarning(ex, "Failed to enumerate active sync root ids for shell cleanup");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void DeleteShellNamespaceRegistration(ShellNamespaceRegistrationInfo registration)
    {
        try
        {
            DeleteSubKeyTree(Registry.CurrentUser, $@"{DesktopNamespaceKeyPath}\{registration.Clsid}");
            DeleteSubKeyTree(Registry.CurrentUser, $@"{ClassesClsidKeyPath}\{registration.Clsid}");
            DeleteSubKeyTree(Registry.CurrentUser, $@"{Wow6432ClassesClsidKeyPath}\{registration.Clsid}");

            _logger.LogInformation(
                "Removed orphaned Explorer shell registration: {Identifier} (CLSID={Clsid}, Path={Path})",
                registration.Identifier,
                registration.Clsid,
                registration.TargetFolderPath ?? "<unknown>");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to remove orphaned Explorer shell registration: {Identifier} (CLSID={Clsid})",
                registration.Identifier,
                registration.Clsid);
        }
    }

    private static void DeleteSubKeyTree(RegistryKey root, string subKeyPath)
    {
        root.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
    }

    private static string? GetShellTargetFolderPath(string clsid)
    {
        return GetShellTargetFolderPath(ClassesClsidKeyPath, clsid)
            ?? GetShellTargetFolderPath(Wow6432ClassesClsidKeyPath, clsid);
    }

    private static string? GetShellDisplayName(string clsid)
    {
        return GetRegistryDefaultValue(ClassesClsidKeyPath, clsid)
            ?? GetRegistryDefaultValue(Wow6432ClassesClsidKeyPath, clsid);
    }

    private static string? GetShellTargetFolderPath(string basePath, string clsid)
    {
        using var propertyBagKey = Registry.CurrentUser.OpenSubKey(
            $@"{basePath}\{clsid}\Instance\InitPropertyBag");

        return propertyBagKey?.GetValue("TargetFolderPath") as string;
    }

    private static string? GetRegistryDefaultValue(string basePath, string clsid)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{basePath}\{clsid}");
        return key?.GetValue(null) as string;
    }

    private static string GetSyncRootIdPrefix()
    {
        return $"{ProviderId}!{Environment.UserName}!";
    }

    private static bool MatchesKnownProviderDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return string.Equals(value, ProviderDisplayName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, LegacyProviderDisplayName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, LegacyMojibakeProviderDisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static StorageProviderSyncRootInfo? GetCurrentSyncRoot(string syncRootId)
    {
        try
        {
            return StorageProviderSyncRootManager.GetCurrentSyncRoots()
                .FirstOrDefault(root => string.Equals(root.Id, syncRootId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static bool NeedsRegistrationRefresh(StorageProviderSyncRootInfo existingRegistration, string syncRootPath)
    {
        var normalizedExistingPath = NormalizePath(existingRegistration.Path?.Path);
        var normalizedRequestedPath = NormalizePath(syncRootPath);

        if (!string.Equals(normalizedExistingPath, normalizedRequestedPath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(
            existingRegistration.DisplayNameResource,
            ProviderDisplayName,
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedPropertyIds = new HashSet<int>
        {
            ExplorerItemStateService.SyncedPropertyId,
            ExplorerItemStateService.SyncingPropertyId,
            ExplorerItemStateService.ConflictPropertyId,
            ExplorerItemStateService.ErrorPropertyId,
            ExplorerItemStateService.PinnedPropertyId,
            ExplorerItemStateService.UnpinnedPropertyId
        };
        var registeredPropertyIds = existingRegistration.StorageProviderItemPropertyDefinitions
            .Select(definition => definition.Id)
            .ToHashSet();
        return !expectedPropertyIds.IsSubsetOf(registeredPropertyIds);
    }

    private static void AddItemPropertyDefinitions(StorageProviderSyncRootInfo syncRootInfo)
    {
        syncRootInfo.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition
        {
            Id = ExplorerItemStateService.SyncedPropertyId,
            DisplayNameResource = "Synced"
        });
        syncRootInfo.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition
        {
            Id = ExplorerItemStateService.SyncingPropertyId,
            DisplayNameResource = "Syncing"
        });
        syncRootInfo.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition
        {
            Id = ExplorerItemStateService.ConflictPropertyId,
            DisplayNameResource = "Conflict"
        });
        syncRootInfo.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition
        {
            Id = ExplorerItemStateService.ErrorPropertyId,
            DisplayNameResource = "Error"
        });
        syncRootInfo.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition
        {
            Id = ExplorerItemStateService.PinnedPropertyId,
            DisplayNameResource = "Pinned"
        });
        syncRootInfo.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition
        {
            Id = ExplorerItemStateService.UnpinnedPropertyId,
            DisplayNameResource = "Online-only"
        });
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
