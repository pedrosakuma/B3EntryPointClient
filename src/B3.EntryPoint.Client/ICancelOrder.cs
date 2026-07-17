using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client;

/// <summary>
/// Cancel previously submitted orders, individually or via mass action.
/// Implemented by <see cref="EntryPointClient"/>.
/// </summary>
public interface ICancelOrder
{
    /// <summary>Submit an <c>OrderCancelRequest</c>.</summary>
    Task CancelAsync(CancelOrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// Submit an <c>OrderCancelRequest</c> with a durable pre-write frame boundary.
    /// </summary>
    /// <remarks>
    /// Transport completion is not venue acceptance. Exact original-sequence
    /// replay is unsupported; reconcile indeterminate attempts rather than resend.
    /// </remarks>
    Task<OutboundAttemptReceipt> CancelWithReceiptAsync(
        CancelOrderRequest request,
        OutboundFramePreparedCallback onFramePrepared,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This ICancelOrder implementation does not support durable outbound receipts.");

    /// <summary>
    /// Submit an <c>OrderMassActionRequest</c> and await the matching
    /// <c>OrderMassActionReport</c>.
    /// </summary>
    Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct = default);
}
