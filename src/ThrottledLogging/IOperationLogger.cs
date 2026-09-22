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

    /// <summary>
    /// A fresh copy of the process-wide defaults, for a caller that wants to change one setting
    /// and keep the rest.
    /// </summary>
    /// <remarks>
    /// <see cref="BeginOperation"/> replaces the defaults wholesale with whatever it is handed, so
    /// an <see cref="OperationOptions"/> built with <see langword="new"/> carries the type's own
    /// defaults and not the ones configured on
    /// <see cref="ThrottledLoggingOptions.Defaults"/>. Start from this property, or from the
    /// <c>configure</c> overload of
    /// <see cref="OperationScopeExtensions.BeginOperation(IOperationLogger, string, Action{OperationOptions})"/>,
    /// to keep them.
    /// </remarks>
    OperationOptions DefaultOptions { get; }
}
