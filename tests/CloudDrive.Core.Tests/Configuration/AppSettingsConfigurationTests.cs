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

    [Theory]
    [InlineData("https://example.com/dav", "https://example.com/dav/", false)]
    [InlineData(" https://EXAMPLE.com:443/dav/ ", "https://example.com/dav", false)]
    [InlineData("http://example.com:80/dav", "http://example.com/dav/", false)]
    [InlineData("https://example.com/dav", "https://example.com/other", true)]
    [InlineData("https://example.com/dav", "https://other.example.com/dav", true)]
    [InlineData("http://example.com/dav", "https://example.com/dav", true)]
    [InlineData("", "https://example.com/dav", false)]
    public void HasWebDavTargetChanged_NormalizesEquivalentTargets(
        string storedUrl,
        string candidateUrl,
        bool expected)
    {
        AppSettings.HasWebDavTargetChanged(storedUrl, candidateUrl).ShouldBe(expected);
    }

}
