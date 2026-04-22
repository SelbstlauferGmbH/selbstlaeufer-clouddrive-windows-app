using System.Text;
using CloudDrive.Core.SyncEngine;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class HydrationTransferPlannerTests
{
    [Theory]
    [InlineData(217662L, 0L, 217662L, 217662L)]
    [InlineData(217662L, 0L, 300000L, 217662L)]
    [InlineData(217662L, 4096L, 300000L, 213566L)]
    [InlineData(217662L, 217662L, 4096L, 0L)]
    [InlineData(0L, 0L, 8192L, 8192L)]
    [InlineData(217662L, 4096L, -1L, 213566L)]
    public void ClampRequestedLength_ReturnsValidTransferLength(
        long fileSize,
        long requiredOffset,
        long requiredLength,
        long expectedLength)
    {
        var actual = HydrationTransferPlanner.ClampRequestedLength(fileSize, requiredOffset, requiredLength);

        actual.ShouldBe(expectedLength);
    }

    [Fact]
    public void CreateTransferRange_UsesOptionalRangeWhenItFullyCoversRequiredRange()
    {
        var actual = HydrationTransferPlanner.CreateTransferRange(
            fileSize: 1960764,
            requiredOffset: 32768,
            requiredLength: 8192,
            optionalOffset: 0,
            optionalLength: 65536);

        actual.ShouldBe((0L, 65536L));
    }

    [Fact]
    public async Task ReadAtLeastUntilTargetOrEofAsync_FillsBufferAcrossShortReads()
    {
        var stream = new PartialReadStream(Encoding.UTF8.GetBytes(new string('A', 10_000)), maxBytesPerRead: 733);
        var buffer = new byte[4096];

        var bytesRead = await HydrationTransferPlanner.ReadAtLeastUntilTargetOrEofAsync(
            stream,
            buffer,
            CancellationToken.None);

        bytesRead.ShouldBe(4096);
        buffer.ShouldAllBe(b => b == (byte)'A');
    }

    private sealed class PartialReadStream : MemoryStream
    {
        private readonly int _maxBytesPerRead;

        public PartialReadStream(byte[] buffer, int maxBytesPerRead)
            : base(buffer)
        {
            _maxBytesPerRead = maxBytesPerRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, _maxBytesPerRead));
        }

        public override int Read(Span<byte> buffer)
        {
            return base.Read(buffer[..Math.Min(buffer.Length, _maxBytesPerRead)]);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, _maxBytesPerRead)], cancellationToken);
        }
    }
}
