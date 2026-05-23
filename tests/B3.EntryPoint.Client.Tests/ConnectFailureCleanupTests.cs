using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Audit regression: when <c>ConnectOnceAsync</c> fails after the TCP socket
/// is established (e.g. peer rejects Establish), the client must dispose the
/// socket, FIXP session, persistence worker, keep-alive scheduler and inbound
/// loop before propagating the exception to the outer retry loop. Without
/// this cleanup, a subsequent retry (or successful reconnect) would orphan
/// the previously-allocated resources because <c>DisposeAsync</c> only frees
/// the *currently bound* fields.
/// </summary>
public class ConnectFailureCleanupTests
{
    private static EntryPointClientOptions Options(InProcessFixpTestPeer peer) => new()
    {
        Endpoint = peer.LocalEndpoint,
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
        KeepAliveIntervalMs = 60_000u,
        ConnectMaxAttempts = 1,
        SessionTeardownTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task ConnectAsync_WhenEstablishRejected_DisposesAllPartialResources()
    {
        await using var peer = new InProcessFixpTestPeer();
        // Reject the very first Establish — TCP connect + Negotiate succeed,
        // EstablishAsync throws FixpRejectedException, which must trigger the
        // partial-connect cleanup before propagating.
        peer.Options.EstablishRejectAfter = 1;
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));

        await Assert.ThrowsAsync<FixpEstablishRejectedException>(() => client.ConnectAsync());

        // Internal state must be fully reset: _session and _tcp both null,
        // and the public State accessor must report Disconnected (which it
        // derives from _session). On unfixed code _session/_tcp would still
        // reference the orphaned partial connect.
        Assert.False(client.HasActiveSessionForTesting,
            "ConnectOnceAsync must release _session/_tcp on failure.");
        Assert.Equal(FixpClientState.Disconnected, client.State);
        Assert.Null(client.KeepAlive);
        Assert.Null(client.Retransmit);
    }

    [Fact]
    public async Task ConnectAsync_AfterFailedAttempt_CanReconnectWithoutLeakingPriorSocket()
    {
        await using var peer = new InProcessFixpTestPeer();
        // First Establish rejected; second accepted.
        peer.Options.EstablishRejectAfter = 1;
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));

        await Assert.ThrowsAsync<FixpEstablishRejectedException>(() => client.ConnectAsync());
        Assert.False(client.HasActiveSessionForTesting);

        // Flip the peer back to accept-mode and reconnect. If the prior
        // failure had leaked a TcpClient/inbound loop, this second connect
        // would either reuse stale fields (InvalidOperationException because
        // _session is non-null) or simply orphan them silently. The fix
        // ensures a clean slate.
        peer.Options.EstablishRejectAfter = null;
        await client.ConnectAsync();
        Assert.True(client.HasActiveSessionForTesting);
        Assert.Equal(FixpClientState.Established, client.State);
    }
}
