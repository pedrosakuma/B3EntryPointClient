using System.Net.Sockets;
using System.Runtime.CompilerServices;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client;

/// <summary>
/// High-level B3 EntryPoint client. Bootstrap scope: <see cref="ConnectAsync"/>
/// performs TCP connect + Negotiate + Establish; <see cref="DisposeAsync"/>
/// sends Terminate. Order submission and event streaming are stubbed and will
/// be wired in subsequent PRs (see <c>docs/CONFORMANCE.md</c>).
/// </summary>
public sealed class EntryPointClient : IAsyncDisposable
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

    /// <summary>
    /// Submit an order and await its terminal Execution/Reject event.
    /// Not yet implemented in the bootstrap — tracked by the order-entry milestone.
    /// </summary>
    public Task<EntryPointEvent> SubmitOrderAsync(SubmitOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        throw new NotImplementedException(
            "SubmitOrderAsync is not yet implemented. Tracked in the order-entry milestone.");
    }

    /// <summary>
    /// Async stream of unsolicited events (ER/Reject/BusinessReject). Not yet
    /// implemented in the bootstrap — emits no events and completes when the
    /// session is closed.
    /// </summary>
    public async IAsyncEnumerable<EntryPointEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureEstablished();
        // Bootstrap stub: no events surfaced. Real implementation lands with the
        // ExecutionReport handling milestone.
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

    private void EnsureEstablished()
    {
        if (_session?.State != FixpClientState.Established)
            throw new InvalidOperationException("Client is not in Established state.");
    }
}
