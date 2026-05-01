using System.Net;
using System.Net.Sockets;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Framing;

namespace B3.EntryPoint.TestPeer;

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
public sealed class InMemoryFixpPeer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = new();
    private Task? _acceptLoop;

    public InMemoryFixpPeer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
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

    private static async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using (tcp)
            await using (var stream = tcp.GetStream())
            {
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
                    switch (templateId)
                    {
                        case NegotiateData.MESSAGE_ID:
                            await SendNegotiateResponseAsync(stream, frame, ct).ConfigureAwait(false);
                            break;
                        case EstablishData.MESSAGE_ID:
                            await SendEstablishAckAsync(stream, frame, ct).ConfigureAwait(false);
                            break;
                        case TerminateData.MESSAGE_ID:
                            await SendTerminateEchoAsync(stream, frame, ct).ConfigureAwait(false);
                            return;
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
    }

    private static async Task SendNegotiateResponseAsync(Stream stream, byte[] requestFrame, CancellationToken ct)
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

        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendEstablishAckAsync(Stream stream, byte[] requestFrame, CancellationToken ct)
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
        if (!ack.TryEncode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out _))
            return;

        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendTerminateEchoAsync(Stream stream, byte[] requestFrame, CancellationToken ct)
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
            TerminationCode = TerminationCode.FINISHED,
        };
        if (!echo.TryEncode(buffer.AsSpan(SofhFrameReader.HeaderSize + MessageHeader.MESSAGE_SIZE), out _))
            return;

        try
        {
            await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

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
