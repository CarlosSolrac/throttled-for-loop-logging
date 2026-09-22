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

    // Two templates, one event id. A null estimate rendered through the numeric template reads
    // "ETA s (–s)", so the warm-up case gets its own wording; the structured properties a sink
    // cares about are identical up to the ETA itself, and both lines carry event id 9004.
    [LoggerMessage(EventId = 9004, Message = "{OperationName} item {ItemLabel} {Outcome} — {Processed}/{TotalItems} done, {Failed} failed, new={IsNew}, submitted {SubmittedAtUtc:O}, {SuppressedSince} held since last line, {RatePerSecond:N1}/s, ETA {EtaSeconds:N1}s ({EtaLowSeconds:N1}–{EtaHighSeconds:N1}s)")]
    public static partial void Progress(
        ILogger logger,
        LogLevel level,
        string operationName,
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

    [LoggerMessage(EventId = 9004, Message = "{OperationName} item {ItemLabel} {Outcome} — {Processed}/{TotalItems} done, {Failed} failed, new={IsNew}, submitted {SubmittedAtUtc:O}, {SuppressedSince} held since last line, {RatePerSecond:N1}/s, ETA not yet known")]
    public static partial void ProgressWithoutEta(
        ILogger logger,
        LogLevel level,
        string operationName,
        string? itemLabel,
        ItemOutcome outcome,
        long processed,
        long? totalItems,
        long failed,
        bool isNew,
        DateTimeOffset submittedAtUtc,
        long suppressedSince,
        double ratePerSecond);

    [LoggerMessage(EventId = 9005, Message = "{OperationName} item {ItemLabel} failed — {Failed} failures so far, new={IsNew}, submitted {SubmittedAtUtc:O}, {SuppressedSince} held since last line")]
    public static partial void ItemFailed(
        ILogger logger,
        LogLevel level,
        Exception? error,
        string operationName,
        string? itemLabel,
        long failed,
        bool isNew,
        DateTimeOffset submittedAtUtc,
        long suppressedSince);

    [LoggerMessage(EventId = 9006, Message = "{OperationName} finished with failures — {Failed} of {TotalItems} failed in {ElapsedSeconds:N1}s. {Breakdown}. Last failure: {LastFailureLabel} at {LastFailureAtUtc:O}")]
    public static partial void FailureSummary(
        ILogger logger,
        LogLevel level,
        string operationName,
        long failed,
        long? totalItems,
        double elapsedSeconds,
        string breakdown,
        string? lastFailureLabel,
        DateTimeOffset? lastFailureAtUtc);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Debug, Message = "A ThrottledLogging observer threw and was ignored.")]
    public static partial void ObserverThrew(ILogger logger, Exception error);
}
