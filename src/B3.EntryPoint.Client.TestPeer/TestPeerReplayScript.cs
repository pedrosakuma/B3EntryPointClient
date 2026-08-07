using System.Collections.ObjectModel;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Models;
using ClientClOrdID = B3.EntryPoint.Client.Models.ClOrdID;
using ClientSide = B3.EntryPoint.Client.Models.Side;

namespace B3.EntryPoint.Client.TestPeer;

/// <summary>
/// Ordered deterministic replay plan for <see cref="InProcessFixpTestPeer"/>.
/// Handshake entries are consumed when the matching inbound FIXP request
/// arrives; outbound frame entries are released only when the test explicitly
/// advances the replay via <see cref="InProcessFixpTestPeer.AdvanceReplayAsync"/>.
/// </summary>
public sealed class TestPeerReplayScript
{
    private readonly ReadOnlyCollection<TestPeerReplayEvent> _events;

    internal TestPeerReplayScript(IEnumerable<TestPeerReplayEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = new ReadOnlyCollection<TestPeerReplayEvent>(events.ToList());
    }

    internal IReadOnlyList<TestPeerReplayEvent> Events => _events;

    public static TestPeerReplayScriptBuilder Create(uint defaultSessionId, ulong defaultSessionVerId) =>
        new(defaultSessionId, defaultSessionVerId);
}

internal abstract class TestPeerReplayEvent
{
    public uint? SessionId { get; set; }
    public ulong? SessionVerId { get; set; }
}

internal abstract class TestPeerHandshakeReplayEvent : TestPeerReplayEvent;

internal sealed class TestPeerNegotiateAcceptReplayEvent : TestPeerHandshakeReplayEvent
{
    public uint? EnteringFirm { get; set; }
}

internal sealed class TestPeerNegotiateRejectReplayEvent : TestPeerHandshakeReplayEvent
{
    public TestPeerNegotiateRejectReplayEvent(NegotiationRejectCode code) => Code = code;
    public NegotiationRejectCode Code { get; }
    public uint? EnteringFirm { get; set; }
    public ulong? CurrentSessionVerId { get; set; }
}

internal sealed class TestPeerEstablishAckReplayEvent : TestPeerHandshakeReplayEvent
{
    public uint? NextSeqNo { get; set; }
    public uint? LastIncomingSeqNo { get; set; }
    public uint? KeepAliveIntervalMs { get; set; }
}

internal sealed class TestPeerEstablishRejectReplayEvent : TestPeerHandshakeReplayEvent
{
    public TestPeerEstablishRejectReplayEvent(EstablishRejectCode code) => Code = code;
    public EstablishRejectCode Code { get; }
}

internal abstract class TestPeerOutboundReplayEvent : TestPeerReplayEvent
{
    public uint? MsgSeqNum { get; set; }
}

internal sealed class TestPeerExecutionReportAcceptedReplayEvent : TestPeerOutboundReplayEvent
{
    public TestPeerExecutionReportAcceptedReplayEvent(ClientClOrdID clOrdId, ulong orderId, ulong securityId, ClientSide side)
    {
        ClOrdId = clOrdId;
        OrderId = orderId;
        SecurityId = securityId;
        Side = side;
    }

    public ClientClOrdID ClOrdId { get; }
    public ulong OrderId { get; }
    public ulong SecurityId { get; }
    public ClientSide Side { get; }
    public OrderStatus OrderStatus { get; set; } = OrderStatus.New;
    public DateTimeOffset? TransactTime { get; set; }
}

internal sealed class TestPeerExecutionReportTradeReplayEvent : TestPeerOutboundReplayEvent
{
    public TestPeerExecutionReportTradeReplayEvent(ClientClOrdID clOrdId, ulong orderId, ulong tradeId, ulong securityId, ClientSide side, decimal lastPx, ulong lastQty)
    {
        ClOrdId = clOrdId;
        OrderId = orderId;
        TradeId = tradeId;
        SecurityId = securityId;
        Side = side;
        LastPx = lastPx;
        LastQty = lastQty;
    }

    public ClientClOrdID ClOrdId { get; }
    public ulong OrderId { get; }
    public ulong TradeId { get; }
    public ulong SecurityId { get; }
    public ClientSide Side { get; }
    public decimal LastPx { get; }
    public ulong LastQty { get; }
    public OrderStatus OrderStatus { get; set; } = OrderStatus.Filled;
    public ulong? LeavesQty { get; set; }
    public ulong? CumQty { get; set; }
    public DateTimeOffset? TransactTime { get; set; }
}

internal sealed class TestPeerNotAppliedReplayEvent : TestPeerReplayEvent
{
    public TestPeerNotAppliedReplayEvent(uint fromSeqNo, uint count)
    {
        FromSeqNo = fromSeqNo;
        Count = count;
    }

