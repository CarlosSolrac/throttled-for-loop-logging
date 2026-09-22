namespace ThrottledLogging;

/// <summary>
/// Reads the live state of every operation in the process. Registered as a singleton alongside
/// <see cref="IOperationLogger"/>, and safe to call concurrently with running operations.
/// </summary>
public interface IOperationRegistry
{
    /// <summary>
    /// Every operation currently in flight.
    /// </summary>
    /// <remarks>
    /// Not a blocking call: it reads interlocked counters out of a concurrent dictionary and
    /// performs no I/O. The asynchronous overload exists only so this composes with
    /// <see langword="await"/>-based call sites such as a health endpoint.
    /// </remarks>
    /// <returns>A snapshot per active operation, in no particular order.</returns>
    IReadOnlyList<OperationSnapshot> GetActiveOperations();

    /// <summary>Awaitable form of <see cref="GetActiveOperations"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A snapshot per active operation.</returns>
    ValueTask<IReadOnlyList<OperationSnapshot>> GetActiveOperationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Process-wide throttling tallies since this registry was created.</summary>
    /// <returns>The counters.</returns>
    ThrottleCounters GetCounters();
}
