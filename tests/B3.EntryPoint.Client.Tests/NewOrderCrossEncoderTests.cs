using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Framing;
using Xunit;
using ClOrdID = B3.EntryPoint.Client.Models.ClOrdID;
using ClientCrossPrioritization = B3.EntryPoint.Client.Models.CrossPrioritization;
using ClientCrossType = B3.EntryPoint.Client.Models.CrossType;
using NewOrderCrossRequest = B3.EntryPoint.Client.Models.NewOrderCrossRequest;
using CrossLeg = B3.EntryPoint.Client.Models.CrossLeg;
using Side = B3.EntryPoint.Client.Models.Side;
using SbeCrossPrioritization = B3.Entrypoint.Fixp.Sbe.V6.CrossPrioritization;
using SbeCrossType = B3.Entrypoint.Fixp.Sbe.V6.CrossType;

namespace B3.EntryPoint.Client.Tests;

public class NewOrderCrossEncoderTests
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
    public void EncodeNewOrderCross_RoundTripsViaSbeReader()
    {
        var req = new NewOrderCrossRequest
        {
            CrossId = "987654321",
            SecurityId = 4242,
            CrossType = ClientCrossType.AllOrNone,
            Prioritization = ClientCrossPrioritization.None,
            Price = 12.34m,
            Legs = new[]
            {
                new CrossLeg { ClOrdID = (ClOrdID)1001UL, Side = Side.Buy, OrderQty = 10 },
                new CrossLeg { ClOrdID = (ClOrdID)1002UL, Side = Side.Sell, OrderQty = 10 },
            },
        };

        var buffer = new byte[NewOrderCrossData.MESSAGE_SIZE + 2 * NewOrderCrossData.NoSidesData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeNewOrderCross(buffer, req, Options(), msgSeqNum: 42);

        Assert.True(len > NewOrderCrossData.MESSAGE_SIZE);

        Assert.True(SofhFrameReader.TryParseHeader(buffer, out var msgLen, out _));
        Assert.Equal((ushort)len, msgLen);

        var afterSofh = buffer.AsSpan(SofhFrameReader.HeaderSize, len - SofhFrameReader.HeaderSize);
        Assert.True(MessageHeader.TryParse(afterSofh, out var header, out _));
        Assert.Equal(NewOrderCrossData.MESSAGE_ID, (int)header.TemplateId);

        var payload = afterSofh.Slice(MessageHeader.MESSAGE_SIZE);
        Assert.True(NewOrderCrossData.TryParse(payload, out var reader));
        ref readonly var data = ref reader.Data;

        Assert.Equal(987654321UL, (ulong)data.CrossID);
        Assert.Equal(4242UL, (ulong)data.SecurityID);
        Assert.Equal(20UL, (ulong)data.OrderQty);
        Assert.Equal(SbeCrossType.ALL_OR_NONE_CROSS, data.CrossType);
        Assert.Equal(SbeCrossPrioritization.NONE, data.CrossPrioritization);
        Assert.Equal(123400L, data.Price.Mantissa);
        Assert.Equal(42u, (uint)data.BusinessHeader.MsgSeqNum);

        int legCount = 0;
        foreach (var leg in reader.NoSides)
        {
            legCount++;
            Assert.True((ulong)leg.ClOrdID == 1001UL || (ulong)leg.ClOrdID == 1002UL);
        }
        Assert.Equal(2, legCount);
    }

    [Fact]
    public void EncodeNewOrderCross_RejectsEmptyLegs()
    {
        var req = new NewOrderCrossRequest
        {
            CrossId = "1",
            SecurityId = 1,
            CrossType = ClientCrossType.AllOrNone,
            Prioritization = ClientCrossPrioritization.None,
            Price = 1m,
            Legs = Array.Empty<CrossLeg>(),
        };
        var buf = new byte[NewOrderCrossData.MESSAGE_SIZE + 64];
        Assert.Throws<ArgumentException>(() => OrderEntryEncoder.EncodeNewOrderCross(buf, req, Options(), 1));
    }

    [Fact]
    public void EncodeNewOrderCross_RejectsNonNumericCrossId()
    {
        var req = new NewOrderCrossRequest
        {
            CrossId = "not-a-number",
            SecurityId = 1,
            CrossType = ClientCrossType.AllOrNone,
            Prioritization = ClientCrossPrioritization.None,
            Price = 1m,
            Legs = new[] { new CrossLeg { ClOrdID = (ClOrdID)1UL, Side = Side.Buy, OrderQty = 1 } },
        };
        var buf = new byte[NewOrderCrossData.MESSAGE_SIZE + 64];
        Assert.Throws<ArgumentException>(() => OrderEntryEncoder.EncodeNewOrderCross(buf, req, Options(), 1));
    }
}
