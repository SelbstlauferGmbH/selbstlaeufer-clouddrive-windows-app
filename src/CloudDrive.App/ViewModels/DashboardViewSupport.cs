using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CloudDrive.App.Services;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;
using CloudDrive.Core.Infrastructure;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace CloudDrive.App.ViewModels;

public sealed class AppDashboardContext
{
    public AppDashboardContext(
        IActivityTracker activityTracker,
        SyncStateDb database,
        Func<AppSettings> settingsProvider,
        Func<SyncState> syncStateProvider,
        Func<bool> isPausedProvider,
        Func<IWebDavService?> webDavProvider,
        Func<UpdateStatusSnapshot> updateStatusProvider,
        Action<SettingsSection> openSettings,
        Action openFolder,
        Action openWebPortal,
        Func<Task>? syncNowAsync,
        Action togglePauseResume,
        Func<Task>? ensureWatchdogScheduledTaskAsync,
        Func<Task>? checkForUpdatesNowAsync,
        DateTime startedAt,
        Func<long, string, string, Task>? confirmRemoteDeleteAsync = null,
        Func<long, string, string, Task>? keepRemoteCopyAsync = null,
        Func<long, string, string, Task>? reuploadRemoteDeletedLocalChangeAsync = null,
        Func<long, string, string, Task>? deleteLocalRemoteDeletedLocalChangeAsync = null,
        Action? applyUpdateAndRestart = null)
    {
        ActivityTracker = activityTracker;
        Database = database;
        SettingsProvider = settingsProvider;
        SyncStateProvider = syncStateProvider;
        IsPausedProvider = isPausedProvider;
        WebDavProvider = webDavProvider;
        UpdateStatusProvider = updateStatusProvider;
        OpenSettings = openSettings;
        OpenFolder = openFolder;
        OpenWebPortal = openWebPortal;
        SyncNowAsync = syncNowAsync;
        TogglePauseResume = togglePauseResume;
        EnsureWatchdogScheduledTaskAsync = ensureWatchdogScheduledTaskAsync;
        CheckForUpdatesNowAsync = checkForUpdatesNowAsync;
        StartedAt = startedAt;
        ConfirmRemoteDeleteAsync = confirmRemoteDeleteAsync;
        KeepRemoteCopyAsync = keepRemoteCopyAsync;
        ReuploadRemoteDeletedLocalChangeAsync = reuploadRemoteDeletedLocalChangeAsync;
        DeleteLocalRemoteDeletedLocalChangeAsync = deleteLocalRemoteDeletedLocalChangeAsync;
        ApplyUpdateAndRestart = applyUpdateAndRestart ?? (() => { });
    }

    public IActivityTracker ActivityTracker { get; }
    public SyncStateDb Database { get; }
    public Func<AppSettings> SettingsProvider { get; }
    public Func<SyncState> SyncStateProvider { get; }
    public Func<bool> IsPausedProvider { get; }
    public Func<IWebDavService?> WebDavProvider { get; }
    public Func<UpdateStatusSnapshot> UpdateStatusProvider { get; }
    public Action<SettingsSection> OpenSettings { get; }
    public Action OpenFolder { get; }
    public Action OpenWebPortal { get; }
    public Func<Task>? SyncNowAsync { get; }
    public Action TogglePauseResume { get; }
    public Func<Task>? EnsureWatchdogScheduledTaskAsync { get; }
    public Func<Task>? CheckForUpdatesNowAsync { get; }
    public DateTime StartedAt { get; }
    public Func<long, string, string, Task>? ConfirmRemoteDeleteAsync { get; }
    public Func<long, string, string, Task>? KeepRemoteCopyAsync { get; }
    public Func<long, string, string, Task>? ReuploadRemoteDeletedLocalChangeAsync { get; }
    public Func<long, string, string, Task>? DeleteLocalRemoteDeletedLocalChangeAsync { get; }
    public Action ApplyUpdateAndRestart { get; }
}

public enum SettingsSection
{
    Activity,
    Problems,
    Statistics,
    HealthCheck,
    Storage,
    General,
    Account,
    Network,
    Advanced
}

public enum DashboardSeverity
{
    Info,
    Warning,
    Error
}

public enum DashboardHealthState
{
    Healthy,
    Warning,
    Error,
    Unknown
}

public enum ActivityActionKind
{
    All,
    Uploaded,
    Downloaded,
    Modified,
    Deleted,
    Synced,
    Created,
    System,
    Error
}

public enum ActivityFileTypeFilter
{
    All,
    Document,
    Spreadsheet,
    Pdf,
    Image,
    Archive,
    Text,
    Folder,
    Other
}

public enum StatisticsRange
{
    Hours24,
    Days7,
    Days30,
    Days90
}

public sealed class HealthSnapshot
{
    public DashboardHealthState State { get; init; } = DashboardHealthState.Unknown;
    public bool IsConfigured { get; init; }
    public HealthCheckFailure? FailureReason { get; init; }
    public long LatencyMs { get; init; }
    public DateTime? CheckedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class DashboardStatusBanner
{
    public required DashboardSeverity Severity { get; init; }
    public required string IconText { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string ActionLabel { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public Brush Background { get; init; } = Brushes.LightBlue;
    public Brush BorderBrush { get; init; } = Brushes.SteelBlue;
    public Brush Foreground { get; init; } = Brushes.MidnightBlue;
}

public sealed class DashboardMetricCard
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public required string Subtext { get; init; }
    public required DashboardHealthState State { get; init; }
    public required SettingsSection TargetSection { get; init; }
    public Brush AccentBrush { get; init; } = Brushes.SeaGreen;

    public string StateLabel => State switch
    {
        DashboardHealthState.Healthy => AppLocalizer.Instance.GetString("Common_Healthy"),
        DashboardHealthState.Warning => AppLocalizer.Instance.GetString("Common_Warning"),
        DashboardHealthState.Error => AppLocalizer.Instance.GetString("Common_Error"),
        _ => AppLocalizer.Instance.GetString("Common_Unknown")
    };
}

public sealed class DashboardHealthCheck
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required DashboardHealthState State { get; init; }
    public required string StatusText { get; init; }
    public required string LastCheckedText { get; init; }
    public required string ActionLabel { get; init; }
    public required string ActionId { get; init; }
    public Brush AccentBrush { get; init; } = Brushes.SeaGreen;
}

public enum ProblemActionKind
{
    None,
    OpenPath,
    OpenFolder,
    SyncNow,
    OpenSettings,
    ConfirmRemoteDelete,
    KeepRemoteCopy,
    ReuploadRemoteDeletedLocalChange,
    DeleteLocalRemoteDeletedLocalChange,
    Dismiss
}

public sealed class ProblemDisplayAction
{
    public required ProblemActionKind Kind { get; init; }
    public required string Label { get; init; }
    public string? Path { get; init; }
    public string? RemotePath { get; init; }
    public SettingsSection? Section { get; init; }
    public long? ProblemId { get; init; }
}

public sealed class ProblemDisplayItem
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Detail { get; init; }
    public required string RelativeTime { get; init; }
    public required string FullTimestamp { get; init; }
    public required string KindLabel { get; init; }
    public required string LocationDisplay { get; init; }
    public required string OccurrenceText { get; init; }
    public required Brush AccentBrush { get; init; }
    public required Brush BorderBrush { get; init; }
    public required Brush BackgroundBrush { get; init; }
    public required Brush ForegroundBrush { get; init; }
    public string? FolderPath { get; init; }
    public ProblemDisplayAction? PrimaryAction { get; init; }
    public ProblemDisplayAction? SecondaryAction { get; init; }
    public ProblemDisplayAction? DismissAction { get; init; }
    public bool HasLocationLink => !string.IsNullOrWhiteSpace(FolderPath);
    public bool HasPrimaryAction => PrimaryAction != null;
    public bool HasSecondaryAction => SecondaryAction != null;
    public bool HasDismissAction => DismissAction != null;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

public sealed class ActivityDisplayItem
{
    public required DateTime Timestamp { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string SubtitlePrefix { get; init; }
    public required string LocationDisplay { get; init; }
    public required string RelativeTime { get; init; }
    public required string FullTimestamp { get; init; }
    public required string GroupLabel { get; init; }
    public required string BadgeText { get; init; }
    public required Brush BadgeBackground { get; init; }
    public required Brush BadgeForeground { get; init; }
    public required ActivityActionKind ActionKind { get; init; }
    public required ActivityFileTypeFilter FileType { get; init; }
    public required string SearchText { get; init; }
    public string? OpenPath { get; init; }
    public string? FolderPath { get; init; }
    public bool OpenPathIsFolder { get; init; }
    public bool HasLocationLink => !string.IsNullOrWhiteSpace(FolderPath);
    public bool HasSubtitleOnly => !HasLocationLink;
    public string StatusLabel { get; init; } = string.Empty;
}

public sealed class ActivityDayGroup
{
    public required string Label { get; init; }
    public required IReadOnlyList<ActivityDisplayItem> Items { get; init; }
}

public sealed class SimpleChartBar
{
    public required string Label { get; init; }
    public required long Value { get; init; }
    public required double Ratio { get; init; }
    public required string DisplayValue { get; init; }
    public Brush Fill { get; init; } = Brushes.SteelBlue;
}

public partial class SettingsNavigationItem : ObservableObject
{
    public SettingsNavigationItem(
        string? group,
        SettingsSection section,
        string glyph,
        string title,
        string description,
        bool isPromoted = false,
        string? hintText = null)
    {
        Group = group;
        Section = section;
        Glyph = glyph;
        Title = title;
        Description = description;
        IsPromoted = isPromoted;
        HintText = hintText;
    }

