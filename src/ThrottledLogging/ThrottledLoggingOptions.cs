namespace ThrottledLogging;

/// <summary>
/// Process-wide settings for <see cref="IOperationLogger"/>, bindable from configuration.
/// </summary>
public sealed class ThrottledLoggingOptions
{
    /// <summary>Settings applied to an operation that does not supply its own.</summary>
    public OperationOptions Defaults { get; set; } = new();

    /// <summary>
    /// Whether the background sweeper runs. It flushes held events whose time threshold has
    /// elapsed while nothing is being submitted, so a stalled loop still produces a heartbeat.
    /// One timer serves the whole process. When settings come from configuration, changing this
    /// starts or stops the sweeper without a restart.
    /// </summary>
    public bool EnableSweeper { get; set; } = true;

    /// <summary>Shortest sweeper tick. The sweeper aims for a quarter of the tightest active time threshold, clamped to this floor.</summary>
    public TimeSpan MinimumSweepInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest sweeper tick.</summary>
    public TimeSpan MaximumSweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Called with every event that survives throttling and reaches the log. Intended for pushing
    /// the same data to metrics, and for asserting on it in tests without parsing log text.
    /// </summary>
    /// <remarks>
    /// Runs inline on the thread that produced the event, so it must be quick. An exception thrown
    /// from here is swallowed and noted once at <c>Debug</c> rather than propagated into the
    /// caller's loop. It is never called while the operation's internal lock is held: for lines
    /// written by the sweeper, by the shutdown flush or while the operation ends, the call comes
    /// just after those lines are written. So it may end the operation, or wait for another thread
    /// that does, without deadlocking.
    /// </remarks>
    public Action<ThrottledEvent>? OnEmitted { get; set; }

    /// <summary>
    /// Set when configuration could not be bound, for instance a number that is not a number, so
    /// the logger can reject these settings and keep the last good ones. Not public, so the
    /// configuration binder never touches it.
    /// </summary>
    internal Exception? BindingError { get; set; }
}
