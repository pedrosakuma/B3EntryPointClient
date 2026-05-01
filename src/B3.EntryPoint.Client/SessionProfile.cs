namespace B3.EntryPoint.Client;

/// <summary>
/// Identifies which gateway profile a FIXP session targets. Drop Copy sessions
/// share the same wire protocol as Order Entry but are read-only and surface
/// only the <c>ExecutionReport</c> family for the entitled firm.
/// </summary>
public enum SessionProfile
{
    /// <summary>Standard order entry session — full submit/replace/cancel API.</summary>
    OrderEntry = 0,

    /// <summary>Read-only fan-out of execution reports for entitled firms.</summary>
    DropCopy = 1,
}
