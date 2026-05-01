using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
    private TcpClient? _tcp;
    private FixpClientSession? _session;

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
    }

    /// <inheritdoc />
    public Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        throw new NotImplementedException(
            "SubmitAsync is not yet wired to the FIXP transport. Tracked by issue #4.");
    }

    /// <inheritdoc />
    public Task<ClOrdID> SubmitSimpleAsync(SimpleNewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        throw new NotImplementedException(
            "SubmitSimpleAsync is not yet wired to the FIXP transport. Tracked by issue #4.");
    }

    /// <inheritdoc />
    public Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        throw new NotImplementedException(
            "ReplaceAsync is not yet wired to the FIXP transport. Tracked by issue #7.");
    }

    /// <inheritdoc />
    public Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        throw new NotImplementedException(
            "ReplaceSimpleAsync is not yet wired to the FIXP transport. Tracked by issue #7.");
    }

    /// <inheritdoc />
    public Task CancelAsync(CancelOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        throw new NotImplementedException(
            "CancelAsync is not yet wired to the FIXP transport. Tracked by issue #8.");
    }

    /// <inheritdoc />
    public Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        throw new NotImplementedException(
            "MassActionAsync is not yet wired to the FIXP transport. Tracked by issue #8.");
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
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public async ValueTask DisposeAsync()
    {
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
