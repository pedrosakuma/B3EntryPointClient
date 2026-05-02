using System.Runtime.CompilerServices;
using System.Threading.Channels;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client;

/// <summary>
/// Routing key for fan-out across per-segment FIXP sessions. The key is a
/// <see cref="byte"/> that matches the FIXP <c>MarketSegmentID</c>.
/// </summary>
public delegate byte SegmentRouter<T>(T request);

/// <summary>
/// Owns one <see cref="EntryPointClient"/> per <c>MarketSegmentID</c>, dispatches
/// order entry requests to the appropriate session and aggregates inbound events
/// into a single <see cref="Events"/> stream.
/// </summary>
/// <remarks>
/// Reliability primitives (keep-alive, retransmit, terminate) remain per-session;
/// inspect each underlying client via <see cref="GetClient(byte)"/>.
/// </remarks>
public sealed class SegmentedEntryPointClient : IAsyncDisposable, ISubmitOrder, IReplaceOrder, ICancelOrder
{
    private readonly Dictionary<byte, EntryPointClient> _clients;
    private readonly SegmentRouter<NewOrderRequest> _routeNewOrder;
    private readonly SegmentRouter<SimpleNewOrderRequest> _routeSimpleNewOrder;
    private readonly SegmentRouter<ReplaceOrderRequest> _routeReplace;
    private readonly SegmentRouter<SimpleModifyRequest> _routeSimpleReplace;
    private readonly SegmentRouter<CancelOrderRequest> _routeCancel;
    private readonly SegmentRouter<MassActionRequest> _routeMassAction;

    private readonly Channel<EntryPointEvent> _aggregatedEvents;
    private readonly List<Task> _pumps = new();
    private readonly CancellationTokenSource _pumpCts = new();
    private bool _connected;

    public SegmentedEntryPointClient(
        IReadOnlyDictionary<byte, EntryPointClientOptions> perSegmentOptions,
        SegmentRouter<NewOrderRequest> routeNewOrder,
        SegmentRouter<ReplaceOrderRequest> routeReplace,
        SegmentRouter<CancelOrderRequest> routeCancel,
        SegmentRouter<MassActionRequest>? routeMassAction = null,
        SegmentRouter<SimpleNewOrderRequest>? routeSimpleNewOrder = null,
        SegmentRouter<SimpleModifyRequest>? routeSimpleReplace = null)
    {
        ArgumentNullException.ThrowIfNull(perSegmentOptions);
        if (perSegmentOptions.Count == 0)
            throw new ArgumentException("At least one segment must be configured.", nameof(perSegmentOptions));

        _clients = perSegmentOptions.ToDictionary(kv => kv.Key, kv => new EntryPointClient(kv.Value));
        _routeNewOrder = routeNewOrder ?? throw new ArgumentNullException(nameof(routeNewOrder));
        _routeReplace = routeReplace ?? throw new ArgumentNullException(nameof(routeReplace));
        _routeCancel = routeCancel ?? throw new ArgumentNullException(nameof(routeCancel));
        _routeMassAction = routeMassAction ?? (_ => DefaultSegment);
        _routeSimpleNewOrder = routeSimpleNewOrder ?? (req => routeNewOrder(MapSimple(req)));
        _routeSimpleReplace = routeSimpleReplace ?? (req => routeReplace(MapSimple(req)));

        var aggregateCapacity = perSegmentOptions.Values.Max(o => o.EventChannelCapacity);
        _aggregatedEvents = Channel.CreateBounded<EntryPointEvent>(new BoundedChannelOptions(aggregateCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>The lowest configured MarketSegmentID; used as a fallback route.</summary>
    public byte DefaultSegment => _clients.Keys.Min();

    /// <summary>The set of configured MarketSegmentIDs.</summary>
    public IReadOnlyCollection<byte> Segments => _clients.Keys;

    /// <summary>Returns the underlying <see cref="EntryPointClient"/> for a given segment.</summary>
    public EntryPointClient GetClient(byte segment) => _clients[segment];

    /// <summary>Connects all per-segment sessions in parallel and starts event aggregation.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected)
            throw new InvalidOperationException("SegmentedEntryPointClient is already connected.");
        await Task.WhenAll(_clients.Values.Select(c => c.ConnectAsync(ct))).ConfigureAwait(false);
        foreach (var (_, client) in _clients)
            _pumps.Add(Task.Run(() => PumpAsync(client, _pumpCts.Token)));
        _connected = true;
    }

    private async Task PumpAsync(EntryPointClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in client.Events(ct).ConfigureAwait(false))
                await _aggregatedEvents.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _aggregatedEvents.Writer.TryComplete(ex);
        }
    }

    /// <summary>
    /// Aggregated event stream merging every per-segment <c>Events()</c>. Backed by a
    /// bounded channel sized to the largest per-segment
    /// <see cref="EntryPointClientOptions.EventChannelCapacity"/>, with
    /// <see cref="BoundedChannelFullMode.Wait"/>: a slow consumer stalls the per-segment
    /// pump tasks, which in turn stall each underlying client's inbound decoder.
    /// </summary>
    public async IAsyncEnumerable<EntryPointEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _aggregatedEvents.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    public Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct = default)
        => Route(_routeNewOrder(request)).SubmitAsync(request, ct);

    public Task<ClOrdID> SubmitSimpleAsync(SimpleNewOrderRequest request, CancellationToken ct = default)
        => Route(_routeSimpleNewOrder(request)).SubmitSimpleAsync(request, ct);

    public Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct = default)
        => Route(_routeReplace(request)).ReplaceAsync(request, ct);

    public Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct = default)
        => Route(_routeSimpleReplace(request)).ReplaceSimpleAsync(request, ct);

    public Task CancelAsync(CancelOrderRequest request, CancellationToken ct = default)
        => Route(_routeCancel(request)).CancelAsync(request, ct);

    public Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct = default)
        => Route(_routeMassAction(request)).MassActionAsync(request, ct);

    private EntryPointClient Route(byte segment)
    {
        if (_clients.TryGetValue(segment, out var c)) return c;
        throw new InvalidOperationException(
            $"No EntryPointClient configured for MarketSegmentID={segment}. Configured: [{string.Join(',', _clients.Keys)}].");
    }

    private static NewOrderRequest MapSimple(SimpleNewOrderRequest s) => new()
    {
        ClOrdID = s.ClOrdID,
        SecurityId = s.SecurityId,
        Side = s.Side,
        OrderType = (OrderType)(byte)s.OrderType,
        OrderQty = s.OrderQty,
        Price = s.Price,
    };

    private static ReplaceOrderRequest MapSimple(SimpleModifyRequest s) => new()
    {
        ClOrdID = s.ClOrdID,
        OrigClOrdID = s.OrigClOrdID,
        SecurityId = s.SecurityId,
        Side = s.Side,
        OrderType = (OrderType)(byte)s.OrderType,
        OrderQty = s.OrderQty,
        Price = s.Price,
    };

    public async ValueTask DisposeAsync()
    {
        try { _pumpCts.Cancel(); } catch { }
        try { await Task.WhenAll(_pumps).ConfigureAwait(false); } catch { }
        _aggregatedEvents.Writer.TryComplete();
        foreach (var c in _clients.Values)
            await c.DisposeAsync().ConfigureAwait(false);
        _pumpCts.Dispose();
    }
}
