using Microsoft.Extensions.Time.Testing;
using ThrottledLogging.Internal;

namespace ThrottledLogging.Tests;

/// <summary>
/// R11 and R13: the running duration aggregate, and which items are allowed to influence the ETA.
/// </summary>
public sealed class EtaTests
{
    private static OperationOptions Options(long total, int halfLife = 50) => new()
    {
        TotalItems = total,
        EveryItems = 1_000_000,
        EveryInterval = TimeSpan.FromHours(1),
        FailureEveryItems = 1_000_000,
        FailureEveryInterval = TimeSpan.FromHours(1),
        EtaHalfLifeItems = halfLife,
    };

    [Fact]
    public void Eta_is_unavailable_before_the_minimum_sample_size()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 100));

        for (int i = 0; i < 3; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromSeconds(1));
        }

        Assert.False(operation.Snapshot().Eta.HasValue);
    }

    [Fact]
    public void Eta_is_unavailable_when_the_total_is_unknown()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions
        {
            EveryItems = 1_000_000,
            EveryInterval = TimeSpan.FromHours(1),
        });

        for (int i = 0; i < 20; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromSeconds(1));
        }

        Assert.False(operation.Snapshot().Eta.HasValue);
    }

    [Fact]
    public void Eta_counts_down_the_remaining_work()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 100));

        for (int i = 0; i < 10; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromSeconds(1));
        }

        EtaEstimate eta = operation.Snapshot().Eta;
        Assert.True(eta.HasValue);

        // Ninety items left at one second each.
        Assert.Equal(90, eta.Remaining!.Value.TotalSeconds, tolerance: 1.0);
        Assert.True(eta.RemainingLow <= eta.Remaining && eta.Remaining <= eta.RemainingHigh);
        Assert.Equal(0.80, eta.Confidence);
    }

    [Fact]
    public void Eta_band_tightens_as_the_loop_progresses()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 400, halfLife: 200));

        // Alternating durations so there is genuine spread for the band to reflect.
        for (int i = 0; i < 20; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromSeconds(i % 2 == 0 ? 1 : 3));
        }

        EtaEstimate early = operation.Snapshot().Eta;

        for (int i = 20; i < 350; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromSeconds(i % 2 == 0 ? 1 : 3));
        }

        EtaEstimate late = operation.Snapshot().Eta;

        TimeSpan earlyWidth = early.RemainingHigh!.Value - early.RemainingLow!.Value;
        TimeSpan lateWidth = late.RemainingHigh!.Value - late.RemainingLow!.Value;
        Assert.True(lateWidth < earlyWidth, $"the band should narrow with fewer items left, but went from {earlyWidth} to {lateWidth}");
    }

    [Fact]
    public void Failed_items_do_not_change_the_reported_mean_item_duration()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 100));

        for (int i = 0; i < 10; i++)
        {
            harness.RunItem(operation, $"ok-{i}", TimeSpan.FromSeconds(1));
            harness.RunItem(operation, $"bad-{i}", TimeSpan.FromSeconds(60), succeed: false);
        }

        OperationSnapshot snapshot = operation.Snapshot();

        // Descriptive statistics answer "how long does a successful item take", so a 60-second
        // timeout must not appear in them.
        Assert.Equal(1.0, snapshot.MeanItemDuration!.Value.TotalSeconds, tolerance: 0.01);
        Assert.Equal(60.0, snapshot.MeanFailureDuration!.Value.TotalSeconds, tolerance: 0.01);
    }

    [Fact]
    public void Failed_items_do_count_toward_the_throughput_rate()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 100));

        // Ten items in ten seconds, alternating outcome. Counting only successes would halve this.
        for (int i = 0; i < 5; i++)
        {
            harness.RunItem(operation, $"ok-{i}", TimeSpan.FromSeconds(1));
            harness.RunItem(operation, $"bad-{i}", TimeSpan.FromSeconds(1), succeed: false);
        }

        Assert.Equal(1.0, operation.Snapshot().RatePerSecond, tolerance: 0.05);
    }

    [Fact]
    public void Slow_failures_lengthen_the_eta()
    {
        TimeSpan allFast = EtaAfterMixedRun(failureDuration: TimeSpan.FromSeconds(1));
        TimeSpan slowFailures = EtaAfterMixedRun(failureDuration: TimeSpan.FromSeconds(60));

        // A timeout burns wall-clock exactly like a success does. Excluding it by outcome would
        // leave both runs predicting the same finish, which is the bias this guards.
        Assert.True(slowFailures > allFast * 5, $"slow failures should stretch the ETA, but {slowFailures} is not far above {allFast}");
    }

    [Fact]
    public void Fast_failures_shorten_the_eta()
    {
        TimeSpan allSameSpeed = EtaAfterMixedRun(failureDuration: TimeSpan.FromSeconds(1));
        TimeSpan failFast = EtaAfterMixedRun(failureDuration: TimeSpan.FromMilliseconds(1));

        // The mirror case: pretending fail-fast items cost a full second reads ~25% too slow.
        Assert.True(failFast < allSameSpeed, $"fail-fast items should shorten the ETA, but {failFast} is not below {allSameSpeed}");
    }

    [Fact]
    public void Eta_is_floored_by_the_longest_in_flight_item()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 1000));

        // Long enough to clear the ETA warm-up gate of five items and one second.
        for (int i = 0; i < 10; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(200));
        }

        TimeSpan before = operation.Snapshot().Eta.Remaining!.Value;

        // One item hangs. It completes nothing, so without the floor no statistic would notice.
        using IItemScope stuck = operation.BeginItem("order-hung");
        harness.Time.Advance(TimeSpan.FromHours(1));

        OperationSnapshot snapshot = operation.Snapshot();
        Assert.True(before < TimeSpan.FromHours(1));
        Assert.True(snapshot.Eta.Remaining >= TimeSpan.FromHours(1), $"a one-hour hang should floor the ETA, but it reads {snapshot.Eta.Remaining}");
        Assert.Equal(TimeSpan.FromHours(1), snapshot.LongestInFlight);
    }

    [Fact]
    public void Eta_tracks_a_step_change_in_item_duration()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 1000, halfLife: 5));

        for (int i = 0; i < 40; i++)
        {
            harness.RunItem(operation, $"fast-{i}", TimeSpan.FromMilliseconds(100));
        }

        TimeSpan whileFast = operation.Snapshot().Eta.Remaining!.Value;

        for (int i = 0; i < 40; i++)
        {
            harness.RunItem(operation, $"slow-{i}", TimeSpan.FromSeconds(5));
        }

        TimeSpan whileSlow = operation.Snapshot().Eta.Remaining!.Value;

        // An all-time mean would still be anchored to the fast phase; the EWMA has moved on.
        Assert.True(whileSlow > whileFast * 10, $"the estimate should follow the slowdown, but went from {whileFast} to {whileSlow}");
    }

    [Fact]
    public void The_typical_item_resists_an_outlier_that_the_mean_does_not()
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 100));

        for (int i = 0; i < 4; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromSeconds(1));
        }

        harness.RunItem(operation, "order-slow", TimeSpan.FromSeconds(100));

        OperationSnapshot snapshot = operation.Snapshot();

        // Arithmetic mean (1+1+1+1+100)/5 = 20.8s; geometric mean 100^0.2 ~= 2.51s.
        Assert.Equal(20.8, snapshot.MeanItemDuration!.Value.TotalSeconds, tolerance: 0.1);
        Assert.Equal(2.51, snapshot.TypicalItemDuration!.Value.TotalSeconds, tolerance: 0.05);
    }

    [Fact]
    public void A_single_pathological_duration_does_not_blow_open_the_variance()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        DurationStatistics statistics = new(halfLifeItems: 5, time);

        for (int i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            statistics.Record(TimeSpan.FromMilliseconds(100), succeeded: true);
        }

        time.Advance(TimeSpan.FromHours(1));
        statistics.Record(TimeSpan.FromHours(1), succeeded: true);

        // Unclamped, a 3600-second deviation would push the variance past a million. Clamped at a
        // few standard deviations it stays usable, so the band does not gape open for the next
        // fifty items.
        Assert.True(statistics.Snapshot().VarianceSeconds < 1.0, $"variance was {statistics.Snapshot().VarianceSeconds}");
    }

    private static TimeSpan EtaAfterMixedRun(TimeSpan failureDuration)
    {
        using TestHarness harness = new();
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", Options(total: 1000, halfLife: 10));

        for (int i = 0; i < 40; i++)
        {
            harness.RunItem(operation, $"ok-{i}", TimeSpan.FromSeconds(1));
            harness.RunItem(operation, $"bad-{i}", failureDuration, succeed: false);
        }

        return operation.Snapshot().Eta.Remaining!.Value;
    }
}
