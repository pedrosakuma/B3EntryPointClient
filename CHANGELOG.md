# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.17.0] - 2026-07-17

### Added
- **api (#223)**: durable outbound attempt receipts for new, replace and
  cancel requests. The new `SubmitWithReceiptAsync`,
  `ReplaceWithReceiptAsync` and `CancelWithReceiptAsync` methods expose an
  awaited callback after sequence reservation and frame encoding but before
  any transport write. Consumers receive immutable session/sequence/ClOrdID
  identity plus a SHA-256 hash of the complete SOFH-framed message.
- **api (#223)**: typed `OutboundAttemptStage`,
  `OutboundAttemptReceipt` and `OutboundAttemptException` evidence
  distinguishes provably-unsent attempts, indeterminate writes, completed
  transport writes and SDK session-state persistence.

### Fixed
- **fixp (#223)**: heartbeat, reconnect, terminate and dispose now coordinate
  with the complete reserve → encode → callback → write transaction, preventing
  sequence publication or session replacement while a frame is being prepared.
- **fixp (#223)**: provably-unsent reservations are reclaimed. Failures after a
  durable frame callback or during a transport write block further application
  sends until reconciliation and a fresh `SessionVerId`, avoiding silent
  outbound sequence gaps.
- **lifecycle (#223)**: `DisposeAsync` remains idempotent and idle-timeout
  teardown no longer deadlocks with reconnect.

## [0.16.0] - 2026-06-03

### Added
- **api (#191, #192)**: cold-start (process-restart) session resume plus
  suspendable shutdown, both opt-in and default-off for back-compat.
  - `EntryPointClientOptions.ConnectMode` (new `ConnectMode` enum,
    default `NegotiateThenEstablish`). Set to `EstablishReuseThenNegotiate`
    to make a process restart RESUME the negotiated FIXP session: when a
    usable persisted snapshot exists, `ConnectAsync` reconnects TCP and sends
    `Establish` reusing the persisted `SessionVerID` (no `Negotiate`) so the
    venue's order-ownership and retransmit buffer survive an OMS restart. On a
    recoverable `Establish` reject (or no usable snapshot) it falls back to a
    fresh `Negotiate`. Requires `SessionStateStore`.
  - `EntryPointClientOptions.NextSessionVerIdSelector` (`Func<uint, uint>?`):
    invoked only on the recoverable-reject fallback to pick a strictly-greater
    `SessionVerID`. Defaults to `prev => prev + 1`.
  - `EntryPointClientOptions.TerminateOnDispose` (`bool`, default `true`).
    When `false`, `DisposeAsync` closes the transport WITHOUT a
    `Terminate(Finished)`, leaving the venue session resumable (Suspended) so a
    subsequent `EstablishReuseThenNegotiate` cold start can reattach it. The
    final snapshot is still persisted.

### Fixed
- **fixp (#191)**: a within-process reconnect reattach (or cold-resume
  fallback) no longer emits the dispose-time `Terminate`, which previously
  evicted venue-side order ownership before the reattach `Establish` and
  defeated #173 reattach.
- **fixp (#191)**: the inbound loop now starts only after every `On*` callback
  (including the retransmit handler) is wired, so an early gap frame in the
  setup window can no longer latch the gap-request guard without issuing a
  `RetransmitRequest`.



### Changed
- **api (#189)**: `EntryPointClientOptions.CancelOnDisconnect` now defaults
  to `DoNotCancelOnDisconnectOrTerminate (0)` instead of
  `CancelOnDisconnectOrTerminate (3)`. The previous aggressive default,
  combined with `CodTimeoutWindowMs = 0`, made the matching engine cancel
  **all** of a session's working orders on any client TCP drop (e.g. an OMS
  restart that reconnects via session resumption). Cancel-on-disconnect is
  now strictly opt-in, matching the enum's natural zero value. Participants
  that want the venue to flatten the book on disconnect must set the property
  (and usually a non-zero `CodTimeoutWindowMs` grace window) explicitly.

## [0.15.2] - 2026-06-01

### Fixed
- **encoder (#183)**: `OrderEntryEncoder` now populates `SendingTime`
  with the current UTC timestamp (nanoseconds, `Ticks * 100`) on every
  outbound `InboundBusinessHeader` and `BidirectionalBusinessHeader`
  instead of leaving it at `default` (zero). Gateways with clock-skew
  validation previously rejected every business message because
  `sendingTime 0` exceeded their tolerance window.

## [0.15.1] - 2026-05-28

### Fixed
- **session (#187)**: `FixpClientSession` now detects peer-side TCP
  closes (FIN/RST/IO error) on the inbound loop and transitions the
  state machine to `Terminated` via a new one-shot transport-closed
  signal. `EntryPointClient` surfaces this through the existing
  `Terminated` event with `TerminationCode.Unspecified` and
  `InitiatedByClient: false`, so `State` and `EnsureEstablished()`
  reflect the dead wire after events such as a matching-platform
  restart instead of leaving consumers stuck on a stale `Established`.

## [0.15.0] - 2026-05-25

### Added
- **models (#177)**: `NewOrderRequest` and `ReplaceOrderRequest` now
  expose six wire fields that schema 8.4.2 NewOrderSingle (template 102)
  and OrderCancelReplaceRequest (template 104) define but the SDK
  previously hid: `OrdTagId` (FIX tag, optional), `MmProtectionReset`
  (mandatory Boolean), `SelfTradePreventionInstruction` (new enum,
  schema id 293), `RoutingInstruction` (new enum, schema id 457),
  `InvestorId` (new readonly record struct wrapping the InvestorID
  composite — `Prefix:ushort + Document:uint`), and `TradingSubAccount`
  (tag 35121, v5). Encoder writes all six at their schema-correct
  offsets on both messages. Unblocks downstream
  `B3TradingPlatform#459` (SubAccount + ExecInst-equivalent gap).
- **models (#178)**: `CancelOrderRequest` now exposes four wire fields
  that schema 8.4.2 OrderCancelRequest (template 105) defines but the
  SDK previously hid: `OrderId` (FIX tag 37; venue-assigned id for
  cancel-by-venue-id), `ExecRestatementReason` (new
  `CancelExecRestatementReason` enum, FIX tag 378; only valid value is
  `CancelOrderDueToOperationalError = 203`), `ExecutingTrader` (FIX
  tag 35506; 5-char fixed string), and `DeskId` (FIX tag 35510;
  varData, ASCII, threaded through the generated
  `OrderCancelRequestData.TryEncode` varData argument).

### Fixed
- **encoder (#179)**: removed a bogus
  `MemoryMarshalAsBytes(ref msg, 100, 1)[0] = (byte)request.AccountType`
  write in `EncodeNewOrderSingle` that silently corrupted the first
  byte of `ExecutingTrader` with ASCII 38/39. Schema 8.4.2
  NewOrderSingle has NO `accountType` field — that field only exists
  on `OrderCancelReplaceRequest` (tag 581, v2). Offset 100 is the
  start of `TraderOptional executingTrader` (5-byte InlineArray), so
  the write was scribbling an unrelated field. Drive-by: also fixed
  the `EncodeOrderCancel` doc-comment that wrongly said 'template id
  104' (cancel is 105).

### Changed (breaking, pre-1.0)
- **models (#179)**: removed `NewOrderRequest.AccountType` (the
  property had no wire effect after the offset-100 bug above was
  fixed, and schema 8.4.2 has no `accountType` field on
  NewOrderSingle). `AccountType` is retained on `ReplaceOrderRequest`
  where it is a legitimate wire field (tag 581).

### Excluded (out of scope, documented)
- `SecurityExchange` (tag 207) is not exposed: the SBE generator emits
  it as a 0-byte `const string Value = "BVMF"` on every message that
  carries it; there is no wire byte to set.
- `ExecInst` (FIX tag 18) and `DisplayResetPolicy` do not exist in
  schema 8.4.2 and cannot be added without a schema bump. ExecInst is
  factored into `SelfTradePreventionInstruction` + `RoutingInstruction`
  + `MmProtectionReset` + `InvestorId`; iceberg refresh policy is
  hardwired in B3's matching engine.

## [0.14.3] - 2026-05-05

### Fixed
- **client (#155)**: `InboundDecoder.DecodeCancel` now surfaces
  `OrigClOrdID` from the wire instead of hard-coding it to `null`.
  `ExecutionReport_Cancel` (template 202, schema 8.4.2) carries
  `origClOrdID` (id=41, `ClOrdIDOptional`, present since v6.3 of the
  schema); the decoder previously dropped it, so participants routing
  ER lookups by the original ClOrdID — every sane implementation,
  since the cancel-side ClOrdID is request-only and was never
  registered as an order — could not resolve cancel acks back to the
  working order. Orders stayed `Working` forever from the
  participant's perspective even after the matching engine removed
  them from the book. Decoder now reads `msg.OrigClOrdID` from the SBE
  binding (`ulong?`, `NullValue = 0`) and maps it to the model:
  present → `new ClOrdID(value)`, absent → `null` (preserves the
  legitimate-null path for unsolicited cancellations from Market
  Operations / Cancel On Disconnect, which omit the field). No public
  API surface change; `OrderCancelled.OrigClOrdID` was already typed
  `ClOrdID?`. Behaviour change is strictly additive.

### Tests
- **client (#157)**: relaxed the two `Assert.ThrowsAnyAsync<SocketException>`
  assertions in `DropCopyClientTests.ConnectAsync_AttemptsTcpConnect`
  and `TerminateApiTests.ReconnectAsync_AcceptsIncreasingVerId_AttemptsTcpConnect`
  to *any non-validation exception*. The original assertions assumed
  TCP port 9999 was unbound, which holds on CI but fails on dev
  machines with anything listening there (TCP connect succeeds →
  `EndOfStreamException` from the negotiate FIN). The documented
  intent of both tests is just "the version/profile guard passed and
  we proceeded into the connect path"; the failure type is irrelevant.
  No production code touched.

## [0.14.2] - 2026-05-05

### Fixed
- **client (#149)**: `EntryPointClient` now throws a deterministic
  `InvalidOperationException` ("outbound MsgSeqNum exhausted; reconnect with
  next SessionVerID") when the outbound counter would exceed `uint.MaxValue`,
  instead of surfacing an opaque `OverflowException` from a deeply nested
  `checked` cast inside the encoder. The four `new SeqNum(checked((uint)x))`
  call sites in `FixpClientSession` and `OrderEntryEncoder` now share a
  single guard so the failure mode is named and points the host at the right
  recovery (Reconnect with the next `SessionVerID`).
- **client (#150)**: `ConnectOnceAsync` no longer leaks partial-connect
  resources when a post-TCP step (TLS handshake, Negotiate, Establish,
  snapshot hydrate, worker startup) throws. Previously `_tcp`/`_session`/
  `_keepAlive`/`_retransmit`/`_persistWorker` stayed bound to the dead
  attempt, and the next retry overwrote those fields and orphaned the prior
  socket / background tasks. Failures now route through
  `StopActiveSessionAsync` before the exception propagates, so retry attempts
  start from a clean slate.
- **client (#152)**: `EntryPointClient` now persists the
  `SessionSnapshot` at lifecycle boundaries (immediately after Establish, and
  again on graceful teardown) instead of only after
  `StateCompactEveryDeltas` (default 1024) appends. Hosts that restarted
  before the threshold previously lost `SessionId`/`SessionVerId` because
  `snapshot.json` never existed — `LoadAsync` returned `null`, the host
  reused the configured verId, and the matching peer rejected the next
  Establish with `InvalidSessionVerId`. The new `PersistSnapshotAsync`
  helper is unconditional and best-effort (failures logged via
  `SnapshotCompactionFailed`); it covers initial Establish, the Reconnect
  path (which routes through `ConnectAsync` after the verId bump), and
  graceful shutdown / pre-Reconnect teardown. No public API surface change;
  no protocol-visible behaviour change.

### Tests
- **conformance, client (#151)**: added round-trip encoder coverage for
  `NewOrderCross` (template 106) and `Quote`/`QuoteRequest` (templates
  401/403) plus matching conformance scenarios. Audit follow-up to #147 —
  these templates had public encoder/API surface but no wire-level offset
  test would have caught a bug analogous to the OCRR one.

## [0.14.1] - 2026-05-04

### Fixed
- **client (#146)**: `EntryPointClient.ReconnectAsync` no longer leaves the
  application-facing event stream permanently closed. The shared
  `_events` channel was being completed by `FixpClientSession.RunInboundLoopAsync`
  on peer Terminate / loop exit / faulted paths, which closed it for every
  subsequent session bound to the same client. The session loop no longer
  touches the writer's lifecycle; only `EntryPointClient.DisposeAsync`
  completes it. Inbound-loop fault telemetry is preserved.
- **client (#147)**: `OrderEntryEncoder.EncodeOrderCancelReplace` no longer
  clobbers `OrigClOrdID` with `long.MinValue` (PriceNull bit pattern). Five
  hand-coded `BinaryPrimitives.Write*` calls were writing at offsets `-8` from
  the actual V6 SBE struct layout (template id 104), because they predated
  the addition of `orderID@76` to the schema and never moved. The offending
  writes now go through the generated SBE setters
  (`SetMinQty`/`SetMaxFloor`/`SetAccountType`/`SetExpireDate`); `StopPx` —
  the only remaining raw mantissa write — moved from the wrong offset 84 to
  the correct offset 92. Receivers that validated OCRR (e.g. requiring
  `OrigClOrdID`) previously rejected every Replace; they now accept.

## [0.14.0] - 2026-05-02

### Added
- **client (#138)**: new `EntryPointClient.InboundGapAtReconnect` event (with `InboundGapAtReconnectEventArgs` payload carrying `FromSeqNo`, `Count`, `PriorSessionVerId`). Raised exactly once per `ReconnectAsync` call when the prior session terminated with an outstanding inbound app-frame gap that cannot be served in-band — the peer bumps `SessionVerID` on reconnect and resets its outbound counter to 1, so the missing range from the prior session is unrecoverable via §4.7. Consumers should reconcile out-of-band (e.g. via a business-layer order-status query). New structured-log events `4010` (`InboundGapDetected`), `4011` (`InboundGapRequestFailed`), `4012` (`InboundGapAtReconnect`).

### Fixed
- **client (#138)**: `EntryPointClient` no longer silently swallows inbound app-frame gaps. The single `_lastInboundSeqNum` running-max counter is replaced by a `_lastContiguousInboundSeqNum` (last contiguous tail from seq 1) plus a `_highestInboundSeqNum` (running max). When an inbound app frame arrives with `SeqNum > contiguous + 1`, the client now auto-issues a §4.7 `RetransmitRequest(fromSeqNo = contiguous + 1, count = missing)` via the existing `RetransmitRequestHandler`. Concurrent gap requests are capped at one in-flight (cleared on `Retransmission` reply or `RetransmitReject`); subsequent in-order frames continue to flow to consumers immediately. Frames that arrive past a gap are buffered for contiguity tracking and the contiguous tail advances as missing seqs arrive (typically via Retransmission). The persisted `SessionSnapshot.LastInboundSeqNum` is now the contiguous tail rather than the running max — pre-0.14.0 snapshots may carry an inflated `LastInboundSeqNum`; on resume the gap is "lost" silently (same warning pattern as the v0.11.1 outbound seq fix). The persistence-worker `OrderClosedDelta`/`InboundDelta` pair now persists the contiguous tail at the time of enqueue, matching `BuildSnapshot` semantics.

### Changed
- **client (#128)**: `EntryPointClient` no longer allocates a `string` per
  terminal `ExecutionReport` on `OnInboundEventForPersistence`. The internal
  `_outstandingOrders` map is now keyed by the strongly-typed
  `B3.EntryPoint.Client.Models.ClOrdID` (a `readonly record struct` over
  `ulong`) and `OrderClosedDelta` carries that struct directly instead of a
  string. Quote/cross flows (whose IDs are arbitrary strings) are tracked in
  a parallel `_outstandingQuoteFlowIds` dict so they keep working unchanged.
  **Wire-format break**: `OrderClosedDelta` now serializes as a JSON number
  (the underlying `uint64`) instead of a JSON string. The new
  `ClOrdIDJsonConverter` accepts both forms when reading, so deltas written
  by &lt;= v0.13.0 still replay cleanly; new deltas written by v0.14.0
  cannot be read by &lt;= v0.13.0 (downgrades require a fresh
  `SessionStateStore` directory). Added the `CloseEventPersistenceBenchmarks`
  BDN harness under `benchmarks/B3.EntryPoint.Benchmarks/` to track the
  per-close allocation count.
- **client (#130)**: audited the entire shipped public surface and
  classified each member into Supported / Experimental / Obsolete (see
  the new `docs/PUBLIC-API.md`). `CancelOnDisconnectType`,
  `EntryPointClientOptions.CancelOnDisconnect`, `IQuoteFlow` and
  `ISubmitCross` (and their members) are now decorated with
  `[System.Diagnostics.CodeAnalysis.Experimental(...)]` because their
  underlying behaviour is either not wired (`CancelOnDisconnect`) or has
  no end-to-end conformance coverage (quote/cross flows). **Calling these
  APIs from a downstream project will produce a build warning** with one
  of the new diagnostic IDs (`B3EP_COD`, `B3EP_QUOTE`, `B3EP_CROSS`).
  Suppress per call site with
  `[SuppressMessage("Usage", "B3EP_<id>")]` or
  `#pragma warning disable B3EP_<id>` to opt in. No member is removed from
  `PublicAPI.Shipped.txt` (removal is reserved for v1.0).

## [0.13.0] - 2026-05-02

### Added
- **client (#123)**: new `EntryPointClientOptions.AutoFlushOutboundFrames` (default `true`) controls whether `FixpClientSession.SendApplicationFrameAsync` flushes the underlying transport after every outbound application frame. The default preserves prior latency-sensitive behavior; set to `false` for throughput-sensitive batching over buffered transports (e.g. `SslStream`) and pair with the new `EntryPointClient.FlushAsync(CancellationToken)` / `IEntryPointClient.FlushAsync(CancellationToken)` at batch boundaries.

### Tests
- Conformance (#125): new `ReconnectRetransmitTests.Reconnect_With_Persisted_State_Resumes_Outbound_SeqNum_After_Drop` end-to-end exercises the full send → drop (peer-side) → terminate → reconnect → resume flow against an `ISessionStateStore`-backed warm-restart. Covers the v0.11.1 `LastAssignedOutboundSeqNum` snapshot fix across a terminate boundary (next `MsgSeqNum` after reconnect is contiguous, e.g. 6) using a new in-memory `ISessionStateStore` test fixture and the `WithSequenceFaults` peer scenario from #113. The companion `RetransmitRequest`-on-gap-detect assertion is included as a commented-out pending block referencing the production gap filed as #138.

## [0.12.0] - 2026-05-02

### Changed
- CI (#129): bumped `actions/checkout` to v6, `actions/setup-dotnet` to v5, and `actions/upload-artifact` to v7 in `bench.yml` and `publish.yml` to drop the deprecated Node 20 runtime (Node 20 is removed from GitHub Actions runners on 2026-09-16). Other workflows were already on these versions.
- **client (#126)**: the inbound event channel backing `IEntryPointClient.Events()` (and `SegmentedEntryPointClient.Events()`) is now actually bounded, matching its long-standing XML doc. Capacity is configurable via the new `EntryPointClientOptions.EventChannelCapacity` (default 4096), and the channel uses `BoundedChannelFullMode.Wait` — when the buffer fills, the inbound decoder awaits a free slot rather than dropping events or growing memory unboundedly. A slow consumer therefore applies backpressure all the way to the wire reader. Previous behavior used `Channel.CreateUnbounded`, which contradicted the docs and could grow without limit on a stalled consumer.
- **client (#124)**: session teardown is now centralized in a private `StopActiveSessionAsync` shared by `ReconnectAsync` and `DisposeAsync`. The new ordering is: cancel session-scoped CTSs (idle watchdog, persistence channel writer) → await background tasks under a hard `EntryPointClientOptions.SessionTeardownTimeout` (default 5s, log event 4009 on timeout) → dispose `KeepAliveScheduler` → `await` `FixpClientSession.DisposeAsync()` (which awaits the inbound loop) → dispose `TcpClient`. `ReconnectAsync` calls this _before_ sending the next `Establish`, eliminating the previous race where the prior session's idle watchdog or persistence work could outlive the new session and leak inbound events into the new stream.

### Fixed
- **client (#121)**: persistence of terminal `ExecutionReport` deltas (`OrderClosedDelta` + `InboundDelta` + maybe-compact) is no longer dispatched as a fire-and-forget `Task.Run` in `OnInboundEventForPersistence`. A single dedicated worker per session lifetime now drains a bounded `Channel<PersistOp>` (capacity = new `EntryPointClientOptions.PersistenceQueueCapacity`, default 256, `BoundedChannelFullMode.Wait`). The producer (inbound loop) blocks when the channel is saturated — backpressure is propagated to the wire reader rather than silently growing the heap; a `Trace`-level log (event 1002) is emitted on saturation. Transient store failures are logged via `OrderClosedPersistFailed` and the worker continues. On `DisposeAsync` / `ReconnectAsync` the worker is awaited (under the same teardown timeout as #124), so in-flight persistence is deterministically drained instead of being orphaned.

## [0.11.1] - 2026-05-02

### Fixed
- **client (#120)**: `EntryPointClient.BuildSnapshot()` and the `KeepAliveScheduler` callback both called `FixpClientSession.NextOutboundSeqNum()` (a post-increment) for read-only purposes, burning one outbound `MsgSeqNum` per snapshot/heartbeat. The first compaction or any heartbeat would create a sequence gap that the peer could reject (`NotApplied`/`Terminate`). Added two non-mutating accessors — `LastAssignedOutboundSeqNum()` and `PeekNextOutboundSeqNum()` — and switched both call sites. Snapshots now report the true last-assigned seq, and the FIXP `Sequence` heartbeat now announces the actual next `MsgSeqNum` the sender will use. Pre-0.11.1 persisted snapshots may have inflated `LastOutboundSeqNum`; on resume the peer may reject with a duplicate-seq error if the gap was never recovered (replay actual `OutboundDelta` records or perform a session reset).

## [0.11.0] - 2026-05-02

### Changed
- Tests (#115): hardened the two timing-sensitive tests flagged in the v0.10.x discovery — `KeepAliveSchedulerPeriodicTests.Start_WithBoundTransport_InvokesSendCallbackPeriodically` now uses a `TaskCompletionSource` that fires on the second tick instead of polling on a wall-clock deadline, and `Spec_4_6_Sequence.SequenceHeartbeatTests.KeepAlive_Sequence_Frames_Are_Exchanged` switches the keep-alive interval from 1s → 250ms and waits via `Task.WhenAll(sentTcs, receivedTcs)` capped at 5×interval instead of an unconditional `Task.Delay(3s)`. Conformance test wall time drops from ~3s to ~320ms; both tests pass 5/5 stress runs.

### Added
- TestPeer (#114): peer-side support for negative-path conformance — `TestPeerOptions.EstablishRejectAfter` (+ `EstablishRejectCodeOverride`) makes the peer respond to the N-th and subsequent `Establish` frames with `EstablishReject` instead of `EstablishmentAck`; `TestPeerOptions.RetransmitRejectCode` makes the peer answer `RetransmitRequest` with `RetransmitReject` carrying the configured code; `InProcessFixpTestPeer.InjectNotAppliedAsync(fromSeqNo, count, ct)` writes a session-layer `NotApplied` frame to every established connection (returns the count of writes).
- Conformance (#114): six new `[ConformanceFact]`/`[TestPeerOnlyConformanceFact]` tests covering `BusinessReject` text round-trip, `ExecutionReport_Reject` for cancel and replace, reconnect rejection (`FixpRejectedException` from `EstablishReject(INVALID_SESSIONVERID)`), `RetransmitReject`, and `NotApplied`.
- TestPeer (#113): `ITestPeerScenario.OnOutboundFrame(OutboundFrameContext)` default-interface hook plus `OutboundFrameAction` discriminated union (`Send` / `Drop` / `SkipSeq` / `DelayThen`) for injecting drops, sequence gaps, and per-frame delays into the peer's outbound app-frame path. `OutboundFrameContext` carries `TemplateId`, `MsgSeqNum`, and `FrameLength`. New `TestPeerScenarios.WithSequenceFaults(inner, schedule)` helper applies a deterministic `Dictionary<int, OutboundFrameAction>` schedule (1-based outbound app-frame ordinal). `docs/TEST-PEER.md` gets a "Sequence-fault simulation" section.


## [0.10.1] - 2026-05-02

### Changed
- Documentation cleanup: removed stale `<remarks>` blocks on `ISubmitOrder`, `IReplaceOrder`, `ICancelOrder`, `IKeepAliveScheduler`, `IRetransmitRequestHandler`, `FixpClientSession`, `FixpClientState`, `FixpClientStateMachine`, `NewOrderRequest`, `EntryPointClient.TerminateAsync`, `EntryPointClient.Events`, and `EntryPointClient.RaiseTerminated` that referred to wire-up as "follow-up PR" / "API surface only" — those features have been live since v0.5.0–v0.7.0. The new prose describes current behavior. No public API change.

### Tests
- Removed dead `catch (NotImplementedException)` swallows in `TestPeerScenarioTests`, `EndToEndSample`, and the `SubmitOrderAsync`/`CancelAsync`/`ReplaceAsync` paths — `SubmitAsync`/`CancelAsync`/`ReplaceAsync` have been wired since v0.5.0 and the catches were masking potential regressions.

## [0.10.0] - 2026-05-02

### Added
- `B3.EntryPoint.Client.TestPeer.DependencyInjection` namespace (#104) with `AddInProcessFixpTestPeer(IServiceCollection, Action<TestPeerOptions>)` (singleton registration via the standard Options pattern) and `AddInProcessFixpTestPeerHosted(IServiceCollection, Action<TestPeerOptions>)` (registers an `IHostedService` that drives `peer.Start()`/`peer.StopAsync(ct)` from the generic-host lifecycle). New package references on TestPeer: `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Hosting.Abstractions` (10.0.7). `docs/TEST-PEER.md` gets a "Use from a generic host" snippet and a working sample test in `tests/Samples/B3.EntryPoint.Client.TestPeer.Sample/HostedSample.cs`.

## [0.9.0] - 2026-05-02

### Added
- TestPeer (#105): `RejectBusiness` now emits a real `BusinessMessageReject` (template 206) with `Text` and bounded `BusinessRejectReason`, decoded by the client into `BusinessReject.Text`.
- TestPeer (#105): `AcceptAndFill` now emits a real `ExecutionReport_Trade` (template 203) with full/partial-fill semantics — `FillQty`/`FillPrice` honored, `LeavesQty` derived, status set to `FILLED` or `PARTIALLY_FILLED`.
- TestPeer (#107): `ITestPeerScenario` extended with `OnCancel(CancelContext)` and `OnModify(ModifyContext)` default-interface hooks; `RejectAll` now also rejects cancels and modifies via `ExecutionReport_Reject` (template 204) with `CxlRejResponseTo` set accordingly. `RejectBusiness` accepts an optional `RejReason`; `AcceptAndFill` accepts optional `FillPrice`/`FillQty`.
- `NewOrderContext` extended with optional `OrderQty`, `Price`, `Side`, `MsgSeqNum` to enable richer scenario decisions.

### Changed
- TestPeer egress is now serialized per connection via a `SemaphoreSlim` and routed through a single `SendFrameAsync` helper; var-data sections are sized dynamically (one length-prefix byte per section) instead of a fixed pad. Removes write races and oversized buffers.
- `InboundDecoder.DecodeBmr` now decodes the `Text` var-data field via `SbeBmr.TryParse` and surfaces it on `BusinessReject.Text` (existing record property).

## [0.8.0] - 2026-05-01

### Added
- New NuGet package `B3.EntryPoint.Client.TestPeer` (#96) — publishes the in-process FIXP test peer so downstream consumers can write `Mode=Real` integration tests without a real B3 endpoint. Public surface: `InProcessFixpTestPeer` (Start/StopAsync/LocalEndpoint, `MessageReceived` event), `TestPeerOptions` (TLS `ServerCertificate`, `ResponseLatency`, `Scenario`, per-firm `Credentials` gating), `ITestPeerScenario` extensibility hook with `NewOrderContext`/`NewOrderResponse` discriminated union, and built-in `TestPeerScenarios.AcceptAll`/`FillImmediately`/`RejectAll(reason)`. End-to-end sample test in `tests/Samples/B3.EntryPoint.Client.TestPeer.Sample/`. Doc page `docs/TEST-PEER.md` linked from README.

### Changed
- The in-process FIXP peer (formerly `tests/B3.EntryPoint.TestPeer/InMemoryFixpPeer`) moved to `src/B3.EntryPoint.Client.TestPeer/InProcessFixpTestPeer` and is now a published API. The constructor takes `TestPeerOptions`; `Endpoint` is preserved as an alias for `LocalEndpoint`.

## [0.7.0] - 2026-05-01

### Added
- TLS transport support via `EntryPointClientOptions.Tls` (`TlsOptions`). Opt-in (`Tls.Enabled = false` by default to preserve back-compat with the in-process simulator and plain-TCP UAT). Configurable target host, certificate validation callback, optional client certificates and `EnabledSslProtocols` (defaults to `SslProtocols.None` so the OS negotiates TLS 1.2/1.3). The handshake is layered transparently under `FixpClientSession` and tagged on the `entrypoint.connect` activity (`net.transport = tls|tcp`).
- `InMemoryFixpPeer` now accepts an optional `X509Certificate2` to wrap accepted connections in `SslStream`, enabling end-to-end TLS integration tests.
- Structured logging across the client at all five `LogLevel`s. New `B3.EntryPoint.Client.Logging.LogMessages` source-generated helpers carry stable `EventId`s by level (1xxx Trace, 2xxx Debug, 3xxx Information, 4xxx Warning, 5xxx Error). Trace logs every inbound/outbound frame (template + length, guarded by `IsEnabled(Trace)`); Debug logs FIXP state transitions and Negotiated/Established; Information logs Connect success and TLS handshake; Warning logs connect retries, idle watchdog, risk decisions, NotApplied/BusinessReject; Error logs `ConnectAsync` retry exhaustion and unhandled inbound-loop faults. Tests assert against `EventId` (not message text) so wording stays free to evolve.

## [0.6.0] - 2026-05-01

### Added
- README badges (CI, license, .NET) and `dotnet add package` install snippets.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` on `B3.EntryPoint.Client` with the v0.5.0 surface seeded into `PublicAPI.Shipped.txt`. New public API additions must be tracked in `PublicAPI.Unshipped.txt` (analyzer error `RS0016` on additions, `RS0017` on removals).
- `IEntryPointClient` and `IDropCopyClient` interfaces aggregating the public surface of the corresponding clients. DI helpers now also register the interface forwarders, so consumers can depend on the abstractions for mocking.
- CI gate: `sourcelink test` runs on every `.snupkg` to verify the embedded GitHub source URLs resolve and match the PDB content hashes (catches broken SourceLink before release).

### Changed
- Stabilized two timing-sensitive tests (telemetry `ActivityListener` filters by operation name; keep-alive scheduler test polls with deadline instead of fixed `Task.Delay`).
- README: replaced the outdated "Roadmap" issue table with a "Status" matrix reflecting that all wire-up issues (#3–#11, #51) are merged. Removed the stale `NotImplementedException` disclaimer; updated the `ICrossQuoteFlows` doc-comments accordingly.
- `EntryPointClient` and `DropCopyClient` constructors now eagerly validate `EntryPointClientOptions` (non-null `Endpoint`/`Credentials`, non-zero `SessionId`/`EnteringFirm`) and throw `ArgumentException` instead of failing later with `NullReferenceException` inside `ConnectAsync`.

## [0.5.0] - 2026-05-01

### Added
- DI helpers `AddEntryPointClient` and `AddDropCopyClient` (`B3.EntryPoint.Client.DependencyInjection`).
- Inbound decoders for `AllocationReport` (template 602) and `PositionMaintenanceReport` (template 503).
- `InMemoryFixpPeer` now emits periodic `Sequence` frames at the negotiated keep-alive interval and replies to `RetransmitRequest` with a `Retransmission(Count=0)`.
- `dotnet pack` validation step in CI to catch packaging breakage on PRs.

### Changed
- `EntryPointClientOptions` properties relaxed from `required init` to `set` so the standard Options pattern can populate them. Existing object-initializer call sites continue to compile.

### Fixed
- `FixpClientSession` was reading `RetransmissionData` / `RetransmitRejectData` at the wrong byte offsets (assumed `SessionID` was 8 bytes; it is 4). Would throw on a real `Retransmission` frame.
- Publish workflow's API-key guard (`if: ${{ env.NUGET_API_KEY != '' }}`) was evaluated before the step's `env:` block ran, silently skipping every push. Job-level `env:` now hoists the secret correctly.

## [0.4.2]

Pre-public-release internal milestones (Hardening + wire-up). See git history for details.
