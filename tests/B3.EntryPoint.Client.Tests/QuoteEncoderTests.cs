using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Framing;
using Xunit;
using Side = B3.EntryPoint.Client.Models.Side;
using QuoteRequestMessage = B3.EntryPoint.Client.Models.QuoteRequestMessage;
using QuoteMessage = B3.EntryPoint.Client.Models.QuoteMessage;
using SettlementType = B3.EntryPoint.Client.Models.SettlementType;
using SbeSettlType = B3.Entrypoint.Fixp.Sbe.V6.SettlType;

namespace B3.EntryPoint.Client.Tests;

public class QuoteEncoderTests
{
    private static EntryPointClientOptions Options() => new()
    {
        Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1),
        SessionId = 7,
        SessionVerId = 1,
        EnteringFirm = 1234,
        Credentials = Credentials.FromUtf8("k"),
        SenderLocation = "SP",
        EnteringTrader = "TR1",
    };

    [Fact]
    public void EncodeQuoteRequest_RoundTripsViaSbeReader()
    {
        var req = new QuoteRequestMessage
        {
            QuoteReqId = "1001",
            SecurityId = 4242,
            Side = Side.Buy,
            Price = 12.34m,
            OrderQty = 100,
            SettlType = SettlementType.Mutual,
            DaysToSettlement = 30,
            ContraBroker = 99,
            FixedRate = 0.0125m,
        };

        var buffer = new byte[QuoteRequestData.MESSAGE_SIZE + 32];
        var len = OrderEntryEncoder.EncodeQuoteRequest(buffer, req, Options(), msgSeqNum: 42);

        Assert.True(SofhFrameReader.TryParseHeader(buffer, out var msgLen, out _));
        Assert.Equal((ushort)len, msgLen);

        var afterSofh = buffer.AsSpan(SofhFrameReader.HeaderSize, len - SofhFrameReader.HeaderSize);
        Assert.True(MessageHeader.TryParse(afterSofh, out var header, out _));
        Assert.Equal(QuoteRequestData.MESSAGE_ID, (int)header.TemplateId);

        var payload = afterSofh.Slice(MessageHeader.MESSAGE_SIZE);
        Assert.True(QuoteRequestData.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(1001UL, (ulong)data.QuoteReqID);
        Assert.Equal(4242UL, (ulong)data.SecurityID);
        Assert.Equal(100UL, (ulong)data.OrderQty);
        Assert.Equal(99u, (uint)data.ContraBroker);
        Assert.Equal(SbeSettlType.MUTUAL, data.SettlType);
        Assert.Equal(30, (ushort)data.DaysToSettlement);
        Assert.Equal(1234000000L, data.Price.Mantissa);
        Assert.Equal(1250000L, data.FixedRate.Mantissa);
        Assert.Equal(42u, (uint)data.BusinessHeader.MsgSeqNum);
    }

    [Fact]
    public void EncodeQuote_RoundTripsViaSbeReader()
    {
        var quote = new QuoteMessage
        {
            QuoteId = "2002",
            SecurityId = 4242,
            Side = Side.Sell,
            OrderQty = 50,
            SettlType = SettlementType.BuyersDiscretion,
            DaysToSettlement = 60,
            Price = 5.5m,
            QuoteReqId = "1001",
            FixedRate = 0.001m,
            Account = 5555,
        };

        var buffer = new byte[QuoteData.MESSAGE_SIZE + 32];
        var len = OrderEntryEncoder.EncodeQuote(buffer, quote, Options(), msgSeqNum: 7);

        var afterSofh = buffer.AsSpan(SofhFrameReader.HeaderSize, len - SofhFrameReader.HeaderSize);
        Assert.True(MessageHeader.TryParse(afterSofh, out var header, out _));
        Assert.Equal(QuoteData.MESSAGE_ID, (int)header.TemplateId);

        var payload = afterSofh.Slice(MessageHeader.MESSAGE_SIZE);
        Assert.True(QuoteData.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(2002UL, (ulong)data.QuoteID);
        Assert.Equal(1001UL, (ulong)data.QuoteReqID);
        Assert.Equal(4242UL, (ulong)data.SecurityID);
        Assert.Equal(50UL, (ulong)data.OrderQty);
        Assert.Equal((byte)Side.Sell, (byte)data.Side);
        Assert.Equal(SbeSettlType.BUYERS_DISCRETION, data.SettlType);
        Assert.Equal(60, (ushort)data.DaysToSettlement);
        Assert.Equal(550000000L, data.Price.Mantissa);
        Assert.Equal(100000L, data.FixedRate.Mantissa);
        Assert.Equal(5555u, data.Account);
        Assert.Equal(7u, (uint)data.BusinessHeader.MsgSeqNum);
    }

    [Fact]
    public void EncodeQuoteCancel_RoundTripsViaSbeReader()
    {
        var buffer = new byte[QuoteCancelData.MESSAGE_SIZE + 32];
        var len = OrderEntryEncoder.EncodeQuoteCancel(buffer, "2002", securityId: 4242, Options(), msgSeqNum: 9);

        var afterSofh = buffer.AsSpan(SofhFrameReader.HeaderSize, len - SofhFrameReader.HeaderSize);
        Assert.True(MessageHeader.TryParse(afterSofh, out var header, out _));
        Assert.Equal(QuoteCancelData.MESSAGE_ID, (int)header.TemplateId);

        var payload = afterSofh.Slice(MessageHeader.MESSAGE_SIZE);
        Assert.True(QuoteCancelData.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(2002UL, data.QuoteID);
        Assert.Equal(4242UL, (ulong)data.SecurityID);
        Assert.Equal(9u, (uint)data.BusinessHeader.MsgSeqNum);
    }

    [Fact]
    public void EncodeQuoteRequest_RejectsNonNumericQuoteReqId()
    {
        var req = new QuoteRequestMessage
        {
            QuoteReqId = "not-a-number",
            SecurityId = 1,
            Side = Side.Buy,
            Price = 1m,
            OrderQty = 1,
            SettlType = SettlementType.Mutual,
            DaysToSettlement = 1,
            ContraBroker = 1,
        };
        var buf = new byte[QuoteRequestData.MESSAGE_SIZE + 32];
        Assert.Throws<ArgumentException>(() => OrderEntryEncoder.EncodeQuoteRequest(buf, req, Options(), 1));
    }
}
