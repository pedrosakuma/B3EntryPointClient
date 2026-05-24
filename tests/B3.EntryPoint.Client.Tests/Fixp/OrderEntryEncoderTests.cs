using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using SbeOcrr = B3.Entrypoint.Fixp.Sbe.V6.OrderCancelReplaceRequestData;
using SbeAccountType = B3.Entrypoint.Fixp.Sbe.V6.AccountType;
using SbeNewOrderCross = B3.Entrypoint.Fixp.Sbe.V6.NewOrderCrossData;
using SbeQuoteRequest = B3.Entrypoint.Fixp.Sbe.V6.QuoteRequestData;
using SbeQuote = B3.Entrypoint.Fixp.Sbe.V6.QuoteData;
using SbeSide = B3.Entrypoint.Fixp.Sbe.V6.Side;
using SbeSettlType = B3.Entrypoint.Fixp.Sbe.V6.SettlType;
using SbeCrossType = B3.Entrypoint.Fixp.Sbe.V6.CrossType;
using SbeCrossPrioritization = B3.Entrypoint.Fixp.Sbe.V6.CrossPrioritization;
using SbeExecuteUnderlyingTrade = B3.Entrypoint.Fixp.Sbe.V6.ExecuteUnderlyingTrade;
using SbeSenderLocation = B3.Entrypoint.Fixp.Sbe.V6.SenderLocation;
using SbeTrader = B3.Entrypoint.Fixp.Sbe.V6.Trader;

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

    // Regression test for issue #179: the encoder used to write
    // `(byte)request.AccountType` at offset 100 of NewOrderSingleData. Schema
    // 8.4.2 has no accountType field in NewOrderSingle (only in
    // OrderCancelReplaceRequest); offset 100 is the start of ExecutingTrader
    // (TraderOptional, 5 bytes), so the write silently corrupted the first
    // byte of ExecutingTrader with ASCII 38/39.
    [Fact]
    public void EncodeNewOrderSingle_DoesNotCorruptExecutingTrader_Issue179()
    {
        var req = new NewOrderRequest
        {
            ClOrdID = new ClOrdID(99UL),
            SecurityId = 1,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            OrderQty = 10,
            Price = 0.05m,
        };
        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeNewOrderSingle(buffer, req, Opts(), msgSeqNum: 3);

        // Frame layout: SOFH (4) + MessageHeader (8) + payload.
        var payload = buffer.AsSpan(4 + 8, len - 4 - 8);
        Assert.True(B3.Entrypoint.Fixp.Sbe.V6.NewOrderSingleData.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        // ExecutingTrader is at offset 100. With the bug it would have decoded
        // as "&\0\0\0\0" (38) since AccountType defaults to RegularAccount (39).
        Assert.Equal(0, data.ExecutingTrader.AsTrimmedSpan().Length);
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

    // Regression test for issue #145: OCRR encoder used hand-coded offsets
    // shifted by -8 (pre-V6 layout, before orderID@76 was added), which both
    // clobbered OrigClOrdID with the StopPx null sentinel (long.MinValue) and
    // wrote MinQty/MaxFloor/AccountType/ExpireDate at the wrong offsets.
    [Fact]
    public void EncodeOrderCancelReplace_RoundTrips_OptionalFields_ToSbeDecoder()
    {
        const ulong OrigClOrd = 0x1122334455667788UL;
        var expireDate = new DateTimeOffset(2030, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var expireDays = (ushort)((expireDate - DateTimeOffset.UnixEpoch).Days);

        var req = new ReplaceOrderRequest
        {
            ClOrdID = new ClOrdID(2UL),
            OrigClOrdID = new ClOrdID(OrigClOrd),
            SecurityId = 7,
            Side = Side.Sell,
            OrderType = OrderType.StopLimit,
            OrderQty = 50,
            Price = 12.34m,
            StopPrice = 11.50m,
            MinQty = 10UL,
            MaxFloor = 25UL,
            AccountType = AccountType.RegularAccount,
            ExpireDate = expireDate,
        };

        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeOrderCancelReplace(buffer, req, Opts(), msgSeqNum: 4);
        Assert.True(len > 0);

        // Frame layout: SOFH (4) + MessageHeader (8) + payload.
        var payload = buffer.AsSpan(4 + 8, len - 4 - 8);
        Assert.True(SbeOcrr.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(OrigClOrd, data.OrigClOrdID);
        Assert.NotEqual(unchecked((ulong)long.MinValue), data.OrigClOrdID);
        Assert.Equal(115_000L, data.StopPx.Mantissa);
        Assert.Equal(10UL, data.MinQty);
        Assert.Equal(25UL, data.MaxFloor);
        Assert.Equal(SbeAccountType.REGULAR_ACCOUNT, data.AccountType);
        Assert.Equal(expireDays, data.ExpireDate);
        // Sanity: also confirm the basic fields encoded correctly.
        Assert.Equal(2UL, data.ClOrdID.Value);
        Assert.Equal(7UL, data.SecurityID.Value);
        Assert.Equal(50UL, data.OrderQty.Value);
        Assert.Equal(123_400L, data.Price.Mantissa);
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

    // --- Round-trip coverage for cross/quote families (audit follow-up) ----
    // Encode via the client's encoder, decode via the generated SBE TryParse,
    // and verify every field — including all optional fields — survives the
    // wire round-trip. Catches mis-aligned offsets, wrong null sentinels, and
    // wrong group encoding the same way the OCRR regression in #145 did.

    private const int SofhSize = 4;
    private const int SbeHeaderSize = 8;

    private static string TrimAscii(ReadOnlySpan<byte> span)
    {
        var idx = span.IndexOf((byte)0);
        if (idx >= 0) span = span.Slice(0, idx);
        while (span.Length > 0 && span[^1] == (byte)' ')
            span = span.Slice(0, span.Length - 1);
        return Encoding.ASCII.GetString(span);
    }

    // SBE InlineArray fixed-strings (Trader, SenderLocation) returned by struct
    // properties are temporaries; copy bytes through MemoryMarshal so we don't
    // hit "may not be passed by reference" scoping errors.
    private static string SenderLocStr(SbeSenderLocation v)
    {
        Span<byte> tmp = stackalloc byte[10];
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref v, 1)).CopyTo(tmp);
        return TrimAscii(tmp);
    }

    private static string TraderStr(SbeTrader v)
    {
        Span<byte> tmp = stackalloc byte[5];
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref v, 1)).CopyTo(tmp);
        return TrimAscii(tmp);
    }

    [Fact]
    public void EncodeNewOrderCross_RoundTrips_AllFieldsAndMultiLeg_ToSbeDecoder()
    {
        var legs = new List<CrossLeg>
        {
            new()
            {
                ClOrdID = new ClOrdID(0xAAAA1111UL),
                Side = Side.Buy,
                OrderQty = 40,
                Account = 12345UL,
            },
            new()
            {
                ClOrdID = new ClOrdID(0xBBBB2222UL),
                Side = Side.Sell,
                OrderQty = 60,
                Account = 67890UL,
            },
        };

        var req = new NewOrderCrossRequest
        {
            CrossId = "9876543210",
            SecurityId = 4242UL,
            CrossType = CrossType.VwapCross,
            Prioritization = CrossPrioritization.SellSidePrioritized,
            Price = 10.55m,
            Legs = legs,
        };

        var opts = Opts();
        var buffer = new byte[1024];
        var len = OrderEntryEncoder.EncodeNewOrderCross(buffer, req, opts, msgSeqNum: 11);
        var (sofhLen, tid) = ReadFrameHeader(buffer);
        Assert.Equal(len, sofhLen);
        Assert.Equal((ushort)106, tid);

        var payload = buffer.AsSpan(SofhSize + SbeHeaderSize, len - SofhSize - SbeHeaderSize);
        Assert.True(SbeNewOrderCross.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(opts.SessionId, data.BusinessHeader.SessionID.Value);
        Assert.Equal(11U, data.BusinessHeader.MsgSeqNum.Value);
        Assert.Equal(opts.DefaultMarketSegmentId, data.BusinessHeader.MarketSegmentID.Value);
        Assert.Equal(9876543210UL, data.CrossID.Value);
        Assert.Equal(opts.SenderLocation, SenderLocStr(data.SenderLocation));
        Assert.Equal(opts.EnteringTrader, TraderStr(data.EnteringTrader));
        Assert.Equal(req.SecurityId, data.SecurityID.Value);
        Assert.Equal(100UL, data.OrderQty.Value); // 40 + 60
        Assert.Equal(105_500L, data.Price.Mantissa); // 10.55 * 1e4
        Assert.Equal(SbeCrossType.VWAP_CROSS, data.CrossType);
        Assert.Equal(SbeCrossPrioritization.SELL_SIDE_IS_PRIORITIZED, data.CrossPrioritization);

        var decodedLegs = new List<(SbeSide side, uint account, uint firm, ulong clord)>();
        foreach (ref readonly var leg in reader.NoSides)
        {
            decodedLegs.Add((leg.Side, leg.Account.Value!.Value, leg.EnteringFirm.Value!.Value, leg.ClOrdID.Value));
        }
        Assert.Equal(2, decodedLegs.Count);
        Assert.Equal(SbeSide.BUY, decodedLegs[0].side);
        Assert.Equal(12345U, decodedLegs[0].account);
        Assert.Equal(opts.EnteringFirm, decodedLegs[0].firm);
        Assert.Equal(0xAAAA1111UL, decodedLegs[0].clord);
        Assert.Equal(SbeSide.SELL, decodedLegs[1].side);
        Assert.Equal(67890U, decodedLegs[1].account);
        Assert.Equal(opts.EnteringFirm, decodedLegs[1].firm);
        Assert.Equal(0xBBBB2222UL, decodedLegs[1].clord);
    }

    [Fact]
    public void EncodeQuoteRequest_RoundTrips_AllOptionalFieldsPopulated_ToSbeDecoder()
    {
        var req = new QuoteRequestMessage
        {
            QuoteReqId = "1122334455",
            SecurityId = 7777UL,
            Side = Side.Buy, // not present on wire; DTO field is unused on encode.
            Price = 12.34567890m,
            OrderQty = 250,
            SettlType = SettlementType.Mutual,
            DaysToSettlement = 30,
            ContraBroker = 9988U,
            FixedRate = 0.0125m,
            QuoteId = "55667788",
            TradeId = 424242U,
            ExecuteUnderlyingTrade = ExecuteUnderlyingTrade.UnderlyingOpposingTrade,
        };

        var opts = Opts();
        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeQuoteRequest(buffer, req, opts, msgSeqNum: 21);
        var (sofhLen, tid) = ReadFrameHeader(buffer);
        Assert.Equal(len, sofhLen);
        Assert.Equal((ushort)401, tid);

        var payload = buffer.AsSpan(SofhSize + SbeHeaderSize, len - SofhSize - SbeHeaderSize);
        Assert.True(SbeQuoteRequest.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(opts.SessionId, data.BusinessHeader.SessionID.Value);
        Assert.Equal(21U, data.BusinessHeader.MsgSeqNum.Value);
        Assert.Equal(req.SecurityId, data.SecurityID.Value);
        Assert.Equal(1122334455UL, data.QuoteReqID.Value);
        Assert.Equal(55667788UL, data.QuoteID);
        Assert.Equal(424242U, data.TradeID);
        Assert.Equal(req.ContraBroker, data.ContraBroker.Value);
        Assert.Equal(1_234_567_890L, data.Price.Mantissa); // 12.34567890 * 1e8
        Assert.Equal(SbeSettlType.MUTUAL, data.SettlType);
        Assert.Equal(SbeExecuteUnderlyingTrade.UNDERLYING_OPPOSING_TRADE, data.ExecuteUnderlyingTrade);
        Assert.Equal(req.OrderQty, data.OrderQty.Value);
        Assert.Equal(opts.SenderLocation, SenderLocStr(data.SenderLocation));
        Assert.Equal(opts.EnteringTrader, TraderStr(data.EnteringTrader));
        Assert.Equal(opts.EnteringTrader, TraderStr(data.ExecutingTrader));
        Assert.Equal(1_250_000L, data.FixedRate.Mantissa); // 0.0125 * 1e8
        Assert.Equal(req.DaysToSettlement, data.DaysToSettlement.Value);
    }

    [Fact]
    public void EncodeQuoteRequest_RoundTrips_OmittedOptionalsAreNull_ToSbeDecoder()
    {
        var req = new QuoteRequestMessage
        {
            QuoteReqId = "1",
            SecurityId = 1UL,
            Side = Side.Sell,
            Price = 1m,
            OrderQty = 1,
            SettlType = SettlementType.BuyersDiscretion,
            DaysToSettlement = 16,
            ContraBroker = 1U,
            // QuoteId / TradeId / ExecuteUnderlyingTrade left null
        };

        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeQuoteRequest(buffer, req, Opts(), msgSeqNum: 22);
        var payload = buffer.AsSpan(SofhSize + SbeHeaderSize, len - SofhSize - SbeHeaderSize);
        Assert.True(SbeQuoteRequest.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Null(data.QuoteID);
        Assert.Null(data.TradeID);
        Assert.Null(data.ExecuteUnderlyingTrade);
    }

    [Fact]
    public void EncodeQuote_RoundTrips_AllOptionalFieldsPopulated_ToSbeDecoder()
    {
        var quote = new QuoteMessage
        {
            QuoteId = "1357924680",
            SecurityId = 5555UL,
            Side = Side.Sell,
            OrderQty = 333,
            SettlType = SettlementType.SellersDiscretion,
            DaysToSettlement = 60,
            Price = 99.87654321m,
            FixedRate = 0.0250m,
            QuoteReqId = "9988776655",
            Account = 7777U,
            TradingSubAccount = 8888U,
            ExecuteUnderlyingTrade = ExecuteUnderlyingTrade.NoUnderlyingTrade,
        };

        var opts = Opts();
        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeQuote(buffer, quote, opts, msgSeqNum: 31);
        var (sofhLen, tid) = ReadFrameHeader(buffer);
        Assert.Equal(len, sofhLen);
        Assert.Equal((ushort)403, tid);

        var payload = buffer.AsSpan(SofhSize + SbeHeaderSize, len - SofhSize - SbeHeaderSize);
        Assert.True(SbeQuote.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(opts.SessionId, data.BusinessHeader.SessionID.Value);
        Assert.Equal(31U, data.BusinessHeader.MsgSeqNum.Value);
        Assert.Equal(quote.SecurityId, data.SecurityID.Value);
        Assert.Equal(9988776655UL, data.QuoteReqID.Value);
        Assert.Equal(1357924680UL, data.QuoteID.Value);
        Assert.Equal(9_987_654_321L, data.Price.Mantissa); // 99.87654321 * 1e8
        Assert.Equal(quote.OrderQty, data.OrderQty.Value);
        Assert.Equal(SbeSide.SELL, data.Side);
        Assert.Equal(SbeSettlType.SELLERS_DISCRETION, data.SettlType);
        Assert.Equal(7777U, data.Account);
        Assert.Equal(opts.SenderLocation, SenderLocStr(data.SenderLocation));
        Assert.Equal(opts.EnteringTrader, TraderStr(data.EnteringTrader));
        Assert.Equal(opts.EnteringTrader, TraderStr(data.ExecutingTrader));
        Assert.Equal(2_500_000L, data.FixedRate.Mantissa); // 0.025 * 1e8
        Assert.Equal(SbeExecuteUnderlyingTrade.NO_UNDERLYING_TRADE, data.ExecuteUnderlyingTrade);
        Assert.Equal(quote.DaysToSettlement, data.DaysToSettlement.Value);
        Assert.Equal(8888U, data.TradingSubAccount);
    }

    [Fact]
    public void EncodeQuote_RoundTrips_NullPriceUsesNullSentinel_ToSbeDecoder()
    {
        // Quote with no price (e.g. pass-back style); encoder writes long.MinValue
        // at offset 52, decoder must surface it as Mantissa==null.
        var quote = new QuoteMessage
        {
            QuoteId = "1",
            SecurityId = 1UL,
            Side = Side.Buy,
            OrderQty = 1,
            SettlType = SettlementType.BuyersDiscretion,
            DaysToSettlement = 16,
            FixedRate = 0m,
            // Price, QuoteReqId, Account, TradingSubAccount, ExecuteUnderlyingTrade all null
        };
        var buffer = new byte[512];
        var len = OrderEntryEncoder.EncodeQuote(buffer, quote, Opts(), msgSeqNum: 32);
        var payload = buffer.AsSpan(SofhSize + SbeHeaderSize, len - SofhSize - SbeHeaderSize);
        Assert.True(SbeQuote.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Null(data.Price.Mantissa);
        Assert.Null(data.Account);
        Assert.Null(data.TradingSubAccount);
        Assert.Null(data.ExecuteUnderlyingTrade);
    }
}
