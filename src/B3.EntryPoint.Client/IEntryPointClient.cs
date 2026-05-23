using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.Risk;
using B3.EntryPoint.Client.State;
using B3.EntryPoint.Client.Telemetry;

namespace B3.EntryPoint.Client;

/// <summary>
/// High-level B3 EntryPoint client contract. Aggregates the order-flow
/// interfaces (<see cref="ISubmitOrder"/>, <see cref="IReplaceOrder"/>,
/// <see cref="ICancelOrder"/>, <see cref="ISubmitCross"/>,
/// <see cref="IQuoteFlow"/>) and exposes session lifecycle, telemetry and
/// inbound event streaming. Implemented by <see cref="EntryPointClient"/>;
/// this interface exists so consumers (notably <c>B3TradingPlatform</c>) can
/// depend on an abstraction and substitute it in unit tests.
/// </summary>
public interface IEntryPointClient :
    IAsyncDisposable,
    ISubmitOrder,
    IReplaceOrder,
    ICancelOrder,
    ISubmitCross,
    IQuoteFlow
{
    /// <summary>Current FIXP session state.</summary>
    FixpClientState State { get; }

    /// <summary>Pre-trade risk gates evaluated before any outbound order frame.</summary>
    IList<IPreTradeGate> RiskGates { get; }

    /// <summary>Keep-alive scheduler bound after <see cref="ConnectAsync"/>.</summary>
    IKeepAliveScheduler? KeepAlive { get; }

    /// <summary>Retransmit handler bound after <see cref="ConnectAsync"/>.</summary>
    IRetransmitRequestHandler? Retransmit { get; }

    /// <summary>Raised when the session is terminated (sent or received).</summary>
    event EventHandler<TerminatedEventArgs>? Terminated;

    /// <summary>Establishes the FIXP session (TCP + Negotiate + Establish).</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Sends a <c>Terminate</c> frame and closes the session.</summary>
    Task TerminateAsync(TerminationCode code, CancellationToken ct = default);

    /// <summary>
    /// Flushes any bytes buffered by the underlying transport. Required at
    /// batch boundaries when
    /// <see cref="EntryPointClientOptions.AutoFlushOutboundFrames"/> is
    /// <see langword="false"/>; a no-op when the client is not connected.
    /// Issue #123.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Reconnects with a bumped <c>SessionVerId</c>.</summary>
    /// <remarks>
    /// Legacy v0.14.3 overload — equivalent to
    /// <see cref="ReconnectAsync(ReconnectMode, System.Func{uint, uint}?, CancellationToken)"/>
    /// with <see cref="ReconnectMode.AlwaysNegotiate"/>. Prefer the new
    /// overload to enable spec-canonical reattach (#173).
    /// </remarks>
#pragma warning disable RS0027 // optional-param overload predates the longer #173 overload; kept for source compat
    Task ReconnectAsync(uint nextSessionVerId, CancellationToken ct = default);
#pragma warning restore RS0027

    /// <summary>
    /// Spec-canonical reconnect (#173). On
    /// <see cref="ReconnectMode.EstablishReuseThenNegotiate"/> tries
    /// <c>Establish</c> reusing the same <c>SessionVerID</c> first and falls
    /// back to <c>Negotiate</c> only when the gateway rejects with a
    /// recoverable code (<c>UNNEGOTIATED</c>, <c>SESSION_BLOCKED</c>,
    /// <c>INVALID_SESSIONID</c>, <c>INVALID_SESSIONVERID</c>,
    /// <c>ALREADY_ESTABLISHED</c>, <c>ESTABLISH_ATTEMPTS_EXCEEDED</c>).
    /// </summary>
#pragma warning disable RS0026, RS0027 // intentional overload introduced in #173; legacy retains optional ct
    Task<ReconnectOutcome> ReconnectAsync(
        ReconnectMode mode,
        Func<uint, uint>? nextSessionVerIdSelector,
        CancellationToken ct);
#pragma warning restore RS0026, RS0027

    /// <summary>Snapshot of in-memory client health counters.</summary>
    ClientHealth GetHealth();

    /// <summary>
    /// Streams inbound EntryPoint events as they are decoded. Backed by a bounded
    /// channel (see <see cref="EntryPointClientOptions.EventChannelCapacity"/>,
    /// default 4096, <see cref="System.Threading.Channels.BoundedChannelFullMode.Wait"/>)
    /// so a slow consumer applies backpressure to the inbound decoder rather than
    /// dropping events or growing memory unboundedly.
    /// </summary>
    IAsyncEnumerable<EntryPointEvent> Events(CancellationToken ct = default);
}
