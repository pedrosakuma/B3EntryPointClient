using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Tests.Fixp;

/// <summary>
/// Coverage for #123 — opt-in <see cref="EntryPointClientOptions.AutoFlushOutboundFrames"/>.
/// AutoFlush=true (default) flushes the underlying <see cref="Stream"/> per
/// frame; AutoFlush=false defers flushing to the explicit
/// <see cref="FixpClientSession.FlushOutboundAsync"/> path.
/// </summary>
public class FixpClientSessionAutoFlushTests
{
    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount;
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FlushCount);
            return Task.CompletedTask;
        }
        public override void Flush() => Interlocked.Increment(ref FlushCount);
    }

    private static EntryPointClientOptions Options(bool autoFlush) => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
        SessionId = 1,
        SessionVerId = 1,
        EnteringFirm = 1,
        Credentials = Credentials.FromUtf8("k"),
        AutoFlushOutboundFrames = autoFlush,
    };

    private static byte[] DummyFrame() => new byte[16];

    [Fact]
    public async Task AutoFlush_True_FlushesPerFrame()
    {
        var stream = new FlushCountingStream();
        var session = new FixpClientSession(stream, Options(autoFlush: true));
        session.ForceEstablishedForTesting();

        for (int i = 0; i < 3; i++)
            await session.SendApplicationFrameAsync(DummyFrame(), DummyFrame().Length, CancellationToken.None);

        Assert.Equal(3, stream.FlushCount);
    }

    [Fact]
    public async Task AutoFlush_False_DoesNotFlushPerFrame()
    {
        var stream = new FlushCountingStream();
        var session = new FixpClientSession(stream, Options(autoFlush: false));
        session.ForceEstablishedForTesting();

        for (int i = 0; i < 3; i++)
            await session.SendApplicationFrameAsync(DummyFrame(), DummyFrame().Length, CancellationToken.None);

        Assert.Equal(0, stream.FlushCount);

        await session.FlushOutboundAsync(CancellationToken.None);

        Assert.Equal(1, stream.FlushCount);
    }
}
