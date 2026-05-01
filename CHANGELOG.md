# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- README badges (CI, license, .NET) and `dotnet add package` install snippets.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` on `B3.EntryPoint.Client` with the v0.5.0 surface seeded into `PublicAPI.Shipped.txt`. New public API additions must be tracked in `PublicAPI.Unshipped.txt` (analyzer error `RS0016` on additions, `RS0017` on removals).
- `IEntryPointClient` and `IDropCopyClient` interfaces aggregating the public surface of the corresponding clients. DI helpers now also register the interface forwarders, so consumers can depend on the abstractions for mocking.

### Changed
- Stabilized two timing-sensitive tests (telemetry `ActivityListener` filters by operation name; keep-alive scheduler test polls with deadline instead of fixed `Task.Delay`).
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
