namespace ThrottledLogging.Internal;

/// <summary>
/// One independent throttling channel: always writes the first event, then holds the most recent
/// one and writes it when a count or time threshold is reached.
/// </summary>
/// <remarks>
/// <para>
/// An operation owns two of these, one for progress and one for failures, with no state in common,
/// so a flood on either cannot crowd the other out. See the design document, section 4.4.
/// </para>
/// <para>
/// The suppressed path costs one increment, one reference exchange and a pair of comparisons. It
/// allocates one <see cref="PendingEvent"/> per submission and never formats a message, which is
/// where the saving over logging every event comes from.
/// </para>
/// </remarks>
internal sealed class ThrottleChannel
{
    private readonly int _everyItems;
    private readonly TimeSpan _everyInterval;
    private readonly TimeProvider _time;

    private PendingEvent? _pending;
    private long _sequence;
    private long _lastEmittedSequence;
    private long _lastEmittedAt;
    private long _sinceLastEmit;
    private long _submitted;
    private long _emitted;
    private long _skipped;
    private long _flushesByCount;
    private long _flushesByTime;
    private long _flushesBySweeper;
    private int _gate;

    /// <summary>Creates a channel.</summary>
    /// <param name="everyItems">Write the held event once this many events have been submitted since the last write.</param>
    /// <param name="everyInterval">Write the held event once this long has passed since the last write.</param>
    /// <param name="time">Clock, injected so thresholds are testable without sleeping.</param>
    public ThrottleChannel(int everyItems, TimeSpan everyInterval, TimeProvider time)
    {
        _everyItems = everyItems;
        _everyInterval = everyInterval;
        _time = time;
        _lastEmittedAt = time.GetTimestamp();
    }

    /// <summary>Events offered to this channel.</summary>
    public long Submitted => Interlocked.Read(ref _submitted);

    /// <summary>Log lines this channel produced.</summary>
    public long Emitted => Interlocked.Read(ref _emitted);

    /// <summary>Events that were superseded before they could be written, and so never appeared individually.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>Writes triggered by the count threshold.</summary>
    public long FlushesByCount => Interlocked.Read(ref _flushesByCount);

    /// <summary>Writes triggered by the time threshold during a submission.</summary>
    public long FlushesByTime => Interlocked.Read(ref _flushesByTime);

    /// <summary>Writes triggered by the background sweeper.</summary>
    public long FlushesBySweeper => Interlocked.Read(ref _flushesBySweeper);

    /// <summary>The most recently submitted event, whether or not it has been written.</summary>
    public PendingEvent? Latest => Volatile.Read(ref _pending);

    /// <summary>
    /// Offers an event. Returns the emission when this call causes a write, otherwise
    /// <see langword="null"/> because the event is now held.
    /// </summary>
    /// <param name="label">The item label.</param>
    /// <param name="outcome">What happened to the item.</param>
    /// <param name="error">The exception, for a failure.</param>
    /// <param name="submittedAtUtc">When the caller submitted the event.</param>
    /// <returns>The emission, or <see langword="null"/>.</returns>
    public Emission? Submit(string? label, ItemOutcome outcome, Exception? error, DateTimeOffset submittedAtUtc)
    {
        PendingEvent pending = new()
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Label = label,
            Outcome = outcome,
            Error = error,
            SubmittedAtUtc = submittedAtUtc,
        };

        Publish(pending);
        Interlocked.Increment(ref _submitted);

        long since = Interlocked.Increment(ref _sinceLastEmit);

        // The first event on a channel is never held (R4). Afterwards, either threshold releases
        // whatever is currently held, which need not be the event that tripped it.
        FlushReason reason;
        if (Interlocked.Read(ref _emitted) == 0)
        {
            reason = FlushReason.First;
        }
        else if (since >= _everyItems)
        {
            reason = FlushReason.Count;
        }
        else if (HasIntervalElapsed())
        {
            reason = FlushReason.Time;
        }
        else
        {
            return null;
        }

