namespace ThrottledLogging;

/// <summary>
/// A point-in-time view of one operation. Safe to take from any thread while the operation runs.
/// </summary>
/// <remarks>
/// A reference type rather than a struct: it carries around twenty members, and snapshots are
/// normally handed out in lists, so copying would cost more than the allocation saves.
/// </remarks>
public sealed record OperationSnapshot
{
    /// <summary>Identifies this operation across logs and processes.</summary>
    public required Guid Id { get; init; }

    /// <summary>The caller-supplied name, for example <c>ImportOrders</c>.</summary>
    public required string Name { get; init; }

    /// <summary>When the operation began, in UTC.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>How long the operation has been running.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Items that completed successfully.</summary>
    public required long Processed { get; init; }

    /// <summary>Items that failed.</summary>
    public required long Failed { get; init; }

    /// <summary>The expected item count, or <see langword="null"/> when the caller did not supply one.</summary>
    public required long? Total { get; init; }

    /// <summary>
    /// Items not yet finished, that is <see cref="Total"/> less <see cref="Processed"/> and
    /// <see cref="Failed"/>. A failed item is finished, so it is not pending.
    /// </summary>
    public required long? Pending { get; init; }

    /// <summary>Item scopes open at this instant.</summary>
    public required int InFlight { get; init; }

    /// <summary>Completions per second, counting successes and failures alike.</summary>
    public required double RatePerSecond { get; init; }

    /// <summary>The share of completed items that failed, from zero to one.</summary>
    public required double FailureRate { get; init; }

    /// <summary>Arithmetic mean duration of a <em>successful</em> item. Descriptive; it does not feed the ETA.</summary>
    public required TimeSpan? MeanItemDuration { get; init; }

    /// <summary>
    /// Geometric mean duration of a successful item — the "typical" item, resistant to outliers.
    /// Descriptive; it does not feed the ETA.
    /// </summary>
    public required TimeSpan? TypicalItemDuration { get; init; }

    /// <summary>Arithmetic mean duration of a failed item, so the mix behind the ETA is visible.</summary>
    public required TimeSpan? MeanFailureDuration { get; init; }

    /// <summary>How long the oldest still-open item has been running.</summary>
    public required TimeSpan? LongestInFlight { get; init; }

    /// <summary>The predicted remaining time and its band.</summary>
    public required EtaEstimate Eta { get; init; }

    /// <summary>The label of the most recent item seen on the progress channel.</summary>
    public required string? LastItemLabel { get; init; }

    /// <summary>When the most recent progress event was submitted, in UTC.</summary>
    public required DateTimeOffset? LastEventSubmittedAtUtc { get; init; }

    /// <summary>The label of the most recent failed item.</summary>
    public required string? LastFailureLabel { get; init; }

    /// <summary>When the most recent failure was submitted, in UTC.</summary>
    public required DateTimeOffset? LastFailureAtUtc { get; init; }

    /// <summary>
    /// Failure counts by exception type name, bounded; everything beyond the cap is collected
    /// under a single <c>(other)</c> key.
    /// </summary>
    public required IReadOnlyDictionary<string, long> FailuresByType { get; init; }
}
