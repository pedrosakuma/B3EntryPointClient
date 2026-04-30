# Conformance scenario inventory

The conformance suite (`tests/B3.EntryPoint.Conformance/`) is the executable
contract for the B3 EntryPoint client. Each spec section gets one folder; each
testable requirement gets one scenario. Scenarios are wire-puro: the same code
drives a local `B3MatchingPlatform` simulator or the real B3 UAT environment —
only the peer endpoint + credentials change.

## Configuration

Peer connection is read from environment variables. Tests that need a peer use
`[ConformanceFact]` and are auto-skipped when these are absent:

| Variable | Description |
| --- | --- |
| `B3EP_PEER` | `host:port` of the B3 EntryPoint TCP listener |
| `B3EP_SESSION_ID` | Numeric SessionID assigned by the peer |
| `B3EP_SESSION_VER_ID` | Initial SessionVerID (must increase across reconnects) |
| `B3EP_FIRM` | EnteringFirm |
| `B3EP_ACCESS_KEY` | Credentials payload (UTF-8 token in the simulator) |

To run the suite against a local simulator:

```bash
export B3EP_PEER=127.0.0.1:9876
export B3EP_SESSION_ID=1
export B3EP_SESSION_VER_ID=1
export B3EP_FIRM=1234
export B3EP_ACCESS_KEY=dev-shared-secret
dotnet test tests/B3.EntryPoint.Conformance --filter "Category=Conformance"
```

## Inventory

### Bootstrap (this PR)

- **`Spec_4_5_Negotiate/HelloNegotiateTests`** — single happy-path scenario:
  TCP connect → `Negotiate → NegotiateResponse` → `Establish → EstablishmentAck`,
  reaches `FixpClientState.Established`, then clean `Terminate`. Will fail
  against a real peer until [`B3MatchingPlatform#42`][mp42] lands; this is the
  intended initial tracking signal described in
  [issue #1](../README.md#where-this-fits).

[mp42]: https://github.com/pedrosakuma/B3MatchingPlatform/issues/42

### Backlog (separate issues)

- **`Spec_4_5_Negotiate/`** — duplicate Negotiate, invalid credentials,
  sessionVerId regression, NegotiateReject codes.
- **`Spec_4_6_Sequencing/`** — gap detection, NotApplied, recovery via
  RetransmitRequest, Sequence keep-alive cadence.
- **`Spec_4_7_Retransmission/`** — replay request inside/outside the retained
  range, `PossResend` flag, Retransmission packet boundaries.
- **`Spec_4_10_Termination/`** — every TerminationCode round-trip, framing
  errors (`INVALID_SOFH`, `DECODING_ERROR`), client-initiated vs.
  server-initiated termination.
- **`Spec_5_*`** — application-message scenarios (NewOrder/Modify/Cancel +
  ExecutionReport correlation).

Add a new scenario by:

1. Picking the right `Spec_<section>/` folder (create one if needed).
2. Writing one `[ConformanceFact]` per testable requirement; one assertion per
   scenario, wire-level only.
3. Updating this inventory.
