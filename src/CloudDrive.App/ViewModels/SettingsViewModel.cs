using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CloudDrive.App.Services;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncRoot;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.App.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly AppDashboardContext _dashboardContext;
    private readonly IAutoStartRegistration _autoStartRegistration;
    private readonly Func<Task>? _resetCallback;
    private readonly Func<Task>? _resetConfigurationCallback;
    private readonly ActivityTracker? _activityTracker;
    private IReadOnlyList<ActivityDisplayItem> _allActivityItems = [];
    private IReadOnlyList<ProblemDisplayItem> _allProblemItems = [];
    private HealthSnapshot _healthSnapshot = new();
    private WebDavLockSupport _webDavLockSupport = WebDavLockSupport.NotChecked();
    private UpdateStatusSnapshot _updateStatus = UpdateStatusSnapshot.CreateUnknown(UpdateService.ReleasesPageUrl, "unknown");
    private DatabaseStatistics? _databaseStatistics;
    private int _currentPage = 1;
    private const int ActivityPageSize = 25;

    [ObservableProperty] private string _webDavUrl = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _syncRootPath = string.Empty;
    [ObservableProperty] private int _syncIntervalSeconds = 300;
    [ObservableProperty] private int _maxConcurrentTransfers = 4;
    [ObservableProperty] private AuthType _authType = AuthType.Basic;
    [ObservableProperty] private bool _enableFileLogging;
    [ObservableProperty] private bool _showNotifications = true;
    [ObservableProperty] private bool _launchOnStartup = true;
    [ObservableProperty] private string _themeMode = "System";
    [ObservableProperty] private string _language = AppLanguage.System;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _hasStoredPassword;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _windowTitle = AppLocalizer.Instance.GetString("App_ControlPanel");
    [ObservableProperty] private string _storageSummary = string.Empty;
    [ObservableProperty] private string _storageSuggestion = string.Empty;
    [ObservableProperty] private string _healthLastChecked = AppLocalizer.Instance.GetString("Common_Never");
    [ObservableProperty] private string _problemSummary = string.Empty;
    [ObservableProperty] private SettingsNavigationItem? _selectedNavigationItem;
    private bool _isDisposed;

    public SettingsViewModel(
        ILoggerFactory loggerFactory,
        AppDashboardContext dashboardContext,
        IAutoStartRegistration? autoStartRegistration = null,
        Func<Task>? resetCallback = null,
        Func<Task>? resetConfigurationCallback = null)
    {
        _loggerFactory = loggerFactory;
        _dashboardContext = dashboardContext;
        _autoStartRegistration = autoStartRegistration ?? new WindowsAutoStartRegistration();
        _resetCallback = resetCallback;
        _resetConfigurationCallback = resetConfigurationCallback;
        _activityTracker = dashboardContext.ActivityTracker as ActivityTracker;

        NavigationItems = new ObservableCollection<SettingsNavigationItem>();
        StatisticsCards = new ObservableCollection<DashboardMetricCard>();
        StatusChartItems = new ObservableCollection<SimpleChartBar>();
        ActivityChartItems = new ObservableCollection<SimpleChartBar>();
        HealthChecks = new ObservableCollection<DashboardHealthCheck>();
        ProblemItems = new ObservableCollection<ProblemDisplayItem>();
        ActivityGroups = new ObservableCollection<ActivityDayGroup>();
        ActivityPageNumbers = new ObservableCollection<int>();
        ActionFilters = new ObservableCollection<FilterOption<ActivityActionKind>>();
        FileTypeFilters = new ObservableCollection<FilterOption<ActivityFileTypeFilter>>();
        RangeFilters = new ObservableCollection<FilterOption<StatisticsRange>>();
        ThemeOptions = new ObservableCollection<SelectionOption>();
        LanguageOptions = new ObservableCollection<SelectionOption>();

        LoadSettings();
        RebuildLocalizedCollections();
        NavigateTo(SettingsSection.General);
        AppLocalizer.Instance.CultureChanged += OnCultureChanged;
    }

    public ObservableCollection<SettingsNavigationItem> NavigationItems { get; }
    public ObservableCollection<DashboardMetricCard> StatisticsCards { get; }
    public ObservableCollection<SimpleChartBar> StatusChartItems { get; }
    public ObservableCollection<SimpleChartBar> ActivityChartItems { get; }
    public ObservableCollection<DashboardHealthCheck> HealthChecks { get; }
    public ObservableCollection<ProblemDisplayItem> ProblemItems { get; }
    public ObservableCollection<ActivityDayGroup> ActivityGroups { get; }
    public ObservableCollection<int> ActivityPageNumbers { get; }
    public ObservableCollection<FilterOption<ActivityActionKind>> ActionFilters { get; }
    public ObservableCollection<FilterOption<ActivityFileTypeFilter>> FileTypeFilters { get; }
    public ObservableCollection<FilterOption<StatisticsRange>> RangeFilters { get; }
    public ObservableCollection<SelectionOption> ThemeOptions { get; }
    public ObservableCollection<SelectionOption> LanguageOptions { get; }

    public DatabaseStatistics? DatabaseStatistics
    {
        get => _databaseStatistics;
        private set => SetProperty(ref _databaseStatistics, value);
    }

    public SettingsSection SelectedSection => SelectedNavigationItem?.Section ?? SettingsSection.General;
    public string VersionText => BuildVersionText();
    public string AccountDisplay => !string.IsNullOrWhiteSpace(Username)
        ? Username
        : TryGetHostFromUrl(WebDavUrl) ?? AppLocalizer.Instance.GetString("Settings_AccountDisplay_None");
    public string SyncRootStatus => Directory.Exists(SyncRootPath)
        ? AppLocalizer.Instance.GetString("Common_Connected")
        : AppLocalizer.Instance.GetString("Common_Missing");
    public string LogPathDisplay => Path.Combine(AppSettings.GetDataDirectory(), "logs");
    public string DataDirectoryDisplay => AppSettings.GetDataDirectory();
    public string CurrentHealthStatus => DashboardBuilder.DescribeHealthStatusForDisplay(_healthSnapshot);
    public string CurrentHealthDetail => DashboardBuilder.DescribeHealthDetailForDisplay(_healthSnapshot);
    public bool HasProblems => ProblemItems.Count > 0;
    public bool HasNoProblems => !HasProblems;
    public bool CanGoPreviousPage => _currentPage > 1;
    public bool CanGoNextPage => _currentPage < TotalPages;
    public int CurrentPage => _currentPage;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(GetFilteredActivities().Count / (double)ActivityPageSize));

    partial void OnSelectedNavigationItemChanged(SettingsNavigationItem? value)
    {
        foreach (var item in NavigationItems)
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        WindowTitle = value == null
            ? AppLocalizer.Instance.GetString("App_ControlPanel")
            : AppLocalizer.Instance.Format("App_ControlPanel_Section", value.Title);
        OnPropertyChanged(nameof(SelectedSection));
    }

    partial void OnSearchTextChanged(string value)
    {
        _currentPage = 1;
        RebuildActivityPage();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (_isDisposed)
            return;

        RebuildLocalizedCollections();
        DatabaseStatistics = _dashboardContext.Database.GetStatistics();
        var settings = _dashboardContext.SettingsProvider();
        _allActivityItems = DashboardBuilder.BuildActivityItems(_activityTracker?.Entries ?? [], settings);
        _allProblemItems = DashboardBuilder.BuildProblemItems(
            _dashboardContext.Database.GetProblems(openOnly: true),
            settings);
        HealthLastChecked = BuildHealthLastCheckedText();
        _updateStatus = _dashboardContext.UpdateStatusProvider();
        RebuildStatistics();
        RebuildHealthChecks();
        RebuildProblems();
        RebuildActivityPage();
        UpdateStorageSummary();
        UpdateNavigationBadges();
        OnPropertyChanged(nameof(AccountDisplay));
        OnPropertyChanged(nameof(SyncRootStatus));
        OnPropertyChanged(nameof(CurrentHealthStatus));
        OnPropertyChanged(nameof(CurrentHealthDetail));
        OnPropertyChanged(nameof(VersionText));
    }

    private void RebuildLocalizedCollections()
    {
        var selectedSection = SelectedNavigationItem?.Section ?? SettingsSection.General;
        var selectedAction = ActionFilters.FirstOrDefault(option => option.IsSelected)?.Value ?? ActivityActionKind.All;
        var selectedFileType = FileTypeFilters.FirstOrDefault(option => option.IsSelected)?.Value ?? ActivityFileTypeFilter.All;
        var selectedRange = RangeFilters.FirstOrDefault(option => option.IsSelected)?.Value ?? StatisticsRange.Days7;

        ReplaceCollection(NavigationItems, DashboardBuilder.CreateNavigation());
        ReplaceCollection(ActionFilters, BuildActionFilters());
        ReplaceCollection(FileTypeFilters, BuildFileTypeFilters());
        ReplaceCollection(RangeFilters, BuildRangeFilters());
        ReplaceCollection(ThemeOptions, AppSettingsOptionFactory.BuildThemeOptions());
        ReplaceCollection(LanguageOptions, AppSettingsOptionFactory.BuildLanguageOptions());

        SelectFilter(ActionFilters, selectedAction);
        SelectFilter(FileTypeFilters, selectedFileType);
        SelectFilter(RangeFilters, selectedRange);
        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.Section == selectedSection)
            ?? NavigationItems.FirstOrDefault(item => item.Section == SettingsSection.General);
    }

    private string BuildHealthLastCheckedText()
    {
        return _healthSnapshot.CheckedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            ?? AppLocalizer.Instance.GetString("Common_Never");
    }

    private static IReadOnlyList<FilterOption<ActivityActionKind>> BuildActionFilters()
    {
        return
        [
            new(ActivityActionKind.All, AppLocalizer.Instance.GetString("Filter_Action_All")),
            new(ActivityActionKind.Uploaded, AppLocalizer.Instance.GetString("Filter_Action_Uploaded")),
            new(ActivityActionKind.Downloaded, AppLocalizer.Instance.GetString("Filter_Action_Downloaded")),
            new(ActivityActionKind.Modified, AppLocalizer.Instance.GetString("Filter_Action_Modified")),
            new(ActivityActionKind.Deleted, AppLocalizer.Instance.GetString("Filter_Action_Deleted")),
            new(ActivityActionKind.Synced, AppLocalizer.Instance.GetString("Filter_Action_Synced")),
            new(ActivityActionKind.Error, AppLocalizer.Instance.GetString("Filter_Action_Errors"))
        ];
    }

    private static IReadOnlyList<FilterOption<ActivityFileTypeFilter>> BuildFileTypeFilters()
    {
        return
        [
            new(ActivityFileTypeFilter.All, AppLocalizer.Instance.GetString("Filter_FileType_All")),
            new(ActivityFileTypeFilter.Pdf, AppLocalizer.Instance.GetString("Filter_FileType_Pdf")),
            new(ActivityFileTypeFilter.Spreadsheet, AppLocalizer.Instance.GetString("Filter_FileType_Sheets")),
            new(ActivityFileTypeFilter.Document, AppLocalizer.Instance.GetString("Filter_FileType_Docs")),
            new(ActivityFileTypeFilter.Image, AppLocalizer.Instance.GetString("Filter_FileType_Images")),
            new(ActivityFileTypeFilter.Archive, AppLocalizer.Instance.GetString("Filter_FileType_Archives")),
            new(ActivityFileTypeFilter.Text, AppLocalizer.Instance.GetString("Filter_FileType_Text")),
            new(ActivityFileTypeFilter.Other, AppLocalizer.Instance.GetString("Filter_FileType_Other"))
        ];
    }

    private static IReadOnlyList<FilterOption<StatisticsRange>> BuildRangeFilters()
    {
        return
        [
            new(StatisticsRange.Hours24, "24h"),
            new(StatisticsRange.Days7, "7d"),
            new(StatisticsRange.Days30, "30d"),
            new(StatisticsRange.Days90, "90d")
        ];
    }

    private void LoadSettings()
    {
        var settings = _dashboardContext.SettingsProvider();
        WebDavUrl = settings.WebDavUrl;
        Username = settings.Username;
        SyncRootPath = settings.SyncRootPath;
        SyncIntervalSeconds = settings.SyncIntervalSeconds;
        MaxConcurrentTransfers = settings.MaxConcurrentTransfers;
        AuthType = AuthType.Basic;
        EnableFileLogging = settings.EnableFileLogging;
        ShowNotifications = settings.ShowNotifications;
        LaunchOnStartup = settings.LaunchOnStartup;
        ThemeMode = settings.ThemeMode;
        Language = AppLanguage.NormalizeSetting(settings.Language);
        HasStoredPassword = CredentialManager.HasPassword();
        _webDavLockSupport = _dashboardContext.WebDavLockSupportProvider();
        OnPropertyChanged(nameof(AccountDisplay));
        OnPropertyChanged(nameof(SyncRootStatus));
    }

    public async Task RefreshAsync(bool forceHealthCheck = false)
    {
        if (_isDisposed)
            return;

        _dashboardContext.Database.EnsureProblemEntriesForTrackedItemStates();
        DatabaseStatistics = _dashboardContext.Database.GetStatistics();

        if (forceHealthCheck || !_healthSnapshot.CheckedAt.HasValue ||
            (DateTime.Now - _healthSnapshot.CheckedAt.Value) > TimeSpan.FromSeconds(30))
        {
            _healthSnapshot = await CreateHealthSnapshotAsync();
            HealthLastChecked = BuildHealthLastCheckedText();
            OnPropertyChanged(nameof(CurrentHealthStatus));
            OnPropertyChanged(nameof(CurrentHealthDetail));
        }

        _updateStatus = _dashboardContext.UpdateStatusProvider();
        _webDavLockSupport = forceHealthCheck && _dashboardContext.RefreshWebDavLockSupportAsync != null
            ? await _dashboardContext.RefreshWebDavLockSupportAsync()
            : _dashboardContext.WebDavLockSupportProvider();

        var settings = _dashboardContext.SettingsProvider();
        var entries = _activityTracker?.Entries ?? [];
        _allActivityItems = DashboardBuilder.BuildActivityItems(entries, settings);
        _allProblemItems = DashboardBuilder.BuildProblemItems(
            _dashboardContext.Database.GetProblems(openOnly: true),
            settings);

        RebuildStatistics();
        RebuildHealthChecks();
        RebuildProblems();
        RebuildActivityPage();
        UpdateStorageSummary();
        UpdateNavigationBadges();
    }

    public void NavigateTo(SettingsSection section)
    {
        if (section == SettingsSection.Storage)
        {
            section = SettingsSection.Statistics;
        }

        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.Section == section);
    }

    public void RefreshThemeState()
    {
        foreach (var item in NavigationItems)
        {
            item.RefreshThemeState();
        }
    }

    public void SelectActionFilter(ActivityActionKind actionKind)
    {
        SelectFilter(ActionFilters, actionKind);
        _currentPage = 1;
        RebuildActivityPage();
    }

    public void SelectFileTypeFilter(ActivityFileTypeFilter fileType)
    {
        SelectFilter(FileTypeFilters, fileType);
        _currentPage = 1;
        RebuildActivityPage();
    }

    public void SelectStatisticsRange(StatisticsRange range)
    {
        SelectFilter(RangeFilters, range);
        RebuildStatistics();
    }

    public void GoToPage(int page)
    {
        _currentPage = Math.Clamp(page, 1, TotalPages);
        RebuildActivityPage();
    }

    public void GoToPreviousPage()
    {
        if (CanGoPreviousPage)
        {
            _currentPage--;
            RebuildActivityPage();
        }
    }

    public void GoToNextPage()
    {
        if (CanGoNextPage)
        {
            _currentPage++;
            RebuildActivityPage();
        }
    }

    public async Task RunAllChecksAsync()
    {
        await RefreshAsync(forceHealthCheck: true);
        StatusMessage = AppLocalizer.Instance.GetString("Settings_Status_HealthChecksUpdated");
    }

    public async Task ExecuteHealthActionAsync(string actionId)
    {
        switch (actionId)
        {
            case "recheck-health":
                await RefreshAsync(forceHealthCheck: true);
                break;
            case "check-updates":
                if (_dashboardContext.CheckForUpdatesNowAsync != null)
                    await _dashboardContext.CheckForUpdatesNowAsync();

                await RefreshAsync();
                break;
            case "install-update":
                _dashboardContext.ApplyUpdateAndRestart();
                break;
            case "open-folder":
                _dashboardContext.OpenFolder();
                break;
            default:
                if (Enum.TryParse<SettingsSection>(actionId, out var section))
                {
                    NavigateTo(section);
                }
                break;
        }
    }

    public async Task DismissProblemAsync(long problemId)
    {
        _dashboardContext.Database.ResolveProblem(problemId);
        await RefreshAsync();
    }

    public async Task TriggerSyncNowAsync()
    {
        if (_dashboardContext.SyncNowAsync != null)
        {
            await _dashboardContext.SyncNowAsync();
        }

        await RefreshAsync(forceHealthCheck: true);
    }

    public string BuildActivityCsv()
    {
        return DashboardBuilder.BuildCsv(GetFilteredActivities());
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
            var state = result.IsHealthy
                ? DashboardHealthState.Healthy
                : result.FailureReason == HealthCheckFailure.HighLatency
                    ? DashboardHealthState.Warning
                    : DashboardHealthState.Error;

            return new HealthSnapshot
            {
                State = state,
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

    private void RebuildStatistics()
    {
        var statisticsCards = DashboardBuilder.BuildStatisticsCards(DatabaseStatistics, _allActivityItems);
        var statusChartItems = DashboardBuilder.BuildStatusChart(DatabaseStatistics);
        var activityChartItems = DashboardBuilder.BuildActivityChart(GetStatisticsRangeItems());

        ReplaceCollection(StatisticsCards, statisticsCards);
        ReplaceCollection(StatusChartItems, statusChartItems);
        ReplaceCollection(ActivityChartItems, activityChartItems);
    }

    private void RebuildHealthChecks()
    {
        ReplaceCollection(HealthChecks, DashboardBuilder.BuildHealthChecks(
            _dashboardContext.SettingsProvider(),
            DatabaseStatistics,
            _healthSnapshot,
            _webDavLockSupport,
            _updateStatus,
            _dashboardContext.StartedAt));
    }

    private void RebuildProblems()
    {
        ReplaceCollection(ProblemItems, _allProblemItems);
        ProblemSummary = ProblemItems.Count == 0
            ? AppLocalizer.Instance.GetString("Settings_ProblemSummary_None")
            : AppLocalizer.Instance.Format("Settings_ProblemSummary_Count", ProblemItems.Count);
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasNoProblems));
    }

    private void RebuildActivityPage()
    {
        _currentPage = Math.Clamp(_currentPage, 1, TotalPages);
        var filtered = GetFilteredActivities();
        var pageItems = filtered
            .Skip((_currentPage - 1) * ActivityPageSize)
            .Take(ActivityPageSize)
            .ToList();

        ReplaceCollection(ActivityGroups, DashboardBuilder.GroupActivities(pageItems));

        ActivityPageNumbers.Clear();
        for (var page = 1; page <= TotalPages; page++)
        {
            ActivityPageNumbers.Add(page);
        }

        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
    }

    private IReadOnlyList<ActivityDisplayItem> GetFilteredActivities()
    {
        return DashboardBuilder.FilterActivities(
            _allActivityItems,
            SearchText,
            ActionFilters.First(option => option.IsSelected).Value,
            FileTypeFilters.First(option => option.IsSelected).Value);
    }

    private IReadOnlyList<ActivityDisplayItem> GetStatisticsRangeItems()
    {
        var range = RangeFilters.First(option => option.IsSelected).Value;
        var duration = range switch
        {
            StatisticsRange.Hours24 => TimeSpan.FromHours(24),
            StatisticsRange.Days7 => TimeSpan.FromDays(7),
            StatisticsRange.Days30 => TimeSpan.FromDays(30),
            StatisticsRange.Days90 => TimeSpan.FromDays(90),
            _ => TimeSpan.FromDays(7)
        };

        return DashboardBuilder.FilterActivities(
            _allActivityItems,
            null,
            ActivityActionKind.All,
            ActivityFileTypeFilter.All,
            duration);
    }

    private void UpdateStorageSummary()
    {
        StorageSummary = DatabaseStatistics == null
            ? AppLocalizer.Instance.GetString("Settings_StorageSummary_None")
            : AppLocalizer.Instance.Format("Settings_StorageSummary_Count", DatabaseStatistics.TotalTrackedDataDisplay, DatabaseStatistics.TotalItems);

        StorageSuggestion = DatabaseStatistics == null
            ? AppLocalizer.Instance.GetString("Settings_StorageSuggestion_VerifySyncRoot")
            : _allProblemItems.Count > 0
                ? AppLocalizer.Instance.GetString("Settings_StorageSuggestion_ReviewProblems")
                : DatabaseStatistics.ErrorItems > 0
                ? AppLocalizer.Instance.GetString("Settings_StorageSuggestion_ResolveErrors")
                : DatabaseStatistics.PendingItems > 0
                    ? AppLocalizer.Instance.GetString("Settings_StorageSuggestion_Pending")
                    : AppLocalizer.Instance.GetString("Settings_StorageSuggestion_Healthy");
    }

    private void UpdateNavigationBadges()
    {
        foreach (var item in NavigationItems)
        {
            item.BadgeCount = item.Section switch
            {
                SettingsSection.Activity => null,
                SettingsSection.Problems => _allProblemItems.Count > 0 ? _allProblemItems.Count : null,
                SettingsSection.HealthCheck => DatabaseStatistics?.OpenProblemCount > 0 ? (int)DatabaseStatistics.OpenProblemCount : null,
                SettingsSection.Statistics => DatabaseStatistics?.PendingItems > 0 ? (int)DatabaseStatistics.PendingItems : null,
                _ => null
            };
        }
    }

    private static void SelectFilter<T>(IEnumerable<FilterOption<T>> options, T selectedValue) where T : struct
    {
        foreach (var option in options)
        {
            option.IsSelected = EqualityComparer<T>.Default.Equals(option.Value, selectedValue);
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

    private static string BuildVersionText()
    {
        var version = typeof(SettingsViewModel).Assembly.GetName().Version;
        if (version == null)
            return AppLocalizer.Instance.GetString("App_Version_Default");

        return AppLocalizer.Instance.Format("App_Version_Format", version.Major, version.Minor, version.Build, version.Revision);
    }

    private static string? TryGetHostFromUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    [RelayCommand]
    private async Task Save(string? password)
    {
        var settings = _dashboardContext.SettingsProvider();
        settings.WebDavUrl = WebDavUrl;
        settings.Username = Username;
        settings.SyncRootPath = SyncRootPath;
        settings.SyncIntervalSeconds = SyncIntervalSeconds;
        settings.MaxConcurrentTransfers = MaxConcurrentTransfers;
        settings.AuthType = AuthType.Basic;
        AuthType = AuthType.Basic;
        settings.EnableFileLogging = EnableFileLogging;
        settings.ShowNotifications = ShowNotifications;
        settings.LaunchOnStartup = LaunchOnStartup;
        settings.ThemeMode = ThemeMode;
        settings.Language = Language;
        settings.Save();
        _autoStartRegistration.Apply(settings.LaunchOnStartup);
        ThemeManager.ApplyTheme(ThemeMode);
        AppLocalizer.Instance.SetLanguage(Language);
        RefreshThemeState();

        if (!string.IsNullOrEmpty(password))
            CredentialManager.SavePassword(password);

        HasStoredPassword = CredentialManager.HasPassword();

        if (_dashboardContext.EnsureWatchdogScheduledTaskAsync != null)
            await _dashboardContext.EnsureWatchdogScheduledTaskAsync();

        StatusMessage = AppLocalizer.Instance.GetString("Settings_Status_SettingsSaved");
        OnPropertyChanged(nameof(AccountDisplay));
        OnPropertyChanged(nameof(SyncRootStatus));
    }

    [RelayCommand]
    private async Task TestConnectionAsync(string? password)
    {
        if (string.IsNullOrEmpty(WebDavUrl))
        {
            StatusMessage = AppLocalizer.Instance.GetString("Settings_Status_EnterWebDavUrl");
            return;
        }

        IsTesting = true;
        StatusMessage = AppLocalizer.Instance.GetString("Settings_Status_TestingConnection");

        try
        {
            var settings = new AppSettings
            {
                WebDavUrl = WebDavUrl,
                Username = Username,
                AuthType = AuthType.Basic
            };

            var pwd = string.IsNullOrEmpty(password)
                ? CredentialManager.LoadPassword() ?? string.Empty
                : password;
            var handler = WebDavAuthHandler.CreateHandler(settings, pwd);
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            using var webDav = new WebDavService(
                httpClient,
                WebDavUrl,
                _loggerFactory.CreateLogger<WebDavService>());

            var sw = Stopwatch.StartNew();
            var success = await webDav.TestConnectionAsync();
            sw.Stop();

            StatusMessage = success
                ? AppLocalizer.Instance.Format("Settings_Status_ConnectionSuccessful", sw.ElapsedMilliseconds)
                : AppLocalizer.Instance.GetString("Settings_Status_ConnectionFailed");
        }
        catch (Exception ex)
        {
            StatusMessage = AppLocalizer.Instance.Format("Settings_Status_ConnectionFailedWithMessage", ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = AppLocalizer.Instance.GetString("Settings_SelectSyncRootFolder"),
            InitialDirectory = SyncRootPath
        };

        if (dialog.ShowDialog() == true)
        {
            SyncRootPath = dialog.FolderName;
            OnPropertyChanged(nameof(SyncRootStatus));
        }
    }

    public int GetPendingUploadCount()
    {
        try
        {
            var settings = AppSettings.Load();
            var dataDir = settings.DataDirectory ?? AppSettings.GetDataDirectory();
            var dbPath = Path.Combine(dataDir, "syncstate.db");
            if (!File.Exists(dbPath)) return 0;

            using var db = new SyncStateDb(dbPath, _loggerFactory.CreateLogger<SyncStateDb>());
            return db.GetByStatus(SyncStatus.PendingUpload).Count
                 + db.GetByStatus(SyncStatus.Syncing).Count
                 + db.GetByStatus(SyncStatus.Error).Count;
        }
        catch
        {
            return 0;
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        await ExecuteResetAsync(_resetCallback);
    }

    [RelayCommand]
    private async Task ResetConfigurationAsync()
    {
        await ExecuteResetAsync(_resetConfigurationCallback);
    }

    public void Dispose()
    {
        _isDisposed = true;
        AppLocalizer.Instance.CultureChanged -= OnCultureChanged;
    }

    private async Task ExecuteResetAsync(Func<Task>? callback)
    {
        if (callback == null)
        {
            StatusMessage = AppLocalizer.Instance.GetString("Settings_Status_ResetUnavailable");
            return;
        }

        await callback();
    }
}
