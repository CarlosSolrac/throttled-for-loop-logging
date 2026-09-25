using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, OperationScope> _active = new();
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly ILogger _selfLogger;
    private readonly IDisposable? _reloadSubscription;

    // Guards starting and stopping the sweeper, which a reload can do at any time.
    private readonly object _sweeperGate = new();

    // Orders registration against Dispose: an operation is either registered before Dispose
    // marks the logger disposed, and so flushed by it, or refused with ObjectDisposedException.
    private readonly object _registryGate = new();

    // Replaced whole when configuration reloads; read with Volatile.Read through Current.
    private Settings _settings;

    // The running sweeper's stop signal, or null when it is off. _sweeper is the most recently
    // started loop, which may still be finishing after being stopped.
    private CancellationTokenSource? _sweeperStop;
    private Task? _sweeper;

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

    /// <summary>
    /// How long <see cref="Dispose"/> waits, in total, for operations whose lock another thread is
    /// holding (usually because a logging provider is slow or hung) before giving up on them.
    /// </summary>
    internal TimeSpan ShutdownLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates the logger for dependency injection, following configuration as it changes. This is
    /// the constructor <see cref="ThrottledLoggingServiceCollectionExtensions.AddThrottledLogging(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{ThrottledLoggingOptions})"/> uses.
    /// </summary>
    /// <param name="loggerFactory">Produces a logger per operation name, so operations can be filtered individually.</param>
    /// <param name="options">Process-wide settings, and notice of their changes.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The initial settings are invalid.</exception>
    /// <remarks>
    /// A change applies to operations begun after it; each running operation keeps the settings it
    /// began with. A change that fails validation is logged (event id 9011) and ignored, leaving
    /// the last good settings in force.
    /// </remarks>
    public OperationLogger(ILoggerFactory loggerFactory, IOptionsMonitor<ThrottledLoggingOptions> options, TimeProvider timeProvider)
        : this(loggerFactory, (options ?? throw new ArgumentNullException(nameof(options))).CurrentValue, timeProvider)
    {
        // The monitor reports changes to named instances too; only the unnamed one configures this.
        _reloadSubscription = options.OnChange((changed, name) =>
        {
            if (string.IsNullOrEmpty(name))
            {
                Reload(changed);
            }
        });

        // Catches a change that landed between reading CurrentValue above and subscribing.
        Reload(options.CurrentValue);
    }

    /// <summary>Creates the logger for dependency injection, with settings fixed at startup.</summary>
    /// <param name="loggerFactory">Produces a logger per operation name, so operations can be filtered individually.</param>
    /// <param name="options">Process-wide settings.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are invalid.</exception>
    public OperationLogger(ILoggerFactory loggerFactory, IOptions<ThrottledLoggingOptions> options, TimeProvider timeProvider)
        : this(loggerFactory, (options ?? throw new ArgumentNullException(nameof(options))).Value, timeProvider)
    {
    }

    /// <summary>Creates the logger without dependency injection.</summary>
    /// <param name="loggerFactory">Produces a logger per operation name.</param>
    /// <param name="options">
    /// Process-wide settings; defaults are used when omitted. They are copied here, so changing
    /// this object afterwards has no effect.
    /// </param>
    /// <param name="timeProvider">The clock; the system clock is used when omitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are invalid.</exception>
    public OperationLogger(ILoggerFactory loggerFactory, ThrottledLoggingOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _loggerFactory = loggerFactory;
        _time = timeProvider ?? TimeProvider.System;
        _selfLogger = loggerFactory.CreateLogger(typeof(OperationLogger).FullName!);
        _settings = Settings.From(options ?? new ThrottledLoggingOptions());

        if (_settings.EnableSweeper)
        {
            StartSweeper();
        }
    }

    /// <inheritdoc />
    public OperationOptions DefaultOptions => Current.Defaults.Clone();

    /// <summary>Whether the background sweeper is on. Exposed for tests.</summary>
    internal bool IsSweeperRunning
    {
        get
        {
            lock (_sweeperGate)
            {
                return _sweeperStop is not null;
            }
        }
    }

    private Settings Current => Volatile.Read(ref _settings);

    /// <inheritdoc />
    public IOperationScope BeginOperation(string name, OperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        // A cheap early refusal; the authoritative check is under the registry lock below.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        OperationOptions effective = (options ?? Current.Defaults).Clone();
        effective.Validate();

        ILogger logger = _loggers.GetOrAdd(name, static (key, factory) => factory.CreateLogger($"ThrottledLogging.{key}"), _loggerFactory);
        OperationScope scope = new(this, logger, name, effective, _time);

        // Registered before the entry line, so a snapshot taken from another thread the instant
        // that line appears already sees the operation. The check and the registration happen under
        // the lock Dispose marks the logger disposed under, so an operation begun while Dispose runs
        // is either refused or registered in time to be flushed. The operation's own lifecycle lock
        // is taken before the registry lock is released and held until the entry line is written,
        // so a shutdown flush never writes "still running" ahead of "started".
        lock (_registryGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _active[scope.Id] = scope;
            scope.EnterForEntry();
        }

        try
        {
            scope.LogEntry();
        }
        catch
        {
            // The caller never receives the scope, so nothing could ever end it: take it back out
            // of the registry rather than leave it (and the context it captured) there for good.
            // Abandoning it also tells a shutdown flush waiting on it that there is nothing to write.
            _active.TryRemove(scope.Id, out _);
            scope.Abandon();
            throw;
        }
        finally
        {
            scope.ExitAfterEntry();
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
        lock (_registryGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
        }

        // First, so no reload can start a sweeper after the one below has been stopped. Reload
        // also checks _disposed under the sweeper gate, which covers a reload already under way.
        IgnoreFailure(() => _reloadSubscription?.Dispose());

        Task? sweeper;
        CancellationTokenSource? stop;
        lock (_sweeperGate)
        {
            sweeper = _sweeper;
            stop = _sweeperStop;
            _sweeperStop = null;
            stop?.Cancel();
        }

        bool sweeperStopped = true;
        try
        {
            // A loop stopped by an earlier reload is awaited by any loop started after it, so the
            // most recent one finishing means they all have.
            sweeperStopped = sweeper?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        }
        catch (AggregateException)
        {
            // The sweeper only ever ends by cancellation; nothing here is worth surfacing.
        }

        if (!sweeperStopped)
        {
            // A provider or an OnEmitted callback is blocking the sweeper mid-emission. The flush
            // below still runs: each operation's sweep and shutdown flush take the same lock, so they
            // cannot interleave, and FlushAtShutdown gives up on an operation whose lock stays held
            // rather than hang behind it. What cannot be promised is that the sweeper's own line
            // reaches its provider before the host disposes that provider.
            IgnoreFailure(() => Log.SweeperDidNotStop(_selfLogger));
        }
        else
        {
            stop?.Dispose();
        }

        FlushAtShutdown();
    }

    /// <summary>
    /// Flushes every registered operation, without letting one whose lock another thread holds
    /// delay the rest or hang shutdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first pass flushes every operation whose lock is free right now and sets the busy ones
    /// aside. The second waits for those, sharing one <see cref="ShutdownLockTimeout"/> budget
    /// between them, so a hung provider costs the whole shutdown that long at most, not that long
    /// per operation. An operation still busy after that is reported as event 9009 and skipped.
    /// </para>
    /// <para>
    /// Nothing is registered after this starts: Dispose marked the logger disposed under the
    /// registry lock, so the enumeration below sees every operation it has to.
    /// </para>
    /// <para>
    /// This bounds only the wait for another thread. A provider that hangs while this thread is
    /// writing to it hangs here too; no synchronous call can be abandoned part way through.
    /// </para>
    /// </remarks>
    private void FlushAtShutdown()
    {
        List<OperationScope> busy = [];
        foreach (OperationScope scope in _active.Values)
        {
            if (!TryFlushAtShutdown(scope, TimeSpan.Zero))
            {
                busy.Add(scope);
            }
        }

        if (busy.Count == 0)
        {
            return;
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(ShutdownLockTimeout.TotalSeconds * Stopwatch.Frequency);
        foreach (OperationScope scope in busy)
        {
            TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            if (!TryFlushAtShutdown(scope, remaining))
            {
                TimeoutException timeout = new($"Another thread held the operation for longer than {ShutdownLockTimeout.TotalSeconds:N1}s, most likely a logging provider that is not returning; its held events were not written.");
                IgnoreFailure(() => Log.ShutdownFlushFailed(_selfLogger, timeout, scope.Name, scope.Id));
            }
        }
    }

    /// <summary>Flushes one operation at shutdown, reporting rather than throwing any failure.</summary>
    /// <param name="scope">The operation.</param>
    /// <param name="lockTimeout">How long to wait for its lock.</param>
    /// <returns><see langword="false"/> only when its lock could not be taken in time.</returns>
    private bool TryFlushAtShutdown(OperationScope scope, TimeSpan lockTimeout)
    {
        try
        {
            return scope.FlushForShutdown(lockTimeout);
        }
#pragma warning disable CA1031 // One misbehaving logging provider must not stop the others being flushed, nor throw out of Dispose.
        catch (Exception error)
#pragma warning restore CA1031
        {
            // Reported through the same factory, which may be the thing that is failing, so
            // the report is best-effort too.
            IgnoreFailure(() => Log.ShutdownFlushFailed(_selfLogger, error, scope.Name, scope.Id));
            return true;
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
    /// <remarks>
    /// A logging provider that throws while one operation's held line is written must not stop the
    /// sweep for the others, nor fault the background loop: an unhandled exception there would end
    /// the loop for good and leave every later heartbeat unwritten. The failure is reported as
    /// event 9012 and the sweep moves on. The line that failed is not retried; the operation's next
    /// held event is swept as usual.
    /// </remarks>
    internal void SweepOnce()
    {
        foreach (OperationScope scope in _active.Values)
        {
            try
            {
                scope.Sweep();
            }
#pragma warning disable CA1031 // A provider may throw anything; the sweeper must survive it.
            catch (Exception error)
#pragma warning restore CA1031
            {
                // Reported through the same factory, which may be the thing that is failing.
                IgnoreFailure(() => Log.SweepFailed(_selfLogger, error, scope.Name, scope.Id));
            }
        }
    }

    /// <summary>Hands an emitted event to the configured observer, if any.</summary>
    /// <param name="emitted">The event.</param>
    internal void NotifyEmitted(ThrottledEvent emitted)
    {
        Action<ThrottledEvent>? observer = Current.OnEmitted;
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

    /// <summary>
    /// Applies reloaded settings: validated as a whole, then swapped in at once, so no reader ever
    /// sees half of one version and half of another. Invalid settings are logged and ignored.
    /// Runs on whatever thread raised the change, so it never waits on the sweeper.
    /// </summary>
    /// <param name="options">The new settings.</param>
    private void Reload(ThrottledLoggingOptions options)
    {
        Settings next;
        try
        {
            next = Settings.From(options);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            IgnoreFailure(() => Log.SettingsReloadRejected(_selfLogger, error));
            return;
        }

        lock (_sweeperGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _settings, next);
            if (next.EnableSweeper)
            {
                StartSweeper();
            }
            else
            {
                StopSweeper();
            }
        }
    }

    /// <summary>Starts the sweeper unless it is already on. Called from the constructor, or under <see cref="_sweeperGate"/>.</summary>
    private void StartSweeper()
    {
        if (_sweeperStop is not null)
        {
            return;
        }

        CancellationTokenSource stop = new();
        Task? previous = _sweeper;
        _sweeperStop = stop;

        // Started with flow suppressed so the sweeper's thread inherits nothing from whoever
        // happened to resolve this singleton first, or raised the reload. Otherwise the first
        // function invocation's scope and Activity would ride along on every heartbeat of every
        // operation that did not capture its own context. Operations that did capture one are
        // swept inside it.
        _sweeper = CleanContext.Start(() => SweepLoopAsync(previous, stop.Token));
    }

    /// <summary>Signals the sweeper to stop without waiting for it. Called under <see cref="_sweeperGate"/>.</summary>
    private void StopSweeper()
    {
        if (_sweeperStop is not { } stop)
        {
            return;
        }

        _sweeperStop = null;
        stop.Cancel();

        // Disposed once the loop has let go of its token, which may be after a sweep in progress.
        _sweeper?.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), stop, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <param name="previous">A loop stopped by an earlier reload, which may still be mid-sweep.</param>
    /// <param name="cancellationToken">Stops this loop.</param>
    private async Task SweepLoopAsync(Task? previous, CancellationToken cancellationToken)
    {
        // Two loops sweeping at once could each write the same heartbeat, so a loop switched back
        // on waits for the one switched off to finish its last sweep.
        if (previous is not null)
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

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
    /// time, clamped so the timer neither spins nor sleeps through a short threshold. Recomputed on
    /// every tick, so reloaded intervals take effect from the next one.
    /// </summary>
    private TimeSpan CurrentSweepInterval()
    {
        Settings settings = Current;
        TimeSpan tightest = settings.Defaults.EveryInterval;
        if (settings.Defaults.FailureEveryInterval < tightest)
        {
            tightest = settings.Defaults.FailureEveryInterval;
        }

        foreach (OperationScope scope in _active.Values)
        {
            if (scope.TightestInterval < tightest)
            {
                tightest = scope.TightestInterval;
            }
        }

        TimeSpan quarter = tightest / 4;
        if (quarter < settings.MinimumSweepInterval)
        {
            return settings.MinimumSweepInterval;
        }

        return quarter > settings.MaximumSweepInterval ? settings.MaximumSweepInterval : quarter;
    }

    /// <summary>
    /// A validated, private copy of <see cref="ThrottledLoggingOptions"/>. Never changed once built:
    /// a reload builds a new one and swaps it in, and nothing the caller does to the options object
    /// afterwards reaches it.
    /// </summary>
    private sealed class Settings
    {
        private static readonly TimeSpan ShortestTimerPeriod = TimeSpan.FromMilliseconds(1);
        private static readonly TimeSpan LongestTimerPeriod = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        private Settings(OperationOptions defaults, Action<ThrottledEvent>? onEmitted, bool enableSweeper, TimeSpan minimumSweepInterval, TimeSpan maximumSweepInterval)
        {
            Defaults = defaults;
            OnEmitted = onEmitted;
            EnableSweeper = enableSweeper;
            MinimumSweepInterval = minimumSweepInterval;
            MaximumSweepInterval = maximumSweepInterval;
        }

        /// <summary>Validated; handed out only as clones.</summary>
        public OperationOptions Defaults { get; }

        public Action<ThrottledEvent>? OnEmitted { get; }

        public bool EnableSweeper { get; }

        public TimeSpan MinimumSweepInterval { get; }

        public TimeSpan MaximumSweepInterval { get; }

        /// <summary>Copies and validates <paramref name="options"/>.</summary>
        /// <param name="options">The settings.</param>
        /// <returns>The copy.</returns>
        /// <exception cref="ArgumentException">A setting is missing or out of range.</exception>
        /// <exception cref="InvalidOperationException">Configuration held a value that could not be converted.</exception>
        public static Settings From(ThrottledLoggingOptions options)
        {
            if (options.BindingError is { } bindingError)
            {
                ExceptionDispatchInfo.Throw(bindingError);
            }

            if (options.Defaults is null)
            {
                throw new ArgumentNullException(nameof(options), "Defaults must not be null.");
            }

            OperationOptions defaults = options.Defaults.Clone();
            defaults.Validate();

            // The sweeper's PeriodicTimer accepts nothing outside this range; a period outside it
            // would throw on the sweeper's thread and stop it for good.
            if (options.MinimumSweepInterval < ShortestTimerPeriod)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.MinimumSweepInterval, "MinimumSweepInterval must be at least one millisecond.");
            }

            if (options.MaximumSweepInterval < options.MinimumSweepInterval)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.MaximumSweepInterval, "MaximumSweepInterval must not be less than MinimumSweepInterval.");
            }

            if (options.MaximumSweepInterval > LongestTimerPeriod)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.MaximumSweepInterval, "MaximumSweepInterval must not exceed about 49.7 days.");
            }

            return new Settings(defaults, options.OnEmitted, options.EnableSweeper, options.MinimumSweepInterval, options.MaximumSweepInterval);
        }
    }
}
