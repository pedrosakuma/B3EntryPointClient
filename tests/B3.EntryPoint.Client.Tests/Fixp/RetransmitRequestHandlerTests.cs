using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Tests.Fixp;

public class RetransmitRequestHandlerTests
{
    [Fact]
    public async Task Request_RejectsZeroCount()
    {
        var h = new RetransmitRequestHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.RequestRetransmitAsync(1, 0));
    }

    [Fact]
    public async Task Request_RejectsCountAboveMax()
    {
        var h = new RetransmitRequestHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.RequestRetransmitAsync(1, RetransmitRequestHandler.MaxCountPerRequest + 1));
    }

    [Fact]
    public async Task Request_RejectsZeroFromSeqNo()
    {
        var h = new RetransmitRequestHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.RequestRetransmitAsync(0, 1));
    }

    [Fact]
    public async Task Request_WithoutBoundTransport_Throws()
    {
        var h = new RetransmitRequestHandler();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.RequestRetransmitAsync(1, 1));
        Assert.Contains("transport", ex.Message);
    }

    [Fact]
    public async Task Request_BoundTransport_InvokesSendAndRaisesEvent()
    {
        var calls = new List<(ulong from, uint count)>();
        Task SendAsync(ulong from, uint count, CancellationToken ct)
        {
            calls.Add((from, count));
            return Task.CompletedTask;
        }
        var ctorInfo = typeof(RetransmitRequestHandler).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 1);
        var h = (RetransmitRequestHandler)ctorInfo.Invoke(new object?[]
        {
            (Func<ulong, uint, CancellationToken, Task>)SendAsync,
        });
        RetransmitRequestedEventArgs? raised = null;
        h.RetransmitRequested += (_, e) => raised = e;
        await h.RequestRetransmitAsync(7, 3);
        Assert.Single(calls);
        Assert.Equal(7UL, calls[0].from);
        Assert.Equal(3u, calls[0].count);
        Assert.NotNull(raised);
        Assert.Equal(7UL, raised!.FromSeqNo);
    }

    [Fact]
    public void Events_RoundTripThroughRaiseHooks()
    {
        var h = new RetransmitRequestHandler();
        RetransmitRequestedEventArgs? req = null;
        RetransmissionEventArgs? resp = null;
        RetransmitRejectedEventArgs? rej = null;
        NotAppliedEventArgs? na = null;
        h.RetransmitRequested += (_, e) => req = e;
        h.RetransmissionReceived += (_, e) => resp = e;
        h.RetransmitRejected += (_, e) => rej = e;
        h.NotAppliedReceived += (_, e) => na = e;

        h.RaiseRetransmitRequested(10, 5, DateTimeOffset.UnixEpoch);
        h.RaiseRetransmissionReceived(10, 5, DateTimeOffset.UnixEpoch);
        h.RaiseRetransmitRejected(RetransmitRejectCode.SystemBusy, DateTimeOffset.UnixEpoch);
        h.RaiseNotAppliedReceived(20, 3);

        Assert.Equal((10UL, 5U), (req!.FromSeqNo, req.Count));
        Assert.Equal((10UL, 5U), (resp!.NextSeqNo, resp.Count));
        Assert.Equal(RetransmitRejectCode.SystemBusy, rej!.Code);
        Assert.Equal((20UL, 3U), (na!.FromSeqNo, na.Count));
    }

    [Fact]
    public void IsExposedAsInterface()
    {
        IRetransmitRequestHandler h = new RetransmitRequestHandler();
        Assert.NotNull(h);
    }
}
