using System.Net;
using B3.EntryPoint.Client.Auth;

namespace B3.EntryPoint.Client;

public sealed class EntryPointClientOptions
{
    public required IPEndPoint Endpoint { get; init; }

    /// <summary>Client connection identification on the gateway, assigned by B3.</summary>
    public required uint SessionId { get; init; }

    /// <summary>Session version identification — must increase on each new Negotiate.</summary>
    public required uint SessionVerId { get; init; }

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
    /// Cancel-on-disconnect behaviour requested at <c>Negotiate</c>. Defaults
    /// to <see cref="CancelOnDisconnectType.CancelOnDisconnectOrTerminate"/>
    /// — the safest choice for a participant that must not leave open orders
    /// after losing the session.
    /// </summary>
    public CancelOnDisconnectType CancelOnDisconnect { get; init; } =
        CancelOnDisconnectType.CancelOnDisconnectOrTerminate;

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

    private static string ThisAssemblyVersion() =>
        typeof(EntryPointClientOptions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
