using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ThrottledLogging.ReadmeAnimation;

/// <summary>
/// Draws a <see cref="Timeline"/> as a self-contained animated SVG: the loop on the left, the library
/// as a black box in the middle, and the console output of <see cref="ILogger"/> on the right.
/// </summary>
/// <remarks>
/// Everything moves through CSS keyframes, with no script and no external resources, because the
/// image is shown through an <c>img</c> tag on GitHub and on nuget.org, where neither would run.
/// </remarks>
internal sealed partial class Renderer
{
    /// <summary>Canvas width.</summary>
    private const double Width = 1000;

    /// <summary>Canvas height.</summary>
    private const double Height = 500;

    /// <summary>Seconds of stillness before the loop starts.</summary>
    private const double Lead = 1.0;

    /// <summary>Seconds an event takes to fly between panels.</summary>
    private const double Flight = 0.35;

    /// <summary>Seconds the finished picture stays on screen before the loop restarts.</summary>
    private const double Hold = 4.0;

    /// <summary>Seconds the whole moving layer takes to fade out at the end of the loop.</summary>
    private const double FadeOut = 0.6;

    private const string Mono = "ui-monospace,SFMono-Regular,Menlo,Consolas,'Liberation Mono','DejaVu Sans Mono',monospace";
    private const string Sans = "-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif";

    // Loop panel.
    private const double LoopX = 24;
    private const double LoopWidth = 252;
    private const double CodeTop = 50;
    private const double CodeLineHeight = 15;
    private const double TileTop = 296;
    private const double TileWidth = 37;
    private const double TileHeight = 28;
    private const double TileGap = 6;
    private const int TileColumns = 6;

    // Black box.
    private const double BoxX = 306;
    private const double BoxWidth = 188;
    private const double BoxTop = 50;
    private const double BoxBottom = 430;
    private const double SlotX = BoxX + 12;
    private const double SlotWidth = BoxWidth - 24;
    private const double SlotHeight = 26;

    // Console panel.
    private const double LogX = 520;
    private const double LogWidth = 456;
    private const double LogTop = 50;
    private const double LogBottom = 430;
    private const double ViewportTop = LogTop + 32;
    private const double ViewportBottom = LogBottom - 10;
    private const double LogLineHeight = 15;
    private const double EntryGap = 6;
    private const int WrapColumn = 60;

    // Palette. The console is dark whatever the page theme, like a terminal; the rest sits on a
    // light card so the black box reads as black on GitHub's light and dark themes alike.
    private const string Card = "#ffffff";
    private const string CardBorder = "#d0d7de";
    private const string Ink = "#1f2328";
    private const string Muted = "#656d76";
    private const string CodeBackground = "#f6f8fa";
    private const string Highlight = "#fff1a8";
    private const string Keyword = "#cf222e";
    private const string TypeName = "#953800";
    private const string StartedColour = "#0969da";
    private const string SucceededColour = "#1a7f37";
    private const string FailedColour = "#cf222e";
    private const string BoxFill = "#000000";
    private const string BoxInk = "#f0f6fc";
    private const string BoxMuted = "#8b949e";
    private const string BoxLine = "#30363d";
    private const string ProgressMeter = "#58a6ff";
    private const string FailureMeter = "#ff7b72";
    private const string Terminal = "#0d1117";
    private const string TerminalInk = "#c9d1d9";
    private const string TerminalMuted = "#8b949e";
    private const string InfoColour = "#3fb950";
    private const string WarnColour = "#d29922";
    private const string NewTrue = "#7ee787";
    private const string NewFalse = "#ffa657";
    private const string Timestamp = "#79c0ff";

