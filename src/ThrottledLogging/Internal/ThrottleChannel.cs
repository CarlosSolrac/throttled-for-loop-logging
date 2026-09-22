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

        Interlocked.Exchange(ref _pending, pending);
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

        return TryEmit(FlushReason.Sweeper);
    }

    /// <summary>Writes whatever is still held, regardless of thresholds, when the operation ends.</summary>
    /// <returns>The emission, or <see langword="null"/> when nothing is held or it has already been written.</returns>
    public Emission? Flush()
    {
        PendingEvent? pending = Volatile.Read(ref _pending);
        if (pending is null || pending.Sequence == Interlocked.Read(ref _lastEmittedSequence))
        {
            return null;
        }

        return TryEmit(FlushReason.Final);
    }

    private bool HasIntervalElapsed() => _time.GetElapsedTime(Interlocked.Read(ref _lastEmittedAt)) >= _everyInterval;

    /// <summary>
    /// Publishes the held event. Exactly one thread passes the gate; everyone else returns at once
    /// rather than queueing, because a duplicate line is worse than a missed trigger that the next
    /// submission or sweep will catch anyway.
    /// </summary>
    private Emission? TryEmit(FlushReason reason)
    {
        if (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
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

            long lastSequence = Interlocked.Read(ref _lastEmittedSequence);
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
