using System.Collections.Concurrent;

namespace ThrottledLogging.Internal;

/// <summary>
/// Counts failures by exception type, bounded so a pathological run cannot grow it without limit.
/// </summary>
/// <remarks>
/// Throttling failures means most of them are never written individually, so the closing summary
/// has to account for them in aggregate. The cap is enforced without a lock, so a burst of new
/// types arriving at once can overshoot it by a few entries before settling; that is cheaper than
/// serialising every failure and the bound still holds in any meaningful sense.
/// </remarks>
internal sealed class FailureBreakdown
{
    /// <summary>Where failures land once the cap on distinct types has been reached.</summary>
    public const string OtherKey = "(other)";

    /// <summary>Where an item disposed without an outcome lands.</summary>
    public const string UnrecordedKey = "(unrecorded)";

    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly int _cap;

    /// <summary>Creates the breakdown.</summary>
    /// <param name="cap">How many distinct exception types to track by name.</param>
    public FailureBreakdown(int cap) => _cap = cap;

    /// <summary>Records one failure.</summary>
    /// <param name="error">The exception; its runtime type name is the key. Null when the item was disposed without an outcome.</param>
    public void Record(Exception? error)
    {
        string key = error is null ? UnrecordedKey : error.GetType().Name;
        if (!_counts.ContainsKey(key) && _counts.Count >= _cap)
        {
            key = OtherKey;
        }

        _counts.AddOrUpdate(key, 1L, static (_, count) => count + 1L);
    }

    /// <summary>Copies the counts out.</summary>
    /// <returns>Counts by exception type name, largest first.</returns>
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        Dictionary<string, long> copy = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, long> entry in _counts)
        {
            copy[entry.Key] = entry.Value;
        }

        return copy;
    }

    /// <summary>Renders the counts for a log line, largest first.</summary>
    /// <returns>For example <c>TimeoutException 248404 · SqlException 982</c>, or an empty string.</returns>
    public string Describe()
    {
        IOrderedEnumerable<KeyValuePair<string, long>> ordered = _counts.OrderByDescending(static e => e.Value).ThenBy(static e => e.Key, StringComparer.Ordinal);
        return string.Join(" · ", ordered.Select(static e => $"{e.Key} {e.Value:N0}"));
    }
}
