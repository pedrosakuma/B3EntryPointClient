using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace B3.EntryPoint.Client;

/// <summary>
/// TLS configuration for the FIXP transport. When <see cref="Enabled"/> is
/// <c>true</c>, the client wraps the underlying TCP stream in an
/// <see cref="SslStream"/> and performs an outbound TLS handshake before
/// FIXP Negotiate.
/// </summary>
/// <remarks>
/// B3 production EntryPoint endpoints require TLS. The in-process simulator
/// runs over plain TCP, so this is opt-in to preserve the simulator path.
/// </remarks>
public sealed class TlsOptions
{
    /// <summary>Enable TLS for the FIXP transport. Defaults to <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// SNI / certificate-target host. When <c>null</c>, the client uses the
    /// IP address from <see cref="EntryPointClientOptions.Endpoint"/>, which
    /// only works if the gateway certificate's SAN matches that IP. Set this
    /// explicitly to the DNS name presented by the B3 gateway certificate.
    /// </summary>
    public string? TargetHost { get; set; }

    /// <summary>
    /// Optional override of remote certificate validation. When <c>null</c>,
    /// the framework's standard chain validation is used. Provide a callback
    /// (e.g. returning <c>true</c>) to disable validation in test environments.
    /// </summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

    /// <summary>
    /// Optional client certificates for mutual TLS. When set, requires
    /// <see cref="Enabled"/> = <c>true</c>; otherwise <see cref="EntryPointClient"/>
    /// constructor throws.
    /// </summary>
    public X509CertificateCollection? ClientCertificates { get; set; }

    /// <summary>
    /// Allowed TLS protocol versions. <see cref="SslProtocols.None"/> (default)
    /// lets the OS negotiate (recommended; typically TLS 1.2/1.3). Pin to
    /// <c>Tls12 | Tls13</c> if compliance requires it.
    /// </summary>
    public SslProtocols EnabledSslProtocols { get; set; } = SslProtocols.None;
}
