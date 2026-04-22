using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace CloudDrive.Core.Localization;

public sealed class AppLocalizer : INotifyPropertyChanged
{
    private readonly object _gate = new();
    private readonly ResourceManager _resourceManager = new(
        "CloudDrive.Core.Localization.AppStrings",
        typeof(AppLocalizer).Assembly);

    private bool _initialized;
    private CultureInfo _systemCulture = CultureInfo.CurrentUICulture;
    private CultureInfo _currentCulture = CultureInfo.GetCultureInfo("en-US");
    private string _currentLanguageSetting = AppLanguage.System;

    public static AppLocalizer Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;

    public CultureInfo CurrentCulture
    {
        get
        {
            lock (_gate)
            {
                return _currentCulture;
            }
        }
    }

    public string CurrentLanguageSetting
    {
        get
        {
            lock (_gate)
            {
                return _currentLanguageSetting;
            }
        }
    }

    public string this[string key] => GetString(key);

    public void Initialize(string? languageSetting)
    {
        CultureInfo cultureToApply;
        string normalizedSetting;

        lock (_gate)
        {
            if (!_initialized)
            {
                _systemCulture = CultureInfo.CurrentUICulture;
                _initialized = true;
            }

            normalizedSetting = AppLanguage.NormalizeSetting(languageSetting);
            cultureToApply = AppLanguage.ResolveCulture(normalizedSetting, _systemCulture);

            _currentLanguageSetting = normalizedSetting;
            _currentCulture = cultureToApply;
        }

        ApplyCulture(cultureToApply);
        RaiseCultureChanged();
    }

    public void SetLanguage(string? languageSetting)
    {
        CultureInfo cultureToApply;
        string normalizedSetting;
        bool changed;

        lock (_gate)
        {
            if (!_initialized)
            {
                _systemCulture = CultureInfo.CurrentUICulture;
                _initialized = true;
            }

            normalizedSetting = AppLanguage.NormalizeSetting(languageSetting);
            cultureToApply = AppLanguage.ResolveCulture(normalizedSetting, _systemCulture);
            changed = _currentLanguageSetting != normalizedSetting || _currentCulture.Name != cultureToApply.Name;

            _currentLanguageSetting = normalizedSetting;
            _currentCulture = cultureToApply;
        }

        ApplyCulture(cultureToApply);

        if (changed)
        {
            RaiseCultureChanged();
        }
    }

    public string GetString(string key)
    {
        var culture = CurrentCulture;
        return GetString(key, culture);
    }

    public string GetString(string key, CultureInfo culture)
    {
        return _resourceManager.GetString(key, culture)
            ?? _resourceManager.GetString(key, CultureInfo.GetCultureInfo("en-US"))
            ?? key;
    }

    public string Format(string key, params object?[] args)
    {
        return string.Format(CurrentCulture, GetString(key), args);
    }

    public string Format(CultureInfo culture, string key, params object?[] args)
    {
        return string.Format(culture, GetString(key, culture), args);
    }

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private void RaiseCultureChanged()
    {
        OnPropertyChanged(nameof(CurrentCulture));
        OnPropertyChanged(nameof(CurrentLanguageSetting));
        OnPropertyChanged("Item[]");
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
