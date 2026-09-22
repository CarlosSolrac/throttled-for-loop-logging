using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace ThrottledLogging.Tests;

/// <summary>
/// Wires an <see cref="OperationLogger"/> to a captured log, a controllable clock and a list of
/// everything that survived throttling, so tests can assert on structure rather than on log text.
/// </summary>
internal sealed class TestHarness : IDisposable
{
    private readonly List<ThrottledEvent> _emitted = [];
    private readonly Lock _gate = new();

    public TestHarness(Action<ThrottledLoggingOptions>? configure = null)
    {
        Collector = new FakeLogCollector();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        ThrottledLoggingOptions options = new()
        {
            // Tests drive the sweeper explicitly through SweepOnce so nothing depends on timing.
            EnableSweeper = false,
            OnEmitted = Record,
        };
        configure?.Invoke(options);

        Logger = new OperationLogger(new CollectingLoggerFactory(Collector), options, Time);
    }

    public FakeLogCollector Collector { get; }

    public FakeTimeProvider Time { get; }

    public OperationLogger Logger { get; }

    /// <summary>Every event that reached the log, in order.</summary>
    public IReadOnlyList<ThrottledEvent> Emitted
    {
        get
        {
            lock (_gate)
            {
                return [.. _emitted];
            }
        }
    }

    public IReadOnlyList<ThrottledEvent> EmittedOfOutcome(ItemOutcome outcome)
        => [.. Emitted.Where(e => e.Outcome == outcome)];

    /// <summary>Events on the progress channel: started and succeeded.</summary>
    public IReadOnlyList<ThrottledEvent> Progress
        => [.. Emitted.Where(e => e.Outcome != ItemOutcome.Failed)];

    /// <summary>Events on the failure channel.</summary>
    public IReadOnlyList<ThrottledEvent> Failures
        => [.. Emitted.Where(e => e.Outcome == ItemOutcome.Failed)];

    public IReadOnlyList<FakeLogRecord> Records => Collector.GetSnapshot();

    /// <summary>Runs one item that takes <paramref name="duration"/> on the fake clock.</summary>
    public void RunItem(IOperationScope operation, string label, TimeSpan duration, bool succeed = true, Exception? error = null)
    {
        using IItemScope item = operation.BeginItem(label);
        Time.Advance(duration);
        if (succeed)
        {
            item.Success();
        }
        else
        {
            item.Failure(error ?? new InvalidOperationException($"{label} failed"));
        }
    }

    public void Dispose() => Logger.Dispose();

    private void Record(ThrottledEvent emitted)
    {
        lock (_gate)
        {
            _emitted.Add(emitted);
        }
    }
}

/// <summary>
/// The smallest factory that hands every category to one collector. Avoids depending on
/// Microsoft.Extensions.Logging proper just to build a factory.
/// </summary>
internal sealed class CollectingLoggerFactory : ILoggerFactory
{
    private readonly FakeLogCollector _collector;

    public CollectingLoggerFactory(FakeLogCollector collector) => _collector = collector;

    public ILogger CreateLogger(string categoryName) => new FakeLogger(_collector, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
        // Nothing to do: this factory has exactly one sink by construction.
    }

    public void Dispose()
    {
        // Nothing to dispose.
    }
}
