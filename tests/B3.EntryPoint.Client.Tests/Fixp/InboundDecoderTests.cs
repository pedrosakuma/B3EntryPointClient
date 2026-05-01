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
}
