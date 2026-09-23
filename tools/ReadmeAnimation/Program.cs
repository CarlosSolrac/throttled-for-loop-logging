using System.Text;
using ThrottledLogging.ReadmeAnimation;

// Writes docs/images/throttled-for-loop.svg, or the path given as the first argument.
string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(RepositoryRoot(), "docs", "images", "throttled-for-loop.svg");

Timeline timeline = await Scenario.RunAsync().ConfigureAwait(false);
string svg = Renderer.Render(timeline);

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, svg, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);

Console.WriteLine($"{timeline.Submissions.Count} events in, {timeline.Lines.Count} lines out, {svg.Length / 1024.0:N1} KiB written to {output}");
foreach (LogLine line in timeline.Lines)
{
    Console.WriteLine($"  {line.At,6:F2}s  {line.Level,-11} {line.Message}");
}

// The directory holding the solution file, found by walking up from the working directory.
static string RepositoryRoot()
{
    for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "ThrottledLogging.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run this from inside the repository, or pass the output path as the first argument.");
}
