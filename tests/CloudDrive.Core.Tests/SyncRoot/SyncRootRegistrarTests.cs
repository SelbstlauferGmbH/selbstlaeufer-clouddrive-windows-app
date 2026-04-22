using CloudDrive.Core.SyncRoot;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncRoot;

public class SyncRootRegistrarTests
{
    [Fact]
    public void ShouldCleanupShellNamespaceRegistration_ReturnsFalse_ForCurrentSyncRoot()
    {
        var registration = new ShellNamespaceRegistrationInfo(
            "{CURRENT}",
            "CloudDrive!testuser!current",
            "CloudDrive",
            @"C:\Users\testuser\CloudDrive");

        var shouldCleanup = SyncRootRegistrar.ShouldCleanupShellNamespaceRegistration(
            registration,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            activeSyncRootIdsKnown: true);

        Assert.False(shouldCleanup);
    }

    [Fact]
    public void ShouldCleanupShellNamespaceRegistration_ReturnsFalse_ForAnotherActiveSyncRoot()
    {
        var registration = new ShellNamespaceRegistrationInfo(
            "{ACTIVE}",
            "CloudDrive!testuser!other-account",
            "CloudDrive",
            @"C:\Users\testuser\OtherCloudDrive");

        var shouldCleanup = SyncRootRegistrar.ShouldCleanupShellNamespaceRegistration(
            registration,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CloudDrive!testuser!other-account"
            },
            activeSyncRootIdsKnown: true);

        Assert.False(shouldCleanup);
    }

    [Fact]
    public void ShouldCleanupShellNamespaceRegistration_ReturnsTrue_ForTempE2ETarget_WhenActiveRootsUnknown()
    {
        var registration = new ShellNamespaceRegistrationInfo(
            "{E2E}",
            "CloudDrive!testuser!e2e-123",
            "CloudDrive",
            Path.Combine(Path.GetTempPath(), "clouddrive-e2e-123", "syncroot"));

        var shouldCleanup = SyncRootRegistrar.ShouldCleanupShellNamespaceRegistration(
            registration,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            activeSyncRootIdsKnown: false);

        Assert.True(shouldCleanup);
    }

    [Fact]
    public void ShouldCleanupShellNamespaceRegistration_ReturnsTrue_ForMissingTarget_WhenActiveRootsUnknown()
    {
        var registration = new ShellNamespaceRegistrationInfo(
            "{MISSING}",
            "CloudDrive!testuser!old",
            "CloudDrive",
            @"C:\definitely-missing\syncroot");

        var shouldCleanup = SyncRootRegistrar.ShouldCleanupShellNamespaceRegistration(
            registration,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            activeSyncRootIdsKnown: false);

        Assert.True(shouldCleanup);
    }

    [Fact]
    public void ShouldCleanupShellNamespaceRegistration_ReturnsTrue_ForInactiveNonCurrentRoot_WhenActiveRootsKnown()
    {
        var registration = new ShellNamespaceRegistrationInfo(
            "{INACTIVE}",
            "CloudDrive!testuser!old",
            "CloudDrive",
            @"C:\Users\testuser\OldCloudDrive");

        var shouldCleanup = SyncRootRegistrar.ShouldCleanupShellNamespaceRegistration(
            registration,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            activeSyncRootIdsKnown: true);

        Assert.True(shouldCleanup);
    }

    [Fact]
    public void GetRedundantCurrentShellNamespaceRegistrations_ReturnsExtraEntriesForCurrentSyncRoot()
    {
        var registrations = new[]
        {
            new ShellNamespaceRegistrationInfo(
                "{KEEP}",
                "CloudDrive!testuser!current",
                "CloudDrive",
                @"C:\Users\testuser\CloudDrive"),
            new ShellNamespaceRegistrationInfo(
                "{REMOVE-SAME-PATH}",
                "CloudDrive!testuser!current",
                "CloudDrive",
                @"C:\Users\testuser\CloudDrive"),
            new ShellNamespaceRegistrationInfo(
                "{REMOVE-OLD-PATH}",
                "CloudDrive!testuser!current",
                "CloudDrive",
                @"C:\Users\testuser\OldCloudDrive"),
            new ShellNamespaceRegistrationInfo(
                "{OTHER}",
                "CloudDrive!testuser!other-account",
                "CloudDrive",
                @"C:\Users\testuser\OtherCloudDrive")
        };

        var redundant = SyncRootRegistrar.GetRedundantCurrentShellNamespaceRegistrations(
            registrations,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive");

        redundant.Select(registration => registration.Clsid).ShouldBe(["{REMOVE-SAME-PATH}", "{REMOVE-OLD-PATH}"]);
    }

    [Fact]
    public void GetRedundantCurrentShellNamespaceRegistrations_KeepsSingleCurrentEntry()
    {
        var registrations = new[]
        {
            new ShellNamespaceRegistrationInfo(
                "{ONLY}",
                "CloudDrive!testuser!current",
                "CloudDrive",
                @"C:\Users\testuser\CloudDrive")
        };

        var redundant = SyncRootRegistrar.GetRedundantCurrentShellNamespaceRegistrations(
            registrations,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive");

        redundant.ShouldBeEmpty();
    }

    [Fact]
    public void ShouldCleanupShellNamespaceRegistration_ReturnsTrue_ForLegacyDisplayName_WhenActiveRootsKnown()
    {
        var registration = new ShellNamespaceRegistrationInfo(
            "{LEGACY}",
            "Selbstläufer CloudDrive",
            "Selbstläufer CloudDrive",
            @"C:\Users\testuser\OldCloudDrive");

        var shouldCleanup = SyncRootRegistrar.ShouldCleanupShellNamespaceRegistration(
            registration,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CloudDrive!testuser!current"
            },
            activeSyncRootIdsKnown: true);

        shouldCleanup.ShouldBeTrue();
    }

    [Fact]
    public void GetRedundantCurrentShellNamespaceRegistrations_TreatsMatchingPathAsCurrent()
    {
        var registrations = new[]
        {
            new ShellNamespaceRegistrationInfo(
                "{KEEP}",
                "Selbstläufer CloudDrive",
                "Selbstläufer CloudDrive",
                @"C:\Users\testuser\CloudDrive"),
            new ShellNamespaceRegistrationInfo(
                "{REMOVE}",
                "CloudDrive",
                "CloudDrive",
                @"C:\Users\testuser\CloudDrive")
        };

        var redundant = SyncRootRegistrar.GetRedundantCurrentShellNamespaceRegistrations(
            registrations,
            "CloudDrive!testuser!current",
            @"C:\Users\testuser\CloudDrive");

        redundant.Select(registration => registration.Clsid).ShouldBe(["{REMOVE}"]);
    }
}
