namespace ThrottledLogging.Internal;

/// <summary>
/// An event offered to a <see cref="ThrottleChannel"/>. Immutable, so it can be published to other
/// threads with a single reference exchange and read without tearing.
/// </summary>
internal sealed class PendingEvent
{
    /// <summary>Position in this channel's submission order. Drives <see cref="ThrottledEvent.IsNew"/>.</summary>
    public required long Sequence { get; init; }

    /// <summary>The caller's label for the item, if any.</summary>
    public required string? Label { get; init; }

    /// <summary>What happened to the item.</summary>
    public required ItemOutcome Outcome { get; init; }

    /// <summary>The exception, for a failure.</summary>
    public required Exception? Error { get; init; }

    /// <summary>When the caller submitted the event, in UTC.</summary>
    public required DateTimeOffset SubmittedAtUtc { get; init; }
}
