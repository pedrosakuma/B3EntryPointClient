using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Coverage for #173: <see cref="EntryPointClient.ReconnectAsync(ReconnectMode, System.Func{uint,uint}?, CancellationToken)"/>.
/// Spec-canonical reconnect should try <c>Establish</c> reusing the current
/// <c>SessionVerID</c> first and fall back to <c>Negotiate</c> only when the
/// gateway rejects with a recoverable code.
/// </summary>
public class ReconnectModeTests
{
    private static EntryPointClientOptions Options(InProcessFixpTestPeer peer) => new()
    {
        Endpoint = peer.LocalEndpoint,
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
        KeepAliveIntervalMs = 60_000u,
    };

    [Fact]
    public async Task Reconnect_EstablishReuseAccepted_ReturnsReattached_AndSurfacesAckCounters()
    {
        await using var peer = new InProcessFixpTestPeer();
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var outcome = await client.ReconnectAsync(ReconnectMode.EstablishReuseThenNegotiate, nextSessionVerIdSelector: null, cts.Token);

        Assert.Equal(ReconnectKind.Reattached, outcome.Kind);
        Assert.Equal(1u, outcome.SessionVerId);
        Assert.False(outcome.RetransmitWindowReady,
            "Default peer mirrors local counters → no gap → no retransmit window.");
    }

