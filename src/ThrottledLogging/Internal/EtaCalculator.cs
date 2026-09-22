namespace ThrottledLogging.Internal;

/// <summary>
/// Turns the running statistics into a predicted finish time and an uncertainty band.
/// </summary>
/// <remarks>
/// The central estimate is <c>remaining / rate</c> rather than <c>remaining x meanDuration</c>.
/// Estimating from observed throughput is parallelism-correct by construction: with several workers
/// the per-item durations overlap, so multiplying by them overstates the remaining wall-clock by
/// roughly the worker count, whereas the completion rate already reflects however many are running.
/// See the design document, section 6.
/// </remarks>
internal static class EtaCalculator
{
    /// <summary>Computes the estimate.</summary>
    /// <param name="total">Expected item count, or <see langword="null"/> when unknown.</param>
    /// <param name="processed">Items that succeeded.</param>
    /// <param name="failed">Items that failed.</param>
    /// <param name="elapsed">How long the operation has been running.</param>
    /// <param name="longestInFlight">Age of the oldest still-open item.</param>
    /// <param name="stats">The running statistics.</param>
    /// <param name="options">Confidence and warm-up settings.</param>
    /// <param name="nowUtc">Current time, for the expected completion instant.</param>
    /// <returns>The estimate, or <see cref="EtaEstimate.Unknown"/>.</returns>
    public static EtaEstimate Compute(
        long? total,
        long processed,
        long failed,
        TimeSpan elapsed,
        TimeSpan? longestInFlight,
        in DurationSnapshot stats,
        OperationOptions options,
        DateTimeOffset nowUtc)
    {
        if (total is not { } expected)
        {
            return EtaEstimate.Unknown;
        }

        long remaining = expected - processed - failed;
        if (remaining <= 0)
        {
            return new EtaEstimate
            {
                Remaining = TimeSpan.Zero,
                RemainingLow = TimeSpan.Zero,
                RemainingHigh = TimeSpan.Zero,
                ExpectedCompletionUtc = nowUtc,
                Confidence = options.EtaConfidence,
            };
        }

        // An estimate from one or two items is worse than no estimate, because it will be believed.
        if (stats.Completions < options.EtaMinimumSamples || elapsed < options.EtaMinimumElapsed || stats.MeanGapSeconds <= 0.0)
        {
            return EtaEstimate.Unknown;
        }

        double centralSeconds = remaining * stats.MeanGapSeconds;

        // The remaining work is a sum of `remaining` draws, so its standard deviation grows with
        // the square root of the count, not the count: the band tightens as the loop progresses.
        // Dividing by the observed concurrency accounts for workers absorbing spread in parallel.
        double z = Normal.TwoSidedZ(options.EtaConfidence);
        double bandSeconds = z * Math.Sqrt(stats.VarianceSeconds) * Math.Sqrt(remaining) / Math.Max(1.0, stats.Concurrency);

        // An item already running longer than the whole predicted remainder means the prediction is
        // stale: a hung item contributes to no completed statistic, so nothing else would notice.
        if (longestInFlight is { } inFlight && inFlight.TotalSeconds > centralSeconds)
        {
            centralSeconds = inFlight.TotalSeconds;
        }

        double lowSeconds = Math.Max(0.0, centralSeconds - bandSeconds);
        double highSeconds = centralSeconds + bandSeconds;

        TimeSpan central = FromSecondsSafe(centralSeconds);
        return new EtaEstimate
        {
            Remaining = central,
            RemainingLow = FromSecondsSafe(lowSeconds),
            RemainingHigh = FromSecondsSafe(highSeconds),
            ExpectedCompletionUtc = nowUtc + central,
            Confidence = options.EtaConfidence,
        };
    }

    /// <summary>Rounds to whole seconds, so an estimate never looks more precise than it is, and never overflows.</summary>
    private static TimeSpan FromSecondsSafe(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0.0)
        {
            return TimeSpan.Zero;
        }

        const double MaxSeconds = 365.0 * 24 * 60 * 60 * 100;
        return TimeSpan.FromSeconds(Math.Round(Math.Min(seconds, MaxSeconds)));
    }
}
