using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ThrottledLogging.Internal;

/// <summary>One long-running function. See <see cref="IOperationScope"/>.</summary>
internal sealed class OperationScope : IOperationScope
{
    // ExecutionContext.Run takes a static callback plus a state object; these avoid allocating a
    // closure on every sweep.
    private static readonly ContextCallback SweepCallback = static state => ((OperationScope)state!).SweepCore();

    private readonly OperationLogger _owner;
    private readonly ILogger _logger;
    private readonly OperationOptions _options;
    private readonly TimeProvider _time;
    private readonly DurationStatistics _statistics;
    private readonly FailureBreakdown _breakdown;
    private readonly ConcurrentDictionary<long, long> _inFlight = new();
    private readonly long _startTimestamp;

    // Serialises the three things that decide an operation's last lines: End, a sweep, and the
    // shutdown flush. Without it, shutdown could check "not ended", lose the CPU while the caller
    // ends the operation, and then write "still running" after the success line; or a sweep could
    // write a heartbeat after the end line. The lock is held while lines are written to the logging
    // providers, but never while the OnEmitted observer runs: its notifications are collected and
    // handed over once the lock is released, so an observer that waits for another thread to end
    // this operation cannot deadlock. A logging provider must likewise not block waiting for this
    // operation to end, since it is called under the lock.
    // (System.Threading.Lock would do, but only exists from .NET 9 and this also targets .NET 8.)
    private readonly object _lifecycleGate = new();

    // The caller's ambient context at BeginOperation: its logging scopes (Microsoft.Extensions.Logging
    // keeps them in an AsyncLocal), Activity.Current (which Application Insights reads for
    // operation_Id), and any other AsyncLocal. Lines written on the caller's own thread have all of
    // this anyway; lines written by the sweeper's timer thread or by OperationLogger.Dispose would
    // have none of it, so those paths run inside this context instead. Null when the caller
    // suppressed flow. Cleared when the operation ends, so an ended operation that something still
    // references does not keep the caller's AsyncLocal values (request objects, buffers) alive.
    private ExecutionContext? _callerContext;

    // OperationOptions.Scope, wrapped once for the whole operation. Null when there is nothing to attach.
    private readonly ContextScopeState? _scopeState;

    private long _processed;
    private long _failed;
    private long _itemIds;
    private int _ended;

    /// <summary>Creates the operation. The entry line is written by <see cref="OperationLogger"/> once the scope is registered.</summary>
    /// <param name="owner">The factory that created this scope.</param>
    /// <param name="logger">Where lines are written.</param>
    /// <param name="name">The operation name.</param>
    /// <param name="options">Validated, caller-independent settings.</param>
    /// <param name="time">The clock.</param>
    public OperationScope(OperationLogger owner, ILogger logger, string name, OperationOptions options, TimeProvider time)
    {
        _owner = owner;
        _logger = logger;
        _options = options;
        _time = time;

        Id = Guid.NewGuid();
        Name = name;
        StartedAtUtc = time.GetUtcNow();
        _startTimestamp = time.GetTimestamp();

        Progress = new ThrottleChannel(options.EveryItems, options.EveryInterval, time);
        Failures = new ThrottleChannel(options.FailureEveryItems, options.FailureEveryInterval, time);
        _statistics = new DurationStatistics(options.EtaHalfLifeItems, time);
        _breakdown = new FailureBreakdown(options.MaxTrackedFailureTypes);

        _callerContext = ExecutionContext.Capture();
        if (options.Scope is { Count: > 0 } scope)
        {
            _scopeState = new ContextScopeState(scope);
        }
    }

    /// <inheritdoc />
    public Guid Id { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>When the operation began.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>The progress channel: started and succeeded events.</summary>
    public ThrottleChannel Progress { get; }

    /// <summary>The failure channel, throttled independently of progress.</summary>
    public ThrottleChannel Failures { get; }

    /// <summary>Whether the operation has ended.</summary>
    public bool HasEnded => Volatile.Read(ref _ended) != 0;

    /// <summary>The shorter of this operation's two time thresholds, which is how often it needs sweeping.</summary>
    public TimeSpan TightestInterval => _options.EveryInterval < _options.FailureEveryInterval ? _options.EveryInterval : _options.FailureEveryInterval;

    /// <inheritdoc />
    public IItemScope BeginItem(string? label = null)
    {
        ObjectDisposedException.ThrowIf(HasEnded, this);

        long id = Interlocked.Increment(ref _itemIds);
        long startTimestamp = _time.GetTimestamp();
        _inFlight[id] = startTimestamp;

        SubmitProgress(label, ItemOutcome.Started);
        return new ItemScope(this, id, label, startTimestamp);
    }

    /// <inheritdoc />
    public void Success(string? note = null) => End(error: null, note, succeeded: true);

    /// <inheritdoc />
    public void Failure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        End(exception, note: null, succeeded: false);
    }

