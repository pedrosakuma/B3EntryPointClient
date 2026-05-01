using B3.EntryPoint.Client.Framing;

namespace B3.EntryPoint.Client.Tests.Framing;

public class SofhFrameTests
{
    [Fact]
    public void WriteHeader_RoundTrips_LittleEndian()
    {
        Span<byte> buf = stackalloc byte[4];
        SofhFrameWriter.WriteHeader(buf, messageLength: 0x0102, encodingType: 0xEB50);

        Assert.Equal(0x02, buf[0]);
        Assert.Equal(0x01, buf[1]);
        Assert.Equal(0x50, buf[2]);
        Assert.Equal(0xEB, buf[3]);

        Assert.True(SofhFrameReader.TryParseHeader(buf, out var len, out var enc));
        Assert.Equal(0x0102, len);
        Assert.Equal(0xEB50, enc);
    }

    [Fact]
    public void TryParseHeader_ReturnsFalse_WhenBufferTooSmall()
    {
        Assert.False(SofhFrameReader.TryParseHeader(new byte[3], out _, out _));
    }

    [Fact]
    public void WriteHeader_Throws_WhenBufferTooSmall()
    {
        var buf = new byte[3];
        Assert.Throws<ArgumentException>(() => SofhFrameWriter.WriteHeader(buf, 10));
    }

    [Fact]
    public async Task ReadFrameAsync_ReadsCompleteFrame()
    {
        // 12-byte frame: 4 SOFH + 8 payload
        var frame = new byte[12];
        SofhFrameWriter.WriteHeader(frame, 12);
        for (var i = 4; i < 12; i++) frame[i] = (byte)i;

        using var ms = new MemoryStream(frame);
        var read = await SofhFrameReader.ReadFrameAsync(ms, CancellationToken.None);
        Assert.Equal(frame, read);
    }

    [Fact]
    public async Task ReadFrameAsync_Throws_WhenMessageLengthLessThanHeader()
    {
        var frame = new byte[4];
        SofhFrameWriter.WriteHeader(frame, 2);
        using var ms = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SofhFrameReader.ReadFrameAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrameAsync_Throws_WhenStreamEndsMidFrame()
    {
        var frame = new byte[6];
        SofhFrameWriter.WriteHeader(frame, 12); // claims 12, only 6 available
        using var ms = new MemoryStream(frame);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => SofhFrameReader.ReadFrameAsync(ms, CancellationToken.None));
    }
}
