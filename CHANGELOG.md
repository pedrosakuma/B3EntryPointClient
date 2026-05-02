# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
