using B3.EntryPoint.Client.Models;
using ClOrdID = B3.EntryPoint.Client.Models.ClOrdID;

namespace B3.EntryPoint.Client;

/// <summary>
/// Submit new orders to the EntryPoint gateway. Implemented by
/// <see cref="EntryPointClient"/> when the session profile is
/// <c>SessionProfile.OrderEntry</c>.
/// </summary>
public interface ISubmitOrder
{
    /// <summary>Submit a <c>NewOrderSingle</c>.</summary>
    /// <returns>The <see cref="ClOrdID"/> echoed in subsequent ExecutionReports.</returns>
    Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct = default);

    /// <summary>Submit a <c>SimpleNewOrder</c> (lightweight reduced field set).</summary>
    Task<ClOrdID> SubmitSimpleAsync(SimpleNewOrderRequest request, CancellationToken ct = default);
}
