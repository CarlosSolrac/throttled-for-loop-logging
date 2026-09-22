namespace ThrottledLogging;

/// <summary>
/// Creates operation scopes. Registered as a singleton by
/// <see cref="ThrottledLoggingServiceCollectionExtensions.AddThrottledLogging(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </summary>
public interface IOperationLogger
{
    /// <summary>
    /// Begins a long-running operation and emits its entry line.
    /// </summary>
    /// <param name="name">Names the operation in the log, for example <c>ImportOrders</c>.</param>
    /// <param name="options">Throttling and estimation settings; the configured defaults are used when omitted.</param>
    /// <returns>The operation scope. Dispose it to end the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    IOperationScope BeginOperation(string name, OperationOptions? options = null);
}
