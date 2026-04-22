using System.Collections.Concurrent;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncEngine;

public sealed class ActiveCloudRequestTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ActiveCloudRequestInfo>> _requestsByPath =
        new(StringComparer.OrdinalIgnoreCase);

    public void BeginFetchData(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var request = ActiveCloudRequestInfo.ForFetchData(callbackInfo, callbackParameters);
        Add(callbackInfo.NormalizedPath, request);
    }

    public void CompleteFetchData(string normalizedPath, string? completionReason = null)
    {
        RemoveByType(normalizedPath, ActiveCloudRequestKind.FetchData, completionReason);
    }

    public void BeginFetchPlaceholders(CF_CALLBACK_INFO callbackInfo)
    {
        var request = ActiveCloudRequestInfo.ForFetchPlaceholders(callbackInfo);
        Add(callbackInfo.NormalizedPath, request);
    }

    public void CompleteFetchPlaceholders(string normalizedPath, string? completionReason = null)
    {
        RemoveByType(normalizedPath, ActiveCloudRequestKind.FetchPlaceholders, completionReason);
    }

    public void MarkCancelFetchData(CF_CALLBACK_INFO callbackInfo)
    {
        RemoveByType(callbackInfo.NormalizedPath, ActiveCloudRequestKind.FetchData, "cancelled by Windows");
    }

    public IReadOnlyList<ActiveCloudRequestInfo> GetSnapshot(string normalizedPath)
    {
        if (_requestsByPath.TryGetValue(normalizedPath, out var requests))
            return requests.Values.OrderBy(r => r.StartedUtc).ToList();

        return [];
    }

    public int GetActiveCount(ActiveCloudRequestKind kind)
    {
        var count = 0;

        foreach (var requests in _requestsByPath.Values)
        {
            count += requests.Values.Count(r => r.Kind == kind);
        }

        return count;
    }

    private void Add(string normalizedPath, ActiveCloudRequestInfo request)
    {
        var requests = _requestsByPath.GetOrAdd(normalizedPath, _ => new ConcurrentDictionary<string, ActiveCloudRequestInfo>());
        requests[request.RequestId] = request;
    }

    private void RemoveByType(string normalizedPath, ActiveCloudRequestKind kind, string? completionReason)
    {
        if (!_requestsByPath.TryGetValue(normalizedPath, out var requests))
            return;

        foreach (var pair in requests)
        {
            if (pair.Value.Kind != kind)
                continue;

            requests.TryRemove(pair.Key, out _);
        }

        if (requests.IsEmpty)
            _requestsByPath.TryRemove(normalizedPath, out _);
    }
}

public sealed class ActiveCloudRequestInfo
{
    public required string RequestId { get; init; }
    public required ActiveCloudRequestKind Kind { get; init; }
    public required string NormalizedPath { get; init; }
    public required DateTime StartedUtc { get; init; }
    public long? RequiredOffset { get; init; }
    public long? RequiredLength { get; init; }

    public TimeSpan Age => DateTime.UtcNow - StartedUtc;

    public static ActiveCloudRequestInfo ForFetchData(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        return new ActiveCloudRequestInfo
        {
            RequestId = BuildRequestId(callbackInfo, ActiveCloudRequestKind.FetchData),
            Kind = ActiveCloudRequestKind.FetchData,
            NormalizedPath = callbackInfo.NormalizedPath,
            StartedUtc = DateTime.UtcNow,
            RequiredOffset = callbackParameters.FetchData.RequiredFileOffset,
            RequiredLength = callbackParameters.FetchData.RequiredLength
        };
    }

    public static ActiveCloudRequestInfo ForFetchPlaceholders(CF_CALLBACK_INFO callbackInfo)
    {
        return new ActiveCloudRequestInfo
        {
            RequestId = BuildRequestId(callbackInfo, ActiveCloudRequestKind.FetchPlaceholders),
            Kind = ActiveCloudRequestKind.FetchPlaceholders,
            NormalizedPath = callbackInfo.NormalizedPath,
            StartedUtc = DateTime.UtcNow
        };
    }

    private static string BuildRequestId(CF_CALLBACK_INFO callbackInfo, ActiveCloudRequestKind kind)
    {
        return $"{kind}:{callbackInfo.TransferKey.GetHashCode():X8}:{callbackInfo.RequestKey.GetHashCode():X8}";
    }
}

public enum ActiveCloudRequestKind
{
    FetchData,
    FetchPlaceholders
}
