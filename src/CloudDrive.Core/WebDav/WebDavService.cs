using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using CloudDrive.Core.Infrastructure;
using CloudDrive.Core.Localization;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.WebDav;

public class WebDavService : ISyncCollectionWebDavService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebDavService> _logger;
    private readonly string _baseUrl;
    private readonly IActivityTracker? _activityTracker;
    private readonly RateLimiter _rateLimiter;

    private static readonly XNamespace DavNs = "DAV:";
    private static readonly HttpStatusCode LockedStatusCode = (HttpStatusCode)423;

    public WebDavService(HttpClient httpClient, string baseUrl, ILogger<WebDavService> logger, IActivityTracker? activityTracker = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
        _activityTracker = activityTracker;
        _rateLimiter = new RateLimiter(maxRequests: 45, window: TimeSpan.FromSeconds(30), logger);
    }

    public async Task<IReadOnlyList<RemoteItem>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        var localizer = AppLocalizer.Instance;
        var activityId = Guid.NewGuid().ToString("N")[..8];
        var url = BuildUrl(remotePath);
        _logger.LogInformation("WebDav[ListDirectory] ActivityId={ActivityId} RemotePath={RemotePath} Url={Url}",
            activityId, remotePath, url);

        using var activityScope = _activityTracker?.Begin(
            $"listdir_{activityId}",
            ActivityCategory.WebDAV,
            localizer.Format("Activity_Listing", remotePath));

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:resourcetype/>
                <D:getcontentlength/>
                <D:getlastmodified/>
                <D:getetag/>
                <D:getcontenttype/>
                <D:displayname/>
              </D:prop>
            </D:propfind>
            """,
            System.Text.Encoding.UTF8,
            "application/xml");

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();

        _logger.LogInformation("WebDav[ListDirectory] ActivityId={ActivityId} Response={StatusCode} DurationMs={DurationMs}",
            activityId, (int)response.StatusCode, sw.ElapsedMilliseconds);

        try
        {
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var items = ParsePropfindResponse(responseBody, remotePath);

            _logger.LogInformation("WebDav[ListDirectory] ActivityId={ActivityId} ItemsReturned={ItemCount}",
                activityId, items.Count);

            activityScope?.CompleteWithStatus(ActivityStatus.Success);
            return items;
        }
        catch (Exception ex)
        {
            activityScope?.CompleteWithStatus(ActivityStatus.Failed);
            _activityTracker?.Record(
                ActivityCategory.WebDAV,
                localizer.Format("Activity_FailedToList", remotePath, ex.Message),
                ActivityStatus.Failed,
                sw.Elapsed);
            throw;
        }
    }

    public async Task<SyncCollectionResult> ReportSyncCollectionAsync(
        string remotePath,
        string? syncToken,
        int depth,
        CancellationToken ct = default)
    {
        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var client = new SyncCollectionClient(_httpClient, _baseUrl, _logger);
        var result = await client.ReportAsync(remotePath, syncToken, depth, ct);
        sw.Stop();

        _logger.LogInformation(
            "WebDav[SyncCollection] RemotePath={RemotePath} Depth={Depth} Supported={Supported} ItemsReturned={ItemCount} DurationMs={DurationMs}",
            remotePath,
            depth <= 1 ? 1 : -1,
            result.Supported,
            result.Items.Count,
            sw.ElapsedMilliseconds);

        return result;
    }

    public Task<Stream> DownloadFileAsync(string remotePath, CancellationToken ct = default) =>
        DownloadFileCoreAsync(remotePath, offset: 0, length: null, ct, bypassRateLimiter: false);

    public Task<Stream> DownloadFilePriorityAsync(string remotePath, CancellationToken ct = default) =>
        DownloadFileCoreAsync(remotePath, offset: 0, length: null, ct, bypassRateLimiter: true);

    public Task<Stream> DownloadFilePriorityAsync(string remotePath, long offset, long? length = null, CancellationToken ct = default) =>
        DownloadFileCoreAsync(remotePath, offset, length, ct, bypassRateLimiter: true);

    private async Task<Stream> DownloadFileCoreAsync(
        string remotePath,
        long offset,
        long? length,
        CancellationToken ct,
        bool bypassRateLimiter)
    {
        var localizer = AppLocalizer.Instance;
        var url = BuildUrl(remotePath);
        var operationLabel = bypassRateLimiter ? "GET (priority)" : "GET";
        var activityPrefix = bypassRateLimiter ? "hydrate" : "download";
        var activityText = localizer.Format(bypassRateLimiter ? "Activity_Hydrating" : "Activity_Downloading", remotePath);
        var isRangeRequest = offset > 0 || (length.HasValue && length.Value > 0);

        _logger.LogDebug(
            "WEBDAV_REQUEST {Operation} {Url} Offset={Offset} Length={Length}",
            operationLabel,
            url,
            offset,
            length);

        using var activityScope = _activityTracker?.Begin(
            $"{activityPrefix}_{remotePath.GetHashCode()}",
            ActivityCategory.WebDAV,
            activityText);

        if (!bypassRateLimiter)
            await _rateLimiter.WaitAsync(ct);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Translate", "f");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        if (isRangeRequest)
        {
            long? rangeEnd = length.HasValue && length.Value > 0
                ? offset + length.Value - 1
                : null;
            request.Headers.Range = new RangeHeaderValue(offset, rangeEnd);
        }

        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        sw.Stop();
        _logger.LogDebug(
            "WEBDAV_RESPONSE {Operation} {Url} {StatusCode} {DurationMs}ms ContentRange={ContentRange}",
            operationLabel,
            url,
            (int)response.StatusCode,
            sw.ElapsedMilliseconds,
            response.Content.Headers.ContentRange?.ToString() ?? "<none>");

        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await LogNotFoundResponseAsync(operationLabel, remotePath, response, ct);
                activityScope?.CompleteWithStatus(ActivityStatus.Failed);
                _activityTracker?.Record(
                    ActivityCategory.WebDAV,
                    localizer.Format("Activity_FileNotFoundOnServer", remotePath),
                    ActivityStatus.Failed,
                    sw.Elapsed);
                response.Dispose();
                throw new FileNotFoundException(localizer.Format("Exception_RemoteFileNotFound", remotePath));
            }

            response.EnsureSuccessStatusCode();
            activityScope?.CompleteWithStatus(ActivityStatus.Success);

            var stream = await response.Content.ReadAsStreamAsync(ct);
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                _logger.LogDebug(
                    "WEBDAV_RESPONSE {Operation} server ignored Range header; skipping to requested offset in-stream. RemotePath={RemotePath} Offset={Offset}",
                    operationLabel,
                    remotePath,
                    offset);
                await SkipBytesAsync(stream, offset, ct);
            }

            return stream;
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activityScope?.CompleteWithStatus(ActivityStatus.Failed);
            _activityTracker?.Record(
                ActivityCategory.WebDAV,
                localizer.Format("Activity_FailedToDownload", remotePath, ex.Message),
                ActivityStatus.Failed,
                sw.Elapsed);
            response.Dispose();
            throw;
        }
    }

    public Task<string?> UploadFileAsync(string remotePath, Stream content, CancellationToken ct = default) =>
        UploadFileAsync(remotePath, content, lockToken: null, ct);

    public async Task<string?> UploadFileAsync(string remotePath, Stream content, string? lockToken, CancellationToken ct = default)
    {
        var localizer = AppLocalizer.Instance;
        var url = BuildUrl(remotePath);
        _logger.LogDebug("WEBDAV_REQUEST PUT {Url}", url);

        using var activityScope = _activityTracker?.Begin(
            $"upload_{remotePath.GetHashCode()}",
            ActivityCategory.WebDAV,
            localizer.Format("Activity_Uploading", remotePath));

        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = streamContent
        };
        AddLockTokenIfPresent(request, lockToken);

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE PUT {Url} {StatusCode} {DurationMs}ms",
            url, (int)response.StatusCode, sw.ElapsedMilliseconds);

        try
        {
            ThrowIfLocked(remotePath, response);
            response.EnsureSuccessStatusCode();
            activityScope?.CompleteWithStatus(ActivityStatus.Success);
            return WebDavETag.Normalize(response.Headers.ETag?.Tag);
        }
        catch (Exception ex)
        {
            activityScope?.CompleteWithStatus(ActivityStatus.Failed);
            _activityTracker?.Record(
                ActivityCategory.WebDAV,
                localizer.Format("Activity_FailedToUpload", remotePath, ex.Message),
                ActivityStatus.Failed,
                sw.Elapsed);
            throw;
        }
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct = default) =>
        DeleteAsync(remotePath, lockToken: null, ct);

    public async Task DeleteAsync(string remotePath, string? lockToken, CancellationToken ct = default)
    {
        var localizer = AppLocalizer.Instance;
        var url = BuildUrl(remotePath);
        _logger.LogDebug("WEBDAV_REQUEST DELETE {Url}", url);

        using var activityScope = _activityTracker?.Begin(
            $"delete_{remotePath.GetHashCode()}",
            ActivityCategory.WebDAV,
            localizer.Format("Activity_Deleting", remotePath));

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddLockTokenIfPresent(request, lockToken);
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE DELETE {Url} {StatusCode} {DurationMs}ms",
            url, (int)response.StatusCode, sw.ElapsedMilliseconds);

        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                activityScope?.CompleteWithStatus(ActivityStatus.Success);
                return;
            }

            ThrowIfLocked(remotePath, response);
            response.EnsureSuccessStatusCode();
            activityScope?.CompleteWithStatus(ActivityStatus.Success);
        }
        catch (Exception ex)
        {
            activityScope?.CompleteWithStatus(ActivityStatus.Failed);
            _activityTracker?.Record(
                ActivityCategory.WebDAV,
                localizer.Format("Activity_FailedToDelete", remotePath, ex.Message),
                ActivityStatus.Failed,
                sw.Elapsed);
            throw;
        }
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default) =>
        MoveAsync(fromPath, toPath, lockToken: null, ct);

    public async Task MoveAsync(string fromPath, string toPath, string? lockToken, CancellationToken ct = default)
    {
        var fromUrl = BuildUrl(fromPath);
        var toUrl = BuildUrl(toPath);
        _logger.LogDebug("WEBDAV_REQUEST MOVE {From} -> {To}", fromUrl, toUrl);

        var request = new HttpRequestMessage(new HttpMethod("MOVE"), fromUrl);
        request.Headers.Add("Destination", toUrl);
        request.Headers.Add("Overwrite", "F");
        AddLockTokenIfPresent(request, lockToken);

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE MOVE {From} {StatusCode} {DurationMs}ms",
            fromUrl, (int)response.StatusCode, sw.ElapsedMilliseconds);
        ThrowIfLocked(fromPath, response);
        response.EnsureSuccessStatusCode();
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        var url = BuildUrl(remotePath);
        _logger.LogDebug("WEBDAV_REQUEST MKCOL {Url}", url);

        var request = new HttpRequestMessage(new HttpMethod("MKCOL"), url);
        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE MKCOL {Url} {StatusCode} {DurationMs}ms",
            url, (int)response.StatusCode, sw.ElapsedMilliseconds);

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            return;
        response.EnsureSuccessStatusCode();
    }

    public async Task<WebDavLockInfo> LockAsync(string remotePath, WebDavLockRequest request, CancellationToken ct = default)
    {
        var url = BuildUrl(remotePath);
        var timeout = NormalizeLockTimeout(request.Timeout);
        _logger.LogDebug("WEBDAV_REQUEST LOCK {Url} Timeout={TimeoutSeconds}s", url, (int)timeout.TotalSeconds);

        var body = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <D:lockinfo xmlns:D="DAV:">
          <D:lockscope><D:exclusive/></D:lockscope>
          <D:locktype><D:write/></D:locktype>
          <D:owner>{SecurityElementEscape(request.Owner)}</D:owner>
        </D:lockinfo>
        """;

        using var httpRequest = new HttpRequestMessage(new HttpMethod("LOCK"), url);
        httpRequest.Headers.TryAddWithoutValidation("Timeout", $"Second-{(int)timeout.TotalSeconds}");
        httpRequest.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml");

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(httpRequest, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE LOCK {Url} {StatusCode} {DurationMs}ms",
            url, (int)response.StatusCode, sw.ElapsedMilliseconds);

        ThrowIfLocked(remotePath, response);
        response.EnsureSuccessStatusCode();

        var token = await ReadLockTokenAsync(response, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new WebDavLockException(remotePath, response.StatusCode, $"WebDAV LOCK response did not contain a lock token for {remotePath}.");

        return new WebDavLockInfo(remotePath, NormalizeLockToken(token), DateTimeOffset.UtcNow.Add(timeout));
    }

    public async Task<WebDavLockInfo> RefreshLockAsync(string remotePath, string lockToken, TimeSpan timeout, CancellationToken ct = default)
    {
        var url = BuildUrl(remotePath);
        var normalizedToken = NormalizeLockToken(lockToken);
        var normalizedTimeout = NormalizeLockTimeout(timeout);
        _logger.LogDebug("WEBDAV_REQUEST LOCK refresh {Url} Timeout={TimeoutSeconds}s", url, (int)normalizedTimeout.TotalSeconds);

        using var request = new HttpRequestMessage(new HttpMethod("LOCK"), url);
        request.Headers.TryAddWithoutValidation("If", $"({normalizedToken})");
        request.Headers.TryAddWithoutValidation("Timeout", $"Second-{(int)normalizedTimeout.TotalSeconds}");

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE LOCK refresh {Url} {StatusCode} {DurationMs}ms",
            url, (int)response.StatusCode, sw.ElapsedMilliseconds);

        ThrowIfLocked(remotePath, response);
        response.EnsureSuccessStatusCode();

        return new WebDavLockInfo(remotePath, normalizedToken, DateTimeOffset.UtcNow.Add(normalizedTimeout));
    }

    public async Task UnlockAsync(string remotePath, string lockToken, CancellationToken ct = default)
    {
        var url = BuildUrl(remotePath);
        var normalizedToken = NormalizeLockToken(lockToken);
        _logger.LogDebug("WEBDAV_REQUEST UNLOCK {Url}", url);

        using var request = new HttpRequestMessage(new HttpMethod("UNLOCK"), url);
        request.Headers.TryAddWithoutValidation("Lock-Token", normalizedToken);

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE UNLOCK {Url} {StatusCode} {DurationMs}ms",
            url, (int)response.StatusCode, sw.ElapsedMilliseconds);

        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == LockedStatusCode)
            return;

        response.EnsureSuccessStatusCode();
    }

    public async Task<WebDavLockSupport> CheckLockSupportAsync(CancellationToken ct = default)
    {
        var probePath = $"/.clouddrive-lock-probe-{Guid.NewGuid():N}.tmp";
        WebDavLockInfo? lockInfo = null;
        var deleted = false;

        try
        {
            lockInfo = await LockAsync(
                probePath,
                new WebDavLockRequest("CloudDrive WebDAV lock capability probe", TimeSpan.FromSeconds(30)),
                ct);

            await using var content = new MemoryStream();
            await UploadFileAsync(probePath, content, lockInfo.Token, ct);

            if (!await RejectsUnprotectedWriteToLockedResourceAsync(probePath, ct))
            {
                await DeleteAsync(probePath, lockInfo.Token, ct);
                deleted = true;
                return WebDavLockSupport.Unsupported(
                    "WebDAV LOCK succeeded, but the server accepted an unprotected write to the locked probe resource.");
            }

            await DeleteAsync(probePath, lockInfo.Token, ct);
            deleted = true;

            return WebDavLockSupport.Supported("WebDAV LOCK enforcement, token-protected PUT, and token-protected DELETE succeeded.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (WebDavLockException ex)
        {
            return WebDavLockSupport.Unsupported(BuildLockSupportFailureDetail(ex));
        }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                ? WebDavLockSupport.Unsupported(BuildLockSupportFailureDetail(ex))
                : WebDavLockSupport.ProbeFailed(BuildLockSupportFailureDetail(ex));
        }
        catch (Exception ex)
        {
            return WebDavLockSupport.ProbeFailed(ex.Message);
        }
        finally
        {
            if (lockInfo != null)
            {
                if (!deleted)
                {
                    try { await DeleteAsync(probePath, lockInfo.Token, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete WebDAV lock probe resource: {RemotePath}", probePath); }
                }

                try { await UnlockAsync(probePath, lockInfo.Token, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to unlock WebDAV lock probe resource: {RemotePath}", probePath); }
            }
        }
    }

    private async Task<bool> RejectsUnprotectedWriteToLockedResourceAsync(string remotePath, CancellationToken ct)
    {
        var url = BuildUrl(remotePath);
        _logger.LogDebug("WEBDAV_REQUEST PUT lock enforcement probe {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(new byte[] { 0x43, 0x44 })
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        await _rateLimiter.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        sw.Stop();
        _logger.LogDebug("WEBDAV_RESPONSE PUT lock enforcement probe {Url} {StatusCode} {DurationMs}ms",
            url, (int)response.StatusCode, sw.ElapsedMilliseconds);

        if (response.StatusCode == LockedStatusCode || response.StatusCode == HttpStatusCode.PreconditionFailed)
            return true;

        if (response.IsSuccessStatusCode)
            return false;

        response.EnsureSuccessStatusCode();
        return false;
    }

    public async Task<RemoteItem?> GetPropertiesAsync(string remotePath, CancellationToken ct = default)
    {
        var url = BuildUrl(remotePath);

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        request.Headers.Add("Depth", "0");
        request.Content = new StringContent(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:resourcetype/>
                <D:getcontentlength/>
                <D:getlastmodified/>
                <D:getetag/>
                <D:getcontenttype/>
              </D:prop>
            </D:propfind>
            """,
            System.Text.Encoding.UTF8,
            "application/xml");

        await _rateLimiter.WaitAsync(ct);
        var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var items = ParsePropfindResponse(responseBody, remotePath, skipSelf: false);
        return items.Count > 0 ? items[0] : null;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl);
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <D:propfind xmlns:D="DAV:">
                  <D:prop><D:resourcetype/></D:prop>
                </D:propfind>
                """,
                System.Text.Encoding.UTF8,
                "application/xml");

            await _rateLimiter.WaitAsync(ct);
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebDAV connection test failed");
            return false;
        }
    }

    public async Task<HealthCheckResult> HealthCheckAsync(CancellationToken ct = default)
    {
        var sw = new Stopwatch();
        try
        {
            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl);
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <D:propfind xmlns:D="DAV:">
                  <D:prop><D:resourcetype/></D:prop>
                </D:propfind>
                """,
                System.Text.Encoding.UTF8,
                "application/xml");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1000);

            sw.Start();
            var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Failure(
                    HealthCheckFailure.ServerUnreachable,
                    sw.ElapsedMilliseconds,
                    AppLocalizer.Instance.Format("Health_ServerRespondedWithStatus", (int)response.StatusCode));
            }

            if (sw.ElapsedMilliseconds >= 1000)
            {
                return HealthCheckResult.Failure(HealthCheckFailure.HighLatency, sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            var elapsedMs = Math.Max(1000, sw.ElapsedMilliseconds);
            return HealthCheckResult.Failure(
                HealthCheckFailure.HighLatency,
                elapsedMs,
                AppLocalizer.Instance.GetString("Health_RequestTimedOut"));
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is System.Net.Sockets.SocketException || ex is TaskCanceledException)
        {
            sw.Stop();
            return HealthCheckResult.Failure(HealthCheckFailure.ServerUnreachable, sw.ElapsedMilliseconds, ex.Message);
        }

        try
        {
            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl);
            request.Headers.Add("Depth", "1");
            request.Content = new StringContent(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <D:propfind xmlns:D="DAV:">
                  <D:prop>
                    <D:resourcetype/>
                    <D:displayname/>
                  </D:prop>
                </D:propfind>
                """,
                System.Text.Encoding.UTF8,
                "application/xml");

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            return HealthCheckResult.Success(sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Failure(HealthCheckFailure.ListingFailed, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private string BuildUrl(string remotePath)
    {
        remotePath = remotePath.Trim('/');
        if (string.IsNullOrEmpty(remotePath))
            return new Uri(_baseUrl).AbsoluteUri;

        var encodedRelativePath = string.Join("/",
            remotePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        var baseUri = new Uri($"{_baseUrl}/");
        return new Uri(baseUri, encodedRelativePath).AbsoluteUri;
    }

    private static void AddLockTokenIfPresent(HttpRequestMessage request, string? lockToken)
    {
        if (string.IsNullOrWhiteSpace(lockToken))
            return;

        request.Headers.TryAddWithoutValidation("If", $"({NormalizeLockToken(lockToken)})");
    }

    private static void ThrowIfLocked(string remotePath, HttpResponseMessage response)
    {
        if (response.StatusCode == LockedStatusCode)
            throw new WebDavLockedException(remotePath, $"WebDAV resource is locked: {remotePath}");
    }

    private static TimeSpan NormalizeLockTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return TimeSpan.FromMinutes(10);

        return timeout < TimeSpan.FromSeconds(30)
            ? TimeSpan.FromSeconds(30)
            : timeout;
    }

    private static string NormalizeLockToken(string lockToken)
    {
        var trimmed = lockToken.Trim();
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
            return trimmed;

        return $"<{trimmed.Trim('<', '>')}>";
    }

    private static string SecurityElementEscape(string value) =>
        System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static string BuildLockSupportFailureDetail(HttpRequestException ex)
    {
        return ex.StatusCode.HasValue
            ? $"WebDAV lock probe failed with HTTP {(int)ex.StatusCode.Value} {ex.StatusCode.Value}."
            : ex.Message;
    }

    private static async Task<string?> ReadLockTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Headers.TryGetValues("Lock-Token", out var tokenValues))
        {
            var token = tokenValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            var doc = XDocument.Parse(responseBody);
            return doc
                .Descendants(DavNs + "locktoken")
                .Elements(DavNs + "href")
                .Select(element => element.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
        catch
        {
            return null;
        }
    }

    private async Task LogNotFoundResponseAsync(
        string operation,
        string remotePath,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        string? bodyPreview = null;
        try
        {
            bodyPreview = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            bodyPreview = $"<failed to read response body: {ex.GetType().Name}>";
        }

        if (!string.IsNullOrEmpty(bodyPreview) && bodyPreview.Length > 400)
        {
            bodyPreview = bodyPreview[..400];
        }

        bodyPreview = bodyPreview?
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        _logger.LogWarning(
            "WEBDAV_NOT_FOUND {Operation} RemotePath={RemotePath} ReasonPhrase={ReasonPhrase} ContentType={ContentType} BodyPreview={BodyPreview}",
            operation,
            remotePath,
            response.ReasonPhrase ?? "<none>",
            response.Content.Headers.ContentType?.ToString() ?? "<none>",
            string.IsNullOrWhiteSpace(bodyPreview) ? "<empty>" : bodyPreview);
    }

    private static async Task SkipBytesAsync(Stream stream, long bytesToSkip, CancellationToken ct)
    {
        if (bytesToSkip <= 0)
            return;

        var buffer = new byte[Math.Min(81920, bytesToSkip)];
        long skipped = 0;

        while (skipped < bytesToSkip)
        {
            var requested = (int)Math.Min(buffer.Length, bytesToSkip - skipped);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), ct);
            if (read == 0)
                break;

            skipped += read;
        }
    }

    private List<RemoteItem> ParsePropfindResponse(string xml, string requestPath, bool skipSelf = true)
    {
        var items = new List<RemoteItem>();
        var doc = XDocument.Parse(xml);
        var responses = doc.Descendants(DavNs + "response");
        var baseUri = new Uri(_baseUrl);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');

        var relPath = requestPath.TrimStart('/').TrimEnd('/');
        var requestAbsPath = string.IsNullOrEmpty(relPath)
            ? basePath
            : $"{basePath}/{relPath}";

        foreach (var resp in responses)
        {
            var hrefRaw = resp.Element(DavNs + "href")?.Value ?? string.Empty;

            string hrefAbsPath;
            if (Uri.TryCreate(hrefRaw, UriKind.Absolute, out var absUri))
                hrefAbsPath = Uri.UnescapeDataString(absUri.AbsolutePath).TrimEnd('/');
            else
                hrefAbsPath = Uri.UnescapeDataString(new Uri(baseUri, hrefRaw).AbsolutePath).TrimEnd('/');

            if (skipSelf && string.Equals(hrefAbsPath, requestAbsPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var propStat = resp.Element(DavNs + "propstat");
            var prop = propStat?.Element(DavNs + "prop");
            if (prop == null)
                continue;

            var resourceType = prop.Element(DavNs + "resourcetype");
            var isDir = resourceType?.Element(DavNs + "collection") != null;

            var sizeStr = prop.Element(DavNs + "getcontentlength")?.Value;
            var lastModStr = prop.Element(DavNs + "getlastmodified")?.Value;
            var etag = prop.Element(DavNs + "getetag")?.Value;
            var contentType = prop.Element(DavNs + "getcontenttype")?.Value;
            var displayName = prop.Element(DavNs + "displayname")?.Value;

            var name = displayName;
            if (string.IsNullOrEmpty(name))
            {
                name = hrefAbsPath[(hrefAbsPath.LastIndexOf('/') + 1)..];
            }

            var itemPath = hrefAbsPath;
            if (itemPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                itemPath = itemPath[basePath.Length..];
            if (!itemPath.StartsWith('/'))
                itemPath = "/" + itemPath;

            items.Add(new RemoteItem
            {
                Name = name,
                RemotePath = itemPath.TrimEnd('/'),
                IsDirectory = isDir,
                Size = long.TryParse(sizeStr, out var size) ? size : 0,
                LastModified = DateTime.TryParse(lastModStr, out var lastMod) ? lastMod : DateTime.MinValue,
                ETag = WebDavETag.Normalize(etag),
                ContentType = contentType
            });
        }

        return items;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