    [Fact]
    public async Task Reconnect_EstablishReuseRejectedUnnegotiated_FallsBackToNegotiate()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            RejectEstablishWithoutPriorNegotiate = true,
        });
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var outcome = await client.ReconnectAsync(
            ReconnectMode.EstablishReuseThenNegotiate,
            nextSessionVerIdSelector: prev => prev + 1u,
            cts.Token);

        Assert.Equal(ReconnectKind.Renegotiated, outcome.Kind);
        Assert.Equal(2u, outcome.SessionVerId);
    }

    [Fact]
    public async Task Reconnect_EstablishReuseRejectedRecoverable_InvokesSelectorOnceWithPreviousVerId()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            RejectEstablishWithoutPriorNegotiate = true,
        });
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var observed = new List<uint>();
        var outcome = await client.ReconnectAsync(
            ReconnectMode.EstablishReuseThenNegotiate,
            nextSessionVerIdSelector: prev =>
            {
                observed.Add(prev);
                return prev + 10u;
            },
            cts.Token);

        Assert.Equal(ReconnectKind.Renegotiated, outcome.Kind);
        Assert.Equal(new[] { 1u }, observed);
        Assert.Equal(11u, outcome.SessionVerId);
    }

    [Fact]
    public async Task Reconnect_AlwaysNegotiate_BumpsSessionVerIdViaSelector()
    {
        await using var peer = new InProcessFixpTestPeer();
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var outcome = await client.ReconnectAsync(
            ReconnectMode.AlwaysNegotiate,
            nextSessionVerIdSelector: prev => prev + 1u,
            cts.Token);

        Assert.Equal(ReconnectKind.Renegotiated, outcome.Kind);
        Assert.Equal(2u, outcome.SessionVerId);
        Assert.False(outcome.RetransmitWindowReady,
            "AlwaysNegotiate path resets counters on the wire → no in-band retransmit.");
    }

    [Fact]
    public async Task Reconnect_EstablishReuseAccepted_InboundGapDetected_FlagsRetransmitWindowReady()
    {
        // Force the peer's EstablishmentAck to advertise a NextSeqNo > what
        // the client has seen so far → inbound gap → RetransmitWindowReady.
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            EstablishAckNextSeqNoOverride = 42u,
        });
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var outcome = await client.ReconnectAsync(
            ReconnectMode.EstablishReuseThenNegotiate,
            nextSessionVerIdSelector: null,
            cts.Token);

        Assert.Equal(ReconnectKind.Reattached, outcome.Kind);
        Assert.True(outcome.RetransmitWindowReady,
            "Server's NextSeqNo ahead of client's last contiguous inbound → gap detected.");
        Assert.Equal(42ul, outcome.ServerNextSeqNoExpected);
    }

    [Fact]
    public async Task Reconnect_EstablishReuseAccepted_NoGap_DoesNotFlagRetransmitWindow()
    {
        await using var peer = new InProcessFixpTestPeer();
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var outcome = await client.ReconnectAsync(
            ReconnectMode.EstablishReuseThenNegotiate,
            nextSessionVerIdSelector: null,
            cts.Token);

        Assert.Equal(ReconnectKind.Reattached, outcome.Kind);
        Assert.False(outcome.RetransmitWindowReady);
    }

    [Fact]
    public async Task Reconnect_EstablishReuseRejectedHardCode_ThrowsWithoutFallback()
    {
        // CREDENTIALS is in the spec's "hard" reject bucket — no auto-renegotiate.
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            EstablishRejectAfter = 2,
            EstablishRejectCodeOverride = B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.CREDENTIALS,
        });
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var ex = await Assert.ThrowsAsync<FixpEstablishRejectedException>(
            () => client.ReconnectAsync(ReconnectMode.EstablishReuseThenNegotiate, null, cts.Token));
        Assert.Equal(B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.CREDENTIALS, ex.Code);
    }

    /// <summary>
    /// Regression for the GPT-5.5 review BLOCKER on PR #174: when the
    /// Establish-reuse attempt is rejected (recoverable), the rejected
    /// session's zero counters MUST NOT overwrite the good snapshot that
    /// was persisted when the prior live session was torn down. Otherwise
    /// a crash between reject and the fallback Negotiate would resurrect
    /// a corrupted state on restart.
    /// </summary>
    [Fact]
    public async Task Reconnect_EstablishReuseRejected_DoesNotCorruptPersistedSnapshot()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            RejectEstablishWithoutPriorNegotiate = true,
        });
        peer.Start();

        // Pre-seed the store with a snapshot that carries a non-zero outbound
        // counter so we can detect any clobber-to-zero on the rejected reuse
        // teardown.
        var store = new SnapshotRecordingStore
        {
            Preseeded = new B3.EntryPoint.Client.State.SessionSnapshot
            {
                SessionId = 42u,
                SessionVerId = 1u,
                LastOutboundSeqNum = 42u,
                LastInboundSeqNum = 7u,
                CapturedAt = DateTimeOffset.UtcNow,
            },
        };
        var options = Options(peer);
        options.SessionStateStore = store;
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var outcome = await client.ReconnectAsync(
            ReconnectMode.EstablishReuseThenNegotiate,
            nextSessionVerIdSelector: prev => prev + 1u,
            cts.Token);

        Assert.Equal(ReconnectKind.Renegotiated, outcome.Kind);

        // Snapshots persisted while still on SessionVerId=1 must NEVER carry
        // a smaller outbound counter than the pre-seeded value. A zero would
        // indicate the rejected-reuse session corrupted the snapshot.
        foreach (var snap in store.Saved)
        {
            if (snap.SessionVerId == 1u)
                Assert.True(snap.LastOutboundSeqNum >= 42u,
                    $"Snapshot for SessionVerId=1 dropped LastOutboundSeqNum to {snap.LastOutboundSeqNum} (expected >= 42). " +
                    "The rejected Establish-reuse session's zero counters were persisted, corrupting state.");
        }
    }

    /// <summary>
    /// Regression for the GPT-5.5 review SHOULD-FIX on PR #174: the
    /// Establish-reuse path must advertise <c>NextSeqNo =
    /// localLastAssignedOutbound + 1</c>, not hard-coded <c>1</c>. Hard-coded
    /// 1 looks like a rewind to the gateway, which echoes
    /// <c>LastIncomingSeqNo = 0</c> in the EstablishmentAck and tricks the
    /// client into flagging <see cref="ReconnectOutcome.RetransmitWindowReady"/>
    /// for a non-existent gap.
    /// </summary>
    [Fact]
    public async Task Reconnect_EstablishReuseAccepted_AfterAppFrames_AdvertisesLocalOutboundNextSeqNo()
    {
        await using var peer = new InProcessFixpTestPeer();
        peer.Start();

        // Pre-seed an outbound counter > 0 to simulate the post-app-frame
        // state. Hydration on ConnectAsync resumes the session counter to 42.
        var store = new SnapshotRecordingStore
        {
            Preseeded = new B3.EntryPoint.Client.State.SessionSnapshot
            {
                SessionId = 42u,
                SessionVerId = 1u,
                LastOutboundSeqNum = 42u,
                LastInboundSeqNum = 0u,
                CapturedAt = DateTimeOffset.UtcNow,
            },
        };
        var options = Options(peer);
        options.SessionStateStore = store;
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var outcome = await client.ReconnectAsync(
            ReconnectMode.EstablishReuseThenNegotiate,
            nextSessionVerIdSelector: null,
            cts.Token);

        Assert.Equal(ReconnectKind.Reattached, outcome.Kind);
        // Peer echoes LastIncomingSeqNo = request.NextSeqNo - 1. After fix
        // we advertise 43, so the peer reports 42 — matching local outbound,
        // no outbound gap.
        Assert.Equal(42ul, outcome.ServerLastIncomingSeqNoSeen);
        Assert.False(outcome.RetransmitWindowReady,
            "Both sides agree on the outbound count → no retransmit window.");
    }

    private sealed class SnapshotRecordingStore : B3.EntryPoint.Client.State.ISessionStateStore
    {
        public B3.EntryPoint.Client.State.SessionSnapshot? Preseeded { get; set; }
        public System.Collections.Concurrent.ConcurrentQueue<B3.EntryPoint.Client.State.SessionSnapshot> Saved { get; } = new();

        public ValueTask<B3.EntryPoint.Client.State.SessionSnapshot?> LoadAsync(CancellationToken ct = default)
            => new(Preseeded);
        public ValueTask SaveAsync(B3.EntryPoint.Client.State.SessionSnapshot snapshot, CancellationToken ct = default)
        {
            Saved.Enqueue(snapshot);
            Preseeded = snapshot;
            return default;
        }
        public ValueTask AppendDeltaAsync(B3.EntryPoint.Client.State.SessionDelta delta, CancellationToken ct = default) => default;
        public ValueTask<B3.EntryPoint.Client.State.SessionSnapshot?> ReplayAsync(CancellationToken ct = default) => new(Preseeded);
        public ValueTask CompactAsync(CancellationToken ct = default) => default;
    }
}
