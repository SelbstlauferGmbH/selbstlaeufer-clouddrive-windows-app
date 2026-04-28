using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CloudDrive.App.Services;
using CloudDrive.App.ViewModels;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncEngine;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Serilog;

namespace CloudDrive.App.Views;

public partial class ActivityPanel : Window
{
    private readonly AppDashboardContext _dashboardContext;
    private readonly ActivityTracker? _activityTracker;
    private readonly DispatcherTimer _refreshTimer;
    private readonly ActivityPanelViewModel _viewModel;
    private IReadOnlyList<ActivityDisplayItem> _allActivities = [];
    private IReadOnlyList<ProblemDisplayItem> _allProblems = [];
    private HealthSnapshot _healthSnapshot = new();
    private bool _allowClose;
    private int _visibleActivityCount = 20;
    private FlyoutEdge _flyoutEdge = FlyoutEdge.Bottom;
    private DateTime _ignoreDeactivateUntilUtc = DateTime.MinValue;
    private readonly EventHandler<ThemeChangedEventArgs> _themeChangedHandler;

    public ActivityPanel(AppDashboardContext dashboardContext)
    {
        InitializeComponent();
        ThemeManager.ApplyWindowTheme(this);

        _dashboardContext = dashboardContext;
        _activityTracker = dashboardContext.ActivityTracker as ActivityTracker;
        _viewModel = new ActivityPanelViewModel();
        DataContext = _viewModel;

        RebuildQuickActions();
        _themeChangedHandler = async (_, _) =>
        {
            if (!IsLoaded)
                return;

            await await Dispatcher.InvokeAsync(() => RefreshAsync(forceHealthCheck: true));
        };
        ThemeManager.ThemeChanged += _themeChangedHandler;
        AppLocalizer.Instance.CultureChanged += OnCultureChanged;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        Deactivated += (_, _) =>
        {
            Log.Information(
                "UI[Flyout] Deactivated. Visible={Visible} IgnoreUntil={IgnoreUntil:o} Now={Now:o}",
                IsVisible,
                _ignoreDeactivateUntilUtc,
                DateTime.UtcNow);

            if (IsVisible)
            {
                if (DateTime.UtcNow < _ignoreDeactivateUntilUtc)
                {
                    Log.Information("UI[Flyout] Ignoring early deactivate during open grace period");
                    return;
                }

                Log.Information("UI[Flyout] Deactivated outside grace period. Hiding.");
                HideFlyout();
            }
        };
    }

    public async Task ShowFlyoutAsync()
    {
        Log.Information("UI[Flyout] ShowFlyoutAsync start");
        await RefreshAsync();
        PositionFlyout();
        _ignoreDeactivateUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        Log.Information(
            "UI[Flyout] Positioned at Left={Left}, Top={Top}, Width={Width}, Height={Height}, Edge={Edge}",
            Left,
            Top,
            Width,
            Height,
            _flyoutEdge);

        if (!IsVisible)
        {
            Log.Information("UI[Flyout] Calling Show()");
            Show();
        }

        PanelRoot.Opacity = 1;
        Log.Information("UI[Flyout] Calling Activate()");
        Activate();
        Focus();
        _refreshTimer.Start();
        Log.Information("UI[Flyout] ShowFlyoutAsync completed. Visible={Visible}", IsVisible);
    }

