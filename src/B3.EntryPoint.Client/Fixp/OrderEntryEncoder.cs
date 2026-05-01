using System.Buffers.Binary;
using System.Text;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Framing;
using B3.EntryPoint.Client.Models;
using SbeClOrdID = B3.Entrypoint.Fixp.Sbe.V6.ClOrdID;
using ClOrdID = B3.EntryPoint.Client.Models.ClOrdID;
using SbeSide = B3.Entrypoint.Fixp.Sbe.V6.Side;
using SbeOrdType = B3.Entrypoint.Fixp.Sbe.V6.OrdType;
using SbeSimpleOrdType = B3.Entrypoint.Fixp.Sbe.V6.SimpleOrdType;
using SbeTimeInForce = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce;
using SbeSimpleTimeInForce = B3.Entrypoint.Fixp.Sbe.V6.SimpleTimeInForce;
using SbeAccountType = B3.Entrypoint.Fixp.Sbe.V6.AccountType;
using SbeMassActionType = B3.Entrypoint.Fixp.Sbe.V6.MassActionType;
using SbeMassActionScope = B3.Entrypoint.Fixp.Sbe.V6.MassActionScope;
using SbeSettlType = B3.Entrypoint.Fixp.Sbe.V6.SettlType;
using SbeExecuteUnderlyingTrade = B3.Entrypoint.Fixp.Sbe.V6.ExecuteUnderlyingTrade;
using SbeCrossType = B3.Entrypoint.Fixp.Sbe.V6.CrossType;
using SbeCrossPrioritization = B3.Entrypoint.Fixp.Sbe.V6.CrossPrioritization;

namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Encodes Order Entry application messages (NewOrderSingle, SimpleNewOrder,
/// OrderCancelReplaceRequest, SimpleModifyOrder, OrderCancelRequest,
/// OrderMassActionRequest) into SOFH-framed SBE buffers. Maps the public
/// <see cref="Models"/> DTOs to wire fields per the v8.4.2 schema.
/// </summary>
internal static class OrderEntryEncoder
{
    private const int SofhSize = SofhFrameReader.HeaderSize;
    private const int SbeHeaderSize = MessageHeader.MESSAGE_SIZE;
    private const sbyte PriceExponent = -4;

    private static long PriceMantissa(decimal price) =>
        (long)(price * 10_000m);

    private static InboundBusinessHeader BuildBusinessHeader(EntryPointClientOptions options, ulong msgSeqNum)
    {
        var header = default(InboundBusinessHeader);
        header.SessionID = new SessionID(options.SessionId);
        header.MsgSeqNum = new SeqNum(checked((uint)msgSeqNum));
        header.SendingTime = default;
        header.MarketSegmentID = new MarketSegmentID(options.DefaultMarketSegmentId);
        return header;
    }

    private static BidirectionalBusinessHeader BuildBidirectionalBusinessHeader(EntryPointClientOptions options, ulong msgSeqNum)
    {
        var header = default(BidirectionalBusinessHeader);
        header.SessionID = new SessionID(options.SessionId);
        header.MsgSeqNum = new SeqNum(checked((uint)msgSeqNum));
        header.SendingTime = default;
        // MarketSegmentID is optional on bidirectional messages; left at NullValue (0) to mean "not set".
        if (options.DefaultMarketSegmentId != 0)
            MemoryMarshalAsBytes(ref header, 17, 1)[0] = options.DefaultMarketSegmentId;
        return header;
    }

    private static long Percentage8Mantissa(decimal value) =>
        (long)(value * 100_000_000m);

    private static long Price8Mantissa(decimal value) =>
        (long)(value * 100_000_000m);

    private static void WriteHeaders(Span<byte> buffer, int totalSize, Action<Span<byte>> writeSbeHeader)
    {
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        writeSbeHeader(buffer.Slice(SofhSize));
    }

    private static void WriteFixedString(Span<byte> destination, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            destination.Clear();
            return;
        }
        var byteCount = Encoding.ASCII.GetByteCount(value);
        if (byteCount > destination.Length)
            throw new ArgumentException(
                $"Value '{value}' exceeds wire length {destination.Length}.", nameof(value));
        Encoding.ASCII.GetBytes(value, destination);
        destination.Slice(byteCount).Clear();
    }

    /// <summary>Encodes a SimpleNewOrder (template id 100). Returns total bytes written.</summary>
    public static int EncodeSimpleNewOrder(
        Span<byte> buffer,
        SimpleNewOrderRequest request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var memo = MemoBytes(request.Account is null ? null : null); // SimpleNewOrder has no memo per schema
        var payloadSize = SimpleNewOrderData.MESSAGE_SIZE + 1 + 0;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize,
            payload => SimpleNewOrderData.WriteHeader(payload));

        var msg = default(SimpleNewOrderData);
        msg.BusinessHeader = BuildBusinessHeader(options, msgSeqNum);
        msg.MmProtectionReset = global::B3.Entrypoint.Fixp.Sbe.V6.Boolean.FALSE_VALUE;
        msg.ClOrdID = new SbeClOrdID(request.ClOrdID.Value);
        msg.SetAccount(request.Account is null ? null : (uint?)checked((uint)request.Account.Value));
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 32, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 42, 5), options.EnteringTrader);
        msg.SecurityID = new SecurityID(request.SecurityId);
        msg.Side = (SbeSide)(byte)request.Side;
        msg.OrdType = (SbeSimpleOrdType)(byte)request.OrderType;
        msg.TimeInForce = (SbeSimpleTimeInForce)(byte)request.TimeInForce;
        msg.OrderQty = new Quantity(request.OrderQty);
        if (request.Price.HasValue)
        {
            // PriceOptional has private mantissa; write directly.
            var mantissa = PriceMantissa(request.Price.Value);
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), mantissa);
        }
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), long.MinValue);
        }

        if (!SimpleNewOrderData.TryEncode(msg, buffer.Slice(SofhSize + SbeHeaderSize), ReadOnlySpan<byte>.Empty, out _))
            throw new InvalidOperationException("Failed to encode SimpleNewOrderData.");
        return totalSize;
    }

    /// <summary>Encodes a SimpleModifyOrder (template id 101). Returns total bytes written.</summary>
    public static int EncodeSimpleModifyOrder(
        Span<byte> buffer,
        SimpleModifyRequest request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var payloadSize = SimpleModifyOrderData.MESSAGE_SIZE + 1 + 0;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => SimpleModifyOrderData.WriteHeader(p));

        var msg = default(SimpleModifyOrderData);
        msg.BusinessHeader = BuildBusinessHeader(options, msgSeqNum);
        msg.ClOrdID = new SbeClOrdID(request.ClOrdID.Value);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 32, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 42, 5), options.EnteringTrader);
        msg.SecurityID = new SecurityID(request.SecurityId);
        msg.Side = (SbeSide)(byte)request.Side;
        msg.OrdType = (SbeSimpleOrdType)(byte)request.OrderType;
        msg.TimeInForce = (SbeSimpleTimeInForce)(byte)request.TimeInForce;
        msg.OrderQty = new Quantity(request.OrderQty);
        if (request.Price.HasValue)
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), PriceMantissa(request.Price.Value));
        else
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), long.MinValue);
        msg.SetOrigClOrdID((ulong?)request.OrigClOrdID.Value);

        if (!SimpleModifyOrderData.TryEncode(msg, buffer.Slice(SofhSize + SbeHeaderSize), ReadOnlySpan<byte>.Empty, out _))
            throw new InvalidOperationException("Failed to encode SimpleModifyOrderData.");
        return totalSize;
    }

    /// <summary>Encodes an OrderCancelRequest (template id 104). Returns total bytes written.</summary>
    public static int EncodeOrderCancel(
        Span<byte> buffer,
        CancelOrderRequest request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var memoBytes = MemoBytes(request.MemoText);
        var payloadSize = OrderCancelRequestData.MESSAGE_SIZE + 1 + 0 + 1 + memoBytes.Length;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => OrderCancelRequestData.WriteHeader(p));

        var msg = default(OrderCancelRequestData);
        msg.BusinessHeader = BuildBusinessHeader(options, msgSeqNum);
        msg.ClOrdID = new SbeClOrdID(request.ClOrdID.Value);
        msg.SecurityID = new SecurityID(request.SecurityId);
        msg.SetOrigClOrdID(request.OrigClOrdID.Value);
        msg.Side = (SbeSide)(byte)request.Side;
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 56, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 66, 5), options.EnteringTrader);

        if (!OrderCancelRequestData.TryEncode(msg, buffer.Slice(SofhSize + SbeHeaderSize),
                ReadOnlySpan<byte>.Empty, memoBytes, out _))
            throw new InvalidOperationException("Failed to encode OrderCancelRequestData.");
        return totalSize;
    }

    /// <summary>Encodes a NewOrderSingle (template id 102). Returns total bytes written.</summary>
    public static int EncodeNewOrderSingle(
        Span<byte> buffer,
        NewOrderRequest request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var memoBytes = MemoBytes(request.MemoText);
        var payloadSize = NewOrderSingleData.MESSAGE_SIZE + 1 + 0 + 1 + memoBytes.Length;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => NewOrderSingleData.WriteHeader(p));

        var msg = default(NewOrderSingleData);
        msg.BusinessHeader = BuildBusinessHeader(options, msgSeqNum);
        msg.MmProtectionReset = global::B3.Entrypoint.Fixp.Sbe.V6.Boolean.FALSE_VALUE;
        msg.ClOrdID = new SbeClOrdID(request.ClOrdID.Value);
        msg.SetAccount(request.Account is null ? null : checked((uint)request.Account.Value));
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 32, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 42, 5), options.EnteringTrader);
        msg.SecurityID = new SecurityID(request.SecurityId);
        msg.Side = (SbeSide)(byte)request.Side;
        msg.OrdType = (SbeOrdType)(byte)request.OrderType;
        msg.TimeInForce = (SbeTimeInForce)(byte)request.TimeInForce;
        msg.OrderQty = new Quantity(request.OrderQty);
        if (request.Price.HasValue)
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), PriceMantissa(request.Price.Value));
        else
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), long.MinValue);
        if (request.StopPrice.HasValue)
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 76, 8), PriceMantissa(request.StopPrice.Value));
        else
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 76, 8), long.MinValue);
        if (request.MinQty.HasValue)
            BinaryPrimitives.WriteUInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 84, 8), request.MinQty.Value);
        if (request.MaxFloor.HasValue)
            BinaryPrimitives.WriteUInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 92, 8), request.MaxFloor.Value);
        MemoryMarshalAsBytes(ref msg, 100, 1)[0] = (byte)request.AccountType;
        if (request.ExpireDate.HasValue)
            BinaryPrimitives.WriteUInt16LittleEndian(MemoryMarshalAsBytes(ref msg, 105, 2),
                (ushort)((request.ExpireDate.Value - DateTimeOffset.UnixEpoch).Days));

        if (!NewOrderSingleData.TryEncode(msg, buffer.Slice(SofhSize + SbeHeaderSize),
                ReadOnlySpan<byte>.Empty, memoBytes, out _))
            throw new InvalidOperationException("Failed to encode NewOrderSingleData.");
        return totalSize;
    }

    /// <summary>Encodes an OrderCancelReplaceRequest (template id 103). Returns total bytes written.</summary>
    public static int EncodeOrderCancelReplace(
        Span<byte> buffer,
        ReplaceOrderRequest request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var memoBytes = MemoBytes(request.MemoText);
        var payloadSize = OrderCancelReplaceRequestData.MESSAGE_SIZE + 1 + 0 + 1 + memoBytes.Length;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => OrderCancelReplaceRequestData.WriteHeader(p));

        var msg = default(OrderCancelReplaceRequestData);
        msg.BusinessHeader = BuildBusinessHeader(options, msgSeqNum);
        msg.MmProtectionReset = global::B3.Entrypoint.Fixp.Sbe.V6.Boolean.FALSE_VALUE;
        msg.ClOrdID = new SbeClOrdID(request.ClOrdID.Value);
        msg.SetAccount(request.Account is null ? null : checked((uint)request.Account.Value));
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 32, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 42, 5), options.EnteringTrader);
        msg.SecurityID = new SecurityID(request.SecurityId);
        msg.Side = (SbeSide)(byte)request.Side;
        msg.OrdType = (SbeOrdType)(byte)request.OrderType;
        msg.SetTimeInForce((SbeTimeInForce)(byte)request.TimeInForce);
        msg.OrderQty = new Quantity(request.OrderQty);
        if (request.Price.HasValue)
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), PriceMantissa(request.Price.Value));
        else
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 68, 8), long.MinValue);
        msg.SetOrigClOrdID(request.OrigClOrdID.Value);
        if (request.StopPrice.HasValue)
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 84, 8), PriceMantissa(request.StopPrice.Value));
        else
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 84, 8), long.MinValue);
        if (request.MinQty.HasValue)
            BinaryPrimitives.WriteUInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 92, 8), request.MinQty.Value);
        if (request.MaxFloor.HasValue)
            BinaryPrimitives.WriteUInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 100, 8), request.MaxFloor.Value);
        MemoryMarshalAsBytes(ref msg, 113, 1)[0] = (byte)request.AccountType;
        if (request.ExpireDate.HasValue)
            BinaryPrimitives.WriteUInt16LittleEndian(MemoryMarshalAsBytes(ref msg, 114, 2),
                (ushort)((request.ExpireDate.Value - DateTimeOffset.UnixEpoch).Days));

        if (!OrderCancelReplaceRequestData.TryEncode(msg, buffer.Slice(SofhSize + SbeHeaderSize),
                ReadOnlySpan<byte>.Empty, memoBytes, out _))
            throw new InvalidOperationException("Failed to encode OrderCancelReplaceRequestData.");
        return totalSize;
    }

    /// <summary>Encodes an OrderMassActionRequest (template id 701). Returns total bytes written.</summary>
    public static int EncodeOrderMassAction(
        Span<byte> buffer,
        MassActionRequest request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var totalSize = SofhSize + SbeHeaderSize + OrderMassActionRequestData.MESSAGE_SIZE;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => OrderMassActionRequestData.WriteHeader(p));

        var msg = default(OrderMassActionRequestData);
        msg.BusinessHeader = BuildBusinessHeader(options, msgSeqNum);
        msg.MassActionType = (SbeMassActionType)(byte)request.ActionType;
        msg.SetMassActionScope((SbeMassActionScope?)(byte?)request.Scope);
        msg.ClOrdID = new SbeClOrdID(request.ClOrdID.Value);
        if (request.Side.HasValue)
            msg.SetSide((SbeSide?)(byte?)request.Side.Value);
        if (request.SecurityId.HasValue)
            msg.SetSecurityID(request.SecurityId.Value);

        if (!msg.TryEncode(buffer.Slice(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode OrderMassActionRequestData.");
        return totalSize;
    }

    private static ReadOnlySpan<byte> MemoBytes(string? memo) =>
        string.IsNullOrEmpty(memo) ? ReadOnlySpan<byte>.Empty : Encoding.UTF8.GetBytes(memo);

    /// <summary>Encodes a NewOrderCross (template id 106). Returns total bytes written.</summary>
    public static int EncodeNewOrderCross(
        Span<byte> buffer,
        NewOrderCrossRequest request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        if (request.Legs is null || request.Legs.Count == 0)
            throw new ArgumentException("NewOrderCross requires at least one leg.", nameof(request));

        const int GroupSizeEncodingSize = 4; // BlockLength (ushort) + NumInGroup (ushort)
        var noSidesPayloadSize = GroupSizeEncodingSize + (NewOrderCrossData.NoSidesData.MESSAGE_SIZE * request.Legs.Count);
        // Variable-length data length encoding is 1 byte each (per existing pattern in EncodeNewOrderSingle).
        var deskIdLen = 0;
        var memoBytes = ReadOnlySpan<byte>.Empty;
        var payloadSize = NewOrderCrossData.MESSAGE_SIZE + noSidesPayloadSize + 1 + deskIdLen + 1 + memoBytes.Length;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => NewOrderCrossData.WriteHeader(p));

        var msg = default(NewOrderCrossData);
        msg.BusinessHeader = BuildBusinessHeader(options, msgSeqNum);
        msg.CrossID = new CrossID(ParseCrossId(request.CrossId));
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 28, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 38, 5), options.EnteringTrader);
        msg.SecurityID = new SecurityID(request.SecurityId);
        msg.OrderQty = new Quantity(SumLegQty(request.Legs));
        BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 64, 8), PriceMantissa(request.Price));
        msg.SetCrossType((SbeCrossType?)(byte)request.CrossType);
        msg.SetCrossPrioritization((SbeCrossPrioritization?)(byte)request.Prioritization);

        // Materialize legs into the wire NoSidesData layout.
        var legs = new NewOrderCrossData.NoSidesData[request.Legs.Count];
        for (int i = 0; i < request.Legs.Count; i++)
        {
            var leg = request.Legs[i];
            ref var slot = ref legs[i];
            slot = default;
            slot.Side = (B3.Entrypoint.Fixp.Sbe.V6.Side)(byte)leg.Side;
            if (leg.Account.HasValue)
                BinaryPrimitives.WriteUInt32LittleEndian(MemoryMarshalAsBytes(ref slot, 2, 4), checked((uint)leg.Account.Value));
            BinaryPrimitives.WriteUInt32LittleEndian(MemoryMarshalAsBytes(ref slot, 6, 4), options.EnteringFirm);
            slot.ClOrdID = new SbeClOrdID(leg.ClOrdID.Value);
        }

        if (!NewOrderCrossData.TryEncode(msg, buffer.Slice(SofhSize + SbeHeaderSize), legs,
                ReadOnlySpan<byte>.Empty, memoBytes, out _))
            throw new InvalidOperationException("Failed to encode NewOrderCrossData.");
        return totalSize;
    }

    private static ulong SumLegQty(IReadOnlyList<CrossLeg> legs)
    {
        ulong sum = 0;
        for (int i = 0; i < legs.Count; i++) sum += legs[i].OrderQty;
        return sum;
    }

    private static ulong ParseCrossId(string crossId)
    {
        if (string.IsNullOrEmpty(crossId))
            throw new ArgumentException("CrossId is required.", nameof(crossId));
        if (!ulong.TryParse(crossId, out var value))
            throw new ArgumentException(
                $"CrossId must be a numeric string parseable to ulong (was '{crossId}').", nameof(crossId));
        return value;
    }

    private static ulong ParseQuoteUlongId(string id, string fieldName)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        if (!ulong.TryParse(id, out var value))
            throw new ArgumentException(
                $"{fieldName} must be a numeric string parseable to ulong (was '{id}').", fieldName);
        return value;
    }

    /// <summary>Encodes a QuoteRequest (template id 401) for B3 Termo. Returns total bytes written.</summary>
    public static int EncodeQuoteRequest(
        Span<byte> buffer,
        QuoteRequestMessage request,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var totalSize = SofhSize + SbeHeaderSize + QuoteRequestData.MESSAGE_SIZE;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => QuoteRequestData.WriteHeader(p));

        var msg = default(QuoteRequestData);
        msg.BusinessHeader = BuildBidirectionalBusinessHeader(options, msgSeqNum);
        msg.SecurityID = new SecurityID(request.SecurityId);
        msg.QuoteReqID = new QuoteReqID(ParseQuoteUlongId(request.QuoteReqId, nameof(request.QuoteReqId)));
        if (!string.IsNullOrEmpty(request.QuoteId))
            msg.SetQuoteID(ParseQuoteUlongId(request.QuoteId, nameof(request.QuoteId)));
        if (request.TradeId.HasValue)
            msg.SetTradeID(request.TradeId.Value);
        msg.ContraBroker = new Firm(request.ContraBroker);
        BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 60, 8), Price8Mantissa(request.Price));
        msg.SettlType = (SbeSettlType)(byte)request.SettlType;
        if (request.ExecuteUnderlyingTrade.HasValue)
            msg.SetExecuteUnderlyingTrade((SbeExecuteUnderlyingTrade)(byte)request.ExecuteUnderlyingTrade.Value);
        msg.OrderQty = new Quantity(request.OrderQty);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 78, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 88, 5), options.EnteringTrader);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 93, 5), options.EnteringTrader);
        BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 98, 8), Percentage8Mantissa(request.FixedRate));
        msg.DaysToSettlement = new DaysToSettlement(request.DaysToSettlement);

        if (!msg.TryEncode(buffer.Slice(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode QuoteRequestData.");
        return totalSize;
    }

    /// <summary>Encodes a Quote (template id 403) for B3 Termo. Returns total bytes written.</summary>
    public static int EncodeQuote(
        Span<byte> buffer,
        QuoteMessage quote,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var totalSize = SofhSize + SbeHeaderSize + QuoteData.MESSAGE_SIZE;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => QuoteData.WriteHeader(p));

        var msg = default(QuoteData);
        msg.BusinessHeader = BuildBidirectionalBusinessHeader(options, msgSeqNum);
        msg.SecurityID = new SecurityID(quote.SecurityId);
        if (!string.IsNullOrEmpty(quote.QuoteReqId))
            msg.QuoteReqID = new QuoteReqID(ParseQuoteUlongId(quote.QuoteReqId, nameof(quote.QuoteReqId)));
        msg.QuoteID = new QuoteID(ParseQuoteUlongId(quote.QuoteId, nameof(quote.QuoteId)));
        if (quote.Price.HasValue)
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 52, 8), Price8Mantissa(quote.Price.Value));
        else
            BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 52, 8), long.MinValue);
        msg.OrderQty = new Quantity(quote.OrderQty);
        msg.Side = (SbeSide)(byte)quote.Side;
        msg.SettlType = (SbeSettlType)(byte)quote.SettlType;
        if (quote.Account.HasValue)
            msg.SetAccount(quote.Account.Value);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 74, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 84, 5), options.EnteringTrader);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 89, 5), options.EnteringTrader);
        BinaryPrimitives.WriteInt64LittleEndian(MemoryMarshalAsBytes(ref msg, 94, 8), Percentage8Mantissa(quote.FixedRate));
        if (quote.ExecuteUnderlyingTrade.HasValue)
            msg.SetExecuteUnderlyingTrade((SbeExecuteUnderlyingTrade)(byte)quote.ExecuteUnderlyingTrade.Value);
        msg.DaysToSettlement = new DaysToSettlement(quote.DaysToSettlement);
        if (quote.TradingSubAccount.HasValue)
            msg.SetTradingSubAccount(quote.TradingSubAccount.Value);

        if (!msg.TryEncode(buffer.Slice(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode QuoteData.");
        return totalSize;
    }

    /// <summary>Encodes a QuoteCancel (template id 404). Returns total bytes written.</summary>
    public static int EncodeQuoteCancel(
        Span<byte> buffer,
        string quoteId,
        ulong securityId,
        EntryPointClientOptions options,
        ulong msgSeqNum)
    {
        var totalSize = SofhSize + SbeHeaderSize + QuoteCancelData.MESSAGE_SIZE;
        WriteHeaders(buffer.Slice(0, totalSize), totalSize, p => QuoteCancelData.WriteHeader(p));

        var msg = default(QuoteCancelData);
        msg.BusinessHeader = BuildBidirectionalBusinessHeader(options, msgSeqNum);
        msg.SecurityID = new SecurityID(securityId);
        msg.SetQuoteID(ParseQuoteUlongId(quoteId, nameof(quoteId)));
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 48, 10), options.SenderLocation);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 58, 5), options.EnteringTrader);
        WriteFixedString(MemoryMarshalAsBytes(ref msg, 63, 5), options.EnteringTrader);

        if (!msg.TryEncode(buffer.Slice(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode QuoteCancelData.");
        return totalSize;
    }


    /// <summary>
    /// Hands out a <see cref="Span{Byte}"/> view over a fixed offset/length within a struct
    /// for the few cases where the SBE-generated surface lacks public setters
    /// (e.g. <c>InlineArray</c>-backed text fields, <c>PriceOptional.Mantissa</c>).
    /// </summary>
    private static Span<byte> MemoryMarshalAsBytes<T>(ref T value, int offset, int length) where T : unmanaged
    {
        var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref value, 1);
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(span).Slice(offset, length);
    }
}
