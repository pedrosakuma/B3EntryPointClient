using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace B3.EntryPoint.Client.Tests;

public class KeepAliveSessionFaultTests
{
    private static EntryPointClient CreateClient(uint sessionId, FakeLogger logger)
    {
        var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 9876),
            SessionId = sessionId,
            SessionVerId = 7,
            EnteringFirm = 1234,
            Credentials = Credentials.FromUtf8("test"),
            KeepAliveIntervalMs = 200,
            Logger = logger,
        });
        client.AttachEstablishedSessionForTesting(new MemoryStream());
        return client;
    }

    [Fact]
    public async Task KeepAliveFailure_TransitionsSessionAndSurfacesIdentity()
    {
        var logger = new FakeLogger();
        await using var client = CreateClient(42, logger);
        var terminated = new TaskCompletionSource<TerminatedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Terminated += (_, args) => terminated.TrySetResult(args);

        client.StartKeepAliveForTesting(
            _ => Task.FromException<ulong>(new IOException("sequence write failed")));

        var args = await terminated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(FixpClientState.Terminated, client.State);
        Assert.Contains("sequence write failed", args.Reason);
        var entry = Assert.Single(logger.Collector.GetSnapshot(), e => e.Id.Id == 5003);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("SessionID=42", entry.Message);
        Assert.Contains("SessionVerID=7", entry.Message);
        Assert.IsType<IOException>(entry.Exception);
    }

    [Fact]
    public async Task MultipleClients_AllSignalReconnectWithoutSilentSchedulers()
    {
        const int clientCount = 3;
        var clientLoggers = Enumerable.Range(1, clientCount)
            .Select(index => (SessionId: (uint)index, Logger: new FakeLogger()))
            .ToArray();
        var clients = clientLoggers
            .Select(item => CreateClient(item.SessionId, item.Logger))
            .ToArray();
        try
        {
            var reconnectSignals = clients.Select(client =>
            {
                var signal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                client.Terminated += (_, _) => signal.TrySetResult();
                client.StartKeepAliveForTesting(
                    _ => Task.FromException<ulong>(new IOException("shared transport failure")));
                return signal.Task;
            }).ToArray();

            await Task.WhenAll(reconnectSignals).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.All(clients, client => Assert.Equal(FixpClientState.Terminated, client.State));
            Assert.All(clientLoggers, item =>
            {
                var entry = Assert.Single(
                    item.Logger.Collector.GetSnapshot(),
                    log => log.Id.Id == 5003);
                Assert.Contains($"SessionID={item.SessionId}", entry.Message);
            });
        }
        finally
        {
            await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task Teardown_CancelsInFlightKeepAliveWithoutFaultingSession()
    {
        var logger = new FakeLogger();
        await using var client = CreateClient(99, logger);
        var sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = 0;
        client.Terminated += (_, _) => Interlocked.Increment(ref terminated);
        client.StartKeepAliveForTesting(async ct =>
        {
            sendStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1UL;
        });

        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopActiveSessionForTestingAsync();

        Assert.Equal(0, Volatile.Read(ref terminated));
        Assert.DoesNotContain(logger.Collector.GetSnapshot(), entry => entry.Id.Id == 5003);
        Assert.Equal(FixpClientState.Disconnected, client.State);
    }
}