    /// <summary>Ends the operation, flushing anything still held. Ending twice is a no-op.</summary>
    public void Dispose() => End(error: null, note: null, succeeded: null);

    /// <inheritdoc />
    public OperationSnapshot Snapshot()
    {
        DurationSnapshot statistics = _statistics.Snapshot();
        long processed = Interlocked.Read(ref _processed);
        long failed = Interlocked.Read(ref _failed);
        TimeSpan elapsed = _time.GetElapsedTime(_startTimestamp);
        TimeSpan? longestInFlight = LongestInFlight();
        DateTimeOffset now = _time.GetUtcNow();

        PendingEvent? lastProgress = Progress.Latest;
        PendingEvent? lastFailure = Failures.Latest;
        long completions = processed + failed;

        return new OperationSnapshot
        {
            Id = Id,
            Name = Name,
            StartedAtUtc = StartedAtUtc,
            Elapsed = elapsed,
            Processed = processed,
            Failed = failed,
            Total = _options.TotalItems,
            Pending = _options.TotalItems is { } total ? Math.Max(0, total - processed - failed) : null,
            InFlight = _inFlight.Count,
            RatePerSecond = statistics.RatePerSecond,
            FailureRate = completions == 0 ? 0.0 : (double)failed / completions,
            MeanItemDuration = FromSeconds(statistics.MeanSuccessSeconds),
            TypicalItemDuration = FromSeconds(statistics.TypicalSuccessSeconds),
            MeanFailureDuration = FromSeconds(statistics.MeanFailureSeconds),
            LongestInFlight = longestInFlight,
            Eta = EtaCalculator.Compute(_options.TotalItems, processed, failed, elapsed, longestInFlight, statistics, _options, now),
            LastItemLabel = lastProgress?.Label,
            LastEventSubmittedAtUtc = lastProgress?.SubmittedAtUtc,
            LastFailureLabel = lastFailure?.Label,
            LastFailureAtUtc = lastFailure?.SubmittedAtUtc,
            FailuresByType = _breakdown.Snapshot(),
        };
    }

    /// <summary>Writes the entry line. Called once, by the factory, after registration.</summary>
    public void LogEntry()
    {
        using IDisposable? scope = BeginContextScope();
        Log.OperationStarted(_logger, _options.Level, Name, Id, _options.TotalItems);
    }

    /// <summary>
    /// Takes the lifecycle lock for <see cref="OperationLogger.BeginOperation"/>, which holds it from
    /// registration until the entry line is written, so a shutdown flush that finds the operation
    /// registered waits for its entry line instead of writing "still running" ahead of it.
    /// </summary>
    /// <remarks>Instant: nothing else can see the operation before it is registered.</remarks>
    public void EnterForEntry() => Monitor.Enter(_lifecycleGate);

    /// <summary>Releases the lock taken by <see cref="EnterForEntry"/>.</summary>
    public void ExitAfterEntry() => Monitor.Exit(_lifecycleGate);

    /// <summary>
    /// Marks an operation whose entry line threw as ended without writing anything, and lets go of
    /// the caller's context. The caller never received it, so nothing else could ever end it, and a
    /// shutdown flush already waiting on it must find nothing to write.
    /// </summary>
    public void Abandon()
    {
        Volatile.Write(ref _ended, 1);
        Volatile.Write(ref _callerContext, null);
    }

    /// <summary>Records the outcome of one item. Called by <see cref="ItemScope"/>.</summary>
    /// <param name="id">The in-flight identifier.</param>
    /// <param name="label">The item label.</param>
    /// <param name="succeeded">Whether it succeeded.</param>
    /// <param name="error">The exception, when it failed.</param>
    /// <param name="startTimestamp">When the item began.</param>
    public void CompleteItem(long id, string? label, bool succeeded, Exception? error, long startTimestamp)
    {
        _inFlight.TryRemove(id, out _);

        // Every completed item feeds the predictive statistics, whatever its outcome: an ETA
        // predicts wall-clock, and a failure consumed just as much of it as a success. See the
        // design document, section 6.7.
        _statistics.Record(_time.GetElapsedTime(startTimestamp), succeeded);

        if (succeeded)
        {
            Interlocked.Increment(ref _processed);
            SubmitProgress(label, ItemOutcome.Succeeded);
        }
        else
        {
            Interlocked.Increment(ref _failed);
            _breakdown.Record(error);
            SubmitFailure(label, error);
        }
    }

