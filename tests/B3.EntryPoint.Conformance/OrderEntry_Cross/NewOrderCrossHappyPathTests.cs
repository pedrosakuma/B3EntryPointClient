using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.OrderEntry_Cross;

/// <summary>
/// Conformance — <c>NewOrderCross</c> (template 106, schema §9). Submits a
/// two-leg cross and expects the peer to acknowledge each leg with an
/// <see cref="OrderAccepted"/> ExecutionReport. Skipped when no peer is
/// configured (the in-process <c>InProcessFixpTestPeer</c> does not model the
/// cross flow, so this only runs against <c>B3MatchingPlatform</c> / B3 UAT).
/// </summary>
[Trait("Category", "Conformance")]
public class NewOrderCrossHappyPathTests
{
    [ExternalPeerOnlyConformanceFact]
    public async Task Submit_NewOrderCross_TwoLegs_Receives_Accepted_For_Each_Leg()
    {
        var peer = PeerEndpoint.TryResolve()!;
        await using var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = peer.Endpoint,
            SessionId = peer.SessionId,
            SessionVerId = peer.SessionVerId,
            EnteringFirm = peer.EnteringFirm,
            Credentials = Credentials.FromUtf8(peer.AccessKey),
        });

        await client.ConnectAsync();

        var crossId = ((ulong)Math.Abs(Guid.NewGuid().GetHashCode()) | 1UL).ToString();
        var buyClOrd = new ClOrdID((ulong)(uint)Guid.NewGuid().GetHashCode() | 1UL);
        var sellClOrd = new ClOrdID((ulong)(uint)Guid.NewGuid().GetHashCode() | 2UL);

#pragma warning disable B3EP_CROSS
        await client.SubmitCrossAsync(new NewOrderCrossRequest
        {
            CrossId = crossId,
            SecurityId = 1,
            CrossType = CrossType.AllOrNone,
            Prioritization = CrossPrioritization.None,
            Price = 0.01m,
            Legs = new List<CrossLeg>
            {
                new() { ClOrdID = buyClOrd,  Side = Side.Buy,  OrderQty = 1, Account = 1 },
                new() { ClOrdID = sellClOrd, Side = Side.Sell, OrderQty = 1, Account = 1 },
            },
        });
#pragma warning restore B3EP_CROSS

        var seen = new HashSet<ulong>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in client.Events(cts.Token))
        {
            if (evt is OrderAccepted ack)
            {
                seen.Add(ack.ClOrdID.Value);
                if (seen.Contains(buyClOrd.Value) && seen.Contains(sellClOrd.Value))
                    return;
            }
        }
        Assert.Fail($"Did not receive OrderAccepted for both cross legs (saw: [{string.Join(',', seen)}])");
    }
}
