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
                            await SendNegotiateResponseAsync(stream, frame, ct).ConfigureAwait(false);
                            break;
                        case EstablishData.MESSAGE_ID:
                            await SendEstablishAckAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case TerminateData.MESSAGE_ID:
                            state.KeepAliveCts?.Cancel();
                            await SendTerminateEchoAsync(stream, frame, ct).ConfigureAwait(false);
                            return;
                        case NewOrderSingleData.MESSAGE_ID:
                            await DispatchNewOrderAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case OrderCancelRequestData.MESSAGE_ID:
                            await SendExecutionReportCancelAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case OrderCancelReplaceRequestData.MESSAGE_ID:
                            await SendExecutionReportModifyAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case OrderMassActionRequestData.MESSAGE_ID:
                            await SendOrderMassActionReportAsync(stream, frame, state, ct).ConfigureAwait(false);
                            break;
                        case RetransmitRequestData.MESSAGE_ID:
                            await SendRetransmissionAsync(stream, frame, ct).ConfigureAwait(false);
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
        var ctx = new NewOrderContext(
            SessionId: req.BusinessHeader.SessionID.Value,
            EnteringFirm: 0u, // SBE NewOrderSingle does not carry EnteringFirm; reserved for future.
            SecurityId: req.SecurityID.Value,
            ClOrdId: req.ClOrdID.Value.ToString());
        var response = _options.Scenario.OnNewOrder(ctx);
        switch (response)
        {
            case NewOrderResponse.RejectBusiness rej:
                // BusinessReject not yet wired in this peer — emit nothing, the
                // client-side BusinessReject path is exercised in unit tests via
                // hand-crafted frames. Surface the reason via the event so
                // downstream tests can assert.
                _ = rej; // intentional: see ITestPeerScenario doc-comment.
                break;
            case NewOrderResponse.AcceptAndFill:
                await SendExecutionReportNewAsync(stream, frame, state, ct).ConfigureAwait(false);
                // Trade ER not yet implemented — see ITestPeerScenario doc-comment.
                break;
            case NewOrderResponse.AcceptAsNew:
            default:
                await SendExecutionReportNewAsync(stream, frame, state, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyLatencyAsync(CancellationToken ct)
    {
        var latency = _options.ResponseLatency;
        if (latency > TimeSpan.Zero)
            try { await Task.Delay(latency, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private async Task SendNegotiateResponseAsync(Stream stream, byte[] requestFrame, CancellationToken ct)
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

        await ApplyLatencyAsync(ct).ConfigureAwait(false);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
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

        await ApplyLatencyAsync(ct).ConfigureAwait(false);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

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
                await ApplyLatencyAsync(ct).ConfigureAwait(false);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task SendTerminateEchoAsync(Stream stream, byte[] requestFrame, CancellationToken ct)
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

        try
        {
            await ApplyLatencyAsync(ct).ConfigureAwait(false);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { }
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

        await SendAppFrameAsync(stream, ExecutionReport_NewData.MESSAGE_ID, ExecutionReport_NewData.MESSAGE_SIZE,
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

        await SendAppFrameAsync(stream, ExecutionReport_CancelData.MESSAGE_ID, ExecutionReport_CancelData.MESSAGE_SIZE,
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

        await SendAppFrameAsync(stream, ExecutionReport_ModifyData.MESSAGE_ID, ExecutionReport_ModifyData.MESSAGE_SIZE,
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

        await SendAppFrameAsync(stream, OrderMassActionReportData.MESSAGE_ID, OrderMassActionReportData.MESSAGE_SIZE,
            (Span<byte> buf, out int bw) => msg.TryEncode(buf, out bw),
            ct).ConfigureAwait(false);
    }

    private async Task SendRetransmissionAsync(Stream stream, byte[] requestFrame, CancellationToken ct)
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

        try
        {
            await ApplyLatencyAsync(ct).ConfigureAwait(false);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

    private static long _orderIdSeq;

    private async Task SendAppFrameAsync(Stream stream, ushort templateId, int payloadSize, EncodeDelegate encode, CancellationToken ct)
    {
        // Allocate generous trailing room for var-data sections (DeskID, Memo, etc.).
        const int VarDataPad = 16;
        var maxTotal = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + payloadSize + VarDataPad;
        var buffer = new byte[maxTotal];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SofhFrameReader.HeaderSize), (ushort)payloadSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SofhFrameReader.HeaderSize + 2), templateId);
        if (!encode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out var bytesWritten))
            return;
        var totalSize = SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE + bytesWritten;
        SofhFrameWriter.WriteHeader(buffer.AsSpan(0, totalSize), checked((ushort)totalSize));
        try
        {
            await ApplyLatencyAsync(ct).ConfigureAwait(false);
            await stream.WriteAsync(buffer.AsMemory(0, totalSize), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

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
