using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.State;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Coverage for #191: cold-start (process-restart) session resume. With
/// <see cref="ConnectMode.EstablishReuseThenNegotiate"/> a fresh process should
/// reattach the previously-negotiated session via <c>Establish</c> reusing the
/// persisted <c>SessionVerID</c> (no <c>Negotiate</c>) so venue-side order
/// ownership survives the restart, falling back to <c>Negotiate</c> only when
/// the peer rejects Establish or no usable snapshot exists. Also covers the
/// <see cref="EntryPointClientOptions.TerminateOnDispose"/> suspendable-shutdown
/// half.
/// </summary>
public class ColdStartResumeTests
{
    private const ushort NegotiateTemplateId = B3.Entrypoint.Fixp.Sbe.V6.NegotiateData.MESSAGE_ID;
    private const ushort EstablishTemplateId = B3.Entrypoint.Fixp.Sbe.V6.EstablishData.MESSAGE_ID;
    private const ushort TerminateTemplateId = B3.Entrypoint.Fixp.Sbe.V6.TerminateData.MESSAGE_ID;

    private static EntryPointClientOptions Options(InProcessFixpTestPeer peer) => new()
    {
        Endpoint = peer.LocalEndpoint,
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
        KeepAliveIntervalMs = 60_000u,
    };

    private static SessionSnapshot Snapshot(uint sessionId = 42u, uint sessionVerId = 7u,
        ulong lastOut = 5u, ulong lastIn = 3u) => new()
        {
            SessionId = sessionId,
            SessionVerId = sessionVerId,
            LastOutboundSeqNum = lastOut,
            LastInboundSeqNum = lastIn,
            CapturedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task ColdResume_WithUsableSnapshot_Reattaches_WithoutNegotiate_AndKeepsSessionVerId()
    {
        await using var peer = new InProcessFixpTestPeer();
        var sawNegotiate = 0;
        var sawEstablish = 0;
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == NegotiateTemplateId) Interlocked.Increment(ref sawNegotiate);
            if (e.TemplateId == EstablishTemplateId) Interlocked.Increment(ref sawEstablish);
        };
        peer.Start();

        var options = Options(peer);
        options.ConnectMode = ConnectMode.EstablishReuseThenNegotiate;
        options.SessionStateStore = new SnapshotStore(Snapshot(sessionVerId: 7u, lastOut: 5u));
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(cts.Token);

