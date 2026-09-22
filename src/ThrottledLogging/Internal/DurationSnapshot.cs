namespace ThrottledLogging.Internal;

/// <summary>
/// A consistent read of <see cref="DurationStatistics"/>, taken under its lock so the members
/// cannot disagree with one another.
/// </summary>
/// <param name="Completions">Items that finished, successfully or not.</param>
/// <param name="Successes">Items that finished successfully.</param>
/// <param name="Failures">Items that failed.</param>
/// <param name="RatePerSecond">Completions per second, drift-tracking.</param>
/// <param name="MeanGapSeconds">Mean wall-clock gap between completions; the reciprocal of the rate.</param>
/// <param name="VarianceSeconds">Exponentially weighted variance of item duration, with outliers clamped.</param>
/// <param name="Concurrency">Items apparently being worked on at once, from Little's law.</param>
/// <param name="MeanSuccessSeconds">Arithmetic mean duration of a successful item; descriptive only.</param>
/// <param name="TypicalSuccessSeconds">Geometric mean duration of a successful item; descriptive only.</param>
/// <param name="MeanFailureSeconds">Arithmetic mean duration of a failed item; descriptive only.</param>
internal readonly record struct DurationSnapshot(
    long Completions,
    long Successes,
    long Failures,
    double RatePerSecond,
    double MeanGapSeconds,
    double VarianceSeconds,
    double Concurrency,
    double? MeanSuccessSeconds,
    double? TypicalSuccessSeconds,
    double? MeanFailureSeconds);
