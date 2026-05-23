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
}
