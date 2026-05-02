# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
