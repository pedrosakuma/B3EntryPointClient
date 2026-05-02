# Test peer

The `B3.EntryPoint.Client.TestPeer` NuGet package ships a self-contained,
in-process FIXP peer that you can point `EntryPointClient` at from your
integration tests. It completes the full Negotiate → Establish → NewOrder →
ExecutionReport handshake without a real B3 endpoint.

The peer is the same one used by this repo's own conformance and unit tests.

## Install

```xml
<PackageReference Include="B3.EntryPoint.Client.TestPeer" Version="0.8.*" />
```

The package transitively brings `B3.EntryPoint.Client` and
`B3.EntryPoint.Sbe`, so you do **not** need to add them again.

## Minimum-viable test

```csharp
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

await using var peer = new InProcessFixpTestPeer();
peer.Start();

await using var client = new EntryPointClient(new EntryPointClientOptions
{
    Endpoint = peer.LocalEndpoint,
    SessionId = 1, SessionVerId = 1, EnteringFirm = 1234,
    Credentials = Credentials.FromUtf8("any-key"),
});

await client.ConnectAsync();
await client.SubmitAsync(new NewOrderRequest
{
    ClOrdID = (ClOrdID)42UL, SecurityId = 1001,
    Side = Side.Buy, OrderType = OrderType.Limit,
    Price = 12.34m, OrderQty = 100,
});
```

## Configuration: `TestPeerOptions`

| Property | Purpose |
|---|---|
| `ServerCertificate` | Enables TLS by setting an `X509Certificate2`. The peer will speak TLS on the loopback socket. |
| `ResponseLatency` | A `TimeSpan` injected before every outbound write. Useful to test client-side timeouts and back-pressure. |
| `Scenario` | An `ITestPeerScenario` controlling how the peer responds to inbound `NewOrderSingle`. |
| `Credentials` | An `IReadOnlyDictionary<uint, byte[]>` of accepted firm → key. When non-empty, Negotiate from any other firm is rejected. |

## Built-in scenarios

```csharp
new TestPeerOptions { Scenario = TestPeerScenarios.AcceptAll }       // ER_New for every NewOrder (default)
new TestPeerOptions { Scenario = TestPeerScenarios.FillImmediately } // ER_New now; ER_Trade is roadmap (#NNN)
new TestPeerOptions { Scenario = TestPeerScenarios.RejectAll("bad price") }
```

## Custom scenarios

`ITestPeerScenario` is intentionally tiny:

```csharp
public sealed class HalveQty : ITestPeerScenario
{
    public NewOrderResponse OnNewOrder(NewOrderContext ctx)
        => new NewOrderResponse.AcceptAsNew();
}
```

`NewOrderResponse` is a discriminated union (`AcceptAsNew`, `AcceptAndFill`,
`RejectBusiness`). See the in-source `<doc>` comments for the exact wire
shape each one currently produces and which are still placeholders.

## Observing inbound traffic

```csharp
peer.MessageReceived += (_, e) =>
    Console.WriteLine($"peer got templateId={e.TemplateId} bytes={e.Payload.Length}");
```

The handler runs on the peer's connection task — keep it cheap, don't block.

## Use from a generic host

For integration tests that already wire everything through DI, the
`B3.EntryPoint.Client.TestPeer.DependencyInjection` namespace ships two
extension methods:

```csharp
using B3.EntryPoint.Client.TestPeer.DependencyInjection;

// Bare registration: caller controls Start()/StopAsync.
services.AddInProcessFixpTestPeer(o => o.Scenario = TestPeerScenarios.AcceptAll);

// Hosted: an IHostedService starts the peer on host.StartAsync()
// and stops it on host.StopAsync().
services.AddInProcessFixpTestPeerHosted(o => o.Scenario = TestPeerScenarios.AcceptAll);
```

The hosted variant is the recommended shape for fixtures that resolve
the EntryPoint client against the peer's bound endpoint. Wiring is
two-phase — the listener only binds inside `host.StartAsync()`, so the
client must be configured *after* the host has started:

```csharp
var peerHost = Host.CreateApplicationBuilder();
peerHost.Services.AddInProcessFixpTestPeerHosted(o =>
    o.Scenario = TestPeerScenarios.AcceptAll);

using var host = peerHost.Build();
await host.StartAsync(ct);

var peer = host.Services.GetRequiredService<InProcessFixpTestPeer>();
// peer.LocalEndpoint is non-null here.

var clientServices = new ServiceCollection();
clientServices.AddEntryPointClient(o =>
{
    o.Endpoint = peer.LocalEndpoint;
    o.SessionId = 1u;
    o.SessionVerId = 1u;
    o.EnteringFirm = 1234u;
    o.Credentials = Credentials.FromUtf8("demo-key");
});
await using var clientProvider = clientServices.BuildServiceProvider();
var client = clientProvider.GetRequiredService<IEntryPointClient>();

await client.ConnectAsync(ct);
// ... exercise the client ...
await host.StopAsync(ct); // hosted service calls peer.StopAsync(ct).
```

A working sample lives in `tests/Samples/B3.EntryPoint.Client.TestPeer.Sample/HostedSample.cs`.

## Sequence-fault simulation

Application frames sent by the peer (ExecutionReports, BusinessReject,
OrderMassActionReport, …) flow through the
`ITestPeerScenario.OnOutboundFrame` hook. Returning an
`OutboundFrameAction.Drop` skips the wire write entirely while still
advancing the peer's outbound sequence counter, producing a one-message
gap; `OutboundFrameAction.SkipSeq(n)` widens that gap by `n` more
sequence numbers; `OutboundFrameAction.DelayThen(delay, inner)` defers
the action by `delay` before re-evaluating it.

`TestPeerScenarios.WithSequenceFaults` ships a deterministic schedule
helper that wraps an inner scenario and applies a fault per 1-based
outbound app-frame ordinal:

```csharp
var schedule = new Dictionary<int, OutboundFrameAction>
{
    [3] = new OutboundFrameAction.Drop(),       // drop the 3rd ER
    [5] = new OutboundFrameAction.SkipSeq(2),   // create a 3-msg gap
};
var scenario = TestPeerScenarios.WithSequenceFaults(
    TestPeerScenarios.AcceptAll, schedule);
await using var peer = new InProcessFixpTestPeer(
    new TestPeerOptions { Scenario = scenario });
```

Use this to drive `IRetransmitRequestHandler` and `KeepAliveScheduler`
under realistic loss conditions without touching transport code.

## Limitations

- Inbound sequence-gap injection (forcing the peer to *receive* with gaps)
  is out of scope; use the §4.7 conformance suite for that path.

These will land in subsequent versions without changing the public API
shape established here.
