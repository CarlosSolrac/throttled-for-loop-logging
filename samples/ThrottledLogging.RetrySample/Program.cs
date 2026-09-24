// The retry loop: what happens when the same item is attempted more than once.
//
// A loop that retries has to decide what an "item" is, and the two obvious answers give very
// different logs. This sample runs the same flaky workload both ways over the same data, then
// shows the one thing neither way tells you.
//
//     dotnet run --project samples/ThrottledLogging.RetrySample
//
// The workload is deterministic, so two runs print the same numbers.
//
//   Pass A  an item scope per ATTEMPT    counts drift away from TotalItems, the ETA follows
//   Pass B  an item scope per ITEM       counts stay honest, retries stay inside the item
//   Pass C  one item retried in place    every line names the same order and says new=True
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ThrottledLogging;

const int OrderCount = 1_000;
const int MaxAttempts = 4;

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
    options.Defaults.EveryItems = 500;
    options.Defaults.EveryInterval = TimeSpan.FromSeconds(2);
    options.Defaults.FailureEveryItems = 100;
    options.Defaults.FailureEveryInterval = TimeSpan.FromSeconds(2);
});

await using ServiceProvider provider = services.BuildServiceProvider();

IOperationLogger operations = provider.GetRequiredService<IOperationLogger>();
IOperationRegistry registry = provider.GetRequiredService<IOperationRegistry>();

Console.WriteLine($"{OrderCount:N0} orders, up to {MaxAttempts} attempts each. {FlakyStore.PoisonCount(OrderCount)} of them never succeed.");

OperationSnapshot perAttempt = await RunPerAttemptAsync(operations, OrderCount);
OperationSnapshot perItem = await RunPerItemAsync(operations, OrderCount);

Compare(perAttempt, perItem, OrderCount);

await RunStuckItemAsync(operations);

Report(registry.GetCounters());

// ---------------------------------------------------------------------------------------
// Pass A. The obvious way: every attempt gets its own item scope.
//
// It reads naturally — one scope around one call — and the failure channel does show each
// failed attempt. But an operation told to expect N items now submits one scope per attempt,
// so Processed plus Failed runs past TotalItems, Pending hits zero long before the loop ends,
// and the ETA counts down to a finish line that was never in the right place: watch it reach
// zero in the output below with half the orders still to come. The library believes what it
// is told, and it has been told that a scope is an item.
// ---------------------------------------------------------------------------------------
static async Task<OperationSnapshot> RunPerAttemptAsync(IOperationLogger operations, int orderCount)
{
    Console.WriteLine();
    Console.WriteLine("Pass A: an item scope per attempt");
    Console.WriteLine("---------------------------------");

    using IOperationScope operation = operations.BeginOperation("ImportOrders/per-attempt", options => options.TotalItems = orderCount);

    for (int order = 1; order <= orderCount; order++)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using IItemScope item = operation.BeginItem($"order-{order}");
            try
            {
                await FlakyStore.ImportAsync(order, attempt);
                item.Success();
                break;
            }
            catch (InvalidOperationException error)
            {
                item.Failure(error);
            }
        }
    }

    OperationSnapshot snapshot = operation.Snapshot();
    operation.Success();
    return snapshot;
}

// ---------------------------------------------------------------------------------------
// Pass B. One item scope per order, with the retries inside it.
//
// The scope now measures what the caller actually cares about: getting this order in,
// however many attempts that took. Processed plus Failed equals TotalItems, Pending counts
// down truthfully, and the item's duration is the whole retry sequence, which is what the
// ETA should be extrapolating from. Only giving up is a failure, so the failure channel
// carries the orders that are really lost rather than every transient blip.
//
// Intermediate attempts are not lost — log them yourself, at a level you can turn off. They
// are debugging detail about one item, not progress through the loop.
// ---------------------------------------------------------------------------------------
static async Task<OperationSnapshot> RunPerItemAsync(IOperationLogger operations, int orderCount)
{
    Console.WriteLine();
    Console.WriteLine("Pass B: an item scope per order, retries inside it");
    Console.WriteLine("-------------------------------------------------");

    using IOperationScope operation = operations.BeginOperation("ImportOrders/per-item", options => options.TotalItems = orderCount);

    for (int order = 1; order <= orderCount; order++)
    {
        using IItemScope item = operation.BeginItem($"order-{order}");

        InvalidOperationException? lastError = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await FlakyStore.ImportAsync(order, attempt);
                lastError = null;
                break;
            }
            catch (InvalidOperationException error)
            {
                lastError = error;
            }
        }

        if (lastError is null)
        {
            item.Success();
        }
        else
        {
            // Out of attempts: this order is the one worth waking someone up about.
            item.Failure(lastError);
        }
    }

    OperationSnapshot snapshot = operation.Snapshot();
    operation.Success();
    return snapshot;
}

