using System.Net;
using B3.EntryPoint.Client.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.EntryPoint.Client;

public sealed class EntryPointClientOptions
{
    public required IPEndPoint Endpoint { get; init; }

    /// <summary>Client connection identification on the gateway, assigned by B3.</summary>
    public required uint SessionId { get; init; }

    /// <summary>Session version identification — must increase on each new Negotiate.</summary>
    public required uint SessionVerId { get; set; }

    /// <summary>Identifies the broker firm that will enter orders.</summary>
    public required uint EnteringFirm { get; init; }

    public required Credentials Credentials { get; init; }

    /// <summary>Convenience constructor for shared-secret/UAT-style auth.</summary>
    public static Credentials AccessKey(string value) => Credentials.FromUtf8(value);

    /// <summary>FIXP keep-alive interval requested by the client (ms).</summary>
    public uint KeepAliveIntervalMs { get; init; } = 1000;

    /// <summary>FIXP keep-alive interval requested by the client.</summary>
    public TimeSpan KeepAliveInterval => TimeSpan.FromMilliseconds(KeepAliveIntervalMs);

    /// <summary>
    /// Identifies the original location for routing orders (FIX <c>SenderLocation</c>,
    /// max 10 chars). Required on every order entry message.
    /// </summary>
    public string SenderLocation { get; init; } = "";

    /// <summary>
    /// Identifier of the trader entering the orders (FIX <c>EnteringTrader</c>,
    /// max 5 chars). Required on every order entry message.
    /// </summary>
    public string EnteringTrader { get; init; } = "";

    /// <summary>
    /// Default <c>MarketSegmentID</c> stamped on the inbound business header
    /// when a request does not specify one. Defaults to 1 (single-segment).
    /// </summary>
    public byte DefaultMarketSegmentId { get; init; } = 1;

    /// <summary>
    /// Cancel-on-disconnect behaviour requested at <c>Negotiate</c>. Defaults
    /// to <see cref="CancelOnDisconnectType.CancelOnDisconnectOrTerminate"/>
    /// — the safest choice for a participant that must not leave open orders
    /// after losing the session.
    /// </summary>
    public CancelOnDisconnectType CancelOnDisconnect { get; init; } =
        CancelOnDisconnectType.CancelOnDisconnectOrTerminate;

    /// <summary>
    /// Profile of the FIXP session. <see cref="SessionProfile.OrderEntry"/> is
    /// the default and allows order submission. <see cref="SessionProfile.DropCopy"/>
    /// is read-only and used by <see cref="DropCopy.DropCopyClient"/>.
    /// </summary>
    public SessionProfile Profile { get; init; } = SessionProfile.OrderEntry;

    /// <summary>Optional client metadata sent in <c>Negotiate.ClientAppName</c>.</summary>
    public string ClientAppName { get; init; } = "B3.EntryPoint.Client";

    /// <summary>Optional client metadata sent in <c>Negotiate.ClientAppVersion</c>.</summary>
    public string ClientAppVersion { get; init; } = ThisAssemblyVersion();

    /// <summary>Optional client IP override sent in <c>Negotiate.ClientIP</c>; resolved automatically when null.</summary>
    public string? ClientIP { get; init; }

    /// <summary>TCP connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Time to wait for <c>NegotiateResponse</c> / <c>EstablishmentAck</c>.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum number of <c>ConnectAsync</c> attempts (TCP+Negotiate+Establish) before
    /// surfacing the underlying exception. <c>1</c> disables retry. Defaults to 1 to preserve
    /// fail-fast semantics in the unit tests.</summary>
    public int ConnectMaxAttempts { get; init; } = 1;

    /// <summary>Base delay for exponential backoff between connect attempts.</summary>
    public TimeSpan ConnectBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum delay between connect attempts (caps the exponential growth).</summary>
    public TimeSpan ConnectMaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Idle timeout — if no inbound frame is observed for this duration the client
    /// closes the session. Defaults to <see cref="TimeSpan.Zero"/> (disabled).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>Optional <see cref="ILogger"/> used by the client for structured events.
    /// Defaults to <see cref="NullLogger.Instance"/> so existing tests are unaffected.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    private static string ThisAssemblyVersion() =>
        typeof(EntryPointClientOptions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
