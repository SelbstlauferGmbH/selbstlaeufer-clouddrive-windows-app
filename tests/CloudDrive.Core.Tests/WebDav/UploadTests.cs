using System.Net;
using System.Net.Http.Headers;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Shouldly;

namespace CloudDrive.Core.Tests.WebDav;

public class UploadTests
{
    private const string BaseUrl = "https://example.test/webdav";

    private static WebDavService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new WebDavService(httpClient, BaseUrl, NullLogger<WebDavService>.Instance);
    }

    [Fact]
    public async Task UploadFileAsync_NormalizesQuotedETagFromPutResponse()
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
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Headers =
                    {
                        ETag = new EntityTagHeaderValue("\"etag-after-upload\"")
                    }
                };
            });

        using var service = CreateService(handlerMock.Object);
        await using var content = new MemoryStream([1, 2, 3]);

        var etag = await service.UploadFileAsync("/folder/Test File.txt", content);

        etag.ShouldBe("etag-after-upload");
        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Method.ShouldBe(HttpMethod.Put);
        capturedRequest.RequestUri!.AbsoluteUri.ShouldBe($"{BaseUrl}/folder/Test%20File.txt");
    }
}
