using System.Globalization;
using CloudDrive.Core.Localization;
using Shouldly;

namespace CloudDrive.Core.Tests.Localization;

public class AppLanguageTests
{
    [Theory]
    [InlineData(null, AppLanguage.System)]
    [InlineData("", AppLanguage.System)]
    [InlineData("English", AppLanguage.English)]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("German", AppLanguage.German)]
    [InlineData("Deutsch", AppLanguage.German)]
    public void NormalizeSetting_MapsLegacyValues(string? value, string expected)
    {
        AppLanguage.NormalizeSetting(value).ShouldBe(expected);
    }

    [Fact]
    public void ResolveCulture_SystemPrefersGermanWhenSupported()
    {
        var culture = AppLanguage.ResolveCulture(AppLanguage.System, CultureInfo.GetCultureInfo("de-DE"));

        culture.TwoLetterISOLanguageName.ShouldBe("de");
    }

    [Fact]
    public void ResolveCulture_SystemFallsBackToEnglishWhenUnsupported()
    {
        var culture = AppLanguage.ResolveCulture(AppLanguage.System, CultureInfo.GetCultureInfo("fr-FR"));

        culture.Name.ShouldBe("en-US");
    }

    [Fact]
    public void GetString_UsesGermanResourcesWhenCultureIsGerman()
    {
        var value = AppLocalizer.Instance.GetString("Common_Save", CultureInfo.GetCultureInfo("de-DE"));

        value.ShouldBe("Speichern");
    }

    [Fact]
    public void GetString_FallsBackToEnglishWhenCultureIsUnsupported()
    {
        var value = AppLocalizer.Instance.GetString("Common_Save", CultureInfo.GetCultureInfo("fr-FR"));

        value.ShouldBe("Save");
    }
}
