using CloudDrive.Core.Configuration;
using Shouldly;

namespace CloudDrive.Core.Tests.Configuration;

public class AppSettingsConfigurationTests
{
    [Theory]
    [InlineData("https://example.com/remote.php/dav/files/demo", true)]
    [InlineData("http://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void TryCreateWebDavUri_ValidatesSupportedAbsoluteUrls(string url, bool expected)
    {
        var success = AppSettings.TryCreateWebDavUri(url, out var uri);

        success.ShouldBe(expected);
        (uri != null).ShouldBe(expected);
    }

    [Fact]
    public void GetAccountConfigurationStatus_IsCompleteWhenUrlUsernameAndPasswordExist()
    {
        var settings = new AppSettings
        {
            WebDavUrl = "https://example.com/remote.php/dav/files/demo",
            Username = "demo"
        };

        var status = settings.GetAccountConfigurationStatus(hasPassword: true);

        status.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void GetAccountConfigurationStatus_IsIncompleteWhenUsernameIsMissing()
    {
        var settings = new AppSettings
        {
            WebDavUrl = "https://example.com/remote.php/dav/files/demo",
            Username = ""
        };

        var status = settings.GetAccountConfigurationStatus(hasPassword: true);

        status.HasValidWebDavUrl.ShouldBeTrue();
        status.HasUsername.ShouldBeFalse();
        status.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void GetAccountConfigurationStatus_IsIncompleteWhenPasswordIsMissing()
    {
        var settings = new AppSettings
        {
            WebDavUrl = "https://example.com/remote.php/dav/files/demo",
            Username = "demo"
        };

        var status = settings.GetAccountConfigurationStatus(hasPassword: false);

        status.HasPassword.ShouldBeFalse();
        status.IsComplete.ShouldBeFalse();
    }
}
