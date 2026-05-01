using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using ClOrdID = B3.EntryPoint.Client.Models.ClOrdID;

namespace B3.EntryPoint.Client;

/// <summary>
/// High-level B3 EntryPoint client. <see cref="ConnectAsync"/> performs TCP
/// connect + Negotiate + Establish; <see cref="DisposeAsync"/> sends Terminate.
/// Order submission, replace, cancel and event streaming are exposed as the
/// public API surface; their wire-level implementations land incrementally
/// (see <c>docs/CONFORMANCE.md</c> and the issues tagged <c>area/api-surface</c>).
/// </summary>
public sealed class EntryPointClient : IAsyncDisposable, ISubmitOrder, IReplaceOrder, ICancelOrder
{
    private readonly EntryPointClientOptions _options;
    private readonly Channel<EntryPointEvent> _events =
        Channel.CreateUnbounded<EntryPointEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
    private TcpClient? _tcp;
    private FixpClientSession? _session;
    private KeepAliveScheduler? _keepAlive;

    /// <summary>Keep-alive scheduler for this client. Bound after <see cref="ConnectAsync"/>.</summary>
    public IKeepAliveScheduler? KeepAlive => _keepAlive;

    public EntryPointClient(EntryPointClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public FixpClientState State => _session?.State ?? FixpClientState.Disconnected;

    /// <summary>
    /// Raised after a <c>Terminate</c> is sent or received. The session is no
    /// longer usable when this fires; a new <see cref="ConnectAsync"/> (with
    /// a bumped <see cref="EntryPointClientOptions.SessionVerId"/>) is
    /// required to resume.
    /// </summary>
    public event EventHandler<TerminatedEventArgs>? Terminated;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_session is not null)
            throw new InvalidOperationException("Client is already connected.");

        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(_options.Endpoint.Address, _options.Endpoint.Port, connectCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _session = new FixpClientSession(tcp.GetStream(), _options);

        await _session.NegotiateAsync(ct).ConfigureAwait(false);
        await _session.EstablishAsync(ct).ConfigureAwait(false);
        _session.StartInboundLoop(_events.Writer);

        _keepAlive = new KeepAliveScheduler(
            _options.KeepAliveInterval,
            sendSequence: (seq, token) => _session.SendSequenceAsync(seq, token),
            nextSeqNo: () => _session.NextOutboundSeqNum());
        _session.OnInboundSequence = nextSeq => _keepAlive.RaiseFrameReceived(nextSeq, DateTimeOffset.UtcNow);
        _keepAlive.Start();
    }

    /// <inheritdoc />
    public async Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[NewOrderSingleData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeNewOrderSingle(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> SubmitSimpleAsync(SimpleNewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[SimpleNewOrderData.MESSAGE_SIZE + 64];
        var len = OrderEntryEncoder.EncodeSimpleNewOrder(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[OrderCancelReplaceRequestData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeOrderCancelReplace(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[SimpleModifyOrderData.MESSAGE_SIZE + 64];
        var len = OrderEntryEncoder.EncodeSimpleModifyOrder(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task CancelAsync(CancelOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[OrderCancelRequestData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeOrderCancel(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[OrderMassActionRequestData.MESSAGE_SIZE + 32];
        var len = OrderEntryEncoder.EncodeOrderMassAction(buffer, request, _options, seq);
        // Fire-and-forget the request; the matching MassActionReport will be
        // surfaced through the inbound dispatcher (issue #23) — until then,
        // we encode+send and return a synthetic Acknowledged report.
        return SendMassActionAsync(buffer, len, request, ct);

        async Task<MassActionReport> SendMassActionAsync(byte[] buf, int length, MassActionRequest req, CancellationToken token)
        {
            await _session.SendApplicationFrameAsync(buf, length, token).ConfigureAwait(false);
            return new MassActionReport
            {
                ClOrdID = req.ClOrdID,
                ActionType = req.ActionType,
                Response = B3.EntryPoint.Client.Models.MassActionResponse.Accepted,
                Scope = req.Scope,
                TotalAffectedOrders = 0,
            };
        }
    }

    /// <summary>
    /// Send a graceful <c>Terminate</c> to the peer and tear down the session.
    /// API surface only — the wire-level encode lands in a follow-up PR (issue #6).
    /// </summary>
    public Task TerminateAsync(TerminationCode code, CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException("Client is not connected.");
        throw new NotImplementedException(
            "TerminateAsync(code) is not yet wired to the FIXP transport. Tracked by issue #6.");
    }

    /// <summary>
    /// Reconnect against the same logical session bumping
    /// <see cref="EntryPointClientOptions.SessionVerId"/>. The next
    /// <c>SessionVerID</c> must be strictly greater than the previous one;
    /// otherwise the gateway terminates with
    /// <see cref="TerminationCode.InvalidSessionVerId"/>.
    /// </summary>
    public Task ReconnectAsync(uint nextSessionVerId, CancellationToken ct = default)
    {
        if (nextSessionVerId <= _options.SessionVerId)
            throw new ArgumentOutOfRangeException(nameof(nextSessionVerId),
                "Next SessionVerID must be strictly greater than the current one.");
        throw new NotImplementedException(
            "ReconnectAsync is not yet implemented. Tracked by issue #6.");
    }

    /// <summary>
    /// Async stream of unsolicited events (ER/Reject/BusinessReject). The
    /// typed event family lands with issue #9; today the stream completes
    /// immediately.
    /// </summary>
    public async IAsyncEnumerable<EntryPointEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureEstablished();
        await foreach (var evt in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    public async ValueTask DisposeAsync()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
        if (_session is not null)
            await _session.DisposeAsync().ConfigureAwait(false);
        _tcp?.Dispose();
        _session = null;
        _tcp = null;
    }

    /// <summary>
    /// Test/internal hook — surface a <see cref="Terminated"/> notification
    /// through the public event. Replaced by the real wire-level handler in
    /// the follow-up implementation PR.
    /// </summary>
    internal void RaiseTerminated(TerminationCode code, string? reason, bool initiatedByClient) =>
        Terminated?.Invoke(this, new TerminatedEventArgs(code, reason, initiatedByClient));

    private void EnsureEstablished()
    {
        if (_session?.State != FixpClientState.Established)
            throw new InvalidOperationException("Client is not in Established state.");
    }
}