        return TryEmit(reason);
    }

    /// <summary>Test seam: runs between the sweeper's checks and its turn at the gate.</summary>
    internal Action? AfterSweeperCheck { get; set; }

    /// <summary>
    /// Writes the held event if its time threshold has elapsed. Called by the background sweeper so
    /// that a loop which has gone quiet still produces a heartbeat.
    /// </summary>
    /// <returns>The emission, or <see langword="null"/> when nothing is due.</returns>
    public Emission? TryFlushDueToTime()
    {
        if (Interlocked.Read(ref _emitted) == 0 || !HasIntervalElapsed())
        {
            return null;
        }

        PendingEvent? pending = Volatile.Read(ref _pending);
        if (pending is null)
        {
            return null;
        }

        // Re-announcing an unchanged event is only worth a line while an item is still open: that
        // is a loop stuck mid-item. Repeating a finished item forever would just be noise.
        bool wouldBeNew = pending.Sequence != Interlocked.Read(ref _lastEmittedSequence);
        if (!wouldBeNew && pending.Outcome != ItemOutcome.Started)
        {
            return null;
        }

        AfterSweeperCheck?.Invoke();
        return TryEmit(FlushReason.Sweeper);
    }

    /// <summary>
    /// Writes whatever is still held, regardless of thresholds: when the operation ends, and when the
    /// logger shuts down while it is still running.
    /// </summary>
    /// <returns>The emission, or <see langword="null"/> when nothing is held or it has already been written.</returns>
    /// <remarks>
    /// Unlike the other paths, this one waits for the gate rather than giving up when another thread
    /// holds it. A final flush that skipped would lose the newest event for good, since nothing
    /// comes after it to catch up. The gate is only ever held for a few field writes, never across
    /// logging, so the wait is short. The "already written" check is repeated inside the gate, so
    /// two final flushes racing each other (the caller ending the operation while the logger shuts
    /// down) write the event once, not twice.
    /// </remarks>
    public Emission? Flush()
    {
        PendingEvent? pending = Volatile.Read(ref _pending);
        if (pending is null || pending.Sequence == Interlocked.Read(ref _lastEmittedSequence))
        {
            return null;
        }

        return TryEmit(FlushReason.Final);
    }

    /// <summary>
    /// Makes <paramref name="pending"/> the held event, unless a later one is already held.
    /// </summary>
    /// <param name="pending">The event just submitted.</param>
    /// <remarks>
    /// Two threads can take sequences 5 and 6 and then publish in the opposite order. A plain
    /// exchange would leave 5 held and lose 6 for good: never written, and not counted as skipped
    /// either, because skipped is the gap between written sequences and 6 lies above every one of
    /// them. Publishing only forwards keeps "hold the latest" true under contention. A stale event
    /// that loses here is not lost from the counts; it sits inside the gap the next write accounts for.
    /// </remarks>
    private void Publish(PendingEvent pending)
    {
        PendingEvent? current = Volatile.Read(ref _pending);
        while (current is null || current.Sequence < pending.Sequence)
        {
            PendingEvent? observed = Interlocked.CompareExchange(ref _pending, pending, current);
            if (ReferenceEquals(observed, current))
            {
                return;
            }

            current = observed;
        }
    }

    private bool HasIntervalElapsed() => _time.GetElapsedTime(Interlocked.Read(ref _lastEmittedAt)) >= _everyInterval;

    /// <summary>
    /// Publishes the held event. Exactly one thread passes the gate; everyone else returns at once
    /// rather than queueing, because a duplicate line is worse than a missed trigger that the next
    /// submission or sweep will catch anyway. The final flush is the exception: it waits its turn,
    /// see <see cref="Flush"/>.
    /// </summary>
    private Emission? TryEmit(FlushReason reason)
    {
        if (reason == FlushReason.Final)
        {
            SpinWait spinner = default;
            while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
            {
                spinner.SpinOnce();
            }
        }
        else if (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            PendingEvent? pending = Volatile.Read(ref _pending);
            if (pending is null)
            {
                return null;
            }

            // Only the sweeper may write an event that has already been written: that repeat is the
            // stalled-loop heartbeat, and says so with IsNew=false. Every other reason writes only
            // something new. Checked here, under the gate, because the threshold or "already
            // written" check the caller made before reaching the gate can be overtaken by another
            // thread's emission in between. Without it two submitters that both saw the count
            // threshold would write the same event twice, and the second write would be counted as
            // one more emission than there were events.
            long lastSequence = Interlocked.Read(ref _lastEmittedSequence);
            if (reason == FlushReason.Sweeper)
            {
                // The sweeper's own checks ran before the gate too. A submitter may have written a
                // newer event since, resetting the interval; repeating that event now would be a
                // heartbeat for a loop that is not stalled at all.
                if (!HasIntervalElapsed() || (pending.Sequence == lastSequence && pending.Outcome != ItemOutcome.Started))
                {
                    return null;
                }
            }
            else if (pending.Sequence == lastSequence)
            {
                return null;
            }

            bool isNew = pending.Sequence != lastSequence;
            long suppressed = isNew ? Math.Max(0, pending.Sequence - lastSequence - 1) : 0;

            Interlocked.Exchange(ref _lastEmittedSequence, pending.Sequence);
            Interlocked.Exchange(ref _lastEmittedAt, _time.GetTimestamp());
            Interlocked.Exchange(ref _sinceLastEmit, 0);
            Interlocked.Increment(ref _emitted);
            Interlocked.Add(ref _skipped, suppressed);

            switch (reason)
            {
                case FlushReason.Count:
                    Interlocked.Increment(ref _flushesByCount);
                    break;
                case FlushReason.Time:
                    Interlocked.Increment(ref _flushesByTime);
                    break;
                case FlushReason.Sweeper:
                    Interlocked.Increment(ref _flushesBySweeper);
                    break;
                case FlushReason.First:
                case FlushReason.Final:
                default:
                    break;
            }

            return new Emission(pending, isNew, suppressed, reason);
        }
        finally
        {
            Volatile.Write(ref _gate, 0);
        }
    }
}
