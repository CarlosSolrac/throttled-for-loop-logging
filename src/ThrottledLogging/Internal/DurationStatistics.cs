namespace ThrottledLogging.Internal;

/// <summary>
/// Running per-item timing for one operation, in constant memory.
/// </summary>
/// <remarks>
/// <para>
/// Statistics are split by purpose, not by outcome. The <em>predictive</em> figures behind the ETA
/// take every completed item, successful or not, because an ETA predicts wall-clock and every item
/// consumed real wall-clock; excluding a subset biases the estimate by however much that subset
/// differed. The <em>descriptive</em> figures reported for humans are successes-only, because "how
/// long does a successful item take" is the question being asked of them. See the design document,
/// section 6.7.
/// </para>
/// <para>
/// A single lock guards the accumulators. It is held for a few nanoseconds and is uncontended next
/// to the work being measured; compare-and-swap loops on <see cref="double"/> would buy nothing
/// until a benchmark says otherwise.
/// </para>
/// </remarks>
internal sealed class DurationStatistics
{
    /// <summary>One tick. Durations are floored here so the geometric mean's logarithm stays defined.</summary>
    private const double MinimumSeconds = 1e-7;

    /// <summary>How many standard deviations of deviation the variance will accept from one item.</summary>
    private const double VarianceClampSigmas = 4.0;

    private readonly object _gate = new();
    private readonly double _alpha;
    private readonly TimeProvider _time;

    private long _completions;
    private double _ewmaSeconds;
    private double _ewmaVariance;
    private double _ewmaGapSeconds;
    private long _lastCompletionTimestamp;

    private long _successes;
    private double _successSumSeconds;
    private double _successSumLogSeconds;

    private long _failures;
    private double _failureSumSeconds;

    /// <summary>Creates the accumulators.</summary>
    /// <param name="halfLifeItems">Half-life of the exponential weighting, in items.</param>
    /// <param name="time">Clock, injected so tests need not sleep.</param>
    public DurationStatistics(int halfLifeItems, TimeProvider time)
    {
        // A half-life of h items means a sample's weight has halved after h more samples.
        _alpha = 1.0 - Math.Pow(2.0, -1.0 / Math.Max(1, halfLifeItems));
        _time = time;
        _lastCompletionTimestamp = time.GetTimestamp();
    }

    /// <summary>Records one finished item.</summary>
    /// <param name="duration">How long the item took.</param>
    /// <param name="succeeded">Whether it succeeded.</param>
    public void Record(TimeSpan duration, bool succeeded)
    {
        double seconds = Math.Max(duration.TotalSeconds, MinimumSeconds);
        long now = _time.GetTimestamp();

        lock (_gate)
        {
            double gap = Math.Max(_time.GetElapsedTime(_lastCompletionTimestamp, now).TotalSeconds, MinimumSeconds);
            _lastCompletionTimestamp = now;

            if (_completions == 0)
            {
                _ewmaSeconds = seconds;
                _ewmaVariance = 0.0;
                _ewmaGapSeconds = gap;
            }
            else
            {
                double deviation = seconds - _ewmaSeconds;

                // The mean follows reality, however extreme the item. Only the variance input is
                // clamped, so one pathological hang cannot leave the confidence band wide open for
                // the next fifty items.
                double limit = VarianceClampSigmas * Math.Max(Math.Sqrt(_ewmaVariance), _ewmaSeconds);
                double clamped = Math.Clamp(deviation, -limit, limit);

                _ewmaVariance = (1.0 - _alpha) * (_ewmaVariance + (_alpha * clamped * clamped));
                _ewmaSeconds += _alpha * deviation;
                _ewmaGapSeconds += _alpha * (gap - _ewmaGapSeconds);
            }

            _completions++;

            if (succeeded)
            {
                _successes++;
                _successSumSeconds += seconds;
                _successSumLogSeconds += Math.Log(seconds);
            }
            else
            {
                _failures++;
                _failureSumSeconds += seconds;
            }
        }
    }

    /// <summary>Reads every figure at once.</summary>
    /// <returns>The snapshot.</returns>
    public DurationSnapshot Snapshot()
    {
        lock (_gate)
        {
            double gap = _completions == 0 ? 0.0 : Math.Max(_ewmaGapSeconds, MinimumSeconds);
            double rate = gap > 0.0 ? 1.0 / gap : 0.0;

            // Little's law: items in flight = arrival rate x time in system. Tells the ETA band how
            // much of the per-item spread is being absorbed by running items side by side.
            double concurrency = gap > 0.0 ? Math.Max(1.0, _ewmaSeconds / gap) : 1.0;

            return new DurationSnapshot(
                Completions: _completions,
                Successes: _successes,
                Failures: _failures,
                RatePerSecond: rate,
                MeanGapSeconds: gap,
                VarianceSeconds: _ewmaVariance,
                Concurrency: concurrency,
                MeanSuccessSeconds: _successes == 0 ? null : _successSumSeconds / _successes,
                TypicalSuccessSeconds: _successes == 0 ? null : Math.Exp(_successSumLogSeconds / _successes),
                MeanFailureSeconds: _failures == 0 ? null : _failureSumSeconds / _failures);
        }
    }
}
