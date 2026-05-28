using System.Net;
using System.Threading.Channels;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Fixp;

/// <summary>
/// Regression coverage for issue #187 — when the underlying TCP transport is
/// closed by the peer (e.g. matching-platform restart) without a preceding
/// <c>Terminate</c> exchange, the session must transition out of
/// <see cref="FixpClientState.Established"/> so consumer code does not keep
/// issuing app frames against a dead wire.
/// </summary>
public class FixpClientSessionTransportClosedTests
{
    private static EntryPointClientOptions Options() => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
        SessionId = 1,
        SessionVerId = 1,
        EnteringFirm = 1,
        Credentials = Credentials.FromUtf8("k"),
    };

    /// <summary>
    /// Read returns 0 (EOF) immediately, mirroring a peer-side TCP FIN.
    /// Writes succeed silently so the session can be driven to Established
    /// via the test-only hook.
    /// </summary>
    private sealed class ClosedReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(0);
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task InboundLoop_PeerClose_TransitionsToTerminated_AndRaisesHook()
    {
        var stream = new ClosedReadStream();
        var session = new FixpClientSession(stream, Options());
        session.ForceEstablishedForTesting();
        Assert.Equal(FixpClientState.Established, session.State);

        var hookTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnTransportClosed = ex => hookTcs.TrySetResult(ex);

        var channel = Channel.CreateUnbounded<EntryPointEvent>();
        session.StartInboundLoop(channel.Writer);

        var fired = await hookTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(fired); // EndOfStream → no IOException carried

        Assert.Equal(FixpClientState.Terminated, session.State);

        // Channel writer must remain open — it is shared across reconnects
        // and only EntryPointClient.DisposeAsync may complete it. (#144)
        Assert.False(channel.Reader.Completion.IsCompleted);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task InboundLoop_UserDispose_DoesNotRaiseTransportClosed()
    {
        // Blocks until cancelled — simulates a healthy peer with no traffic.
        var stream = new BlockingReadStream();
        var session = new FixpClientSession(stream, Options());
        session.ForceEstablishedForTesting();

        var raised = 0;
        session.OnTransportClosed = _ => Interlocked.Increment(ref raised);

        var channel = Channel.CreateUnbounded<EntryPointEvent>();
        session.StartInboundLoop(channel.Writer);

        // Give the loop a moment to enter ReadAsync.
        await Task.Delay(50);

        await session.DisposeAsync();

        Assert.Equal(0, raised);
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