    public string? Group { get; }
    public SettingsSection Section { get; }
    public string Glyph { get; }
    public string Title { get; }
    public string Description { get; }
    public bool IsPromoted { get; }
    public string? HintText { get; }
    public string? BadgeText => BadgeCount?.ToString(CultureInfo.CurrentCulture) ?? HintText;
    public Brush BadgeBackground => BadgeCount.HasValue
        ? DashboardThemePalette.Brush(220, 38, 38, 248, 113, 113)
        : IsPromoted
            ? DashboardThemePalette.Brush(180, 83, 9, 245, 158, 11)
            : Brushes.Transparent;
    public Brush BadgeForeground => Brushes.White;
    public Brush GlyphBackground => IsPromoted
        ? DashboardThemePalette.Brush(255, 247, 237, 55, 37, 9)
        : DashboardThemePalette.Brush(238, 242, 255, 35, 44, 73);
    public Brush GlyphForeground => IsPromoted
        ? DashboardThemePalette.Brush(194, 65, 12, 253, 186, 116)
        : DashboardThemePalette.Brush(67, 56, 202, 199, 210, 254);
    public Brush PromotionBackground => IsPromoted
        ? DashboardThemePalette.Brush(255, 251, 235, 51, 37, 9)
        : Brushes.Transparent;
    public Brush PromotionBorderBrush => IsPromoted
        ? DashboardThemePalette.Brush(253, 186, 116, 245, 158, 11)
        : Brushes.Transparent;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int? _badgeCount;

