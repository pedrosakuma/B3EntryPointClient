using System.Buffers.Binary;
using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Fixp;

public class OrderEntryEncoderTests
{
    private static EntryPointClientOptions Opts() => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
        SessionId = 42,
        SessionVerId = 7,
        EnteringFirm = 100,
        Credentials = Credentials.FromUtf8("k"),
        SenderLocation = "SP-001",
        EnteringTrader = "T0001",
        DefaultMarketSegmentId = 1,
    };

    // Wire layout: SOFH (4 bytes: msgLen[2] LE + encoding-type[2] LE) + SBE MessageHeader (8 bytes) + payload.
    // SBE header: blockLength(uint16)|templateId(uint16)|...
    private static (ushort sofhLen, ushort templateId) ReadFrameHeader(byte[] buffer)
    {
        var sofhLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(0, 2));
        var templateId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4 + 2, 2));
        return (sofhLen, templateId);
    }

    [Fact]
    public void EncodeSimpleNewOrder_WritesTemplateId_100_AndExpectedLength()
    {
        var req = new SimpleNewOrderRequest
        {
            ClOrdID = new ClOrdID(12345UL),
            SecurityId = 7,
            Side = Side.Buy,
            OrderType = SimpleOrderType.Limit,
            OrderQty = 100,
            Price = 12.34m,
            Account = 555,
        };
        var buffer = new byte[256];
        var len = OrderEntryEncoder.EncodeSimpleNewOrder(buffer, req, Opts(), msgSeqNum: 1);
        var (sofhLen, tid) = ReadFrameHeader(buffer);
        Assert.Equal(len, sofhLen);
        Assert.Equal((ushort)100, tid);
    }

    [Fact]
    public void EncodeOrderCancel_WritesTemplateId_105()
    {
        var req = new CancelOrderRequest
        {
            ClOrdID = new ClOrdID(2UL),
            OrigClOrdID = new ClOrdID(1UL),
            SecurityId = 7,
            Side = Side.Sell,
        };
        var buffer = new byte[256];
        var len = OrderEntryEncoder.EncodeOrderCancel(buffer, req, Opts(), msgSeqNum: 2);
        var (_, tid) = ReadFrameHeader(buffer);
        Assert.True(len > 0);
        Assert.Equal((ushort)105, tid);
    }

    [Fact]
    public void EncodeNewOrderSingle_WritesTemplateId_102()
    {
        var req = new NewOrderRequest
        {
            ClOrdID = new ClOrdID(99UL),
            SecurityId = 1,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            OrderQty = 10,
            Price = 0.05m,
            Account = 1,
        };
        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeNewOrderSingle(buffer, req, Opts(), msgSeqNum: 3);
        var (_, tid) = ReadFrameHeader(buffer);
        Assert.True(len > 0);
        Assert.Equal((ushort)102, tid);
    }

    [Fact]
    public void EncodeOrderCancelReplace_WritesTemplateId_104()
    {
        var req = new ReplaceOrderRequest
        {
            ClOrdID = new ClOrdID(2UL),
            OrigClOrdID = new ClOrdID(1UL),
            SecurityId = 1,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            OrderQty = 20,
            Price = 0.10m,
        };
        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeOrderCancelReplace(buffer, req, Opts(), msgSeqNum: 4);
        var (_, tid) = ReadFrameHeader(buffer);
        Assert.True(len > 0);
        Assert.Equal((ushort)104, tid);
    }

    [Fact]
    public void EncodeSimpleModifyOrder_WritesTemplateId_101()
    {
        var req = new SimpleModifyRequest
        {
            ClOrdID = new ClOrdID(2UL),
            OrigClOrdID = new ClOrdID(1UL),
            SecurityId = 1,
            Side = Side.Buy,
            OrderType = SimpleOrderType.Limit,
            OrderQty = 15,
            Price = 1.0m,
        };
        var buffer = new byte[256];
        var len = OrderEntryEncoder.EncodeSimpleModifyOrder(buffer, req, Opts(), msgSeqNum: 5);
        var (_, tid) = ReadFrameHeader(buffer);
        Assert.True(len > 0);
        Assert.Equal((ushort)101, tid);
    }

    [Fact]
    public void EncodeOrderMassAction_WritesTemplateId_701_FixedLength()
    {
        var req = new MassActionRequest
        {
            ClOrdID = new ClOrdID(7UL),
            ActionType = MassActionType.CancelOrders,
            Scope = MassActionScope.AllOrdersForATradingSession,
            SecurityId = 1,
            Side = Side.Buy,
        };
        var buffer = new byte[128];
        var len = OrderEntryEncoder.EncodeOrderMassAction(buffer, req, Opts(), msgSeqNum: 6);
        var (sofhLen, tid) = ReadFrameHeader(buffer);
        Assert.Equal(len, sofhLen);
        Assert.Equal((ushort)701, tid);
    }
}
