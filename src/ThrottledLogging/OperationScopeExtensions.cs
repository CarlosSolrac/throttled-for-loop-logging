namespace ThrottledLogging;

/// <summary>
/// Delegate forms of the scope API. A <see langword="using"/> block cannot tell "returned normally"
/// from "threw", so the explicit API relies on the caller remembering to record an outcome. These
/// overloads record it for you and are the recommended default.
/// </summary>
public static class OperationScopeExtensions
{
    /// <summary>
    /// Begins an operation whose settings start from the configured defaults, so a caller that
    /// wants one setting changed does not have to restate the rest.
    /// </summary>
    /// <param name="logger">The operation factory.</param>
    /// <param name="name">The operation name.</param>
    /// <param name="configure">Applies the caller's changes to a copy of <see cref="IOperationLogger.DefaultOptions"/>.</param>
    /// <returns>The operation scope. Dispose it to end the operation.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IOperationScope BeginOperation(this IOperationLogger logger, string name, Action<OperationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configure);

        OperationOptions options = logger.DefaultOptions;
        configure(options);
        return logger.BeginOperation(name, options);
    }

    /// <summary>Runs a long-running function as an operation, recording its outcome.</summary>
    /// <param name="logger">The operation factory.</param>
    /// <param name="name">The operation name.</param>
    /// <param name="body">The work. The scope is passed in so the body can start items.</param>
    /// <param name="options">Throttling and estimation settings.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the operation does.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static async Task RunAsync(
        this IOperationLogger logger,
        string name,
        Func<IOperationScope, CancellationToken, Task> body,
        OperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(body);

        using IOperationScope operation = logger.BeginOperation(name, options);
        try
        {
            await body(operation, cancellationToken).ConfigureAwait(false);
            operation.Success();
        }
        catch (Exception error)
        {
            operation.Failure(error);
            throw;
        }
    }

    /// <summary>
    /// Processes a sequence, opening an item scope per element and recording its outcome, so a
    /// failing element is logged rather than aborting the loop.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="operation">The operation the items belong to.</param>
    /// <param name="source">The elements.</param>
    /// <param name="body">Processes one element.</param>
    /// <param name="label">Produces the log label for an element; <see cref="object.ToString"/> is used when omitted.</param>
    /// <param name="maxDegreeOfParallelism">How many elements to process at once.</param>
    /// <param name="cancellationToken">Cancels the loop.</param>
    /// <returns>A task that completes when every element has been attempted.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is less than one.</exception>
    public static async Task ForEachAsync<T>(
        this IOperationScope operation,
        IEnumerable<T> source,
        Func<T, CancellationToken, ValueTask> body,
        Func<T, string?>? label = null,
        int maxDegreeOfParallelism = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

        if (maxDegreeOfParallelism == 1)
        {
            foreach (T element in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessAsync(operation, element, body, label, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            source,
            parallelOptions,
            async (element, token) => await ProcessAsync(operation, element, body, label, token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static async ValueTask ProcessAsync<T>(
        IOperationScope operation,
        T element,
        Func<T, CancellationToken, ValueTask> body,
        Func<T, string?>? label,
        CancellationToken cancellationToken)
    {
        using IItemScope item = operation.BeginItem(label is null ? element?.ToString() : label(element));
        try
        {
            await body(element, cancellationToken).ConfigureAwait(false);
            item.Success();
        }
#pragma warning disable CA1031 // One element failing is data about the run, not a reason to abandon it.
        catch (Exception error)
#pragma warning restore CA1031
        {
            item.Failure(error);
        }
    }
}
