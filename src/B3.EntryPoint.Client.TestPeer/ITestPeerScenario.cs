using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.EntryPoint.Client.TestPeer;

/// <summary>
/// Strategy that decides how an <see cref="InProcessFixpTestPeer"/> responds
/// to inbound application messages. Implementations must be thread-safe — the
/// peer may invoke them concurrently across connections.
/// </summary>
/// <remarks>
/// The default is <see cref="TestPeerScenarios.AcceptAll"/>, which mirrors the
/// historical behaviour: every NewOrderSingle yields an
/// <c>ExecutionReport_New</c> with <c>OrdStatus = NEW</c>. Implementations
/// can return:
/// <list type="bullet">
/// <item><see cref="NewOrderResponse.AcceptAsNew"/> — single ER (NEW).</item>
/// <item><see cref="NewOrderResponse.AcceptAndFill"/> — ER (NEW) followed by ER (FILLED) — full or partial fill.</item>
/// <item><see cref="NewOrderResponse.RejectBusiness"/> — BusinessMessageReject with optional <see cref="NewOrderResponse.RejectBusiness.RejReason"/> code.</item>
/// </list>
/// <para>
/// <see cref="OnCancel"/> and <see cref="OnModify"/> are exposed as default
/// interface methods so existing implementations remain source-compatible:
/// the peer falls back to accepting cancels/modifies (mirrors v0.8.0
/// behaviour) when the implementation does not override them.
/// </para>
/// </remarks>
public interface ITestPeerScenario
{
    /// <summary>Decide what the peer should respond to an inbound NewOrderSingle.</summary>
    NewOrderResponse OnNewOrder(NewOrderContext context);

    /// <summary>
    /// Decide what the peer should respond to an inbound OrderCancelRequest.
    /// Default: <see cref="CancelResponse.Accept"/> (emit <c>ExecutionReport_Cancel</c>).
    /// </summary>
    CancelResponse OnCancel(CancelContext context) => new CancelResponse.Accept();

    /// <summary>
    /// Decide what the peer should respond to an inbound OrderCancelReplaceRequest.
    /// Default: <see cref="ModifyResponse.Accept"/> (emit <c>ExecutionReport_Modify</c>).
    /// </summary>
    ModifyResponse OnModify(ModifyContext context) => new ModifyResponse.Accept();

    /// <summary>
    /// Decide what the peer does for an outbound application frame. Fires
    /// after the frame is encoded but before it hits the wire. The default
    /// is <see cref="OutboundFrameAction.Send"/>; implementations can return
    /// <see cref="OutboundFrameAction.Drop"/> (silently swallow the frame —
    /// the next frame will arrive with a <c>MsgSeqNum</c> gap, exercising
    /// the client's gap-detection / retransmit path) or
    /// <see cref="OutboundFrameAction.SkipSeq"/> (drop the frame *and*
    /// advance the peer's outbound seq counter further to widen the gap).
    /// <para>
    /// Wrapping with <see cref="OutboundFrameAction.DelayThen"/> applies a
    /// post-encode delay before evaluating the inner action.
    /// </para>
    /// </summary>
    OutboundFrameAction OnOutboundFrame(OutboundFrameContext context)
        => new OutboundFrameAction.Send();
}

/// <summary>Decoded summary of an outbound application frame about to be sent.</summary>
/// <param name="TemplateId">SBE template id (e.g. 201 for ExecutionReport_New, 203 for ExecutionReport_Trade).</param>
/// <param name="MsgSeqNum">FIXP <c>MsgSeqNum</c> assigned to this frame.</param>
/// <param name="FrameLength">Total frame length in bytes (SOFH + SBE header + payload + var-data).</param>
public readonly record struct OutboundFrameContext(uint TemplateId, ulong MsgSeqNum, int FrameLength);

/// <summary>
/// Discriminated union describing what <see cref="InProcessFixpTestPeer"/>
/// does with an outbound application frame after
/// <see cref="ITestPeerScenario.OnOutboundFrame"/> is invoked.
/// </summary>
public abstract record OutboundFrameAction
{
    private OutboundFrameAction() { }

    /// <summary>Send the frame as encoded (default behaviour).</summary>
    public sealed record Send : OutboundFrameAction;

    /// <summary>
    /// Silently drop the frame. The peer's <c>MsgSeqNum</c> counter has
    /// already been incremented at the call site, so the next frame will
    /// arrive at the client with a one-step gap.
    /// </summary>
    public sealed record Drop : OutboundFrameAction;

    /// <summary>
    /// Drop the frame and additionally advance the peer's outbound
    /// <c>MsgSeqNum</c> counter by <paramref name="Skip"/>. The next frame
    /// arrives at the client with a gap of <c>1 + Skip</c> messages.
    /// </summary>
    /// <param name="Skip">Extra seq numbers to advance after dropping. Must be &gt;= 1.</param>
    public sealed record SkipSeq(uint Skip) : OutboundFrameAction;

