using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace B3.EntryPoint.Client.TestPeer.DependencyInjection;

/// <summary>
/// <see cref="IServiceCollection"/> helpers that register
/// <see cref="InProcessFixpTestPeer"/> via the standard Options pattern.
/// </summary>
/// <remarks>
/// The peer holds a long-lived listening socket and is registered as a
/// singleton. Use <see cref="AddInProcessFixpTestPeerHosted"/> when running
/// inside a generic host so the peer is started/stopped automatically with
/// the host lifecycle. Use the bare <see cref="AddInProcessFixpTestPeer"/>
/// when the test fixture controls Start/Stop manually.
/// </remarks>
public static class InProcessFixpTestPeerServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="InProcessFixpTestPeer"/> configured
    /// via <paramref name="configure"/>. The caller is responsible for
    /// invoking <see cref="InProcessFixpTestPeer.Start"/> and
    /// <see cref="InProcessFixpTestPeer.StopAsync"/>; the singleton is
    /// disposed by the container on shutdown.
    /// </summary>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static IServiceCollection AddInProcessFixpTestPeer(
        this IServiceCollection services,
        Action<TestPeerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<TestPeerOptions>().Configure(configure);
        services.TryAddSingleton(static sp =>
            new InProcessFixpTestPeer(sp.GetRequiredService<IOptions<TestPeerOptions>>().Value));

        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="InProcessFixpTestPeer"/> plus an
    /// <see cref="IHostedService"/> that calls
    /// <see cref="InProcessFixpTestPeer.Start"/> on
    /// <see cref="IHostedService.StartAsync"/> and
    /// <see cref="InProcessFixpTestPeer.StopAsync"/> on
    /// <see cref="IHostedService.StopAsync"/>.
    /// </summary>
    /// <remarks>
    /// After <c>host.StartAsync()</c> returns, <c>peer.LocalEndpoint</c> is
    /// non-null and can be read to configure an
    /// <see cref="EntryPointClient"/> against it. See
    /// <c>docs/TEST-PEER.md</c> for an end-to-end snippet.
    /// </remarks>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static IServiceCollection AddInProcessFixpTestPeerHosted(
        this IServiceCollection services,
        Action<TestPeerOptions> configure)
    {
        services.AddInProcessFixpTestPeer(configure);
        services.AddHostedService<InProcessFixpTestPeerHostedService>();
        return services;
    }
}
