using System.Diagnostics;

// Order-of-magnitude comparison of candidate "is this event new?" markers.
// Loops are inlined rather than passed as delegates so that delegate dispatch
// (~1-2 ns) does not swamp the cheapest candidate.
internal static class Program
{
    private const int Iterations = 50_000_000;
    private const int WarmUp = 1_000_000;

    private static long _sequence;
    private static Guid _guidSink;
    private static long _longSink;
    private static int _intSink;
    private static readonly string Label = "order-173402";

    private static void Main()
    {
        Console.WriteLine($".NET {Environment.Version} · {Environment.ProcessorCount} cores · {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine($"{Iterations:N0} iterations each, after {WarmUp:N0} warm-up\n");

        // Warm up every path so the JIT has tiered up before measurement.
        for (int i = 0; i < WarmUp; i++)
        {
            _guidSink = Guid.NewGuid();
            _guidSink = Guid.CreateVersion7();
            _longSink = Interlocked.Increment(ref _sequence);
            _intSink = Label.GetHashCode();
            _intSink = HashCode.Combine(Label, 1);
            _longSink = Stopwatch.GetTimestamp();
        }

        double guid4 = Time("Guid.NewGuid()            (v4, random)", static () => { for (int i = 0; i < Iterations; i++) { _guidSink = Guid.NewGuid(); } });
        double guid7 = Time("Guid.CreateVersion7()     (v7, random)", static () => { for (int i = 0; i < Iterations; i++) { _guidSink = Guid.CreateVersion7(); } });
        double seq = Time("Interlocked.Increment(ref long)       ", static () => { for (int i = 0; i < Iterations; i++) { _longSink = Interlocked.Increment(ref _sequence); } });
        double hash = Time("string.GetHashCode()      (12 chars) ", static () => { for (int i = 0; i < Iterations; i++) { _intSink = Label.GetHashCode(); } });
        double comb = Time("HashCode.Combine(label, outcome)      ", static () => { for (int i = 0; i < Iterations; i++) { _intSink = HashCode.Combine(Label, 1); } });
        double ts = Time("Stopwatch.GetTimestamp()              ", static () => { for (int i = 0; i < Iterations; i++) { _longSink = Stopwatch.GetTimestamp(); } });

        Console.WriteLine($"\nRelative to one interlocked sequence increment ({seq:F2} ns):");
        Console.WriteLine($"  Guid.NewGuid()          {guid4 / seq,6:F1}x");
        Console.WriteLine($"  Guid.CreateVersion7()   {guid7 / seq,6:F1}x");
        Console.WriteLine($"  string.GetHashCode()    {hash / seq,6:F1}x");
        Console.WriteLine($"  HashCode.Combine(..)    {comb / seq,6:F1}x");
        Console.WriteLine($"  Stopwatch.GetTimestamp  {ts / seq,6:F1}x");
        Console.WriteLine($"\n(sinks {_guidSink} {_longSink} {_intSink})");
    }

    private static double Time(string name, Action body)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Stopwatch sw = Stopwatch.StartNew();
        body();
        sw.Stop();
        double nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / Iterations;
        Console.WriteLine($"{name}  {nsPerOp,8:F2} ns/op");
        return nsPerOp;
    }
}
