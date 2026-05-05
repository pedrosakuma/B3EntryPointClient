using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Fixp;

/// <summary>
/// Audit regression: outbound MsgSeqNum must not silently wrap past the FIXP
/// uint32 boundary. The previous implementation surfaced a deeply nested
/// <see cref="OverflowException"/> from a <c>checked((uint)…)</c> cast inside
/// the encoder; this suite asserts that callers now get a clear, actionable
/// <see cref="InvalidOperationException"/> instead, that the counter does not
/// advance past the limit, and that a one-shot warning fires near the threshold.
/// </summary>
public class OutboundSeqNumBoundaryTests
{
    private static EntryPointClientOptions Opts() => new()
    {
        Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1),
        SessionId = 42,
        SessionVerId = 7,
        EnteringFirm = 100,
        Credentials = Credentials.FromUtf8("k"),
        SenderLocation = "SP-001",
        EnteringTrader = "T0001",
        DefaultMarketSegmentId = 1,
    };

    private static FixpClientSession NewSession(EntryPointClientOptions? opts = null) =>
        new(new MemoryStream(), opts ?? Opts());

    [Fact]
    public void ToWireSeqNum_AtMaxUint32_Encodes_Successfully()
    {
        var seq = SeqNumGuard.ToWireSeqNum(uint.MaxValue);
        Assert.Equal(uint.MaxValue, seq.Value);
    }

    [Fact]
    public void ToWireSeqNum_PastMaxUint32_Throws_InvalidOperation_With_Rotation_Hint()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SeqNumGuard.ToWireSeqNum((ulong)uint.MaxValue + 1UL));
        Assert.Contains("ReconnectAsync", ex.Message);
        Assert.Contains("SessionVerID", ex.Message);
    }

    [Fact]
    public void NextOutboundSeqNum_At_Boundary_Returns_MaxUint32()
    {
        var session = NewSession();
        session.ResumeOutboundSeqNum(uint.MaxValue);
        Assert.Equal((ulong)uint.MaxValue, session.NextOutboundSeqNum());
        Assert.Equal((ulong)uint.MaxValue, session.LastAssignedOutboundSeqNum());
    }

    [Fact]
    public void NextOutboundSeqNum_Past_Boundary_Throws_And_Does_Not_Advance_Counter()
    {
        var session = NewSession();
        session.ResumeOutboundSeqNum((ulong)uint.MaxValue + 1UL);
        Assert.Equal((ulong)uint.MaxValue, session.LastAssignedOutboundSeqNum());

        var ex = Assert.Throws<InvalidOperationException>(() => session.NextOutboundSeqNum());
        Assert.Contains("ReconnectAsync", ex.Message);

        // Counter rolled back; subsequent calls keep failing rather than silently advancing.
        Assert.Equal((ulong)uint.MaxValue, session.LastAssignedOutboundSeqNum());
        Assert.Throws<InvalidOperationException>(() => session.NextOutboundSeqNum());
        Assert.Equal((ulong)uint.MaxValue, session.LastAssignedOutboundSeqNum());
    }

    [Fact]
    public void OrderEntryEncoder_At_MaxUint32_Encodes_Successfully()
    {
        var req = new SimpleNewOrderRequest
        {
            ClOrdID = new ClOrdID(1UL),
            SecurityId = 7,
            Side = Side.Buy,
            OrderType = SimpleOrderType.Limit,
            OrderQty = 100,
            Price = 12.34m,
            Account = 555,
        };
        var buffer = new byte[256];
        var len = OrderEntryEncoder.EncodeSimpleNewOrder(buffer, req, Opts(), msgSeqNum: uint.MaxValue);
        Assert.True(len > 0);
    }

    [Fact]
    public void OrderEntryEncoder_Past_MaxUint32_Throws_InvalidOperation()
    {
        var req = new SimpleNewOrderRequest
        {
            ClOrdID = new ClOrdID(1UL),
            SecurityId = 7,
            Side = Side.Buy,
            OrderType = SimpleOrderType.Limit,
            OrderQty = 100,
            Price = 12.34m,
            Account = 555,
        };
        var buffer = new byte[256];
        var ex = Assert.Throws<InvalidOperationException>(
            () => OrderEntryEncoder.EncodeSimpleNewOrder(buffer, req, Opts(), msgSeqNum: (ulong)uint.MaxValue + 1UL));
        Assert.Contains("ReconnectAsync", ex.Message);
    }

    [Fact]
    public void NextOutboundSeqNum_Crossing_NearExhaustionThreshold_Logs_Warning_Once()
    {
        var captured = new List<(int eventId, string message)>();
        var opts = Opts();
        opts.Logger = new CapturingLogger(captured);
        var session = NewSession(opts);

        session.ResumeOutboundSeqNum(SeqNumGuard.NearExhaustionThreshold - 1UL);
        _ = session.NextOutboundSeqNum(); // assigns NearExhaustionThreshold - 1, still below
        Assert.DoesNotContain(captured, e => e.eventId == 4013);

        _ = session.NextOutboundSeqNum(); // assigns NearExhaustionThreshold — first crossing
        Assert.Single(captured, e => e.eventId == 4013);

        _ = session.NextOutboundSeqNum();
        _ = session.NextOutboundSeqNum();
        Assert.Single(captured, e => e.eventId == 4013); // still exactly one
    }

    private sealed class CapturingLogger(List<(int eventId, string message)> sink)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Add((eventId.Id, formatter(state, exception)));
        }
    }
}
