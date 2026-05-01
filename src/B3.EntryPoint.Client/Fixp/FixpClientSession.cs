using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Threading.Channels;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Framing;
using B3.EntryPoint.Client.Models;
using SbeTerminationCode = B3.Entrypoint.Fixp.Sbe.V6.TerminationCode;

namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Owns one TCP-backed FIXP client session: encodes/decodes Negotiate and
/// Establish messages over SOFH-framed SBE per spec §4.4–§4.6, drives the
/// pure <see cref="FixpClientStateMachine"/>, and exposes async hooks for the
/// outer <see cref="EntryPointClient"/>.
/// </summary>
/// <remarks>
/// Bootstrap scope: only Negotiate→NegotiateResponse and Establish→EstablishmentAck
/// are implemented end-to-end. Heartbeats, Sequence/Retransmit, and the
/// application-message flow ride on top of this in follow-up issues.
/// </remarks>
internal sealed class FixpClientSession : IAsyncDisposable
{
    private const int SofhSize = SofhFrameReader.HeaderSize;
    private const int SbeHeaderSize = MessageHeader.MESSAGE_SIZE;

    private readonly Stream _stream;
    private readonly EntryPointClientOptions _options;
    private readonly FixpClientStateMachine _machine = new();

    public FixpClientSession(Stream stream, EntryPointClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        _stream = stream;
        _options = options;
        _machine.Fire(FixpClientTrigger.TcpConnected);
    }

    public FixpClientState State => _machine.State;

    public async Task NegotiateAsync(CancellationToken ct)
    {
        if (_machine.State != FixpClientState.TcpConnected)
            throw new InvalidOperationException($"Cannot Negotiate from state {_machine.State}.");

        await SendNegotiateAsync(ct).ConfigureAwait(false);
        _machine.Fire(FixpClientTrigger.SendNegotiate);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var frame = await SofhFrameReader.ReadFrameAsync(_stream, timeout.Token).ConfigureAwait(false);
        var templateId = ReadTemplateId(frame);

        if (templateId == NegotiateResponseData.MESSAGE_ID)
        {
            _machine.Fire(FixpClientTrigger.NegotiateResponseReceived);
        }
        else if (templateId == NegotiateRejectData.MESSAGE_ID)
        {
            _machine.Fire(FixpClientTrigger.NegotiateRejectReceived);
            throw new FixpRejectedException("Negotiate rejected by peer.");
        }
        else
        {
            _machine.Fire(FixpClientTrigger.ProtocolError);
            throw new InvalidDataException($"Unexpected templateId {templateId} during Negotiate handshake.");
        }
    }

    public async Task EstablishAsync(CancellationToken ct)
    {
        if (_machine.State != FixpClientState.Negotiated)
            throw new InvalidOperationException($"Cannot Establish from state {_machine.State}.");

        await SendEstablishAsync(ct).ConfigureAwait(false);
        _machine.Fire(FixpClientTrigger.SendEstablish);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var frame = await SofhFrameReader.ReadFrameAsync(_stream, timeout.Token).ConfigureAwait(false);
        var templateId = ReadTemplateId(frame);

        if (templateId == EstablishAckData.MESSAGE_ID)
        {
            _machine.Fire(FixpClientTrigger.EstablishAckReceived);
        }
        else if (templateId == EstablishRejectData.MESSAGE_ID)
        {
            _machine.Fire(FixpClientTrigger.EstablishRejectReceived);
            throw new FixpRejectedException("Establish rejected by peer.");
        }
        else
        {
            _machine.Fire(FixpClientTrigger.ProtocolError);
            throw new InvalidDataException($"Unexpected templateId {templateId} during Establish handshake.");
        }
    }

    public async Task TerminateAsync(SbeTerminationCode code, CancellationToken ct)
    {
        if (_machine.State == FixpClientState.Disconnected || _machine.State == FixpClientState.Terminated)
            return;

        try
        {
            await SendTerminateAsync(code, ct).ConfigureAwait(false);
        }
        catch (IOException) { /* peer may have already closed */ }
        catch (ObjectDisposedException) { /* stream already closed */ }

        if (_machine.CanFire(FixpClientTrigger.SendTerminate))
            _machine.Fire(FixpClientTrigger.SendTerminate);
    }

    private long _outboundSeqNum;
    private Task? _inboundLoop;
    private CancellationTokenSource? _inboundCts;
    private ChannelWriter<EntryPointEvent>? _eventWriter;

