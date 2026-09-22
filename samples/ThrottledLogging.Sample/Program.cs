// A runnable tour of ThrottledLogging.
//
// It starts three long-running operations at once, each looping over items that mostly
// succeed and occasionally fail, while a fourth thread polls the registry and prints what
// every operation is doing. Run it and watch the console: a quarter of a million item
// events turn into a handful of log lines, and the failure lines arrive on their own
// schedule rather than drowning the progress lines out.
//
//     dotnet run --project samples/ThrottledLogging.Sample
//
// It takes a handful of seconds on Linux and macOS, and longer on Windows, where the timer
// granularity behind the simulated work is coarser.
//
// Sections below map to the README:
//   1. Registration          - AddThrottledLogging
//   2. The delegate form     - RunAsync + ForEachAsync, the recommended default
//   3. The explicit form     - BeginOperation / BeginItem, when you need the scopes yourself
//   4. The live registry     - GetActiveOperationsAsync from another thread
//   5. The counters          - GetCounters when everything has finished
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ThrottledLogging;

// ---------------------------------------------------------------------------------------
// 1. Registration. AddThrottledLogging needs an ILoggerFactory and nothing else; the
//    TimeProvider is taken from the container when one is registered and falls back to
//    TimeProvider.System.
// ---------------------------------------------------------------------------------------
ServiceCollection services = new();

services.AddLogging(static builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(static console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    }));

services.AddThrottledLogging(static options =>
{
    // Loosened from the 500-item / 10-second defaults, because this sample submits a few
    // hundred thousand events in a few seconds and the point is to end up with a page of
    // log lines rather than a flood.
    options.Defaults.EveryItems = 10_000;
    options.Defaults.EveryInterval = TimeSpan.FromSeconds(2);
    options.Defaults.FailureEveryItems = 500;
    options.Defaults.FailureEveryInterval = TimeSpan.FromSeconds(3);
});

await using ServiceProvider provider = services.BuildServiceProvider();

IOperationLogger operations = provider.GetRequiredService<IOperationLogger>();
IOperationRegistry registry = provider.GetRequiredService<IOperationRegistry>();
ILogger<Program> log = provider.GetRequiredService<ILogger<Program>>();

using CancellationTokenSource polling = new();
Task poller = PollRegistryAsync(registry, log, polling.Token);

log.LogInformation("Starting three operations at once. Press nothing; this takes a few seconds.");

// ---------------------------------------------------------------------------------------
// 2 and 3. Three operations in flight, the scenario the registry exists for. The first two
//    use the delegate form, the third drives the scopes by hand.
// ---------------------------------------------------------------------------------------
Task[] running =
[
    ImportOrdersAsync(operations, itemCount: 150_000, parallelism: 64),
    ReindexDocumentsAsync(operations, itemCount: 60_000, parallelism: 32),
    RepairAccountsByHandAsync(operations, itemCount: 2_500),
];

await Task.WhenAll(running);

await polling.CancelAsync();
await poller;

// ---------------------------------------------------------------------------------------
// 5. The counters. Held-back events settle when the line that supersedes them is written,
//    so submitted = logged + held back only once every operation has ended - which, here,
//    it has.
// ---------------------------------------------------------------------------------------
ThrottleCounters counters = registry.GetCounters();

Console.WriteLine();
Console.WriteLine("Throttle counters");
Console.WriteLine("-----------------");
Console.WriteLine($"  progress: {counters.EventsSubmitted:N0} submitted, {counters.EventsLogged:N0} logged, {counters.EventsHeldBack:N0} held back");
Console.WriteLine($"  failures: {counters.FailuresSubmitted:N0} submitted, {counters.FailuresLogged:N0} logged, {counters.FailuresHeldBack:N0} held back");
Console.WriteLine($"  flushes:  {counters.FlushesByCount:N0} by count, {counters.FlushesByTime:N0} by time, {counters.FlushesBySweeper:N0} by sweeper");
Console.WriteLine($"  operations: {counters.CompletedOperations} completed, {counters.FailedOperations} failed, {counters.ActiveOperations} still active");

long totalEvents = counters.EventsSubmitted + counters.FailuresSubmitted;
long totalLines = counters.EventsLogged + counters.FailuresLogged;
Console.WriteLine($"  {totalEvents:N0} events became {totalLines:N0} log lines.");

