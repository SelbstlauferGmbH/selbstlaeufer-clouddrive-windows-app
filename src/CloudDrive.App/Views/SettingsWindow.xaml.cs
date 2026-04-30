using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CloudDrive.App.Services;
using CloudDrive.App.ViewModels;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;

namespace CloudDrive.App.Views;

public partial class SettingsWindow : Window
{
    private const string StoredPasswordMask = "********";
    private readonly DispatcherTimer _refreshTimer;
    private readonly EventHandler<ThemeChangedEventArgs> _themeChangedHandler;
    private readonly PropertyChangedEventHandler _viewModelPropertyChangedHandler;
    private bool _isClosing;
    private bool _showingStoredPasswordMask;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ThemeManager.ApplyWindowTheme(this);
        ((CollectionViewSource)Resources["GroupedNavigationItems"]).Source = viewModel.NavigationItems;
        _themeChangedHandler = async (_, _) =>
        {
            if (_isClosing)
                return;

            if (DataContext is not SettingsViewModel vm)
                return;

            vm.RefreshThemeState();
            await await Dispatcher.InvokeAsync(() => vm.RefreshAsync(forceHealthCheck: true));
        };
        ThemeManager.ThemeChanged += _themeChangedHandler;
        _viewModelPropertyChangedHandler = (_, e) =>
        {
            if (_isClosing || e.PropertyName != nameof(SettingsViewModel.HasStoredPassword))
                return;

            Dispatcher.Invoke(SyncPasswordBoxMask);
        };
        viewModel.PropertyChanged += _viewModelPropertyChangedHandler;

        SyncPasswordBoxMask();
        RestoreWindowBounds();

