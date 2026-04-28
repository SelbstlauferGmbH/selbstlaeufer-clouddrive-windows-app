using System.Text.Json;

namespace CloudDrive.Core.Tests.Infrastructure;

/// <summary>
/// Writes all captured log events to a JSON Lines file after each E2E test class runs.
///
/// Output: TestResults/session-YYYYMMDD-HHmmss-{id}.jsonl  (repo root)
///
/// Each line is one JSON object. The file starts with a "session-start" record,
/// contains one "log-event" record per InMemoryLogSink entry, and ends with a
/// "session-end" record that summarises the outcome.
///
/// This format lets AI agents (Claude Code, Codex) read a complete causal timeline
/// of what the test suite did. Pair it with the WebDAV server log (e2e-*-server.jsonl)
/// produced by run-e2e-local.ps1 for a full client+server view.
/// </summary>
public sealed class TestSessionLogger
{
    private readonly string _sessionId;
    private readonly DateTime _startUtc;

    public string OutputPath { get; }

    public TestSessionLogger(string label = "")
    {
        _startUtc = DateTime.UtcNow;
        var shortId = Guid.NewGuid().ToString("N")[..8];
        _sessionId = $"{_startUtc:yyyyMMdd-HHmmss}-{shortId}";

        var suffix = string.IsNullOrWhiteSpace(label) ? "" : $"-{label}";
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        OutputPath = Path.Combine(dir, $"session-{_sessionId}{suffix}.jsonl");
    }

    /// <summary>
    /// Serialises all events in <paramref name="logSink"/> to <see cref="OutputPath"/>.
    /// Call this from IAsyncLifetime.DisposeAsync after the test class finishes.
    /// </summary>
    public void Flush(InMemoryLogSink logSink, bool passed = true, string? failureDetail = null)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };

        using var writer = new StreamWriter(OutputPath, append: false, System.Text.Encoding.UTF8);

        writer.WriteLine(JsonSerializer.Serialize(new
        {
            ts         = _startUtc.ToString("o"),
            record     = "session-start",
            source     = "test-runner",
            session_id = _sessionId,
            webdav_url = Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_WEBDAV_URL"),
        }, opts));

        foreach (var e in logSink.Events)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                ts        = e.Timestamp.ToString("o"),
                record    = "log-event",
                source    = "test-client",
                level     = e.Level.ToString(),
                category  = ShortCategory(e.Category),
                message   = e.Message,
                exception = e.Exception?.ToString(),
            }, opts));
        }

        var errors = logSink.GetErrors();
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            ts             = DateTime.UtcNow.ToString("o"),
            record         = "session-end",
            source         = "test-runner",
            passed,
            total_events   = logSink.Events.Count,
            error_count    = errors.Count,
            failure_detail = failureDetail,
        }, opts));
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private static string ShortCategory(string category)
    {
        // "CloudDrive.Core.WebDav.WebDavService" → "WebDavService"
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private static string ResolveOutputDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0)
                return Path.Combine(dir, "TestResults");
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(Path.GetTempPath(), "clouddrive-test-results");
    }
}
