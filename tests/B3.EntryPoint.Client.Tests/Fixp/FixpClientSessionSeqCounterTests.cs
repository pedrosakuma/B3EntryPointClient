using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Tests.Fixp;

/// <summary>
/// Regression coverage for #120 — non-mutating seq-counter accessors.
/// Verifies that <see cref="FixpClientSession.LastAssignedOutboundSeqNum"/>
/// and <see cref="FixpClientSession.PeekNextOutboundSeqNum"/> do not advance
/// the outbound counter, and that <see cref="FixpClientSession.NextOutboundSeqNum"/>
/// resumes from the correct value after they are called.
/// </summary>
public class FixpClientSessionSeqCounterTests
{
    private static FixpClientSession NewSession()
    {
        var opts = new EntryPointClientOptions
        {
            Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1),
            SessionId = 1,
            SessionVerId = 1,
            EnteringFirm = 1,
            Credentials = Credentials.FromUtf8("k"),
        };
        return new FixpClientSession(new MemoryStream(), opts);
    }

    [Fact]
    public void Peek_AndLastAssigned_DoNotMutateCounter()
    {
        var session = NewSession();

        // Counter starts at 0 — nothing assigned yet.
        Assert.Equal(0UL, session.LastAssignedOutboundSeqNum());
        Assert.Equal(1UL, session.PeekNextOutboundSeqNum());

        // Calling either accessor repeatedly must not mutate the counter.
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(0UL, session.LastAssignedOutboundSeqNum());
            Assert.Equal(1UL, session.PeekNextOutboundSeqNum());
        }

        // First real allocation returns 1; counter advances exactly once.
        Assert.Equal(1UL, session.NextOutboundSeqNum());
        Assert.Equal(1UL, session.LastAssignedOutboundSeqNum());
        Assert.Equal(2UL, session.PeekNextOutboundSeqNum());
    }

    [Fact]
    public void Allocations_Are_Contiguous_When_Interleaved_With_Peek_And_LastAssigned()
    {
        var session = NewSession();
        var observed = new List<ulong>();

        // Simulate the buggy pattern from #120: BuildSnapshot + keep-alive both
        // peek/read between every app send. Before the fix this would burn
        // outbound seqs and produce gaps.
        for (int i = 0; i < 100; i++)
        {
            _ = session.PeekNextOutboundSeqNum();
            _ = session.LastAssignedOutboundSeqNum();
            observed.Add(session.NextOutboundSeqNum());
            _ = session.PeekNextOutboundSeqNum();
            _ = session.LastAssignedOutboundSeqNum();
        }

        // Must be 1,2,3,...,100 — no gaps.
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (ulong)i), observed);
    }

    [Fact]
    public void ResumeOutboundSeqNum_Then_Peek_Reports_The_Resumed_Next()
    {
        var session = NewSession();

        session.ResumeOutboundSeqNum(nextOutboundSeqNum: 42UL);

        // After resuming "next outbound seq = 42", LastAssigned is 41 and Peek is 42.
        Assert.Equal(41UL, session.LastAssignedOutboundSeqNum());
        Assert.Equal(42UL, session.PeekNextOutboundSeqNum());

        Assert.Equal(42UL, session.NextOutboundSeqNum());
        Assert.Equal(42UL, session.LastAssignedOutboundSeqNum());
        Assert.Equal(43UL, session.PeekNextOutboundSeqNum());
    }
}
