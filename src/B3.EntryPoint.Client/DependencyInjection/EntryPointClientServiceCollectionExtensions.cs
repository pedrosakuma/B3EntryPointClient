using B3.EntryPoint.Client.DropCopy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace B3.EntryPoint.Client.DependencyInjection;

/// <summary>
/// <see cref="IServiceCollection"/> helpers that register
/// <see cref="EntryPointClient"/> and <see cref="DropCopyClient"/> using
/// the standard Options pattern. Both clients hold a long-lived TCP
/// connection and are registered as singletons.
/// </summary>
/// <remarks>
/// The caller owns the connection lifecycle: resolve the client and call
/// <c>ConnectAsync</c>. The DI container will dispose the singleton on
/// shutdown via <see cref="IAsyncDisposable"/>.
/// </remarks>
public static class EntryPointClientServiceCollectionExtensions
{
    /// <summary>Named-options key used by <see cref="AddDropCopyClient"/>.</summary>
    public const string DropCopyOptionsName = "B3.EntryPoint.DropCopy";

    /// <summary>
    /// Registers a singleton <see cref="EntryPointClient"/> configured via
    /// <paramref name="configure"/>. Options are validated on first resolve.
    /// </summary>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static IServiceCollection AddEntryPointClient(
        this IServiceCollection services,
        Action<EntryPointClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<EntryPointClientOptions>()
            .Configure(configure)
            .Validate(ValidateOptions, "Invalid EntryPointClientOptions: Endpoint, SessionId, EnteringFirm, and Credentials are required.");

        services.TryAddSingleton(static sp =>
            new EntryPointClient(sp.GetRequiredService<IOptions<EntryPointClientOptions>>().Value));
        services.TryAddSingleton<IEntryPointClient>(static sp => sp.GetRequiredService<EntryPointClient>());

        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="DropCopyClient"/> configured via
    /// <paramref name="configure"/>. Options are validated on first resolve.
    /// </summary>
    /// <remarks>
    /// Uses a separately-named options instance so an application can
    /// register both an Order-Entry client and a Drop-Copy client side
    /// by side without their options colliding.
    /// </remarks>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static IServiceCollection AddDropCopyClient(
        this IServiceCollection services,
        Action<EntryPointClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<EntryPointClientOptions>(DropCopyOptionsName)
            .Configure(configure)
            .PostConfigure(static o => o.Profile = SessionProfile.DropCopy)
            .Validate(ValidateOptions, "Invalid EntryPointClientOptions for DropCopyClient: Endpoint, SessionId, EnteringFirm, and Credentials are required.");

        services.TryAddSingleton(static sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<EntryPointClientOptions>>();
            return new DropCopyClient(monitor.Get(DropCopyOptionsName));
        });
        services.TryAddSingleton<IDropCopyClient>(static sp => sp.GetRequiredService<DropCopyClient>());

        return services;
    }

    private static bool ValidateOptions(EntryPointClientOptions options)
    {
        if (options is null) return false;
        if (options.Endpoint is null) return false;
        if (options.SessionId == 0u) return false;
        if (options.EnteringFirm == 0u) return false;
        if (options.Credentials is null) return false;
        return true;
    }
}