    public uint FromSeqNo { get; }
    public uint Count { get; }
}

public sealed class TestPeerReplayScriptBuilder
{
    private readonly uint _defaultSessionId;
    private readonly ulong _defaultSessionVerId;
    private readonly List<TestPeerReplayEvent> _events = new();

    internal TestPeerReplayScriptBuilder(uint defaultSessionId, ulong defaultSessionVerId)
    {
        _defaultSessionId = defaultSessionId;
        _defaultSessionVerId = defaultSessionVerId;
    }

    public TestPeerReplayScriptBuilder NegotiateAccept(uint? sessionId = null, ulong? sessionVerId = null, uint? enteringFirm = null)
    {
        _events.Add(new TestPeerNegotiateAcceptReplayEvent
        {
            SessionId = sessionId ?? _defaultSessionId,
            SessionVerId = sessionVerId ?? _defaultSessionVerId,
            EnteringFirm = enteringFirm,
        });
        return this;
    }

    public TestPeerReplayScriptBuilder NegotiateReject(NegotiationRejectCode code, uint? sessionId = null, ulong? sessionVerId = null, uint? enteringFirm = null, ulong? currentSessionVerId = null)
    {
        _events.Add(new TestPeerNegotiateRejectReplayEvent(code)
        {
            SessionId = sessionId ?? _defaultSessionId,
            SessionVerId = sessionVerId ?? _defaultSessionVerId,
            EnteringFirm = enteringFirm,
            CurrentSessionVerId = currentSessionVerId,
        });
        return this;
    }

    public TestPeerReplayScriptBuilder EstablishAck(uint? sessionId = null, ulong? sessionVerId = null, uint? nextSeqNo = null, uint? lastIncomingSeqNo = null, uint? keepAliveIntervalMs = null)
    {
        _events.Add(new TestPeerEstablishAckReplayEvent
        {
            SessionId = sessionId ?? _defaultSessionId,
            SessionVerId = sessionVerId ?? _defaultSessionVerId,
            NextSeqNo = nextSeqNo,
            LastIncomingSeqNo = lastIncomingSeqNo,
            KeepAliveIntervalMs = keepAliveIntervalMs,
        });
        return this;
    }

    public TestPeerReplayScriptBuilder EstablishReject(EstablishRejectCode code, uint? sessionId = null, ulong? sessionVerId = null)
    {
        _events.Add(new TestPeerEstablishRejectReplayEvent(code)
        {
            SessionId = sessionId ?? _defaultSessionId,
            SessionVerId = sessionVerId ?? _defaultSessionVerId,
        });
        return this;
    }

    public TestPeerReplayScriptBuilder ExecutionReportAccepted(ClientClOrdID clOrdId, ulong orderId, ulong securityId, ClientSide side, uint? msgSeqNum = null, uint? sessionId = null, ulong? sessionVerId = null, OrderStatus orderStatus = OrderStatus.New, DateTimeOffset? transactTime = null)
    {
        _events.Add(new TestPeerExecutionReportAcceptedReplayEvent(clOrdId, orderId, securityId, side)
        {
            MsgSeqNum = msgSeqNum,
            SessionId = sessionId ?? _defaultSessionId,
            SessionVerId = sessionVerId ?? _defaultSessionVerId,
            OrderStatus = orderStatus,
            TransactTime = transactTime,
        });
        return this;
    }

    public TestPeerReplayScriptBuilder ExecutionReportTrade(ClientClOrdID clOrdId, ulong orderId, ulong tradeId, ulong securityId, ClientSide side, decimal lastPx, ulong lastQty, uint? msgSeqNum = null, uint? sessionId = null, ulong? sessionVerId = null, OrderStatus orderStatus = OrderStatus.Filled, ulong? leavesQty = null, ulong? cumQty = null, DateTimeOffset? transactTime = null)
    {
        _events.Add(new TestPeerExecutionReportTradeReplayEvent(clOrdId, orderId, tradeId, securityId, side, lastPx, lastQty)
        {
            MsgSeqNum = msgSeqNum,
            SessionId = sessionId ?? _defaultSessionId,
            SessionVerId = sessionVerId ?? _defaultSessionVerId,
            OrderStatus = orderStatus,
            LeavesQty = leavesQty,
            CumQty = cumQty,
            TransactTime = transactTime,
        });
        return this;
    }

    public TestPeerReplayScriptBuilder NotApplied(uint fromSeqNo, uint count, uint? sessionId = null, ulong? sessionVerId = null)
    {
        _events.Add(new TestPeerNotAppliedReplayEvent(fromSeqNo, count)
        {
            SessionId = sessionId ?? _defaultSessionId,
            SessionVerId = sessionVerId ?? _defaultSessionVerId,
        });
        return this;
    }

    public TestPeerReplayScript Build() => new(_events);
}
