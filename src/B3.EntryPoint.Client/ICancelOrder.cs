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
    /// Submit an <c>OrderMassActionRequest</c> and await the matching
    /// <c>OrderMassActionReport</c>.
    /// </summary>
    Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct = default);
}