    /// <summary>
    /// Lets the background sweeper release held events whose time threshold has passed. Runs in
    /// the caller's captured context, so a heartbeat carries the same scopes and trace as the lines
    /// the caller's own thread writes.
    /// </summary>
    public void Sweep()
    {
        if (HasEnded)
        {
            return;
        }

        // The sweeper's own thread starts with no ambient context (OperationLogger starts it with
        // flow suppressed), so an operation that captured nothing is swept bare, as intended.
        RunInCallerContext(SweepCallback, isolateWhenUncaptured: false);
    }

    /// <summary>
    /// Called by <see cref="OperationLogger.Dispose"/> for an operation that has not ended: writes
    /// out whatever is held on both channels, then one line saying the operation was still running.
    /// </summary>
    /// <remarks>
    /// The operation is deliberately left open. Hosts dispose the logger while shutting down, and
    /// the caller's loop may still be draining; ending the scope here would make its next
    /// <see cref="BeginItem"/> throw. If the caller does end it later, the usual closing line
    /// follows. If the process dies first, the shutdown line is the operation's last word, with
    /// the counts it had reached.
    /// </remarks>
    /// <param name="lockTimeout">
    /// How long to wait for the lifecycle lock. Another thread holds it while writing this
    /// operation's lines, and a logging provider that hangs there would otherwise hang shutdown.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the lock could not be taken in time, so nothing was written;
    /// <see langword="true"/> otherwise, including when the operation had already ended.
    /// </returns>
    public bool FlushForShutdown(TimeSpan lockTimeout)
    {
        if (HasEnded)
        {
            return true;
        }

        // Dispose runs on whatever thread the host disposes on, often inside some unrelated
        // invocation's scope. An operation that captured nothing must not borrow that.
        bool flushed = false;
        RunInCallerContext(state => flushed = ((OperationScope)state!).FlushForShutdownCore(lockTimeout), isolateWhenUncaptured: true);
        return flushed;
    }

    /// <summary>Runs <paramref name="callback"/> in the context captured at BeginOperation.</summary>
    /// <param name="callback">The work, called with this operation as its state.</param>
    /// <param name="isolateWhenUncaptured">
    /// When nothing was captured, run on a thread-pool thread with flow suppressed, so the work
    /// sees no ambient context at all rather than the current thread's. Blocks until it is done;
    /// only used at shutdown, where blocking is expected.
    /// </param>
    private void RunInCallerContext(ContextCallback callback, bool isolateWhenUncaptured)
    {
        ExecutionContext? captured = Volatile.Read(ref _callerContext);
        if (captured is not null)
        {
            // A captured ExecutionContext is immutable and may be run any number of times, from any
            // thread, including concurrently (true on .NET Core and later, which is all this targets).
            // Run restores the calling thread's own context afterwards, even if callback throws.
            ExecutionContext.Run(captured, callback, this);
            return;
        }

        if (!isolateWhenUncaptured)
        {
            callback(this);
            return;
        }

        CleanContext.RunAndWait(() => callback(this));
    }

    /// <param name="lockTimeout">How long to wait for the lifecycle lock.</param>
    /// <returns>Whether the lock was taken.</returns>
    private bool FlushForShutdownCore(TimeSpan lockTimeout)
    {
        List<ThrottledEvent> deferred = [];
        bool taken = false;
        try
        {
            Monitor.TryEnter(_lifecycleGate, lockTimeout, ref taken);
            if (taken)
            {
                FlushForShutdownLocked(deferred);
            }

            return taken;
        }
        finally
        {
            if (taken)
            {
                Monitor.Exit(_lifecycleGate);
            }

            NotifyDeferred(deferred);
        }
    }

    private void FlushForShutdownLocked(List<ThrottledEvent> deferred)
    {
        if (HasEnded)
        {
            return;
        }

        if (Progress.Flush() is { } heldProgress)
        {
            Emit(heldProgress, _options.Level, deferred);
        }

        if (Failures.Flush() is { } heldFailure)
        {
            Emit(heldFailure, _options.FailureLevel, deferred);
        }

        // A logging provider can end the operation while writing the lines above: it runs on this
        // thread, which already holds the lifecycle lock, so End gets straight back in and writes
        // the closing line. "Still running" after that would be false.
        if (HasEnded)
        {
            return;
        }

        OperationSnapshot snapshot = Snapshot();
        using IDisposable? scope = BeginContextScope();
        Log.OperationStillRunningAtShutdown(
            _logger,
            _options.FailureLevel,
            Name,
            Id,
            snapshot.Elapsed.TotalSeconds,
            snapshot.Processed,
            snapshot.Failed,
            snapshot.InFlight);
    }

