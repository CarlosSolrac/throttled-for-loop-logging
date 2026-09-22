namespace ThrottledLogging.Internal;

/// <summary>A decision to write a held event out, with the bookkeeping that goes on the log line.</summary>
/// <param name="Event">The event being written.</param>
/// <param name="IsNew">Whether anything was submitted since the previous emission.</param>
/// <param name="SuppressedSince">How many events were skipped since the previous emission.</param>
/// <param name="Reason">What triggered the write.</param>
internal readonly record struct Emission(PendingEvent Event, bool IsNew, long SuppressedSince, FlushReason Reason);