    /// <summary>
    /// Wait <paramref name="Delay"/> then apply <paramref name="Then"/>.
    /// Useful to test client-side timeouts or out-of-order arrival relative
    /// to other frames.
    /// </summary>
    public sealed record DelayThen(TimeSpan Delay, OutboundFrameAction Then) : OutboundFrameAction;
}

/// <summary>Decoded summary of an inbound NewOrderSingle.</summary>
public readonly record struct NewOrderContext(
    uint SessionId,
    uint EnteringFirm,
    ulong SecurityId,
    string ClOrdId)
{
    /// <summary>OrderQty from the inbound NewOrderSingle (when present).</summary>
    public ulong? OrderQty { get; init; }

    /// <summary>Price from the inbound NewOrderSingle (when present; market orders may be unset).</summary>
    public decimal? Price { get; init; }

    /// <summary>Side from the inbound NewOrderSingle (when present).</summary>
    public Side? Side { get; init; }

    /// <summary>Inbound BusinessHeader.MsgSeqNum, surfaced for BusinessMessageReject's RefSeqNum.</summary>
    public uint MsgSeqNum { get; init; }
}

/// <summary>Decoded summary of an inbound OrderCancelRequest.</summary>
public readonly record struct CancelContext(
    uint SessionId,
    ulong SecurityId,
    string ClOrdId,
    string OrigClOrdId)
{
    /// <summary>Inbound BusinessHeader.MsgSeqNum, surfaced for ExecutionReport_Reject's MsgSeqNum context.</summary>
    public uint MsgSeqNum { get; init; }
}

/// <summary>Decoded summary of an inbound OrderCancelReplaceRequest.</summary>
public readonly record struct ModifyContext(
    uint SessionId,
    ulong SecurityId,
    string ClOrdId,
    string OrigClOrdId)
{
    /// <summary>OrderQty from the inbound OrderCancelReplaceRequest (when present).</summary>
    public ulong? OrderQty { get; init; }

    /// <summary>Price from the inbound OrderCancelReplaceRequest (when present).</summary>
    public decimal? Price { get; init; }

    /// <summary>Inbound BusinessHeader.MsgSeqNum.</summary>
    public uint MsgSeqNum { get; init; }
}

/// <summary>Discriminated union of peer responses to a NewOrderSingle.</summary>
public abstract record NewOrderResponse
{
    private NewOrderResponse() { }

    /// <summary>Single <c>ExecutionReport_New</c> with <c>OrdStatus = NEW</c> (default).</summary>
    public sealed record AcceptAsNew : NewOrderResponse;

    /// <summary>
    /// <c>ExecutionReport_New</c> immediately followed by an
    /// <c>ExecutionReport_Trade</c>. The trade carries
    /// <c>OrdStatus = FILLED</c> (full fill) or <c>PARTIALLY_FILLED</c>
    /// (when <see cref="FillQty"/> is supplied and lower than the inbound
    /// <c>OrderQty</c>). When <see cref="FillPrice"/> is unset, the inbound
    /// order's <c>Price</c> is used (or <c>1.0m</c> for market orders).
    /// </summary>
    public sealed record AcceptAndFill : NewOrderResponse
    {
        /// <summary>LastPx for the trade ER. Falls back to inbound order Price, then 1.0m.</summary>
        public decimal? FillPrice { get; init; }

        /// <summary>
        /// LastQty for the trade ER. Null = full fill (uses inbound OrderQty).
        /// Values lower than OrderQty produce a partial fill (PARTIALLY_FILLED + LeavesQty).
        /// Values greater than OrderQty are clamped to OrderQty.
        /// </summary>
        public ulong? FillQty { get; init; }
    }

    /// <summary>
    /// <c>BusinessMessageReject</c> referencing the inbound order, with the
    /// supplied <see cref="Reason"/> placed in the message's <c>Text</c>
    /// field (ASCII, truncated at 250 bytes per schema).
    /// </summary>
    public sealed record RejectBusiness(string Reason) : NewOrderResponse
    {
        /// <summary>
        /// BusinessRejectReason code emitted on the wire. Defaults to
        /// <c>99</c> (Other) when unset.
        /// </summary>
        public uint? RejReason { get; init; }
    }
}

/// <summary>Discriminated union of peer responses to an OrderCancelRequest.</summary>
public abstract record CancelResponse
{
    private CancelResponse() { }

    /// <summary>Emit <c>ExecutionReport_Cancel</c> with <c>OrdStatus = CANCELED</c>.</summary>
    public sealed record Accept : CancelResponse;

