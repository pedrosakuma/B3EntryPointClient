using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Quote_Flow;

/// <summary>
/// Conformance — <c>Quote</c> (template 403, B3 Termo §10). Sends a market-maker
/// Quote and expects a <see cref="QuoteStatusUpdated"/> from the peer carrying
/// the same quote id. Skipped without a configured peer.
/// </summary>
[Trait("Category", "Conformance")]
public class QuoteSubmitHappyPathTests
{
    [ConformanceFact]
    public async Task Send_Quote_Receives_QuoteStatus()
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

        var quoteId = ((ulong)Math.Abs(Guid.NewGuid().GetHashCode()) | 1UL).ToString();

#pragma warning disable B3EP_QUOTE
        await client.SendQuoteAsync(new QuoteMessage
        {
            QuoteId = quoteId,
            SecurityId = 1,
            Side = Side.Sell,
            OrderQty = 1,
            SettlType = SettlementType.Mutual,
            DaysToSettlement = 30,
            Price = 1.00m,
            FixedRate = 0.01m,
        });
#pragma warning restore B3EP_QUOTE

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in client.Events(cts.Token))
        {
            if (evt is QuoteStatusUpdated upd && upd.QuoteId == quoteId)
                return;
        }
        Assert.Fail("No QuoteStatusUpdated received for submitted Quote");
    }
}
