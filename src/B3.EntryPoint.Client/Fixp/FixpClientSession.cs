using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Threading.Channels;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Framing;
using B3.EntryPoint.Client.Logging;
using B3.EntryPoint.Client.Models;
using Microsoft.Extensions.Logging;
using SbeTerminationCode = B3.Entrypoint.Fixp.Sbe.V6.TerminationCode;

namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Owns one TCP-backed FIXP client session: encodes/decodes Negotiate and
/// Establish messages over SOFH-framed SBE per spec §4.4–§4.6, drives the
/// pure <see cref="FixpClientStateMachine"/>, and exposes async hooks for the
/// outer <see cref="EntryPointClient"/>.
/// </summary>
internal sealed class FixpClientSession : IAsyncDisposable
{
    private const int SofhSize = SofhFrameReader.HeaderSize;
    private const int SbeHeaderSize = MessageHeader.MESSAGE_SIZE;

    private readonly Stream _stream;
    private readonly EntryPointClientOptions _options;
    private readonly FixpClientStateMachine _machine = new();

    // #211: every outbound frame (Negotiate/Establish/Terminate/Sequence/
    // RetransmitRequest/application frames) ultimately writes to this single
    // shared _stream. Stream/SslStream implementations do not support
    // concurrent WriteAsync calls, so all writers must serialize through this
    // lock to guarantee both (a) no two callers interleave bytes on the wire,
    // and (b) frames land on the wire in the same order their callers
    // acquired the lock.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FixpClientSession(Stream stream, EntryPointClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        _stream = stream;
        _options = options;
        Fire(FixpClientTrigger.TcpConnected);
    }

    private void Fire(FixpClientTrigger trigger)
    {
        var from = _machine.State;
        _machine.Fire(trigger);
        if (_machine.State != from)
            _options.Logger.StateTransition(from, _machine.State, trigger);
    }

    public FixpClientState State => _machine.State;

    public async Task NegotiateAsync(CancellationToken ct)
    {
        if (_machine.State != FixpClientState.TcpConnected)
            throw new InvalidOperationException($"Cannot Negotiate from state {_machine.State}.");

        await SendNegotiateAsync(ct).ConfigureAwait(false);
        Fire(FixpClientTrigger.SendNegotiate);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var frame = await SofhFrameReader.ReadFrameAsync(_stream, timeout.Token).ConfigureAwait(false);
        var templateId = ReadTemplateId(frame);
        if (_options.Logger.IsEnabled(LogLevel.Trace))
            _options.Logger.InboundFrame(templateId, frame.Length);

        if (templateId == NegotiateResponseData.MESSAGE_ID)
        {
            Fire(FixpClientTrigger.NegotiateResponseReceived);
        }
        else if (templateId == NegotiateRejectData.MESSAGE_ID)
        {
            Fire(FixpClientTrigger.NegotiateRejectReceived);
            throw new FixpRejectedException("Negotiate rejected by peer.");
        }
        else
        {
            Fire(FixpClientTrigger.ProtocolError);
            throw new InvalidDataException($"Unexpected templateId {templateId} during Negotiate handshake.");
        }
    }

    public async Task<EstablishResult> EstablishAsync(CancellationToken ct)
    {
        if (_machine.State != FixpClientState.Negotiated)
            throw new InvalidOperationException($"Cannot Establish from state {_machine.State}.");

        // First Establish on a fresh Negotiate-driven session: client's app
        // outbound sequence starts at 1 per spec §4.5.
        await SendEstablishAsync(nextSeqNo: 1u, ct).ConfigureAwait(false);
        Fire(FixpClientTrigger.SendEstablish);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var frame = await SofhFrameReader.ReadFrameAsync(_stream, timeout.Token).ConfigureAwait(false);
        var templateId = ReadTemplateId(frame);
        if (_options.Logger.IsEnabled(LogLevel.Trace))
            _options.Logger.InboundFrame(templateId, frame.Length);

        if (templateId == EstablishAckData.MESSAGE_ID)
        {
            Fire(FixpClientTrigger.EstablishAckReceived);
            return ParseEstablishAck(frame);
        }
        else if (templateId == EstablishRejectData.MESSAGE_ID)
        {
            Fire(FixpClientTrigger.EstablishRejectReceived);
            var code = ParseEstablishRejectCode(frame);
            throw new FixpEstablishRejectedException(code);
        }
        else
        {
            Fire(FixpClientTrigger.ProtocolError);
            throw new InvalidDataException($"Unexpected templateId {templateId} during Establish handshake.");
        }
    }

    /// <summary>
    /// Spec-canonical reattach (#173). Sends an <c>Establish</c> reusing the
    /// current <c>SessionVerID</c> on a freshly-reconnected TCP socket
    /// (i.e. without a preceding <c>Negotiate</c>), then awaits either
    /// <c>EstablishmentAck</c> (reattach succeeded) or <c>EstablishReject</c>
    /// (peer cannot reattach — caller decides whether to fall back to a fresh
    /// Negotiate based on the returned <see cref="EstablishRejectCode"/>).
    /// Unlike <see cref="EstablishAsync"/>, this method does NOT throw on a
    /// reject: the rejection code is part of the normal return contract so
    /// callers can branch on it.
    /// </summary>
    /// <param name="nextSeqNo">
    /// Client-side next outbound application <c>MsgSeqNum</c> to advertise.
    /// On a reattach the peer uses this to compute whether the client has
    /// unconfirmed outbound frames; callers must pass
    /// <c>localLastAssignedOutbound + 1</c> (1 when no app frame has been
    /// sent yet under this SessionVerID).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<EstablishReuseResult> EstablishReuseAsync(uint nextSeqNo, CancellationToken ct)
    {
        if (_machine.State != FixpClientState.TcpConnected)
            throw new InvalidOperationException($"Cannot Establish-reuse from state {_machine.State}.");

        await SendEstablishAsync(nextSeqNo, ct).ConfigureAwait(false);
        Fire(FixpClientTrigger.SendEstablish);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var frame = await SofhFrameReader.ReadFrameAsync(_stream, timeout.Token).ConfigureAwait(false);
        var templateId = ReadTemplateId(frame);
        if (_options.Logger.IsEnabled(LogLevel.Trace))
            _options.Logger.InboundFrame(templateId, frame.Length);

        if (templateId == EstablishAckData.MESSAGE_ID)
        {
            Fire(FixpClientTrigger.EstablishAckReceived);
            return EstablishReuseResult.FromAck(ParseEstablishAck(frame));
        }
        else if (templateId == EstablishRejectData.MESSAGE_ID)
        {
            Fire(FixpClientTrigger.EstablishRejectReceived);
            return EstablishReuseResult.FromReject(ParseEstablishRejectCode(frame));
        }
        else
        {
            Fire(FixpClientTrigger.ProtocolError);
            throw new InvalidDataException($"Unexpected templateId {templateId} during Establish-reuse handshake.");
        }
    }

    private static EstablishResult ParseEstablishAck(byte[] frame)
    {
        var payload = frame.AsSpan(SofhSize + SbeHeaderSize);
        if (!EstablishAckData.TryParse(payload, out var reader))
            throw new InvalidDataException("EstablishAckData payload too small.");
        ref readonly var ack = ref reader.Data;
        return new EstablishResult(ack.NextSeqNo.Value, ack.LastIncomingSeqNo.Value);
    }

    private static EstablishRejectCode ParseEstablishRejectCode(byte[] frame)
    {
        var payload = frame.AsSpan(SofhSize + SbeHeaderSize);
        if (!EstablishRejectData.TryParse(payload, out var reader))
            return EstablishRejectCode.UNSPECIFIED;
        ref readonly var rej = ref reader.Data;
        return rej.EstablishmentRejectCode;
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
            Fire(FixpClientTrigger.SendTerminate);
    }

    private long _outboundSeqNum;
    private Task? _inboundLoop;
    private CancellationTokenSource? _inboundCts;
    private ChannelWriter<EntryPointEvent>? _eventWriter;
    // Set by DisposeAsync before cancelling the inbound loop so the loop can
    // distinguish a user-driven graceful shutdown (silent) from a peer-side
    // TCP close (must surface as a transport-loss notification). (#187)
    private int _userShutdown;
    // Latches the one-shot transport-closed notification so concurrent
    // observers (inbound loop end + an outbound write that races with the
    // peer close) cannot raise the hook twice. (#187)
    private int _transportClosedRaised;

    /// <summary>
    /// Resumes the outbound counter from a persisted snapshot. Must be called
    /// before any frame is sent (i.e. immediately after Establish completes).
    /// </summary>
    public void ResumeOutboundSeqNum(ulong nextOutboundSeqNum)
    {
        if (nextOutboundSeqNum == 0UL) return;
        // NextOutboundSeqNum returns the post-increment, so seed with (next - 1).
        System.Threading.Interlocked.Exchange(ref _outboundSeqNum, (long)(nextOutboundSeqNum - 1UL));
    }

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
                if (_options.Logger.IsEnabled(LogLevel.Trace))
                    _options.Logger.InboundFrame(InboundDecoder.ReadTemplateId(frame), frame.Length);
                if (InboundDecoder.TryDecode(frame, out var evt) && evt is not null)
                {
                    try { OnInboundEvent?.Invoke(evt); } catch { /* swallow — telemetry/persistence must not break the loop */ }
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
                            // SessionID(4) + RequestTimestamp(8) + NextSeqNo(4) + Count(4) = 20 bytes
                            if (payload.Length >= RetransmissionData.MESSAGE_SIZE)
                            {
                                var reqTs = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(4, 8));
                                var nextSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
                                var cnt = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
                                OnInboundRetransmission?.Invoke(nextSeq, cnt, reqTs);
                            }
                            break;
                        case RetransmitRejectData.MESSAGE_ID:
                            // SessionID(4) + RequestTimestamp(8) + Code(1)
                            if (payload.Length >= RetransmitRejectData.MESSAGE_SIZE)
                            {
                                var reqTs = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(4, 8));
                                var code = (uint)payload[12];
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
                        case TerminateData.MESSAGE_ID:
                            // Advance the state machine BEFORE notifying the
                            // outer client so EnsureEstablished() (sync app
                            // sends) sees the session as no longer Established
                            // and refuses new outbound frames. Per schema §4.8
                            // a Terminate signals the sender is going to
                            // disconnect; sending app frames on the same TCP
                            // socket after that is a protocol error.
                            try { Fire(FixpClientTrigger.TerminateReceived); }
                            catch (InvalidFixpTransitionException) { /* already Terminating/Terminated */ }

                            // SessionID(4) + SessionVerID(8) + TerminationCode(1) = 13 bytes
                            if (payload.Length >= TerminateData.MESSAGE_SIZE)
                            {
                                var code = payload[12];
                                OnInboundTerminate?.Invoke(code);
                            }
                            // peer is shutting us down — exit loop. Do NOT
                            // complete the shared event channel writer: it
                            // outlives this session and is owned by the outer
                            // EntryPointClient (see #144).
                            return;
                        default:
                            // Unknown templateId on a SOFH-framed SBE message.
                            // Log so operators can spot a gateway protocol
                            // version drift; intentionally do not terminate
                            // the session (newer gateway templates may be
                            // safely ignorable by older clients). See spec
                            // TerminationCode.UNRECOGNIZED_MESSAGE for the
                            // peer-side counterpart.
                            _options.Logger.UnknownInboundTemplateId(templateId, frame.Length);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by DisposeAsync (graceful shutdown) — no transport-loss
            // signal needed. If the cancellation happened concurrently with a
            // peer-side close, the user-shutdown flag distinguishes the two:
            // a peer close that arrived before our cancel still surfaces below.
            if (System.Threading.Volatile.Read(ref _userShutdown) != 0)
                return;
            RaiseTransportClosed(reason: null);
            return;
        }
        catch (EndOfStreamException)
        {
            // Peer half-closed the TCP connection (FIN). Surface as a
            // transport-loss signal so the outer client transitions out of
            // Established and consumers stop issuing app frames. (#187)
            RaiseTransportClosed(reason: null);
            return;
        }
        catch (IOException ex)
        {
            // Peer closed / RST / socket error. Surface as a transport-loss
            // signal so callers see an accurate session state instead of a
            // stale Established. (#187)
            RaiseTransportClosed(ex);
            return;
        }
        catch (Exception ex)
        {
            // Surface telemetry but do NOT poison the shared event channel:
            // the writer is owned by the outer EntryPointClient and must
            // remain usable across session swaps (e.g. ReconnectAsync). #144
            _options.Logger.InboundLoopFaulted(ex);
            RaiseTransportClosed(ex);
            return;
        }
        // Loop exiting normally (cancellation, peer closed). The shared event
        // channel writer is intentionally NOT completed here — only
        // EntryPointClient.DisposeAsync may complete it. #144
        if (System.Threading.Volatile.Read(ref _userShutdown) == 0)
            RaiseTransportClosed(reason: null);
    }

    /// <summary>
    /// Latched, idempotent notification that the underlying TCP transport
    /// is gone (peer-initiated close, RST, or IO error). Drives the state
    /// machine to <see cref="FixpClientState.Terminated"/> when still
    /// transition-eligible and invokes <see cref="OnTransportClosed"/> so
    /// the outer <see cref="EntryPointClient"/> can raise a
    /// <c>Terminated</c> event and refuse further app sends. (#187)
    /// </summary>
    private void RaiseTransportClosed(Exception? reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _transportClosedRaised, 1) != 0)
            return;
        try
        {
            if (_machine.CanFire(FixpClientTrigger.TransportClosed))
                Fire(FixpClientTrigger.TransportClosed);
        }
        catch (InvalidFixpTransitionException) { /* already at a terminal state */ }
        try { OnTransportClosed?.Invoke(reason); }
        catch { /* swallow — telemetry must not break shutdown */ }
    }

    /// <summary>
    /// Test-only hook that drives the internal state machine from
    /// <see cref="FixpClientState.TcpConnected"/> straight to
    /// <see cref="FixpClientState.Established"/> without performing a real
    /// handshake. Used by unit tests that exercise outbound-frame plumbing
    /// (e.g. AutoFlush behavior, #123) over an in-memory transport.
    /// </summary>
    internal void ForceEstablishedForTesting()
    {
        Fire(FixpClientTrigger.SendNegotiate);
        Fire(FixpClientTrigger.NegotiateResponseReceived);
        Fire(FixpClientTrigger.SendEstablish);
        Fire(FixpClientTrigger.EstablishAckReceived);
    }

    /// <summary>Sends an Order Entry application frame previously encoded via <see cref="OrderEntryEncoder"/>.</summary>
    public async Task SendApplicationFrameAsync(byte[] buffer, int length, CancellationToken ct)
    {
        if (_machine.State != FixpClientState.Established)
            throw new InvalidOperationException($"Cannot send application frame from state {_machine.State}.");
        if (_options.Logger.IsEnabled(LogLevel.Trace) && length >= SofhSize + SbeHeaderSize)
            _options.Logger.OutboundFrame(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(SofhSize, 2)), length);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            if (_options.AutoFlushOutboundFrames)
                await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task SendApplicationFrameWithEvidenceAsync(byte[] buffer, int length, CancellationToken ct)
    {
        if (_machine.State != FixpClientState.Established)
            throw new InvalidOperationException($"Cannot send application frame from state {_machine.State}.");
        if (_options.Logger.IsEnabled(LogLevel.Trace) && length >= SofhSize + SbeHeaderSize)
            _options.Logger.OutboundFrame(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(SofhSize, 2)), length);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var writeCompleted = false;
            try
            {
                await _stream.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                writeCompleted = true;
                if (_options.AutoFlushOutboundFrames)
                    await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ApplicationFrameWriteException(writeCompleted, ex);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Flushes any bytes buffered by the underlying transport. Thin wrapper
    /// over <see cref="Stream.FlushAsync(CancellationToken)"/>; intended to be
    /// called at batch boundaries when
    /// <see cref="EntryPointClientOptions.AutoFlushOutboundFrames"/> is
    /// <see langword="false"/>. Issue #123.
    /// </summary>
    public async Task FlushOutboundAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Returns the next outbound MsgSeqNum and increments the counter.</summary>
    /// <remarks>
    /// Throws <see cref="InvalidOperationException"/> when the counter would exceed
    /// <see cref="SeqNumGuard.MaxWireSeqNum"/> — FIXP <c>MsgSeqNum</c> is scoped to
    /// a single <c>SessionID</c>/<c>SessionVerID</c> pair and must not wrap. The
    /// counter is rolled back so the failed allocation does not consume a slot.
    /// Logs a one-shot warning when crossing
    /// <see cref="SeqNumGuard.NearExhaustionThreshold"/> so operators can rotate
    /// <c>SessionVerID</c> proactively via <c>ReconnectAsync</c>.
    /// </remarks>
    public ulong NextOutboundSeqNum()
    {
        var next = (ulong)System.Threading.Interlocked.Increment(ref _outboundSeqNum);
        if (next > SeqNumGuard.MaxWireSeqNum)
        {
            // Roll back so the counter never reports a value that cannot be encoded.
            System.Threading.Interlocked.Decrement(ref _outboundSeqNum);
            throw new InvalidOperationException(
                $"Outbound MsgSeqNum {next} exceeds the FIXP wire SeqNum (uint32) limit " +
                $"of {SeqNumGuard.MaxWireSeqNum}. Per schema v8.4.2, MsgSeqNum is scoped " +
                "to a single SessionID/SessionVerID pair and must not wrap. Rotate by " +
                "calling EntryPointClient.ReconnectAsync(nextSessionVerId, ...) with a " +
                "strictly greater SessionVerID before exhausting the counter.");
        }
        if (next >= SeqNumGuard.NearExhaustionThreshold &&
            System.Threading.Interlocked.Exchange(ref _seqNumNearExhaustionLogged, 1) == 0)
        {
            _options.Logger.OutboundSeqNumNearExhaustion(next, SeqNumGuard.MaxWireSeqNum);
        }
        return next;
    }

    private int _seqNumNearExhaustionLogged;

    /// <summary>
    /// Reads the last outbound MsgSeqNum that was assigned via <see cref="NextOutboundSeqNum"/>
    /// without mutating the counter. Returns 0 when no outbound frame has been assigned yet.
    /// </summary>
    /// <remarks>
    /// "Assigned" means the seq slot was reserved for an outbound send attempt. The frame
    /// may not have been successfully flushed to the wire if the underlying write failed.
    /// </remarks>
    public ulong LastAssignedOutboundSeqNum() =>
        (ulong)System.Threading.Interlocked.Read(ref _outboundSeqNum);

    internal bool TryRollbackUnwrittenOutboundSeqNum(ulong seqNum)
    {
        var expected = checked((long)seqNum);
        return Interlocked.CompareExchange(ref _outboundSeqNum, expected - 1L, expected) == expected;
    }

    /// <summary>
    /// Returns the value the next call to <see cref="NextOutboundSeqNum"/> would produce
    /// (i.e. the announce-only "next NextSeqNo" used by FIXP <c>Sequence</c> frames),
    /// without mutating the counter.
    /// </summary>
    /// <remarks>
    /// Note: callers that read this value and then write it to the wire on a separate
    /// path (e.g. <c>KeepAliveScheduler</c>) can race with concurrent application sends
    /// that allocate a new seq between the peek and the wire write. Tracked separately
    /// in the outbound-serialization follow-up.
    /// </remarks>
    public ulong PeekNextOutboundSeqNum() =>
        (ulong)System.Threading.Interlocked.Read(ref _outboundSeqNum) + 1UL;

    /// <summary>Sends a single FIXP <c>Sequence</c> frame announcing <paramref name="nextSeqNo"/>.</summary>
    public async Task SendSequenceAsync(ulong nextSeqNo, CancellationToken ct)
    {
        var totalSize = SofhSize + SbeHeaderSize + SequenceData.MESSAGE_SIZE;
        var buffer = new byte[totalSize];
        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        SequenceData.WriteHeader(buffer.AsSpan(SofhSize));
        var payload = new SequenceData
        {
            NextSeqNo = SeqNumGuard.ToWireSeqNum(nextSeqNo),
        };
        if (!payload.TryEncode(buffer.AsSpan(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode Sequence payload.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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
            FromSeqNo = SeqNumGuard.ToWireSeqNum(fromSeqNo),
            Count = new MessageCounter(count),
        };
        if (!payload.TryEncode(buffer.AsSpan(SofhSize + SbeHeaderSize), out _))
            throw new InvalidOperationException("Failed to encode RetransmitRequest payload.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Optional hook: invoked by the inbound loop when a peer Sequence frame arrives.</summary>
    internal Action<ulong>? OnInboundSequence { get; set; }

    /// <summary>Optional hook: invoked for every decoded application
    /// <see cref="EntryPointEvent"/> before it is published to the user channel.
    /// Used internally to drive <see cref="EntryPointClient"/> persistence.</summary>
    internal Action<Models.EntryPointEvent>? OnInboundEvent { get; set; }

    /// <summary>Optional hook: invoked when a Retransmission frame arrives. Args: (nextSeqNo, count, requestTimestampNanos).</summary>
    internal Action<ulong, uint, ulong>? OnInboundRetransmission { get; set; }

    /// <summary>Optional hook: invoked when a RetransmitReject frame arrives. Arg: rejectCode (uint).</summary>
    internal Action<uint, ulong>? OnInboundRetransmitReject { get; set; }

    /// <summary>Optional hook: invoked when a NotApplied frame arrives. Args: (fromSeqNo, count).</summary>
    internal Action<ulong, uint>? OnInboundNotApplied { get; set; }

    /// <summary>Optional hook: invoked when a Terminate frame arrives. Arg: terminationCode (byte).</summary>
    internal Action<byte>? OnInboundTerminate { get; set; }

    /// <summary>
    /// Optional hook: invoked exactly once when the underlying TCP transport
    /// is closed by the peer (FIN, RST, IO error) without a preceding
    /// <c>Terminate</c> exchange. Arg is the IO exception that triggered the
    /// detection, or <see langword="null"/> for a clean FIN / cancellation
    /// that the user did not initiate. Drives <see cref="EntryPointClient"/>
    /// to raise its <c>Terminated</c> event and refuse further app sends so
    /// callers see an accurate session state instead of a stale
    /// <see cref="FixpClientState.Established"/>. (#187)
    /// </summary>
    internal Action<Exception?>? OnTransportClosed { get; set; }

    /// <summary>
    /// When set, suppresses the dispose-time <c>Terminate(Finished)</c> for
    /// this session instance regardless of
    /// <see cref="EntryPointClientOptions.TerminateOnDispose"/>. Used by the
    /// client for INTERNAL teardowns (within-process reconnect / cold-start
    /// reattach fallback) where the transport is being closed only to
    /// immediately re-open under the same or a fresh SessionVerID — sending a
    /// Terminate(Finished) there would tell the venue the session is
    /// done-for-good and evict order ownership (#191).
    /// </summary>
    internal bool SuppressTerminateOnDispose { get; set; }

    public async ValueTask DisposeAsync()
    {
        System.Threading.Volatile.Write(ref _userShutdown, 1);
        try { _inboundCts?.Cancel(); } catch { /* ignore */ }
        if (_inboundLoop is not null)
        {
            try { await _inboundLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _inboundCts?.Dispose();
        if (_options.TerminateOnDispose && !SuppressTerminateOnDispose)
        {
            // #191: gated so a host that intends to restart and resume can
            // dispose WITHOUT telling the venue the session is done-for-good
            // (Terminate=Finished evicts venue-side order ownership). When
            // false (or suppressed for an internal reattach teardown), the
            // transport is closed cleanly below and the venue keeps the
            // session resumable (Suspended).
            try { await TerminateAsync(SbeTerminationCode.FINISHED, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
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

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendEstablishAsync(uint nextSeqNo, CancellationToken ct)
    {
        var creds = _options.Credentials.AsSpan();

        var payloadSize = EstablishData.MESSAGE_SIZE + 1 + creds.Length;
        var totalSize = SofhSize + SbeHeaderSize + payloadSize;
        var buffer = new byte[totalSize];

        SofhFrameWriter.WriteHeader(buffer, checked((ushort)totalSize));
        EstablishData.WriteHeader(buffer.AsSpan(SofhSize));

#pragma warning disable B3EP_COD // wire CancelOnDisconnect into Establish payload
        var payload = new EstablishData
        {
            SessionID = _options.SessionId,
            SessionVerID = new SessionVerID((ulong)_options.SessionVerId),
            Timestamp = new UTCTimestampNanos { Time = (ulong)NowUnixNanos() },
            KeepAliveInterval = new DeltaInMillis { Time = _options.KeepAliveIntervalMs },
            NextSeqNo = new SeqNum(nextSeqNo),
            CancelOnDisconnectType = (B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType)_options.CancelOnDisconnect,
            CodTimeoutWindow = new DeltaInMillis { Time = _options.CodTimeoutWindowMs },
        };
#pragma warning restore B3EP_COD

        if (!EstablishData.TryEncode(payload, buffer.AsSpan(SofhSize + SbeHeaderSize), creds, out _))
            throw new InvalidOperationException("Failed to encode Establish payload.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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

internal sealed class ApplicationFrameWriteException : Exception
{
    public ApplicationFrameWriteException(bool transportWriteCompleted, Exception innerException)
        : base("The application-frame transport write did not complete.", innerException)
    {
        TransportWriteCompleted = transportWriteCompleted;
    }

    public bool TransportWriteCompleted { get; }
}

public class FixpRejectedException(string message) : Exception(message);

/// <summary>
/// Thrown by <see cref="FixpClientSession.EstablishAsync(System.Threading.CancellationToken)"/>
/// when the peer responds with an <c>EstablishReject</c>. Carries the
/// <see cref="EstablishRejectCode"/> reported by the gateway so callers can
/// distinguish recoverable (UNNEGOTIATED, INVALID_SESSIONID, …) from hard
/// (CREDENTIALS, PROTOCOL_VERSION_NOT_SUPPORTED, …) reasons.
/// </summary>
public sealed class FixpEstablishRejectedException : FixpRejectedException
{
    public FixpEstablishRejectedException(EstablishRejectCode code)
        : base($"Establish rejected by peer: {code}.")
    {
        Code = code;
    }

    public EstablishRejectCode Code { get; }
}

/// <summary>Decoded outcome of a successful <c>EstablishmentAck</c> (#173).</summary>
internal readonly record struct EstablishResult(ulong NextSeqNo, ulong LastIncomingSeqNo);

/// <summary>
/// Outcome of <see cref="FixpClientSession.EstablishReuseAsync(uint, System.Threading.CancellationToken)"/>:
/// either an <see cref="Ack"/> (peer accepted reattach) or a
/// <see cref="RejectCode"/> (peer refused — caller decides on the fallback
/// strategy). Exactly one of the two is non-null.
/// </summary>
internal readonly record struct EstablishReuseResult(EstablishResult? Ack, EstablishRejectCode? RejectCode)
{
    public bool Accepted => Ack.HasValue;

    internal static EstablishReuseResult FromAck(EstablishResult ack) => new(ack, null);
    internal static EstablishReuseResult FromReject(EstablishRejectCode code) => new(null, code);
}
