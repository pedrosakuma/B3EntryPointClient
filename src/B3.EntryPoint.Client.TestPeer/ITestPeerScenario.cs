namespace B3.EntryPoint.Client.TestPeer;

/// <summary>
/// Strategy that decides how an <see cref="InProcessFixpTestPeer"/> responds
/// to inbound application messages. Implementations must be thread-safe — the
/// peer may invoke them concurrently across connections.
/// </summary>
/// <remarks>
/// The default is <see cref="TestPeerScenarios.AcceptAll"/>, which mirrors the
/// historical behaviour: every NewOrderSingle yields an
/// <c>ExecutionReport_New</c> with <c>OrdStatus = NEW</c>. Implementations
/// can return:
/// <list type="bullet">
/// <item><see cref="NewOrderResponse.AcceptAsNew"/> — single ER (NEW).</item>
/// <item><see cref="NewOrderResponse.AcceptAndFill"/> — ER (NEW) followed by ER (FILLED).</item>
/// <item><see cref="NewOrderResponse.RejectBusiness"/> — BusinessMessageReject.</item>
/// </list>
/// </remarks>
public interface ITestPeerScenario
{
    /// <summary>
    /// Decide what the peer should respond to an inbound NewOrderSingle.
    /// </summary>
    NewOrderResponse OnNewOrder(NewOrderContext context);
}

/// <summary>Decoded summary of an inbound NewOrderSingle.</summary>
public readonly record struct NewOrderContext(
    uint SessionId,
    uint EnteringFirm,
    ulong SecurityId,
    string ClOrdId);

/// <summary>Discriminated union of peer responses to a NewOrderSingle.</summary>
public abstract record NewOrderResponse
{
    private NewOrderResponse() { }

    /// <summary>Single <c>ExecutionReport_New</c> with <c>OrdStatus = NEW</c> (default).</summary>
    public sealed record AcceptAsNew : NewOrderResponse;

    /// <summary>
    /// <c>ExecutionReport_New</c> immediately followed by an
    /// <c>ExecutionReport_Trade</c> with <c>OrdStatus = FILLED</c>.
    /// (Trade ER not yet implemented in the wire — currently sends a single
    /// New ER. Tracked for follow-up.)
    /// </summary>
    public sealed record AcceptAndFill : NewOrderResponse;

    /// <summary>
    /// <c>BusinessMessageReject</c> referencing the inbound order, with the
    /// supplied <see cref="Reason"/>.
    /// </summary>
    public sealed record RejectBusiness(string Reason) : NewOrderResponse;
}

/// <summary>Built-in <see cref="ITestPeerScenario"/> implementations.</summary>
public static class TestPeerScenarios
{
    /// <summary>Accept every NewOrder as <c>NEW</c> (historical default).</summary>
    public static ITestPeerScenario AcceptAll { get; } = new AcceptAllScenario();

    /// <summary>Accept every NewOrder and immediately fill it.</summary>
    public static ITestPeerScenario FillImmediately { get; } = new FillImmediatelyScenario();

    /// <summary>Reject every NewOrder with the given reason.</summary>
    public static ITestPeerScenario RejectAll(string reason = "rejected by test peer") =>
        new RejectAllScenario(reason);

    private sealed class AcceptAllScenario : ITestPeerScenario
    {
        public NewOrderResponse OnNewOrder(NewOrderContext context) => new NewOrderResponse.AcceptAsNew();
    }

    private sealed class FillImmediatelyScenario : ITestPeerScenario
    {
        public NewOrderResponse OnNewOrder(NewOrderContext context) => new NewOrderResponse.AcceptAndFill();
    }

    private sealed class RejectAllScenario : ITestPeerScenario
    {
        private readonly string _reason;
        public RejectAllScenario(string reason) { _reason = reason; }
        public NewOrderResponse OnNewOrder(NewOrderContext context) => new NewOrderResponse.RejectBusiness(_reason);
    }
}
