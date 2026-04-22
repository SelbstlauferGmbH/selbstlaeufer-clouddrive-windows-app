using System.Net;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace CloudDrive.Core.Tests.WebDav;

public class HealthCheckTests
{
    private WebDavService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new WebDavService(httpClient, "http://localhost", NullLogger<WebDavService>.Instance);
    }

    [Fact]
    public async Task HealthCheckAsync_WhenServerUnreachable_ReturnsServerUnreachable()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("Server unreachable"));

        using var service = CreateService(handlerMock.Object);

        // Act
        var result = await service.HealthCheckAsync();

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal(HealthCheckFailure.ServerUnreachable, result.FailureReason);
    }

    [Fact]
    public async Task HealthCheckAsync_WhenHighLatency_ReturnsHighLatency()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns(async (HttpRequestMessage request, CancellationToken ct) =>
            {
                await Task.Delay(1100, ct); // Simulate high latency > 1000ms
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <?xml version="1.0" encoding="utf-8"?>
                        <D:multistatus xmlns:D="DAV:">
                          <D:response>
                            <D:href>/</D:href>
                            <D:propstat>
                              <D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                              <D:status>HTTP/1.1 200 OK</D:status>
                            </D:propstat>
                          </D:response>
                        </D:multistatus>
                        """)
                };
            });

        using var service = CreateService(handlerMock.Object);

        // Act
        var result = await service.HealthCheckAsync();

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal(HealthCheckFailure.HighLatency, result.FailureReason);
        Assert.True(result.LatencyMs >= 1000);
    }

    [Fact]
    public async Task HealthCheckAsync_WhenListingFails_ReturnsListingFailed()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        var callCount = 0;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: PROPFIND Depth:0 (Health Check)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            <?xml version="1.0" encoding="utf-8"?>
                            <D:multistatus xmlns:D="DAV:">
                              <D:response>
                                <D:href>/</D:href>
                                <D:propstat>
                                  <D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                                  <D:status>HTTP/1.1 200 OK</D:status>
                                </D:propstat>
                              </D:response>
                            </D:multistatus>
                            """)
                    };
                }
                else
                {
                    // Second call: PROPFIND Depth:1 (ListDirectory)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            });

        using var service = CreateService(handlerMock.Object);

        // Act
        var result = await service.HealthCheckAsync();

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal(HealthCheckFailure.ListingFailed, result.FailureReason);
    }

    [Fact]
    public async Task HealthCheckAsync_WhenHealthy_ReturnsSuccess()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <?xml version="1.0" encoding="utf-8"?>
                    <D:multistatus xmlns:D="DAV:">
                      <D:response>
                        <D:href>/</D:href>
                        <D:propstat>
                          <D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                          <D:status>HTTP/1.1 200 OK</D:status>
                        </D:propstat>
                      </D:response>
                    </D:multistatus>
                    """)
            });

        using var service = CreateService(handlerMock.Object);

        // Act
        var result = await service.HealthCheckAsync();

        // Assert
        Assert.True(result.IsHealthy);
        Assert.Equal(HealthCheckFailure.None, result.FailureReason);
        Assert.True(result.LatencyMs < 1000);
    }
}