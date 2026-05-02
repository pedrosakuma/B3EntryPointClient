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

    /// <summary>Reconnects with a bumped <c>SessionVerId</c>.</summary>
    Task ReconnectAsync(uint nextSessionVerId, CancellationToken ct = default);

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