    public void HideFlyout()
    {
        Log.Information("UI[Flyout] Hide requested. Visible={Visible}", IsVisible);
        _refreshTimer.Stop();
        if (IsVisible)
        {
            Log.Information("UI[Flyout] Completing hide with Hide()");
            Hide();
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RebuildQuickActions();
        _ = Dispatcher.InvokeAsync(async () => await RefreshAsync(forceHealthCheck: false));
    }

    private void RebuildQuickActions()
    {
        ReplaceCollection(_viewModel.QuickActions,
        [
            new QuickActionItem("open-folder", "DIR", AppLocalizer.Instance.GetString("Common_OpenFolder")),
            new QuickActionItem("open-activity-log", "LOG", AppLocalizer.Instance.GetString("Navigation_Activity_Title")),
            new QuickActionItem("statistics", "SS", AppLocalizer.Instance.GetString("Navigation_Statistics_Title")),
            new QuickActionItem("sync-now", "NOW", AppLocalizer.Instance.GetString("Common_SyncNow"))
        ]);
    }

    private async Task RefreshAsync(bool forceHealthCheck = false)
    {
        Log.Debug("UI[Flyout] Refresh start. ForceHealthCheck={ForceHealthCheck}", forceHealthCheck);
        var settings = _dashboardContext.SettingsProvider();
        _dashboardContext.Database.EnsureProblemEntriesForTrackedItemStates();
        _viewModel.HeaderTitle = string.IsNullOrWhiteSpace(settings.WebDavUrl)
            ? AppLocalizer.Instance.GetString("App_Name")
            : $"{AppLocalizer.Instance.GetString("App_Name")} - {TryGetHost(settings.WebDavUrl) ?? settings.Username}";
        _viewModel.HeaderSubtitle = !string.IsNullOrWhiteSpace(settings.Username)
            ? settings.Username
            : settings.SyncRootPath;

        var stats = _dashboardContext.Database.GetStatistics();
        if (forceHealthCheck || !_healthSnapshot.CheckedAt.HasValue ||
            (DateTime.Now - _healthSnapshot.CheckedAt.Value) > TimeSpan.FromSeconds(30))
        {
            _healthSnapshot = await CreateHealthSnapshotAsync();
        }

        ReplaceCollection(
            _viewModel.StatusBanners,
            DashboardBuilder.BuildStatusBanners(
                _dashboardContext.SyncStateProvider(),
                _dashboardContext.IsPausedProvider(),
                stats,
                _healthSnapshot));

        ReplaceCollection(
            _viewModel.Metrics,
            DashboardBuilder.BuildFlyoutMetrics(
                _dashboardContext.SyncStateProvider(),
                stats,
                _healthSnapshot,
                _dashboardContext.StartedAt));

        var openProblems = _dashboardContext.Database.GetProblems(openOnly: true);
        _allProblems = DashboardBuilder.BuildProblemItems(
            openProblems,
            settings,
            take: 3);
        ReplaceCollection(_viewModel.ProblemItems, _allProblems);
        _viewModel.HasProblems = _allProblems.Count > 0;
        _viewModel.ShowViewAllProblems = openProblems.Count > _allProblems.Count;

        _allActivities = DashboardBuilder.BuildActivityItems(_activityTracker?.Entries ?? [], settings);
        _visibleActivityCount = Math.Clamp(_visibleActivityCount, 20, Math.Max(20, _allActivities.Count));
        RebuildActivityGroups();
        Log.Debug(
            "UI[Flyout] Refresh complete. Banners={BannerCount}, Metrics={MetricCount}, Problems={ProblemCount}, Activities={ActivityCount}",
            _viewModel.StatusBanners.Count,
            _viewModel.Metrics.Count,
            _viewModel.ProblemItems.Count,
            _allActivities.Count);
    }

    private async Task<HealthSnapshot> CreateHealthSnapshotAsync()
    {
        var webDav = _dashboardContext.WebDavProvider();
        if (webDav == null)
        {
            return new HealthSnapshot
            {
                State = DashboardHealthState.Unknown,
                IsConfigured = false,
                CheckedAt = DateTime.Now,
                ErrorMessage = null
            };
        }

        try
        {
            var result = await webDav.HealthCheckAsync();
            return new HealthSnapshot
            {
                State = result.IsHealthy
                    ? DashboardHealthState.Healthy
                    : result.FailureReason == CloudDrive.Core.WebDav.HealthCheckFailure.HighLatency
                        ? DashboardHealthState.Warning
                        : DashboardHealthState.Error,
                IsConfigured = true,
                FailureReason = result.IsHealthy ? null : result.FailureReason,
                LatencyMs = result.LatencyMs,
                CheckedAt = DateTime.Now,
                ErrorMessage = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            return new HealthSnapshot
            {
                State = DashboardHealthState.Error,
                IsConfigured = true,
                CheckedAt = DateTime.Now,
                ErrorMessage = ex.Message
            };
        }
    }

    private void RebuildActivityGroups()
    {
        var visibleItems = _allActivities.Take(_visibleActivityCount).ToList();
        ReplaceCollection(_viewModel.ActivityGroups, DashboardBuilder.GroupActivities(visibleItems));
        _viewModel.HasActivity = visibleItems.Count > 0;
        _viewModel.ShowViewAllActivity = _allActivities.Count >= 100;
        Log.Debug(
            "UI[Flyout] Activity groups rebuilt. VisibleItems={VisibleItems}, Groups={GroupCount}, ShowViewAll={ShowViewAll}",
            visibleItems.Count,
            _viewModel.ActivityGroups.Count,
            _viewModel.ShowViewAllActivity);
    }

    private void PositionFlyout()
    {
        MaxHeight = SystemParameters.WorkArea.Height * 0.80;
        Height = Math.Min(MaxHeight, 640);
        UpdateLayout();

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        var source = HwndSource.FromHwnd(helper.Handle);
        if (source?.CompositionTarget == null)
        {
            Log.Warning("UI[Flyout] Could not get composition target for DPI conversion. Falling back to raw coordinates.");
            PositionFlyoutWithoutDpiConversion();
            return;
        }

        var transformFromDevice = source.CompositionTarget.TransformFromDevice;
        var transformToDevice = source.CompositionTarget.TransformToDevice;

        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var workingArea = screen.WorkingArea;
        var screenBounds = screen.Bounds;
        const double margin = 8;

        _flyoutEdge = DetectTaskbarEdge(workingArea, screenBounds, cursor);

        var sizePx = transformToDevice.Transform(new Point(Width, Height));
        var widthPx = sizePx.X;
        var heightPx = sizePx.Y;
        var marginPx = transformToDevice.Transform(new Point(margin, margin)).X;

        double leftPx;
        double topPx;

        switch (_flyoutEdge)
        {
            case FlyoutEdge.Top:
                leftPx = Clamp(cursor.X - widthPx / 2, workingArea.Left + marginPx, workingArea.Right - widthPx - marginPx);
                topPx = workingArea.Top + marginPx;
                break;
            case FlyoutEdge.Left:
                leftPx = workingArea.Left + marginPx;
                topPx = Clamp(cursor.Y - heightPx / 2, workingArea.Top + marginPx, workingArea.Bottom - heightPx - marginPx);
                break;
            case FlyoutEdge.Right:
                leftPx = workingArea.Right - widthPx - marginPx;
                topPx = Clamp(cursor.Y - heightPx / 2, workingArea.Top + marginPx, workingArea.Bottom - heightPx - marginPx);
                break;
            default:
                leftPx = Clamp(cursor.X - widthPx / 2, workingArea.Left + marginPx, workingArea.Right - widthPx - marginPx);
                topPx = workingArea.Bottom - heightPx - marginPx;
                break;
        }

        var leftTopDip = transformFromDevice.Transform(new Point(leftPx, topPx));
        Left = leftTopDip.X;
        Top = leftTopDip.Y;

        Log.Information(
            "UI[Flyout] DPI-aware position computed. CursorPx=({CursorX},{CursorY}) SizeDip=({WidthDip},{HeightDip}) SizePx=({WidthPx},{HeightPx}) LeftPx={LeftPx} TopPx={TopPx} LeftDip={LeftDip} TopDip={TopDip} Edge={Edge}",
            cursor.X,
            cursor.Y,
            Width,
            Height,
            widthPx,
            heightPx,
            leftPx,
            topPx,
            Left,
            Top,
            _flyoutEdge);
    }

    private void PositionFlyoutWithoutDpiConversion()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var workingArea = screen.WorkingArea;
        var screenBounds = screen.Bounds;
        const double margin = 8;

        _flyoutEdge = DetectTaskbarEdge(workingArea, screenBounds, cursor);

        switch (_flyoutEdge)
        {
            case FlyoutEdge.Top:
                Left = Clamp(cursor.X - Width / 2, workingArea.Left + margin, workingArea.Right - Width - margin);
                Top = workingArea.Top + margin;
                break;
            case FlyoutEdge.Left:
                Left = workingArea.Left + margin;
                Top = Clamp(cursor.Y - Height / 2, workingArea.Top + margin, workingArea.Bottom - Height - margin);
                break;
            case FlyoutEdge.Right:
                Left = workingArea.Right - Width - margin;
                Top = Clamp(cursor.Y - Height / 2, workingArea.Top + margin, workingArea.Bottom - Height - margin);
                break;
            default:
                Left = Clamp(cursor.X - Width / 2, workingArea.Left + margin, workingArea.Right - Width - margin);
                Top = workingArea.Bottom - Height - margin;
                break;
        }
    }

    private static FlyoutEdge DetectTaskbarEdge(System.Drawing.Rectangle workingArea, System.Drawing.Rectangle bounds, System.Drawing.Point cursor)
    {
        if (workingArea.Bottom < bounds.Bottom || cursor.Y >= workingArea.Bottom)
            return FlyoutEdge.Bottom;
        if (workingArea.Top > bounds.Top || cursor.Y <= workingArea.Top)
            return FlyoutEdge.Top;
        if (workingArea.Left > bounds.Left || cursor.X <= workingArea.Left)
            return FlyoutEdge.Left;
        if (workingArea.Right < bounds.Right || cursor.X >= workingArea.Right)
            return FlyoutEdge.Right;
        return FlyoutEdge.Bottom;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("UI[Flyout] Settings button clicked");
        HideFlyout();
        _dashboardContext.OpenSettings(SettingsSection.General);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("UI[Flyout] Close button clicked");
        HideFlyout();
    }

    private void StatusBanner_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DashboardStatusBanner banner)
        {
            ExecuteBannerAction(banner.ActionId);
        }
    }

    private void StatusBannerAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is DashboardStatusBanner banner)
        {
            ExecuteBannerAction(banner.ActionId);
        }
    }

    private void ExecuteBannerAction(string actionId)
    {
        Log.Information("UI[Flyout] Banner action triggered: {ActionId}", actionId);
        if (actionId == "toggle-pause")
        {
            _dashboardContext.TogglePauseResume();
            _ = RefreshAsync(forceHealthCheck: true);
            return;
        }

        if (Enum.TryParse<SettingsSection>(actionId, out var section))
        {
            HideFlyout();
            _dashboardContext.OpenSettings(section);
        }
    }

    private void MetricCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DashboardMetricCard metric)
        {
            HideFlyout();
            _dashboardContext.OpenSettings(metric.TargetSection);
        }
    }

    private async void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not QuickActionItem item)
            return;

        Log.Information("UI[Flyout] Quick action clicked: {ActionId}", item.Id);
        switch (item.Id)
        {
            case "open-folder":
                _dashboardContext.OpenFolder();
                break;
            case "open-activity-log":
                HideFlyout();
                _dashboardContext.OpenSettings(SettingsSection.Activity);
                break;
            case "statistics":
                HideFlyout();
                _dashboardContext.OpenSettings(SettingsSection.Statistics);
                break;
            case "sync-now":
                if (_dashboardContext.SyncNowAsync != null)
                {
                    await _dashboardContext.SyncNowAsync();
                    await RefreshAsync(forceHealthCheck: true);
                }
                break;
        }
    }

    private void ActivityItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityDisplayItem item)
            return;

        var path = item.OpenPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (item.OpenPathIsFolder || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = path,
                    UseShellExecute = true
                });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
        }
    }

    private void LocationLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not ActivityDisplayItem item || string.IsNullOrWhiteSpace(item.FolderPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = item.FolderPath,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void ViewAllActivity_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("UI[Flyout] View all activity clicked");
        HideFlyout();
        _dashboardContext.OpenSettings(SettingsSection.Activity);
    }

    private async void ProblemAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if ((sender as FrameworkElement)?.Tag is not ProblemDisplayAction action)
            return;

        await ExecuteProblemActionAsync(action);
    }

    private void ProblemLocationLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if ((sender as FrameworkElement)?.Tag is ProblemDisplayItem item &&
            !string.IsNullOrWhiteSpace(item.FolderPath))
        {
            OpenWithShell(item.FolderPath);
        }
    }

    private void ViewAllProblems_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _dashboardContext.OpenSettings(SettingsSection.Problems);
    }

    private void ActivityScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 40)
            return;

        if (_visibleActivityCount >= Math.Min(100, _allActivities.Count))
            return;

        _visibleActivityCount = Math.Min(_visibleActivityCount + 20, Math.Min(100, _allActivities.Count));
        RebuildActivityGroups();
        Log.Information("UI[Flyout] Infinite scroll loaded more items. VisibleCount={VisibleCount}", _visibleActivityCount);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Log.Information("UI[Flyout] Escape pressed");
            e.Handled = true;
            HideFlyout();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideFlyout();
            return;
        }

        _refreshTimer.Stop();
        ThemeManager.ThemeChanged -= _themeChangedHandler;
        AppLocalizer.Instance.CultureChanged -= OnCultureChanged;
        base.OnClosing(e);
    }

    private async Task ExecuteProblemActionAsync(ProblemDisplayAction action)
    {
        switch (action.Kind)
        {
            case ProblemActionKind.OpenPath:
            case ProblemActionKind.OpenFolder:
                if (!string.IsNullOrWhiteSpace(action.Path))
                    OpenWithShell(action.Path);
                break;

            case ProblemActionKind.SyncNow:
                if (_dashboardContext.SyncNowAsync != null)
                {
                    await _dashboardContext.SyncNowAsync();
                    await RefreshAsync(forceHealthCheck: true);
                }
                break;

            case ProblemActionKind.OpenSettings:
                if (action.Section.HasValue)
                {
                    HideFlyout();
                    _dashboardContext.OpenSettings(action.Section.Value);
                }
                break;

            case ProblemActionKind.ConfirmRemoteDelete:
                if (action.ProblemId.HasValue &&
                    !string.IsNullOrWhiteSpace(action.Path) &&
                    !string.IsNullOrWhiteSpace(action.RemotePath) &&
                    _dashboardContext.ConfirmRemoteDeleteAsync != null)
                {
                    await _dashboardContext.ConfirmRemoteDeleteAsync(action.ProblemId.Value, action.Path, action.RemotePath);
                    await RefreshAsync();
                }
                break;

            case ProblemActionKind.KeepRemoteCopy:
                if (action.ProblemId.HasValue &&
                    !string.IsNullOrWhiteSpace(action.Path) &&
                    !string.IsNullOrWhiteSpace(action.RemotePath) &&
                    _dashboardContext.KeepRemoteCopyAsync != null)
                {
                    await _dashboardContext.KeepRemoteCopyAsync(action.ProblemId.Value, action.Path, action.RemotePath);
                    await RefreshAsync();
                }
                break;

            case ProblemActionKind.Dismiss:
                if (action.ProblemId.HasValue)
                {
                    _dashboardContext.Database.ResolveProblem(action.ProblemId.Value);
                    await RefreshAsync();
                }
                break;
        }
    }

    private static void OpenWithShell(string path)
    {
        try
        {
            var absolute = Path.GetFullPath(path);
            if (!File.Exists(absolute) && !Directory.Exists(absolute))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = absolute,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private enum FlyoutEdge
    {
        Bottom,
        Top,
        Left,
        Right
    }
}

public partial class ActivityPanelViewModel : ObservableObject
{
    public ObservableCollection<DashboardStatusBanner> StatusBanners { get; } = new();
    public ObservableCollection<DashboardMetricCard> Metrics { get; } = new();
    public ObservableCollection<ProblemDisplayItem> ProblemItems { get; } = new();
    public ObservableCollection<ActivityDayGroup> ActivityGroups { get; } = new();
    public ObservableCollection<QuickActionItem> QuickActions { get; } = new();

    [ObservableProperty] private string _headerTitle = AppLocalizer.Instance.GetString("App_Name");
    [ObservableProperty] private string _headerSubtitle = string.Empty;
    [ObservableProperty] private bool _hasProblems;
    [ObservableProperty] private bool _hasActivity;
    [ObservableProperty] private bool _showViewAllProblems;
    [ObservableProperty] private bool _showViewAllActivity;

    public bool HasNoActivity => !HasActivity;
    public bool HasAnyContent => HasProblems || HasActivity;
    public bool HasNoContent => !HasAnyContent;

    partial void OnHasActivityChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoActivity));
        OnPropertyChanged(nameof(HasAnyContent));
        OnPropertyChanged(nameof(HasNoContent));
    }

    partial void OnHasProblemsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAnyContent));
        OnPropertyChanged(nameof(HasNoContent));
    }
}

public sealed record QuickActionItem(string Id, string Glyph, string Label);
