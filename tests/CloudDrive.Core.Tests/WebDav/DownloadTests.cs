using System.Net;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace CloudDrive.Core.Tests.WebDav;

public class DownloadTests
{
    private const string BaseUrl = "https://example.test/webdav";

    private static WebDavService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new WebDavService(httpClient, BaseUrl, NullLogger<WebDavService>.Instance);
    }

    [Fact]
    public async Task DownloadFilePriorityAsync_AddsTranslateAndRangeHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(new MemoryStream([1, 2, 3, 4]))
                };
            });

        using var service = CreateService(handlerMock.Object);

        await using var stream = await service.DownloadFilePriorityAsync(
            "/Win-CASA/classic-car66.heic",
            offset: 4096,
            length: 8192,
            ct: CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Equal("f", Assert.Single(capturedRequest.Headers.GetValues("Translate")));
        Assert.NotNull(capturedRequest.Headers.Range);
        Assert.Equal(4096, capturedRequest.Headers.Range!.Ranges.Single().From);
        Assert.Equal(12287, capturedRequest.Headers.Range.Ranges.Single().To);
    }
}
