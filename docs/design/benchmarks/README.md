# Event-marker benchmark

Backs the table in
[`../throttled-operation-logging.md` §4.2](../throttled-operation-logging.md) — the cost of
each candidate marker for deciding whether a held event is new since the last emitted line.

Deliberately kept out of the solution: it is a one-off measurement, not a regression suite.
To reproduce, copy both files into an empty directory, rename
`EventMarkerBenchmark.csproj.txt` to `bench.csproj`, and run:

```bash
dotnet run -c Release
```

It is a warmed inline loop with a sink rather than BenchmarkDotNet, so treat the ratios as
sound and the absolute nanoseconds as machine-dependent. If these numbers ever need to
carry weight in a decision, redo them under BenchmarkDotNet.

Result on the machine that produced the table (.NET 10.0.12, x64, 4-core shared VM,
50,000,000 iterations each):

```
Guid.NewGuid()            (v4, random)    367.65 ns/op    43.0x
Guid.CreateVersion7()     (v7, random)    393.29 ns/op    46.0x
Stopwatch.GetTimestamp()                   21.93 ns/op     2.6x
HashCode.Combine(label, outcome)           15.77 ns/op     1.8x
string.GetHashCode()      (12 chars)       10.11 ns/op     1.2x
Interlocked.Increment(ref long)             8.54 ns/op     1.0x
```
