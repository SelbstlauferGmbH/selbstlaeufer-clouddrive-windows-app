using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public class RetryPolicy
{
    private readonly int _maxRetries;
    private readonly ILogger _logger;

    public RetryPolicy(int maxRetries, ILogger logger)
    {
        _maxRetries = maxRetries;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, string operationName, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                return await action();
            }
            catch (Exception ex) when (attempt < _maxRetries && IsTransient(ex, ct))
            {
                attempt++;
                var backoffSeconds = Math.Min(60, Math.Pow(2, attempt)); // Cap backoff to 60s
                var delay = TimeSpan.FromSeconds(backoffSeconds);
                _logger.LogWarning(ex, "{Operation} failed (transient error, attempt {Attempt}/{Max}), retrying in {Delay}s",
                    operationName, attempt, _maxRetries, delay.TotalSeconds);
                
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (TaskCanceledException)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }
    }

    public async Task ExecuteAsync(Func<Task> action, string operationName, CancellationToken ct)
    {
        await ExecuteAsync(async () => { await action(); return true; }, operationName, ct);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && !ct.IsCancellationRequested) return true; // HttpClient timeout (TaskCanceledException w/o correct token)
        if (ex is TimeoutException) return true;
        
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == null) return true; // Network error
            var code = (int)httpEx.StatusCode;
            return code == 429 || code >= 500;
        }
        
        return false;
    }
}
