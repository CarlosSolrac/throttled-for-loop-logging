namespace ThrottledLogging.Tests;

/// <summary>R10 and R12: the counter report, and the live view of everything in flight.</summary>
public sealed class CountersAndRegistryTests
{
    private static OperationOptions Options(long? total = null) => new()
    {
        TotalItems = total,
        EveryItems = 10,
        EveryInterval = TimeSpan.FromHours(1),
        FailureEveryItems = 5,
        FailureEveryInterval = TimeSpan.FromHours(1),
    };

    [Fact]
    public void Counters_report_submitted_written_and_held_back()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options());

        for (int i = 0; i < 100; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(1));
        }

        ThrottleCounters counters = harness.Logger.GetCounters();

        Assert.Equal(200, counters.EventsSubmitted);
        Assert.True(counters.EventsHeldBack > counters.EventsLogged, "throttling is supposed to hold back far more than it writes");
        Assert.True(counters.FlushesByCount > 0);
        Assert.Equal(1, counters.ActiveOperations);

        // Events still being held are submitted but not yet classified, so the three only balance
        // once the operation has flushed what it was holding.
        Assert.True(counters.EventsLogged + counters.EventsHeldBack <= counters.EventsSubmitted);

        operation.Success();
        ThrottleCounters final = harness.Logger.GetCounters();
        Assert.Equal(final.EventsSubmitted, final.EventsLogged + final.EventsHeldBack);
    }

    [Fact]
    public void Counters_separate_the_failure_channel_from_progress()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options());

        for (int i = 0; i < 40; i++)
        {
            harness.RunItem(operation, $"bad-{i}", TimeSpan.FromMilliseconds(1), succeed: false);
        }

        operation.Success();
        ThrottleCounters counters = harness.Logger.GetCounters();

        Assert.Equal(40, counters.FailuresSubmitted);
        Assert.Equal(counters.FailuresSubmitted, counters.FailuresLogged + counters.FailuresHeldBack);

        // Started events still land on the progress channel; nothing succeeded.
        Assert.Equal(40, counters.EventsSubmitted);
        Assert.Equal(counters.EventsSubmitted, counters.EventsLogged + counters.EventsHeldBack);
    }

    [Fact]
    public void Counters_survive_the_operations_they_came_from()
    {
        using TestHarness harness = new();

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options()))
        {
            for (int i = 0; i < 50; i++)
            {
                harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(1));
            }

            operation.Success();
        }

        ThrottleCounters counters = harness.Logger.GetCounters();

        Assert.Equal(0, counters.ActiveOperations);
        Assert.Equal(1, counters.CompletedOperations);
        Assert.Equal(100, counters.EventsSubmitted);
        Assert.Equal(counters.EventsSubmitted, counters.EventsLogged + counters.EventsHeldBack);
    }

    [Fact]
    public void Counters_tell_completed_operations_from_failed_ones()
    {
        using TestHarness harness = new();

        using (IOperationScope good = harness.Logger.BeginOperation("Good", Options()))
        {
            good.Success();
        }

        using (IOperationScope bad = harness.Logger.BeginOperation("Bad", Options()))
        {
            bad.Failure(new InvalidOperationException("no"));
        }

        using (harness.Logger.BeginOperation("Abandoned", Options()))
        {
        }

        ThrottleCounters counters = harness.Logger.GetCounters();
        Assert.Equal(1, counters.CompletedOperations);
        Assert.Equal(2, counters.FailedOperations);
    }

    [Fact]
    public void GetActiveOperations_lists_every_operation_in_flight()
    {
        using TestHarness harness = new();

        using IOperationScope first = harness.Logger.BeginOperation("ImportOrders", Options(total: 100));
        using IOperationScope second = harness.Logger.BeginOperation("RebuildIndex", Options(total: 50));
        using IOperationScope third = harness.Logger.BeginOperation("SyncCustomers", Options(total: 25));

        harness.RunItem(first, "order-1", TimeSpan.FromSeconds(1));
        harness.RunItem(second, "index-1", TimeSpan.FromSeconds(1), succeed: false);

        IReadOnlyList<OperationSnapshot> active = harness.Logger.GetActiveOperations();

        Assert.Equal(3, active.Count);
        Assert.Equal(["ImportOrders", "RebuildIndex", "SyncCustomers"], active.Select(o => o.Name).Order(StringComparer.Ordinal));

        OperationSnapshot orders = active.Single(o => o.Name == "ImportOrders");
        Assert.Equal(1, orders.Processed);
        Assert.Equal(0, orders.Failed);
        Assert.Equal(99, orders.Pending);

        OperationSnapshot index = active.Single(o => o.Name == "RebuildIndex");
        Assert.Equal(0, index.Processed);
        Assert.Equal(1, index.Failed);
        Assert.Equal(49, index.Pending);

        Assert.Equal(25, active.Single(o => o.Name == "SyncCustomers").Pending);
    }

    [Fact]
    public async Task GetActiveOperationsAsync_returns_the_same_view()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 10));

        IReadOnlyList<OperationSnapshot> active = await harness.Logger.GetActiveOperationsAsync(TestContext.Current.CancellationToken);

        OperationSnapshot only = Assert.Single(active);
        Assert.Equal("ImportOrders", only.Name);
    }

    [Fact]
    public void A_finished_operation_drops_out_of_the_active_list()
    {
        using TestHarness harness = new();

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options()))
        {
            Assert.Single(harness.Logger.GetActiveOperations());
            operation.Success();
        }

        Assert.Empty(harness.Logger.GetActiveOperations());
    }

    [Fact]
    public void Snapshot_reports_items_currently_open()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 10));

        using IItemScope a = operation.BeginItem("a");
        using IItemScope b = operation.BeginItem("b");
        using IItemScope c = operation.BeginItem("c");

        Assert.Equal(3, operation.Snapshot().InFlight);

        a.Success();
        Assert.Equal(2, operation.Snapshot().InFlight);
    }
}
