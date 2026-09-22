using ThrottledLogging.Internal;

namespace ThrottledLogging.Tests;

/// <summary>R9: many threads submitting at once must lose nothing and double-count nothing.</summary>
public sealed class ConcurrencyTests
{
    private const int Threads = 8;
    private const int ItemsPerThread = 500;

    [Fact]
    public void Concurrent_submitters_never_lose_a_count()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 37,
            EveryInterval = TimeSpan.FromHours(1),
            FailureEveryItems = 11,
            FailureEveryInterval = TimeSpan.FromHours(1),
        });

        RunInParallel(harness, operation);

        OperationSnapshot snapshot = operation.Snapshot();
        Assert.Equal(Threads * ItemsPerThread * 3 / 4, snapshot.Processed);
        Assert.Equal(Threads * ItemsPerThread / 4, snapshot.Failed);
        Assert.Equal(0, snapshot.InFlight);
    }

    [Fact]
    public void Every_submitted_event_is_either_written_or_counted_as_skipped()
    {
        using TestHarness harness = new();
        OperationScope operation = (OperationScope)harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 37,
            EveryInterval = TimeSpan.FromHours(1),
            FailureEveryItems = 11,
            FailureEveryInterval = TimeSpan.FromHours(1),
        });

        RunInParallel(harness, operation);
        operation.Success();

        // The sequence arithmetic accounts for every event exactly once: it was written, or the
        // gap between two written sequences counted it as skipped. Nothing falls between.
        Assert.Equal(operation.Progress.Submitted, operation.Progress.Emitted + operation.Progress.Skipped);
        Assert.Equal(operation.Failures.Submitted, operation.Failures.Emitted + operation.Failures.Skipped);
    }

    [Fact]
    public void Each_written_event_is_written_exactly_once()
    {
        using TestHarness harness = new();
        OperationScope operation = (OperationScope)harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 23,
            EveryInterval = TimeSpan.FromHours(1),
            FailureEveryItems = 7,
            FailureEveryInterval = TimeSpan.FromHours(1),
        });

        RunInParallel(harness, operation);
        operation.Success();

        // Under contention only one thread passes the emission gate, so no line can appear twice.
        List<(string? Label, ItemOutcome Outcome)> written = [.. harness.Emitted.Where(e => e.IsNew).Select(e => (e.ItemLabel, e.Outcome))];
        Assert.Equal(written.Count, written.Distinct().Count());
    }

    [Fact]
    public async Task Counters_stay_consistent_while_snapshots_are_taken_from_another_thread()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            TotalItems = Threads * ItemsPerThread,
            EveryItems = 50,
            EveryInterval = TimeSpan.FromHours(1),
        });

        using CancellationTokenSource reading = new();
        Task reader = Task.Run(
            () =>
            {
                while (!reading.IsCancellationRequested)
                {
                    foreach (OperationSnapshot snapshot in harness.Logger.GetActiveOperations())
                    {
                        Assert.True(snapshot.Processed + snapshot.Failed <= Threads * ItemsPerThread);
                        Assert.True(snapshot.Pending >= 0);
                    }
                }
            },
            TestContext.Current.CancellationToken);

        RunInParallel(harness, operation);
        await reading.CancelAsync();
        await reader;

        Assert.Equal(Threads * ItemsPerThread, operation.Snapshot().Processed + operation.Snapshot().Failed);
    }

    private static void RunInParallel(TestHarness harness, IOperationScope operation)
    {
        _ = harness;
        Parallel.For(0, Threads, thread =>
        {
            for (int i = 0; i < ItemsPerThread; i++)
            {
                using IItemScope item = operation.BeginItem($"t{thread}-i{i}");
                if (i % 4 == 3)
                {
                    item.Failure(new InvalidOperationException($"t{thread}-i{i}"));
                }
                else
                {
                    item.Success();
                }
            }
        });
    }
}
