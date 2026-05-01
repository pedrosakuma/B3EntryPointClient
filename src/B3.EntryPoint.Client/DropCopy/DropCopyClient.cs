using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.DropCopy;

/// <summary>
/// Read-only Drop Copy session. Establishes a FIXP session with
/// <see cref="SessionProfile.DropCopy"/> and surfaces only the
/// <see cref="EntryPointEvent"/> stream for the entitled firm.
/// Submission (NewOrder / Replace / Cancel) is intentionally absent —
/// callers should use <see cref="EntryPointClient"/> for that.
/// </summary>
public sealed class DropCopyClient : IAsyncDisposable
{
    private readonly EntryPointClient _inner;

    public DropCopyClient(EntryPointClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile != SessionProfile.DropCopy)
        {
            throw new ArgumentException(
                $"DropCopyClient requires {nameof(EntryPointClientOptions.Profile)} = {SessionProfile.DropCopy}.",
                nameof(options));
        }
        _inner = new EntryPointClient(options);
    }

    /// <summary>Establishes the Drop Copy FIXP session.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "Drop Copy session establishment is part of the wire-up. Tracked by issue #10.");

    /// <summary>Read-only event stream — same shape as <see cref="EntryPointClient.Events"/>.</summary>
    public IAsyncEnumerable<EntryPointEvent> Events(CancellationToken cancellationToken = default)
        => _inner.Events(cancellationToken);

    /// <summary>Terminates the Drop Copy FIXP session.</summary>
    public Task TerminateAsync(TerminationCode code = TerminationCode.Finished, CancellationToken cancellationToken = default)
        => _inner.TerminateAsync(code, cancellationToken);

    /// <summary>Raised when the gateway terminates the session.</summary>
    public event EventHandler<TerminatedEventArgs>? Terminated
    {
        add => _inner.Terminated += value;
        remove => _inner.Terminated -= value;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