    /// <summary>The loop body shown in the left panel.</summary>
    private static readonly string[] Code =
    [
        "foreach (Order order in orders)",
        "{",
        "    using IItemScope item =",
        "        op.BeginItem(order.Id);",
        "    try",
        "    {",
        "        Import(order);",
        "        item.Success();",
        "    }",
        "    catch (Exception ex)",
        "    {",
        "        item.Failure(ex);",
        "    }",
        "}",
    ];

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) { "foreach", "in", "using", "try", "catch" };
    private static readonly HashSet<string> TypeNames = new(StringComparer.Ordinal) { "Order", "IItemScope", "Exception" };

    private const int BeginItemLine = 3;
    private const int ImportLine = 6;
    private const int SuccessLine = 7;
    private const int FailureLine = 11;

    private readonly Timeline _timeline;
    private readonly double _duration;
    private readonly StringBuilder _css = new();
    private readonly StringBuilder _static = new();
    private readonly StringBuilder _moving = new();
    private int _animations;

    /// <summary>Prepares to draw <paramref name="timeline"/>.</summary>
    /// <param name="timeline">What the scripted run produced.</param>
    private Renderer(Timeline timeline)
    {
        _timeline = timeline;
        _duration = Lead + timeline.EndedAt + (2 * Flight) + 1.0 + Hold;
    }

    /// <summary>Draws <paramref name="timeline"/> as an animated SVG document.</summary>
    /// <param name="timeline">What the scripted run produced.</param>
    /// <returns>The SVG document.</returns>
    public static string Render(Timeline timeline)
    {
        Renderer renderer = new(timeline);
        return renderer.Draw();
    }

    /// <summary>Maps a fake-clock time in the loop to seconds into the animation.</summary>
    /// <param name="at">Seconds since the operation began.</param>
    /// <returns>Seconds since the animation began.</returns>
    private static double InLoop(double at) => Lead + at;

    /// <summary>Maps a fake-clock time to when its event reaches the black box.</summary>
    /// <param name="at">Seconds since the operation began.</param>
    /// <returns>Seconds since the animation began.</returns>
    private static double InBox(double at) => Lead + at + Flight;

    /// <summary>Formats a number for SVG and CSS.</summary>
    /// <param name="value">The number.</param>
    /// <returns>Its invariant, trimmed representation.</returns>
    private static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Escapes text for an XML text node or attribute.</summary>
    /// <param name="text">The raw text.</param>
    /// <returns>The escaped text.</returns>
    private static string X(string text) => SecurityElement.Escape(text);

    /// <summary>A CSS translation.</summary>
    /// <param name="x">Horizontal offset.</param>
    /// <param name="y">Vertical offset.</param>
    /// <returns>The declaration.</returns>
    private static string Translate(double x, double y) => $"transform:translate({N(x)}px,{N(y)}px)";

    /// <summary>Registers a track and returns the attribute that plays it.</summary>
    /// <param name="track">The keyframes.</param>
    /// <param name="style">Further CSS declarations for the element, if any.</param>
    /// <returns>A <c>style</c> attribute.</returns>
    private string Play(Track track, string? style = null)
    {
        string name = string.Create(CultureInfo.InvariantCulture, $"k{_animations++}");
        track.Write(name, _duration, _css);
        return style is null ? $"style=\"animation-name:{name}\"" : $"style=\"animation-name:{name};{style}\"";
    }

    /// <summary>An opacity track that is visible between <paramref name="from"/> and <paramref name="until"/>.</summary>
    /// <param name="from">When it appears, in animation seconds.</param>
    /// <param name="until">When it disappears, in animation seconds.</param>
    /// <param name="fade">How long appearing and disappearing take.</param>
    /// <returns>The track.</returns>
    private static Track Visible(double from, double until, double fade = 0.12)
    {
        Track track = new Track("opacity:0").Ease(from, fade, "opacity:1");
        return until < double.MaxValue ? track.Ease(until, fade, "opacity:0") : track;
    }

    /// <summary>Draws the whole picture.</summary>
    /// <returns>The SVG document.</returns>
    private string Draw()
    {
        DrawFrame();
        DrawLoop();
        DrawBox();
        DrawConsole();

        Track fade = new Track("opacity:1").Ease(_duration - FadeOut, FadeOut - 0.05, "opacity:0");

        StringBuilder svg = new();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(Width)}\" height=\"{N(Height)}\" viewBox=\"0 0 {N(Width)} {N(Height)}\" role=\"img\" aria-labelledby=\"title desc\">\n");
        svg.Append("<title id=\"title\">ThrottledForLoopLogging: a for loop, the library, and what reaches ILogger</title>\n");
        svg.Append("<desc id=\"desc\">A loop over 18 orders submits a started event and a succeeded or failed event for every item. ");
        svg.Append("The library always writes the first event, then holds only the most recent one and writes it when a count or time threshold is reached. ");
        svg.Append("Failures travel through their own tighter channel. When one item hangs, the held event is written again as a heartbeat with new=False.</desc>\n");
        svg.Append("<style>\n");
        svg.Append(CultureInfo.InvariantCulture, $"*{{animation-duration:{N(_duration)}s;animation-timing-function:linear;animation-iteration-count:infinite;animation-fill-mode:both}}\n");
        svg.Append(".m{font-family:").Append(Mono).Append("}\n");
        svg.Append(".s{font-family:").Append(Sans).Append("}\n");
        // Without motion, show the finished picture rather than the empty first frame.
        svg.Append(CultureInfo.InvariantCulture, $"@media (prefers-reduced-motion:reduce){{*{{animation-play-state:paused;animation-delay:-{N(_duration - Hold + 0.5)}s}}}}\n");
        svg.Append(_css);
        fade.Write("fade", _duration, svg);
        svg.Append("</style>\n");
        svg.Append(_static);
        svg.Append("<g style=\"animation-name:fade\">\n");
        svg.Append(_moving);
        svg.Append("</g>\n");
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>Draws the card, the column headings and the caption.</summary>
    private void DrawFrame()
    {
        _static.Append(CultureInfo.InvariantCulture, $"<rect x=\"0.5\" y=\"0.5\" width=\"{N(Width - 1)}\" height=\"{N(Height - 1)}\" rx=\"12\" fill=\"{Card}\" stroke=\"{CardBorder}\"/>\n");
        Heading(LoopX, "your for loop");
        Heading(LogX, "what reaches ILogger");

        const double captionY = Height - 42;
        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(LoopX)}\" y=\"{N(captionY)}\" font-size=\"11.5\" fill=\"{Muted}\">");
        _static.Append(CultureInfo.InvariantCulture, $"A scripted run through the real library on a fake clock. Every console line is its own output, wrapped to fit.</text>\n");
        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(LoopX)}\" y=\"{N(captionY + 17)}\" font-size=\"11.5\" fill=\"{Muted}\">");
        _static.Append(CultureInfo.InvariantCulture, $"Thresholds scaled down to fit: progress every {Scenario.EveryItems} events or {Scenario.EveryInterval.TotalSeconds:0} s, failures every {Scenario.FailureEveryItems} events or {Scenario.FailureEveryInterval.TotalSeconds:0} s. ");
        _static.Append("The defaults are 500 events or 10 s, and 50 failures or 5 s.</text>\n");
    }

    /// <summary>Writes a column heading.</summary>
    /// <param name="x">Left edge.</param>
    /// <param name="text">The heading.</param>
    private void Heading(double x, string text)
        => _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(x)}\" y=\"36\" font-size=\"13\" font-weight=\"600\" fill=\"{Muted}\">{X(text)}</text>\n");

    /// <summary>Where the tile for item <paramref name="item"/> sits.</summary>
    /// <param name="item">The 1-based item number.</param>
    /// <returns>The tile's top-left corner.</returns>
    private static (double X, double Y) Tile(int item)
    {
        int index = item - 1;
        return (LoopX + (index % TileColumns * (TileWidth + TileGap)), TileTop + (index / TileColumns * (TileHeight + TileGap)));
    }

    /// <summary>Draws the code, the item tiles and the events leaving them.</summary>
    private void DrawLoop()
    {
        double codeBottom = CodeTop + 14 + (Code.Length * CodeLineHeight);
        _static.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(LoopX)}\" y=\"{N(CodeTop)}\" width=\"{N(LoopWidth)}\" height=\"{N(codeBottom - CodeTop)}\" rx=\"8\" fill=\"{CodeBackground}\" stroke=\"{CardBorder}\"/>\n");

        DrawCodeHighlights();

        for (int line = 0; line < Code.Length; line++)
        {
            double y = CodeTop + 20 + (line * CodeLineHeight);
            _static.Append(CultureInfo.InvariantCulture, $"<text class=\"m\" x=\"{N(LoopX + 12)}\" y=\"{N(y)}\" font-size=\"11.5\" fill=\"{Ink}\" xml:space=\"preserve\">");
            foreach (Match token in CodeToken().Matches(Code[line]))
            {
                string colour = Keywords.Contains(token.Value) ? Keyword : TypeNames.Contains(token.Value) ? TypeName : string.Empty;
                _static.Append(colour.Length == 0 ? X(token.Value) : $"<tspan fill=\"{colour}\">{X(token.Value)}</tspan>");
            }

            _static.Append("</text>\n");
        }

        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(LoopX)}\" y=\"{N(TileTop - 10)}\" font-size=\"11.5\" fill=\"{Muted}\">orders, {Scenario.TotalItems} items</text>\n");

        foreach (ItemRun run in _timeline.Items)
        {
            (double x, double y) = Tile(run.Item);
            string label = run.Item.ToString(CultureInfo.InvariantCulture);
            string tile = $"x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(TileWidth)}\" height=\"{N(TileHeight)}\" rx=\"5\"";
            string text = $"class=\"m\" x=\"{N(x + (TileWidth / 2))}\" y=\"{N(y + 18)}\" font-size=\"11.5\" text-anchor=\"middle\"";

            _static.Append(CultureInfo.InvariantCulture, $"<rect {tile} fill=\"#eaeef2\"/><text {text} fill=\"{Muted}\">{label}</text>\n");

            string done = run.Succeeded ? SucceededColour : FailedColour;
            string doneFill = run.Succeeded ? "#dafbe1" : "#ffebe9";
            Track shape = new Track("fill:#eaeef2;stroke:#eaeef2")
                .Jump(InLoop(run.StartedAt), $"fill:#ddf4ff;stroke:{StartedColour}")
                .Jump(InLoop(run.EndedAt), $"fill:{doneFill};stroke:{done}");
            Track ink = new Track($"fill:{Muted}")
                .Jump(InLoop(run.StartedAt), $"fill:{StartedColour}")
                .Jump(InLoop(run.EndedAt), $"fill:{done}");
            _moving.Append(CultureInfo.InvariantCulture, $"<rect {tile} stroke-width=\"1.5\" {Play(shape)}/><text {text} font-weight=\"600\" {Play(ink)}>{label}</text>\n");
        }

        double legendY = TileTop + (3 * (TileHeight + TileGap)) + 16;
        double legendX = LoopX + 5;
        foreach ((string name, string colour) in new[] { ("started", StartedColour), ("succeeded", SucceededColour), ("failed", FailedColour) })
        {
            _static.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{N(legendX)}\" cy=\"{N(legendY - 4)}\" r=\"5\" fill=\"{colour}\"/>");
            _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(legendX + 10)}\" y=\"{N(legendY)}\" font-size=\"11.5\" fill=\"{Muted}\">{name}</text>\n");
            legendX += 20 + (name.Length * 6.6);
        }

        foreach (Submission submission in _timeline.Submissions)
        {
            (double x, double y) = Tile(submission.Item);
            double slotY = SlotTop(submission.Channel) + (SlotHeight / 2);
            string colour = Colour(submission.Outcome);
            double leaves = InLoop(submission.At);

            Track position = new Track(Translate(x + (TileWidth / 2), y + (TileHeight / 2))).Ease(leaves, Flight, Translate(SlotX + 8, slotY));
            Track opacity = new Track("opacity:0").Jump(leaves, "opacity:1").Jump(leaves + Flight, "opacity:0");
            _moving.Append(CultureInfo.InvariantCulture, $"<g {Play(opacity)}><circle r=\"5.5\" fill=\"{colour}\" stroke=\"#ffffff\" stroke-width=\"1.5\" {Play(position)}/></g>\n");
        }
    }

    /// <summary>Lights the line of code the loop is on.</summary>
    private void DrawCodeHighlights()
    {
        Track beginItem = new("opacity:0");
        Track import = new("opacity:0");
        Track success = new("opacity:0");
        Track failure = new("opacity:0");

        foreach (ItemRun run in _timeline.Items)
        {
            double start = InLoop(run.StartedAt);
            double end = InLoop(run.EndedAt);
            beginItem.Jump(start, "opacity:1").Jump(start + 0.12, "opacity:0");
            import.Jump(start + 0.12, "opacity:1").Jump(end, "opacity:0");
            (run.Succeeded ? success : failure).Jump(end, "opacity:1").Jump(end + 0.1, "opacity:0");
        }

        HighlightLines(BeginItemLine - 1, 2, beginItem);
        HighlightLines(ImportLine, 1, import);
        HighlightLines(SuccessLine, 1, success);
        HighlightLines(FailureLine, 1, failure);
    }

    /// <summary>Draws a highlight bar behind some lines of code.</summary>
    /// <param name="first">Zero-based index of the first line.</param>
    /// <param name="count">How many lines it covers.</param>
    /// <param name="track">When it shows.</param>
    private void HighlightLines(int first, int count, Track track)
    {
        double y = CodeTop + 9 + (first * CodeLineHeight);
        _static.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(LoopX + 4)}\" y=\"{N(y)}\" width=\"{N(LoopWidth - 8)}\" height=\"{N(count * CodeLineHeight)}\" rx=\"3\" fill=\"{Highlight}\" {Play(track)}/>\n");
    }

    /// <summary>The colour of an event dot.</summary>
    /// <param name="outcome">The event's outcome.</param>
    /// <returns>A CSS colour.</returns>
    private static string Colour(ItemOutcome outcome) => outcome switch
    {
        ItemOutcome.Started => StartedColour,
        ItemOutcome.Succeeded => SucceededColour,
        _ => FailedColour,
    };

    /// <summary>The top of a channel's section inside the box.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The section's top edge.</returns>
    private static double SectionTop(Channel channel) => channel == Channel.Progress ? BoxTop + 50 : BoxTop + 160;

    /// <summary>The top of a channel's held-event slot.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The slot's top edge.</returns>
    private static double SlotTop(Channel channel) => SectionTop(channel) + 22;

    /// <summary>Draws the black box: one section per channel, and the counters.</summary>
    private void DrawBox()
    {
        _static.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(BoxX)}\" y=\"{N(BoxTop)}\" width=\"{N(BoxWidth)}\" height=\"{N(BoxBottom - BoxTop)}\" rx=\"10\" fill=\"{BoxFill}\"/>\n");
        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(BoxX + (BoxWidth / 2))}\" y=\"{N(BoxTop + 26)}\" font-size=\"13\" font-weight=\"700\" fill=\"{BoxInk}\" text-anchor=\"middle\">ThrottledForLoopLogging</text>\n");

        DrawChannel(Channel.Progress, "progress", Scenario.EveryItems, Scenario.EveryInterval, ProgressMeter);
        DrawChannel(Channel.Failures, "failures", Scenario.FailureEveryItems, Scenario.FailureEveryInterval, FailureMeter);
        DrawCounters();
    }

    /// <summary>Draws one channel: the event it holds, how many have arrived since it last wrote, and how long ago that was.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="name">Its display name.</param>
    /// <param name="everyItems">Its count threshold.</param>
    /// <param name="everyInterval">Its time threshold.</param>
    /// <param name="meter">The colour of its meters.</param>
    private void DrawChannel(Channel channel, string name, int everyItems, TimeSpan everyInterval, string meter)
    {
        double top = SectionTop(channel);
        double slotTop = SlotTop(channel);
        double cellsTop = slotTop + SlotHeight + 10;
        double barTop = cellsTop + 18;

        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(SlotX)}\" y=\"{N(top + 12)}\" font-size=\"11\" font-weight=\"600\" fill=\"{BoxMuted}\" letter-spacing=\"0.5\">{name.ToUpperInvariant()}</text>\n");
        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(SlotX + SlotWidth)}\" y=\"{N(top + 12)}\" font-size=\"11\" fill=\"{BoxMuted}\" text-anchor=\"end\">holds the latest</text>\n");
        _static.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(SlotX)}\" y=\"{N(slotTop)}\" width=\"{N(SlotWidth)}\" height=\"{N(SlotHeight)}\" rx=\"5\" fill=\"#161b22\" stroke=\"{BoxLine}\"/>\n");

        DrawHeld(channel, slotTop);

        // Count meter: one cell per event since the channel last wrote.
        const double cellGap = 3;
        double cellWidth = (SlotWidth - 64 - ((everyItems - 1) * cellGap)) / everyItems;
        List<(double At, int Count)> counts = CountSinceLastWrite(channel);
        for (int cell = 1; cell <= everyItems; cell++)
        {
            double x = SlotX + ((cell - 1) * (cellWidth + cellGap));
            string geometry = $"x=\"{N(x)}\" y=\"{N(cellsTop)}\" width=\"{N(cellWidth)}\" height=\"9\" rx=\"2\"";
            _static.Append(CultureInfo.InvariantCulture, $"<rect {geometry} fill=\"{BoxLine}\"/>");

            Track lit = new("opacity:0");
            bool on = false;
            foreach ((double at, int count) in counts)
            {
                if (count >= cell != on)
                {
                    on = !on;
                    lit.Jump(at, on ? "opacity:1" : "opacity:0");
                }
            }

            _moving.Append(CultureInfo.InvariantCulture, $"<rect {geometry} fill=\"{meter}\" {Play(lit)}/>\n");
        }

        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(SlotX + SlotWidth)}\" y=\"{N(cellsTop + 8.5)}\" font-size=\"10.5\" fill=\"{BoxMuted}\" text-anchor=\"end\">{everyItems} events</text>\n");

        // Time meter: how much of the interval has passed since the channel last wrote.
        double barWidth = SlotWidth - 64;
        string bar = $"x=\"{N(SlotX)}\" y=\"{N(barTop)}\" width=\"{N(barWidth)}\" height=\"6\" rx=\"3\"";
        _static.Append(CultureInfo.InvariantCulture, $"<rect {bar} fill=\"{BoxLine}\"/>");
        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(SlotX + SlotWidth)}\" y=\"{N(barTop + 6.5)}\" font-size=\"10.5\" fill=\"{BoxMuted}\" text-anchor=\"end\">{everyInterval.TotalSeconds:0} seconds</text>\n");

        Track fill = TimeSinceLastWrite(channel, everyInterval.TotalSeconds);
        _moving.Append(CultureInfo.InvariantCulture, $"<rect {bar} fill=\"{meter}\" {Play(fill, $"transform-origin:{N(SlotX)}px {N(barTop)}px")}/>\n");
    }

    /// <summary>Shows which event a channel holds: bright while it is still unwritten, dim once written.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="slotTop">The slot's top edge.</param>
    private void DrawHeld(Channel channel, double slotTop)
    {
        List<Submission> submissions = [.. _timeline.Submissions.Where(s => s.Channel == channel)];
        double end = InBox(_timeline.EndedAt) + 0.4;

        for (int index = 0; index < submissions.Count; index++)
        {
            Submission held = submissions[index];
            double arrives = InBox(held.At);
            double replaced = index + 1 < submissions.Count ? InBox(submissions[index + 1].At) : end;
            Release? release = _timeline.Releases.FirstOrDefault(r => r.Label == held.Label && r.Outcome == held.Outcome && r.IsNew);
            double written = release is null ? double.MaxValue : InBox(release.At);

            string text = $"{held.Label} {held.Outcome}";
            string at = $"class=\"m\" x=\"{N(SlotX + 10)}\" y=\"{N(slotTop + 17.5)}\" font-size=\"12\"";

            double brightUntil = Math.Min(replaced, written);
            _moving.Append(CultureInfo.InvariantCulture, $"<g {Play(Visible(arrives, brightUntil, 0.04))}><circle cx=\"{N(SlotX + SlotWidth - 12)}\" cy=\"{N(slotTop + (SlotHeight / 2))}\" r=\"4\" fill=\"{Colour(held.Outcome)}\"/><text {at} fill=\"{BoxInk}\">{X(text)}</text></g>\n");

            if (written < replaced)
            {
                _moving.Append(CultureInfo.InvariantCulture, $"<text {at} fill=\"#484f58\" {Play(Visible(written, replaced, 0.04))}>{X(text)}</text>\n");
            }
        }
    }

    /// <summary>How many events a channel has received since it last wrote, as it changes.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The count after each change, in animation seconds.</returns>
    private List<(double At, int Count)> CountSinceLastWrite(Channel channel)
    {
        List<(double At, int Order, bool Arrival)> changes = [];
        changes.AddRange(_timeline.Submissions.Where(s => s.Channel == channel).Select(s => (InBox(s.At), 0, true)));
        changes.AddRange(_timeline.Releases.Where(r => r.Channel == channel).Select(r => (InBox(r.At), 1, false)));

        List<(double At, int Count)> counts = [];
        int count = 0;
        foreach ((double at, _, bool arrival) in changes.OrderBy(c => c.At).ThenBy(c => c.Order))
        {
            if (arrival)
            {
                count++;
                counts.Add((at, count));
            }
            else
            {
                // A write caused by an arrival happens in the same instant; linger so the full meter is seen.
                bool causedByArrival = counts.Count > 0 && Math.Abs(counts[^1].At - at) < 1e-6;
                count = 0;
                counts.Add((causedByArrival ? at + 0.3 : at, 0));
            }
        }

        return counts;
    }

    /// <summary>A meter that fills over one interval after every write and empties when the channel writes.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="interval">Its time threshold in seconds.</param>
    /// <returns>A track of <c>transform:scaleX</c>.</returns>
    private Track TimeSinceLastWrite(Channel channel, double interval)
    {
        static string Scale(double fraction) => $"transform:scaleX({N(Math.Clamp(fraction, 0, 1))})";

        double start = InBox(0);
        double stop = InBox(_timeline.EndedAt);
        List<double> writes = [.. _timeline.Releases.Where(r => r.Channel == channel).Select(r => InBox(r.At)), stop];

        Track track = new Track(Scale(0));
        double last = start;
        foreach (double write in writes)
        {
            double full = last + interval;
            if (full <= write)
            {
                track.To(full, Scale(1)).To(write, Scale(1));
            }
            else
            {
                track.To(write, Scale((write - last) / interval));
            }

            if (write < stop)
            {
                track.Jump(write, Scale(0));
            }

            last = write;
        }

        return track;
    }

    /// <summary>Draws the running totals and the clock at the foot of the box.</summary>
    private void DrawCounters()
    {
        double top = BoxBottom - 118;
        _static.Append(CultureInfo.InvariantCulture, $"<line x1=\"{N(SlotX)}\" y1=\"{N(top)}\" x2=\"{N(SlotX + SlotWidth)}\" y2=\"{N(top)}\" stroke=\"{BoxLine}\"/>\n");

        List<double> arrivals = [.. _timeline.Submissions.Select(s => InBox(s.At))];
        List<double> written = [.. LineTimes()];

        Ticker(top + 24, "events in", arrivals);
        Ticker(top + 46, "lines out", written);

        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(SlotX)}\" y=\"{N(top + 76)}\" font-size=\"11\" fill=\"{BoxMuted}\">clock (UTC)</text>\n");
        double y = top + 96;
        int tenths = (int)Math.Round(_timeline.EndedAt * 10);
        for (int tick = 0; tick <= tenths; tick++)
        {
            double from = tick == 0 ? 0 : InLoop(tick / 10.0);
            double until = tick == tenths ? double.MaxValue : InLoop((tick + 1) / 10.0);
            string clock = Scenario.Start.AddSeconds(tick / 10.0).ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
            Track track = tick == 0 ? new Track("opacity:1").Jump(until, "opacity:0") : new Track("opacity:0").Jump(from, "opacity:1");
            if (tick != 0 && until < double.MaxValue)
            {
                track.Jump(until, "opacity:0");
            }

            _moving.Append(CultureInfo.InvariantCulture, $"<text class=\"m\" x=\"{N(SlotX)}\" y=\"{N(y)}\" font-size=\"15\" fill=\"{BoxInk}\" {Play(track)}>{clock}</text>\n");
        }
    }

    /// <summary>A labelled number that counts up at each of <paramref name="times"/>.</summary>
    /// <param name="y">Baseline.</param>
    /// <param name="label">What it counts.</param>
    /// <param name="times">When it goes up by one, in animation seconds.</param>
    private void Ticker(double y, string label, IReadOnlyList<double> times)
    {
        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"s\" x=\"{N(SlotX)}\" y=\"{N(y)}\" font-size=\"12\" fill=\"{BoxMuted}\">{label}</text>\n");
        List<double> ordered = [.. times.Order()];
        for (int value = 0; value <= ordered.Count; value++)
        {
            double from = value == 0 ? 0 : ordered[value - 1];
            double until = value == ordered.Count ? double.MaxValue : ordered[value];
            if (until - from < 1e-6)
            {
                continue;
            }

            Track track = value == 0 ? new Track("opacity:1") : new Track("opacity:0").Jump(from, "opacity:1");
            if (until < double.MaxValue)
            {
                track.Jump(until, "opacity:0");
            }

            _moving.Append(CultureInfo.InvariantCulture, $"<text class=\"m\" x=\"{N(SlotX + SlotWidth)}\" y=\"{N(y)}\" font-size=\"13\" font-weight=\"600\" fill=\"{BoxInk}\" text-anchor=\"end\" {Play(track)}>{value}</text>\n");
        }
    }

    /// <summary>When each console line appears, in animation seconds. Lines written in the same instant appear a beat apart.</summary>
    /// <returns>One time per line of <see cref="Timeline.Lines"/>.</returns>
    private IEnumerable<double> LineTimes()
    {
        double previous = double.MinValue;
        foreach (LogLine line in _timeline.Lines)
        {
            double at = Math.Max(InBox(line.At) + Flight, previous + 0.3);
            previous = at;
            yield return at;
        }
    }

    /// <summary>Draws the console and every line that reaches it, scrolling as it fills.</summary>
    private void DrawConsole()
    {
        _static.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(LogX)}\" y=\"{N(LogTop)}\" width=\"{N(LogWidth)}\" height=\"{N(LogBottom - LogTop)}\" rx=\"8\" fill=\"{Terminal}\"/>\n");
        for (int dot = 0; dot < 3; dot++)
        {
            _static.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{N(LogX + 16 + (dot * 14))}\" cy=\"{N(LogTop + 15)}\" r=\"4.5\" fill=\"{BoxLine}\"/>");
        }

        _static.Append(CultureInfo.InvariantCulture, $"<text class=\"m\" x=\"{N(LogX + LogWidth / 2)}\" y=\"{N(LogTop + 19)}\" font-size=\"11.5\" fill=\"{TerminalMuted}\" text-anchor=\"middle\">console logger</text>\n");
        _static.Append(CultureInfo.InvariantCulture, $"<clipPath id=\"viewport\"><rect x=\"{N(LogX)}\" y=\"{N(ViewportTop)}\" width=\"{N(LogWidth)}\" height=\"{N(ViewportBottom - ViewportTop)}\"/></clipPath>\n");

        List<double> appears = [.. LineTimes()];
        List<(string[] Rows, double Top)> entries = [];
        double bottom = 0;
        foreach (LogLine line in _timeline.Lines)
        {
            string[] rows = ConsoleRows(line);
            entries.Add((rows, bottom));
            bottom += (rows.Length * LogLineHeight) + EntryGap;
        }

        // Scroll so the newest entry is always fully in view.
        double viewport = ViewportBottom - ViewportTop;
        Track scroll = new(Translate(0, 0));
        List<double> offsets = [];
        double offset = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            double entryBottom = entries[index].Top + (entries[index].Rows.Length * LogLineHeight);
            double wanted = Math.Max(offset, entryBottom - viewport + 4);
            if (wanted > offset)
            {
                scroll.Ease(appears[index] - 0.3, 0.3, Translate(0, -wanted));
                offset = wanted;
            }

            offsets.Add(offset);
        }

        _moving.Append("<g clip-path=\"url(#viewport)\">\n");
        _moving.Append(CultureInfo.InvariantCulture, $"<g {Play(scroll)}>\n");
        for (int index = 0; index < entries.Count; index++)
        {
            (string[] rows, double top) = entries[index];
            double y = ViewportTop + top;
            double appear = appears[index];

            Track flash = new Track("opacity:0").Jump(appear, "opacity:0.22").Ease(appear + 0.6, 1.6, "opacity:0");
            _moving.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(LogX + 4)}\" y=\"{N(y - 2)}\" width=\"{N(LogWidth - 8)}\" height=\"{N((rows.Length * LogLineHeight) + 3)}\" rx=\"3\" fill=\"#388bfd\" {Play(flash)}/>\n");

            _moving.Append(CultureInfo.InvariantCulture, $"<g {Play(Visible(appear, double.MaxValue, 0.15))}>");
            for (int row = 0; row < rows.Length; row++)
            {
                double baseline = y + 11 + (row * LogLineHeight);
                _moving.Append(CultureInfo.InvariantCulture, $"<text class=\"m\" x=\"{N(LogX + 12)}\" y=\"{N(baseline)}\" font-size=\"11.5\" fill=\"{TerminalInk}\" xml:space=\"preserve\">{Colourise(rows[row], row == 0)}</text>");
            }

            _moving.Append("</g>\n");
        }

        _moving.Append("</g>\n</g>\n");

        DrawWrites(appears, entries, offsets);
    }

    /// <summary>Draws a dot flying from the box to each console line as it is written.</summary>
    /// <param name="appears">When each line appears.</param>
    /// <param name="entries">Each line's rows and its top within the scrolled console.</param>
    /// <param name="offsets">How far the console has scrolled when each line appears.</param>
    private void DrawWrites(List<double> appears, List<(string[] Rows, double Top)> entries, List<double> offsets)
    {
        Queue<Release> progress = new(_timeline.Releases.Where(r => r.Channel == Channel.Progress));
        Queue<Release> failures = new(_timeline.Releases.Where(r => r.Channel == Channel.Failures));

        for (int index = 0; index < _timeline.Lines.Count; index++)
        {
            LogLine line = _timeline.Lines[index];
            (double fromX, double fromY, string colour) = line.EventId switch
            {
                9004 when progress.TryDequeue(out Release? r) => (SlotX + SlotWidth, SlotTop(Channel.Progress) + (SlotHeight / 2), Colour(r.Outcome)),
                9005 when failures.TryDequeue(out Release? r) => (SlotX + SlotWidth, SlotTop(Channel.Failures) + (SlotHeight / 2), Colour(r.Outcome)),
                _ => (BoxX + BoxWidth - 8, BoxTop + 22, line.Level >= LogLevel.Warning ? WarnColour : InfoColour),
            };

            double toY = ViewportTop + entries[index].Top - offsets[index] + 7;
            double leaves = appears[index] - Flight;
            Track position = new Track(Translate(fromX, fromY)).Ease(leaves, Flight, Translate(LogX + 6, toY));
            Track opacity = new Track("opacity:0").Jump(leaves, "opacity:1").Jump(appears[index], "opacity:0");
            _moving.Append(CultureInfo.InvariantCulture, $"<g {Play(opacity)}><circle r=\"5.5\" fill=\"{colour}\" stroke=\"#ffffff\" stroke-width=\"1.5\" {Play(position)}/></g>\n");
        }
    }

    /// <summary>Lays a line out the way the default console formatter does, wrapped to the panel.</summary>
    /// <param name="line">The captured line.</param>
    /// <returns>The rows to draw; the first is the level and category.</returns>
    private static string[] ConsoleRows(LogLine line)
    {
        const string indent = "      ";
        string level = line.Level switch
        {
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            LogLevel.Debug => "dbug",
            LogLevel.Trace => "trce",
            _ => "info",
        };

        List<string> rows = [$"{level}: {line.Category}[{line.EventId}]"];
        StringBuilder row = new(indent);
        foreach (string word in line.Message.TrimEnd().Split(' '))
        {
            if (row.Length > indent.Length && row.Length + 1 + word.Length > WrapColumn)
            {
                rows.Add(row.ToString());
                row.Clear().Append(indent);
            }

            if (row.Length > indent.Length)
            {
                row.Append(' ');
            }

            row.Append(word);
        }

        rows.Add(row.ToString());
        return [.. rows];
    }

    /// <summary>Colours the parts of a console row a reader should notice.</summary>
    /// <param name="row">The row.</param>
    /// <param name="isHeader">Whether it is the level and category row.</param>
    /// <returns>SVG text content.</returns>
    private static string Colourise(string row, bool isHeader)
    {
        if (isHeader)
        {
            int colon = row.IndexOf(':', StringComparison.Ordinal);
            string colour = row.StartsWith("info", StringComparison.Ordinal) ? InfoColour : WarnColour;
            return $"<tspan fill=\"{colour}\">{X(row[..colon])}</tspan><tspan fill=\"{TerminalMuted}\">{X(row[colon..])}</tspan>";
        }

        return Emphasis().Replace(X(row), match => match.Value switch
        {
            "new=True" => $"<tspan fill=\"{NewTrue}\">{match.Value}</tspan>",
            "new=False" => $"<tspan fill=\"{NewFalse}\" font-weight=\"700\">{match.Value}</tspan>",
            _ => $"<tspan fill=\"{Timestamp}\">{match.Value}</tspan>",
        });
    }

    /// <summary>Splits a line of code into words and everything between them.</summary>
    [GeneratedRegex(@"\w+|\W+")]
    private static partial Regex CodeToken();

    /// <summary>The new-since-last-line flag and ISO 8601 timestamps.</summary>
    [GeneratedRegex(@"new=(True|False)|\d{4}-\d\d-\d\dT[\d:.]+\+\d\d:\d\d")]
    private static partial Regex Emphasis();
}
