# throttled-logging-dotnet

Throttled `ILogger` instrumentation for long-running .NET functions and the loops inside them.

You get entry and exit visibility, live progress and an ETA — without a log line per iteration.

```
ImportOrders() over 250,000 orders
  without this library   500,002 lines
  with it                a line every 500 items or 10 seconds, whichever comes first
```

## How it looks

```csharp
public sealed class OrderImporter(IOperationLogger operations)
{
    public async Task ImportAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        using IOperationScope op = operations.BeginOperation("ImportOrders", options =>
        {
            options.TotalItems    = orders.Count;
            options.EveryItems    = 500;
            options.EveryInterval = TimeSpan.FromSeconds(10);
        });

        foreach (Order order in orders)
        {
            using IItemScope item = op.BeginItem(order.Id);
            try
            {
                await ProcessAsync(order, ct);
                item.Success();
            }
            catch (OrderException ex)
            {
                item.Failure(ex);
            }
        }

        op.Success();
    }
}
```

That overload starts from the registered defaults and applies your changes on top. Handing
`BeginOperation` an `OperationOptions` you built with `new` replaces them wholesale instead —
`IOperationLogger.DefaultOptions` gives you a copy to start from when you need the object itself.

Register it once:

```csharp
services.AddThrottledLogging(options =>
{
    options.Defaults.EveryItems    = 500;
    options.Defaults.EveryInterval = TimeSpan.FromSeconds(10);
});
```

And ask what is running, from anywhere, at any time:

```csharp
foreach (OperationSnapshot op in await registry.GetActiveOperationsAsync(ct))
{
    Console.WriteLine($"{op.Name}: {op.Processed}/{op.Total} done, {op.Failed} failed, " +
                      $"{op.RatePerSecond:N1}/s, ETA {op.Eta.Remaining}");
}
```

## What it does

- **Always logs the first event**, then holds the most recent one and releases it when a count
  or time threshold is reached. You lose the middle of a burst, never the end of it.
- **Reports the time an event was submitted**, not the time it reached the log, so no timestamp
  in a throttled line is a lie.
- **Says whether anything is new** since the last line, so a stalled loop is visible as a
  repeated heartbeat rather than silence.
- **Throttles failures on their own channel**, so 250,000 failures cost a handful of lines and
  a flood of successes can never bury the one failure you needed to see.
- **Estimates the finish** from observed throughput, which stays correct when the loop runs
  several items at once, with a confidence band that tightens as the loop progresses.
- **Is safe from any thread**, and reports its own counters.

## Layout

```
src/ThrottledLogging               the library        (net8.0; net10.0)
tests/ThrottledLogging.Tests       xUnit v3 tests     (net10.0)
samples/ThrottledLogging.Sample    a runnable tour    (net10.0)
docs/design                        the design document and its reasoning
```

## Sample

```bash
dotnet run --project samples/ThrottledLogging.Sample
```

It starts three operations at once, loops over a couple of hundred thousand items that mostly
succeed and sometimes fail, and polls the registry from a fourth thread while they run. Its five
sections are registration, the delegate form, the explicit form, the live registry and the
counters. A run ends with something like:

```
  -- 3 operation(s) in flight --
  ImportOrders        45,204 processed, 103,542 pending,  1,254 failed, 63 in flight,   23,571/s, ETA 4.0s (between 4.0s and 4.0s, 80 % confidence)
  ReindexDocuments    23,233 processed,  36,767 pending,      0 failed, 32 in flight,    9,683/s, ETA 4.0s (between 4.0s and 4.0s, 80 % confidence)
  RepairAccounts         660 processed,   1,775 pending,     65 failed, 1 in flight,      249/s, ETA 7.0s (between 7.0s and 7.0s, 80 % confidence)

Throttle counters
-----------------
  progress: 420,719 submitted, 37 logged, 420,682 held back
  failures: 4,281 submitted, 15 logged, 4,266 held back
  flushes:  34 by count, 8 by time, 0 by sweeper
  operations: 3 completed, 0 failed, 0 still active
  425,000 events became 52 log lines.
```

The bands are narrow there because the sample's items all take the same two milliseconds; a
workload with real spread gives a wider one.

## Getting started

Requires the **.NET 10 SDK** (see `global.json`).

```bash
dotnet restore
dotnet build
dotnet test
```

Tests run on [Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro),
which the .NET 10 SDK uses in place of VSTest; the opt-in lives in `global.json`.

To run a single test:

```bash
dotnet test --filter-method '*First_item_in_the_loop_is_always_logged*'
```

## Conventions

Set solution-wide in `Directory.Build.props` and `.editorconfig`:

- nullable reference types on, warnings as errors;
- XML documentation required on everything public (`CS1591` is an error);
- package versions centralised in `Directory.Packages.props`;
- 220-character line limit, file-scoped namespaces, 4-space indent.

## Design

[`docs/design/throttled-operation-logging.md`](docs/design/throttled-operation-logging.md) carries
the requirements, the API, the throttling state machine, the thread-safety plan and the reasoning
behind the ETA. Section 12 lists where the built library differs from the design as first written.

## Publishing

Releases go to NuGet.org on a tag push, via
[`.github/workflows/release.yml`](.github/workflows/release.yml). The one-time account and
credential setup, and the package ID this library still has to move off, are in
[`docs/publishing.md`](docs/publishing.md).

## Licence

MIT — see [LICENSE](LICENSE).
