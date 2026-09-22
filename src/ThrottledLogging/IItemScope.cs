namespace ThrottledLogging;

/// <summary>
/// One unit of work inside an operation, normally one turn of a loop. Timing starts when the scope
/// is created and stops at the first call to <see cref="Success"/>, <see cref="Failure"/> or
/// <see cref="IDisposable.Dispose"/>.
/// </summary>
/// <remarks>
/// Disposing without recording an outcome counts the item as failed, because a scope left by an
/// exception unwinding the stack is a failure in every case that matters. Record the outcome
/// explicitly, or use the delegate overloads on <see cref="OperationScopeExtensions"/>, which do it
/// for you.
/// </remarks>
public interface IItemScope : IDisposable
{
    /// <summary>The label this scope was created with, if any.</summary>
    string? Label { get; }

    /// <summary>Records the item as processed successfully. Later calls are ignored.</summary>
    void Success();

    /// <summary>Records the item as failed. Later calls are ignored.</summary>
    /// <param name="exception">The exception that caused the failure.</param>
    void Failure(Exception exception);
}
