using System.Net;
using System.Reflection;
using System.Threading.Channels;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests;

public class EntryPointClientEventChannelTests
{
    private static EntryPointClientOptions Valid(int capacity) => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 9000),
        SessionId = 1u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
        EventChannelCapacity = capacity,
    };

    private static Channel<EntryPointEvent> GetChannel(EntryPointClient client)
    {
        var field = typeof(EntryPointClient).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Channel<EntryPointEvent>)field.GetValue(client)!;
    }

    private static OrderRejected MakeEvent(ulong seq) => new()
    {
        SeqNum = seq,
        SendingTime = DateTimeOffset.UnixEpoch,
        ClOrdID = new ClOrdID(seq),
        OrderId = seq,
        RejectCode = 0,
    };

    [Fact]
    public void Options_EventChannelCapacity_DefaultsTo4096()
    {
        Assert.Equal(4096, new EntryPointClientOptions().EventChannelCapacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Options_EventChannelCapacity_RejectsNonPositive(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntryPointClientOptions { EventChannelCapacity = value });
    }

    [Fact]
    public async Task EventChannel_IsBounded_ProducerBlocksUntilConsumerDrains()
    {
        var client = new EntryPointClient(Valid(capacity: 2));
        var channel = GetChannel(client);
        var writer = channel.Writer;
        var reader = channel.Reader;

        // Saturate the channel up to capacity. Synchronous TryWrite should succeed
        // exactly `capacity` times for a bounded channel with FullMode.Wait.
        Assert.True(writer.TryWrite(MakeEvent(1)));
        Assert.True(writer.TryWrite(MakeEvent(2)));
        Assert.False(writer.TryWrite(MakeEvent(3)),
            "TryWrite must fail once the bounded channel is full.");

        // The next WriteAsync must NOT complete synchronously — the producer is
        // expected to await a free slot (BoundedChannelFullMode.Wait).
        var blocked = writer.WriteAsync(MakeEvent(3)).AsTask();
        await Task.Delay(50);
        Assert.False(blocked.IsCompleted,
            "WriteAsync should block while the bounded channel is full; an unbounded channel would have completed synchronously.");

        // Drain one item — the pending WriteAsync should now complete promptly.
        Assert.True(reader.TryRead(out _));
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(blocked.IsCompletedSuccessfully);

        await client.DisposeAsync();
    }
}
