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
            _sweeper = Task.Run(() => SweepLoopAsync(_shutdown.Token));
        }
    }

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
        scope.LogEntry();
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

    /// <summary>Stops the sweeper. Operations already handed out keep working and can still be ended.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();

        try
        {
            _sweeper?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The sweeper only ever ends by cancellation; nothing here is worth surfacing.
        }

        _shutdown.Dispose();
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
