using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.DropCopy;

namespace B3.EntryPoint.Client.Tests;

public class EntryPointClientConstructorTests
{
    private static EntryPointClientOptions Valid() => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 9000),
        SessionId = 1u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
    };

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EntryPointClient(null!));
    }

    [Fact]
    public void Ctor_MissingEndpoint_Throws()
    {
        var opts = Valid();
        opts.Endpoint = null!;
        var ex = Assert.Throws<ArgumentException>(() => new EntryPointClient(opts));
        Assert.Contains("Endpoint", ex.Message);
    }

    [Fact]
    public void Ctor_MissingCredentials_Throws()
    {
        var opts = Valid();
        opts.Credentials = null!;
        var ex = Assert.Throws<ArgumentException>(() => new EntryPointClient(opts));
        Assert.Contains("Credentials", ex.Message);
    }

    [Fact]
    public void Ctor_ZeroSessionId_Throws()
    {
        var opts = Valid();
        opts.SessionId = 0u;
        var ex = Assert.Throws<ArgumentException>(() => new EntryPointClient(opts));
        Assert.Contains("SessionId", ex.Message);
    }

    [Fact]
    public void Ctor_ZeroEnteringFirm_Throws()
    {
        var opts = Valid();
        opts.EnteringFirm = 0u;
        var ex = Assert.Throws<ArgumentException>(() => new EntryPointClient(opts));
        Assert.Contains("EnteringFirm", ex.Message);
    }

    [Fact]
    public void Ctor_ValidOptions_Succeeds()
    {
        var client = new EntryPointClient(Valid());
        Assert.NotNull(client);
    }

    [Fact]
    public void DropCopyCtor_MissingEndpoint_Throws()
    {
        var opts = Valid();
        opts.Profile = SessionProfile.DropCopy;
        opts.Endpoint = null!;
        Assert.Throws<ArgumentException>(() => new DropCopyClient(opts));
    }

    [Fact]
    public void DropCopyCtor_WrongProfile_Throws()
    {
        var opts = Valid(); // Profile defaults to OrderEntry.
        var ex = Assert.Throws<ArgumentException>(() => new DropCopyClient(opts));
        Assert.Contains("DropCopy", ex.Message);
    }
}
