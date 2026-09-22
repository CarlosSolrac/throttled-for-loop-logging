namespace ThrottledLogging;

/// <summary>
/// One long-running function. Created by <see cref="IOperationLogger.BeginOperation"/> and ended by
/// disposal, which flushes any held events and writes the closing summary.
/// </summary>
public interface IOperationScope : IDisposable
{
    /// <summary>Identifies this operation across logs and processes.</summary>
    Guid Id { get; }

    /// <summary>The caller-supplied name.</summary>
    string Name { get; }

    /// <summary>
    /// Begins one unit of work and emits a <see cref="ItemOutcome.Started"/> event, subject to
    /// throttling.
    /// </summary>
    /// <param name="label">Identifies the item in the log, for example an order number.</param>
    /// <returns>A scope to record the outcome on.</returns>
    IItemScope BeginItem(string? label = null);

    /// <summary>Records the operation as successful. Later calls are ignored.</summary>
    /// <param name="note">Optional detail for the closing line.</param>
    void Success(string? note = null);

    /// <summary>Records the operation as failed. Later calls are ignored.</summary>
    /// <param name="exception">The exception that ended the operation.</param>
    void Failure(Exception exception);

    /// <summary>Takes a point-in-time view of this operation. Safe from any thread.</summary>
    /// <returns>The snapshot.</returns>
    OperationSnapshot Snapshot();
}
