# B3EntryPointClient

Wire-puro **client** library for the B3 EntryPoint (SBE/FIXP 8.4.2) order-entry
protocol. Symmetric counterpart of [`B3MarketDataPlatform`][md] (UMDF consumer):
this library **produces** orders and **consumes** ExecutionReports. The
conformance suite drives any peer that speaks the protocol — locally a
[`B3MatchingPlatform`][mp] simulator, or the real B3 UAT environment with a
config swap.

[md]: https://github.com/pedrosakuma/B3MarketDataPlatform
[mp]: https://github.com/pedrosakuma/B3MatchingPlatform

## Where this fits

| Repo | Role | Wire IN | Wire OUT |
| --- | --- | --- | --- |
| [`B3MatchingPlatform`][mp] | Exchange (matching engine + UMDF publisher) | EntryPoint orders | UMDF MD + EntryPoint ER |
| [`B3MarketDataPlatform`][md] | Market-data subscriber | UMDF | — |
| [`B3TradingPlatform`][tp] | Participant / OMS-like backend | EntryPoint ER | EntryPoint orders |
| **`B3EntryPointClient`** *(this repo)* | Wire-puro client lib + conformance suite | EntryPoint ER | EntryPoint orders |

[tp]: https://github.com/pedrosakuma/B3TradingPlatform

`B3TradingPlatform` will consume this library. The whole point of staying
wire-puro is so the same code can drive the simulator or B3 UAT.

## What's inside

- **`B3.EntryPoint.Sbe`** — SBE bindings generated from
  `schemas/b3-entrypoint-messages-8.4.2.xml` via `SbeSourceGenerator`. The schema
  is a vendored byte-identical copy of the one in `B3MatchingPlatform`.
- **`B3.EntryPoint.Client`** — the public client library:
  `Framing/` (SOFH), `Fixp/` (FIXP client state machine + session), `Auth/`,
  `Models/`, `EntryPointClient.cs` (high-level API).
- **`B3.EntryPoint.Cli`** — thin CLI wrapper for manual smoke testing
  (`connect` only in the bootstrap).

## Build & test

```bash
dotnet build SbeB3EntryPointClient.slnx
dotnet test  SbeB3EntryPointClient.slnx
```

## Minimum-viable usage

```csharp
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;

await using var client = new EntryPointClient(new EntryPointClientOptions
{
    Endpoint     = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9876),
    SessionId    = 1,
    SessionVerId = 1,
    EnteringFirm = 1234,
    Credentials  = Credentials.FromUtf8("dev-shared-secret"),
});

await client.ConnectAsync(); // TCP + Negotiate + Establish
// client.State == FixpClientState.Established
```

The high-level `SubmitOrderAsync` and unsolicited `Events` stream are wired
in subsequent PRs (see [`docs/CONFORMANCE.md`](docs/CONFORMANCE.md) for the
spec-driven scenario inventory). The bootstrap intentionally ships their
**shape only** so `B3TradingPlatform` can compile against the API.

## Conformance suite

`tests/B3.EntryPoint.Conformance/` is organised one folder per spec section
(`Spec_4_5_Negotiate/`, `Spec_4_6_Sequencing/`, …). Tests using
`[ConformanceFact]` are auto-skipped when the peer env vars
(`B3EP_PEER`, `B3EP_SESSION_ID`, `B3EP_SESSION_VER_ID`, `B3EP_FIRM`,
`B3EP_ACCESS_KEY`) are not set, so CI is green without a peer configured.

To run against a local `B3MatchingPlatform`:

```bash
export B3EP_PEER=127.0.0.1:9876
export B3EP_SESSION_ID=1
export B3EP_SESSION_VER_ID=1
export B3EP_FIRM=1234
export B3EP_ACCESS_KEY=dev-shared-secret
dotnet test tests/B3.EntryPoint.Conformance --filter "Category=Conformance"
```

## Schemas

`schemas/b3-entrypoint-messages-8.4.2.xml` is the official B3 SBE schema, kept
byte-identical with the copy in `B3MatchingPlatform`. Do not hand-edit;
regenerate the bindings when upgrading and mirror the change there.

## License

MIT — see [`LICENSE`](LICENSE).
