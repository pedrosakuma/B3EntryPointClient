using Microsoft.Extensions.Hosting;

namespace B3.EntryPoint.Client.TestPeer.DependencyInjection;

/// <summary>
/// Adapter that drives <see cref="InProcessFixpTestPeer"/> from the generic
/// host lifecycle: <c>Start()</c> on <see cref="StartAsync"/>,
/// <c>StopAsync(ct)</c> on <see cref="StopAsync"/>.
/// </summary>
internal sealed class InProcessFixpTestPeerHostedService : IHostedService
{
    private readonly InProcessFixpTestPeer _peer;

    public InProcessFixpTestPeerHostedService(InProcessFixpTestPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        _peer = peer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _peer.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => _peer.StopAsync(cancellationToken);
}
