using System.Net;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Shouldly;

namespace CloudDrive.Core.Tests.WebDav;

public class LockTests
{
    private const string BaseUrl = "https://example.test/webdav";

    private static WebDavService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new WebDavService(httpClient, BaseUrl, NullLogger<WebDavService>.Instance);
    }

    [Fact]
    public async Task LockAsync_SendsExclusiveWriteLockAndParsesLockTokenHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
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
                capturedBody = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                var response = new HttpResponseMessage(HttpStatusCode.Created);
                response.Headers.TryAddWithoutValidation("Lock-Token", "<opaquelocktoken:test>");
                return response;
            });

        using var service = CreateService(handlerMock.Object);

        var lockInfo = await service.LockAsync(
            "/Hallo.docx",
            new WebDavLockRequest("CloudDrive test", TimeSpan.FromMinutes(10)));

        lockInfo.Token.ShouldBe("<opaquelocktoken:test>");
        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Method.Method.ShouldBe("LOCK");
        capturedRequest.Headers.GetValues("Timeout").Single().ShouldBe("Second-600");
        capturedBody.ShouldNotBeNull();
        capturedBody!.ShouldContain("<D:exclusive/>");
        capturedBody.ShouldContain("<D:write/>");
    }

    [Fact]
    public async Task UnlockAsync_SendsLockTokenHeader()
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
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

        using var service = CreateService(handlerMock.Object);

        await service.UnlockAsync("/Hallo.docx", "opaquelocktoken:test");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Method.Method.ShouldBe("UNLOCK");
        capturedRequest.Headers.GetValues("Lock-Token").Single().ShouldBe("<opaquelocktoken:test>");
    }
}
