using B3.EntryPoint.Client.Models;
using ClOrdID = B3.EntryPoint.Client.Models.ClOrdID;

namespace B3.EntryPoint.Client;

/// <summary>
/// Replace previously submitted orders. Implemented by
/// <see cref="EntryPointClient"/>.
/// </summary>
/// <remarks>
/// API surface only — the SBE encoding lands in a follow-up PR (issue #7).
/// </remarks>
public interface IReplaceOrder
{
    /// <summary>Submit an <c>OrderCancelReplaceRequest</c>.</summary>
    Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct = default);

    /// <summary>Submit a <c>SimpleModifyOrder</c>.</summary>
    Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct = default);
}
