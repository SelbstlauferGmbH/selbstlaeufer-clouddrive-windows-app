using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CloudDrive.App.Services;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.App.ViewModels;

public partial class ConfigurationWizardViewModel : ObservableObject, IDisposable
{
    private const int PreferencesStepIndex = 0;
    private const int AccountStepIndex = 1;

    private readonly ILoggerFactory _loggerFactory;
    private readonly IAutoStartRegistration _autoStartRegistration;
    private bool _isDisposed;

    [ObservableProperty] private int _currentStepIndex = PreferencesStepIndex;
    [ObservableProperty] private bool _launchOnStartup = true;
    [ObservableProperty] private string _themeMode = "System";
    [ObservableProperty] private string _language = AppLanguage.System;
    [ObservableProperty] private string _webDavUrl = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _pendingPassword = string.Empty;
    [ObservableProperty] private bool _hasStoredPassword;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isTesting;

    public ConfigurationWizardViewModel(ILoggerFactory loggerFactory, IAutoStartRegistration? autoStartRegistration = null)
    {
        _loggerFactory = loggerFactory;
        _autoStartRegistration = autoStartRegistration ?? new WindowsAutoStartRegistration();
        ThemeOptions = new ObservableCollection<SelectionOption>();
        LanguageOptions = new ObservableCollection<SelectionOption>();

        LoadSettings();
        RebuildLocalizedCollections();
        AppLocalizer.Instance.CultureChanged += OnCultureChanged;
    }

    public ObservableCollection<SelectionOption> ThemeOptions { get; }
    public ObservableCollection<SelectionOption> LanguageOptions { get; }

    public bool IsPreferencesStep => CurrentStepIndex == PreferencesStepIndex;
    public bool IsAccountStep => CurrentStepIndex == AccountStepIndex;
    public bool CanGoBack => CurrentStepIndex > PreferencesStepIndex;
    public string WindowTitle => AppLocalizer.Instance.GetString("Wizard_WindowTitle");
    public string PasswordHint => HasStoredPassword
        ? AppLocalizer.Instance.GetString("Wizard_PasswordHint_Stored")
        : AppLocalizer.Instance.GetString("Wizard_PasswordHint_Missing");
    public string PrimaryActionText => IsAccountStep
        ? AppLocalizer.Instance.GetString("Wizard_SaveAndStart")
        : AppLocalizer.Instance.GetString("Common_Next");

    partial void OnCurrentStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPreferencesStep));
        OnPropertyChanged(nameof(IsAccountStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    partial void OnThemeModeChanged(string value)
    {
        if (_isDisposed)
            return;

        ThemeManager.ApplyTheme(NormalizeThemeMode(value));
    }

    partial void OnLanguageChanged(string value)
    {
        if (_isDisposed)
            return;

        AppLocalizer.Instance.SetLanguage(AppLanguage.NormalizeSetting(value));
    }

    partial void OnHasStoredPasswordChanged(bool value)
    {
        OnPropertyChanged(nameof(PasswordHint));
    }

    public void MoveNext()
    {
        StatusMessage = string.Empty;
        CurrentStepIndex = AccountStepIndex;
    }

    public void MoveBack()
    {
        StatusMessage = string.Empty;
        CurrentStepIndex = PreferencesStepIndex;
    }

    public async Task TestConnectionAsync()
    {
        HasStoredPassword = CredentialManager.HasPassword();
        var validationMessage = ValidateAccountConfiguration(requirePassword: true);
        if (!string.IsNullOrEmpty(validationMessage))
        {
            StatusMessage = validationMessage;
            CurrentStepIndex = AccountStepIndex;
            return;
        }

        IsTesting = true;
        StatusMessage = AppLocalizer.Instance.GetString("Settings_Status_TestingConnection");

        try
        {
            var settings = new AppSettings
            {
                WebDavUrl = WebDavUrl.Trim(),
                Username = Username.Trim(),
                AuthType = AuthType.Basic
            };

            var password = PendingPassword.Length > 0
                ? PendingPassword
                : CredentialManager.LoadPassword() ?? string.Empty;

            using var handler = WebDavAuthHandler.CreateHandler(settings, password);
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            using var webDav = new WebDavService(
                httpClient,
                settings.WebDavUrl,
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

    public async Task<bool> FinishAsync()
    {
        HasStoredPassword = CredentialManager.HasPassword();
        var validationMessage = ValidateAccountConfiguration(requirePassword: true);
        if (!string.IsNullOrEmpty(validationMessage))
        {
            StatusMessage = validationMessage;
            CurrentStepIndex = AccountStepIndex;
            return false;
        }

        var settings = AppSettings.Load();
        settings.ThemeMode = NormalizeThemeMode(ThemeMode);
        settings.Language = AppLanguage.NormalizeSetting(Language);
        settings.LaunchOnStartup = LaunchOnStartup;
        settings.WebDavUrl = WebDavUrl.Trim();
        settings.Username = Username.Trim();
        settings.AuthType = AuthType.Basic;
        settings.Save();
        _autoStartRegistration.Apply(settings.LaunchOnStartup);

        if (PendingPassword.Length > 0)
            CredentialManager.SavePassword(PendingPassword);

        ThemeManager.ApplyTheme(settings.ThemeMode);
        AppLocalizer.Instance.SetLanguage(settings.Language);
        HasStoredPassword = CredentialManager.HasPassword();
        StatusMessage = AppLocalizer.Instance.GetString("Wizard_Status_Starting");

        await Task.CompletedTask;
        return true;
    }

    public void Dispose()
    {
        _isDisposed = true;
        AppLocalizer.Instance.CultureChanged -= OnCultureChanged;
    }

    private void LoadSettings()
    {
        var settings = AppSettings.Load();
        LaunchOnStartup = settings.LaunchOnStartup;
        ThemeMode = NormalizeThemeMode(settings.ThemeMode);
        Language = AppLanguage.NormalizeSetting(settings.Language);
        WebDavUrl = settings.WebDavUrl;
        Username = settings.Username;
        PendingPassword = string.Empty;
        HasStoredPassword = CredentialManager.HasPassword();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (_isDisposed)
            return;

        RebuildLocalizedCollections();
    }

    private void RebuildLocalizedCollections()
    {
        ReplaceCollection(ThemeOptions, AppSettingsOptionFactory.BuildThemeOptions());
        ReplaceCollection(LanguageOptions, AppSettingsOptionFactory.BuildLanguageOptions());

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(PasswordHint));
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    private string? ValidateAccountConfiguration(bool requirePassword)
    {
        var configurationStatus = new AccountConfigurationStatus(
            AppSettings.TryCreateWebDavUri(WebDavUrl, out _),
            !string.IsNullOrWhiteSpace(Username),
            HasStoredPassword || PendingPassword.Length > 0);

        if (!configurationStatus.HasValidWebDavUrl)
            return AppLocalizer.Instance.GetString("Wizard_Status_EnterValidWebDavUrl");

        if (!configurationStatus.HasUsername)
            return AppLocalizer.Instance.GetString("Wizard_Status_EnterUsername");

        if (requirePassword && !configurationStatus.HasPassword)
            return AppLocalizer.Instance.GetString("Wizard_Status_EnterPassword");

        return null;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static string NormalizeThemeMode(string? themeMode)
    {
        if (string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase))
            return "Light";
        if (string.Equals(themeMode, "Dark", StringComparison.OrdinalIgnoreCase))
            return "Dark";
        return "System";
    }
}
