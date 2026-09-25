using Microsoft.Extensions.Logging;

namespace ThrottledLogging.Internal;

/// <summary>
/// Every line this library writes, defined through the logging source generator so that no message
/// is formatted unless it is actually going to be written.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 9000, Message = "{OperationName} started (operation {OperationId}, {TotalItems} items expected)")]
    public static partial void OperationStarted(ILogger logger, LogLevel level, string operationName, Guid operationId, long? totalItems);

    [LoggerMessage(EventId = 9001, Message = "{OperationName} ({OperationId}) succeeded in {ElapsedSeconds:N1}s — {Processed} processed, {Failed} failed. {Note}")]
    public static partial void OperationSucceeded(ILogger logger, LogLevel level, string operationName, double elapsedSeconds, long processed, long failed, string? note, Guid operationId);

    [LoggerMessage(EventId = 9002, Message = "{OperationName} ({OperationId}) failed after {ElapsedSeconds:N1}s — {Processed} processed, {Failed} failed")]
    public static partial void OperationFailed(ILogger logger, LogLevel level, Exception error, string operationName, double elapsedSeconds, long processed, long failed, Guid operationId);

    [LoggerMessage(EventId = 9003, Message = "{OperationName} ({OperationId}) ended without an outcome after {ElapsedSeconds:N1}s — {Processed} processed, {Failed} failed")]
    public static partial void OperationIncomplete(ILogger logger, LogLevel level, string operationName, double elapsedSeconds, long processed, long failed, Guid operationId);

    // Every line from 9004 onwards names its operation by id as well as by name. The name alone is
    // ambiguous as soon as the same function runs twice at once: two scaled-out instances of one
    // Azure Function, or two threads in one process. The id is the only field that tells their
    // item lines apart without relying on the sink keeping scopes.

    // Two templates, one event id. A null estimate rendered through the numeric template reads
    // "ETA s (–s)", so the warm-up case gets its own wording; the structured properties a sink
    // cares about are identical up to the ETA itself, and both lines carry event id 9004.
    [LoggerMessage(EventId = 9004, Message = "{OperationName} ({OperationId}) item {ItemLabel} {Outcome} — {Processed}/{TotalItems} done, {Failed} failed, new={IsNew}, submitted {SubmittedAtUtc:O}, {SuppressedSince} held since last line, {RatePerSecond:N1}/s, ETA {EtaSeconds:N1}s ({EtaLowSeconds:N1}–{EtaHighSeconds:N1}s)")]
    public static partial void Progress(
        ILogger logger,
        LogLevel level,
        string operationName,
        Guid operationId,
        string? itemLabel,
        ItemOutcome outcome,
        long processed,
        long? totalItems,
        long failed,
        bool isNew,
        DateTimeOffset submittedAtUtc,
        long suppressedSince,
        double ratePerSecond,
        double etaSeconds,
        double etaLowSeconds,
        double etaHighSeconds);

    [LoggerMessage(EventId = 9004, Message = "{OperationName} ({OperationId}) item {ItemLabel} {Outcome} — {Processed}/{TotalItems} done, {Failed} failed, new={IsNew}, submitted {SubmittedAtUtc:O}, {SuppressedSince} held since last line, {RatePerSecond:N1}/s, ETA not yet known")]
    public static partial void ProgressWithoutEta(
        ILogger logger,
        LogLevel level,
        string operationName,
        Guid operationId,
        string? itemLabel,
        ItemOutcome outcome,
        long processed,
        long? totalItems,
        long failed,
        bool isNew,
        DateTimeOffset submittedAtUtc,
        long suppressedSince,
        double ratePerSecond);

    [LoggerMessage(EventId = 9005, Message = "{OperationName} ({OperationId}) item {ItemLabel} failed — {Failed} failures so far, new={IsNew}, submitted {SubmittedAtUtc:O}, {SuppressedSince} held since last line")]
    public static partial void ItemFailed(
        ILogger logger,
        LogLevel level,
        Exception? error,
        string operationName,
        Guid operationId,
        string? itemLabel,
        long failed,
        bool isNew,
        DateTimeOffset submittedAtUtc,
        long suppressedSince);

    [LoggerMessage(EventId = 9006, Message = "{OperationName} ({OperationId}) finished with failures — {Failed} of {TotalItems} failed in {ElapsedSeconds:N1}s. {Breakdown}. Last failure: {LastFailureLabel} at {LastFailureAtUtc:O}")]
    public static partial void FailureSummary(
        ILogger logger,
        LogLevel level,
        string operationName,
        Guid operationId,
        long failed,
        long? totalItems,
        double elapsedSeconds,
        string breakdown,
        string? lastFailureLabel,
        DateTimeOffset? lastFailureAtUtc);

    // Written by OperationLogger.Dispose for each operation still running, after its held events
    // have been flushed. Usually the host is shutting down or recycling; if this is the last line
    // an operation ever writes, the process ended before the operation did.
    [LoggerMessage(EventId = 9008, Message = "{OperationName} ({OperationId}) was still running when the logger shut down, after {ElapsedSeconds:N1}s — {Processed} processed, {Failed} failed, {InFlight} in flight")]
    public static partial void OperationStillRunningAtShutdown(ILogger logger, LogLevel level, string operationName, Guid operationId, double elapsedSeconds, long processed, long failed, int inFlight);

    [LoggerMessage(EventId = 9009, Level = LogLevel.Warning, Message = "Flushing {OperationName} ({OperationId}) at shutdown failed.")]
    public static partial void ShutdownFlushFailed(ILogger logger, Exception error, string operationName, Guid operationId);

    [LoggerMessage(EventId = 9010, Level = LogLevel.Warning, Message = "The ThrottledLogging sweeper did not stop within five seconds; flushing running operations anyway.")]
    public static partial void SweeperDidNotStop(ILogger logger);

    // Written when configuration reloads with settings that fail validation. The previous settings
    // stay in force; the exception says which value was rejected.
    [LoggerMessage(EventId = 9011, Level = LogLevel.Warning, Message = "Reloaded ThrottledLogging settings were rejected; the previous settings stay in force.")]
    public static partial void SettingsReloadRejected(ILogger logger, Exception error);

    // Written when writing an operation's held line from the sweeper throws, usually because a
    // logging provider failed. That line is lost; the sweeper keeps running for every operation.
    [LoggerMessage(EventId = 9012, Level = LogLevel.Warning, Message = "Sweeping {OperationName} ({OperationId}) failed; the sweeper keeps running.")]
    public static partial void SweepFailed(ILogger logger, Exception error, string operationName, Guid operationId);

    // Written by a Dispose call made while another is still flushing, when the first has not finished
    // within the bound. The second call returns anyway rather than hang host shutdown.
    [LoggerMessage(EventId = 9013, Level = LogLevel.Warning, Message = "Dispose returned after waiting {WaitedSeconds:N1}s for an earlier Dispose that is still writing to the logging providers.")]
    public static partial void SecondDisposeTimedOut(ILogger logger, double waitedSeconds);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Debug, Message = "A ThrottledLogging observer threw and was ignored.")]
    public static partial void ObserverThrew(ILogger logger, Exception error);
}
