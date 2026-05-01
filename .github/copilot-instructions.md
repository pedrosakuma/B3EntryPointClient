# Copilot instructions for B3EntryPointClient

Wire-puro **client** library for the B3 EntryPoint (SBE/FIXP 8.4.2) order-entry
protocol. Symmetric counterpart of `B3MarketDataPlatform` (UMDF consumer) — this
side **produces** orders and **consumes** ExecutionReports. Sister of the
`B3MatchingPlatform` simulator, against which the conformance suite runs.

## The non-negotiable architectural rule

**This repo depends on the schema and nothing else above the schema.** No
references to internal types from `B3MatchingPlatform` (or any other repo). The
only contract is the wire spec. If something in this lib starts smelling like
"this only makes sense against `B3MatchingPlatform`", it's a bug — that logic
belongs upstream. The whole point of being wire-puro is so the same client can
drive the simulator or B3 UAT with only an endpoint/credentials swap.

## Toolchain

- .NET SDK pinned in `global.json` (`10.0.201`, `rollForward: latestFeature`).
  Target framework is `net10.0` (set in `Directory.Build.props`).
- `Nullable`, `ImplicitUsings`, and **`TreatWarningsAsErrors=true`** are on
  globally — a new warning fails the build.
- xUnit (`xunit` v2) is the test framework. The `Xunit` namespace is added via
  `<Using Include="Xunit" />` in each test csproj.

## Build / test / run

```bash
dotnet build SbeB3EntryPointClient.slnx
dotnet test  SbeB3EntryPointClient.slnx
dotnet run --project src/B3.EntryPoint.Cli -- connect \
  --endpoint 127.0.0.1:9876 --session-id 1 --session-ver-id 1 \
  --firm 1234 --access-key dev-shared-secret
```

### Pre-PR checklist (mirrors required CI checks)

```bash
dotnet restore SbeB3EntryPointClient.slnx
dotnet build   SbeB3EntryPointClient.slnx --no-restore -c Release
dotnet test    SbeB3EntryPointClient.slnx --no-build   -c Release
dotnet format  SbeB3EntryPointClient.slnx --verify-no-changes --no-restore --severity warn
```

## Architecture

```
EntryPointClient (high-level API)
        │
        ▼
FixpClientSession ──── owns Stream, encodes/decodes Negotiate/Establish/Terminate
        │              over SOFH-framed SBE
        ▼
FixpClientStateMachine (pure logic, no I/O)
        │
        ▼
SofhFrameReader / SofhFrameWriter (Simple Open Framing Header, schema §4.4)
        │
        ▼
B3.EntryPoint.Sbe (generated bindings via SbeSourceGenerator)
        │
        ▼
schemas/b3-entrypoint-messages-8.4.2.xml (vendored, byte-identical to the copy
                                          in B3MatchingPlatform)
```

Projects under `src/`:

1. `B3.EntryPoint.Sbe` — generated SBE bindings (do not hand-edit, regenerate
   from the vendored schema and mirror version changes in `B3MatchingPlatform`).
2. `B3.EntryPoint.Client` — the public library. `Framing/` (SOFH), `Fixp/` (state
   machine + session), `Auth/Credentials.cs`, `Models/` (user-facing DTOs),
   `EntryPointClient.cs` (high-level API).
3. `B3.EntryPoint.Cli` — thin command-line wrapper for manual smoke tests.

Tests under `tests/`:

- `B3.EntryPoint.Client.Tests` — unit tests (no socket).
- `B3.EntryPoint.Conformance` — scenario suite that drives a real peer
  (`B3MatchingPlatform` locally, or B3 UAT). Scenarios are organised by spec
  section (`Spec_4_5_Negotiate/`, `Spec_4_6_Sequencing/`, …). Tests using
  `[ConformanceFact]` are auto-skipped when peer env vars
  (`B3EP_PEER` / `B3EP_SESSION_ID` / `B3EP_SESSION_VER_ID` / `B3EP_FIRM` /
  `B3EP_ACCESS_KEY`) are not set, so CI is green without a peer.

## Key conventions

- **Wire framing.** Every message on the wire is `SOFH (4 bytes) + SBE
  MessageHeader (8 bytes) + payload`. SOFH is the schema-defined `FramingHeader`
  composite (`uint16 messageLength` + `uint16 encodingType`, both LE), where
  `messageLength` includes the SOFH itself. Encoding type for SBE 1.0 LE is
  `0xEB50` (FIX SOFH §3).
- **Bootstrap scope.** `EntryPointClient.ConnectAsync` performs TCP + Negotiate
  + Establish only. `SubmitOrderAsync` and the unsolicited `Events` stream are
  intentionally `NotImplementedException`/empty in the bootstrap PR — they
  define the API shape so consumers (`B3TradingPlatform`) can compile against
  it. Real implementations land per spec section in follow-up issues.
- **Pure state machine.** `FixpClientStateMachine` is I/O-free pure logic.
  All wire decisions are: read a frame → check templateId → fire a trigger →
  observe new state → decide what to send next. Test the state machine in
  isolation; test framing in isolation; integrate in `FixpClientSession`.
- **Credentials are opaque.** The B3 spec does not constrain the Negotiate /
  Establish `Credentials` varData. Treat it as bytes (`Credentials.FromUtf8`
  is a convenience for the simulator's shared-secret style).
- **Schemas are vendored.** Do not hand-edit `schemas/`. Keep
  `b3-entrypoint-messages-8.4.2.xml` byte-identical with the copy in
  `B3MatchingPlatform` (the SHA must match).
