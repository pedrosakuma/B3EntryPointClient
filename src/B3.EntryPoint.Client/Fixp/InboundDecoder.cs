using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Models;
using ClientCancelled = B3.EntryPoint.Client.Models.OrderCancelled;
using ClientEvent = B3.EntryPoint.Client.Models.EntryPointEvent;
using ClientForwarded = B3.EntryPoint.Client.Models.OrderForwarded;
using ClientModified = B3.EntryPoint.Client.Models.OrderModified;
using ClientReject = B3.EntryPoint.Client.Models.OrderRejected;
using ClientTrade = B3.EntryPoint.Client.Models.OrderTrade;
using ClientAccepted = B3.EntryPoint.Client.Models.OrderAccepted;
using ClientBusinessReject = B3.EntryPoint.Client.Models.BusinessReject;
using SbeBmr = B3.Entrypoint.Fixp.Sbe.V6.BusinessMessageRejectData;
using SbeErCancel = B3.Entrypoint.Fixp.Sbe.V6.ExecutionReport_CancelData;
using SbeErForward = B3.Entrypoint.Fixp.Sbe.V6.ExecutionReport_ForwardData;
using SbeErModify = B3.Entrypoint.Fixp.Sbe.V6.ExecutionReport_ModifyData;
using SbeErNew = B3.Entrypoint.Fixp.Sbe.V6.ExecutionReport_NewData;
using SbeErReject = B3.Entrypoint.Fixp.Sbe.V6.ExecutionReport_RejectData;
using SbeErTrade = B3.Entrypoint.Fixp.Sbe.V6.ExecutionReport_TradeData;
using ClientClOrdID = B3.EntryPoint.Client.Models.ClOrdID;

namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Decodes inbound application messages (ExecutionReport family + BusinessMessageReject)
/// into the typed <see cref="EntryPointEvent"/> records exposed via
/// <see cref="EntryPointClient.Events"/>.
/// </summary>
internal static class InboundDecoder
{
    private const int SofhSize = 4;
    private const int SbeHeaderSize = 8;