    /// <summary>
    /// Emit <c>ExecutionReport_Reject</c> (template 204) with
    /// <c>CxlRejResponseTo = CANCEL</c> and the supplied <see cref="Reason"/>.
    /// </summary>
    public sealed record Reject(string Reason) : CancelResponse
    {
        /// <summary>OrdRejReason code; defaults to <c>99</c> (Other).</summary>
        public uint? RejReason { get; init; }
    }
}

/// <summary>Discriminated union of peer responses to an OrderCancelReplaceRequest.</summary>
public abstract record ModifyResponse
{
    private ModifyResponse() { }

    /// <summary>Emit <c>ExecutionReport_Modify</c> with <c>OrdStatus = REPLACED</c>.</summary>
    public sealed record Accept : ModifyResponse;

    /// <summary>
    /// Emit <c>ExecutionReport_Reject</c> (template 204) with
    /// <c>CxlRejResponseTo = REPLACE</c> and the supplied <see cref="Reason"/>.
    /// </summary>
    public sealed record Reject(string Reason) : ModifyResponse
    {
        /// <summary>OrdRejReason code; defaults to <c>99</c> (Other).</summary>
        public uint? RejReason { get; init; }
    }
}

/// <summary>Built-in <see cref="ITestPeerScenario"/> implementations.</summary>
public static class TestPeerScenarios
{
    /// <summary>Accept every NewOrder as <c>NEW</c> (historical default). Cancel/Modify accepted.</summary>
    public static ITestPeerScenario AcceptAll { get; } = new AcceptAllScenario();

    /// <summary>Accept every NewOrder and immediately fill it. Cancel/Modify accepted.</summary>
    public static ITestPeerScenario FillImmediately { get; } = new FillImmediatelyScenario();

    /// <summary>Reject every NewOrder, Cancel, and Modify with the given reason.</summary>
    public static ITestPeerScenario RejectAll(string reason = "rejected by test peer") =>
        new RejectAllScenario(reason);

    /// <summary>
    /// Wraps an <paramref name="inner"/> scenario and applies a deterministic
    /// outbound fault schedule on top of its app-frame responses. Each entry
    /// in <paramref name="schedule"/> maps a 1-based outbound app-frame index
    /// to the <see cref="OutboundFrameAction"/> the peer should apply for
    /// that frame; frames whose index is not in the map use
    /// <see cref="OutboundFrameAction.Send"/>.
    /// <para>
    /// The frame index counts only application messages emitted by the peer
    /// (those routed through the <see cref="ITestPeerScenario.OnOutboundFrame"/>
    /// hook), so a schedule of <c>{ 2 = Drop }</c> drops the second app
    /// frame regardless of session-layer Negotiate/Establish/Sequence
    /// traffic.
    /// </para>
    /// </summary>
    public static ITestPeerScenario WithSequenceFaults(ITestPeerScenario inner, IReadOnlyDictionary<int, OutboundFrameAction> schedule)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(schedule);
        return new SequenceFaultScenario(inner, schedule);
    }

    private sealed class AcceptAllScenario : ITestPeerScenario
    {
        public NewOrderResponse OnNewOrder(NewOrderContext context) => new NewOrderResponse.AcceptAsNew();
    }

    private sealed class FillImmediatelyScenario : ITestPeerScenario
    {
        public NewOrderResponse OnNewOrder(NewOrderContext context) => new NewOrderResponse.AcceptAndFill();
    }

    private sealed class RejectAllScenario : ITestPeerScenario
    {
        private readonly string _reason;
        public RejectAllScenario(string reason) { _reason = reason; }
        public NewOrderResponse OnNewOrder(NewOrderContext context) => new NewOrderResponse.RejectBusiness(_reason);
        public CancelResponse OnCancel(CancelContext context) => new CancelResponse.Reject(_reason);
        public ModifyResponse OnModify(ModifyContext context) => new ModifyResponse.Reject(_reason);
    }

    private sealed class SequenceFaultScenario : ITestPeerScenario
    {
        private readonly ITestPeerScenario _inner;
        private readonly IReadOnlyDictionary<int, OutboundFrameAction> _schedule;
        private int _frameCount;

        public SequenceFaultScenario(ITestPeerScenario inner, IReadOnlyDictionary<int, OutboundFrameAction> schedule)
        {
            _inner = inner;
            _schedule = schedule;
        }

        public NewOrderResponse OnNewOrder(NewOrderContext context) => _inner.OnNewOrder(context);
        public CancelResponse OnCancel(CancelContext context) => _inner.OnCancel(context);
        public ModifyResponse OnModify(ModifyContext context) => _inner.OnModify(context);

        public OutboundFrameAction OnOutboundFrame(OutboundFrameContext context)
        {
            var index = System.Threading.Interlocked.Increment(ref _frameCount);
            return _schedule.TryGetValue(index, out var action) ? action : new OutboundFrameAction.Send();
        }
    }
}
