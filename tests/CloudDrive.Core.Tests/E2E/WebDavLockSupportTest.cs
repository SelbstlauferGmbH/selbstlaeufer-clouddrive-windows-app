using CloudDrive.Core.Configuration;
using CloudDrive.Core.Tests.Infrastructure;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.E2E;

[Collection("E2E")]
[Trait("Category", "E2E")]
public class WebDavLockSupportTest
{
    private static TimeSpan Timeout => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_TIMEOUT_SECONDS"), out var seconds)
            ? seconds
            : 30);

    [Fact]
    public async Task CheckLockSupportAsync_WhenServerEnforcesLocks_ReturnsSupported()
    {
        DotEnvLoader.Load();
        using var service = CreateService(RequiredEnv("CLOUDDRIVE_TEST_WEBDAV_URL"));
        using var cts = new CancellationTokenSource(Timeout);

        var support = await service.CheckLockSupportAsync(cts.Token);

        support.State.ShouldBe(WebDavLockSupportState.Supported, support.Detail);
        await AssertNoProbeFilesRemainAsync(service, cts.Token);
    }

    [Fact]
    public async Task CheckLockSupportAsync_WhenServerRejectsLockMethod_ReturnsUnsupported()
    {
        DotEnvLoader.Load();
        using var service = CreateService(RequiredEnv("CLOUDDRIVE_TEST_WEBDAV_NOLOCK_URL"));
        using var cts = new CancellationTokenSource(Timeout);

        var support = await service.CheckLockSupportAsync(cts.Token);

        support.State.ShouldBe(WebDavLockSupportState.Unsupported, support.Detail);
        support.Detail.ShouldContain("405");
        await AssertNoProbeFilesRemainAsync(service, cts.Token);
    }

    private static WebDavService CreateService(string webDavUrl)
    {
        var settings = new AppSettings
        {
            WebDavUrl = webDavUrl,
            Username = RequiredEnv("CLOUDDRIVE_TEST_USERNAME"),
            AuthType = AuthType.Basic
        };

        var handler = WebDavAuthHandler.CreateHandler(settings, RequiredEnv("CLOUDDRIVE_TEST_PASSWORD"));
        var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout
        };

        return new WebDavService(httpClient, webDavUrl, NullLogger<WebDavService>.Instance);
    }

    private static async Task AssertNoProbeFilesRemainAsync(WebDavService service, CancellationToken ct)
    {
        var items = await service.ListDirectoryAsync("/", ct);
        items.Any(item => item.Name.StartsWith(".clouddrive-lock-probe-", StringComparison.Ordinal))
            .ShouldBeFalse("lock detection should clean up its temporary probe resource");
    }

    private static string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} environment variable is required for WebDAV lock E2E tests.");
}
