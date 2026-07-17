using B3.EntryPoint.Client.Models;
using ClOrdID = B3.EntryPoint.Client.Models.ClOrdID;

namespace B3.EntryPoint.Client;

/// <summary>
/// Replace previously submitted orders. Implemented by
/// <see cref="EntryPointClient"/>.
/// </summary>
public interface IReplaceOrder
{
    /// <summary>Submit an <c>OrderCancelReplaceRequest</c>.</summary>
    Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// Submit an <c>OrderCancelReplaceRequest</c> with a durable pre-write frame boundary.
    /// </summary>
    /// <remarks>
    /// Transport completion is not venue acceptance. Exact original-sequence
    /// replay is unsupported; reconcile indeterminate attempts rather than resend.
    /// </remarks>
    Task<OutboundAttemptReceipt> ReplaceWithReceiptAsync(
        ReplaceOrderRequest request,
        OutboundFramePreparedCallback onFramePrepared,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This IReplaceOrder implementation does not support durable outbound receipts.");

    /// <summary>Submit a <c>SimpleModifyOrder</c>.</summary>
    Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct = default);
}
