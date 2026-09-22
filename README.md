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
        using IOperationScope op = operations.BeginOperation("ImportOrders", new OperationOptions
        {
            TotalItems    = orders.Count,
            EveryItems    = 500,
            EveryInterval = TimeSpan.FromSeconds(10),
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
src/ThrottledLogging            the library      (net8.0; net10.0)
tests/ThrottledLogging.Tests    xUnit v3 tests   (net10.0)
docs/design                     the design document and its reasoning
```

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

## Licence

MIT — see [LICENSE](LICENSE).
