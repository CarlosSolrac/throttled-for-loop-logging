namespace ThrottledLogging;

/// <summary>
/// A prediction of how much longer an operation will take, with an uncertainty band.
/// </summary>
/// <remarks>
/// Every member is <see langword="null"/> until the operation knows its total item count and
/// has completed enough items to estimate from. See the design document, section 6.
/// </remarks>
public readonly record struct EtaEstimate
{
    /// <summary>An estimate with no data behind it yet.</summary>
    public static EtaEstimate Unknown => default;

    /// <summary>The central estimate of remaining time, or <see langword="null"/> when unknown.</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>The optimistic end of the band.</summary>
    public TimeSpan? RemainingLow { get; init; }

    /// <summary>The pessimistic end of the band.</summary>
    public TimeSpan? RemainingHigh { get; init; }

    /// <summary>When the operation is expected to finish, in UTC.</summary>
    public DateTimeOffset? ExpectedCompletionUtc { get; init; }

    /// <summary>
    /// The two-sided confidence the band was computed at, for example <c>0.80</c>. Zero when
    /// there is no estimate.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>Whether an estimate is available at all.</summary>
    public bool HasValue => Remaining.HasValue;
}