    partial void OnBadgeCountChanged(int? value)
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeBackground));
    }

    public void RefreshThemeState()
    {
        OnPropertyChanged(nameof(BadgeBackground));
        OnPropertyChanged(nameof(GlyphBackground));
        OnPropertyChanged(nameof(GlyphForeground));
        OnPropertyChanged(nameof(PromotionBackground));
        OnPropertyChanged(nameof(PromotionBorderBrush));
    }

    private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public partial class FilterOption<T> : ObservableObject where T : struct
{
    public FilterOption(T value, string label)
    {
        Value = value;
        Label = label;
    }

    public T Value { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;
}

internal static class DashboardThemePalette
{
    public static SolidColorBrush Brush(
        byte lightR,
        byte lightG,
        byte lightB,
        byte darkR,
        byte darkG,
        byte darkB)
    {
        return ThemeManager.IsDarkTheme
            ? CreateBrush(darkR, darkG, darkB)
            : CreateBrush(lightR, lightG, lightB);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public static class DashboardBuilder
{
    private static readonly Regex PathRegex = new(
        @"([A-Za-z]:\\[^:<>""|?*\r\n]+|/[^:<>""|?*\r\n]+)",
        RegexOptions.Compiled);

    private static string L(string key) => AppLocalizer.Instance.GetString(key);

    private static string LF(string key, params object?[] args) => AppLocalizer.Instance.Format(key, args);

    public static IReadOnlyList<SettingsNavigationItem> CreateNavigation()
    {
        return
        [
            new(null, SettingsSection.Account, "AC", L("Navigation_Account_Title"), L("Navigation_Account_Description")),
            new(L("Navigation_Group_Main"), SettingsSection.Activity, "ACT", L("Navigation_Activity_Title"), L("Navigation_Activity_Description")),
            new(L("Navigation_Group_Main"), SettingsSection.Problems, "FIX", L("Navigation_Problems_Title"), L("Navigation_Problems_Description")),
            new(L("Navigation_Group_Main"), SettingsSection.Statistics, "SS", L("Navigation_Statistics_Title"), L("Navigation_Statistics_Description")),
            new(L("Navigation_Group_Main"), SettingsSection.HealthCheck, "HC", L("Navigation_HealthCheck_Title"), L("Navigation_HealthCheck_Description")),
            new(L("Navigation_Group_Config"), SettingsSection.General, "GN", L("Navigation_General_Title"), L("Navigation_General_Description")),
            new(L("Navigation_Group_Config"), SettingsSection.Network, "NW", L("Navigation_Network_Title"), L("Navigation_Network_Description")),
            new(L("Navigation_Group_Config"), SettingsSection.Advanced, "AD", L("Navigation_Advanced_Title"), L("Navigation_Advanced_Description"))
        ];
    }

    public static string DescribeHealthStatusForDisplay(HealthSnapshot health) => DescribeHealthStatus(health);

    public static string DescribeHealthDetailForDisplay(HealthSnapshot health) => DescribeHealthDetail(health);

    public static IReadOnlyList<ActivityDisplayItem> BuildActivityItems(IEnumerable<ActivityEntry> entries, AppSettings settings)
    {
        return entries
            .OrderByDescending(entry => entry.Timestamp)
            .Select(entry => BuildActivityItem(entry, settings))
            .ToList();
    }

    public static IReadOnlyList<ProblemDisplayItem> BuildProblemItems(
        IEnumerable<SyncProblem> problems,
        AppSettings settings,
        int? take = null)
    {
        IEnumerable<SyncProblem> ordered = problems
            .Where(problem => problem.Status == SyncProblemStatus.Open)
            .OrderByDescending(problem => problem.LastOccurredAt);

        if (take.HasValue)
            ordered = ordered.Take(take.Value);

        return ordered
            .Select(problem => BuildProblemItem(problem, settings))
            .ToList();
    }

    public static IReadOnlyList<ActivityDayGroup> GroupActivities(IEnumerable<ActivityDisplayItem> items)
    {
        return items
            .GroupBy(item => item.GroupLabel)
            .Select(group => new ActivityDayGroup
            {
                Label = group.Key,
                Items = group.OrderByDescending(item => item.Timestamp).ToList()
            })
            .ToList();
    }

    public static IReadOnlyList<ActivityDisplayItem> FilterActivities(
        IEnumerable<ActivityDisplayItem> items,
        string? searchText,
        ActivityActionKind actionKind,
        ActivityFileTypeFilter fileType,
        TimeSpan? range = null)
    {
        var filtered = items;

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var query = searchText.Trim();
            filtered = filtered.Where(item =>
                item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (actionKind != ActivityActionKind.All)
        {
            filtered = filtered.Where(item => item.ActionKind == actionKind);
        }

        if (fileType != ActivityFileTypeFilter.All)
        {
            filtered = filtered.Where(item => item.FileType == fileType);
        }

        if (range.HasValue)
        {
            var threshold = DateTime.Now.Subtract(range.Value);
            filtered = filtered.Where(item => item.Timestamp >= threshold);
        }

        return filtered
            .OrderByDescending(item => item.Timestamp)
            .ToList();
    }

    public static IReadOnlyList<DashboardStatusBanner> BuildStatusBanners(
        SyncState syncState,
        bool isPaused,
        DatabaseStatistics? stats,
        HealthSnapshot health)
    {
        var banners = new List<DashboardStatusBanner>();
        var openProblems = stats?.OpenProblemCount ?? 0;
        var openConflicts = stats?.OpenConflictProblemCount ?? 0;

        if (isPaused || syncState == SyncState.Paused)
        {
            banners.Add(new DashboardStatusBanner
            {
                Severity = DashboardSeverity.Info,
                IconText = "||",
                Title = L("Banner_SyncPaused_Title"),
                Description = L("Banner_SyncPaused_Description"),
                ActionLabel = L("Common_Resume"),
                ActionId = "toggle-pause",
                Background = DashboardThemePalette.Brush(239, 246, 255, 16, 35, 60),
                BorderBrush = DashboardThemePalette.Brush(96, 165, 250, 96, 165, 250),
                Foreground = DashboardThemePalette.Brush(30, 64, 175, 147, 197, 253)
            });
        }

        if (health.State == DashboardHealthState.Error || syncState == SyncState.Disconnected)
        {
            banners.Add(new DashboardStatusBanner
            {
                Severity = DashboardSeverity.Error,
                IconText = "!",
                Title = L("Banner_ConnectionAttention_Title"),
                Description = DescribeHealthDetail(health),
                ActionLabel = L("Navigation_HealthCheck_Title"),
                ActionId = SettingsSection.HealthCheck.ToString(),
                Background = DashboardThemePalette.Brush(254, 242, 242, 53, 17, 22),
                BorderBrush = DashboardThemePalette.Brush(248, 113, 113, 248, 113, 113),
                Foreground = DashboardThemePalette.Brush(153, 27, 27, 252, 165, 165)
            });
        }
        else if (openProblems > 0)
        {
            var problemLabel = openConflicts > 0
                ? LF("Banner_ProblemsAttention_DescriptionWithConflicts", openProblems, openConflicts)
                : LF("Banner_ProblemsAttention_Description", openProblems);

            banners.Add(new DashboardStatusBanner
            {
                Severity = DashboardSeverity.Warning,
                IconText = "!",
                Title = L("Banner_ProblemsAttention_Title"),
                Description = problemLabel,
                ActionLabel = L("Navigation_Problems_Title"),
                ActionId = SettingsSection.Problems.ToString(),
                Background = DashboardThemePalette.Brush(255, 251, 235, 51, 37, 9),
                BorderBrush = DashboardThemePalette.Brush(251, 191, 36, 245, 158, 11),
                Foreground = DashboardThemePalette.Brush(146, 64, 14, 252, 211, 77)
            });
        }
        else if ((stats?.PendingItems ?? 0) > 0 || syncState == SyncState.Syncing)
        {
            banners.Add(new DashboardStatusBanner
            {
                Severity = DashboardSeverity.Warning,
                IconText = "!",
                Title = L("Banner_ChangesSyncing_Title"),
                Description = LF("Banner_ChangesSyncing_Description", stats?.PendingItems ?? 0),
                ActionLabel = L("Navigation_Statistics_Title"),
                ActionId = SettingsSection.Statistics.ToString(),
                Background = DashboardThemePalette.Brush(255, 251, 235, 51, 37, 9),
                BorderBrush = DashboardThemePalette.Brush(251, 191, 36, 245, 158, 11),
                Foreground = DashboardThemePalette.Brush(146, 64, 14, 252, 211, 77)
            });
        }

        if ((stats?.ErrorItems ?? 0) > 0 && banners.Count < 2)
        {
            var fallbackCount = Math.Max(stats!.OpenProblemCount, stats.ErrorItems);
            banners.Add(new DashboardStatusBanner
            {
                Severity = DashboardSeverity.Warning,
                IconText = "!",
                Title = L("Banner_ProblemsAttention_Title"),
                Description = LF("Banner_ProblemsFallback_Description", fallbackCount),
                ActionLabel = L("Navigation_Problems_Title"),
                ActionId = SettingsSection.Problems.ToString(),
                Background = DashboardThemePalette.Brush(255, 251, 235, 51, 37, 9),
                BorderBrush = DashboardThemePalette.Brush(251, 191, 36, 245, 158, 11),
                Foreground = DashboardThemePalette.Brush(146, 64, 14, 252, 211, 77)
            });
        }

        return banners.Take(2).ToList();
    }

    public static IReadOnlyList<DashboardMetricCard> BuildFlyoutMetrics(
        SyncState syncState,
        DatabaseStatistics? stats,
        HealthSnapshot health,
        DateTime startedAt)
    {
        var pendingCount = stats?.PendingItems ?? 0;
        var connected = health.State == DashboardHealthState.Healthy;
        var latencyText = DescribeHealthLatency(health);

        return
        [
            new DashboardMetricCard
            {
                Label = L("Metrics_Flyout_Sync_Label"),
                Value = pendingCount > 0 ? LF("Metrics_Flyout_Pending", pendingCount) : GetSyncStateLabel(syncState),
                Subtext = pendingCount > 0 ? L("Metrics_Flyout_Sync_SubtextPending") : L("Metrics_Flyout_Sync_SubtextState"),
                State = syncState switch
                {
                    SyncState.Error or SyncState.Disconnected => DashboardHealthState.Error,
                    SyncState.Syncing => DashboardHealthState.Warning,
                    SyncState.Paused => DashboardHealthState.Warning,
                    _ => DashboardHealthState.Healthy
                },
                TargetSection = SettingsSection.Statistics,
                AccentBrush = syncState switch
                {
                    SyncState.Error or SyncState.Disconnected => BrushFromRgb(220, 38, 38),
                    SyncState.Syncing or SyncState.Paused => BrushFromRgb(245, 158, 11),
                    _ => BrushFromRgb(34, 197, 94)
                }
            },
            new DashboardMetricCard
            {
                Label = L("Metrics_Flyout_Storage_Label"),
                Value = stats?.TotalTrackedDataDisplay ?? "0 B",
                Subtext = LF("Metrics_Flyout_TrackedItems", stats?.TotalItems ?? 0),
                State = ((stats?.ErrorItems ?? 0) > 0 || (stats?.OpenProblemCount ?? 0) > 0)
                    ? DashboardHealthState.Warning
                    : DashboardHealthState.Healthy,
                TargetSection = SettingsSection.Statistics,
                AccentBrush = BrushFromRgb(59, 130, 246)
            },
            new DashboardMetricCard
            {
                Label = L("Metrics_Flyout_Services_Label"),
                Value = connected ? L("Metrics_Flyout_Services_Online") : L("Metrics_Flyout_Services_Offline"),
                Subtext = string.IsNullOrWhiteSpace(latencyText) ? DescribeHealthStatus(health) : latencyText,
                State = health.State,
                TargetSection = SettingsSection.HealthCheck,
                AccentBrush = health.State switch
                {
                    DashboardHealthState.Error => BrushFromRgb(220, 38, 38),
                    DashboardHealthState.Warning => BrushFromRgb(245, 158, 11),
                    DashboardHealthState.Healthy => BrushFromRgb(34, 197, 94),
                    _ => BrushFromRgb(148, 163, 184)
                }
            },
            new DashboardMetricCard
            {
                Label = L("Metrics_Flyout_Uptime_Label"),
                Value = FormatUptime(startedAt),
                Subtext = L("Metrics_Flyout_Uptime_Subtext"),
                State = DashboardHealthState.Healthy,
                TargetSection = SettingsSection.HealthCheck,
                AccentBrush = BrushFromRgb(99, 102, 241)
            }
        ];
    }

    public static IReadOnlyList<DashboardMetricCard> BuildStatisticsCards(
        DatabaseStatistics? stats,
        IReadOnlyList<ActivityDisplayItem> activities)
    {
        var now = DateTime.Now;
        var recentErrors = activities.Count(item =>
            item.ActionKind == ActivityActionKind.Error &&
            item.Timestamp >= now.AddDays(-7));
        var previousErrors = activities.Count(item =>
            item.ActionKind == ActivityActionKind.Error &&
            item.Timestamp < now.AddDays(-7) &&
            item.Timestamp >= now.AddDays(-14));
        var recentSuccesses = activities.Count(item =>
            item.ActionKind != ActivityActionKind.System &&
            item.ActionKind != ActivityActionKind.Error &&
            item.Timestamp >= now.AddDays(-7));

        var errorTrend = recentErrors <= previousErrors
            ? LF("Metrics_Errors_TrendBetter", previousErrors - recentErrors)
            : LF("Metrics_Errors_TrendWorse", recentErrors - previousErrors);

        return
        [
            new DashboardMetricCard
            {
                Label = L("Metrics_TrackedItems_Label"),
                Value = (stats?.TotalItems ?? 0).ToString("N0", CultureInfo.CurrentCulture),
                Subtext = L("Metrics_TrackedItems_Subtext"),
                State = DashboardHealthState.Healthy,
                TargetSection = SettingsSection.Statistics,
                AccentBrush = BrushFromRgb(59, 130, 246)
            },
            new DashboardMetricCard
            {
                Label = L("Metrics_LocalData_Label"),
                Value = stats?.TotalTrackedDataDisplay ?? "0 B",
                Subtext = L("Metrics_LocalData_Subtext"),
                State = DashboardHealthState.Healthy,
                TargetSection = SettingsSection.Statistics,
                AccentBrush = BrushFromRgb(16, 185, 129)
            },
            new DashboardMetricCard
            {
                Label = L("Metrics_RecentActivity_Label"),
                Value = recentSuccesses.ToString("N0", CultureInfo.CurrentCulture),
                Subtext = L("Metrics_RecentActivity_Subtext"),
                State = recentSuccesses > 0 ? DashboardHealthState.Healthy : DashboardHealthState.Unknown,
                TargetSection = SettingsSection.Activity,
                AccentBrush = BrushFromRgb(99, 102, 241)
            },
            new DashboardMetricCard
            {
                Label = L("Metrics_Errors_Label"),
                Value = recentErrors.ToString("N0", CultureInfo.CurrentCulture),
                Subtext = errorTrend,
                State = recentErrors == 0 ? DashboardHealthState.Healthy : DashboardHealthState.Warning,
                TargetSection = SettingsSection.HealthCheck,
                AccentBrush = recentErrors == 0 ? BrushFromRgb(34, 197, 94) : BrushFromRgb(245, 158, 11)
            }
        ];
    }

    public static IReadOnlyList<DashboardHealthCheck> BuildHealthChecks(
        AppSettings settings,
        DatabaseStatistics? stats,
        HealthSnapshot health,
        UpdateStatusSnapshot updaterStatus,
        DateTime startedAt)
    {
        var checks = new List<DashboardHealthCheck>
        {
            new()
            {
                Name = L("HealthCheck_WebDav_Name"),
                Description = DescribeHealthDetail(health),
                State = health.State,
                StatusText = DescribeHealthStatus(health),
                LastCheckedText = health.CheckedAt.HasValue
                    ? FormatRelativeTime(health.CheckedAt.Value)
                    : L("HealthCheck_LastChecked_Never"),
                ActionLabel = L("Common_Recheck"),
                ActionId = "recheck-health",
                AccentBrush = BrushForState(health.State)
            },
            new()
            {
                Name = L("HealthCheck_Updater_Name"),
                Description = DescribeUpdaterDetail(updaterStatus),
                State = DescribeUpdaterState(updaterStatus),
                StatusText = DescribeUpdaterStatusText(updaterStatus),
                LastCheckedText = updaterStatus.LastCheckedUtc.HasValue
                    ? FormatRelativeTime(updaterStatus.LastCheckedUtc.Value.LocalDateTime)
                    : L("HealthCheck_LastChecked_Never"),
                ActionLabel = updaterStatus.State == UpdateStatusState.UpdateReady
                    ? L("Common_UpdateAndRestartNow")
                    : L("Common_CheckNow"),
                ActionId = updaterStatus.State == UpdateStatusState.UpdateReady
                    ? "install-update"
                    : "check-updates",
                AccentBrush = BrushForState(DescribeUpdaterState(updaterStatus))
            },
            new()
            {
                Name = L("HealthCheck_SyncRoot_Name"),
                Description = settings.SyncRootPath,
                State = Directory.Exists(settings.SyncRootPath)
                    ? DashboardHealthState.Healthy
                    : DashboardHealthState.Error,
                StatusText = Directory.Exists(settings.SyncRootPath) ? L("Common_Available") : L("Common_Missing"),
                LastCheckedText = LF("HealthCheck_SyncRoot_LastChecked", DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)),
                ActionLabel = L("Common_Open"),
                ActionId = "open-folder",
                AccentBrush = Directory.Exists(settings.SyncRootPath)
                    ? BrushFromRgb(34, 197, 94)
                    : BrushFromRgb(220, 38, 38)
            },
            new()
            {
                Name = L("HealthCheck_Database_Name"),
                Description = stats?.DatabasePath ?? L("HealthCheck_Database_NoPath"),
                State = stats == null
                    ? DashboardHealthState.Unknown
                    : File.Exists(stats.DatabasePath)
                        ? DashboardHealthState.Healthy
                        : DashboardHealthState.Warning,
                StatusText = stats == null
                    ? L("Common_Unknown")
                    : File.Exists(stats.DatabasePath) ? L("Common_Healthy") : L("HealthCheck_Database_NeedsAttention"),
                LastCheckedText = stats?.LastModifiedDisplay ?? L("Common_Never"),
                ActionLabel = L("Navigation_Statistics_Title"),
                ActionId = SettingsSection.Statistics.ToString(),
                AccentBrush = stats == null
                    ? BrushFromRgb(148, 163, 184)
                    : File.Exists(stats.DatabasePath)
                        ? BrushFromRgb(34, 197, 94)
                        : BrushFromRgb(245, 158, 11)
            },
            new()
            {
                Name = L("HealthCheck_AppSession_Name"),
                Description = L("HealthCheck_AppSession_Description"),
                State = DashboardHealthState.Healthy,
                StatusText = L("Common_Running"),
                LastCheckedText = FormatUptime(startedAt),
                ActionLabel = L("Navigation_Statistics_Title"),
                ActionId = SettingsSection.Statistics.ToString(),
                AccentBrush = BrushFromRgb(99, 102, 241)
            }
        };

        return checks;
    }

    private static DashboardHealthState DescribeUpdaterState(UpdateStatusSnapshot snapshot)
    {
        return snapshot.State switch
        {
            UpdateStatusState.Reachable => DashboardHealthState.Healthy,
            UpdateStatusState.UpdateReady => DashboardHealthState.Warning,
            UpdateStatusState.Checking => DashboardHealthState.Warning,
            UpdateStatusState.Failed => DashboardHealthState.Error,
            UpdateStatusState.NotInstalled => DashboardHealthState.Unknown,
            _ => DashboardHealthState.Unknown
        };
    }

    private static string DescribeUpdaterStatusText(UpdateStatusSnapshot snapshot)
    {
        return snapshot.State switch
        {
            UpdateStatusState.Reachable => L("HealthCheck_Updater_Status_Reachable"),
            UpdateStatusState.UpdateReady => L("HealthCheck_Updater_Status_UpdateReady"),
            UpdateStatusState.Checking => L("HealthCheck_Updater_Status_Checking"),
            UpdateStatusState.Failed => L("HealthCheck_Updater_Status_Failed"),
            UpdateStatusState.NotInstalled => L("HealthCheck_Updater_Status_NotInstalled"),
            _ => L("Common_Unknown")
        };
    }

    private static string DescribeUpdaterDetail(UpdateStatusSnapshot snapshot)
    {
        var lines = new List<string>();

        lines.Add(snapshot.State switch
        {
            UpdateStatusState.Reachable => L("HealthCheck_Updater_Detail_Reachable"),
            UpdateStatusState.UpdateReady => string.IsNullOrWhiteSpace(snapshot.AvailableVersion)
                ? L("HealthCheck_Updater_Detail_UpdateReady")
                : LF("HealthCheck_Updater_Detail_UpdateReadyWithVersion", snapshot.AvailableVersion),
            UpdateStatusState.Checking => L("HealthCheck_Updater_Detail_Checking"),
            UpdateStatusState.Failed => L("HealthCheck_Updater_Detail_Failed"),
            UpdateStatusState.NotInstalled => L("HealthCheck_Updater_Detail_NotInstalled"),
            _ => L("HealthCheck_Updater_Detail_NotChecked")
        });

        lines.Add(LF("HealthCheck_Updater_Source", snapshot.SourceUrl));

        if (!string.IsNullOrWhiteSpace(snapshot.CurrentVersion))
            lines.Add(LF("HealthCheck_Updater_CurrentVersion", snapshot.CurrentVersion));

        if (!string.IsNullOrWhiteSpace(snapshot.AvailableVersion))
            lines.Add(LF("HealthCheck_Updater_AvailableVersion", snapshot.AvailableVersion));

        if (!string.IsNullOrWhiteSpace(snapshot.AttemptedLocation) &&
            !string.Equals(snapshot.AttemptedLocation, snapshot.SourceUrl, StringComparison.Ordinal))
        {
            lines.Add(LF("HealthCheck_Updater_AttemptedLocation", snapshot.AttemptedLocation));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.FailureMessage))
            lines.Add(LF("HealthCheck_Updater_Failure", snapshot.FailureMessage));

        return string.Join(Environment.NewLine, lines);
    }

    public static IReadOnlyList<SimpleChartBar> BuildStatusChart(DatabaseStatistics? stats)
    {
        if (stats == null || stats.CountByStatus.Count == 0)
            return [];

        var max = Math.Max(1L, stats.CountByStatus.Values.Max());

        return stats.CountByStatus
            .OrderByDescending(pair => pair.Value)
            .Where(pair => pair.Value > 0)
            .Select(pair => new SimpleChartBar
            {
                Label = GetSyncStatusLabel(pair.Key),
                Value = pair.Value,
                Ratio = pair.Value / (double)max,
                DisplayValue = pair.Value.ToString("N0", CultureInfo.CurrentCulture),
                Fill = pair.Key switch
                {
                    SyncStatus.Synced => BrushFromRgb(34, 197, 94),
                    SyncStatus.Syncing => BrushFromRgb(59, 130, 246),
                    SyncStatus.PendingUpload or SyncStatus.PendingDownload or SyncStatus.RemoteDeletePendingUserChoice => BrushFromRgb(245, 158, 11),
                    SyncStatus.Error or SyncStatus.Conflict => BrushFromRgb(220, 38, 38),
                    _ => BrushFromRgb(148, 163, 184)
                }
            })
            .ToList();
    }

    public static IReadOnlyList<SimpleChartBar> BuildActivityChart(IEnumerable<ActivityDisplayItem> items)
    {
        var groups = items
            .GroupBy(item => item.ActionKind)
            .Select(group => new { group.Key, Count = group.LongCount() })
            .OrderByDescending(group => group.Count)
            .ToList();

        if (groups.Count == 0)
            return [];

        var max = Math.Max(1L, groups.Max(group => group.Count));

        return groups
            .Select(group => new SimpleChartBar
            {
                Label = GetActivityActionLabel(group.Key),
                Value = group.Count,
                Ratio = group.Count / (double)max,
                DisplayValue = group.Count.ToString("N0", CultureInfo.CurrentCulture),
                Fill = group.Key switch
                {
                    ActivityActionKind.Uploaded => BrushFromRgb(37, 99, 235),
                    ActivityActionKind.Downloaded => BrushFromRgb(14, 165, 233),
                    ActivityActionKind.Modified => BrushFromRgb(234, 179, 8),
                    ActivityActionKind.Deleted => BrushFromRgb(239, 68, 68),
                    ActivityActionKind.Synced => BrushFromRgb(34, 197, 94),
                    ActivityActionKind.Error => BrushFromRgb(220, 38, 38),
                    _ => BrushFromRgb(100, 116, 139)
                }
            })
            .ToList();
    }

    public static string BuildCsv(IEnumerable<ActivityDisplayItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L("Csv_Header"));

        foreach (var item in items)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(item.FullTimestamp),
                EscapeCsv(item.Title),
                EscapeCsv(string.IsNullOrWhiteSpace(item.LocationDisplay)
                    ? item.Subtitle
                    : $"{item.SubtitlePrefix} {item.LocationDisplay}"),
                EscapeCsv(item.LocationDisplay),
                EscapeCsv(item.StatusLabel)));
        }

        return sb.ToString();
    }

    public static string FormatRelativeTime(DateTime timestamp)
    {
        var delta = DateTime.Now - timestamp;

        if (delta.TotalHours >= 48)
            return timestamp.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);

        if (delta.TotalDays >= 1)
            return L("Common_Yesterday");

        if (delta.TotalHours >= 1)
        {
            var hours = Math.Max(1, (int)Math.Floor(delta.TotalHours));
            return hours == 1 ? LF("Time_HourAgo", hours) : LF("Time_HoursAgo", hours);
        }

        if (delta.TotalMinutes >= 1)
        {
            var minutes = Math.Max(1, (int)Math.Floor(delta.TotalMinutes));
            return minutes == 1 ? LF("Time_MinuteAgo", minutes) : LF("Time_MinutesAgo", minutes);
        }

        var seconds = Math.Max(1, (int)Math.Floor(Math.Max(delta.TotalSeconds, 1)));
        return seconds == 1 ? LF("Time_SecondAgo", seconds) : LF("Time_SecondsAgo", seconds);
    }

    public static string FormatUptime(DateTime startedAt)
    {
        var uptime = DateTime.Now - startedAt;

        if (uptime.TotalDays >= 1)
            return LF("Time_Uptime_DaysHours", (int)uptime.TotalDays, uptime.Hours);
        if (uptime.TotalHours >= 1)
            return LF("Time_Uptime_HoursMinutes", (int)uptime.TotalHours, uptime.Minutes);
        if (uptime.TotalMinutes >= 1)
            return LF("Time_Uptime_Minutes", (int)uptime.TotalMinutes);

        return LF("Time_Uptime_Seconds", Math.Max(1, (int)uptime.TotalSeconds));
    }

    private static ProblemDisplayItem BuildProblemItem(SyncProblem problem, AppSettings settings)
    {
        var focusPath = problem.ConflictCopyPath ?? problem.LocalPath;
        var folderPath = GetFolderPath(focusPath ?? problem.LocalPath);
        var locationDisplay = GetLocationDisplay(folderPath, settings.SyncRootPath);
        var (accent, border, background, foreground) = GetProblemPalette(problem);
        var primaryAction = BuildPrimaryProblemAction(problem);
        var secondaryAction = BuildSecondaryProblemAction(problem, folderPath);
        var localizedProblem = LocalizeProblem(problem);

        return new ProblemDisplayItem
        {
            Id = problem.Id,
            Title = localizedProblem.Title,
            Summary = localizedProblem.Summary,
            Detail = localizedProblem.Detail,
            RelativeTime = FormatRelativeTime(problem.LastOccurredAt.ToLocalTime()),
            FullTimestamp = problem.LastOccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            KindLabel = problem.ProblemType switch
            {
                SyncProblemType.Conflict => L("Problem_Kind_Conflict"),
                SyncProblemType.Connection => L("Problem_Kind_Connection"),
                SyncProblemType.RemoteLock => L("Problem_Kind_RemoteLock"),
                SyncProblemType.RemoteDeleteConfirmation => L("Problem_Kind_RemoteDeleteConfirmation"),
                SyncProblemType.RemoteDeletedLocalChanged => L("Problem_Kind_RemoteDeletedLocalChanged"),
                SyncProblemType.RemoteListing or SyncProblemType.RemoteSync => L("Problem_Kind_SyncError"),
                SyncProblemType.Upload => L("Problem_Kind_UploadError"),
                SyncProblemType.Download => L("Problem_Kind_DownloadError"),
                _ => L("Problem_Kind_Generic")
            },
            LocationDisplay = locationDisplay,
            OccurrenceText = problem.OccurrenceCount > 1
                ? LF("Problem_Occurrence_Repeated", problem.OccurrenceCount)
                : L("Problem_Occurrence_First"),
            AccentBrush = accent,
            BorderBrush = border,
            BackgroundBrush = background,
            ForegroundBrush = foreground,
            FolderPath = folderPath,
            PrimaryAction = primaryAction,
            SecondaryAction = secondaryAction,
            DismissAction = problem.ProblemType is SyncProblemType.RemoteDeleteConfirmation or SyncProblemType.RemoteDeletedLocalChanged
                ? null
                : new ProblemDisplayAction
                {
                    Kind = ProblemActionKind.Dismiss,
                    Label = L("Common_Dismiss"),
                    ProblemId = problem.Id
                }
        };
    }

    private static ActivityDisplayItem BuildActivityItem(ActivityEntry entry, AppSettings settings)
    {
        var message = entry.Message.Trim();
        var pathToken = ExtractPath(message);
        var localPath = ResolveLocalPath(pathToken, settings);
        var folderPath = GetFolderPath(localPath);
        var actionKind = GetActionKind(entry, message);
        var fileType = GetFileType(pathToken, localPath, actionKind);
        var badge = GetBadge(fileType);
        var title = GetTitle(message, pathToken, localPath, actionKind);
        var subtitlePrefix = GetSubtitlePrefix(actionKind, entry.Status, localPath, pathToken);
        var locationDisplay = GetLocationDisplay(folderPath, settings.SyncRootPath);
        var subtitle = locationDisplay.Length > 0
            ? $"{subtitlePrefix} {locationDisplay}"
            : string.IsNullOrWhiteSpace(subtitlePrefix) ? message : subtitlePrefix;

        return new ActivityDisplayItem
        {
            Timestamp = entry.Timestamp,
            Title = title,
            Subtitle = subtitle,
            SubtitlePrefix = subtitlePrefix,
            LocationDisplay = locationDisplay,
            RelativeTime = FormatRelativeTime(entry.Timestamp),
            FullTimestamp = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            GroupLabel = GetGroupLabel(entry.Timestamp),
            BadgeText = badge.Text,
            BadgeBackground = badge.Background,
            BadgeForeground = badge.Foreground,
            ActionKind = actionKind,
            FileType = fileType,
            SearchText = $"{title} {subtitle} {message} {locationDisplay}",
            OpenPath = localPath,
            FolderPath = folderPath,
            OpenPathIsFolder = Directory.Exists(localPath),
            StatusLabel = GetActivityStatusLabel(entry.Status)
        };
    }

    private static ProblemDisplayAction? BuildPrimaryProblemAction(SyncProblem problem)
    {
        if (!string.IsNullOrWhiteSpace(problem.ConflictCopyPath))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.OpenPath,
                Label = L("Problem_Action_OpenConflictCopy"),
                Path = problem.ConflictCopyPath
            };
        }

        if (problem.ProblemType == SyncProblemType.RemoteDeleteConfirmation &&
            !string.IsNullOrWhiteSpace(problem.LocalPath) &&
            !string.IsNullOrWhiteSpace(problem.RemotePath))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.ConfirmRemoteDelete,
                Label = L("Problem_Action_DeleteRemote"),
                Path = problem.LocalPath,
                RemotePath = problem.RemotePath,
                ProblemId = problem.Id
            };
        }

        if (problem.ProblemType == SyncProblemType.RemoteDeletedLocalChanged &&
            !string.IsNullOrWhiteSpace(problem.LocalPath) &&
            !string.IsNullOrWhiteSpace(problem.RemotePath))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.ReuploadRemoteDeletedLocalChange,
                Label = L("Problem_Action_ReuploadLocal"),
                Path = problem.LocalPath,
                RemotePath = problem.RemotePath,
                ProblemId = problem.Id
            };
        }

        if (!string.IsNullOrWhiteSpace(problem.LocalPath) &&
            (File.Exists(problem.LocalPath) || Directory.Exists(problem.LocalPath)))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.OpenPath,
                Label = Directory.Exists(problem.LocalPath) ? L("Common_OpenFolder") : L("Common_OpenFile"),
                Path = problem.LocalPath
            };
        }

        if (problem.ProblemType is SyncProblemType.Connection or SyncProblemType.RemoteListing or SyncProblemType.RemoteSync or SyncProblemType.Upload)
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.SyncNow,
                Label = L("Common_SyncNow")
            };
        }

        return null;
    }

    private static ProblemDisplayAction? BuildSecondaryProblemAction(SyncProblem problem, string? folderPath)
    {
        if (problem.ProblemType == SyncProblemType.RemoteDeleteConfirmation &&
            !string.IsNullOrWhiteSpace(problem.LocalPath) &&
            !string.IsNullOrWhiteSpace(problem.RemotePath))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.KeepRemoteCopy,
                Label = L("Problem_Action_KeepRemote"),
                Path = problem.LocalPath,
                RemotePath = problem.RemotePath,
                ProblemId = problem.Id
            };
        }

        if (problem.ProblemType == SyncProblemType.RemoteDeletedLocalChanged &&
            !string.IsNullOrWhiteSpace(problem.LocalPath) &&
            !string.IsNullOrWhiteSpace(problem.RemotePath))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.DeleteLocalRemoteDeletedLocalChange,
                Label = L("Problem_Action_DeleteLocal"),
                Path = problem.LocalPath,
                RemotePath = problem.RemotePath,
                ProblemId = problem.Id
            };
        }

        if (problem.ProblemType == SyncProblemType.Conflict &&
            !string.IsNullOrWhiteSpace(problem.LocalPath) &&
            File.Exists(problem.LocalPath))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.OpenPath,
                Label = L("Problem_Action_OpenOriginal"),
                Path = problem.LocalPath
            };
        }

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.OpenFolder,
                Label = L("Common_OpenFolder"),
                Path = folderPath
            };
        }

        if (problem.ProblemType is SyncProblemType.Connection or SyncProblemType.RemoteListing or SyncProblemType.RemoteSync)
        {
            return new ProblemDisplayAction
            {
                Kind = ProblemActionKind.OpenSettings,
                Label = L("Problem_Action_HealthCheck"),
                Section = SettingsSection.HealthCheck
            };
        }

        return null;
    }

    private static string? ExtractPath(string message)
    {
        var match = PathRegex.Match(message);
        return match.Success ? match.Value.TrimEnd('.', ',', ';') : null;
    }

    private static string? ResolveLocalPath(string? pathToken, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(pathToken))
            return null;

        if (Path.IsPathRooted(pathToken) && !pathToken.StartsWith('/'))
            return pathToken;

        if (pathToken.StartsWith('/'))
        {
            var relative = pathToken.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(settings.SyncRootPath, relative);
        }

        return null;
    }

    private static string? GetFolderPath(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            return null;

        if (Directory.Exists(localPath))
            return localPath;

        return Path.GetDirectoryName(localPath);
    }

    private static ActivityActionKind GetActionKind(ActivityEntry entry, string message)
    {
        if (entry.Status == ActivityStatus.Failed ||
            ContainsAny(message, "conflict", "konflikt"))
            return ActivityActionKind.Error;

        if (ContainsAny(message, "upload", "hochlad"))
            return ActivityActionKind.Uploaded;
        if (ContainsAny(message, "download", "hydrat", "herunterlad"))
            return ActivityActionKind.Downloaded;
        if (ContainsAny(message, "delet", "gelösch", "geloesch", "entfern"))
            return ActivityActionKind.Deleted;
        if (ContainsAny(message, "rename", "move", "verschob", "umben"))
            return ActivityActionKind.Modified;
        if (ContainsAny(message, "create", "erstellt"))
            return ActivityActionKind.Created;
        if (ContainsAny(message, "sync", "synchron", "list"))
            return ActivityActionKind.Synced;

        return ActivityActionKind.System;
    }

    private static ActivityFileTypeFilter GetFileType(string? pathToken, string? localPath, ActivityActionKind actionKind)
    {
        if (actionKind == ActivityActionKind.System || actionKind == ActivityActionKind.Error)
            return ActivityFileTypeFilter.Other;

        if ((!string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath)) ||
            (pathToken?.EndsWith('/') ?? false))
            return ActivityFileTypeFilter.Folder;

        var extension = Path.GetExtension(localPath ?? pathToken ?? string.Empty)
            .TrimStart('.')
            .ToLowerInvariant();

        return extension switch
        {
            "pdf" => ActivityFileTypeFilter.Pdf,
            "xls" or "xlsx" or "csv" => ActivityFileTypeFilter.Spreadsheet,
            "doc" or "docx" or "ppt" or "pptx" => ActivityFileTypeFilter.Document,
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "svg" or "webp" => ActivityFileTypeFilter.Image,
            "zip" or "7z" or "rar" or "tar" or "gz" => ActivityFileTypeFilter.Archive,
            "txt" or "md" or "json" or "xml" or "log" or "cs" => ActivityFileTypeFilter.Text,
            "" => ActivityFileTypeFilter.Other,
            _ => ActivityFileTypeFilter.Other
        };
    }

    private static (string Text, Brush Background, Brush Foreground) GetBadge(ActivityFileTypeFilter fileType)
    {
        return fileType switch
        {
            ActivityFileTypeFilter.Pdf => ("PDF", BrushFromRgb(220, 38, 38), Brushes.White),
            ActivityFileTypeFilter.Spreadsheet => ("XLS", BrushFromRgb(22, 163, 74), Brushes.White),
            ActivityFileTypeFilter.Document => ("DOC", BrushFromRgb(37, 99, 235), Brushes.White),
            ActivityFileTypeFilter.Image => ("IMG", BrushFromRgb(8, 145, 178), Brushes.White),
            ActivityFileTypeFilter.Archive => ("ZIP", BrushFromRgb(202, 138, 4), Brushes.White),
            ActivityFileTypeFilter.Text => ("TXT", BrushFromRgb(100, 116, 139), Brushes.White),
            ActivityFileTypeFilter.Folder => ("DIR", BrushFromRgb(79, 70, 229), Brushes.White),
            _ => ("SYS", BrushFromRgb(71, 85, 105), Brushes.White)
        };
    }

    private static string GetTitle(string message, string? pathToken, string? localPath, ActivityActionKind actionKind)
    {
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            var fileName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }

        if (!string.IsNullOrWhiteSpace(pathToken))
        {
            var normalized = pathToken.TrimEnd('/', '\\');
            var lastSegment = normalized.Split('/', '\\').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(lastSegment))
                return lastSegment;
        }

        if (actionKind == ActivityActionKind.System)
            return message;

        return L("Activity_Event_Title");
    }

    private static string GetSubtitlePrefix(
        ActivityActionKind actionKind,
        ActivityStatus status,
        string? localPath,
        string? pathToken)
    {
        if (actionKind == ActivityActionKind.System)
            return string.Empty;

        var isDirectory = (!string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath)) ||
                          (pathToken?.EndsWith('/') ?? false);
        var objectLabel = isDirectory ? L("Activity_Object_Folder") : L("Activity_Object_Item");

        return actionKind switch
        {
            ActivityActionKind.Uploaded => status == ActivityStatus.InProgress ? LF("Activity_Subtitle_UploadingTo", objectLabel) : LF("Activity_Subtitle_UploadedTo", objectLabel),
            ActivityActionKind.Downloaded => status == ActivityStatus.InProgress ? LF("Activity_Subtitle_DownloadingFrom", objectLabel) : LF("Activity_Subtitle_DownloadedFrom", objectLabel),
            ActivityActionKind.Modified => LF("Activity_Subtitle_UpdatedIn", objectLabel),
            ActivityActionKind.Deleted => LF("Activity_Subtitle_DeletedFrom", objectLabel),
            ActivityActionKind.Created => LF("Activity_Subtitle_CreatedIn", objectLabel),
            ActivityActionKind.Synced => LF("Activity_Subtitle_SyncedIn", objectLabel),
            ActivityActionKind.Error => L("Activity_Subtitle_NeedsAttentionIn"),
            _ => LF("Activity_Subtitle_UpdatedIn", objectLabel)
        };
    }

    private static string GetLocationDisplay(string? folderPath, string syncRootPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return string.Empty;

        try
        {
            if (!string.IsNullOrWhiteSpace(syncRootPath) &&
                folderPath.StartsWith(syncRootPath, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(syncRootPath, folderPath);
                if (relative == ".")
                    return L("App_Name");

                return relative.Replace(Path.DirectorySeparatorChar, '/');
            }
        }
        catch
        {
        }

        return Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar))
            ?? folderPath;
    }

    private static string GetGroupLabel(DateTime timestamp)
    {
        var today = DateTime.Today;
        if (timestamp.Date == today)
            return L("Common_Today");
        if (timestamp.Date == today.AddDays(-1))
            return L("Common_Yesterday");

        return timestamp.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);
    }

    private static (string Title, string Summary, string Detail) LocalizeProblem(SyncProblem problem)
    {
        var fileName = Path.GetFileName(problem.LocalPath ?? problem.RemotePath ?? string.Empty);
        var normalizedFileName = string.IsNullOrWhiteSpace(fileName) ? L("Activity_Event_Title") : fileName;

        return problem.ProblemType switch
        {
            SyncProblemType.Conflict => (
                LF("Problem_Conflict_Title", normalizedFileName),
                !string.IsNullOrWhiteSpace(problem.ConflictCopyPath)
                    ? LF("Problem_Conflict_Summary_WithCopy", Path.GetFileName(problem.ConflictCopyPath))
                    : L("Problem_Conflict_Summary_Generic"),
                L("Problem_Conflict_Detail")),
            SyncProblemType.Upload => (
                LF("Problem_CouldNotSync_Title", normalizedFileName),
                LF("Problem_Upload_Summary", normalizedFileName),
                problem.Details),
            SyncProblemType.Connection => (
                L("Problem_ConnectionLost_Title"),
                L("Problem_ConnectionLost_Summary"),
                problem.Details),
            SyncProblemType.RemoteLock => (
                problem.Title,
                problem.Summary,
                problem.Details),
            SyncProblemType.RemoteDeleteConfirmation => (
                problem.Title,
                problem.Summary,
                problem.Details),
            SyncProblemType.RemoteDeletedLocalChanged => (
                problem.Title,
                problem.Summary,
                problem.Details),
            SyncProblemType.RemoteListing => (
                L("Problem_RemoteFolder_Title"),
                L("Problem_RemoteFolder_Summary"),
                problem.Details),
            SyncProblemType.RemoteSync => (
                L("Problem_RemoteSync_Title"),
                L("Problem_RemoteSync_Summary"),
                problem.Details),
            _ => (problem.Title, problem.Summary, problem.Details)
        };
    }

    private static string DescribeHealthStatus(HealthSnapshot health)
    {
        if (!health.IsConfigured)
            return L("Common_NotConfigured");

        if (health.FailureReason.HasValue)
            return GetHealthFailureLabel(health.FailureReason.Value);

        return health.State switch
        {
            DashboardHealthState.Healthy => L("Common_Healthy"),
            DashboardHealthState.Warning => L("Common_Warning"),
            DashboardHealthState.Error => L("Common_Error"),
            _ => L("Common_NotChecked")
        };
    }

    private static string DescribeHealthDetail(HealthSnapshot health)
    {
        if (!health.IsConfigured)
            return L("Health_Detail_NotConfigured");

        if (health.State == DashboardHealthState.Healthy)
            return LF("Health_Detail_Healthy", health.LatencyMs);

        if (!string.IsNullOrWhiteSpace(health.ErrorMessage))
            return health.ErrorMessage;

        if (health.FailureReason.HasValue)
            return LF("Health_Detail_Failed", GetHealthFailureLabel(health.FailureReason.Value));

        return L("Health_Detail_Unavailable");
    }

    private static string DescribeHealthLatency(HealthSnapshot health)
    {
        if (health.LatencyMs <= 0)
            return L("Common_NotAvailable");

        return string.Format(CultureInfo.CurrentCulture, "{0} ms", health.LatencyMs);
    }

    private static string GetHealthFailureLabel(HealthCheckFailure failureReason)
    {
        return failureReason switch
        {
            HealthCheckFailure.ServerUnreachable => L("Health_Failure_ServerUnreachable"),
            HealthCheckFailure.HighLatency => L("Health_Failure_HighLatency"),
            HealthCheckFailure.ListingFailed => L("Health_Failure_ListingFailed"),
            _ => L("Common_Unknown")
        };
    }

    private static string GetSyncStateLabel(SyncState syncState)
    {
        return syncState switch
        {
            SyncState.Synced => L("SyncState_Synced"),
            SyncState.Syncing => L("SyncState_Syncing"),
            SyncState.Error => L("SyncState_Error"),
            SyncState.Paused => L("SyncState_Paused"),
            SyncState.Disconnected => L("SyncState_Disconnected"),
            _ => syncState.ToString()
        };
    }

    private static string GetSyncStatusLabel(SyncStatus syncStatus)
    {
        return syncStatus switch
        {
            SyncStatus.PendingUpload => L("SyncStatus_PendingUpload"),
            SyncStatus.PendingDownload => L("SyncStatus_PendingDownload"),
            SyncStatus.Syncing => L("SyncStatus_Syncing"),
            SyncStatus.Synced => L("SyncStatus_Synced"),
            SyncStatus.Error => L("SyncStatus_Error"),
            SyncStatus.Conflict => L("SyncStatus_Conflict"),
            SyncStatus.RemoteDeletePendingUserChoice => L("SyncStatus_RemoteDeletePendingUserChoice"),
            _ => syncStatus.ToString()
        };
    }

    private static string GetActivityActionLabel(ActivityActionKind actionKind)
    {
        return actionKind switch
        {
            ActivityActionKind.Uploaded => L("ActivityAction_Uploaded"),
            ActivityActionKind.Downloaded => L("ActivityAction_Downloaded"),
            ActivityActionKind.Modified => L("ActivityAction_Modified"),
            ActivityActionKind.Deleted => L("ActivityAction_Deleted"),
            ActivityActionKind.Synced => L("ActivityAction_Synced"),
            ActivityActionKind.Created => L("ActivityAction_Created"),
            ActivityActionKind.System => L("ActivityAction_System"),
            ActivityActionKind.Error => L("ActivityAction_Error"),
            _ => actionKind.ToString()
        };
    }

    private static string GetActivityStatusLabel(ActivityStatus status)
    {
        return status switch
        {
            ActivityStatus.InProgress => L("SyncState_Syncing"),
            ActivityStatus.Success => L("Common_Healthy"),
            ActivityStatus.Failed => L("Common_Error"),
            _ => status.ToString()
        };
    }

    private static bool ContainsAny(string message, params string[] parts)
    {
        return parts.Any(part => message.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static Brush BrushForState(DashboardHealthState state)
    {
        return state switch
        {
            DashboardHealthState.Healthy => BrushFromRgb(34, 197, 94),
            DashboardHealthState.Warning => BrushFromRgb(245, 158, 11),
            DashboardHealthState.Error => BrushFromRgb(220, 38, 38),
            _ => BrushFromRgb(148, 163, 184)
        };
    }

    private static (Brush Accent, Brush Border, Brush Background, Brush Foreground) GetProblemPalette(SyncProblem problem)
    {
        return problem.Severity switch
        {
            SyncProblemSeverity.Warning => (
                BrushFromRgb(245, 158, 11),
                DashboardThemePalette.Brush(251, 191, 36, 245, 158, 11),
                DashboardThemePalette.Brush(255, 251, 235, 51, 37, 9),
                DashboardThemePalette.Brush(146, 64, 14, 252, 211, 77)),
            _ => (
                BrushFromRgb(220, 38, 38),
                DashboardThemePalette.Brush(248, 113, 113, 248, 113, 113),
                DashboardThemePalette.Brush(254, 242, 242, 53, 17, 22),
                DashboardThemePalette.Brush(153, 27, 27, 252, 165, 165))
        };
    }

    private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string EscapeCsv(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