    private void SweepCore()
    {
        List<ThrottledEvent> deferred = [];
        try
        {
            lock (_lifecycleGate)
            {
                if (HasEnded)
                {
                    return;
                }

                if (Progress.TryFlushDueToTime() is { } progress)
                {
                    Emit(progress, _options.Level, deferred);
                }

                if (Failures.TryFlushDueToTime() is { } failure)
                {
                    Emit(failure, _options.FailureLevel, deferred);
                }
            }
        }
        finally
        {
            NotifyDeferred(deferred);
        }
    }

    private void SubmitProgress(string? label, ItemOutcome outcome)
    {
        if (Progress.Submit(label, outcome, error: null, _time.GetUtcNow()) is { } emission)
        {
            Emit(emission, _options.Level);
        }
    }

    private void SubmitFailure(string? label, Exception? error)
    {
        if (Failures.Submit(label, ItemOutcome.Failed, error, _time.GetUtcNow()) is { } emission)
        {
            Emit(emission, _options.FailureLevel);
        }
    }

    /// <summary>
    /// Opens <see cref="OperationOptions.Scope"/> on this operation's logger, or does nothing when
    /// there is none. Each line opens and closes it around itself rather than holding it for the
    /// operation's lifetime, because logging scopes live in the ambient context of whichever thread
    /// writes, and an operation's lines are written from several.
    /// </summary>
    /// <returns>The scope to dispose, or <see langword="null"/>.</returns>
    private IDisposable? BeginContextScope() => _scopeState is null ? null : _logger.BeginScope(_scopeState);

    /// <summary>Writes one emission and tells the <c>OnEmitted</c> observer about it.</summary>
    /// <param name="emission">What the channel released.</param>
    /// <param name="level">The level to write at.</param>
    /// <param name="deferred">
    /// When the caller holds the lifecycle lock, the list to add the observer notification to
    /// instead of making it now; the caller makes it once the lock is released. See <see cref="NotifyDeferred"/>.
    /// </param>
    private void Emit(in Emission emission, LogLevel level, List<ThrottledEvent>? deferred = null)
    {
        OperationSnapshot snapshot = Snapshot();
        PendingEvent pending = emission.Event;

        // The operation's scope covers the log call only. OnEmitted below runs outside it, so a
        // callback that begins another operation does not capture this one's scope, and one that
        // ends this operation does not open it a second time around the closing lines.
        using (BeginContextScope())
        {
            WriteEmission(emission, pending, snapshot, level);
        }

        ThrottledEvent emitted = new()
        {
            OperationName = Name,
            OperationId = Id,
            ItemLabel = pending.Label,
            Outcome = pending.Outcome,
            SubmittedAtUtc = pending.SubmittedAtUtc,
            EmittedAtUtc = _time.GetUtcNow(),
            IsNew = emission.IsNew,
            SuppressedSince = emission.SuppressedSince,
            Error = pending.Error,
            Progress = snapshot,
        };

        if (deferred is null)
        {
            _owner.NotifyEmitted(emitted);
        }
        else
        {
            deferred.Add(emitted);
        }
    }

    /// <summary>
    /// Hands notifications collected under the lifecycle lock to the <c>OnEmitted</c> observer, after
    /// the lock has been released.
    /// </summary>
    /// <param name="deferred">The notifications, in the order their lines were written; may be <see langword="null"/>.</param>
    /// <remarks>
    /// The observer is user code, and user code may wait for another thread to end this very
    /// operation. Called under the lock, that wait would deadlock: the other thread's End needs the
    /// lock the observer's thread is holding. Called after it, the observer sees exactly what it saw
    /// before, just a moment later, and anything it does to the operation (including ending it)
    /// happens after the lines already written, never interleaved with them.
    /// </remarks>
    private void NotifyDeferred(List<ThrottledEvent>? deferred)
    {
        if (deferred is null)
        {
            return;
        }

        foreach (ThrottledEvent emitted in deferred)
        {
            _owner.NotifyEmitted(emitted);
        }
    }

