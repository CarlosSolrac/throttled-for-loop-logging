using System.Collections;
using System.Globalization;
using System.Text;

namespace ThrottledLogging.Internal;

/// <summary>
/// The state object handed to <c>ILogger.BeginScope</c> for <see cref="OperationOptions.Scope"/>.
/// </summary>
/// <remarks>
/// <para>
/// Logging providers recognise scope state by shape rather than by type. Anything enumerable as
/// <c>KeyValuePair&lt;string, object?&gt;</c> is treated as structured: Application Insights copies
/// each pair into <c>customDimensions</c>, and the JSON console formatter writes each as a
/// property. Providers that only print text call <see cref="ToString"/>.
/// </para>
/// <para>
/// A plain <see cref="Dictionary{TKey, TValue}"/> satisfies the structured providers but renders as
/// its type name in the text ones, which is why this wrapper exists. It is built once per operation
/// and is immutable, so one instance is shared by every line that operation writes, on any thread.
/// </para>
/// </remarks>
internal sealed class ContextScopeState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?>[] _pairs;
    private string? _text;

    /// <summary>Captures the pairs, in the order the source enumerates them.</summary>
    /// <param name="pairs">The operation's already-copied <see cref="OperationOptions.Scope"/>.</param>
    public ContextScopeState(IEnumerable<KeyValuePair<string, object?>> pairs) => _pairs = [.. pairs];

    /// <summary>Whether there is anything to attach. An empty scope is never opened.</summary>
    public bool IsEmpty => _pairs.Length == 0;

    /// <inheritdoc />
    public int Count => _pairs.Length;

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index] => _pairs[index];

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_pairs).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => _pairs.GetEnumerator();

    /// <summary>
    /// <c>Key:Value, Key:Value</c>, formatted with the invariant culture so the same run reads the
    /// same whatever machine wrote it. Built on first use and cached; a benign race may build it twice.
    /// </summary>
    /// <returns>The pairs as text.</returns>
    public override string ToString()
    {
        if (_text is { } cached)
        {
            return cached;
        }

        StringBuilder builder = new();
        foreach (KeyValuePair<string, object?> pair in _pairs)
        {
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(pair.Key).Append(':').Append(Convert.ToString(pair.Value, CultureInfo.InvariantCulture));
        }

        _text = builder.ToString();
        return _text;
    }
}
