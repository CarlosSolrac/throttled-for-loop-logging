using Microsoft.Extensions.Logging;

namespace ThrottledLogging;

/// <summary>
/// Per-operation throttling and estimation settings. The progress channel and the failure channel
/// are configured separately and throttle independently; see the design document, section 4.4.
/// </summary>
public sealed class OperationOptions
{
    /// <summary>
    /// How many items the operation expects to process. Without it there is no ETA, because there
    /// is nothing to count down from.
    /// </summary>
    public long? TotalItems { get; set; }

    /// <summary>Emit the held progress event once this many progress events have been submitted since the last emission.</summary>
    public int EveryItems { get; set; } = 500;

    /// <summary>Emit the held progress event once this long has passed since the last emission.</summary>
    public TimeSpan EveryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The level progress events are written at.</summary>
    public LogLevel Level { get; set; } = LogLevel.Information;

    /// <summary>
    /// Count threshold for the failure channel. Tighter than <see cref="EveryItems"/> by default,
    /// because failures are rarer and more interesting than successes.
    /// </summary>
    public int FailureEveryItems { get; set; } = 50;

    /// <summary>Time threshold for the failure channel.</summary>
    public TimeSpan FailureEveryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The level failure events are written at.</summary>
    public LogLevel FailureLevel { get; set; } = LogLevel.Warning;

    /// <summary>
    /// How many distinct exception types the failure breakdown tracks before collecting the rest
    /// under a single <c>(other)</c> key.
    /// </summary>
    public int MaxTrackedFailureTypes { get; set; } = 20;

    /// <summary>
    /// Half-life, in items, of the exponentially weighted statistics behind the ETA. Smaller values
    /// follow a changing workload faster and are noisier.
    /// </summary>
    public int EtaHalfLifeItems { get; set; } = 50;

    /// <summary>Two-sided confidence for the ETA band, for example <c>0.80</c>.</summary>
    public double EtaConfidence { get; set; } = 0.80;

    /// <summary>Completed items required before an ETA is offered at all.</summary>
    public int EtaMinimumSamples { get; set; } = 5;

    /// <summary>Elapsed time required before an ETA is offered at all.</summary>
    public TimeSpan EtaMinimumElapsed { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Key/value pairs attached, as a logging scope, to every line this operation writes: entry,
    /// item, failure, heartbeat, summary and end lines alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use it for whatever identifies the run to whoever reads the log later: a batch id, a file
    /// name, a queue message id and its delivery count, an Azure Functions invocation id. Sinks
    /// that understand structured scopes store each pair as its own field (Application Insights
    /// adds them to <c>customDimensions</c>; the JSON console formatter writes them as properties);
    /// text sinks with scopes enabled print them as <c>Key:Value, Key:Value</c>. A sink that ignores
    /// scopes ignores these too, and the operation id on every line still identifies the run.
    /// </para>
    /// <para>
    /// The dictionary is copied when the operation begins, so changing it afterwards has no effect
    /// on an operation already running. <see langword="null"/> or empty opens no scope at all.
    /// </para>
    /// <para>
    /// This is not the only way context reaches the log. The operation also remembers the caller's
    /// own logging scopes and <see cref="System.Diagnostics.Activity"/> from the moment it began,
    /// and restores them for lines written by the background sweeper. Values already in a scope the
    /// caller opened therefore need not be repeated here; a sink with scopes enabled would print
    /// them twice.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, object?>? Scope { get; set; }

    /// <summary>
    /// Returns an independent copy, so a caller's instance cannot be mutated underneath a running
    /// operation. <see cref="Scope"/> is copied too, not shared.
    /// </summary>
    internal OperationOptions Clone()
    {
        OperationOptions copy = (OperationOptions)MemberwiseClone();
        copy.Scope = Scope is null ? null : new Dictionary<string, object?>(Scope, StringComparer.Ordinal);
        return copy;
    }

    /// <summary>Throws when any setting is outside its supported range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is invalid.</exception>
    internal void Validate()
    {
        ThrowIfNotPositive(EveryItems, nameof(EveryItems));
        ThrowIfNotPositive(FailureEveryItems, nameof(FailureEveryItems));
        ThrowIfNotPositive(EtaHalfLifeItems, nameof(EtaHalfLifeItems));
        ThrowIfNotPositive(MaxTrackedFailureTypes, nameof(MaxTrackedFailureTypes));

        if (EveryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(EveryInterval), EveryInterval, "Must be greater than zero.");
        }

        if (FailureEveryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FailureEveryInterval), FailureEveryInterval, "Must be greater than zero.");
        }

        if (EtaConfidence is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(EtaConfidence), EtaConfidence, "Must be strictly between zero and one.");
        }

        if (TotalItems is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TotalItems), TotalItems, "Must not be negative.");
        }
    }

    private static void ThrowIfNotPositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be greater than zero.");
        }
    }
}
