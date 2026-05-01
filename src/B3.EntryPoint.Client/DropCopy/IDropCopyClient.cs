using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.DropCopy;

/// <summary>
/// Drop Copy session client contract. Implemented by
/// <see cref="DropCopyClient"/>. Exists so consumers can mock the drop-copy
/// flow independently of the concrete TCP-bound implementation.
/// </summary>
public interface IDropCopyClient : IAsyncDisposable
{
    /// <summary>Establishes the Drop Copy FIXP session.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams inbound EntryPoint events (drop-copy reports).</summary>
    IAsyncEnumerable<EntryPointEvent> Events(CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Terminate</c> frame and closes the drop-copy session.</summary>
    Task TerminateAsync(TerminationCode code = TerminationCode.Finished, CancellationToken cancellationToken = default);

    /// <summary>Raised when the drop-copy session is terminated.</summary>
    event EventHandler<TerminatedEventArgs>? Terminated;
}
