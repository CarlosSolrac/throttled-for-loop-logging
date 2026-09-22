namespace ThrottledLogging.Internal;

/// <summary>Why a held event was written out.</summary>
internal enum FlushReason
{
    /// <summary>The first event on the channel, which is never held (R4).</summary>
    First,

    /// <summary>The count threshold was reached.</summary>
    Count,

    /// <summary>The time threshold elapsed while an event was being submitted.</summary>
    Time,

    /// <summary>The background sweeper found an overdue held event.</summary>
    Sweeper,

    /// <summary>The operation ended and flushed whatever was still held.</summary>
    Final,
}
