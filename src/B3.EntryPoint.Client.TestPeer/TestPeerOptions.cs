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

    /// <summary>
    /// When set, the peer responds to the N-th and subsequent
    /// <c>Establish</c> frames (1-based) with an <c>EstablishReject</c>
    /// carrying <see cref="EstablishRejectCodeOverride"/> instead of an
    /// <c>EstablishmentAck</c>. Counts across all connections within this
    /// peer instance. Defaults to <c>null</c> (always ack). Use
    /// <c>EstablishRejectAfter = 2</c> to test the reconnect-rejected path.
    /// </summary>
    public int? EstablishRejectAfter { get; set; }

    /// <summary>
    /// <see cref="B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode"/> value
    /// returned when <see cref="EstablishRejectAfter"/> triggers a reject.
    /// Defaults to <c>INVALID_SESSIONVERID</c>.
    /// </summary>
    public B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode EstablishRejectCodeOverride { get; set; }
        = B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.INVALID_SESSIONVERID;

    /// <summary>
    /// When set, the peer responds to inbound <c>RetransmitRequest</c>
    /// frames with a <c>RetransmitReject</c> carrying this code instead of
    /// the (empty) <c>Retransmission</c> reply. Defaults to <c>null</c>.
    /// </summary>
    public B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode? RetransmitRejectCode { get; set; }
}
