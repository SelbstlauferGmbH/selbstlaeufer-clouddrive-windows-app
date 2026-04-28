using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Tests.Infrastructure;

/// <summary>
/// Streams every log event to a JSON Lines file in real-time as the test runs.
///
/// Purpose: InMemoryLogSink batches events and only writes them via TestSessionLogger.Flush()
/// at DisposeAsync. If a test crashes before that point, no client logs exist on disk.
/// This sink writes each event immediately so logs survive any failure mode.
///
/// Add alongside InMemoryLogSink in the LoggerFactory:
///   b.AddProvider(LogSink);
///   b.AddProvider(new JsonFileLogSink(path));
/// </summary>
internal sealed class JsonFileLogSink : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false };

    public string FilePath { get; }

    public JsonFileLogSink(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
        Emit(new
        {
            ts     = DateTime.UtcNow.ToString("o"),
            record = "stream-start",
            source = "test-client",
        });
    }

    public ILogger CreateLogger(string categoryName) =>
        new StreamingLogger(this, categoryName);

    public void Dispose()
    {
        try
        {
            Emit(new
            {
                ts     = DateTime.UtcNow.ToString("o"),
                record = "stream-end",
                source = "test-client",
            });
        }
        catch { /* best effort */ }
        _writer.Dispose();
    }

    internal void Emit(object entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOpts);
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    // ── inner logger ──────────────────────────────────────────────────────────

    private sealed class StreamingLogger(JsonFileLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Emit(new
            {
                ts        = DateTime.UtcNow.ToString("o"),
                record    = "log-event",
                source    = "test-client",
                level     = logLevel.ToString(),
                category  = ShortCategory(category),
                message   = formatter(state, exception),
                exception = exception?.ToString(),
            });
        }

        private static string ShortCategory(string cat)
        {
            var dot = cat.LastIndexOf('.');
            return dot >= 0 ? cat[(dot + 1)..] : cat;
        }
    }
}
