using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.DependencyInjection;
using B3.EntryPoint.Client.DropCopy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.EntryPoint.Client.Tests.DependencyInjection;

public class EntryPointClientServiceCollectionExtensionsTests
{
    private static void ConfigureValid(EntryPointClientOptions o, ushort port = 9000, uint sessionId = 42u)
    {
        o.Endpoint = new IPEndPoint(IPAddress.Loopback, port);
        o.SessionId = sessionId;
        o.SessionVerId = 1u;
        o.EnteringFirm = 7u;
        o.Credentials = Credentials.FromUtf8("k");
    }

    [Fact]
    public async Task AddEntryPointClient_RegistersSingleton()
    {
        var services = new ServiceCollection();
        services.AddEntryPointClient(o => ConfigureValid(o));

        var sp = services.BuildServiceProvider();
        try
        {
            var a = sp.GetRequiredService<EntryPointClient>();
            var b = sp.GetRequiredService<EntryPointClient>();
            Assert.Same(a, b);
        }
        finally { await ((IAsyncDisposable)sp).DisposeAsync(); }
    }

    [Fact]
    public async Task AddEntryPointClient_AppliesOptions()
    {
        var services = new ServiceCollection();
        services.AddEntryPointClient(o =>
        {
            ConfigureValid(o, sessionId: 99u);
            o.KeepAliveIntervalMs = 7777u;
        });

        var sp = services.BuildServiceProvider();
        try
        {
            var opts = sp.GetRequiredService<IOptions<EntryPointClientOptions>>().Value;
            Assert.Equal(99u, opts.SessionId);
            Assert.Equal(7777u, opts.KeepAliveIntervalMs);
        }
        finally { await ((IAsyncDisposable)sp).DisposeAsync(); }
    }

    [Fact]
    public void AddEntryPointClient_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            EntryPointClientServiceCollectionExtensions.AddEntryPointClient(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddEntryPointClient(null!));
    }

    [Fact]
    public async Task AddEntryPointClient_ValidatesOnResolve()
    {
        var services = new ServiceCollection();
        services.AddEntryPointClient(_ => { /* leave defaults: missing required fields */ });

        var sp = services.BuildServiceProvider();
        try
        {
            Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<EntryPointClient>());
        }
        finally { await ((IAsyncDisposable)sp).DisposeAsync(); }
    }

    [Fact]
    public async Task AddDropCopyClient_RegistersSingleton_WithSeparateNamedOptions()
    {
        var services = new ServiceCollection();
        services.AddEntryPointClient(o => ConfigureValid(o, port: 9000, sessionId: 1u));
        services.AddDropCopyClient(o => ConfigureValid(o, port: 9001, sessionId: 2u));

        var sp = services.BuildServiceProvider();
        try
        {
            Assert.NotNull(sp.GetRequiredService<EntryPointClient>());
            Assert.NotNull(sp.GetRequiredService<DropCopyClient>());

            var monitor = sp.GetRequiredService<IOptionsMonitor<EntryPointClientOptions>>();
            Assert.Equal(1u, monitor.Get(Microsoft.Extensions.Options.Options.DefaultName).SessionId);
            Assert.Equal(2u, monitor.Get(EntryPointClientServiceCollectionExtensions.DropCopyOptionsName).SessionId);
        }
        finally { await ((IAsyncDisposable)sp).DisposeAsync(); }
    }

    [Fact]
    public void AddDropCopyClient_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            EntryPointClientServiceCollectionExtensions.AddDropCopyClient(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddDropCopyClient(null!));
    }
}
