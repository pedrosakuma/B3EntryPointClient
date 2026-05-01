using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.TestPeer;

namespace B3.EntryPoint.Client.Tests;

public class TlsTransportTests
{
    private static EntryPointClientOptions Options(IPEndPoint endpoint, Action<TlsOptions>? configureTls = null)
    {
        var opts = new EntryPointClientOptions
        {
            Endpoint = endpoint,
            SessionId = 42u,
            SessionVerId = 1u,
            EnteringFirm = 7u,
            Credentials = Credentials.FromUtf8("test-key"),
            KeepAliveIntervalMs = 60_000u,
        };
        configureTls?.Invoke(opts.Tls);
        return opts;
    }

    [Fact]
    public void TlsOptions_DefaultsAreOff()
    {
        var opts = new EntryPointClientOptions();
        Assert.NotNull(opts.Tls);
        Assert.False(opts.Tls.Enabled);
        Assert.Null(opts.Tls.TargetHost);
        Assert.Null(opts.Tls.RemoteCertificateValidationCallback);
        Assert.Null(opts.Tls.ClientCertificates);
        Assert.Equal(SslProtocols.None, opts.Tls.EnabledSslProtocols);
    }

    [Fact]
    public void Ctor_ClientCertificatesWithoutEnabled_Throws()
    {
        var opts = Options(new IPEndPoint(IPAddress.Loopback, 9000));
        opts.Tls.ClientCertificates = new X509CertificateCollection { CreateSelfSigned("client") };
        var ex = Assert.Throws<ArgumentException>(() => new EntryPointClient(opts));
        Assert.Contains("ClientCertificates", ex.Message);
        Assert.Contains("Enabled", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_OverTls_PerformsHandshakeAndEstablishes()
    {
        using var serverCert = CreateSelfSigned("localhost");
        await using var peer = new InMemoryFixpPeer(serverCert);
        peer.Start();

        var opts = Options(peer.Endpoint, tls =>
        {
            tls.Enabled = true;
            tls.TargetHost = "localhost";
            tls.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        });

        await using var client = new EntryPointClient(opts);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        Assert.Equal(FixpClientState.Established, client.State);
    }

    [Fact]
    public async Task ConnectAsync_TlsDisabled_StillConnectsToPlainPeer()
    {
        await using var peer = new InMemoryFixpPeer();
        peer.Start();

        var opts = Options(peer.Endpoint);
        await using var client = new EntryPointClient(opts);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        Assert.Equal(FixpClientState.Established, client.State);
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(subject);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Re-import with private key persisted (Windows quirk; harmless on Linux).
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
    }
}
