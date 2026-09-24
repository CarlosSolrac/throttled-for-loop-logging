using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThrottledLogging.Internal;

namespace ThrottledLogging;

/// <summary>
/// Creates operations and reports on the ones that are running. Register once, as a singleton.
/// </summary>
public sealed class OperationLogger : IOperationLogger, IOperationRegistry, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ThrottledLoggingOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, OperationScope> _active = new();
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task? _sweeper;
    private readonly ILogger _selfLogger;

    private long _retiredEventsSubmitted;
    private long _retiredEventsLogged;
    private long _retiredEventsHeldBack;
    private long _retiredFailuresSubmitted;
    private long _retiredFailuresLogged;
    private long _retiredFailuresHeldBack;
    private long _retiredFlushesByCount;
    private long _retiredFlushesByTime;
    private long _retiredFlushesBySweeper;
    private long _completedOperations;
    private long _failedOperations;
    private int _disposed;

    /// <summary>Creates the logger for dependency injection.</summary>
    /// <param name="loggerFactory">Produces a logger per operation name, so operations can be filtered individually.</param>
    /// <param name="options">Process-wide settings.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OperationLogger(ILoggerFactory loggerFactory, IOptions<ThrottledLoggingOptions> options, TimeProvider timeProvider)
        : this(loggerFactory, (options ?? throw new ArgumentNullException(nameof(options))).Value, timeProvider)
    {
    }

    /// <summary>Creates the logger without dependency injection.</summary>
    /// <param name="loggerFactory">Produces a logger per operation name.</param>
    /// <param name="options">Process-wide settings; defaults are used when omitted.</param>
    /// <param name="timeProvider">The clock; the system clock is used when omitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    public OperationLogger(ILoggerFactory loggerFactory, ThrottledLoggingOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _loggerFactory = loggerFactory;
        _options = options ?? new ThrottledLoggingOptions();
        _time = timeProvider ?? TimeProvider.System;
        _selfLogger = loggerFactory.CreateLogger(typeof(OperationLogger).FullName!);

        _options.Defaults.Validate();

        if (_options.EnableSweeper)
        {
            // Started with flow suppressed so the sweeper's thread inherits nothing from whoever
            // happened to resolve this singleton first. Otherwise the first function invocation's
            // scope and Activity would ride along on every heartbeat of every operation that did
            // not capture its own context. Operations that did capture one are swept inside it.
            _sweeper = CleanContext.Start(() => SweepLoopAsync(_shutdown.Token));
        }
    }

    /// <inheritdoc />
    public OperationOptions DefaultOptions => _options.Defaults.Clone();

    /// <inheritdoc />
    public IOperationScope BeginOperation(string name, OperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        OperationOptions effective = (options ?? _options.Defaults).Clone();
        effective.Validate();

        ILogger logger = _loggers.GetOrAdd(name, static (key, factory) => factory.CreateLogger($"ThrottledLogging.{key}"), _loggerFactory);
        OperationScope scope = new(this, logger, name, effective, _time);

        // Registered before the entry line, so a snapshot taken from another thread the instant
        // that line appears already sees the operation.
        _active[scope.Id] = scope;
        try
        {
            scope.LogEntry();
        }
        catch
        {
            // The caller never receives the scope, so nothing could ever end it: take it back out
            // of the registry rather than leave it (and the context it captured) there for good.
            _active.TryRemove(scope.Id, out _);
            throw;
        }

        return scope;
    }

    /// <inheritdoc />
    public IReadOnlyList<OperationSnapshot> GetActiveOperations()
    {
        List<OperationSnapshot> snapshots = new(_active.Count);
        foreach (OperationScope scope in _active.Values)
        {
            if (!scope.HasEnded)
            {
                snapshots.Add(scope.Snapshot());
            }
        }

        return snapshots;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OperationSnapshot>> GetActiveOperationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IReadOnlyList<OperationSnapshot>>(GetActiveOperations());
    }

    /// <inheritdoc />
    public ThrottleCounters GetCounters()
    {
        long submitted = Interlocked.Read(ref _retiredEventsSubmitted);
        long logged = Interlocked.Read(ref _retiredEventsLogged);
        long held = Interlocked.Read(ref _retiredEventsHeldBack);
        long failSubmitted = Interlocked.Read(ref _retiredFailuresSubmitted);
        long failLogged = Interlocked.Read(ref _retiredFailuresLogged);
        long failHeld = Interlocked.Read(ref _retiredFailuresHeldBack);
        long byCount = Interlocked.Read(ref _retiredFlushesByCount);
        long byTime = Interlocked.Read(ref _retiredFlushesByTime);
        long bySweeper = Interlocked.Read(ref _retiredFlushesBySweeper);
        int active = 0;

        foreach (OperationScope scope in _active.Values)
        {
            active++;
            submitted += scope.Progress.Submitted;
            logged += scope.Progress.Emitted;
            held += scope.Progress.Skipped;
            failSubmitted += scope.Failures.Submitted;
            failLogged += scope.Failures.Emitted;
            failHeld += scope.Failures.Skipped;
            byCount += scope.Progress.FlushesByCount + scope.Failures.FlushesByCount;
            byTime += scope.Progress.FlushesByTime + scope.Failures.FlushesByTime;
            bySweeper += scope.Progress.FlushesBySweeper + scope.Failures.FlushesBySweeper;
        }

        return new ThrottleCounters
        {
            EventsSubmitted = submitted,
            EventsLogged = logged,
            EventsHeldBack = held,
            FailuresSubmitted = failSubmitted,
            FailuresLogged = failLogged,
            FailuresHeldBack = failHeld,
            FlushesByCount = byCount,
            FlushesByTime = byTime,
            FlushesBySweeper = bySweeper,
            ActiveOperations = active,
            CompletedOperations = Interlocked.Read(ref _completedOperations),
            FailedOperations = Interlocked.Read(ref _failedOperations),
        };
    }

    /// <summary>
    /// Stops the sweeper, then writes out every operation that is still running: its held events,
    /// followed by one line (event id 9008) saying it was still running and how far it had got.
    /// Operations already handed out keep working and can still be ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dependency injection container disposes this singleton when the host stops, which is the
    /// last moment the process is known to be alive. Without this, an Azure Functions recycle,
    /// a scale-in or a Kubernetes pod eviction would leave an operation's log ending at whatever
    /// happened to be its last throttled line, with the most recent events lost and no hint of
    /// why the run stopped.
    /// </para>
    /// <para>
    /// The container disposes in reverse order of creation, and this logger depends on the
    /// <see cref="ILoggerFactory"/>, so the factory and its providers are still alive here. Whether
    /// a provider then gets those lines off the machine before the process exits is up to the
    /// provider: Application Insights buffers, and needs its channel flushed on shutdown.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();

        bool sweeperStopped = true;
        try
        {
            sweeperStopped = _sweeper?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        }
        catch (AggregateException)
        {
            // The sweeper only ever ends by cancellation; nothing here is worth surfacing.
        }

        if (!sweeperStopped)
        {
            // A provider or an OnEmitted callback is blocking the sweeper mid-emission. The flush
            // below is still safe to run alongside it: each operation's sweep and shutdown flush
            // take the same lock, and a final flush waits for the channel rather than skipping. What
            // cannot be promised is that the sweeper's own line reaches its provider before the
            // host disposes that provider.
            IgnoreFailure(() => Log.SweeperDidNotStop(_selfLogger));
        }
        else
        {
            _shutdown.Dispose();
        }

        foreach (OperationScope scope in _active.Values)
        {
            try
            {
                scope.FlushForShutdown();
            }
#pragma warning disable CA1031 // One misbehaving logging provider must not stop the others being flushed, nor throw out of Dispose.
            catch (Exception error)
#pragma warning restore CA1031
            {
                // Reported through the same factory, which may be the thing that is failing, so
                // the report is best-effort too.
                IgnoreFailure(() => Log.ShutdownFlushFailed(_selfLogger, error, scope.Name, scope.Id));
            }
        }
    }

    /// <summary>Runs a diagnostic write that must never throw out of <see cref="Dispose"/>.</summary>
    /// <param name="write">The write.</param>
    private static void IgnoreFailure(Action write)
    {
        try
        {
            write();
        }
#pragma warning disable CA1031 // Nothing is left to report a failure to.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Deliberately empty.
        }
    }

    /// <summary>Runs one sweep across every active operation. Exposed so tests need no background timer.</summary>
    internal void SweepOnce()
    {
        foreach (OperationScope scope in _active.Values)
        {
            scope.Sweep();
        }
    }

    /// <summary>Hands an emitted event to the configured observer, if any.</summary>
    /// <param name="emitted">The event.</param>
    internal void NotifyEmitted(ThrottledEvent emitted)
    {
        Action<ThrottledEvent>? observer = _options.OnEmitted;
        if (observer is null)
        {
            return;
        }

        try
        {
            observer(emitted);
        }
#pragma warning disable CA1031 // An observer must never be able to break the caller's loop.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Log.ObserverThrew(_selfLogger, error);
        }
    }

    /// <summary>Removes a finished operation and folds its tallies into the retired totals.</summary>
    /// <param name="scope">The finished operation.</param>
    /// <param name="succeeded">Whether it ended successfully.</param>
    internal void Retire(OperationScope scope, bool succeeded)
    {
        _active.TryRemove(scope.Id, out _);

        Interlocked.Add(ref _retiredEventsSubmitted, scope.Progress.Submitted);
        Interlocked.Add(ref _retiredEventsLogged, scope.Progress.Emitted);
        Interlocked.Add(ref _retiredEventsHeldBack, scope.Progress.Skipped);
        Interlocked.Add(ref _retiredFailuresSubmitted, scope.Failures.Submitted);
        Interlocked.Add(ref _retiredFailuresLogged, scope.Failures.Emitted);
        Interlocked.Add(ref _retiredFailuresHeldBack, scope.Failures.Skipped);
        Interlocked.Add(ref _retiredFlushesByCount, scope.Progress.FlushesByCount + scope.Failures.FlushesByCount);
        Interlocked.Add(ref _retiredFlushesByTime, scope.Progress.FlushesByTime + scope.Failures.FlushesByTime);
        Interlocked.Add(ref _retiredFlushesBySweeper, scope.Progress.FlushesBySweeper + scope.Failures.FlushesBySweeper);

        if (succeeded)
        {
            Interlocked.Increment(ref _completedOperations);
        }
        else
        {
            Interlocked.Increment(ref _failedOperations);
        }
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(CurrentSweepInterval(), _time);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                SweepOnce();
                timer.Period = CurrentSweepInterval();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// A quarter of the tightest time threshold in play, so no held event waits much past its due
    /// time, clamped so the timer neither spins nor sleeps through a short threshold.
    /// </summary>
    private TimeSpan CurrentSweepInterval()
    {
        TimeSpan tightest = _options.Defaults.EveryInterval;
        if (_options.Defaults.FailureEveryInterval < tightest)
        {
            tightest = _options.Defaults.FailureEveryInterval;
        }

        foreach (OperationScope scope in _active.Values)
        {
            if (scope.TightestInterval < tightest)
            {
                tightest = scope.TightestInterval;
            }
        }

        TimeSpan quarter = tightest / 4;
        if (quarter < _options.MinimumSweepInterval)
        {
            return _options.MinimumSweepInterval;
        }

        return quarter > _options.MaximumSweepInterval ? _options.MaximumSweepInterval : quarter;
    }
}