// ---------------------------------------------------------------------------------------
// Pass C. One order, retried in place, forever.
//
// This is the case the throttler cannot currently describe. Each attempt is a genuinely new
// event, so IsNew is true on every emitted line and the held-back count keeps climbing — the
// same shape a log of 300 different orders would have. What the line does not say is that
// the label has not changed since the last one, which is the difference between "getting
// through the work" and "stuck on order 7".
//
// Contrast it with a loop that submits nothing at all: there the sweeper re-emits the held
// event with IsNew false, and the stall is obvious.
// ---------------------------------------------------------------------------------------
static async Task RunStuckItemAsync(IOperationLogger operations)
{
    Console.WriteLine();
    Console.WriteLine("Pass C: one order, retried in place");
    Console.WriteLine("-----------------------------------");

    using IOperationScope operation = operations.BeginOperation("ImportOrders/stuck", options =>
    {
        options.TotalItems = 1;
        options.EveryItems = 60;
        options.FailureEveryItems = 60;
    });

    for (int attempt = 1; attempt <= 300; attempt++)
    {
        using IItemScope item = operation.BeginItem("order-7");
        try
        {
            await FlakyStore.ImportAsync(FlakyStore.PoisonOrder, attempt);
            item.Success();
            break;
        }
        catch (InvalidOperationException error)
        {
            item.Failure(error);
        }
    }

    // Now the other shape of stuck: one attempt that hangs, submitting nothing at all. The
    // sweeper re-emits the held Started event, and those lines say new=False. Compare them
    // with the retry lines above, which describe an equally stuck loop and say new=True.
    Console.WriteLine();
    Console.WriteLine("  ... and the same order hanging inside one attempt, for contrast:");

    using (IItemScope hanging = operation.BeginItem("order-7"))
    {
        await Task.Delay(TimeSpan.FromSeconds(5.5));
        hanging.Failure(new InvalidOperationException("order-7 timed out."));
    }

    operation.Failure(new InvalidOperationException("order-7 never imported"));
}

static void Compare(OperationSnapshot perAttempt, OperationSnapshot perItem, int orderCount)
{
    Console.WriteLine();
    Console.WriteLine("What the two passes counted");
    Console.WriteLine("---------------------------");
    Console.WriteLine($"                        expected   processed   failed   sum vs expected");
    Console.WriteLine($"  per attempt           {orderCount,8:N0}   {perAttempt.Processed,9:N0}   {perAttempt.Failed,6:N0}   {Drift(perAttempt, orderCount)}");
    Console.WriteLine($"  per item              {orderCount,8:N0}   {perItem.Processed,9:N0}   {perItem.Failed,6:N0}   {Drift(perItem, orderCount)}");
    Console.WriteLine();
    Console.WriteLine($"  mean item duration:   per attempt {Format(perAttempt.MeanItemDuration)}, per item {Format(perItem.MeanItemDuration)}");
    Console.WriteLine("  Per attempt, an item is one call; per item, it is the whole retry sequence, which is");
    Console.WriteLine("  the number the ETA should be extrapolating from.");

    static string Drift(OperationSnapshot snapshot, int orderCount)
    {
        long sum = snapshot.Processed + snapshot.Failed;
        long drift = sum - orderCount;
        return drift == 0 ? $"{sum,6:N0}   (exact)" : $"{sum,6:N0}   ({drift:+#,##0;-#,##0} too many)";
    }

    static string Format(TimeSpan? value) => value is { } span ? $"{span.TotalMilliseconds:N2}ms" : "unknown";
}

static void Report(ThrottleCounters counters)
{
    Console.WriteLine();
    Console.WriteLine("Throttle counters");
    Console.WriteLine("-----------------");
    Console.WriteLine($"  progress: {counters.EventsSubmitted:N0} submitted, {counters.EventsLogged:N0} logged, {counters.EventsHeldBack:N0} held back");
    Console.WriteLine($"  failures: {counters.FailuresSubmitted:N0} submitted, {counters.FailuresLogged:N0} logged, {counters.FailuresHeldBack:N0} held back");
    Console.WriteLine($"  operations: {counters.CompletedOperations} completed, {counters.FailedOperations} failed");
}

// A store that fails the first few attempts on some orders and never accepts others. Nothing
// random: order 7 and every 97th order after it are poison, and the rest need (order % 3)
// failures before they go through, so the numbers above are the same on every run.
internal static class FlakyStore
{
    public const int PoisonOrder = 7;

    public static int PoisonCount(int orderCount) => orderCount < PoisonOrder ? 0 : ((orderCount - PoisonOrder) / 97) + 1;

    public static async ValueTask ImportAsync(int order, int attempt)
    {
        // A millisecond of pretend network call, so the durations and the ETA mean something.
        await Task.Delay(TimeSpan.FromMilliseconds(1));

        if (IsPoison(order) || attempt <= order % 3)
        {
            throw new InvalidOperationException($"order-{order} rejected on attempt {attempt}.");
        }
    }

    private static bool IsPoison(int order) => order >= PoisonOrder && (order - PoisonOrder) % 97 == 0;
}
