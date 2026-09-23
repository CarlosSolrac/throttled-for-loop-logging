using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ThrottledLogging.ReadmeAnimation;

/// <summary>
/// The loop the animation shows: a short import with a few failures and one item that hangs, run
/// through the real library on a fake clock so every line in the picture is one it really writes.
/// </summary>
internal static class Scenario
{
    /// <summary>The operation name, which is also the last part of the logger category.</summary>
    public const string OperationName = "ImportOrders";

    /// <summary>
    /// Progress thresholds, scaled down from the defaults of 500 events or 10 seconds so a loop short
    /// enough to watch still trips both of them.
    /// </summary>
    public const int EveryItems = 8;

    /// <inheritdoc cref="EveryItems"/>
    public static readonly TimeSpan EveryInterval = TimeSpan.FromSeconds(4);

    /// <summary>Failure thresholds, scaled down from the defaults of 50 events or 5 seconds.</summary>
    public const int FailureEveryItems = 3;

    /// <inheritdoc cref="FailureEveryItems"/>
    public static readonly TimeSpan FailureEveryInterval = TimeSpan.FromSeconds(2);

    /// <summary>When the operation starts, on the fake clock.</summary>
    public static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How far the fake clock moves per step. Small enough that the sweeper fires close to when it is due.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The script: how long each item takes and whether it succeeds. Durations vary a little so the ETA
    /// band has something to measure, and item 12 hangs long enough for two sweeper heartbeats.
    /// </summary>
    private static readonly (double Seconds, bool Succeeds)[] Script =
    [
        (0.30, true), (0.40, true), (0.35, true), (0.45, true), (0.30, true),
        (0.40, false), (0.35, true), (0.30, false), (0.45, false), (0.35, true),
        (0.40, false), (8.40, true), (0.30, true), (0.45, true), (0.35, true),
        (0.40, true), (0.30, true), (0.35, true),
    ];

    /// <summary>The pause between one item ending and the next starting, so the two events are seen apart.</summary>
    private static readonly TimeSpan BetweenItems = TimeSpan.FromMilliseconds(100);

    /// <summary>How many items the loop processes.</summary>
    public static int TotalItems => Script.Length;

    /// <summary>Runs the loop and returns what it submitted and what was written.</summary>
    /// <returns>The captured timeline.</returns>
    public static async Task<Timeline> RunAsync()
    {
        FakeTimeProvider time = new(Start);
        CapturingLoggerFactory loggers = new(time);
        List<Release> releases = [];
        List<Submission> submissions = [];
        List<ItemRun> items = [];

        ThrottledLoggingOptions options = new()
        {
            // The real sweeper, on the fake clock, is what writes the heartbeat for the hung item.
            EnableSweeper = true,
            MinimumSweepInterval = TimeSpan.FromMilliseconds(100),
            OnEmitted = emitted =>
            {
                lock (releases)
                {
                    releases.Add(new Release(Seconds(emitted.EmittedAtUtc), emitted.ItemLabel, emitted.Outcome, Seconds(emitted.SubmittedAtUtc), emitted.IsNew));
                }
            },
        };
        options.Defaults.TotalItems = Script.Length;
        options.Defaults.EveryItems = EveryItems;
        options.Defaults.EveryInterval = EveryInterval;
        options.Defaults.FailureEveryItems = FailureEveryItems;
        options.Defaults.FailureEveryInterval = FailureEveryInterval;

        using (OperationLogger operations = new(loggers, options, time))
        {
            using IOperationScope operation = operations.BeginOperation(OperationName);

            for (int index = 0; index < Script.Length; index++)
            {
                (double seconds, bool succeeds) = Script[index];
                int number = index + 1;
                string label = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"order-{number}");
                double startedAt = Seconds(time.GetUtcNow());

                using IItemScope item = operation.BeginItem(label);
                submissions.Add(new Submission(startedAt, number, label, ItemOutcome.Started));

                await AdvanceAsync(time, TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);

                double endedAt = Seconds(time.GetUtcNow());
                if (succeeds)
                {
                    item.Success();
                }
                else
                {
                    item.Failure(new TimeoutException($"{label} timed out"));
                }

                submissions.Add(new Submission(endedAt, number, label, succeeds ? ItemOutcome.Succeeded : ItemOutcome.Failed));
                items.Add(new ItemRun(number, label, startedAt, endedAt, succeeds));

                if (number < Script.Length)
                {
                    await AdvanceAsync(time, BetweenItems).ConfigureAwait(false);
                }
            }

            operation.Success();
        }

        lock (releases)
        {
            return new Timeline(items, submissions, [.. releases], loggers.Lines, Seconds(time.GetUtcNow()));
        }
    }

    /// <summary>Seconds between the start of the operation and <paramref name="at"/>.</summary>
    /// <param name="at">A time on the fake clock.</param>
    /// <returns>The offset in seconds.</returns>
    public static double Seconds(DateTimeOffset at) => (at - Start).TotalSeconds;

    /// <summary>
    /// Moves the fake clock forward in small steps, yielding after each one so the sweeper's timer
    /// callback runs before the next step rather than all at once at the end.
    /// </summary>
    /// <param name="time">The fake clock.</param>
    /// <param name="duration">How far to move it.</param>
    private static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan duration)
    {
        for (TimeSpan moved = TimeSpan.Zero; moved < duration; moved += Step)
        {
            time.Advance(Step);
            await Task.Delay(TimeSpan.FromMilliseconds(15)).ConfigureAwait(false);
        }
    }

    /// <summary>Hands every category a logger that records formatted lines against the fake clock.</summary>
    private sealed class CapturingLoggerFactory(TimeProvider time) : ILoggerFactory
    {
        private readonly List<LogLine> _lines = [];

        /// <summary>The clock every line is stamped with.</summary>
        public TimeProvider Time { get; } = time;

        /// <summary>Every line written so far, in order.</summary>
        public IReadOnlyList<LogLine> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        /// <inheritdoc />
        public void AddProvider(ILoggerProvider provider)
        {
            // One sink by construction.
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing to release.
        }

        /// <summary>Records one line.</summary>
        /// <param name="line">The line.</param>
        private void Add(LogLine line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        /// <summary>A logger for one category.</summary>
        private sealed class CapturingLogger(CapturingLoggerFactory owner, string category) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            /// <inheritdoc />
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner.Add(new LogLine(Seconds(owner.Time.GetUtcNow()), logLevel, category, eventId.Id, formatter(state, exception)));
                }
            }
        }
    }
}
