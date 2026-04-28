using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.WebDav;

public sealed record SyncCollectionResult(
    bool Supported,
    string? SyncToken,
    IReadOnlyList<RemoteItem> Items);

public sealed class SyncCollectionClient
{
    private static readonly XNamespace DavNs = "DAV:";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger _logger;

    public SyncCollectionClient(HttpClient httpClient, string baseUrl, ILogger logger)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
    }

    public async Task<SyncCollectionResult> ReportAsync(
        string remotePath,
        string? syncToken,
        int depth,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod("REPORT"), BuildUrl(remotePath));
        request.Headers.TryAddWithoutValidation("Depth", depth <= 1 ? "1" : "infinity");
        request.Content = new StringContent(
            BuildReportBody(syncToken),
            System.Text.Encoding.UTF8,
            "application/xml");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented
            or HttpStatusCode.Forbidden
            or HttpStatusCode.BadRequest)
        {
            _logger.LogDebug(
                "WebDAV sync-collection REPORT not supported for {RemotePath}: Status={StatusCode}",
                remotePath,
                (int)response.StatusCode);
            return new SyncCollectionResult(Supported: false, SyncToken: syncToken, Items: []);
        }

        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(xml, remotePath);
    }

    private static string BuildReportBody(string? syncToken)
    {
        var tokenElement = string.IsNullOrWhiteSpace(syncToken)
            ? "<D:sync-token/>"
            : $"<D:sync-token>{System.Security.SecurityElement.Escape(syncToken)}</D:sync-token>";

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:sync-collection xmlns:D="DAV:">
              {{tokenElement}}
              <D:sync-level>1</D:sync-level>
              <D:prop>
                <D:resourcetype/>
                <D:getcontentlength/>
                <D:getlastmodified/>
                <D:getetag/>
                <D:getcontenttype/>
                <D:displayname/>
              </D:prop>
            </D:sync-collection>
            """;
    }

    private SyncCollectionResult ParseResponse(string xml, string requestPath)
    {
        var doc = XDocument.Parse(xml);
        var token = doc.Descendants(DavNs + "sync-token").FirstOrDefault()?.Value;
        var items = new List<RemoteItem>();
        var baseUri = new Uri(_baseUrl);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var requestAbsPath = string.IsNullOrWhiteSpace(requestPath.Trim('/'))
            ? basePath
            : $"{basePath}/{requestPath.Trim('/')}";

        foreach (var response in doc.Descendants(DavNs + "response"))
        {
            var href = response.Element(DavNs + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var hrefPath = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                ? absolute.AbsolutePath
                : new Uri(baseUri, href).AbsolutePath;
            hrefPath = Uri.UnescapeDataString(hrefPath).TrimEnd('/');
            if (string.Equals(hrefPath, requestAbsPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var prop = response.Descendants(DavNs + "prop").FirstOrDefault();
            if (prop == null)
                continue;

            var resourceType = prop.Element(DavNs + "resourcetype");
            var isDirectory = resourceType?.Element(DavNs + "collection") != null;
            var remotePath = hrefPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
                ? hrefPath[basePath.Length..]
                : hrefPath;
            if (!remotePath.StartsWith('/'))
                remotePath = "/" + remotePath;

            var name = prop.Element(DavNs + "displayname")?.Value;
            if (string.IsNullOrWhiteSpace(name))
                name = remotePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;

            items.Add(new RemoteItem
            {
                Name = name,
                RemotePath = remotePath.TrimEnd('/'),
                IsDirectory = isDirectory,
                Size = long.TryParse(prop.Element(DavNs + "getcontentlength")?.Value, out var size) ? size : 0,
                LastModified = DateTime.TryParse(prop.Element(DavNs + "getlastmodified")?.Value, out var lastModified)
                    ? lastModified
                    : DateTime.MinValue,
                ETag = WebDavETag.Normalize(prop.Element(DavNs + "getetag")?.Value),
                ContentType = prop.Element(DavNs + "getcontenttype")?.Value
            });
        }

        return new SyncCollectionResult(Supported: true, token, items);
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
}
