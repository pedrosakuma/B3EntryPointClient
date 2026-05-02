using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Framing;
using SbeTerminationCode = B3.Entrypoint.Fixp.Sbe.V6.TerminationCode;

namespace B3.EntryPoint.Client.TestPeer;

/// <summary>
/// Minimal in-process FIXP gateway for conformance tests. Accepts TCP
/// connections, reads SOFH-framed SBE messages, and replies to the FIXP
/// session-layer handshake (Negotiate/Establish/Terminate). Application
/// messages are silently consumed so the client's send paths exercise their
/// full encode/flush cycle.
///
/// Not a complete B3 simulator — just enough to drive the conformance tests
/// without an external endpoint. Application response messages
/// (ExecutionReport*) are out of scope here.
/// </summary>
public sealed class InProcessFixpTestPeer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = new();
    private readonly TestPeerOptions _options;
    private Task? _acceptLoop;

    public InProcessFixpTestPeer() : this(new TestPeerOptions()) { }

    /// <summary>
    /// Back-compat ctor: TLS-only knob. Equivalent to
    /// <c>new InProcessFixpTestPeer(new TestPeerOptions { ServerCertificate = serverCertificate })</c>.
    /// </summary>
    public InProcessFixpTestPeer(X509Certificate2? serverCertificate)
        : this(new TestPeerOptions { ServerCertificate = serverCertificate }) { }

    /// <summary>
    /// Creates a peer with full <see cref="TestPeerOptions"/> control: TLS,
    /// response latency, scenario, per-firm credentials.
    /// </summary>
    public InProcessFixpTestPeer(TestPeerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    /// <summary>Configured options for this peer.</summary>
    public TestPeerOptions Options => _options;

    /// <summary>Loopback endpoint the peer is listening on. Only valid after <see cref="Start"/>.</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Alias for <see cref="LocalEndpoint"/> kept for back-compat with the pre-NuGet shape.</summary>
    public IPEndPoint Endpoint => LocalEndpoint;

    /// <summary>
    /// Raised for every inbound frame the peer reads (after SOFH framing).
    /// Subscribers run on the connection task — do not block. The payload is
    /// owned by the peer and only valid for the call duration.
    /// </summary>
    public event EventHandler<TestPeerMessageEventArgs>? MessageReceived;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stops accepting new connections and waits for in-flight connection
    /// handlers to finish (with a short grace). Equivalent to disposing
    /// without freeing the underlying <see cref="CancellationTokenSource"/>.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        Task[] tasks;
        lock (_connections) tasks = _connections.ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); } catch { }
        if (_acceptLoop is not null)
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); } catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                lock (_connections)
                    _connections.Add(HandleConnectionAsync(tcp, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private sealed class ConnectionState
    {
        public uint OutSeq = 1;
        public CancellationTokenSource? KeepAliveCts;
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

    /// <summary>
    /// Single point of egress for every outbound frame. Serializes writes per
    /// connection so concurrent senders (app frames + the peer-side Sequence
    /// keep-alive loop) cannot interleave SOFH frames on the wire.
    /// Latency is applied while holding the lock so total throughput observed
    /// by the client is the sum, not the max, of concurrent senders.
    /// </summary>
    private async Task SendFrameAsync(Stream stream, ConnectionState state, byte[] buffer, int totalSize, CancellationToken ct)
    {
        await state.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ApplyLatencyAsync(ct).ConfigureAwait(false);
            await stream.WriteAsync(buffer.AsMemory(0, totalSize), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { }
        finally
        {
            try { state.SendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        var state = new ConnectionState();
        var serverCertificate = _options.ServerCertificate;
        try
        {
            using (tcp)
            {
                System.IO.Stream stream = tcp.GetStream();
                SslStream? ssl = null;
                if (serverCertificate is not null)
                {
                    ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCertificate,
                        ClientCertificateRequired = false,
                    }, ct).ConfigureAwait(false);
                    stream = ssl;
                }
                await using var _ssl = ssl;
                await using var _stream = stream;
                while (!ct.IsCancellationRequested)
                {
                    byte[] frame;
                    try
                    {
                        frame = await SofhFrameReader.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException) { return; }
                    catch (IOException) { return; }

                    var templateId = ReadTemplateId(frame);
                    RaiseMessageReceived(templateId, frame);
                    switch (templateId)
                    {
                        case NegotiateData.MESSAGE_ID:
                            if (!ValidateNegotiate(frame))
                            {
                                // Credentials map configured and firm not allowed → close cold.
                                return;
                            }
                            await SendNegotiateResponseAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case EstablishData.MESSAGE_ID:
                            await SendEstablishAckAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case TerminateData.MESSAGE_ID:
                            state.KeepAliveCts?.Cancel();
                            await SendTerminateEchoAsync(stream, frame, state, ct).ConfigureAwait(false);
                            return;
                        case NewOrderSingleData.MESSAGE_ID:
                            await DispatchNewOrderAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case OrderCancelRequestData.MESSAGE_ID:
                            await DispatchCancelAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case OrderCancelReplaceRequestData.MESSAGE_ID:
                            await DispatchModifyAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case OrderMassActionRequestData.MESSAGE_ID:
                            await SendOrderMassActionReportAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case RetransmitRequestData.MESSAGE_ID:
                            await SendRetransmissionAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        default:
                            // Application or session-layer messages we don't model — swallow.
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { state.KeepAliveCts?.Cancel(); } catch { }
            state.KeepAliveCts?.Dispose();
        }
    }

    private void RaiseMessageReceived(ushort templateId, byte[] frame)
    {
        var handler = MessageReceived;
        if (handler is null) return;
        var payloadStart = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE;
        var payload = frame.Length > payloadStart
            ? new ReadOnlyMemory<byte>(frame, payloadStart, frame.Length - payloadStart)
            : ReadOnlyMemory<byte>.Empty;
        try { handler(this, new TestPeerMessageEventArgs(templateId, payload)); } catch { /* never break the peer loop */ }
    }

    private bool ValidateNegotiate(byte[] frame)
    {
        var creds = _options.Credentials;
        if (creds is null || creds.Count == 0) return true;
        if (!NegotiateData.TryParse(frame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out var reader))
            return false;
        var firmId = reader.Data.EnteringFirm.Value;
        return creds.ContainsKey(firmId);
    }

    private async Task DispatchNewOrderAsync(Stream stream, byte[] frame, ConnectionState state, CancellationToken ct)
    {
        var payload = frame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!NewOrderSingleData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;
        var orderQty = req.OrderQty.Value;
        var priceMantissa = req.Price.Mantissa; // optional → null for market
        decimal? reqPrice = priceMantissa.HasValue ? MantissaToDecimal(priceMantissa.GetValueOrDefault()) : null;
        var ctx = new NewOrderContext(
            SessionId: req.BusinessHeader.SessionID.Value,
            EnteringFirm: 0u, // SBE NewOrderSingle does not carry EnteringFirm; reserved for future.
            SecurityId: req.SecurityID.Value,
            ClOrdId: req.ClOrdID.Value.ToString())
        {
            OrderQty = orderQty,
            Price = reqPrice,
            Side = req.Side,
            MsgSeqNum = req.BusinessHeader.MsgSeqNum.Value,
        };
        var response = _options.Scenario.OnNewOrder(ctx);
        switch (response)
        {
            case NewOrderResponse.RejectBusiness rej:
                await SendBusinessMessageRejectAsync(stream, ctx, rej, state, ct).ConfigureAwait(false);
                break;
            case NewOrderResponse.AcceptAndFill fill:
                await SendExecutionReportNewAsync(stream, frame, state, ct).ConfigureAwait(false);
                await SendExecutionReportTradeAsync(stream, frame, ctx, fill, state, ct).ConfigureAwait(false);
                break;
            case NewOrderResponse.AcceptAsNew:
            default:
                await SendExecutionReportNewAsync(stream, frame, state, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task DispatchCancelAsync(Stream stream, byte[] frame, ConnectionState state, CancellationToken ct)
    {
        var payload = frame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!OrderCancelRequestData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;
        var ctx = new CancelContext(
            SessionId: req.BusinessHeader.SessionID.Value,
            SecurityId: req.SecurityID.Value,
            ClOrdId: req.ClOrdID.Value.ToString(),
            OrigClOrdId: (req.OrigClOrdID ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            MsgSeqNum = req.BusinessHeader.MsgSeqNum.Value,
        };
        var response = _options.Scenario.OnCancel(ctx);
        switch (response)
        {
            case CancelResponse.Reject rej:
                await SendExecutionReportRejectAsync(stream, frame, req.BusinessHeader.SessionID, req.ClOrdID, req.SecurityID, req.Side, CxlRejResponseTo.CANCEL, rej.Reason, rej.RejReason, state, ct).ConfigureAwait(false);
                break;
            case CancelResponse.Accept:
            default:
                await SendExecutionReportCancelAsync(stream, frame, state, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task DispatchModifyAsync(Stream stream, byte[] frame, ConnectionState state, CancellationToken ct)
    {
        var payload = frame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!OrderCancelReplaceRequestData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;
        var ctx = new ModifyContext(
            SessionId: req.BusinessHeader.SessionID.Value,
            SecurityId: req.SecurityID.Value,
            ClOrdId: req.ClOrdID.Value.ToString(),
            OrigClOrdId: (req.OrigClOrdID ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            OrderQty = req.OrderQty.Value,
            Price = req.Price.Mantissa.HasValue ? MantissaToDecimal(req.Price.Mantissa.GetValueOrDefault()) : null,
            MsgSeqNum = req.BusinessHeader.MsgSeqNum.Value,
        };
        var response = _options.Scenario.OnModify(ctx);
        switch (response)
        {
            case ModifyResponse.Reject rej:
                await SendExecutionReportRejectAsync(stream, frame, req.BusinessHeader.SessionID, req.ClOrdID, req.SecurityID, req.Side, CxlRejResponseTo.REPLACE, rej.Reason, rej.RejReason, state, ct).ConfigureAwait(false);
                break;
            case ModifyResponse.Accept:
            default:
                await SendExecutionReportModifyAsync(stream, frame, state, ct).ConfigureAwait(false);
                break;
        }
    }

    private static decimal MantissaToDecimal(long mantissa) => (decimal)mantissa / 10000m;

    private async Task ApplyLatencyAsync(CancellationToken ct)
    {
        var latency = _options.ResponseLatency;
        if (latency > TimeSpan.Zero)
            try { await Task.Delay(latency, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private async Task SendNegotiateResponseAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        if (!NegotiateData.TryParse(requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var totalSize = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + NegotiateResponseData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        NegotiateResponseData.WriteHeader(buffer.AsSpan(SofhFrameReader.HeaderSize));

        var resp = new NegotiateResponseData
        {
            SessionID = req.SessionID,
            SessionVerID = req.SessionVerID,
            RequestTimestamp = req.Timestamp,
            EnteringFirm = req.EnteringFirm,
        };
        if (!resp.TryEncode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out _))
            return;

        await SendFrameAsync(stream, state, buffer, totalSize, ct).ConfigureAwait(false);
    }

    private async Task SendEstablishAckAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        if (!EstablishData.TryParse(requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var totalSize = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + EstablishAckData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        EstablishAckData.WriteHeader(buffer.AsSpan(SofhFrameReader.HeaderSize));

        var ack = new EstablishAckData
        {
            SessionID = req.SessionID,
            SessionVerID = req.SessionVerID,
            RequestTimestamp = req.Timestamp,
            KeepAliveInterval = req.KeepAliveInterval,
            NextSeqNo = new SeqNum(1u),
            LastIncomingSeqNo = new SeqNum(req.NextSeqNo.Value > 0u ? req.NextSeqNo.Value - 1u : 0u),
        };
        var keepAliveMs = req.KeepAliveInterval.Time;
        if (!ack.TryEncode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out _))
            return;

        await SendFrameAsync(stream, state, buffer, totalSize, ct).ConfigureAwait(false);

        // Spec §4.6: peer-side keep-alive. Emit Sequence frames at the
        // negotiated interval so the client can observe inbound heartbeats.
        if (keepAliveMs > 0 && state.KeepAliveCts is null)
        {
            state.KeepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => PeerSequenceLoopAsync(stream, state, TimeSpan.FromMilliseconds(keepAliveMs), state.KeepAliveCts.Token));
        }
    }

    private async Task PeerSequenceLoopAsync(Stream stream, ConnectionState state, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var totalSize = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + SequenceData.MESSAGE_SIZE;
                var buffer = new byte[totalSize];
                SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
                SequenceData.WriteHeader(buffer.AsSpan(SofhFrameReader.HeaderSize));
                var seq = new SequenceData { NextSeqNo = new SeqNum(state.OutSeq) };
                if (!seq.TryEncode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out _))
                    return;
                await SendFrameAsync(stream, state, buffer, totalSize, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task SendTerminateEchoAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        if (!TerminateData.TryParse(requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var totalSize = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + TerminateData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        TerminateData.WriteHeader(buffer.AsSpan(SofhFrameReader.HeaderSize));

        var echo = new TerminateData
        {
            SessionID = req.SessionID,
            SessionVerID = req.SessionVerID,
            TerminationCode = SbeTerminationCode.FINISHED,
        };
        if (!echo.TryEncode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out _))
            return;

        await SendFrameAsync(stream, state, buffer, totalSize, ct).ConfigureAwait(false);
    }

    private async Task SendExecutionReportNewAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        var payload = requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!NewOrderSingleData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var msg = new ExecutionReport_NewData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = req.BusinessHeader.SessionID,
                MsgSeqNum = new SeqNum(state.OutSeq++),
            },
            Side = req.Side,
            OrdStatus = OrdStatus.NEW,
            ClOrdID = req.ClOrdID,
            OrderID = new OrderID((ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq)),
            SecurityID = req.SecurityID,
            TransactTime = new UTCTimestampNanos { Time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL },
        };

        await SendAppFrameAsync(stream, state, ExecutionReport_NewData.MESSAGE_ID, ExecutionReport_NewData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => ExecutionReport_NewData.TryEncode(msg, buf, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, out bw),
            ct).ConfigureAwait(false);
    }

    private async Task SendExecutionReportCancelAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        var payload = requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!OrderCancelRequestData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var msg = new ExecutionReport_CancelData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = req.BusinessHeader.SessionID,
                MsgSeqNum = new SeqNum(state.OutSeq++),
            },
            Side = req.Side,
            OrdStatus = OrdStatus.CANCELED,
            ClOrdID = req.ClOrdID,
            OrderID = new OrderID((ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq)),
            SecurityID = req.SecurityID,
            TransactTime = new UTCTimestampNanos { Time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL },
        };
        msg.SetOrigClOrdID(req.OrigClOrdID);

        await SendAppFrameAsync(stream, state, ExecutionReport_CancelData.MESSAGE_ID, ExecutionReport_CancelData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => ExecutionReport_CancelData.TryEncode(msg, buf, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, out bw),
            ct).ConfigureAwait(false);
    }

    private async Task SendExecutionReportModifyAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        var payload = requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!OrderCancelReplaceRequestData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var msg = new ExecutionReport_ModifyData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = req.BusinessHeader.SessionID,
                MsgSeqNum = new SeqNum(state.OutSeq++),
            },
            Side = req.Side,
            OrdStatus = OrdStatus.REPLACED,
            ClOrdID = req.ClOrdID,
            OrderID = new OrderID((ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq)),
            SecurityID = req.SecurityID,
            TransactTime = new UTCTimestampNanos { Time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL },
        };
        msg.SetOrigClOrdID(req.OrigClOrdID);

        await SendAppFrameAsync(stream, state, ExecutionReport_ModifyData.MESSAGE_ID, ExecutionReport_ModifyData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => ExecutionReport_ModifyData.TryEncode(msg, buf, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, out bw),
            ct).ConfigureAwait(false);
    }

    private async Task SendOrderMassActionReportAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        var payload = requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!OrderMassActionRequestData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var msg = new OrderMassActionReportData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = req.BusinessHeader.SessionID,
                MsgSeqNum = new SeqNum(state.OutSeq++),
            },
            MassActionType = req.MassActionType,
            ClOrdID = req.ClOrdID,
            MassActionReportID = new MassActionReportID((ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq)),
            TransactTime = new UTCTimestampNanos { Time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL },
            MassActionResponse = MassActionResponse.ACCEPTED,
        };
        msg.SetMassActionScope(req.MassActionScope);

        await SendAppFrameAsync(stream, state, OrderMassActionReportData.MESSAGE_ID, OrderMassActionReportData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => msg.TryEncode(buf, out bw),
            ct).ConfigureAwait(false);
    }

    private async Task SendExecutionReportTradeAsync(Stream stream, byte[] requestFrame, NewOrderContext ctx, NewOrderResponse.AcceptAndFill fill, ConnectionState state, CancellationToken ct)
    {
        var payload = requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE);
        if (!NewOrderSingleData.TryParse(payload, out var reader))
            return;
        ref readonly var req = ref reader.Data;

        var orderQty = ctx.OrderQty ?? req.OrderQty.Value;
        var requestedFill = fill.FillQty ?? orderQty;
        var fillQty = requestedFill > orderQty ? orderQty : requestedFill;
        var isPartial = fillQty < orderQty;
        var leavesQty = orderQty - fillQty;

        var fillPx = fill.FillPrice ?? ctx.Price ?? 1.0m;
        var pxMantissa = (long)decimal.Round(fillPx * 10000m);
        var execId = (ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq);
        var orderId = (ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq);
        var tradeId = (ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq);
        var nowNanos = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;

        var msg = new ExecutionReport_TradeData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = req.BusinessHeader.SessionID,
                MsgSeqNum = new SeqNum(state.OutSeq++),
            },
            Side = req.Side,
            OrdStatus = isPartial ? OrdStatus.PARTIALLY_FILLED : OrdStatus.FILLED,
            SecurityID = req.SecurityID,
            LastQty = new Quantity(fillQty),
            LastPx = new Price { Mantissa = pxMantissa },
            ExecID = new ExecID(execId),
            TransactTime = new UTCTimestampNanos { Time = nowNanos },
            LeavesQty = new Quantity(leavesQty),
            CumQty = new Quantity(fillQty),
            ExecType = ExecType.TRADE,
            TradeID = new TradeID((uint)(tradeId & 0xFFFFFFFFu)),
            OrderID = new OrderID(orderId),
        };
        msg.SetClOrdID(req.ClOrdID.Value);

        await SendAppFrameAsync(stream, state, ExecutionReport_TradeData.MESSAGE_ID, ExecutionReport_TradeData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => ExecutionReport_TradeData.TryEncode(msg, buf, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, out bw),
            ct).ConfigureAwait(false);
    }

    private async Task SendBusinessMessageRejectAsync(Stream stream, NewOrderContext ctx, NewOrderResponse.RejectBusiness rej, ConnectionState state, CancellationToken ct)
    {
        // Schema bound: TextEncoding length prefix is 1 byte → 255 max; B3 caps at 250.
        const int MaxTextBytes = 250;
        var reasonBytes = rej.Reason is { Length: > 0 }
            ? System.Text.Encoding.ASCII.GetBytes(rej.Reason)
            : Array.Empty<byte>();
        if (reasonBytes.Length > MaxTextBytes)
            Array.Resize(ref reasonBytes, MaxTextBytes);

        var nowNanos = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        var msg = new BusinessMessageRejectData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = new SessionID(ctx.SessionId),
                MsgSeqNum = new SeqNum(state.OutSeq++),

            },
            RefMsgType = MessageType.NewOrderSingle,
            RefSeqNum = new SeqNum(ctx.MsgSeqNum),
            BusinessRejectReason = new RejReason(rej.RejReason ?? 99u),
        };

        var reasonMemory = new ReadOnlyMemory<byte>(reasonBytes);
        await SendAppFrameAsync(stream, state, BusinessMessageRejectData.MESSAGE_ID, BusinessMessageRejectData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => BusinessMessageRejectData.TryEncode(msg, buf, ReadOnlySpan<byte>.Empty, reasonBytes, out bw),
            new[] { ReadOnlyMemory<byte>.Empty, reasonMemory }, ct).ConfigureAwait(false);
    }

    private async Task SendExecutionReportRejectAsync(Stream stream, byte[] requestFrame, SessionID sessionId, ClOrdID clOrdId, SecurityID securityId, Side side,
        CxlRejResponseTo responseTo, string reason, uint? rejReasonCode, ConnectionState state, CancellationToken ct)
    {
        _ = requestFrame;
        const int MaxTextBytes = 250;
        var reasonBytes = reason is { Length: > 0 }
            ? System.Text.Encoding.ASCII.GetBytes(reason)
            : Array.Empty<byte>();
        if (reasonBytes.Length > MaxTextBytes)
            Array.Resize(ref reasonBytes, MaxTextBytes);

        var nowNanos = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        var execId = (ulong)System.Threading.Interlocked.Increment(ref _orderIdSeq);
        var msg = new ExecutionReport_RejectData
        {
            BusinessHeader = new OutboundBusinessHeader
            {
                SessionID = sessionId,
                MsgSeqNum = new SeqNum(state.OutSeq++),

            },
            Side = side,
            CxlRejResponseTo = responseTo,
            ClOrdID = clOrdId,
            SecurityID = securityId,
            OrdRejReason = new RejReason(rejReasonCode ?? 99u),
            TransactTime = new UTCTimestampNanos { Time = nowNanos },
            ExecID = new ExecID(execId),
        };

        var reasonMemory = new ReadOnlyMemory<byte>(reasonBytes);
        await SendAppFrameAsync(stream, state, ExecutionReport_RejectData.MESSAGE_ID, ExecutionReport_RejectData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => ExecutionReport_RejectData.TryEncode(msg, buf, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, reasonBytes, out bw),
            new[] { ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, reasonMemory }, ct).ConfigureAwait(false);
    }

    private async Task SendRetransmissionAsync(Stream stream, byte[] requestFrame, ConnectionState state, CancellationToken ct)
    {
        if (!RetransmitRequestData.TryParse(requestFrame.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out var reader))
            return;
        var sessionId = reader.Data.SessionID;
        var requestTs = reader.Data.Timestamp;
        var fromSeqNo = reader.Data.FromSeqNo;

        var totalSize = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + RetransmissionData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        RetransmissionData.WriteHeader(buffer.AsSpan(SofhFrameReader.HeaderSize));

        var msg = new RetransmissionData
        {
            SessionID = sessionId,
            RequestTimestamp = requestTs,
            NextSeqNo = fromSeqNo,
            Count = new MessageCounter(0u),
        };
        if (!msg.TryEncode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out _))
            return;

        await SendFrameAsync(stream, state, buffer, totalSize, ct).ConfigureAwait(false);
    }

    private static long _orderIdSeq;

    /// <summary>
    /// Encodes and sends an application frame. The buffer is sized to fit the
    /// fixed payload plus all caller-supplied variable-length data sections,
    /// each prefixed by its 1-byte length. Pass empty spans for sections you
    /// don't intend to populate — every section the message defines must be
    /// listed so its length-prefix byte is reserved.
    /// </summary>
    private async Task SendAppFrameAsync(Stream stream, ConnectionState state, ushort templateId, int payloadSize, EncodeDelegate encode,
        ReadOnlyMemory<byte>[] varDataSections, CancellationToken ct)
    {
        // Each var-data section on the wire is `byte length + payload`, even
        // when payload is empty (length prefix is always emitted by the encoder).
        var varTotal = 0;
        for (int i = 0; i < varDataSections.Length; i++)
            varTotal += 1 + varDataSections[i].Length;
        // Floor at 4 bytes so messages whose var-data sections aren't all
        // listed by the caller still don't overrun.
        var varReserve = Math.Max(varTotal, 4);
        var maxTotal = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + payloadSize + varReserve;
        var buffer = new byte[maxTotal];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SofhFrameReader.HeaderSize), (ushort)payloadSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SofhFrameReader.HeaderSize + 2), templateId);
        if (!encode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out var bytesWritten))
            return;
        var totalSize = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + bytesWritten;
        SofhFrameWriter.WriteHeader(buffer.AsSpan(0, totalSize), checked((ushort)totalSize));
        await SendFrameAsync(stream, state, buffer, totalSize, ct).ConfigureAwait(false);
    }

    private Task SendAppFrameAsync(Stream stream, ConnectionState state, ushort templateId, int payloadSize, EncodeDelegate encode, CancellationToken ct)
        => SendAppFrameAsync(stream, state, templateId, payloadSize, encode, Array.Empty<ReadOnlyMemory<byte>>(), ct);

    private delegate bool EncodeDelegate(Span<byte> buffer, out int bytesWritten);

    private static ushort ReadTemplateId(byte[] frame) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            frame.AsSpan(SofhFrameReader.HeaderSize + 2, 2));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        Task[] tasks;
        lock (_connections) tasks = _connections.ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        if (_acceptLoop is not null)
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
