# Public API surface (v0.14.0)

This document is an audit of `B3.EntryPoint.Client`'s public API
(post-v0.13.0 promotion of `PublicAPI.Shipped.txt`, ~1218 entries).
Every member is classified as **Supported**, **Experimental** or
**Obsolete**. This is the deliverable for issue #130.

> **Annotation policy.** Removing a `Shipped` member is a hard breaking
> change reserved for v1.0. Until then the audit only **annotates**
> (no member is deleted from `PublicAPI.Shipped.txt` in this PR).
> Experimental members are decorated with
> `[System.Diagnostics.CodeAnalysis.Experimental("B3EP_<area>")]` so a
> consumer who calls them gets a build warning naming the area; that
> warning can be suppressed per call site with the standard
> `[SuppressMessage]` / `#pragma` mechanism. Obsolete members would be
> decorated with `[Obsolete]` — none in this audit.

---

## Supported surface

The following groups are wired end-to-end, exercised by behavioural
tests, and have XML documentation describing their contract and error
modes. They are safe to take a hard dependency on at minor-version
granularity (semantic-versioned breaks at major-version bumps only).

### Order entry — `B3.EntryPoint.Client.EntryPointClient`

Implements `ISubmitOrder`, `IReplaceOrder`, `ICancelOrder` and the
session lifecycle exposed by `IEntryPointClient`.

| Member | Notes |
| --- | --- |
| `ConnectAsync(CancellationToken)` | TCP + Negotiate + Establish; covered by conformance §4.2/§4.3. |
| `DisposeAsync()` | Best-effort `Terminate` + drain (#121, #124). |
| `ReconnectAsync(uint nextSessionVerId, CancellationToken)` | Strict-monotonic SessionVerId; teardown ordering documented. Covered by `ReconnectTeardownTests` and conformance §4.7 retransmit. |
| `SubmitAsync` / `SubmitSimpleAsync` | NewOrder + SimpleNewOrder encoders + risk gate + delta persist (#128 typed key). |
| `ReplaceAsync` / `ReplaceSimpleAsync` | OrderCancelReplace + SimpleModify. |
| `CancelAsync` | OrderCancelRequest. |
| `MassActionAsync` | OrderMassAction. |
| `FlushAsync(CancellationToken)` | #123 batch-boundary flush. |
| `Events()` | Bounded inbound event channel (#126). |
| `GetHealth()` | Liveness/inbound-staleness probe. |

### Drop copy — `B3.EntryPoint.Client.DropCopy.DropCopyClient`

Read-only `Profile = SessionProfile.DropCopy` session. Same lifecycle
as `EntryPointClient`; no order entry methods exposed.

### Session control — `B3.EntryPoint.Client.Fixp.IKeepAliveScheduler` and `IRetransmitRequestHandler`

Exposed as nullable properties on `EntryPointClient` (`KeepAlive`,
`Retransmit`). Bound after `ConnectAsync`. Both are part of the
spec-mandated FIXP plumbing (§4.6, §4.7) and have unit + conformance
coverage. Concrete implementations `KeepAliveScheduler` and
`RetransmitRequestHandler` ship as `public sealed` for advanced
hosting scenarios but the typical consumer should code against the
interfaces.

### State persistence — `B3.EntryPoint.Client.State`

`ISessionStateStore`, `FileSessionStateStore`, `SessionSnapshot`,
`OutboundDelta`, `InboundDelta`, `OrderClosedDelta`. See
`docs/CONFORMANCE.md` §4.7 for the warm-restart contract. After #128
`OrderClosedDelta` carries a strongly-typed
`B3.EntryPoint.Client.Models.ClOrdID` and is serialized as a JSON
number (ulong); the `ClOrdIDJsonConverter` reads both number and
string forms for backward compatibility with v0.13.0 deltas.

### Models — `B3.EntryPoint.Client.Models`

Request/event records (`NewOrderRequest`, `OrderTrade`, `OrderCancelled`,
…), enums, and the `ClOrdID` value type. Stable.

### Risk — `B3.EntryPoint.Client.Risk`

`IRiskGate` and the built-in `MaxNotionalRiskGate` /
`MaxOpenOrdersRiskGate`. Stable.

### Logging / Telemetry — `B3.EntryPoint.Client.Logging`, `B3.EntryPoint.Client.Telemetry`

`LogMessages` extension (source-generated `LoggerMessage`s, EventIds
1xxx/4xxx/5xxx) and `EntryPointTelemetry` meters. Stable.

---

## Experimental surface

All members in this section are decorated with
`[Experimental("B3EP_<id>")]`. Calling them produces a build warning
(diagnostic ID = the second column) which a consumer can suppress
per-site. Within this repository the IDs are blanket-suppressed in
`Directory.Build.props` (`<NoWarn>`) so internal tests still build.

| Member | Diagnostic ID | Rationale |
| --- | --- | --- |
| `B3.EntryPoint.Client.CancelOnDisconnectType` (enum) | `B3EP_COD` | Type ships, but `EntryPointClientOptions.CancelOnDisconnect` is **not** wired into the FIXP `Negotiate` frame — the gateway-side behaviour is whatever the gateway defaults to, regardless of what the consumer sets. Track via #130 follow-up; do not depend on this for risk-critical flows. |
| `EntryPointClientOptions.CancelOnDisconnect` (property) | `B3EP_COD` | Same — see above. |
| `B3.EntryPoint.Client.IQuoteFlow` (interface + members `SendQuoteRequestAsync`, `SendQuoteAsync`, `CancelQuoteAsync`) | `B3EP_QUOTE` | Wire encoders ship and `EntryPointClient` implements the interface, but coverage stops at API-surface assertions (`CrossQuoteApiSurfaceTests`). No end-to-end conformance test exercises `Quote*` round-trips with a peer. Treat as preview. |
| `B3.EntryPoint.Client.ISubmitCross` (interface + member `SubmitCrossAsync`) | `B3EP_CROSS` | Same shape: encoder wired, no behavioural test. |

The attribute applied to the interface flows through to the
`EntryPointClient.SubmitCrossAsync` / quote-flow methods at use sites
(invoking `client.SendQuoteAsync(...)` triggers the warning even if
the call uses `EntryPointClient` directly, because the method
implements an experimental interface member).

### Why not just remove these?

Removing them is a hard breaking change reserved for v1.0. Annotating
gives downstream consumers a build-time signal **today** and an
ergonomic opt-in (`#pragma warning disable B3EP_QUOTE`) without
breaking compilation. When v1.0 ships, items still classified as
Experimental can be promoted (drop the attribute) or removed (delete
from `PublicAPI.Shipped.txt`).

---

## Obsolete surface

None at v0.14.0. Future `[Obsolete]` decorations should land here with
a "use X instead" pointer and a deprecation horizon.

---

## How to consume this audit

* **Producer (this repo)**: when adding a new public member, place it
  in one of the three buckets *before* promoting `Unshipped → Shipped`
  at release time. If it lacks a behavioural test or has a known wiring
  gap, ship it `[Experimental]` instead of un-annotated.
* **Consumer**: pin to a specific `B3.EntryPoint.Client` minor version
  if you call any Experimental member; suppress the diagnostic
  per-call-site with
  `[SuppressMessage("Usage", "B3EP_<id>")]` or
  `#pragma warning disable B3EP_<id>` so unrelated experimental
  additions in later versions still surface for review.
