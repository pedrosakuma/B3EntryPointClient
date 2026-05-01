using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;

namespace B3.EntryPoint.Client.Tests;

public class TerminateApiTests
{
    [Fact]
    public async Task TerminateAsync_NotConnected_Throws()
    {
        await using var c = MakeClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.TerminateAsync(TerminationCode.Finished));
    }

    [Fact]
    public async Task ReconnectAsync_RejectsNonIncreasingVerId()
    {
        await using var c = MakeClient(sessionVerId: 5);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            c.ReconnectAsync(5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            c.ReconnectAsync(4));
    }

    [Fact]
    public async Task ReconnectAsync_AcceptsIncreasingVerId_ButNotImplemented()
    {
        await using var c = MakeClient(sessionVerId: 5);
        var ex = await Assert.ThrowsAsync<NotImplementedException>(() =>
            c.ReconnectAsync(6));
        Assert.Contains("issue #6", ex.Message);
    }

    [Fact]
    public void TerminationCode_HasExpectedValues()
    {
        Assert.Equal((byte)1, (byte)TerminationCode.Finished);
        Assert.Equal((byte)10, (byte)TerminationCode.KeepaliveIntervalLapsed);
        Assert.Equal((byte)30, (byte)TerminationCode.BackupTakeoverInProgress);
    }

    [Fact]
    public void Options_DefaultCancelOnDisconnect_IsSafest()
    {
        var o = new EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
            SessionId = 1,
            SessionVerId = 1,
            EnteringFirm = 1,
            Credentials = Credentials.FromUtf8("k"),
        };
        Assert.Equal(CancelOnDisconnectType.CancelOnDisconnectOrTerminate, o.CancelOnDisconnect);
    }

    [Fact]
    public void Terminated_Event_FiresThroughHook()
    {
        var c = MakeClient();
        TerminatedEventArgs? captured = null;
        c.Terminated += (_, e) => captured = e;
        c.RaiseTerminated(TerminationCode.Finished, "bye", initiatedByClient: true);
        Assert.NotNull(captured);
        Assert.Equal(TerminationCode.Finished, captured!.Code);
        Assert.True(captured.InitiatedByClient);
        Assert.Equal("bye", captured.Reason);
    }

    private static EntryPointClient MakeClient(uint sessionVerId = 1) => new(new EntryPointClientOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 9999),
        SessionId = 1,
        SessionVerId = sessionVerId,
        EnteringFirm = 1,
        Credentials = Credentials.FromUtf8("k"),
    });
}
