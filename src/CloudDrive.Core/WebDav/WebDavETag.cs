namespace CloudDrive.Core.WebDav;

public static class WebDavETag
{
    public static string? Normalize(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
            return null;

        var value = etag.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            value = value[2..].Trim();

        value = value.Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool Equals(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
}
