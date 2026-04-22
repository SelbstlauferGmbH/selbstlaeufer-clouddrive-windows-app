using System.Globalization;

namespace CloudDrive.Core.Localization;

public static class AppLanguage
{
    public const string System = "System";
    public const string English = "en";
    public const string German = "de";

    public static readonly IReadOnlyList<string> SupportedLanguages =
    [
        System,
        English,
        German
    ];

    public static string NormalizeSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return System;

        return value.Trim().ToLowerInvariant() switch
        {
            "system" => System,
            "en" or "en-us" or "en-gb" or "english" => English,
            "de" or "de-de" or "de-at" or "de-ch" or "german" or "deutsch" => German,
            _ => System
        };
    }

    public static CultureInfo ResolveCulture(string? setting, CultureInfo? systemCulture = null)
    {
        var normalized = NormalizeSetting(setting);
        var detectedSystemCulture = systemCulture ?? CultureInfo.CurrentUICulture;

        return normalized switch
        {
            German => CultureInfo.GetCultureInfo("de-DE"),
            English => CultureInfo.GetCultureInfo("en-US"),
            _ => ResolveSystemCulture(detectedSystemCulture)
        };
    }

    public static CultureInfo ResolveSystemCulture(CultureInfo culture)
    {
        return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "de" => culture,
            "en" => culture,
            _ => CultureInfo.GetCultureInfo("en-US")
        };
    }
}
