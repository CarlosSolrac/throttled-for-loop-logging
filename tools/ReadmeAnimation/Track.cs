using System.Globalization;
using System.Text;

namespace ThrottledLogging.ReadmeAnimation;

/// <summary>
/// The keyframes of one CSS animation, written in seconds and emitted as percentages of the loop.
/// Values move linearly between frames; <see cref="Jump"/> makes a change look instant.
/// </summary>
/// <param name="initial">The declaration at the start of the loop, for example <c>opacity:0</c>.</param>
internal sealed class Track(string initial)
{
    /// <summary>How long an instant change takes, in seconds. Long enough to keep frames strictly ordered.</summary>
    private const double Instant = 0.01;

    private readonly List<(double At, string Declaration)> _frames = [(0, initial)];

    /// <summary>The declaration in force at the latest frame.</summary>
    public string Last => _frames[^1].Declaration;

    /// <summary>Moves linearly from the previous frame to <paramref name="declaration"/>, arriving at <paramref name="at"/>.</summary>
    /// <param name="at">Seconds since the start of the loop.</param>
    /// <param name="declaration">The CSS declaration.</param>
    /// <returns>This track.</returns>
    public Track To(double at, string declaration)
    {
        _frames.Add((Math.Max(at, _frames[^1].At + Instant), declaration));
        return this;
    }

    /// <summary>Holds the current value until <paramref name="at"/>, then changes to <paramref name="declaration"/> at once.</summary>
    /// <param name="at">Seconds since the start of the loop.</param>
    /// <param name="declaration">The CSS declaration.</param>
    /// <returns>This track.</returns>
    public Track Jump(double at, string declaration)
    {
        string held = Last;
        To(at, held);
        return To(at + Instant, declaration);
    }

    /// <summary>Holds the current value until <paramref name="at"/>, then moves to <paramref name="declaration"/> over <paramref name="seconds"/>.</summary>
    /// <param name="at">When the change starts, in seconds since the start of the loop.</param>
    /// <param name="seconds">How long the change takes.</param>
    /// <param name="declaration">The CSS declaration.</param>
    /// <returns>This track.</returns>
    public Track Ease(double at, double seconds, string declaration)
    {
        string held = Last;
        To(at, held);
        return To(at + seconds, declaration);
    }

    /// <summary>Writes the track as a <c>@keyframes</c> rule.</summary>
    /// <param name="name">The animation name.</param>
    /// <param name="duration">The length of the loop in seconds.</param>
    /// <param name="css">Where the rule is written.</param>
    public void Write(string name, double duration, StringBuilder css)
    {
        css.Append(CultureInfo.InvariantCulture, $"@keyframes {name}{{");
        double previous = -1;
        foreach ((double at, string declaration) in _frames)
        {
            double percent = Math.Round(Math.Clamp(at / duration, 0, 1) * 100, 3);
            if (percent <= previous)
            {
                continue;
            }

            css.Append(CultureInfo.InvariantCulture, $"{percent}%{{{declaration}}}");
            previous = percent;
        }

        if (previous < 100)
        {
            css.Append(CultureInfo.InvariantCulture, $"100%{{{Last}}}");
        }

        css.Append('}').Append('\n');
    }
}
