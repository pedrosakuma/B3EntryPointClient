using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.State;

namespace B3.EntryPoint.Client.Tests;

public class OutboundAttemptReceiptTests
{
    [Fact]
    public async Task SubmitWithReceipt_CallbackPrecedesWrite_AndReturnsFrameIdentity()
    {
        var stream = new MemoryStream();
        await using var client = CreateClient(stream);
        OutboundFrameIdentity? prepared = null;

        var receipt = await client.SubmitWithReceiptAsync(NewOrder(11), (frame, _) =>
        {
            Assert.Equal(0, stream.Length);
            prepared = frame;
            return ValueTask.CompletedTask;
        });

        Assert.Same(prepared, receipt.Frame);
        Assert.Equal(OutboundAttemptStage.TransportWriteCompleted, receipt.Stage);
        Assert.Equal(42u, receipt.Frame.SessionId);
        Assert.Equal(7u, receipt.Frame.SessionVerId);
        Assert.Equal(1UL, receipt.Frame.MsgSeqNum);
        Assert.Equal(OutboundOperationKind.NewOrder, receipt.Frame.Operation);
        Assert.Equal(new ClOrdID(11), receipt.Frame.ClOrdID);
        Assert.Equal(stream.Length, receipt.Frame.EncodedFrameLength);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(stream.ToArray())),
            receipt.Frame.EncodedFrameSha256);
    }

    [Fact]
    public async Task CallbackFailure_CannotWriteFrame()
    {
        var stream = new MemoryStream();
        await using var client = CreateClient(stream);

        var ex = await Assert.ThrowsAsync<OutboundAttemptException>(() =>
            client.CancelWithReceiptAsync(Cancel(12), (_, _) =>
                ValueTask.FromException(new IOException("ledger unavailable"))));

        Assert.Equal(OutboundAttemptStage.SequenceReservedAndEncoded, ex.LastStage);
        Assert.True(ex.NoTransportWritePossible);
        Assert.NotNull(ex.Frame);
        Assert.Equal(0, stream.Length);

        var next = await client.SubmitWithReceiptAsync(NewOrder(120), CompletedCallback);
        Assert.Equal(1UL, next.Frame.MsgSeqNum);
    }

    [Fact]
    public async Task CancellationBeforeReservation_IsClassifiedNotStarted()
    {
        var stream = new MemoryStream();
        await using var client = CreateClient(stream);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var callbackCalled = false;

        var ex = await Assert.ThrowsAsync<OutboundAttemptException>(() =>
            client.SubmitWithReceiptAsync(NewOrder(17), (_, _) =>
            {
                callbackCalled = true;
                return ValueTask.CompletedTask;
            }, cts.Token));

        Assert.Equal(OutboundAttemptStage.NotStarted, ex.LastStage);
        Assert.True(ex.NoTransportWritePossible);
        Assert.Null(ex.Frame);
        Assert.False(callbackCalled);
        Assert.Equal(0, stream.Length);

        var next = await client.SubmitWithReceiptAsync(NewOrder(180), CompletedCallback);
        Assert.Equal(1UL, next.Frame.MsgSeqNum);
    }

    [Fact]
    public async Task EncodingFailure_AfterReservation_CannotWriteFrame()
    {
        var stream = new MemoryStream();
        await using var client = CreateClient(stream);
        var request = NewOrder(18) with { MemoText = new string('x', 1_000) };
        var callbackCalled = false;

        var ex = await Assert.ThrowsAsync<OutboundAttemptException>(() =>
            client.SubmitWithReceiptAsync(request, (_, _) =>
            {
                callbackCalled = true;
                return ValueTask.CompletedTask;
            }));

        Assert.Equal(OutboundAttemptStage.SequenceReserved, ex.LastStage);
        Assert.True(ex.NoTransportWritePossible);
        Assert.Null(ex.Frame);
        Assert.False(callbackCalled);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task CancellationAfterCallbackBeforeWrite_IsProvedUnsent()
    {
        var stream = new MemoryStream();
        await using var client = CreateClient(stream);
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAsync<OutboundAttemptException>(() =>
            client.ReplaceWithReceiptAsync(Replace(13), (_, _) =>
            {
                cts.Cancel();
                return ValueTask.CompletedTask;
            }, cts.Token));

        Assert.Equal(OutboundAttemptStage.FramePrepared, ex.LastStage);
        Assert.True(ex.NoTransportWritePossible);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task PartialWrite_IsIndeterminate()
    {
        var stream = new PartialWriteThenThrowStream();
        await using var client = CreateClient(stream);

        var ex = await Assert.ThrowsAsync<OutboundAttemptException>(() =>
            client.SubmitWithReceiptAsync(NewOrder(14), CompletedCallback));

        Assert.Equal(OutboundAttemptStage.TransportWriteStarted, ex.LastStage);
        Assert.False(ex.NoTransportWritePossible);
        Assert.True(stream.BytesWritten > 0);
    }

    [Fact]
    public async Task FlushFailure_ReportsCompletedTransportWrite()
    {
        var stream = new FlushFailingStream();
        await using var client = CreateClient(stream);

        var ex = await Assert.ThrowsAsync<OutboundAttemptException>(() =>
            client.SubmitWithReceiptAsync(NewOrder(15), CompletedCallback));

        Assert.Equal(OutboundAttemptStage.TransportWriteCompleted, ex.LastStage);
        Assert.False(ex.NoTransportWritePossible);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task SessionStateFailure_FollowsCompletedTransportWrite()
    {
        var stream = new MemoryStream();
        await using var client = CreateClient(stream, new ThrowingStore());

        var ex = await Assert.ThrowsAsync<OutboundAttemptException>(() =>
            client.SubmitWithReceiptAsync(NewOrder(16), CompletedCallback));

        Assert.Equal(OutboundAttemptStage.TransportWriteCompleted, ex.LastStage);
        Assert.False(ex.NoTransportWritePossible);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task ConcurrentNewCancelReplace_PreserveWireSequenceOrder()
    {
        var stream = new MemoryStream();
        await using var client = CreateClient(stream);

        var tasks = new[]
        {
            client.SubmitWithReceiptAsync(NewOrder(21), CompletedCallback),
            client.CancelWithReceiptAsync(Cancel(22), CompletedCallback),
            client.ReplaceWithReceiptAsync(Replace(23), CompletedCallback),
        };

        var receipts = await Task.WhenAll(tasks);
        Assert.Equal(new ulong[] { 1, 2, 3 }, receipts.Select(r => r.Frame.MsgSeqNum).Order().ToArray());

        var bytes = stream.ToArray();
        var wireSeqNums = new List<uint>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
            wireSeqNums.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 16, 4)));
            offset += frameLength;
        }

        Assert.Equal(new uint[] { 1, 2, 3 }, wireSeqNums);
    }

    private static ValueTask CompletedCallback(OutboundFrameIdentity _, CancellationToken __) =>
        ValueTask.CompletedTask;

    private static EntryPointClient CreateClient(Stream stream, ISessionStateStore? store = null)
    {
        var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
            SessionId = 42,
            SessionVerId = 7,
            EnteringFirm = 9,
            Credentials = Credentials.FromUtf8("k"),
            SessionStateStore = store,
            StateCompactEveryDeltas = 0,
            TerminateOnDispose = false,
        });
        client.AttachEstablishedSessionForTesting(stream);
        return client;
    }

    private static NewOrderRequest NewOrder(ulong clOrdId) => new()
    {
        ClOrdID = new ClOrdID(clOrdId),
        SecurityId = 100,
        Side = Side.Buy,
        OrderType = OrderType.Limit,
        OrderQty = 10,
        Price = 12.34m,
    };

    private static CancelOrderRequest Cancel(ulong clOrdId) => new()
    {
        ClOrdID = new ClOrdID(clOrdId),
        OrigClOrdID = new ClOrdID(clOrdId + 100),
        SecurityId = 100,
        Side = Side.Buy,
    };

    private static ReplaceOrderRequest Replace(ulong clOrdId) => new()
    {
        ClOrdID = new ClOrdID(clOrdId),
        OrigClOrdID = new ClOrdID(clOrdId + 100),
        SecurityId = 100,
        Side = Side.Buy,
        OrderType = OrderType.Limit,
        OrderQty = 10,
        Price = 12.35m,
    };

    private sealed class PartialWriteThenThrowStream : MemoryStream
    {
        public int BytesWritten { get; private set; }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var partial = Math.Min(8, buffer.Length);
            await base.WriteAsync(buffer[..partial], cancellationToken);
            BytesWritten += partial;
            throw new IOException("connection reset during write");
        }
    }

    private sealed class FlushFailingStream : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("flush failed"));
    }

    private sealed class ThrowingStore : ISessionStateStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<SessionSnapshot?>(null);

        public ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default) =>
            ValueTask.FromException(new IOException("state store failed"));

        public ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<SessionSnapshot?>(null);

        public ValueTask CompactAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