        Assert.Equal(0, sawNegotiate);
        Assert.Equal(1, sawEstablish);
        // Reused, not bumped.
        Assert.Equal(7u, options.SessionVerId);
    }

    [Fact]
    public async Task ColdResume_EstablishRejectedUnnegotiated_FallsBackToNegotiate_WithBumpedVerId()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            RejectEstablishWithoutPriorNegotiate = true,
        });
        var sawNegotiate = 0;
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == NegotiateTemplateId) Interlocked.Increment(ref sawNegotiate);
        };
        peer.Start();

        var options = Options(peer);
        options.ConnectMode = ConnectMode.EstablishReuseThenNegotiate;
        options.SessionStateStore = new SnapshotStore(Snapshot(sessionVerId: 7u));
        options.NextSessionVerIdSelector = prev => prev + 100u;
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(cts.Token);

        // The recoverable reject forced a Negotiate, and the verId was bumped
        // off the persisted (rejected) value via the selector.
        Assert.True(sawNegotiate >= 1);
        Assert.Equal(107u, options.SessionVerId);
    }

    [Fact]
    public async Task ColdResume_NoSnapshot_PlainNegotiate_NoVerIdChange()
    {
        await using var peer = new InProcessFixpTestPeer();
        var sawNegotiate = 0;
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == NegotiateTemplateId) Interlocked.Increment(ref sawNegotiate);
        };
        peer.Start();

        var options = Options(peer);
        options.ConnectMode = ConnectMode.EstablishReuseThenNegotiate;
        options.SessionStateStore = new SnapshotStore(null);
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(cts.Token);

        Assert.Equal(1, sawNegotiate);
        Assert.Equal(1u, options.SessionVerId);
    }

    [Fact]
    public async Task ColdResume_SessionIdMismatchSnapshot_PlainNegotiate()
    {
        await using var peer = new InProcessFixpTestPeer();
        var sawNegotiate = 0;
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == NegotiateTemplateId) Interlocked.Increment(ref sawNegotiate);
        };
        peer.Start();

        var options = Options(peer);
        options.ConnectMode = ConnectMode.EstablishReuseThenNegotiate;
        // Snapshot belongs to a different SessionId → not reattachable.
        options.SessionStateStore = new SnapshotStore(Snapshot(sessionId: 999u, sessionVerId: 7u));
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(cts.Token);

        Assert.Equal(1, sawNegotiate);
        Assert.Equal(1u, options.SessionVerId);
    }

    [Fact]
    public async Task ColdResume_SnapshotWithZeroVerId_PlainNegotiate()
    {
        await using var peer = new InProcessFixpTestPeer();
        var sawNegotiate = 0;
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == NegotiateTemplateId) Interlocked.Increment(ref sawNegotiate);
        };
        peer.Start();

        var options = Options(peer);
        options.ConnectMode = ConnectMode.EstablishReuseThenNegotiate;
        options.SessionStateStore = new SnapshotStore(Snapshot(sessionVerId: 0u));
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(cts.Token);

        Assert.Equal(1, sawNegotiate);
    }

    [Fact]
    public async Task DefaultConnectMode_AlwaysNegotiates_RegressionGuard()
    {
        await using var peer = new InProcessFixpTestPeer();
        var sawNegotiate = 0;
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == NegotiateTemplateId) Interlocked.Increment(ref sawNegotiate);
        };
        peer.Start();

        var options = Options(peer);
        // Default ConnectMode + a perfectly usable snapshot must STILL Negotiate.
        options.SessionStateStore = new SnapshotStore(Snapshot(sessionVerId: 7u));
        Assert.Equal(ConnectMode.NegotiateThenEstablish, options.ConnectMode);
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(cts.Token);

        Assert.Equal(1, sawNegotiate);
    }

    [Fact]
    public async Task TerminateOnDispose_False_DoesNotSendTerminate()
    {
        await using var peer = new InProcessFixpTestPeer();
        var sawTerminate = 0;
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == TerminateTemplateId) Interlocked.Increment(ref sawTerminate);
        };
        peer.Start();

        var options = Options(peer);
        options.TerminateOnDispose = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new EntryPointClient(options);
        await client.ConnectAsync(cts.Token);

        await client.DisposeAsync();

        // Give the peer a moment to observe any (unexpected) Terminate frame.
        await Task.Delay(200, cts.Token);
        Assert.Equal(0, sawTerminate);
    }

    [Fact]
    public async Task TerminateOnDispose_DefaultTrue_SendsTerminate()
    {
        await using var peer = new InProcessFixpTestPeer();
        var terminateSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MessageReceived += (_, e) =>
        {
            if (e.TemplateId == TerminateTemplateId) terminateSeen.TrySetResult();
        };
        peer.Start();

        var options = Options(peer);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new EntryPointClient(options);
        await client.ConnectAsync(cts.Token);

        await client.DisposeAsync();

        var completed = await Task.WhenAny(terminateSeen.Task, Task.Delay(2000, cts.Token));
        Assert.Same(terminateSeen.Task, completed);
    }

    [Fact]
    public async Task Reconnect_EstablishReuse_DoesNotEmitTerminate_EvenWhenTerminateOnDisposeTrue()
    {
        await using var peer = new InProcessFixpTestPeer();
        var terminatesDuringReconnect = 0;
        var reconnecting = false;
        peer.MessageReceived += (_, e) =>
        {
            if (reconnecting && e.TemplateId == TerminateTemplateId)
                Interlocked.Increment(ref terminatesDuringReconnect);
        };
        peer.Start();

        var options = Options(peer); // TerminateOnDispose defaults to true
        await using var client = new EntryPointClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        reconnecting = true;
        var outcome = await client.ReconnectAsync(
            ReconnectMode.EstablishReuseThenNegotiate, nextSessionVerIdSelector: null, cts.Token);
        await Task.Delay(150, cts.Token);
        reconnecting = false;

        Assert.Equal(ReconnectKind.Reattached, outcome.Kind);
        Assert.Equal(0, terminatesDuringReconnect);
    }

    private sealed class SnapshotStore : ISessionStateStore
    {
        private SessionSnapshot? _snapshot;
        public SnapshotStore(SessionSnapshot? snapshot) => _snapshot = snapshot;

        public ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default) => new(_snapshot);
        public ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default)
        {
            _snapshot = snapshot;
            return default;
        }
        public ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default) => default;
        public ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default) => new(_snapshot);
        public ValueTask CompactAsync(CancellationToken ct = default) => default;
    }
}