    /// <summary>Reads templateId (uint16 LE at SOFH+2) from a SOFH-framed SBE message.</summary>
    public static ushort ReadTemplateId(ReadOnlySpan<byte> frame) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(SofhSize + 2, 2));

    /// <summary>
    /// Decodes a SOFH-framed inbound message into an <see cref="EntryPointEvent"/>, if it
    /// belongs to a known application template. Returns false for session-layer messages
    /// (Sequence, NotApplied, Retransmission, Terminate, etc.) which the caller handles.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out ClientEvent? evt)
    {
        evt = null;
        if (frame.Length < SofhSize + SbeHeaderSize)
            return false;
        var templateId = ReadTemplateId(frame);
        var payload = frame[(SofhSize + SbeHeaderSize)..];

        switch (templateId)
        {
            case SbeErNew.MESSAGE_ID:
                evt = DecodeNew(payload);
                return true;
            case SbeErModify.MESSAGE_ID:
                evt = DecodeModify(payload);
                return true;
            case SbeErCancel.MESSAGE_ID:
                evt = DecodeCancel(payload);
                return true;
            case SbeErTrade.MESSAGE_ID:
                evt = DecodeTrade(payload);
                return true;
            case SbeErReject.MESSAGE_ID:
                evt = DecodeReject(payload);
                return true;
            case SbeErForward.MESSAGE_ID:
                evt = DecodeForward(payload);
                return true;
            case SbeBmr.MESSAGE_ID:
                evt = DecodeBmr(payload);
                return true;
            default:
                return false;
        }
    }

    // -- private decoders ----------------------------------------------------

    private static ClientAccepted DecodeNew(ReadOnlySpan<byte> payload)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SbeErNew>(payload);
        return new ClientAccepted
        {
            SeqNum = msg.BusinessHeader.MsgSeqNum.Value,
            SendingTime = ToDateTime(msg.BusinessHeader.SendingTime.Time),
            ClOrdID = new ClientClOrdID(msg.ClOrdID.Value),
            OrderId = msg.OrderID.Value,
            OrderStatus = (OrderStatus)(byte)msg.OrdStatus,
            SecurityId = msg.SecurityID.Value,
            Side = (Models.Side)(byte)msg.Side,
            TransactTime = ToDateTime(msg.TransactTime.Time),
        };
    }

    private static ClientModified DecodeModify(ReadOnlySpan<byte> payload)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SbeErModify>(payload);
        return new ClientModified
        {
            SeqNum = msg.BusinessHeader.MsgSeqNum.Value,
            SendingTime = ToDateTime(msg.BusinessHeader.SendingTime.Time),
            ClOrdID = new ClientClOrdID(msg.ClOrdID.Value),
            OrigClOrdID = new ClientClOrdID(msg.OrigClOrdID ?? 0UL),
            OrderId = msg.OrderID.Value,
            OrderStatus = (OrderStatus)(byte)msg.OrdStatus,
            LeavesQty = msg.LeavesQty.Value,
            CumQty = msg.CumQty.Value,
            TransactTime = ToDateTime(msg.TransactTime.Time),
        };
    }

    private static ClientCancelled DecodeCancel(ReadOnlySpan<byte> payload)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SbeErCancel>(payload);
        return new ClientCancelled
        {
            SeqNum = msg.BusinessHeader.MsgSeqNum.Value,
            SendingTime = ToDateTime(msg.BusinessHeader.SendingTime.Time),
            ClOrdID = new ClientClOrdID(msg.ClOrdID.Value),
            OrigClOrdID = null,
            OrderId = msg.OrderID.Value,
            OrderStatus = (OrderStatus)(byte)msg.OrdStatus,
            TransactTime = ToDateTime(msg.TransactTime.Time),
        };
    }

    private static ClientTrade DecodeTrade(ReadOnlySpan<byte> payload)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SbeErTrade>(payload);
        return new ClientTrade
        {
            SeqNum = msg.BusinessHeader.MsgSeqNum.Value,
            SendingTime = ToDateTime(msg.BusinessHeader.SendingTime.Time),
            ClOrdID = new ClientClOrdID(msg.ClOrdID ?? 0UL),
            OrderId = msg.OrderID.Value,
            TradeId = msg.TradeID.Value,
            OrderStatus = (OrderStatus)(byte)msg.OrdStatus,
            LastPx = MantissaToDecimal(msg.LastPx.Mantissa),
            LastQty = msg.LastQty.Value,
            LeavesQty = msg.LeavesQty.Value,
            CumQty = msg.CumQty.Value,
            TransactTime = ToDateTime(msg.TransactTime.Time),
        };
    }

    private static ClientReject DecodeReject(ReadOnlySpan<byte> payload)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SbeErReject>(payload);
        return new ClientReject
        {
            SeqNum = msg.BusinessHeader.MsgSeqNum.Value,
            SendingTime = ToDateTime(msg.BusinessHeader.SendingTime.Time),
            ClOrdID = new ClientClOrdID(msg.ClOrdID.Value),
            OrderId = 0UL,
            RejectCode = (ushort)msg.OrdRejReason.Value,
            TransactTime = ToDateTime(msg.TransactTime.Time),
        };
    }

    private static ClientForwarded DecodeForward(ReadOnlySpan<byte> payload)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SbeErForward>(payload);
        // Forward doesn't carry a top-level ClOrdID; use SecondaryOrderID as the order id.
        return new ClientForwarded
        {
            SeqNum = msg.BusinessHeader.MsgSeqNum.Value,
            SendingTime = ToDateTime(msg.BusinessHeader.SendingTime.Time),
            ClOrdID = new ClientClOrdID(msg.SecondaryOrderID.Value),
            OrderId = msg.SecondaryOrderID.Value,
            TransactTime = ToDateTime(msg.TransactTime.Time),
        };
    }

    private static ClientBusinessReject DecodeBmr(ReadOnlySpan<byte> payload)
    {
        ref readonly var msg = ref MemoryMarshal.AsRef<SbeBmr>(payload);
        return new ClientBusinessReject
        {
            SeqNum = msg.BusinessHeader.MsgSeqNum.Value,
            SendingTime = ToDateTime(msg.BusinessHeader.SendingTime.Time),
            RefSeqNum = msg.RefSeqNum.Value,
            RejectReason = (ushort)msg.BusinessRejectReason.Value,
        };
    }

    private static DateTimeOffset ToDateTime(ulong unixNanos)
    {
        if (unixNanos == 0UL) return DateTimeOffset.MinValue;
        var ticks = (long)(unixNanos / 100UL);
        return DateTimeOffset.UnixEpoch.AddTicks(ticks);
    }

    private static DateTimeOffset ToDateTime(ulong? unixNanos) =>
        unixNanos.HasValue ? ToDateTime(unixNanos.Value) : DateTimeOffset.MinValue;

    private static decimal MantissaToDecimal(long mantissa)
    {
        // Schema fixes Exponent at -4 for prices.
        return mantissa / 10_000m;
    }
}
