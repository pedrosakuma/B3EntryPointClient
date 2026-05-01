using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Tests.Fixp;

public class KeepAliveSchedulerTests
{
    [Fact]
    public void Ctor_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeepAliveScheduler(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeepAliveScheduler(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Ctor_StoresInterval()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), s.KeepAliveInterval);
    }

    [Fact]
    public void Start_ThrowsNotImplemented_PerIssue3()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        var ex = Assert.Throws<NotImplementedException>(s.Start);
        Assert.Contains("issue #3", ex.Message);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        s.Stop();
        s.Stop();
    }

    [Fact]
    public void RaiseFrameSent_FiresEvent()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        SequenceFrameEventArgs? captured = null;
        s.SequenceFrameSent += (_, e) => captured = e;
        var at = DateTimeOffset.UtcNow;
        s.RaiseFrameSent(42, at);
        Assert.NotNull(captured);
        Assert.Equal(42UL, captured!.NextSeqNo);
        Assert.Equal(at, captured.At);
    }

    [Fact]
    public void RaiseFrameReceived_FiresEvent()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        SequenceFrameEventArgs? captured = null;
        s.SequenceFrameReceived += (_, e) => captured = e;
        s.RaiseFrameReceived(7, DateTimeOffset.UtcNow);
        Assert.NotNull(captured);
        Assert.Equal(7UL, captured!.NextSeqNo);
    }

    [Fact]
    public void IsExposedAsInterface()
    {
        IKeepAliveScheduler s = new KeepAliveScheduler(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), s.KeepAliveInterval);
    }
}
