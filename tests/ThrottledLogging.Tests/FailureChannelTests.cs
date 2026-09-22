using Microsoft.Extensions.Logging.Testing;
using ThrottledLogging.Internal;

namespace ThrottledLogging.Tests;

/// <summary>R14: failures throttle on their own channel, and are accounted for in aggregate.</summary>
public sealed class FailureChannelTests
{
    private static OperationOptions Quiet(int failureEvery = 1_000_000) => new()
    {
        EveryItems = 1_000_000,
        EveryInterval = TimeSpan.FromHours(1),
        FailureEveryItems = failureEvery,
        FailureEveryInterval = TimeSpan.FromHours(1),
    };

    [Fact]
    public void First_failure_is_always_logged_immediately()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Quiet());

        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(5), succeed: false);

        ThrottledEvent failure = Assert.Single(harness.Failures);
        Assert.Equal("order-1", failure.ItemLabel);
        Assert.True(failure.IsNew);
    }

    [Fact]
    public void Later_failures_are_held_rather_than_logged()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Quiet());

        for (int i = 1; i <= 250; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(1), succeed: false);
        }

        // 250 failures, one line. The point of R14.
        Assert.Single(harness.Failures);
    }

    [Fact]
    public void Failure_thresholds_are_independent_of_the_progress_channel()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 1_000_000,          // progress effectively silent
            EveryInterval = TimeSpan.FromHours(1),
            FailureEveryItems = 5,           // failures still speak up
            FailureEveryInterval = TimeSpan.FromHours(1),
        });

        // A flood of successes must not consume the failure channel's budget.
        for (int i = 1; i <= 500; i++)
        {
            harness.RunItem(operation, $"ok-{i}", TimeSpan.FromMilliseconds(1));
        }

        for (int i = 1; i <= 20; i++)
        {
            harness.RunItem(operation, $"bad-{i}", TimeSpan.FromMilliseconds(1), succeed: false);
        }

        Assert.Single(harness.Progress);
        Assert.Equal(4, harness.Failures.Count);
    }

    [Fact]
    public void A_flood_of_failures_does_not_crowd_out_progress()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 10,
            EveryInterval = TimeSpan.FromHours(1),
            FailureEveryItems = 1_000_000,
            FailureEveryInterval = TimeSpan.FromHours(1),
        });

        for (int i = 1; i <= 100; i++)
        {
            harness.RunItem(operation, $"bad-{i}", TimeSpan.FromMilliseconds(1), succeed: false);
            harness.RunItem(operation, $"ok-{i}", TimeSpan.FromMilliseconds(1));
        }

        Assert.Single(harness.Failures);
        Assert.True(harness.Progress.Count > 1, "progress must still be reported while failures pour in");
    }

    [Fact]
    public void Final_summary_reports_failure_counts_by_exception_type()
    {
        using TestHarness harness = new();

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Quiet()))
        {
            for (int i = 0; i < 7; i++)
            {
                harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(1), succeed: false, error: new TimeoutException());
            }

            for (int i = 0; i < 2; i++)
            {
                harness.RunItem(operation, $"other-{i}", TimeSpan.FromMilliseconds(1), succeed: false, error: new InvalidOperationException());
            }

            operation.Success();
        }

        FakeLogRecord summary = Assert.Single(harness.Records, r => r.Id == 9006);
        Assert.Contains("TimeoutException 7", summary.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException 2", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Last_held_failure_is_flushed_when_the_operation_ends()
    {
        using TestHarness harness = new();

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Quiet()))
        {
            harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(1), succeed: false);
            harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(1), succeed: false);
            harness.RunItem(operation, "order-last", TimeSpan.FromMilliseconds(1), succeed: false);
            operation.Success();
        }

        // The first failure went out at once; the run must not end hiding its most recent one.
        Assert.Equal(2, harness.Failures.Count);
        Assert.Equal("order-last", harness.Failures[^1].ItemLabel);
    }

    [Fact]
    public void Failure_type_breakdown_is_capped_and_buckets_the_remainder()
    {
        FailureBreakdown breakdown = new(cap: 3);

        breakdown.Record(new TimeoutException());
        breakdown.Record(new InvalidOperationException());
        breakdown.Record(new ArgumentException());
        breakdown.Record(new FormatException());
        breakdown.Record(new NotSupportedException());

        IReadOnlyDictionary<string, long> counts = breakdown.Snapshot();
        Assert.Equal(4, counts.Count);
        Assert.Equal(2, counts[FailureBreakdown.OtherKey]);
    }

    [Fact]
    public void An_item_disposed_without_an_outcome_counts_as_a_failure()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Quiet());

        try
        {
            using IItemScope item = operation.BeginItem("order-1");
            throw new InvalidOperationException("unwinds past the using");
        }
        catch (InvalidOperationException)
        {
            // The point is what the scope recorded on the way out.
        }

        OperationSnapshot snapshot = operation.Snapshot();
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(0, snapshot.Processed);
        Assert.Equal(1, snapshot.FailuresByType[FailureBreakdown.UnrecordedKey]);
    }
}
