using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Tests.Infrastructure;

/// <summary>
/// Captures structured log events in memory for test assertions.
/// Provides event-driven waiting (no file polling).
/// </summary>
public sealed class InMemoryLogSink : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Events { get; } = new();
    private readonly SemaphoreSlim _eventSignal = new(0);

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void Dispose() => _eventSignal.Dispose();

    internal void Add(LogEntry entry)
    {
        Events.Enqueue(entry);
        _eventSignal.Release();
    }

    /// <summary>Wait for a log event matching the predicate. Event-driven, no polling.</summary>
    public async Task<LogEntry> WaitForAsync(
        Func<LogEntry, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        // Check existing events first
        foreach (var e in Events)
            if (predicate(e)) return e;

        // Wait for new events
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            if (await _eventSignal.WaitAsync(remaining))
            {
                // Check all events (new ones added since last check)
                foreach (var e in Events)
                    if (predicate(e)) return e;
            }
        }

        throw new TimeoutException(
            $"Timed out waiting for matching log event after {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>Query events by message substring.</summary>
    public List<LogEntry> GetByMessage(string contains) =>
        Events.Where(e => e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Get all error-level events.</summary>
    public List<LogEntry> GetErrors() =>
        Events.Where(e => e.Level >= LogLevel.Error).ToList();

    private sealed class SinkLogger(InMemoryLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Add(new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = logLevel,
                Category = category,
                Message = formatter(state, exception),
                Exception = exception,
                EventId = eventId
            });
        }
    }
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public Exception? Exception { get; init; }
    public EventId EventId { get; init; }
}
