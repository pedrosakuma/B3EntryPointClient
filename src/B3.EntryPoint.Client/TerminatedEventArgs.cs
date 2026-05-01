namespace B3.EntryPoint.Client;

/// <summary>
/// Payload of <see cref="EntryPointClient.Terminated"/>. Surfaces both
/// client-initiated and peer-initiated terminations.
/// </summary>
public sealed class TerminatedEventArgs : EventArgs
{
    public TerminatedEventArgs(TerminationCode code, string? reason, bool initiatedByClient)
    {
        Code = code;
        Reason = reason;
        InitiatedByClient = initiatedByClient;
    }

    public TerminationCode Code { get; }

    public string? Reason { get; }

    public bool InitiatedByClient { get; }
}
