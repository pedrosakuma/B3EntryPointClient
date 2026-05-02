using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client;

/// <summary>
/// Submits a <c>NewOrderCross</c> message (schema §9). Implemented by
/// <see cref="EntryPointClient.SubmitCrossAsync"/>.
/// </summary>
/// <remarks>
/// Marked <see cref="System.Diagnostics.CodeAnalysis.ExperimentalAttribute"/>
/// (#130): the wire encoder ships, but the cross flow has only API-surface
/// coverage and lacks behavioural / conformance tests against a peer.
/// Treat as preview and pin to a specific package version. Suppress
/// <c>B3EP_CROSS</c> at the call site to opt-in.
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("B3EP_CROSS")]
public interface ISubmitCross
{
    /// <summary>Submits a cross trade. Returns the <c>CrossID</c> echoed back.</summary>
    Task<string> SubmitCrossAsync(NewOrderCrossRequest request, CancellationToken ct = default);
}

/// <summary>
/// Quote-side messaging (QuoteRequest, Quote, QuoteCancel). Implemented by
/// <see cref="EntryPointClient"/>.
/// </summary>
/// <remarks>
/// Marked <see cref="System.Diagnostics.CodeAnalysis.ExperimentalAttribute"/>
/// (#130): the wire encoders ship, but the quote flow is exercised only
/// through API-surface tests; there is no end-to-end conformance test
/// asserting the gateway's <c>QuoteAck</c>/<c>QuoteCancel</c> reply
/// semantics. Suppress <c>B3EP_QUOTE</c> at the call site to opt-in.
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("B3EP_QUOTE")]
public interface IQuoteFlow
{
    /// <summary>Sends a <c>QuoteRequest</c>.</summary>
    Task SendQuoteRequestAsync(QuoteRequestMessage request, CancellationToken ct = default);

    /// <summary>Sends a <c>Quote</c> (market-maker quote).</summary>
    Task SendQuoteAsync(QuoteMessage quote, CancellationToken ct = default);

    /// <summary>Cancels a previously sent quote by <paramref name="quoteId"/>.</summary>
    Task CancelQuoteAsync(string quoteId, CancellationToken ct = default);
}
