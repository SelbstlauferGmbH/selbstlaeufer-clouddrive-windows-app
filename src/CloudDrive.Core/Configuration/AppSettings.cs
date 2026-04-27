using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CloudDrive.Core.Configuration;

public readonly record struct AccountConfigurationStatus(bool HasValidWebDavUrl, bool HasUsername, bool HasPassword)
{
    public bool IsComplete => HasValidWebDavUrl && HasUsername && HasPassword;
}

public class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudDrive");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public string WebDavUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string SyncRootPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudDrive");
    public string? SyncRootAccountIdOverride { get; set; }
    public AuthType AuthType { get; set; } = AuthType.Basic;
    public int SyncIntervalSeconds { get; set; } = 30;
    public int MaxConcurrentTransfers { get; set; } = 4;
    public bool EnableFileLogging { get; set; } = false;
    public bool EnableWebDavLocking { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool LaunchOnStartup { get; set; } = true;
    public string ThemeMode { get; set; } = "System";
    public string Language { get; set; } = "System";
    public double? SettingsWindowLeft { get; set; }
    public double? SettingsWindowTop { get; set; }
    public double? SettingsWindowWidth { get; set; }
    public double? SettingsWindowHeight { get; set; }

    /// <summary>
    /// Per-instance data directory override. When set, SyncCoordinator uses this
    /// instead of the static <see cref="GetDataDirectory()"/> path.
    /// Used by tests to isolate the SQLite database.
    /// </summary>
    public string? DataDirectory { get; set; }

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        var json = File.ReadAllText(SettingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public AccountConfigurationStatus GetAccountConfigurationStatus(bool hasPassword)
    {
        return new AccountConfigurationStatus(
            TryGetWebDavUri(out _),
            !string.IsNullOrWhiteSpace(Username),
            hasPassword);
    }

    public bool HasCompleteAccountConfiguration(bool hasPassword)
    {
        return GetAccountConfigurationStatus(hasPassword).IsComplete;
    }

    public bool TryGetWebDavUri([NotNullWhen(true)] out Uri? uri)
    {
        return TryCreateWebDavUri(WebDavUrl, out uri);
    }

    public static bool TryCreateWebDavUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            uri = null;
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            uri = null;
            return false;
        }

        return true;
    }

    public static string GetDataDirectory() => SettingsDir;
}

public enum AuthType
{
    Basic,
    Ntlm,
    Negotiate
}
