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

## Limitations (v0.8.0)

- `AcceptAndFill` currently emits a single `ExecutionReport_New`; the
  follow-up `ExecutionReport_Trade` is on the roadmap.
- `RejectBusiness` does not yet emit a `BusinessMessageReject` frame; the
  reason is surfaced via the scenario callback for assertion only.
- Drop / sequence-gap injection is not yet implemented; only
  `ResponseLatency` is wired in this release.

These will land in subsequent versions without changing the public API
shape established here.
