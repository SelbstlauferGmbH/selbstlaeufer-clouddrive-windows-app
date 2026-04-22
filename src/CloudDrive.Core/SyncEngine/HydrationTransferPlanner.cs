namespace CloudDrive.Core.SyncEngine;

internal static class HydrationTransferPlanner
{
    private const long CFEof = -1;

    public static long ClampRequestedLength(long fileSize, long requiredOffset, long requiredLength)
    {
        if (requiredOffset < 0)
            return 0;

        if (requiredLength == CFEof)
        {
            if (fileSize <= 0)
                return 0;

            return Math.Max(fileSize - requiredOffset, 0);
        }

        if (requiredLength <= 0)
            return 0;

        if (fileSize <= 0)
            return requiredLength;

        var remaining = fileSize - requiredOffset;
        if (remaining <= 0)
            return 0;

        return Math.Min(requiredLength, remaining);
    }

    public static long ClampOptionalLength(long fileSize, long optionalOffset, long optionalLength)
    {
        if (optionalOffset < 0)
            return 0;

        if (optionalLength == CFEof)
        {
            if (fileSize <= 0)
                return 0;

            return Math.Max(fileSize - optionalOffset, 0);
        }

        if (optionalLength <= 0)
            return 0;

        if (fileSize <= 0)
            return optionalLength;

        var remaining = fileSize - optionalOffset;
        if (remaining <= 0)
            return 0;

        return Math.Min(optionalLength, remaining);
    }

    public static (long Offset, long Length) CreateTransferRange(
        long fileSize,
        long requiredOffset,
        long requiredLength,
        long optionalOffset,
        long optionalLength)
    {
        var normalizedRequiredLength = ClampRequestedLength(fileSize, requiredOffset, requiredLength);
        if (normalizedRequiredLength <= 0)
            return (requiredOffset, 0);

        var normalizedOptionalLength = ClampOptionalLength(fileSize, optionalOffset, optionalLength);
        if (normalizedOptionalLength <= normalizedRequiredLength)
            return (requiredOffset, normalizedRequiredLength);

        var requiredEnd = requiredOffset + normalizedRequiredLength;
        var optionalEnd = optionalOffset + normalizedOptionalLength;
        if (optionalOffset > requiredOffset || optionalEnd < requiredEnd)
            return (requiredOffset, normalizedRequiredLength);

        return (optionalOffset, normalizedOptionalLength);
    }

    public static async Task<int> ReadAtLeastUntilTargetOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
                break;

            totalRead += read;
        }

        return totalRead;
    }
}
