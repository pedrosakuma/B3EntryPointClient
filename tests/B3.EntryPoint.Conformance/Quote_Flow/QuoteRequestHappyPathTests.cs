using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Quote_Flow;

/// <summary>
/// Conformance — <c>QuoteRequest</c> (template 401, B3 Termo §10). Sends a
/// QuoteRequest and waits for either a <see cref="QuoteRequestRejected"/> (if
/// the peer rejects) or a <see cref="QuoteStatusUpdated"/> (if the peer
/// quotes back). Either response confirms the encoder produced a wire-valid
/// frame the peer accepted into its dispatch path.
/// </summary>
[Trait("Category", "Conformance")]
public class QuoteRequestHappyPathTests
{
    [ExternalPeerOnlyConformanceFact]
    public async Task Send_QuoteRequest_Receives_Quote_Or_Reject()
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

        var quoteReqId = ((ulong)Math.Abs(Guid.NewGuid().GetHashCode()) | 1UL).ToString();

#pragma warning disable B3EP_QUOTE
        await client.SendQuoteRequestAsync(new QuoteRequestMessage
        {
            QuoteReqId = quoteReqId,
            SecurityId = 1,
            Side = Side.Buy,
            Price = 1.00m,
            OrderQty = 1,
            SettlType = SettlementType.Mutual,
            DaysToSettlement = 30,
            ContraBroker = peer.EnteringFirm,
            FixedRate = 0.01m,
        });
#pragma warning restore B3EP_QUOTE

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in client.Events(cts.Token))
        {
            switch (evt)
            {
                case QuoteRequestRejected rej:
                    Assert.Equal(quoteReqId, rej.QuoteReqId);
                    return;
                case QuoteStatusUpdated upd when upd.QuoteReqId == quoteReqId:
                    return;
            }
        }
        Assert.Fail("No quote-flow response received");
    }
}
