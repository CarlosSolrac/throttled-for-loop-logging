namespace ThrottledLogging;

/// <summary>
/// Process-wide tallies of what the throttler has done since it was created. Returned by
/// <see cref="IOperationRegistry.GetCounters"/>.
/// </summary>
/// <remarks>
/// The progress channel (<see cref="ItemOutcome.Started"/> and <see cref="ItemOutcome.Succeeded"/>)
/// and the failure channel (<see cref="ItemOutcome.Failed"/>) are counted separately, because they
/// throttle independently. See the design document, section 4.4.
/// </remarks>
public readonly record struct ThrottleCounters
{
    /// <summary>Progress events offered to the throttler.</summary>
    public required long EventsSubmitted { get; init; }

    /// <summary>Progress events actually written to the log.</summary>
    public required long EventsLogged { get; init; }

    /// <summary>
    /// Progress events superseded before they could be written, and so never seen individually.
    /// Counted when the superseding line is written, so an event still being held is in
    /// <see cref="EventsSubmitted"/> but not yet in here or in <see cref="EventsLogged"/>. The three
    /// balance once every operation has ended.
    /// </summary>
    public required long EventsHeldBack { get; init; }

    /// <summary>Failure events offered to the throttler.</summary>
    public required long FailuresSubmitted { get; init; }

    /// <summary>Failure events actually written to the log.</summary>
    public required long FailuresLogged { get; init; }

    /// <summary>
    /// Failure events superseded before they could be written. Counted on the same basis as
    /// <see cref="EventsHeldBack"/>.
    /// </summary>
    public required long FailuresHeldBack { get; init; }

    /// <summary>Emissions triggered by reaching a count threshold.</summary>
    public required long FlushesByCount { get; init; }

    /// <summary>Emissions triggered by a time threshold elapsing during a submission.</summary>
    public required long FlushesByTime { get; init; }

    /// <summary>Emissions triggered by the background sweeper rather than by a submission.</summary>
    public required long FlushesBySweeper { get; init; }

    /// <summary>Operations currently in flight.</summary>
    public required int ActiveOperations { get; init; }

    /// <summary>Operations that finished successfully.</summary>
    public required long CompletedOperations { get; init; }

    /// <summary>Operations that finished with a failure or were disposed without an outcome.</summary>
    public required long FailedOperations { get; init; }
}
