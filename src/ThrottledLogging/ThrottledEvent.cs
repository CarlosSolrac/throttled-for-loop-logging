namespace ThrottledLogging;

/// <summary>
/// One event as it reaches the log, after throttling. Held events are emitted later than they
/// were submitted, so the two timestamps are both recorded and are not interchangeable.
/// </summary>
public sealed record ThrottledEvent
{
    /// <summary>The operation this event belongs to.</summary>
    public required string OperationName { get; init; }

    /// <summary>The operation's identifier.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>The caller-supplied label for the item, if any.</summary>
    public required string? ItemLabel { get; init; }

    /// <summary>What happened to the item.</summary>
    public required ItemOutcome Outcome { get; init; }

    /// <summary>When the caller submitted this event, in UTC. This is when the thing happened.</summary>
    public required DateTimeOffset SubmittedAtUtc { get; init; }

    /// <summary>When the event reached the log, in UTC. Later than <see cref="SubmittedAtUtc"/> for a held event.</summary>
    public required DateTimeOffset EmittedAtUtc { get; init; }

    /// <summary>
    /// Whether anything was submitted on this channel since the previous emission. False means the
    /// same held event is being re-emitted, so the loop has not moved.
    /// </summary>
    public required bool IsNew { get; init; }

    /// <summary>How many events on this channel were held back and skipped since the previous emission.</summary>
    public required long SuppressedSince { get; init; }

    /// <summary>The exception, when <see cref="Outcome"/> is <see cref="ItemOutcome.Failed"/>.</summary>
    public required Exception? Error { get; init; }

    /// <summary>Progress and ETA at the moment of emission.</summary>
    public required OperationSnapshot Progress { get; init; }
}
