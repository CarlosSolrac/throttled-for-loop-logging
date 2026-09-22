namespace ThrottledLogging.Internal;

/// <summary>One unit of work. See <see cref="IItemScope"/>.</summary>
internal sealed class ItemScope : IItemScope
{
    private readonly OperationScope _operation;
    private readonly long _id;
    private readonly long _startTimestamp;
    private int _completed;

    /// <summary>Creates the scope and starts its clock.</summary>
    /// <param name="operation">The owning operation.</param>
    /// <param name="id">Identifies this item among those in flight.</param>
    /// <param name="label">The caller's label.</param>
    /// <param name="startTimestamp">When the item began, as a <see cref="TimeProvider"/> timestamp.</param>
    public ItemScope(OperationScope operation, long id, string? label, long startTimestamp)
    {
        _operation = operation;
        _id = id;
        _startTimestamp = startTimestamp;
        Label = label;
    }

    /// <inheritdoc />
    public string? Label { get; }

    /// <inheritdoc />
    public void Success() => Complete(succeeded: true, error: null);

    /// <inheritdoc />
    public void Failure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Complete(succeeded: false, exception);
    }

    /// <summary>
    /// Ends the item. An item disposed without an outcome counts as failed: the usual way that
    /// happens is an exception unwinding the stack past the <see langword="using"/>, and treating
    /// it as a success would hide exactly the items worth seeing.
    /// </summary>
    public void Dispose() => Complete(succeeded: false, error: null);

    private void Complete(bool succeeded, Exception? error)
    {
        // First outcome wins, so Dispose after an explicit Success or Failure is a no-op.
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _operation.CompleteItem(_id, Label, succeeded, error, _startTimestamp);
    }
}
