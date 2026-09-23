using Microsoft.Extensions.Logging;

namespace ThrottledLogging.ReadmeAnimation;

/// <summary>Which of an operation's two throttle channels an event travels through.</summary>
internal enum Channel
{
    /// <summary>Started and succeeded events.</summary>
    Progress,

    /// <summary>Failed events.</summary>
    Failures,
}

/// <summary>One event the loop handed to the library, whether or not it was ever written.</summary>
/// <param name="At">Seconds since the operation began, on the fake clock.</param>
/// <param name="Item">The 1-based item number.</param>
/// <param name="Label">The item label passed to <c>BeginItem</c>.</param>
/// <param name="Outcome">What happened to the item.</param>
internal sealed record Submission(double At, int Item, string Label, ItemOutcome Outcome)
{
    /// <summary>The channel this event is throttled on.</summary>
    public Channel Channel => Outcome == ItemOutcome.Failed ? Channel.Failures : Channel.Progress;
}

/// <summary>One line that reached <see cref="ILogger"/>.</summary>
/// <param name="At">Seconds since the operation began, on the fake clock.</param>
/// <param name="Level">The level it was written at.</param>
/// <param name="Category">The logger category.</param>
/// <param name="EventId">The event id.</param>
/// <param name="Message">The formatted message, exactly as the library produced it.</param>
internal sealed record LogLine(double At, LogLevel Level, string Category, int EventId, string Message);

/// <summary>A held event the library released, as reported through <see cref="ThrottledLoggingOptions.OnEmitted"/>.</summary>
/// <param name="At">Seconds since the operation began when it was written.</param>
/// <param name="Label">The item label.</param>
/// <param name="Outcome">The item outcome.</param>
/// <param name="SubmittedAt">Seconds since the operation began when the loop submitted it.</param>
/// <param name="IsNew">Whether it had not been written before.</param>
internal sealed record Release(double At, string? Label, ItemOutcome Outcome, double SubmittedAt, bool IsNew)
{
    /// <summary>The channel it was released from.</summary>
    public Channel Channel => Outcome == ItemOutcome.Failed ? Channel.Failures : Channel.Progress;
}

/// <summary>When one item of the loop ran and how it ended.</summary>
/// <param name="Item">The 1-based item number.</param>
/// <param name="Label">The item label.</param>
/// <param name="StartedAt">Seconds since the operation began when the item started.</param>
/// <param name="EndedAt">Seconds since the operation began when the item ended.</param>
/// <param name="Succeeded">Whether the item succeeded.</param>
internal sealed record ItemRun(int Item, string Label, double StartedAt, double EndedAt, bool Succeeded);

/// <summary>Everything the scripted run produced, in fake-clock seconds from the start of the operation.</summary>
/// <param name="Items">Every item of the loop.</param>
/// <param name="Submissions">Every event offered to the library, in order.</param>
/// <param name="Releases">Every event the library wrote, in order.</param>
/// <param name="Lines">Every line that reached the logger, in order.</param>
/// <param name="EndedAt">Seconds since the operation began when it ended.</param>
internal sealed record Timeline(
    IReadOnlyList<ItemRun> Items,
    IReadOnlyList<Submission> Submissions,
    IReadOnlyList<Release> Releases,
    IReadOnlyList<LogLine> Lines,
    double EndedAt);
