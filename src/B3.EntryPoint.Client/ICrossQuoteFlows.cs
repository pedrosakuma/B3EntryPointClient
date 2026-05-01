using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client;

/// <summary>
/// Submits a <c>NewOrderCross</c> message (schema §9). Implemented by
/// <see cref="EntryPointClient.SubmitCrossAsync"/>.
/// </summary>
public interface ISubmitCross
{
    /// <summary>Submits a cross trade. Returns the <c>CrossID</c> echoed back.</summary>
    Task<string> SubmitCrossAsync(NewOrderCrossRequest request, CancellationToken ct = default);
}

/// <summary>
/// Quote-side messaging (QuoteRequest, Quote, QuoteCancel). Implemented by
/// <see cref="EntryPointClient"/>.
/// </summary>
public interface IQuoteFlow
{
    /// <summary>Sends a <c>QuoteRequest</c>.</summary>
    Task SendQuoteRequestAsync(QuoteRequestMessage request, CancellationToken ct = default);

    /// <summary>Sends a <c>Quote</c> (market-maker quote).</summary>
    Task SendQuoteAsync(QuoteMessage quote, CancellationToken ct = default);

    /// <summary>Cancels a previously sent quote by <paramref name="quoteId"/>.</summary>
    Task CancelQuoteAsync(string quoteId, CancellationToken ct = default);
}
