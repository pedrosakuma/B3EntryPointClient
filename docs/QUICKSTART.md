# Quickstart

This guide gets a working `EntryPointClient` running in less than a minute,
without a real B3 EntryPoint endpoint, by routing it to the in-memory FIXP
peer that ships with this repo.

## Prerequisites

- .NET 10 SDK (see `global.json` for the exact version pin)

## Run the sample

```bash
dotnet run --project samples/B3.EntryPoint.Quickstart
```

Expected output:

```
Connected. State=Established
Submitted 42
Terminated.
```

## What the sample does

The full source is at
[`samples/B3.EntryPoint.Quickstart/Program.cs`](../samples/B3.EntryPoint.Quickstart/Program.cs)
and walks through the canonical lifecycle:

1. **Boot a local peer** — `InMemoryFixpPeer` listens on a dynamic loopback
   port and answers Negotiate / Establish / Terminate.
2. **Build options** — `EntryPointClientOptions` carries the peer endpoint,
   `SessionId`, `SessionVerId`, `EnteringFirm`, and `Credentials`.
3. **`ConnectAsync`** — runs the FIXP handshake (Negotiate → Establish) and
   starts the inbound loop.
4. **`SubmitAsync(NewOrderRequest)`** — encodes a `NewOrderSingle` SBE frame
   and pushes it across the socket. Pre-trade `RiskGates` (if registered)
   evaluate first.
5. **Drain `Events()`** — `IAsyncEnumerable<EntryPointEvent>` of decoded
   `OrderAccepted` / `OrderModified` / `OrderCancelled` / `OrderTrade` /
   `OrderRejected` / `BusinessReject`.
6. **`TerminateAsync`** — sends a `Terminate(Finished)` and tears the session
   down cleanly.

## Adding a real endpoint

Swap the peer for a real address:

```csharp
var options = new EntryPointClientOptions
{
    Endpoint = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 9_000),
    SessionId = 12345,
    SessionVerId = 1,
    EnteringFirm = 1234,
    Credentials = Credentials.FromUtf8(File.ReadAllText("/secrets/key")),
};
```

## Warm-restart (snapshot persistence)

Wire an `ISessionStateStore` (e.g. `FileSessionStateStore`) via
`EntryPointClientOptions.SessionStateStore` and the client will:

- Hydrate `LastOutboundSeqNum` / outstanding orders on `ConnectAsync`.
- Append an `OutboundDelta` after every send.
- Append `OrderClosedDelta` on terminal Execution Reports.
- Compact every `StateCompactEveryDeltas` (default 1024) updates.

```csharp
options.SessionStateStore = new FileSessionStateStore("/var/lib/myapp/state");
options.StateCompactEveryDeltas = 512;
```

## Observability

Telemetry uses pure BCL primitives (no SDK dependency):

- `ActivitySource("B3.EntryPoint.Client")` — spans for Connect/Negotiate/
  Establish/Terminate and every Submit/Replace/Cancel/MassAction.
- `Meter("B3.EntryPoint.Client")` — counters
  (`orders.submitted`, `orders.replaced`, `orders.cancelled`,
  `orders.mass_actions`, `risk.rejections`, `session.terminations`)
  and a histogram (`outbound.latency` in ms).

Hook them up with the OpenTelemetry .NET SDK in your host process — no
configuration is needed in this library.
