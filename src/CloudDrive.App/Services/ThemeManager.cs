using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace CloudDrive.App.Services;

public sealed class ThemeChangedEventArgs(string requestedMode, string effectiveMode, bool isDarkTheme) : EventArgs
{
    public string RequestedMode { get; } = requestedMode;
    public string EffectiveMode { get; } = effectiveMode;
    public bool IsDarkTheme { get; } = isDarkTheme;
}

public static class ThemeManager
{
    private static readonly Uri LightThemeUri = new("Themes/Theme.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Themes/Theme.Dark.xaml", UriKind.Relative);
    private static readonly object SyncRoot = new();
    private static bool _initialized;
    private static ResourceDictionary? _activeThemeDictionary;

    public static event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public static string RequestedMode { get; private set; } = "System";
    public static string EffectiveMode { get; private set; } = "Light";
    public static bool IsDarkTheme { get; private set; }

    public static void Initialize(string requestedMode)
    {
        lock (SyncRoot)
        {
            if (!_initialized)
            {
                SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                _initialized = true;
            }
        }

        ApplyTheme(requestedMode);
    }

    public static void ApplyTheme(string requestedMode)
    {
        var application = System.Windows.Application.Current;
        if (application == null)
            return;

        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.Invoke(() => ApplyTheme(requestedMode));
            return;
        }

        RequestedMode = NormalizeMode(requestedMode);
        var darkTheme = ResolveIsDark(RequestedMode);
        var effectiveMode = darkTheme ? "Dark" : "Light";

        if (_activeThemeDictionary != null)
        {
            application.Resources.MergedDictionaries.Remove(_activeThemeDictionary);
        }

        _activeThemeDictionary = new ResourceDictionary
        {
            Source = darkTheme ? DarkThemeUri : LightThemeUri
        };
        application.Resources.MergedDictionaries.Add(_activeThemeDictionary);

        IsDarkTheme = darkTheme;
        EffectiveMode = effectiveMode;

        foreach (Window window in application.Windows)
        {
            ApplyWindowTheme(window);
        }

        ThemeChanged?.Invoke(null, new ThemeChangedEventArgs(RequestedMode, EffectiveMode, IsDarkTheme));
    }

    public static void ApplyWindowTheme(Window window)
    {
        if (window.IsInitialized)
        {
            ApplyImmersiveDarkMode(window);
            return;
        }

        window.SourceInitialized -= Window_SourceInitialized;
        window.SourceInitialized += Window_SourceInitialized;
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        window.SourceInitialized -= Window_SourceInitialized;
        ApplyImmersiveDarkMode(window);
    }

    private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!string.Equals(RequestedMode, "System", StringComparison.OrdinalIgnoreCase))
            return;

        if (e.Category is not UserPreferenceCategory.Color and
            not UserPreferenceCategory.General and
            not UserPreferenceCategory.VisualStyle)
        {
            return;
        }

        ApplyTheme("System");
    }

    private static void ApplyImmersiveDarkMode(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var useDarkMode = IsDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    private static bool ResolveIsDark(string requestedMode)
    {
        if (string.Equals(requestedMode, "Dark", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(requestedMode, "Light", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var personalizeKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            var value = personalizeKey?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeMode(string? requestedMode)
    {
        if (string.Equals(requestedMode, "Light", StringComparison.OrdinalIgnoreCase))
            return "Light";
        if (string.Equals(requestedMode, "Dark", StringComparison.OrdinalIgnoreCase))
            return "Dark";
        return "System";
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
}
