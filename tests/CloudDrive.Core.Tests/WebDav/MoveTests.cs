using System.Net;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace CloudDrive.Core.Tests.WebDav;

public class MoveTests
{
    private const string BaseUrl = "https://example.test/webdav";

    private static WebDavService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new WebDavService(httpClient, BaseUrl, NullLogger<WebDavService>.Instance);
    }

    [Fact]
    public async Task MoveAsync_EncodesRequestUriAndDestinationHeader()
    {
        // Arrange
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
                return new HttpResponseMessage(HttpStatusCode.Created);
            });

        using var service = CreateService(handlerMock.Object);

        // Act
        await service.MoveAsync(
            "/Win-CASA/Ventoy mit Datenpartition.pdf",
            "/Win-CASA/Ventoy mit Datenpartition 0002.pdf");

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Equal("MOVE", capturedRequest!.Method.Method);
        Assert.Equal(
            $"{BaseUrl}/Win-CASA/Ventoy%20mit%20Datenpartition.pdf",
            capturedRequest.RequestUri!.AbsoluteUri);
        Assert.True(capturedRequest.Headers.TryGetValues("Destination", out var destinationValues));
        Assert.Equal(
            $"{BaseUrl}/Win-CASA/Ventoy%20mit%20Datenpartition%200002.pdf",
            Assert.Single(destinationValues));
    }
}
