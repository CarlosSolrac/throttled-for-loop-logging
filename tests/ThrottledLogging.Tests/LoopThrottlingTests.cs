namespace ThrottledLogging.Tests;

/// <summary>R2 and R4 to R8 through the public API, where one item produces two progress events.</summary>
public sealed class LoopThrottlingTests
{
    [Fact]
    public void First_item_in_the_loop_is_always_logged()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 1_000_000,
            EveryInterval = TimeSpan.FromHours(1),
        });

        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));

        ThrottledEvent emitted = Assert.Single(harness.Progress);
        Assert.Equal(ItemOutcome.Started, emitted.Outcome);
        Assert.Equal("order-1", emitted.ItemLabel);
    }

    [Fact]
    public void Later_items_are_held_rather_than_logged()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 1_000_000,
            EveryInterval = TimeSpan.FromHours(1),
        });

        for (int i = 1; i <= 50; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(10));
        }

        // 100 progress events offered, exactly one written.
        Assert.Single(harness.Progress);
    }

    [Fact]
    public void Held_event_is_written_once_the_count_threshold_is_reached()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 10,
            EveryInterval = TimeSpan.FromHours(1),
        });

        for (int i = 1; i <= 20; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(10));
        }

        // 40 progress events, threshold 10: lines at events 1, 11, 21 and 31.
        Assert.Equal(4, harness.Progress.Count);
        Assert.All(harness.Progress.Skip(1), e => Assert.True(e.SuppressedSince > 0));
    }

    [Fact]
    public void Held_event_is_written_once_the_time_threshold_elapses()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 1_000_000,
            EveryInterval = TimeSpan.FromSeconds(30),
        });

        harness.RunItem(operation, "order-1", TimeSpan.FromSeconds(1));
        int afterFirst = harness.Progress.Count;

        harness.RunItem(operation, "order-2", TimeSpan.FromSeconds(60));

        Assert.Equal(1, afterFirst);
        Assert.True(harness.Progress.Count > afterFirst, "the time threshold should have released the held event");
    }

    [Fact]
    public void Written_event_carries_the_time_it_was_submitted_not_the_time_it_reached_the_log()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 1_000_000,
            EveryInterval = TimeSpan.FromSeconds(10),
        });

        harness.RunItem(operation, "order-1", TimeSpan.Zero);
        harness.RunItem(operation, "order-2", TimeSpan.Zero);
        DateTimeOffset submittedAt = harness.Time.GetUtcNow();

        // Nothing is submitted for a minute; the sweeper is what eventually releases the held event.
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        harness.Logger.SweepOnce();

        ThrottledEvent released = harness.Progress[^1];
        Assert.Equal(submittedAt, released.SubmittedAtUtc);
        Assert.Equal(submittedAt.AddMinutes(1), released.EmittedAtUtc);
        Assert.True(released.SubmittedAtUtc < released.EmittedAtUtc, "a held event reaches the log after the thing it describes happened");
    }

    [Fact]
    public void A_stalled_item_produces_a_heartbeat_that_reports_nothing_new()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 1_000_000,
            EveryInterval = TimeSpan.FromSeconds(10),
        });

        using IItemScope stuck = operation.BeginItem("order-1");
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Logger.SweepOnce();

        Assert.Equal(2, harness.Progress.Count);
        ThrottledEvent heartbeat = harness.Progress[1];
        Assert.False(heartbeat.IsNew);
        Assert.Equal("order-1", heartbeat.ItemLabel);
        Assert.Equal(ItemOutcome.Started, heartbeat.Outcome);
    }

    [Fact]
    public async Task ForEachAsync_records_an_outcome_for_every_element()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions { TotalItems = 6 });

        await operation.ForEachAsync(
            Enumerable.Range(1, 6),
            (i, _) => i % 3 == 0 ? throw new InvalidOperationException($"item {i}") : ValueTask.CompletedTask,
            label: static i => $"order-{i}",
            cancellationToken: TestContext.Current.CancellationToken);

        OperationSnapshot snapshot = operation.Snapshot();
        Assert.Equal(4, snapshot.Processed);
        Assert.Equal(2, snapshot.Failed);
        Assert.Equal(0, snapshot.Pending);
    }
}
