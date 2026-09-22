namespace ThrottledLogging;

/// <summary>
/// What happened to a single unit of work inside an operation.
/// </summary>
public enum ItemOutcome
{
    /// <summary>Processing of the item has begun and has not yet finished.</summary>
    Started = 0,

    /// <summary>The item was processed successfully.</summary>
    Succeeded = 1,

    /// <summary>The item failed.</summary>
    Failed = 2,
}
