using System.Runtime.ExceptionServices;

namespace ThrottledLogging.Internal;

/// <summary>Runs work on the thread pool with no ambient context inherited from the caller.</summary>
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
    /// waiting thread, in that thread's context. For work that must be waited on, see
    /// <see cref="RunAndWait"/>.
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
    /// Runs <paramref name="work"/> on a thread-pool thread in the default, empty context and blocks
    /// until it finishes, rethrowing whatever it threw.
    /// </summary>
    /// <param name="work">The work.</param>
    /// <remarks>
    /// Uses <see cref="ThreadPool.UnsafeQueueUserWorkItem(WaitCallback, object?)"/> rather than a
    /// task: it never flows the caller's context, and unlike a task it can never be inlined onto the
    /// waiting thread, which would run it in exactly the context this exists to avoid. Thread-pool
    /// threads return to the default context between work items.
    /// </remarks>
    public static void RunAndWait(Action work)
    {
        using ManualResetEventSlim done = new(initialState: false);
        ExceptionDispatchInfo? failure = null;

        ThreadPool.UnsafeQueueUserWorkItem(
            _ =>
            {
                try
                {
                    work();
                }
#pragma warning disable CA1031 // Captured here and rethrown on the waiting thread.
                catch (Exception error)
#pragma warning restore CA1031
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    done.Set();
                }
            },
            null);

        done.Wait();
        failure?.Throw();
    }
}
