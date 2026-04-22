using CloudDrive.Core.Localization;

namespace CloudDrive.App.ViewModels;

internal static class AppSettingsOptionFactory
{
    public static IReadOnlyList<SelectionOption> BuildThemeOptions()
    {
        return
        [
            new("System", AppLocalizer.Instance.GetString("Common_System")),
            new("Light", AppLocalizer.Instance.GetString("Common_Light")),
            new("Dark", AppLocalizer.Instance.GetString("Common_Dark"))
        ];
    }

    public static IReadOnlyList<SelectionOption> BuildLanguageOptions()
    {
        return
        [
            new(AppLanguage.System, AppLocalizer.Instance.GetString("Language_System")),
            new(AppLanguage.English, AppLocalizer.Instance.GetString("Common_English")),
            new(AppLanguage.German, AppLocalizer.Instance.GetString("Common_German"))
        ];
    }
}
