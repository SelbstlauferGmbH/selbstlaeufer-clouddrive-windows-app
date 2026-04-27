using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDrive.Core.Tests.WebDav;

public sealed class RateLimiterTests
{
    [Fact]
    public async Task WaitAsync_AllowsConcurrentWaitersWithoutOverReleasingSemaphore()
    {
        var limiter = new RateLimiter(2, TimeSpan.FromMilliseconds(5), NullLogger.Instance);

        var tasks = Enumerable.Range(0, 25)
            .Select(_ => limiter.WaitAsync())
            .ToArray();

        await Task.WhenAll(tasks);
    }
}
