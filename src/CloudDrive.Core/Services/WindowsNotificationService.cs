using System.Collections.Concurrent;
using System.Security;
using CloudDrive.Core.Data;
using CloudDrive.Core.SyncEngine;
using Microsoft.Extensions.Logging;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CloudDrive.Core.Services;

public sealed class WindowsNotificationService : IDisposable
{
    private static readonly TimeSpan PerKeyRateLimit = TimeSpan.FromSeconds(30);

    private readonly ISyncProblemService _problemService;
    private readonly ILogger<WindowsNotificationService> _logger;
    private readonly string _appUserModelId;
    private readonly bool _enabled;
    private readonly ConcurrentDictionary<string, DateTime> _lastToastByKey = new(StringComparer.OrdinalIgnoreCase);

    public WindowsNotificationService(
        ISyncProblemService problemService,
        ILogger<WindowsNotificationService> logger,
        string appUserModelId,
        bool enabled)
    {
        _problemService = problemService;
        _logger = logger;
        _appUserModelId = appUserModelId;
        _enabled = enabled;

        _problemService.ProblemReported += OnProblemReported;
    }

    private void OnProblemReported(SyncProblem problem)
    {
        if (!_enabled || !ShouldNotify(problem))
            return;

        var key = problem.DedupeKey ?? $"{problem.ProblemType}:{problem.LocalPath}:{problem.RemotePath}";
        var now = DateTime.UtcNow;
        if (_lastToastByKey.TryGetValue(key, out var lastToast) &&
            now - lastToast < PerKeyRateLimit)
        {
            return;
        }

        _lastToastByKey[key] = now;

        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(BuildToastXml(problem, key));
            var notification = new ToastNotification(xml)
            {
                Tag = SafeTag(key),
                Group = "CloudDrive.SyncProblems"
            };

            ToastNotificationManager.CreateToastNotifier(_appUserModelId).Show(notification);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows toast notification could not be shown");
        }
    }

    private static bool ShouldNotify(SyncProblem problem)
    {
        if (problem.Severity != SyncProblemSeverity.Error &&
            problem.ProblemType != SyncProblemType.Conflict)
        {
            return false;
        }

        return problem.ProblemType is SyncProblemType.Conflict
            or SyncProblemType.Upload
            or SyncProblemType.Download
            or SyncProblemType.DiskFull
            or SyncProblemType.PermissionDenied;
    }

    private static string BuildToastXml(SyncProblem problem, string key)
    {
        var title = SecurityElement.Escape(problem.Title) ?? "CloudDrive";
        var summary = SecurityElement.Escape(problem.Summary) ?? string.Empty;
        var arguments = SecurityElement.Escape($"clouddrive://problems?dedupeKey={Uri.EscapeDataString(key)}");
        return $$"""
            <toast activationType="protocol" launch="{{arguments}}">
              <visual>
                <binding template="ToastGeneric">
                  <text>{{title}}</text>
                  <text>{{summary}}</text>
                </binding>
              </visual>
            </toast>
            """;
    }

    private static string SafeTag(string key)
    {
        var tag = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(key)).ToLowerInvariant();
        return tag.Length <= 64 ? tag : tag[..64];
    }

    public void Dispose()
    {
        _problemService.ProblemReported -= OnProblemReported;
    }
}