// ---------------------------------------------------------------------------------------
// 2. The delegate form. RunAsync records the operation's own success or failure, and
//    ForEachAsync opens an item scope per element and records that element's outcome, so a
//    failing element is logged instead of aborting the loop.
// ---------------------------------------------------------------------------------------
static Task ImportOrdersAsync(IOperationLogger operations, int itemCount, int parallelism)
{
    // The configure overload starts from the defaults registered above. Handing BeginOperation
    // or RunAsync an OperationOptions built with `new` would replace them wholesale, which is
    // rarely what you want.
    OperationOptions options = operations.DefaultOptions;
    options.TotalItems = itemCount;
    options.EveryItems = 20_000;

    int[] orders = [.. Enumerable.Range(1, itemCount)];

    return operations.RunAsync(
        "ImportOrders",
        (operation, token) => operation.ForEachAsync(
            orders,
            static (order, _) => ProcessAsync(order, failEvery: 37),
            label: static order => $"order-{order}",
            maxDegreeOfParallelism: parallelism,
            cancellationToken: token),
        options);
}

// Same shape, one item at a time, and no explicit label delegate: ToString() names the item.
static Task ReindexDocumentsAsync(IOperationLogger operations, int itemCount, int parallelism)
{
    OperationOptions options = operations.DefaultOptions;
    options.TotalItems = itemCount;

    IEnumerable<string> documents = Enumerable.Range(1, itemCount).Select(static id => $"doc-{id:D6}");

    return operations.RunAsync(
        "ReindexDocuments",
        (operation, token) => operation.ForEachAsync(
            documents,
            static (_, _) => ProcessAsync(0, failEvery: 0),
            maxDegreeOfParallelism: parallelism,
            cancellationToken: token),
        options);
}

// ---------------------------------------------------------------------------------------
// 3. The explicit form. Useful when the loop body is not a delegate, or when the outcome
//    depends on something the loop decides. Note that disposing an item scope without an
//    outcome counts it as a failure, which is why every path below records one.
// ---------------------------------------------------------------------------------------
static async Task RepairAccountsByHandAsync(IOperationLogger operations, int itemCount)
{
    // The configure overload again, in its shortest form: a copy of the defaults, two changes.
    using IOperationScope operation = operations.BeginOperation("RepairAccounts", options =>
    {
        options.TotalItems = itemCount;

        // This loop produces failures in bursts, so give its failure channel a longer leash
        // than the process-wide default.
        options.FailureEveryItems = 200;
    });
    try
    {
        for (int account = 1; account <= itemCount; account++)
        {
            using IItemScope item = operation.BeginItem($"account-{account}");
            try
            {
                await ProcessAsync(account, failEvery: 11);
                item.Success();
            }
            catch (InvalidOperationException error)
            {
                item.Failure(error);
            }
        }

        operation.Success($"{itemCount:N0} accounts visited");
    }
    catch (Exception error)
    {
        operation.Failure(error);
        throw;
    }
}

// The pretend workload: a couple of milliseconds of waiting, and every so often a throw so
// the failure channel has something to throttle. Around 210,000 items go through it, of
// which roughly 4,300 fail.
static async ValueTask ProcessAsync(int item, int failEvery)
{
    await Task.Delay(TimeSpan.FromMilliseconds(2));

    if (failEvery > 0 && item % failEvery == 0)
    {
        throw new InvalidOperationException($"Item {item} could not be processed.");
    }
}

// ---------------------------------------------------------------------------------------
// 4. The live registry. This is the "three threads are running, show me all of them" case:
//    a snapshot per active operation, read without touching the operations themselves.
//    GetActiveOperations() is the synchronous twin of this call.
// ---------------------------------------------------------------------------------------
static async Task PollRegistryAsync(IOperationRegistry registry, ILogger log, CancellationToken cancellationToken)
{
    using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            IReadOnlyList<OperationSnapshot> active = await registry.GetActiveOperationsAsync(cancellationToken);
            if (active.Count == 0)
            {
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"  -- {active.Count} operation(s) in flight --");

            foreach (OperationSnapshot snapshot in active.OrderBy(static s => s.Name, StringComparer.Ordinal))
            {
                string eta = snapshot.Eta.HasValue
                    ? $"{Format(snapshot.Eta.Remaining)} (between {Format(snapshot.Eta.RemainingLow)} and {Format(snapshot.Eta.RemainingHigh)}, {snapshot.Eta.Confidence:P0} confidence)"
                    : "warming up";

                Console.WriteLine(
                    $"  {snapshot.Name,-18} {snapshot.Processed,7:N0} processed, {snapshot.Pending,7:N0} pending, {snapshot.Failed,6:N0} failed, " +
                    $"{snapshot.InFlight} in flight, {snapshot.RatePerSecond,8:N0}/s, ETA {eta}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        // The loop above is cancelled on purpose once the work is done.
    }

    log.LogInformation("Registry polling stopped.");

    static string Format(TimeSpan? value) => value is { } span ? $"{span.TotalSeconds:N1}s" : "?";
}
