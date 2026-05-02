using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.DependencyInjection;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Client.TestPeer.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace B3.EntryPoint.Client.TestPeer.Sample;

/// <summary>
/// Demonstrates the recommended generic-host wiring for downstream
/// integration test fixtures: the test peer is registered as a hosted
/// service so it starts/stops with the host, then the EntryPoint client
/// is registered against the peer's bound endpoint.
///
/// The wiring is intentionally two-phase — host.StartAsync() must
/// complete before LocalEndpoint can be read — which is the only
/// timing constraint callers need to know about.
/// </summary>
public class HostedSample
{
    [Fact]
    public async Task GenericHost_WiresPeerAndClient_RoundTrip()
    {
        // Phase 1: build a host that owns the test peer's lifecycle.
        // We deliberately do NOT register the EntryPointClient here —
        // the client needs the peer's LocalEndpoint, which only exists
        // after Start() has bound the listener.
        var peerHost = Host.CreateApplicationBuilder();
        peerHost.Services.AddInProcessFixpTestPeerHosted(o =>
        {
            o.Scenario = TestPeerScenarios.AcceptAll;
        });

        using var ctx = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = peerHost.Build();
        await host.StartAsync(ctx.Token);

        var peer = host.Services.GetRequiredService<InProcessFixpTestPeer>();
        Assert.NotNull(peer.LocalEndpoint);

        // Phase 2: with the listener bound, register and resolve the
        // client against peer.LocalEndpoint.
        var clientServices = new ServiceCollection();
        clientServices.AddEntryPointClient(o =>
        {
            o.Endpoint = peer.LocalEndpoint;
            o.SessionId = 1u;
            o.SessionVerId = 1u;
            o.EnteringFirm = 1234u;
            o.Credentials = Credentials.FromUtf8("demo-key");
            o.KeepAliveIntervalMs = 60_000u;
        });
        await using var clientProvider = clientServices.BuildServiceProvider();
        var client = clientProvider.GetRequiredService<IEntryPointClient>();

        await client.ConnectAsync(ctx.Token);
        Assert.Equal(FixpClientState.Established, client.State);

        await client.TerminateAsync(TerminationCode.Finished, ctx.Token);

        // Phase 3: explicitly verify the hosted-service path stops the peer.
        await host.StopAsync(ctx.Token);
    }
}
