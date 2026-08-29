using System.Diagnostics;
using Xunit.Sdk;

namespace B3.EntryPoint.Conformance.Infrastructure;

/// <summary>
/// Replaces the flaky <c>Assert.Same(task, await Task.WhenAny(task,
/// Task.Delay(timeout)))</c> pattern (see #245) with a helper that reports
/// elapsed time on timeout, making CI flakes easier to diagnose, and that
/// re-awaits the original task so any exception it faulted with propagates
/// instead of being swallowed.
/// </summary>
public static class AsyncAssert
{
    public static async Task CompletesWithinAsync(Task task, TimeSpan timeout, string? because = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            var reason = because is null ? string.Empty : $" ({because})";
            throw new XunitException(
                $"Expected the awaited task to complete within {timeout}{reason}, but it did not. Elapsed: {stopwatch.Elapsed}.");
        }

        await task.ConfigureAwait(false);
    }

    public static async Task<T> CompletesWithinAsync<T>(Task<T> task, TimeSpan timeout, string? because = null, CancellationToken cancellationToken = default)
    {
        await CompletesWithinAsync((Task)task, timeout, because, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }
}
