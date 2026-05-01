using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace B3.EntryPoint.Client.TestPeer;

/// <summary>
/// Configuration for <see cref="InProcessFixpTestPeer"/>.
///
/// All properties are optional and default to "accept everything, no latency,
/// no TLS" so a bare <c>new InProcessFixpTestPeer()</c> behaves identically to
/// the in-process peer that powered this repo's conformance suite before
/// becoming a published package.
/// </summary>
public sealed class TestPeerOptions
{
    /// <summary>
    /// When non-null, every accepted TCP connection is wrapped in an
    /// <see cref="SslStream"/> using this certificate for the server-side TLS
    /// handshake. Use a runtime self-signed cert for tests and pair with
    /// <c>EntryPointClientOptions.Tls.RemoteCertificateValidationCallback</c>
    /// returning <c>true</c>.
    /// </summary>
    public X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// Artificial delay applied before the peer writes each outbound frame
    /// (handshake responses, ExecutionReports, Sequence heartbeats).
    /// Defaults to <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan ResponseLatency { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Strategy that decides what the peer responds to inbound application
    /// messages. Defaults to <see cref="TestPeerScenarios.AcceptAll"/>.
    /// </summary>
    public ITestPeerScenario Scenario { get; set; } = TestPeerScenarios.AcceptAll;

    /// <summary>
    /// Optional per-firm credential map (firm id → expected access-key bytes).
    /// When non-null the peer rejects Negotiate frames whose
    /// <c>EnteringFirm</c> is not in the map. When null (default) every firm
    /// is accepted, mirroring the historical behaviour.
    /// </summary>
    public IReadOnlyDictionary<uint, byte[]>? Credentials { get; set; }
}
