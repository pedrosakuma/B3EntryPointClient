using System.Collections.Concurrent;
using System.Net;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests.Fixp;

/// <summary>
/// Regression coverage for #211 — concurrent outbound application-frame sends
/// must not race on the underlying transport <c>Stream</c>.
/// </summary>
public class ConcurrentSendOrderingTests
{
    /// <summary>
    /// A <see cref="MemoryStream"/> whose <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
    /// artificially stalls before completing whenever the buffer's first byte
    /// is <see cref="SlowMarker"/>. This lets a test start a "slow" write
    /// first and a "fast" write second, and observe whether the underlying
    /// stream still receives them in call order (correct, serialized) or in
    /// completion order (buggy, racy) — exactly the class of bug in #211.
    /// </summary>
    private sealed class DelayableStream : MemoryStream
    {
        public const byte SlowMarker = 0xAA;
        public TimeSpan Delay = TimeSpan.FromMilliseconds(200);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count > 0 && buffer[offset] == SlowMarker)
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > 0 && buffer.Span[0] == SlowMarker)
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static EntryPointClientOptions SessionOptions() => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
        SessionId = 1,
        SessionVerId = 1,
        EnteringFirm = 1,
        Credentials = Credentials.FromUtf8("k"),
    };

    /// <summary>
    /// Without write-side serialization, a "slow" frame started first can
    /// lose the race to a "fast" frame started second and land on the wire
    /// out of call order (or interleaved with it). #211's fix wraps every
    /// <see cref="FixpClientSession"/> outbound write in a lock, so the slow
    /// frame's bytes must always precede the fast frame's bytes in the
    /// underlying stream regardless of which write completes first.
    /// </summary>
    [Fact]
    public async Task SendApplicationFrameAsync_SerializesConcurrentWrites_PreservesCallOrder()
    {
        var stream = new DelayableStream();
        var session = new FixpClientSession(stream, SessionOptions());
        session.ForceEstablishedForTesting();

        var slowFrame = Enumerable.Repeat(DelayableStream.SlowMarker, 32).ToArray();
        var fastFrame = Enumerable.Repeat((byte)0xBB, 32).ToArray();

        var slowTask = session.SendApplicationFrameAsync(slowFrame, slowFrame.Length, CancellationToken.None);
        // Give the slow call a head start so it acquires the write lock (and
        // enters its artificial delay) before the fast call is issued.
        await Task.Delay(50);
        var fastTask = session.SendApplicationFrameAsync(fastFrame, fastFrame.Length, CancellationToken.None);

        await Task.WhenAll(slowTask, fastTask);

        var written = stream.ToArray();
        Assert.Equal(slowFrame.Length + fastFrame.Length, written.Length);
        Assert.Equal(slowFrame, written[..slowFrame.Length]);
        Assert.Equal(fastFrame, written[slowFrame.Length..]);
    }

    private static EntryPointClientOptions ClientOptions(InProcessFixpTestPeer peer) => new()
    {
        Endpoint = peer.LocalEndpoint,
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("test-key"),
        KeepAliveIntervalMs = 60_000u,
    };

    /// <summary>
    /// End-to-end coverage over a real TCP loopback connection (mirroring
    /// B3TradingPlatform's "Cancel All" panic action): a burst of concurrent
    /// <see cref="EntryPointClient.CancelAsync"/> calls must land on the wire
    /// with strictly increasing, gap-free MsgSeqNum and no decode failures.
    /// </summary>
    [Fact]
    public async Task ConcurrentCancelAsync_ArrivesInGapFreeSeqOrder()
    {
        await using var peer = new InProcessFixpTestPeer();
        var received = new ConcurrentQueue<(uint MsgSeqNum, string ClOrdId)>();
        var decodeFailures = 0;

        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId != OrderCancelRequestData.MESSAGE_ID)
                return;
            if (!OrderCancelRequestData.TryParse(e.Payload.Span, out var reader))
            {
                Interlocked.Increment(ref decodeFailures);
                return;
            }
            ref readonly var req = ref reader.Data;
            received.Enqueue((req.BusinessHeader.MsgSeqNum.Value, req.ClOrdID.Value.ToString()));
        };
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connectCts.Token);

        const int concurrency = 8;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tasks = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            var clOrdId = (1000UL + (ulong)i).ToString();
            tasks[i] = client.CancelAsync(new CancelOrderRequest
            {
                ClOrdID = B3.EntryPoint.Client.Models.ClOrdID.Parse(clOrdId),
                OrigClOrdID = B3.EntryPoint.Client.Models.ClOrdID.Parse(clOrdId),
                SecurityId = 1,
                Side = B3.EntryPoint.Client.Models.Side.Buy,
            }, cts.Token);
        }
        await Task.WhenAll(tasks);

        // Give the peer's inbound loop a moment to drain the last frames.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (received.Count < concurrency && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(0, decodeFailures);
        Assert.Equal(concurrency, received.Count);

        var seqNums = received.Select(r => r.MsgSeqNum).ToArray();
        var sorted = seqNums.OrderBy(s => s).ToArray();
        // Gap-free and unique: every reserved seq number actually reached the
        // peer exactly once, with no corrupted/duplicated frames.
        for (int i = 1; i < sorted.Length; i++)
            Assert.Equal(sorted[i - 1] + 1, sorted[i]);
    }
}