    /// <summary>
    /// Starts the inbound dispatch loop. Decoded application events are written to
    /// <paramref name="writer"/>; session-layer messages (Sequence/NotApplied/etc.)
    /// are silently consumed for now (#24/#25 will surface them).
    /// Must be called after <see cref="EstablishAsync"/>.
    /// </summary>
    public void StartInboundLoop(ChannelWriter<EntryPointEvent> writer)
    {
        if (_inboundLoop is not null)
            throw new InvalidOperationException("Inbound loop already running.");
        _eventWriter = writer;
        _inboundCts = new CancellationTokenSource();
        _inboundLoop = Task.Run(() => RunInboundLoopAsync(_inboundCts.Token));
    }

    private async Task RunInboundLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await SofhFrameReader.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                if (frame.Length < SofhSize + SbeHeaderSize) continue;
                if (InboundDecoder.TryDecode(frame, out var evt) && evt is not null)
                {
                    await _eventWriter!.WriteAsync(evt, ct).ConfigureAwait(false);
                }
                else
                {
                    var templateId = InboundDecoder.ReadTemplateId(frame);
                    var payload = frame.AsSpan(SofhSize + SbeHeaderSize);
                    switch (templateId)
                    {
                        case SequenceData.MESSAGE_ID:
                            OnInboundSequence?.Invoke(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload));
                            break;
                        case RetransmissionData.MESSAGE_ID:
                            // SessionID(8) + RequestTimestamp(8) + NextSeqNo(4) + Count(4) = 24 bytes
                            if (payload.Length >= RetransmissionData.MESSAGE_SIZE)
                            {
                                var reqTs = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8, 8));
                                var nextSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
                                var cnt = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(20, 4));
                                OnInboundRetransmission?.Invoke(nextSeq, cnt, reqTs);
                            }
                            break;
                        case RetransmitRejectData.MESSAGE_ID:
                            // SessionID(8) + RequestTimestamp(8) + Code(1)
                            if (payload.Length >= RetransmitRejectData.MESSAGE_SIZE)
                            {
                                var reqTs = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8, 8));
                                var code = (uint)payload[16];
                                OnInboundRetransmitReject?.Invoke(code, reqTs);
                            }
                            break;
                        case NotAppliedData.MESSAGE_ID:
                            // FromSeqNo(4) + Count(4) = 8 bytes
                            if (payload.Length >= NotAppliedData.MESSAGE_SIZE)
                            {
                                var fromSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
                                var cnt = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
                                OnInboundNotApplied?.Invoke(fromSeq, cnt);
                            }
                            break;
                        // Terminate handled in #26.
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (EndOfStreamException) { /* peer closed */ }
        catch (IOException) { /* peer closed */ }
        catch (Exception ex)
        {
            _eventWriter?.TryComplete(ex);
            return;
        }
        _eventWriter?.TryComplete();
    }

    /// <summary>Sends an Order Entry application frame previously encoded via <see cref="OrderEntryEncoder"/>.</summary>
    public async Task SendApplicationFrameAsync(byte[] buffer, int length, CancellationToken ct)
    {
        if (_machine.State != FixpClientState.Established)
            throw new InvalidOperationException($"Cannot send application frame from state {_machine.State}.");
        await _stream.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns the next outbound MsgSeqNum and increments the counter.</summary>
    public ulong NextOutboundSeqNum() =>
        (ulong)System.Threading.Interlocked.Increment(ref _outboundSeqNum);

    /// <summary>Sends a single FIXP <c>Sequence</c> frame announcing <paramref name="nextSeqNo"/>.</summary>
    public async Task SendSequenceAsync(ulong nextSeqNo, CancellationToken ct)
    {
        var totalSize = SofhSize + SbeHeaderSize + SequenceData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        SequenceData.WriteHeader(buffer.AsSpan(SofhSize));
        var payload = new SequenceData
        {
            NextSeqNo = new SeqNum(checked((uint)nextSeqNo)),
        };
        if (!payload.TryEncode(buffer.AsSpan(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode Sequence payload.");

        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sends a §4.7 RetransmitRequest covering [fromSeqNo, fromSeqNo+count).</summary>
    public async Task SendRetransmitRequestAsync(ulong fromSeqNo, uint count, CancellationToken ct)
    {
        var totalSize = SofhSize + SbeHeaderSize + RetransmitRequestData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        RetransmitRequestData.WriteHeader(buffer.AsSpan(SofhSize));
        var payload = new RetransmitRequestData
        {
            SessionID = _options.SessionId,
            Timestamp = new UTCTimestampNanos { Time = (ulong)NowUnixNanos() },
            FromSeqNo = new SeqNum(checked((uint)fromSeqNo)),
            Count = new MessageCounter(count),
        };
        if (!payload.TryEncode(buffer.AsSpan(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode RetransmitRequest payload.");
        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Optional hook: invoked by the inbound loop when a peer Sequence frame arrives.</summary>
    internal Action<ulong>? OnInboundSequence { get; set; }

    /// <summary>Optional hook: invoked when a Retransmission frame arrives. Args: (nextSeqNo, count, requestTimestampNanos).</summary>
    internal Action<ulong, uint, ulong>? OnInboundRetransmission { get; set; }

    /// <summary>Optional hook: invoked when a RetransmitReject frame arrives. Arg: rejectCode (uint).</summary>
    internal Action<uint, ulong>? OnInboundRetransmitReject { get; set; }

    /// <summary>Optional hook: invoked when a NotApplied frame arrives. Args: (fromSeqNo, count).</summary>
    internal Action<ulong, uint>? OnInboundNotApplied { get; set; }

    public async ValueTask DisposeAsync()
    {
        try { _inboundCts?.Cancel(); } catch { /* ignore */ }
        if (_inboundLoop is not null)
        {
            try { await _inboundLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _inboundCts?.Dispose();
        try { await TerminateAsync(SbeTerminationCode.FINISHED, CancellationToken.None).ConfigureAwait(false); }
        catch { /* best-effort */ }
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    // -- Wire encoding -------------------------------------------------------

    private async Task SendNegotiateAsync(CancellationToken ct)
    {
        var creds = _options.Credentials.AsSpan();
        var ip = Encoding.UTF8.GetBytes(_options.ClientIP ?? Dns.GetHostName());
        var name = Encoding.UTF8.GetBytes(_options.ClientAppName);
        var ver = Encoding.UTF8.GetBytes(_options.ClientAppVersion);

        var payloadSize = NegotiateData.MESSAGE_SIZE
            + 1 + creds.Length + 1 + ip.Length + 1 + name.Length + 1 + ver.Length;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        var buffer = new byte[totalSize];

        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        NegotiateData.WriteHeader(buffer.AsSpan(SofhSize));

        var payload = new NegotiateData
        {
            SessionID = _options.SessionId,
            SessionVerID = new SessionVerID((ulong)_options.SessionVerId),
            Timestamp = new UTCTimestampNanos { Time = (ulong)NowUnixNanos() },
            EnteringFirm = new Firm(_options.EnteringFirm),
        };

        if (!NegotiateData.TryEncode(payload, buffer.AsSpan(SofhSize + SbeHeaderSize),
                creds, ip, name, ver, out _))
        {
            throw new InvalidOperationException("Failed to encode Negotiate payload.");
        }

        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task SendEstablishAsync(CancellationToken ct)
    {
        var creds = _options.Credentials.AsSpan();

        var payloadSize = EstablishData.MESSAGE_SIZE + 1 + creds.Length;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        var buffer = new byte[totalSize];

        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        EstablishData.WriteHeader(buffer.AsSpan(SofhSize));

        var payload = new EstablishData
        {
            SessionID = _options.SessionId,
            SessionVerID = new SessionVerID((ulong)_options.SessionVerId),
            Timestamp = new UTCTimestampNanos { Time = (ulong)NowUnixNanos() },
            KeepAliveInterval = new DeltaInMillis { Time = _options.KeepAliveIntervalMs },
            NextSeqNo = new SeqNum(1),
        };

        if (!EstablishData.TryEncode(payload, buffer.AsSpan(SofhSize + SbeHeaderSize), creds, out _))
            throw new InvalidOperationException("Failed to encode Establish payload.");

        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task SendTerminateAsync(SbeTerminationCode code, CancellationToken ct)
    {
        var totalSize = SofhSize + SbeHeaderSize + TerminateData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];

        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        TerminateData.WriteHeader(buffer.AsSpan(SofhSize));

        var payload = new TerminateData
        {
            SessionID = _options.SessionId,
            SessionVerID = new SessionVerID((ulong)_options.SessionVerId),
            TerminationCode = code,
        };

        if (!payload.TryEncode(buffer.AsSpan(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode Terminate payload.");

        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static ushort ReadTemplateId(byte[] frame)
    {
        if (frame.Length < SofhSize + SbeHeaderSize)
            throw new InvalidDataException("Frame too small to contain SBE message header.");
        // MessageHeader layout: blockLength(uint16) + templateId(uint16) + ...
        return BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(SofhSize + 2, 2));
    }

    private static long NowUnixNanos() =>
        (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100L;
}

public sealed class FixpRejectedException(string message) : Exception(message);