        Loaded += async (_, _) =>
        {
            if (_isClosing)
                return;

            await viewModel.RefreshAsync(forceHealthCheck: true);
            SyncPasswordBoxMask();
        };

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_isClosing)
                return;

            if (DataContext is SettingsViewModel vm)
                await vm.RefreshAsync();
        };
        _refreshTimer.Start();
    }

    public void NavigateTo(SettingsSection section)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.NavigateTo(section);
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            await vm.TestConnectionCommand.ExecuteAsync(GetEnteredPassword());
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var targetChangeDecision = RemoteTargetChangeDecision.KeepLocalState;
            AppSettings? previousSettings = null;

            if (vm.HasRemoteTargetChanged())
            {
                previousSettings = vm.CreateSavedSettingsSnapshot();
                targetChangeDecision = ShowRemoteTargetChangeDialog(
                    previousSettings.WebDavUrl,
                    vm.WebDavUrl);

                if (targetChangeDecision == RemoteTargetChangeDecision.Cancel)
                {
                    vm.StatusMessage = AppLocalizer.Instance.GetString("RemoteTargetChange_Status_SaveCancelled");
                    return;
                }
            }

            await vm.SaveCommand.ExecuteAsync(GetEnteredPassword());
            SetPasswordBoxValue(string.Empty, showingStoredPasswordMask: false);
            SyncPasswordBoxMask();

            if (targetChangeDecision == RemoteTargetChangeDecision.ResetLocalState &&
                previousSettings != null)
            {
                await vm.ResetLocalStateForRemoteTargetChangeAsync(previousSettings);
            }
        }
    }

    private RemoteTargetChangeDecision ShowRemoteTargetChangeDialog(string oldTarget, string newTarget)
    {
        var dialog = new RemoteTargetChangeWindow(oldTarget, newTarget)
        {
            Owner = this
        };

        return dialog.ShowDialog() == true
            ? dialog.Decision
            : RemoteTargetChangeDecision.Cancel;
    }

    private void PasswordBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_showingStoredPasswordMask)
            SetPasswordBoxValue(string.Empty, showingStoredPasswordMask: false);
    }

    private void PasswordBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SyncPasswordBoxMask();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CrashButton_Click(object sender, RoutedEventArgs e)
    {
        CloudDrive.Core.Helpers.DebugHelper.TriggerSilentCrash();
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmResetAsync(
            "Settings_Reset_Title",
            "Settings_Reset_Message",
            vm => vm.ResetCommand.ExecuteAsync(null));
    }

    private async void FullResetButton_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmResetAsync(
            "Settings_ResetFull_Title",
            "Settings_ResetFull_Message",
            vm => vm.ResetConfigurationCommand.ExecuteAsync(null));
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = AppLocalizer.Instance.GetString("Settings_ExportCsv_Title"),
            Filter = AppLocalizer.Instance.GetString("Settings_ExportCsv_Filter"),
            FileName = AppLocalizer.Instance.Format("Settings_ExportCsv_FileName", DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.CurrentCulture))
        };

        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, vm.BuildActivityCsv());
    }

    private async Task ConfirmResetAsync(
        string titleKey,
        string messageKey,
        Func<SettingsViewModel, Task> executeResetAsync)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var settings = AppSettings.Load();
        var message = AppLocalizer.Instance.Format(messageKey, settings.SyncRootPath);

        var pendingCount = vm.GetPendingUploadCount();
        if (pendingCount > 0)
            message += AppLocalizer.Instance.Format("Settings_Reset_PendingUploads", pendingCount);

        var result = System.Windows.MessageBox.Show(
            message,
            AppLocalizer.Instance.GetString(titleKey),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result == MessageBoxResult.OK)
        {
            await executeResetAsync(vm);
        }
    }

    private void ActionFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FilterOption<ActivityActionKind> option &&
            DataContext is SettingsViewModel vm)
        {
            vm.SelectActionFilter(option.Value);
        }
    }

    private void FileTypeFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FilterOption<ActivityFileTypeFilter> option &&
            DataContext is SettingsViewModel vm)
        {
            vm.SelectFileTypeFilter(option.Value);
        }
    }

    private void RangeFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FilterOption<StatisticsRange> option &&
            DataContext is SettingsViewModel vm)
        {
            vm.SelectStatisticsRange(option.Value);
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.GoToPreviousPage();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.GoToNextPage();
    }

    private void PageNumber_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int page &&
            DataContext is SettingsViewModel vm)
        {
            vm.GoToPage(page);
        }
    }

    private async void RunAllChecks_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            await vm.RunAllChecksAsync();
    }

    private async void HealthAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string actionId &&
            DataContext is SettingsViewModel vm)
        {
            await vm.ExecuteHealthActionAsync(actionId);
        }
    }

    private void ActivityItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityDisplayItem item ||
            string.IsNullOrWhiteSpace(item.OpenPath))
            return;

        OpenPath(item.OpenPathIsFolder || Directory.Exists(item.OpenPath)
            ? item.OpenPath
            : item.OpenPath);
    }

    private void LocationLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is ActivityDisplayItem item &&
            !string.IsNullOrWhiteSpace(item.FolderPath))
        {
            OpenPath(item.FolderPath);
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        OpenPath(Path.Combine(AppContext.BaseDirectory, "USAGE.md"));
    }

    private async void ProblemAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProblemDisplayAction action)
            return;

        if (DataContext is not SettingsViewModel vm)
            return;

        switch (action.Kind)
        {
            case ProblemActionKind.OpenPath:
            case ProblemActionKind.OpenFolder:
                if (!string.IsNullOrWhiteSpace(action.Path))
                    OpenPath(action.Path);
                break;

            case ProblemActionKind.SyncNow:
                await vm.TriggerSyncNowAsync();
                break;

            case ProblemActionKind.OpenSettings:
                if (action.Section.HasValue)
                    vm.NavigateTo(action.Section.Value);
                break;

            case ProblemActionKind.ConfirmRemoteDelete:
                if (action.ProblemId.HasValue &&
                    !string.IsNullOrWhiteSpace(action.Path) &&
                    !string.IsNullOrWhiteSpace(action.RemotePath))
                {
                    await vm.ConfirmRemoteDeleteAsync(action.ProblemId.Value, action.Path, action.RemotePath);
                }
                break;

            case ProblemActionKind.KeepRemoteCopy:
                if (action.ProblemId.HasValue &&
                    !string.IsNullOrWhiteSpace(action.Path) &&
                    !string.IsNullOrWhiteSpace(action.RemotePath))
                {
                    await vm.KeepRemoteCopyAsync(action.ProblemId.Value, action.Path, action.RemotePath);
                }
                break;

            case ProblemActionKind.ReuploadRemoteDeletedLocalChange:
                if (action.ProblemId.HasValue &&
                    !string.IsNullOrWhiteSpace(action.Path) &&
                    !string.IsNullOrWhiteSpace(action.RemotePath))
                {
                    await vm.ReuploadRemoteDeletedLocalChangeAsync(action.ProblemId.Value, action.Path, action.RemotePath);
                }
                break;

            case ProblemActionKind.DeleteLocalRemoteDeletedLocalChange:
                if (action.ProblemId.HasValue &&
                    !string.IsNullOrWhiteSpace(action.Path) &&
                    !string.IsNullOrWhiteSpace(action.RemotePath))
                {
                    await vm.DeleteLocalRemoteDeletedLocalChangeAsync(action.ProblemId.Value, action.Path, action.RemotePath);
                }
                break;

            case ProblemActionKind.Dismiss:
                if (action.ProblemId.HasValue)
                    await vm.DismissProblemAsync(action.ProblemId.Value);
                break;
        }
    }

    private void ProblemLocationLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if ((sender as FrameworkElement)?.Tag is ActivityDisplayItem activityItem &&
            !string.IsNullOrWhiteSpace(activityItem.FolderPath))
        {
            OpenPath(activityItem.FolderPath);
            return;
        }

        if ((sender as FrameworkElement)?.Tag is ProblemDisplayItem problemItem &&
            !string.IsNullOrWhiteSpace(problemItem.FolderPath))
        {
            OpenPath(problemItem.FolderPath);
        }
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        OpenUri(AppSettings.Load().WebDavUrl, "privacy");
    }

    private void Terms_Click(object sender, RoutedEventArgs e)
    {
        OpenUri(AppSettings.Load().WebDavUrl, "terms");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        _refreshTimer.Stop();
        ThemeManager.ThemeChanged -= _themeChangedHandler;
        if (DataContext is SettingsViewModel vm)
            vm.PropertyChanged -= _viewModelPropertyChangedHandler;
        (DataContext as IDisposable)?.Dispose();
        DataContext = null;
        PersistWindowBounds();
        base.OnClosing(e);
    }

    private string? GetEnteredPassword()
    {
        return _showingStoredPasswordMask || string.IsNullOrEmpty(PasswordBox.Password)
            ? null
            : PasswordBox.Password;
    }

    private void SyncPasswordBoxMask()
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        if (PasswordBox.IsKeyboardFocused)
            return;

        if (vm.HasStoredPassword && string.IsNullOrEmpty(PasswordBox.Password))
        {
            SetPasswordBoxValue(StoredPasswordMask, showingStoredPasswordMask: true);
            return;
        }

        if (!vm.HasStoredPassword && _showingStoredPasswordMask)
        {
            SetPasswordBoxValue(string.Empty, showingStoredPasswordMask: false);
        }
    }

    private void SetPasswordBoxValue(string password, bool showingStoredPasswordMask)
    {
        PasswordBox.Password = password;
        _showingStoredPasswordMask = showingStoredPasswordMask;
    }

    private void RestoreWindowBounds()
    {
        var settings = AppSettings.Load();
        if (settings.SettingsWindowWidth.HasValue) Width = settings.SettingsWindowWidth.Value;
        if (settings.SettingsWindowHeight.HasValue) Height = settings.SettingsWindowHeight.Value;
        if (settings.SettingsWindowLeft.HasValue) Left = settings.SettingsWindowLeft.Value;
        if (settings.SettingsWindowTop.HasValue) Top = settings.SettingsWindowTop.Value;
    }

    private void PersistWindowBounds()
    {
        var settings = AppSettings.Load();
        settings.SettingsWindowLeft = Left;
        settings.SettingsWindowTop = Top;
        settings.SettingsWindowWidth = Width;
        settings.SettingsWindowHeight = Height;
        settings.Save();
    }

    private static void OpenPath(string path)
    {
        try
        {
            var absolute = Path.GetFullPath(path);
            if (!File.Exists(absolute) && !Directory.Exists(absolute))
                return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = absolute,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static void OpenUri(string? baseUrl, string suffix)
    {
        try
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                var target = new Uri(uri, suffix);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = target.ToString(),
                    UseShellExecute = true
                });
            }
        }
        catch
        {
        }
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

public class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SettingsSection section && parameter is string parameterText &&
            Enum.TryParse<SettingsSection>(parameterText, out var target))
        {
            return section == target ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
