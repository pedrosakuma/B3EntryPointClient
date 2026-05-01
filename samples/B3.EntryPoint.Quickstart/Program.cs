// Quickstart: connect EntryPointClient to the in-memory FIXP peer,
// submit a NewOrderSingle, drain a couple of events, then terminate.
//
// Run:  dotnet run --project samples/B3.EntryPoint.Quickstart
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

await using var peer = new InProcessFixpTestPeer();
peer.Start();

var options = new EntryPointClientOptions
{
    Endpoint = peer.Endpoint,
    SessionId = 1,
    SessionVerId = 1,
    EnteringFirm = 1234,
    Credentials = Credentials.FromUtf8("demo-key"),
};

await using var client = new EntryPointClient(options);
await client.ConnectAsync();
Console.WriteLine($"Connected. State={client.State}");

try
{
    var clordid = await client.SubmitAsync(new NewOrderRequest
    {
        ClOrdID = (ClOrdID)42UL,
        SecurityId = 1001,
        Side = Side.Buy,
        OrderType = OrderType.Limit,
        Price = 12.34m,
        OrderQty = 100,
    });
    Console.WriteLine($"Submitted {clordid.Value}");
}
catch (NotImplementedException ex)
{
    // Some send paths still surface NotImplementedException while wire-up
    // (issues #W*) lands. The interface is stable; the swap is purely
    // internal. See README "Roadmap (next phase — wire-up)".
    Console.WriteLine($"Submit not yet wired: {ex.Message}");
}

// Drain up to ~1s of events without blocking forever.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
try
{
    await foreach (var evt in client.Events().WithCancellation(cts.Token))
        Console.WriteLine($"<- {evt.GetType().Name} seq={evt.SeqNum}");
}
catch (OperationCanceledException) { /* expected */ }

await client.TerminateAsync(TerminationCode.Finished);
Console.WriteLine("Terminated.");
