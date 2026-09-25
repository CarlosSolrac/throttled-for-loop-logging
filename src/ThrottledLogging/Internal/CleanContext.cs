namespace ThrottledLogging.Internal;

/// <summary>Runs work with no ambient context inherited from the caller.</summary>
internal static class CleanContext
{
    /// <summary>
    /// Starts <paramref name="work"/> with execution-context flow suppressed, so it sees none of the
    /// caller's logging scopes, <see cref="System.Diagnostics.Activity"/> or other AsyncLocal values.
    /// </summary>
    /// <param name="work">The work.</param>
    /// <returns>The started task.</returns>
    /// <remarks>
    /// <see cref="ExecutionContext.SuppressFlow"/> throws when flow is already suppressed, and the
    /// caller may well have done that itself, so it is only called when needed. Only for work that
    /// nobody waits on straight away: a task waited on before it starts can be run inline on the
    /// waiting thread, in that thread's context. For work that must be waited on, run it in
    /// <see cref="Empty"/> instead.
    /// </remarks>
    public static Task Start(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Run(work);
        }

        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    /// <summary>
    /// The default execution context: no logging scopes, no <see cref="System.Diagnostics.Activity"/>,
    /// no AsyncLocal values. <see cref="ExecutionContext.Run"/> runs work in it on the calling thread.
    /// </summary>
    /// <remarks>
    /// There is no public way to name the default context, so it is captured once from a thread
    /// started with flow suppressed, which begins in it. Running in it needs no other thread, so it
    /// cannot be held up by a starved thread pool at shutdown, and it cannot be inlined into the
    /// wrong context the way a task waited on before it starts can be.
    /// </remarks>
    public static ExecutionContext Empty { get; } = CaptureEmpty();

    private static ExecutionContext CaptureEmpty()
    {
        ExecutionContext? empty = null;
        Thread capture = new(() => empty = ExecutionContext.Capture()) { IsBackground = true, Name = "ThrottledLogging context capture" };
        if (ExecutionContext.IsFlowSuppressed())
        {
            capture.Start();
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                capture.Start();
            }
        }

        capture.Join();

        // Capture only returns null when flow is suppressed on the capturing thread, which a new
        // thread never starts with.
        return empty ?? throw new InvalidOperationException("Could not capture the default execution context.");
    }
}
