using Microsoft.Extensions.Time.Testing;
using ThrottledLogging.Internal;

namespace ThrottledLogging.Tests;

/// <summary>
/// R4 to R8 at the level of a single channel, where the state machine can be exercised exactly.
/// </summary>
public sealed class ThrottleChannelTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (ThrottleChannel Channel, FakeTimeProvider Time) Build(int everyItems = 1000, int everySeconds = 3600)
    {
        FakeTimeProvider time = new(Origin);
        return (new ThrottleChannel(everyItems, TimeSpan.FromSeconds(everySeconds), time), time);
    }

    [Fact]
    public void First_event_is_always_logged()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build();

        Emission? emission = channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());

        Assert.NotNull(emission);
        Assert.Equal(FlushReason.First, emission.Value.Reason);
        Assert.True(emission.Value.IsNew);
        Assert.Equal(0, emission.Value.SuppressedSince);
    }

    [Fact]
    public void Sweeper_overtaken_by_a_submitter_does_not_repeat_the_event_the_submitter_just_wrote()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everySeconds: 10);
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());      // first: written
        channel.Submit("order-1", ItemOutcome.Succeeded, null, time.GetUtcNow());    // held
        time.Advance(TimeSpan.FromSeconds(11));

        // Between the sweeper's checks and its turn at the gate, the loop finishes another item and
        // writes it itself on the time threshold, which resets the interval.
        Emission? written = null;
        channel.AfterSweeperCheck = () => written = channel.Submit("order-2", ItemOutcome.Succeeded, null, time.GetUtcNow());

        Emission? swept = channel.TryFlushDueToTime();

        Assert.NotNull(written);
        Assert.Null(swept);
        Assert.Equal(2, channel.Emitted);
    }

    [Fact]
    public void Second_event_is_held_and_not_logged()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build();
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());

        Emission? emission = channel.Submit("order-2", ItemOutcome.Started, null, time.GetUtcNow());

        Assert.Null(emission);
        Assert.Equal("order-2", channel.Latest?.Label);
        Assert.Equal(2, channel.Submitted);
        Assert.Equal(1, channel.Emitted);
    }

    [Fact]
    public void Held_event_is_logged_once_the_count_threshold_is_reached()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everyItems: 4);
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());

        Assert.Null(channel.Submit("order-2", ItemOutcome.Started, null, time.GetUtcNow()));
        Assert.Null(channel.Submit("order-3", ItemOutcome.Started, null, time.GetUtcNow()));
        Assert.Null(channel.Submit("order-4", ItemOutcome.Started, null, time.GetUtcNow()));
        Emission? emission = channel.Submit("order-5", ItemOutcome.Started, null, time.GetUtcNow());

        Assert.NotNull(emission);
        Assert.Equal(FlushReason.Count, emission.Value.Reason);

        // The newest event is what goes out, not the one that happened to trip the threshold first.
        Assert.Equal("order-5", emission.Value.Event.Label);

        // Events 2, 3 and 4 never appeared individually.
        Assert.Equal(3, emission.Value.SuppressedSince);
    }

    [Fact]
    public void Held_event_is_logged_once_the_time_threshold_elapses()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everyItems: 1_000_000, everySeconds: 10);
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());
        Assert.Null(channel.Submit("order-2", ItemOutcome.Started, null, time.GetUtcNow()));

        time.Advance(TimeSpan.FromSeconds(10));
        Emission? emission = channel.Submit("order-3", ItemOutcome.Started, null, time.GetUtcNow());

        Assert.NotNull(emission);
        Assert.Equal(FlushReason.Time, emission.Value.Reason);
        Assert.Equal("order-3", emission.Value.Event.Label);
        Assert.Equal(1, emission.Value.SuppressedSince);
    }

    [Fact]
    public void Logged_event_carries_the_submit_time_not_the_emit_time()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everyItems: 2);
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());

        time.Advance(TimeSpan.FromMinutes(5));
        DateTimeOffset submittedAt = time.GetUtcNow();
        Assert.Null(channel.Submit("order-2", ItemOutcome.Started, null, submittedAt));

        time.Advance(TimeSpan.FromMinutes(5));
        Emission? emission = channel.Submit("order-3", ItemOutcome.Started, null, time.GetUtcNow());

        // order-3 superseded order-2, so its own submit time is what must be reported: ten minutes
        // after the origin, not five.
        Assert.NotNull(emission);
        Assert.Equal(Origin.AddMinutes(10), emission.Value.Event.SubmittedAtUtc);
    }

    [Fact]
    public void IsNew_is_false_when_the_same_held_event_is_re_emitted()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everySeconds: 10);

        // A Started event with nothing after it: the loop is stuck mid-item.
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(10));

        Emission? heartbeat = channel.TryFlushDueToTime();

        Assert.NotNull(heartbeat);
        Assert.False(heartbeat.Value.IsNew);
        Assert.Equal(0, heartbeat.Value.SuppressedSince);
        Assert.Equal("order-1", heartbeat.Value.Event.Label);
    }

    [Fact]
    public void IsNew_is_true_when_something_arrived_since_the_last_line()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everySeconds: 10);
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());
        channel.Submit("order-2", ItemOutcome.Started, null, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(10));

        Emission? emission = channel.TryFlushDueToTime();

        Assert.NotNull(emission);
        Assert.True(emission.Value.IsNew);
        Assert.Equal("order-2", emission.Value.Event.Label);
    }

    [Fact]
    public void A_finished_item_is_not_re_announced_by_the_sweeper()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everySeconds: 10);
        channel.Submit("order-1", ItemOutcome.Succeeded, null, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(60));

        // Nothing is stuck, so repeating the last completion forever would be pure noise.
        Assert.Null(channel.TryFlushDueToTime());
    }

    [Fact]
    public void Flush_releases_whatever_is_still_held()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build();
        channel.Submit("order-1", ItemOutcome.Started, null, time.GetUtcNow());
        channel.Submit("order-2", ItemOutcome.Succeeded, null, time.GetUtcNow());

        Emission? emission = channel.Flush();

        Assert.NotNull(emission);
        Assert.Equal(FlushReason.Final, emission.Value.Reason);
        Assert.Equal("order-2", emission.Value.Event.Label);
        Assert.Null(channel.Flush());
    }

    [Fact]
    public void Skipped_counts_every_event_that_never_appeared_individually()
    {
        (ThrottleChannel channel, FakeTimeProvider time) = Build(everyItems: 10);
        for (int i = 0; i < 25; i++)
        {
            channel.Submit($"order-{i}", ItemOutcome.Started, null, time.GetUtcNow());
        }

        channel.Flush();

        // 25 submitted, and every one that was not itself written is counted as skipped.
        Assert.Equal(25, channel.Submitted);
        Assert.Equal(25, channel.Emitted + channel.Skipped);
    }
}
