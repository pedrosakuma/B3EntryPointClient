using System.Buffers.Binary;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Framing;
using B3.EntryPoint.Client.Models;
using ClientAccepted = B3.EntryPoint.Client.Models.OrderAccepted;
using ClientReject = B3.EntryPoint.Client.Models.BusinessReject;

namespace B3.EntryPoint.Client.Tests.Fixp;

public class InboundDecoderTests
{
    private const int SofhSize = SofhFrameWriter.HeaderSize;
    private const int SbeHeaderSize = MessageHeader.MESSAGE_SIZE;

    private static byte[] BuildFrame(ushort templateId, int payloadSize, Action<Span<byte>> writePayload)
    {
        // Allocate generous trailing room for encoders that write var-data sections.
        var total = SofhSize + SbeHeaderSize + payloadSize + 16;
        var buffer = new byte[total];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)total));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SofhSize, 2), (ushort)payloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SofhSize + 2, 2), templateId);
        writePayload(buffer.AsSpan(SofhSize + SbeHeaderSize));
        return buffer;
    }

    [Fact]
    public void TryDecode_ExecutionReportNew_ReturnsOrderAccepted()
    {
        var msg = new ExecutionReport_NewData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = 1,
                MsgSeqNum = new SeqNum(42),
            },
            Side = B3.Entrypoint.Fixp.Sbe.V6.Side.BUY,
            OrdStatus = OrdStatus.NEW,
            ClOrdID = new B3.Entrypoint.Fixp.Sbe.V6.ClOrdID(7777UL),
            OrderID = new OrderID(123456UL),
            SecurityID = new SecurityID(9UL),
            TransactTime = new UTCTimestampNanos { Time = 1_700_000_000_000_000_000UL },
        };

        var frame = BuildFrame(ExecutionReport_NewData.MESSAGE_ID, ExecutionReport_NewData.MESSAGE_SIZE, span =>
        {
            if (!ExecutionReport_NewData.TryEncode(msg, span, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, out _))
                throw new InvalidOperationException("encode failed");
        });

        Assert.True(InboundDecoder.TryDecode(frame, out var evt));
        var accepted = Assert.IsType<ClientAccepted>(evt);
        Assert.Equal(42UL, accepted.SeqNum);
        Assert.Equal(7777UL, accepted.ClOrdID.Value);
        Assert.Equal(123456UL, accepted.OrderId);
    }

    [Fact]
    public void TryDecode_BusinessMessageReject_ReturnsBusinessReject()
    {
        var msg = new BusinessMessageRejectData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = 1,
                MsgSeqNum = new SeqNum(99),
            },
            RefMsgType = MessageType.NewOrderSingle,
            RefSeqNum = new SeqNum(7),
            BusinessRejectReason = new RejReason(11),
        };

        var frame = BuildFrame(BusinessMessageRejectData.MESSAGE_ID, BusinessMessageRejectData.MESSAGE_SIZE, span =>
        {
            if (!msg.TryEncode(span, out _))
                throw new InvalidOperationException("encode failed");
        });

        Assert.True(InboundDecoder.TryDecode(frame, out var evt));
        var reject = Assert.IsType<ClientReject>(evt);
        Assert.Equal(99UL, reject.SeqNum);
        Assert.Equal(7UL, reject.RefSeqNum);
        Assert.Equal((ushort)11, reject.RejectReason);
    }

    [Fact]
    public void TryDecode_UnknownTemplate_ReturnsFalse()
    {
        var frame = BuildFrame(9999, 0, _ => { });
        Assert.False(InboundDecoder.TryDecode(frame, out var evt));
        Assert.Null(evt);
    }

    [Fact]
    public void TryDecode_QuoteRequestReject_ReturnsQuoteRequestRejected()
    {
        var msg = new QuoteRequestRejectData
        {
            BusinessHeader = new BidirectionalBusinessHeader
            {
                SessionID = 1,
                MsgSeqNum = new SeqNum(55),
            },
            QuoteReqID = new QuoteReqID(1001UL),
            SecurityID = new SecurityID(4242UL),
            ContraBroker = new Firm(99),
            EnteringTrader = default,
            ExecutingTrader = default,
            SenderLocation = default,
            TransactTime = new UTCTimestampNanos { Time = 1_700_000_000_000_000_000UL },
        };
        msg.SetQuoteRequestRejectReason(5);
        msg.SetQuoteID(2002UL);

        var frame = BuildFrame(QuoteRequestRejectData.MESSAGE_ID, QuoteRequestRejectData.MESSAGE_SIZE, span =>
        {
            if (!msg.TryEncode(span, out _))
                throw new InvalidOperationException("encode failed");
        });

        Assert.True(InboundDecoder.TryDecode(frame, out var evt));
        var rejected = Assert.IsType<QuoteRequestRejected>(evt);
        Assert.Equal(55UL, rejected.SeqNum);
        Assert.Equal("1001", rejected.QuoteReqId);
        Assert.Equal(4242UL, rejected.SecurityId);
        Assert.Equal("2002", rejected.QuoteId);
        Assert.Equal(5u, rejected.RejectReason);
    }

    [Fact]
    public void TryDecode_QuoteStatusReport_ReturnsQuoteStatusUpdated()
    {
        var msg = new QuoteStatusReportData
        {
            BusinessHeader = new BidirectionalBusinessHeader
            {
                SessionID = 1,
                MsgSeqNum = new SeqNum(77),
            },
            QuoteReqID = new QuoteReqID(1001UL),
            QuoteID = new QuoteID(2002UL),
            SecurityID = new SecurityID(4242UL),
            ContraBroker = new Firm(99),
            EnteringTrader = default,
            ExecutingTrader = default,
            SenderLocation = default,
            QuoteStatus = B3.Entrypoint.Fixp.Sbe.V6.QuoteStatus.ACCEPTED,
            TransactTime = new UTCTimestampNanos { Time = 1_700_000_000_000_000_000UL },
        };

        var frame = BuildFrame(QuoteStatusReportData.MESSAGE_ID, QuoteStatusReportData.MESSAGE_SIZE, span =>
        {
            if (!msg.TryEncode(span, out _))
                throw new InvalidOperationException("encode failed");
        });

        Assert.True(InboundDecoder.TryDecode(frame, out var evt));
        var updated = Assert.IsType<QuoteStatusUpdated>(evt);
        Assert.Equal(77UL, updated.SeqNum);
        Assert.Equal("2002", updated.QuoteId);
        Assert.Equal("1001", updated.QuoteReqId);
        Assert.Equal(4242UL, updated.SecurityId);
        Assert.Equal(B3.EntryPoint.Client.Models.QuoteStatus.Accepted, updated.Status);
    }

    [Fact]
    public void TryDecode_OrderMassActionReport_ReturnsMassActionExecuted()
    {
        var msg = new OrderMassActionReportData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = 1,
                MsgSeqNum = new SeqNum(123),
            },
            MassActionType = B3.Entrypoint.Fixp.Sbe.V6.MassActionType.CANCEL_ORDERS,
            ClOrdID = new B3.Entrypoint.Fixp.Sbe.V6.ClOrdID(8888UL),
            MassActionReportID = new MassActionReportID(55555UL),
            TransactTime = new UTCTimestampNanos { Time = 1_700_000_000_000_000_000UL },
            MassActionResponse = B3.Entrypoint.Fixp.Sbe.V6.MassActionResponse.ACCEPTED,
        };
        msg.SetMassActionScope(B3.Entrypoint.Fixp.Sbe.V6.MassActionScope.ALL_ORDERS_FOR_A_TRADING_SESSION);
        msg.SetMassActionRejectReason(null);
        msg.SetExecRestatementReason(null);
        msg.SetSide(B3.Entrypoint.Fixp.Sbe.V6.Side.BUY);
        msg.SetSecurityID(4242UL);

        var frame = BuildFrame(OrderMassActionReportData.MESSAGE_ID, OrderMassActionReportData.MESSAGE_SIZE, span =>
        {
            if (!msg.TryEncode(span, out _))
                throw new InvalidOperationException("encode failed");
        });

        Assert.True(InboundDecoder.TryDecode(frame, out var evt));
        var report = Assert.IsType<MassActionExecuted>(evt);
        Assert.Equal(123UL, report.SeqNum);
        Assert.Equal(8888UL, report.ClOrdID.Value);
        Assert.Equal(55555UL, report.MassActionReportId);
        Assert.Equal(B3.EntryPoint.Client.Models.MassActionType.CancelOrders, report.ActionType);
        Assert.Equal(B3.EntryPoint.Client.Models.MassActionScope.AllOrdersForATradingSession, report.Scope);
        Assert.Equal(B3.EntryPoint.Client.Models.MassActionResponse.Accepted, report.Response);
        Assert.Equal(B3.EntryPoint.Client.Models.Side.Buy, report.Side);
        Assert.Equal(4242UL, report.SecurityId);
        Assert.Null(report.RejectReason);
    }

    [Fact]
    public void TryDecode_AllocationReport_ReturnsAllocationReceived()
    {
        var msg = new AllocationReportData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = 1,
                MsgSeqNum = new SeqNum(321),
            },
            AllocID = new AllocID(7777UL),
            SecurityID = new SecurityID(4242UL),
            AllocReportID = new AllocReportID(99999UL),
            AllocTransType = B3.Entrypoint.Fixp.Sbe.V6.AllocTransType.NEW,
            AllocReportType = B3.Entrypoint.Fixp.Sbe.V6.AllocReportType.REQUEST_TO_INTERMEDIARY,
            AllocNoOrdersType = B3.Entrypoint.Fixp.Sbe.V6.AllocNoOrdersType.NOT_SPECIFIED,
            Quantity = new Quantity(10UL),
            AllocStatus = B3.Entrypoint.Fixp.Sbe.V6.AllocStatus.ACCEPTED,
            TransactTime = new UTCTimestampNanos { Time = 1_700_000_000_000_000_000UL },
            Side = B3.Entrypoint.Fixp.Sbe.V6.Side.BUY,
        };
        msg.SetTradeDate((ushort)9876);

        var frame = BuildFrame(AllocationReportData.MESSAGE_ID, AllocationReportData.MESSAGE_SIZE, span =>
        {
            if (!msg.TryEncode(span, out _))
                throw new InvalidOperationException("encode failed");
        });

        Assert.True(InboundDecoder.TryDecode(frame, out var evt));
        var alloc = Assert.IsType<AllocationReceived>(evt);
        Assert.Equal(321UL, alloc.SeqNum);
        Assert.Equal(7777UL, alloc.AllocId);
        Assert.Equal(99999UL, alloc.AllocReportId);
        Assert.Equal(4242UL, alloc.SecurityId);
        Assert.Equal(B3.EntryPoint.Client.Models.AllocTransType.New, alloc.TransType);
        Assert.Equal(B3.EntryPoint.Client.Models.AllocReportType.RequestToIntermediary, alloc.ReportType);
        Assert.Equal(B3.EntryPoint.Client.Models.AllocStatus.Accepted, alloc.Status);
        Assert.Equal(10UL, alloc.Quantity);
        Assert.Equal(B3.EntryPoint.Client.Models.Side.Buy, alloc.Side);
        Assert.Equal((ushort?)9876, alloc.TradeDate);
    }

    [Fact]
    public void TryDecode_PositionMaintenanceReport_ReturnsPositionMaintenanceReceived()
    {
        var msg = new PositionMaintenanceReportData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = 1,
                MsgSeqNum = new SeqNum(555),
            },
            SecurityID = new SecurityID(4242UL),
            PosMaintRptID = new PosMaintRptID(123456UL),
            PosTransType = B3.Entrypoint.Fixp.Sbe.V6.PosTransType.EXERCISE,
            PosMaintAction = B3.Entrypoint.Fixp.Sbe.V6.PosMaintAction.NEW,
            PosMaintStatus = B3.Entrypoint.Fixp.Sbe.V6.PosMaintStatus.ACCEPTED,
            ClearingBusinessDate = new LocalMktDate((ushort)9876),
            TransactTime = new UTCTimestampNanos { Time = 1_700_000_000_000_000_000UL },
        };
        msg.SetPosReqID(7777UL);
        msg.SetTradeID(42u);
        msg.SetAccountType(B3.Entrypoint.Fixp.Sbe.V6.AccountType.REGULAR_ACCOUNT);
        msg.SetAccount(99u);
        msg.SetPosMaintResult(0u); // 0 is the null value, so should map to null.

        var frame = BuildFrame(PositionMaintenanceReportData.MESSAGE_ID, PositionMaintenanceReportData.MESSAGE_SIZE, span =>
        {
            if (!msg.TryEncode(span, out _))
                throw new InvalidOperationException("encode failed");
        });

        Assert.True(InboundDecoder.TryDecode(frame, out var evt));
        var pmr = Assert.IsType<PositionMaintenanceReceived>(evt);
        Assert.Equal(555UL, pmr.SeqNum);
        Assert.Equal(123456UL, pmr.PosMaintRptId);
        Assert.Equal(4242UL, pmr.SecurityId);
        Assert.Equal(B3.EntryPoint.Client.Models.PosTransType.Exercise, pmr.TransType);
        Assert.Equal(B3.EntryPoint.Client.Models.PosMaintAction.New, pmr.Action);
        Assert.Equal(B3.EntryPoint.Client.Models.PosMaintStatus.Accepted, pmr.Status);
        Assert.Equal(7777UL, pmr.PosReqId);
        Assert.Equal(42u, pmr.TradeId);
        Assert.Equal(B3.EntryPoint.Client.Models.AccountType.RegularAccount, pmr.AccountType);
        Assert.Equal(99u, pmr.Account);
    }
}