    private void WriteEmission(in Emission emission, PendingEvent pending, OperationSnapshot snapshot, LogLevel level)
    {
        if (pending.Outcome == ItemOutcome.Failed)
        {
            Log.ItemFailed(_logger, level, pending.Error, Name, Id, pending.Label, snapshot.Failed, emission.IsNew, pending.SubmittedAtUtc, emission.SuppressedSince);
        }
        else
        {
            // During the ETA warm-up there is no band to print, so the line says so rather than
            // rendering an empty one. See Log.Progress and Log.ProgressWithoutEta.
            if (snapshot.Eta is { Remaining: { } remaining, RemainingLow: { } low, RemainingHigh: { } high })
            {
                Log.Progress(
                    _logger,
                    level,
                    Name,
                    Id,
                    pending.Label,
                    pending.Outcome,
                    snapshot.Processed,
                    snapshot.Total,
                    snapshot.Failed,
                    emission.IsNew,
                    pending.SubmittedAtUtc,
                    emission.SuppressedSince,
                    snapshot.RatePerSecond,
                    remaining.TotalSeconds,
                    low.TotalSeconds,
                    high.TotalSeconds);
            }
            else
            {
                Log.ProgressWithoutEta(
                    _logger,
                    level,
                    Name,
                    Id,
                    pending.Label,
                    pending.Outcome,
                    snapshot.Processed,
                    snapshot.Total,
                    snapshot.Failed,
                    emission.IsNew,
                    pending.SubmittedAtUtc,
                    emission.SuppressedSince,
                    snapshot.RatePerSecond);
            }
        }
    }

    private TimeSpan? LongestInFlight()
    {
        long oldest = long.MaxValue;
        foreach (long started in _inFlight.Values)
        {
            if (started < oldest)
            {
                oldest = started;
            }
        }

        return oldest == long.MaxValue ? null : _time.GetElapsedTime(oldest);
    }

    private void End(Exception? error, string? note, bool? succeeded)
    {
        List<ThrottledEvent> deferred = [];
        try
        {
            lock (_lifecycleGate)
            {
                if (Interlocked.Exchange(ref _ended, 1) != 0)
                {
                    return;
                }

                try
                {
                    WriteClosingLines(error, note, succeeded, deferred);
                }
                finally
                {
                    // Whatever the logging providers did above, the operation leaves the registry
                    // and lets go of the caller's context. Otherwise one throwing provider would
                    // strand it in the singleton for the life of the process, already marked ended
                    // so that no second Dispose could retire it.
                    Volatile.Write(ref _callerContext, null);
                    _owner.Retire(this, succeeded ?? false);
                }
            }
        }
        finally
        {
            NotifyDeferred(deferred);
        }
    }

    private void WriteClosingLines(Exception? error, string? note, bool? succeeded, List<ThrottledEvent> deferred)
    {
        // Whatever is still held goes out before the closing line, so a run that ends badly never
        // hides its most recent failure.
        if (Progress.Flush() is { } heldProgress)
        {
            Emit(heldProgress, _options.Level, deferred);
        }

        if (Failures.Flush() is { } heldFailure)
        {
            Emit(heldFailure, _options.FailureLevel, deferred);
        }

        OperationSnapshot snapshot = Snapshot();
        double elapsedSeconds = snapshot.Elapsed.TotalSeconds;
        using IDisposable? scope = BeginContextScope();

        if (snapshot.Failed > 0 && _logger.IsEnabled(_options.FailureLevel))
        {
            // Describe() sorts and joins, so it is built only once the level is known to be on.
            string breakdown = _breakdown.Describe();
            Log.FailureSummary(
                _logger,
                _options.FailureLevel,
                Name,
                Id,
                snapshot.Failed,
                snapshot.Total,
                elapsedSeconds,
                breakdown,
                snapshot.LastFailureLabel,
                snapshot.LastFailureAtUtc);
        }

        switch (succeeded)
        {
            case true:
                Log.OperationSucceeded(_logger, _options.Level, Name, elapsedSeconds, snapshot.Processed, snapshot.Failed, note, Id);
                break;
            case false:
                Log.OperationFailed(_logger, _options.FailureLevel, error!, Name, elapsedSeconds, snapshot.Processed, snapshot.Failed, Id);
                break;
            default:
                Log.OperationIncomplete(_logger, _options.FailureLevel, Name, elapsedSeconds, snapshot.Processed, snapshot.Failed, Id);
                break;
        }
    }

    private static TimeSpan? FromSeconds(double? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;
}
