# Context across hosts: Azure Functions and Application Insights

ThrottledLogging keeps everything in memory, inside one process. That is deliberate: it keeps the
hot path cheap, and it never slows your loop down with I/O. It also means that nothing about an
operation survives the process. When Azure Functions recycles a worker, scales out to more
instances or retries a failed invocation, each run starts from zero, with a new operation id,
counts from 0 and no ETA history.

What *does* survive is whatever the host and the trigger know about the run: a message id, a
delivery count, a Durable instance id, a trace id. This guide shows how to put those on every line
the library writes, so that a Kusto query can stitch the runs back together afterwards.

Nothing here needs Azure. The library depends only on `Microsoft.Extensions.Logging`
abstractions, and everything below works the same with any logging provider; Application
Insights is simply the one that stores the context most usefully.

- [What reaches every line](#what-reaches-every-line)
- [What survives a restart, by trigger](#what-survives-a-restart-by-trigger)
- [Wiring up the worker](#wiring-up-the-worker)
- [One helper for every trigger](#one-helper-for-every-trigger)
- [Storage queue](#storage-queue)
- [Service Bus](#service-bus)
- [Timer](#timer)
- [Durable Functions](#durable-functions)
- [Without Azure](#without-azure)
- [Reading it back in Application Insights](#reading-it-back-in-application-insights)
- [Things to know](#things-to-know)

## What reaches every line

Since 0.2.0 three things carry context onto the lines the library writes:

| Mechanism | What it carries | Where it applies |
|---|---|---|
| **The caller's own logging scope and `Activity`** | Whatever you opened with `ILogger.BeginScope` before calling `BeginOperation`, and `Activity.Current` (which Application Insights uses as `operation_Id`) | Every line. Lines written on your thread have them anyway; the operation also captures them at `BeginOperation` and restores them for the heartbeat lines written by the background sweeper and for the shutdown flush |
| **`OperationOptions.Scope`** | Key/value pairs you hand to one operation | Every line that operation writes, and only those |
| **`OperationId`** | The operation's own `Guid` | Every line, as a structured field and in the message text |

Use the first when your own lines should carry the same context, which in a function is almost
always the case. Use `OperationOptions.Scope` when there is no outer scope to open, such as a
console job that logs only through the library. Don't put the same keys in both: a text sink with
scopes enabled would print them twice. Application Insights doesn't mind, because repeated keys
just overwrite each other.

When the host shuts down, disposing `OperationLogger` writes out every operation still running:
its held events, then one line (event id **9008**) saying it was still running, with the counts it
had reached. If that is an operation's last line, the process died before the operation finished.

## What survives a restart, by trigger

| Trigger | BusinessKey (stable across retries) | Attempt | Also worth logging |
|---|---|---|---|
| Storage queue | `QueueMessage.MessageId` | `DequeueCount` | `InsertedOn` |
| Service Bus | `ServiceBusReceivedMessage.MessageId` | `DeliveryCount` | `CorrelationId`, `SequenceNumber`, `EnqueuedTime`; the sender's trace context arrives with the message |
| Timer | The data the run covers, e.g. the hour being imported, read from a checkpoint in storage | Always 1 (count InvocationIds instead) | `IsPastDue`, `ScheduleStatus.Last`/`Next` (persisted by the host in storage) |
| Durable Functions | The orchestration `InstanceId` | Not visible to an activity | Chunk index; finished chunks survive, because each completed activity is checkpointed |

Every trigger also has an `InvocationId`, which is new on every run, and runs on a host instance
(`WEBSITE_INSTANCE_ID`, shown as `cloud_RoleInstance`). Neither survives a restart, and that is
exactly what makes them useful: a change of `InvocationId` under one BusinessKey marks a retry,
and a change of process id marks a recycle.

Don't read Application Insights at runtime to recover context. Ingestion lags by minutes, and it
is a query store, not a state store. Treat it as the place to look afterwards.

## Wiring up the worker

For the .NET isolated worker. The in-process model reaches end of support in November 2026. The
examples were compiled against these packages:

| Package | Version |
|---|---|
| `Microsoft.Azure.Functions.Worker` | 2.52.0 |
| `Microsoft.Azure.Functions.Worker.Sdk` | 2.1.0 |
| `Microsoft.Azure.Functions.Worker.ApplicationInsights` | 2.51.0 |
| `Microsoft.ApplicationInsights.WorkerService` | 2.23.0 |
| `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues` | 5.5.5 |
| `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` | 5.24.0 |
| `Microsoft.Azure.Functions.Worker.Extensions.Timer` | 4.3.1 |
| `Microsoft.Azure.Functions.Worker.Extensions.DurableTask` | 1.19.1 |
| `ThrottledForLoopLogging` | 0.2.0-alpha |

`Microsoft.ApplicationInsights.WorkerService` stays on 2.x on purpose. The worker's Application
Insights package depends on the 2.x SDK, and 3.x is a different, OpenTelemetry-based SDK. If you
move to OpenTelemetry, set `IncludeScopes = true` on its logging options, because scopes are off by
default there.

`Program.cs`:

```csharp
using Examples;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.ApplicationInsights;
using ThrottledLogging;

FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);

// 1. Send the worker's own ILogger output to Application Insights directly. Without these two
//    calls, logs travel to the Functions host over gRPC and are re-logged there, which keeps the
//    message and level but drops the logging scopes. The scopes are the whole point here: they
//    carry BusinessKey, Attempt and InvocationId into customDimensions.
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// 2. Scopes become customDimensions only when the provider includes them. It does by default in
//    Microsoft.ApplicationInsights.WorkerService 2.x; saying so explicitly guards against a change.
builder.Logging.Services.Configure<ApplicationInsightsLoggerOptions>(options => options.IncludeScopes = true);

// 3. The Application Insights SDK adds a filter rule that drops everything below Warning. Remove
//    it, or every Information line this library writes (entry, progress, success) disappears and
//    only failures reach the portal. Levels are then set by the worker's own configuration (the
//    "Logging" section of appsettings.json or the app settings Logging__LogLevel__...), not by
//    host.json: host.json governs the host process's logs, and these come from the worker.
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    LoggerFilterRule? defaultRule = options.Rules.FirstOrDefault(rule =>
        rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

// 4. The library itself, with its settings in the "ThrottledLogging" section of configuration
//    (appsettings.json below, or app settings such as ThrottledLogging__Defaults__EveryItems).
//    A function can still override them per operation (start from DefaultOptions, see below).
//    The binding is read when OperationLogger is first resolved, so adding the JSON file after
//    this line is fine.
builder.Services.AddThrottledLogging(builder.Configuration.GetSection("ThrottledLogging"));

// Stand-in for your data access. Replace with whatever the functions really read and write.
builder.Services.AddSingleton<IOrderStore, InMemoryOrderStore>();

// The worker reads appsettings.json only if asked to. Without this, a "Logging" or
// "ThrottledLogging" section there has no effect. reloadOnChange lets a changed file reach the
// library without a restart; see "Changing settings without a restart" below.
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

IHost host = builder.Build();

// 5. Shutdown, in the right order. When the platform recycles or scales in, the host signals
//    ApplicationStopping first and disposes services last. By the time the container disposes
//    OperationLogger, Application Insights may already have stopped sending. So:
//    - on ApplicationStopping, dispose OperationLogger early. That writes every running
//      operation's held events plus one "still running" line (event 9008) while telemetry is
//      still flowing. The operations themselves keep working; a function that finishes during
//      the drain still logs its normal end line afterwards.
//    - on ApplicationStopped, flush the telemetry channel so those lines leave the machine.
//    Operations that finish during the drain still write their end lines afterwards, but those
//    rely on the telemetry channel's own flush when the container disposes it.
IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
OperationLogger operationLogger = host.Services.GetRequiredService<OperationLogger>();
TelemetryClient telemetry = host.Services.GetRequiredService<TelemetryClient>();
lifetime.ApplicationStopping.Register(operationLogger.Dispose);
lifetime.ApplicationStopped.Register(() => telemetry.FlushAsync(CancellationToken.None).GetAwaiter().GetResult());

host.Run();
```

Two lines in there are easy to leave out. If the worker doesn't send its own telemetry
(`AddApplicationInsightsTelemetryWorkerService` plus `ConfigureFunctionsApplicationInsights`), logs
reach Application Insights through the Functions host, which keeps the message and drops the
scopes. If the default filter rule stays, nothing below `Warning` arrives at all.

`appsettings.json`, with these defaults suiting a loop of tens of thousands of short items. Every
key is optional; anything left out keeps the library's default. Time spans use `hh:mm:ss`.

```json
{
  "ThrottledLogging": {
    "EnableSweeper": true,
    "MinimumSweepInterval": "00:00:01",
    "MaximumSweepInterval": "00:00:30",
    "Defaults": {
      "EveryItems": 1000,
      "EveryInterval": "00:00:30",
      "Level": "Information",
      "FailureEveryItems": 100,
      "FailureEveryInterval": "00:00:10",
      "FailureLevel": "Warning"
    }
  }
}
```

Mark the file `CopyToOutputDirectory` = `PreserveNewest` in the project, or the worker never sees
it. `OnEmitted` is a delegate and cannot come from configuration; pass it in code as the second
argument, `AddThrottledLogging(section, options => options.OnEmitted = ...)`, which is re-applied
after every reload.

### Changing settings without a restart

When configuration changes (a reloaded file, a refreshed Azure App Configuration provider, a
mounted Kubernetes ConfigMap):

- **Operations begun afterwards** use the new defaults. **Operations already running** keep the
  settings they began with, so a loop's thresholds never shift halfway through it.
- **`EnableSweeper`** starts or stops the sweeper. **Sweep intervals** take effect from the
  sweeper's next tick.
- **Invalid settings** (say `EveryItems: 0`, or a maximum sweep interval below the minimum) are
  rejected as a whole: the library logs event 9011 at `Warning` with the reason, and the last good
  settings stay in force. Settings invalid at startup still throw, when `OperationLogger` is first
  resolved.

In Azure Functions, changing an app setting in the portal restarts the worker anyway, so reload
matters mostly where configuration changes underneath a running process.

The examples below stand in for real data access with a small interface. Swap it for your own:

```csharp
namespace Examples;

/// <summary>One unit of work inside a batch.</summary>
/// <param name="Id">The order's identifier; used as the item label in the log.</param>
/// <param name="Payload">Whatever processing needs.</param>
public sealed record Order(string Id, string Payload);

/// <summary>Stand-in for the app's data access, so the examples compile on their own.</summary>
public interface IOrderStore
{
    /// <summary>Counts the orders in a batch, so the operation can report an ETA.</summary>
    Task<long> CountAsync(string batchId, CancellationToken cancellationToken);

    /// <summary>Streams a batch's orders.</summary>
    IAsyncEnumerable<Order> ReadAsync(string batchId, CancellationToken cancellationToken);

    /// <summary>Processes one order. Throws when it fails.</summary>
    Task ProcessAsync(Order order, CancellationToken cancellationToken);

    /// <summary>The oldest hour, before <paramref name="before"/>, not yet marked done; <see langword="null"/> when all are.</summary>
    /// <remarks>Backed by durable storage (a table row, a blob), so it survives restarts. This is the timer's checkpoint.</remarks>
    Task<DateTimeOffset?> OldestPendingHourAsync(DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>Records that <paramref name="hour"/> has been imported.</summary>
    Task MarkHourDoneAsync(DateTimeOffset hour, CancellationToken cancellationToken);
}

/// <summary>A trivial implementation for local runs.</summary>
public sealed class InMemoryOrderStore : IOrderStore
{
    /// <inheritdoc />
    public Task<long> CountAsync(string batchId, CancellationToken cancellationToken) => Task.FromResult(1_000L);

    /// <inheritdoc />
    public async IAsyncEnumerable<Order> ReadAsync(string batchId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < 1_000; i++)
        {
            await Task.Yield();
            yield return new Order($"{batchId}-{i}", string.Empty);
        }
    }

    /// <inheritdoc />
    public Task ProcessAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<DateTimeOffset?> OldestPendingHourAsync(DateTimeOffset before, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);

    /// <inheritdoc />
    public Task MarkHourDoneAsync(DateTimeOffset hour, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

## One helper for every trigger

Each function builds the same set of fields and opens them as a scope before it begins the
operation. That way one Kusto query works whatever trigger started the run.

```csharp
using System.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Examples;

/// <summary>
/// Builds the one set of key/value pairs every function in this app attaches to its logs, so that
/// the same Kusto query works whichever trigger started the run.
/// </summary>
/// <remarks>
/// Each field answers one question someone asks when reading the log after an incident:
/// <list type="table">
///   <item><term>BusinessKey</term><description>Which piece of work was this? Stable across retries, restarts and instances. The field to search by.</description></item>
///   <item><term>Attempt</term><description>How many times has that work been tried? 1 on the first delivery.</description></item>
///   <item><term>Trigger</term><description>What started it: Queue, ServiceBus, Timer or Durable.</description></item>
///   <item><term>InvocationId</term><description>Which single execution? New on every run, retries included.</description></item>
///   <item><term>FunctionName</term><description>Which function? Useful when several share one work item.</description></item>
///   <item><term>HostInstance</term><description>Which machine? The same value Application Insights shows as cloud_RoleInstance.</description></item>
///   <item><term>ProcessId / ProcessStartedUtc</term><description>Which worker process? A change between two lines of one BusinessKey means the process was recycled in between.</description></item>
///   <item><term>TraceId</term><description>The W3C trace id; equals operation_Id in Application Insights, and links producer and consumer when the trigger propagates it.</description></item>
/// </list>
/// </remarks>
public static class InvocationContext
{
    // Read once: neither can change while the process lives.
    private static readonly string HostInstance = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? Environment.MachineName;
    private static readonly DateTimeOffset ProcessStartedUtc = new(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>Creates the pairs for one invocation.</summary>
    /// <param name="context">The worker's per-invocation context.</param>
    /// <param name="trigger">A short name for the trigger type.</param>
    /// <param name="businessKey">What identifies the work across retries: a message id, a batch id, a Durable instance id.</param>
    /// <param name="attempt">Which delivery of that work this is, counting from 1.</param>
    /// <returns>A new dictionary; the caller may add fields specific to its trigger.</returns>
    public static Dictionary<string, object?> Create(FunctionContext context, string trigger, string businessKey, long attempt)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BusinessKey"] = businessKey,
            ["Attempt"] = attempt,
            ["Trigger"] = trigger,
            ["InvocationId"] = context.InvocationId,
            ["FunctionName"] = context.FunctionDefinition.Name,
            ["HostInstance"] = HostInstance,
            ["ProcessId"] = Environment.ProcessId,
            ["ProcessStartedUtc"] = ProcessStartedUtc,
            // Activity.Current is the invocation's activity when Application Insights is wired up in
            // the worker; the trace context the host passed in is the fallback. Either way this is
            // the bare 32-hex-digit trace id, the same value Application Insights stores as operation_Id.
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? TraceIdFrom(context.TraceContext.TraceParent),
        };
    }

    /// <summary>
    /// Extracts the trace id from a W3C <c>traceparent</c> header, which reads
    /// <c>version-traceid-parentid-flags</c>, for example
    /// <c>00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01</c>.
    /// </summary>
    /// <param name="traceParent">The header value, or <see langword="null"/>.</param>
    /// <returns>The 32-character trace id, or <see langword="null"/> when the header is missing or malformed.</returns>
    private static string? TraceIdFrom(string? traceParent)
    {
        if (string.IsNullOrEmpty(traceParent))
        {
            return null;
        }

        string[] parts = traceParent.Split('-');
        return parts.Length == 4 && parts[1].Length == 32 ? parts[1] : null;
    }

    /// <summary>
    /// Opens the pairs as a logging scope. Everything logged inside it, by the function and by
    /// ThrottledLogging (including the background heartbeat, which restores the scope captured at
    /// BeginOperation), carries them.
    /// </summary>
    /// <param name="logger">Any logger from the app's factory; scopes are shared across categories.</param>
    /// <param name="pairs">From <see cref="Create"/>.</param>
    /// <returns>The scope; dispose it when the invocation ends.</returns>
    public static IDisposable? BeginInvocationScope(this ILogger logger, IReadOnlyDictionary<string, object?> pairs)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return logger.BeginScope(pairs);
    }
}
```

## Storage queue

The simplest pattern. The message is the durable part: its `MessageId` holds steady across
redeliveries and `DequeueCount` counts them.

```csharp
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ThrottledLogging;

namespace Examples;

/// <summary>
/// Storage queue trigger: one message names a batch, and the function works through every order in it.
/// </summary>
/// <remarks>
/// <para>
/// What survives a restart here is the message. Its <c>MessageId</c> stays the same on every
/// redelivery and its <c>DequeueCount</c> goes up by one each time it is dequeued, so
/// BusinessKey = MessageId and Attempt = DequeueCount group every try of the same batch together,
/// on any instance, and number them.
/// </para>
/// <para>
/// While the function runs, the queue trigger keeps extending the message's visibility, so a long
/// loop does not by itself hand the message to another instance. The message comes back when the
/// function throws (after the host's <c>visibilityTimeout</c>), or when the process dies and can no
/// longer extend it (once the current visibility window lapses). Either way the next attempt has a
/// higher DequeueCount. After <c>maxDequeueCount</c> attempts (5 by default) the message moves to
/// the <c>-poison</c> queue instead, and the last attempt in the log is the last there will be.
/// </para>
/// </remarks>
public sealed class QueueImport
{
    private readonly IOperationLogger _operations;
    private readonly IOrderStore _orders;
    private readonly ILogger<QueueImport> _logger;

    /// <summary>Created by the worker through dependency injection.</summary>
    public QueueImport(IOperationLogger operations, IOrderStore orders, ILogger<QueueImport> logger)
    {
        _operations = operations;
        _orders = orders;
        _logger = logger;
    }

    /// <summary>Imports the batch the message names.</summary>
    /// <param name="message">The raw queue message, bound as <see cref="QueueMessage"/> for its metadata. Its body is the batch id.</param>
    /// <param name="context">The invocation.</param>
    /// <param name="cancellationToken">Signalled when the host is shutting down.</param>
    [Function(nameof(QueueImport))]
    public async Task RunAsync([QueueTrigger("order-batches")] QueueMessage message, FunctionContext context, CancellationToken cancellationToken)
    {
        string batchId = message.Body.ToString();

        // Everything that identifies this run, opened once for the whole invocation.
        Dictionary<string, object?> pairs = InvocationContext.Create(context, "Queue", businessKey: message.MessageId, attempt: message.DequeueCount);
        pairs["BatchId"] = batchId;
        pairs["EnqueuedUtc"] = message.InsertedOn;
        using IDisposable? scope = _logger.BeginInvocationScope(pairs);

        if (message.DequeueCount > 1)
        {
            // Not throttled, and it should not be: this line is what tells a reader the previous
            // attempt did not finish, whatever its own log says.
            _logger.LogWarning("Batch {BatchId} is being retried, attempt {Attempt}", batchId, message.DequeueCount);
        }

        // Start from the configured defaults and change only what differs; a fresh
        // OperationOptions would silently discard what Program.cs configured.
        OperationOptions options = _operations.DefaultOptions;
        options.TotalItems = await _orders.CountAsync(batchId, cancellationToken);

        // BeginOperation runs inside the scope, so the operation captures it: every line the
        // operation writes, including heartbeats from the background sweeper thread, carries
        // BusinessKey, Attempt and InvocationId as customDimensions.
        using IOperationScope operation = _operations.BeginOperation("ImportOrders", options);
        try
        {
            await foreach (Order order in _orders.ReadAsync(batchId, cancellationToken))
            {
                using IItemScope item = operation.BeginItem(order.Id);
                try
                {
                    await _orders.ProcessAsync(order, cancellationToken);
                    item.Success();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // One bad order does not fail the batch. The failure channel throttles these on
                    // its own, so 50,000 bad orders still produce a readable log.
                    item.Failure(error);
                }
            }

            operation.Success($"batch {batchId}");
        }
        catch (Exception error)
        {
            // Includes cancellation at shutdown. The message is not deleted, so it will be
            // redelivered with DequeueCount + 1 and the next attempt's lines join this one's
            // under the same BusinessKey.
            operation.Failure(error);
            throw;
        }
    }
}
```

## Service Bus

The same shape, with three differences. The lock-renewal window has to cover the longest batch,
which is a host.json setting rather than code. The message is settled before success is
recorded. And the sender's trace context comes along, so the sender's request and this
function's lines share one `operation_Id` in Application Insights.

```csharp
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ThrottledLogging;

namespace Examples;

/// <summary>
/// Service Bus trigger with manual settlement: the function completes or abandons the message
/// itself, only after the batch's outcome is known.
/// </summary>
/// <remarks>
/// <para>
/// Service Bus gives the best context of the four triggers. <c>MessageId</c> is stable across
/// redeliveries and <c>DeliveryCount</c> counts them, like a storage queue. On top of that the
/// sender's trace context travels with the message, so Application Insights puts the sender's
/// request and this function's logs under one operation_Id, and <c>CorrelationId</c> carries
/// whatever the sender chose to put there.
/// </para>
/// <para>
/// Keeping the lock for a long batch: a queue's lock lasts one minute by default (the queue's
/// <c>LockDuration</c>). The Functions extension renews it automatically while the function runs,
/// but only for up to <c>maxAutoLockRenewalDuration</c>, five minutes by default. A batch that can
/// run longer needs that raised in host.json, to more than the longest batch you expect:
/// <code>
/// { "version": "2.0", "extensions": { "serviceBus": { "maxAutoLockRenewalDuration": "02:00:00" } } }
/// </code>
/// Renewal is by the extension on its own timer, so it keeps going during one slow item, which
/// renewing by hand between items would not. If the lock is lost anyway, settling fails with
/// <c>MessageLockLost</c>, the message is redelivered with DeliveryCount + 1, and the operation
/// below is logged as failed, not succeeded. Session-enabled queues lock the session rather than
/// the message; check the extension's session settings instead of relying on this paragraph.
/// </para>
/// </remarks>
public sealed class ServiceBusImport
{
    private readonly IOperationLogger _operations;
    private readonly IOrderStore _orders;
    private readonly ILogger<ServiceBusImport> _logger;

    /// <summary>Created by the worker through dependency injection.</summary>
    public ServiceBusImport(IOperationLogger operations, IOrderStore orders, ILogger<ServiceBusImport> logger)
    {
        _operations = operations;
        _orders = orders;
        _logger = logger;
    }

    /// <summary>Imports the batch the message names, then completes the message.</summary>
    /// <param name="message">The received message, bound whole for its metadata.</param>
    /// <param name="actions">Settlement for that message.</param>
    /// <param name="context">The invocation.</param>
    /// <param name="cancellationToken">Signalled when the host is shutting down.</param>
    [Function(nameof(ServiceBusImport))]
    public async Task RunAsync(
        [ServiceBusTrigger("order-batches", Connection = "ServiceBus", AutoCompleteMessages = false)] ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        string batchId = message.Body.ToString();

        Dictionary<string, object?> pairs = InvocationContext.Create(context, "ServiceBus", businessKey: message.MessageId, attempt: message.DeliveryCount);
        pairs["BatchId"] = batchId;
        pairs["CorrelationId"] = message.CorrelationId;
        pairs["SequenceNumber"] = message.SequenceNumber;
        pairs["EnqueuedUtc"] = message.EnqueuedTime;
        pairs["LockedUntilUtc"] = message.LockedUntil;
        using IDisposable? scope = _logger.BeginInvocationScope(pairs);

        OperationOptions options = _operations.DefaultOptions;
        options.TotalItems = await _orders.CountAsync(batchId, cancellationToken);

        using IOperationScope operation = _operations.BeginOperation("ImportOrders", options);
        try
        {
            await foreach (Order order in _orders.ReadAsync(batchId, cancellationToken))
            {
                using IItemScope item = operation.BeginItem(order.Id);
                try
                {
                    await _orders.ProcessAsync(order, cancellationToken);
                    item.Success();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    item.Failure(error);
                }
            }

            // Settle first, then record success. The first outcome an operation records is the one
            // it keeps, so logging success before CompleteMessageAsync would leave a success line
            // for a batch whose message then failed to settle and came back.
            await actions.CompleteMessageAsync(message, cancellationToken);
            operation.Success($"batch {batchId}");
        }
        catch (Exception error)
        {
            operation.Failure(error);

            // Abandon so the message is redelivered now with DeliveryCount + 1, rather than waiting
            // for the lock to expire. After MaxDeliveryCount it goes to the dead-letter queue, and
            // the log shows every attempt under one BusinessKey. Abandoning can fail too (the lock
            // may be what was lost); that must not hide the original error, so it is only logged.
            try
            {
                await actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
            }
            catch (Exception abandonError) when (abandonError is ServiceBusException or InvalidOperationException)
            {
                _logger.LogWarning(abandonError, "Could not abandon message {MessageId}; it will be redelivered when its lock expires", message.MessageId);
            }

            throw;
        }
    }
}
```

## Timer

A timer has no message, no delivery count and no redelivery, so something else has to remember
what is done. Here that is a checkpoint in storage, and the stable key is the hour of data the run
covers, never the time it happens to run. This example also shows the delegate form, `RunAsync`,
in place of the explicit `try`/`catch`.

```csharp
using System.Globalization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ThrottledLogging;

namespace Examples;

/// <summary>
/// Timer trigger: every hour, import every finished hour of orders that has not been imported yet.
/// </summary>
/// <remarks>
/// <para>
/// A timer has no message, so nothing counts attempts and nothing is redelivered. If the host
/// restarts mid-run, that hour is simply not finished. So the timer does not decide what to import
/// from the clock: "the hour before now" would skip 09:00 if the 10:05 run were delayed until
/// 12:05, and would name a different hour on a manual rerun. It asks a checkpoint in durable
/// storage for the oldest hour not yet done, imports it, marks it done, and repeats. A run that
/// dies leaves its hour unmarked, and the next tick picks it up again.
/// </para>
/// <para>
/// The hour is the BusinessKey, so every attempt at one hour shares a key however many ticks it
/// took. Attempt stays 1 because nothing counts attempts for a timer; count distinct InvocationIds
/// per BusinessKey in the log instead (see the retry query in the guide).
/// </para>
/// </remarks>
public sealed class TimerImport
{
    // Bounds one tick's work, so a long outage is caught up over several ticks rather than one
    // invocation running into the function timeout.
    private const int MaxHoursPerRun = 6;

    private readonly IOperationLogger _operations;
    private readonly IOrderStore _orders;
    private readonly ILogger<TimerImport> _logger;

    /// <summary>Created by the worker through dependency injection.</summary>
    public TimerImport(IOperationLogger operations, IOrderStore orders, ILogger<TimerImport> logger)
    {
        _operations = operations;
        _orders = orders;
        _logger = logger;
    }

    /// <summary>Imports pending hours, oldest first.</summary>
    /// <param name="timer">Schedule state, persisted by the host in storage between runs.</param>
    /// <param name="context">The invocation.</param>
    /// <param name="cancellationToken">Signalled when the host is shutting down.</param>
    [Function(nameof(TimerImport))]
    public async Task RunAsync([TimerTrigger("0 5 * * * *")] TimerInfo timer, FunctionContext context, CancellationToken cancellationToken)
    {
        // Only whole hours that have ended are eligible.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset currentHour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);

        for (int run = 0; run < MaxHoursPerRun; run++)
        {
            if (await _orders.OldestPendingHourAsync(currentHour, cancellationToken) is not { } hour)
            {
                return;
            }

            await ImportHourAsync(hour, timer, context, cancellationToken);
            await _orders.MarkHourDoneAsync(hour, cancellationToken);
        }
    }

    private async Task ImportHourAsync(DateTimeOffset hour, TimerInfo timer, FunctionContext context, CancellationToken cancellationToken)
    {
        // e.g. "orders-2026-09-24T09": stable however many ticks or reruns it takes.
        string batchId = "orders-" + hour.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);

        // Opened per hour, so each hour's lines carry that hour's key. IsPastDue says the schedule
        // itself was missed (the app was down or a previous tick overran).
        Dictionary<string, object?> pairs = InvocationContext.Create(context, "Timer", businessKey: batchId, attempt: 1);
        pairs["IsPastDue"] = timer.IsPastDue;
        pairs["ScheduleLastUtc"] = timer.ScheduleStatus?.Last;
        pairs["ScheduleNextUtc"] = timer.ScheduleStatus?.Next;
        using IDisposable? scope = _logger.BeginInvocationScope(pairs);

        OperationOptions options = _operations.DefaultOptions;
        options.TotalItems = await _orders.CountAsync(batchId, cancellationToken);

        // RunAsync (an extension on IOperationLogger) begins the operation, logs success when the
        // delegate returns and failure when it throws, and ends it either way: the explicit
        // try/catch of the queue example, with less to get wrong. A failure propagates, so the
        // hour is not marked done and the next tick retries it.
        await _operations.RunAsync(
            "ImportOrders",
            async (operation, ct) =>
            {
                await foreach (Order order in _orders.ReadAsync(batchId, ct))
                {
                    using IItemScope item = operation.BeginItem(order.Id);
                    try
                    {
                        await _orders.ProcessAsync(order, ct);
                        item.Success();
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        item.Failure(error);
                    }
                }
            },
            options,
            cancellationToken);
    }
}
```

## Durable Functions

The only pattern here where a restart doesn't throw away finished work. The orchestrator splits the
batch, each chunk is an activity, and the framework checkpoints each completed activity. A chunk
that was running when the process died runs again from its start, so processing has to be
idempotent. The throttled
operation lives in the activity. An orchestrator replays its code from the top, so an operation
started there would log its entry line on every replay.

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using ThrottledLogging;

namespace Examples;

/// <summary>What the orchestrator hands each activity.</summary>
/// <param name="InstanceId">The orchestration's id. Activities cannot read it themselves, so it travels in the input.</param>
/// <param name="BatchId">The batch being imported.</param>
/// <param name="ChunkIndex">Which chunk, from 0.</param>
/// <param name="ChunkCount">How many chunks the batch was split into.</param>
/// <param name="OrderIds">The orders in this chunk.</param>
public sealed record ChunkInput(string InstanceId, string BatchId, int ChunkIndex, int ChunkCount, IReadOnlyList<string> OrderIds);

/// <summary>What each activity reports back.</summary>
/// <param name="Processed">Orders that succeeded.</param>
/// <param name="Failed">Orders that failed.</param>
public sealed record ChunkResult(long Processed, long Failed);

/// <summary>
/// Durable Functions: an orchestrator splits a batch into chunks and runs one activity per chunk.
/// </summary>
/// <remarks>
/// <para>
/// This is the only one of the four patterns where a restart does not throw away finished work.
/// Each completed activity is checkpointed; after a recycle the orchestrator replays, skips the
/// chunks already done, and carries on. The InstanceId never changes, so it is the BusinessKey.
/// </para>
/// <para>
/// What a checkpoint does not do: an activity runs at least once, not exactly once. If the process
/// dies halfway through a 5,000-order chunk, or after the last order but before the result is
/// recorded, the whole chunk runs again. Processing an order must therefore be idempotent (an
/// upsert, a check for "already imported"), or the chunk has to keep its own per-order progress in
/// durable storage. The log shows it plainly: two operations for one BusinessKey and ChunkIndex,
/// with different InvocationIds.
/// </para>
/// <para>
/// ThrottledLogging belongs in the activity, never in the orchestrator. An orchestrator's code
/// re-runs from the top on every replay, so an operation begun there would log its entry line
/// again on every replay, and its ETA would measure replay speed rather than work. The
/// orchestrator logs through <c>CreateReplaySafeLogger</c>, which drops lines during replay.
/// </para>
/// </remarks>
public sealed class DurableImport
{
    private const int ChunkSize = 5_000;

    private readonly IOperationLogger _operations;
    private readonly IOrderStore _orders;
    private readonly ILogger<DurableImport> _logger;

    /// <summary>Created by the worker through dependency injection.</summary>
    public DurableImport(IOperationLogger operations, IOrderStore orders, ILogger<DurableImport> logger)
    {
        _operations = operations;
        _orders = orders;
        _logger = logger;
    }

    /// <summary>Splits the batch and fans the chunks out, a few at a time.</summary>
    /// <param name="context">The orchestration context; its InstanceId is stable for the whole run.</param>
    /// <returns>Totals across every chunk.</returns>
    [Function(nameof(ImportBatchOrchestrator))]
    public async Task<ChunkResult> ImportBatchOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ILogger logger = context.CreateReplaySafeLogger<DurableImport>();
        string batchId = context.GetInput<string>() ?? throw new InvalidOperationException("The orchestration needs a batch id as input.");

        // Reading from storage is not deterministic, so it happens in an activity, and replay
        // returns the recorded result instead of reading again.
        IReadOnlyList<string> orderIds = await context.CallActivityAsync<IReadOnlyList<string>>(nameof(ListOrders), batchId);
        List<ChunkInput> chunks = [];
        int chunkCount = (orderIds.Count + ChunkSize - 1) / ChunkSize;
        for (int index = 0; index < chunkCount; index++)
        {
            chunks.Add(new ChunkInput(context.InstanceId, batchId, index, chunkCount, [.. orderIds.Skip(index * ChunkSize).Take(ChunkSize)]));
        }

        // Replay-safe: written once, not once per replay. The scope makes it searchable by the
        // same BusinessKey the activities use.
        using (logger.BeginScope(new Dictionary<string, object?> { ["BusinessKey"] = context.InstanceId, ["Trigger"] = "Durable" }))
        {
            logger.LogInformation("Batch {BatchId} split into {ChunkCount} chunks of up to {ChunkSize}", batchId, chunkCount, ChunkSize);
        }

        // An activity that fails is retried by the framework, up to three times with back-off.
        TaskOptions retry = TaskOptions.FromRetryPolicy(new RetryPolicy(maxNumberOfAttempts: 3, firstRetryInterval: TimeSpan.FromSeconds(10)));
        ChunkResult[] results = await Task.WhenAll(chunks.Select(chunk => context.CallActivityAsync<ChunkResult>(nameof(ImportChunk), chunk, retry)));

        return new ChunkResult(results.Sum(static r => r.Processed), results.Sum(static r => r.Failed));
    }

    /// <summary>Lists the batch's order ids.</summary>
    /// <param name="batchId">The batch.</param>
    /// <param name="context">The invocation.</param>
    /// <returns>Every order id in the batch.</returns>
    [Function(nameof(ListOrders))]
    public async Task<IReadOnlyList<string>> ListOrders([ActivityTrigger] string batchId, FunctionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<string> ids = [];
        await foreach (Order order in _orders.ReadAsync(batchId, context.CancellationToken))
        {
            ids.Add(order.Id);
        }

        return ids;
    }

    /// <summary>Imports one chunk. This is where the throttled operation lives.</summary>
    /// <param name="input">The chunk, with the orchestration's InstanceId.</param>
    /// <param name="context">The invocation.</param>
    /// <returns>This chunk's counts.</returns>
    [Function(nameof(ImportChunk))]
    public async Task<ChunkResult> ImportChunk([ActivityTrigger] ChunkInput input, FunctionContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // An activity cannot see its retry count, so Attempt stays 1; a retried chunk shows up as a
        // second operation with the same BusinessKey and ChunkIndex and a new InvocationId.
        Dictionary<string, object?> pairs = InvocationContext.Create(context, "Durable", businessKey: input.InstanceId, attempt: 1);
        pairs["BatchId"] = input.BatchId;
        pairs["ChunkIndex"] = input.ChunkIndex;
        pairs["ChunkCount"] = input.ChunkCount;
        using IDisposable? scope = _logger.BeginInvocationScope(pairs);

        OperationOptions options = _operations.DefaultOptions;
        options.TotalItems = input.OrderIds.Count;

        // The operation name includes the chunk so a single chunk's progress and ETA read on
        // their own. Filter on BusinessKey to see the whole batch.
        using IOperationScope operation = _operations.BeginOperation($"ImportOrders[{input.ChunkIndex + 1}/{input.ChunkCount}]", options);
        long processed = 0;
        long failed = 0;
        try
        {
            foreach (string orderId in input.OrderIds)
            {
                using IItemScope item = operation.BeginItem(orderId);
                try
                {
                    await _orders.ProcessAsync(new Order(orderId, string.Empty), context.CancellationToken);
                    item.Success();
                    processed++;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    item.Failure(error);
                    failed++;
                }
            }

            operation.Success();
            return new ChunkResult(processed, failed);
        }
        catch (Exception error)
        {
            operation.Failure(error);
            throw;
        }
    }

    /// <summary>Starts an import. The instance id is chosen here so a repeated request for the same batch finds the existing run.</summary>
    /// <param name="batchId">The batch to import; the body of the queue message.</param>
    /// <param name="client">The Durable client.</param>
    /// <param name="context">The invocation.</param>
    [Function(nameof(StartImport))]
    public async Task StartImport([QueueTrigger("durable-order-batches")] string batchId, [DurableClient] DurableTaskClient client, FunctionContext context)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(context);

        // A deterministic instance id makes the batch id the BusinessKey end to end.
        string instanceId = "import-" + batchId;
        OrchestrationMetadata? existing = await client.GetInstanceAsync(instanceId, context.CancellationToken);
        if (existing is { IsRunning: true })
        {
            _logger.LogInformation("Import {InstanceId} is already running; not starting another", instanceId);
            return;
        }

        await client.ScheduleNewOrchestrationInstanceAsync(nameof(ImportBatchOrchestrator), batchId, new StartOrchestrationOptions(instanceId), context.CancellationToken);
    }
}
```

## Without Azure

Nothing above depends on Azure beyond the trigger bindings. The same library in a console job on
the generic host, writing JSON lines with scopes to standard output:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThrottledLogging;

namespace Examples;

/// <summary>
/// The same idea with no Azure and no Application Insights: a console job on the generic host,
/// logging JSON to standard output for whatever collects it (a container runtime, systemd, a file).
/// </summary>
public static class PlainHost
{
    /// <summary>Runs one import as a console job.</summary>
    /// <param name="args">Command-line arguments; the first is the batch id.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // One JSON object per line, scopes included, so every field is queryable downstream.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

        builder.Services.AddThrottledLogging();
        builder.Services.AddSingleton<IOrderStore, InMemoryOrderStore>();

        using IHost host = builder.Build();
        IOperationLogger operations = host.Services.GetRequiredService<IOperationLogger>();
        IOrderStore orders = host.Services.GetRequiredService<IOrderStore>();

        string batchId = args.Length > 0 ? args[0] : "batch-local";

        // Without a host to supply an invocation id, the job supplies its own context. Putting it
        // in OperationOptions.Scope rather than in an outer BeginScope attaches it to the
        // library's lines only; use an outer scope instead when your own lines need it too.
        OperationOptions options = operations.DefaultOptions;
        options.TotalItems = await orders.CountAsync(batchId, CancellationToken.None);
        options.Scope = new Dictionary<string, object?>
        {
            ["BusinessKey"] = batchId,
            ["JobRunId"] = Guid.NewGuid(),
            ["Machine"] = Environment.MachineName,
        };

        using IOperationScope operation = operations.BeginOperation("ImportOrders", options);
        await foreach (Order order in orders.ReadAsync(batchId, CancellationToken.None))
        {
            using IItemScope item = operation.BeginItem(order.Id);
            try
            {
                await orders.ProcessAsync(order, CancellationToken.None);
                item.Success();
            }
            catch (InvalidOperationException error)
            {
                item.Failure(error);
            }
        }

        operation.Success();

        // Disposing the host disposes OperationLogger, which would flush anything still running.
        // Here nothing is, because the operation has ended.
        return 0;
    }
}
```

A line from that job, formatted for reading:

```json
{
  "EventId": 9004,
  "LogLevel": "Information",
  "Category": "ThrottledLogging.ImportOrders",
  "Message": "ImportOrders (3f0c…) item batch-local-499 Succeeded — 500/1000 done, 0 failed, new=True, …",
  "State": { "OperationName": "ImportOrders", "OperationId": "3f0c…", "ItemLabel": "batch-local-499", "IsNew": true, "…": "…" },
  "Scopes": [ { "Message": "BusinessKey:batch-local, JobRunId:9a1e…, Machine:build-01", "BusinessKey": "batch-local", "JobRunId": "9a1e…", "Machine": "build-01" } ]
}
```

With a plain text sink, the scope prints as `=> BusinessKey:batch-local, JobRunId:…, Machine:…`
when the sink's `IncludeScopes` is on. If a sink ignores scopes entirely, you still have
`OperationId` on every line.

## Reading it back in Application Insights

Each structured field of a line becomes a `customDimensions` entry. That covers the library's own
fields (`OperationId`, `ItemLabel`, `IsNew`, `Processed`, …) and every scope pair. `operation_Id`
is the W3C trace id.

Two things decide where to look:

- **Lines that carry an exception go to the `exceptions` table, not `traces`.** With the 2.x
  provider, `TrackExceptionsAsExceptionTelemetry` is on by default. That includes item failures
  (9005) and failed operations (9002). Every query below therefore starts from a union of both
  tables. The alternative is to set `TrackExceptionsAsExceptionTelemetry = false` on
  `ApplicationInsightsLoggerOptions` and keep everything in `traces`.
- **Adaptive sampling is on by default**, and it can drop Information lines under load. A missing
  line is then not proof that it was never written. For logs you query by absence ("never
  finished"), exclude traces from sampling, for example with
  `AddApplicationInsightsTelemetryWorkerService(o => o.EnableAdaptiveSampling = false)`, or
  configure sampling to exclude `Trace` and `Exception` telemetry.

A helper to paste at the top of each query (or save as a function):

```kusto
let lines = (since: timespan) {
    union traces, exceptions
    | where timestamp > ago(since)
    | extend Message = coalesce(message, tostring(customDimensions.FormattedMessage), outerMessage),
             EventId = toint(customDimensions.EventId),
             OperationId = tostring(customDimensions.OperationId),
             BusinessKey = tostring(customDimensions.BusinessKey),
             InvocationId = tostring(customDimensions.InvocationId),
             Attempt = toint(customDimensions.Attempt),
             ChunkIndex = toint(customDimensions.ChunkIndex),
             ProcessId = toint(customDimensions.ProcessId)
};
```

**Every line for one piece of work, across retries, instances and recycles:**

```kusto
lines(7d)
| where BusinessKey == "<message id, batch id or instance id>"
| project timestamp, itemType, Attempt, cloud_RoleInstance, ProcessId, InvocationId, OperationId, EventId, severityLevel, Message
| order by timestamp asc
```

**Operations that never finished**, meaning no success (9001), failure (9002) or incomplete (9003)
line. The last line each one wrote shows how far it got. `ShutDown` tells you whether the host saw
it coming. With no 9008 either, the process was killed outright, or sampling dropped the lines
(see above).

```kusto
lines(1d)
| where isnotempty(OperationId)
| summarize Started = min(timestamp),
            LastLine = max(timestamp),
            Ended = countif(EventId in (9001, 9002, 9003)),
            ShutDown = countif(EventId == 9008),
            arg_max(timestamp, Message),
            BusinessKey = take_any(BusinessKey),
            Instance = take_any(cloud_RoleInstance)
            by OperationId
| where Ended == 0
| order by LastLine desc
```

**Retries: pieces of work that took more than one execution, and how each one ended.** This
counts executions (InvocationIds), not outcome lines. One execution can write both 9008 at
shutdown and 9001 once it drains, and that is not a retry. Durable chunks are separate work,
so they are keyed by chunk as well.

```kusto
lines(7d)
| where isnotempty(OperationId) and isnotempty(BusinessKey)
| summarize Outcome = take_anyif(EventId, EventId in (9001, 9002, 9003)),
            Attempt = max(Attempt),
            Started = min(timestamp)
            by BusinessKey, ChunkIndex, InvocationId, OperationId
| extend Outcome = case(Outcome == 9001, "succeeded", Outcome == 9002, "failed", Outcome == 9003, "incomplete", "never finished")
| summarize Executions = dcount(InvocationId),
            History = make_list(bag_pack("started", Started, "attempt", Attempt, "outcome", Outcome))
            by BusinessKey, ChunkIndex
| where Executions > 1
```

**Progress of everything still running**, from the latest progress line of each operation that
has no closing line yet:

```kusto
let recent = lines(1h) | where isnotempty(OperationId);
let ended = recent | where EventId in (9001, 9002, 9003) | distinct OperationId;
recent
| where EventId == 9004
| where OperationId !in (ended)
| summarize arg_max(timestamp, *) by OperationId
| project timestamp,
          Operation = tostring(customDimensions.OperationName),
          BusinessKey,
          Processed = tolong(customDimensions.Processed),
          Total = tolong(customDimensions.TotalItems),
          Failed = tolong(customDimensions.Failed),
          EtaSeconds = todouble(customDimensions.EtaSeconds),
          IsNew = tobool(customDimensions.IsNew)
```

A row whose `timestamp` keeps advancing while `IsNew` stays `false` is a loop that has stopped
moving: the sweeper keeps resending the same held event as a heartbeat. An operation that started
more than an hour ago and has written nothing since falls outside this window. Widen `lines(1h)`
for long runs.

## Things to know

- **Context is captured at `BeginOperation`.** Open your scope *before* you call it. A scope
  opened afterwards still reaches lines written on your own thread, but not the heartbeat lines
  from the sweeper.
- **The captured context is held until the operation ends, then released.** It includes the
  caller's `AsyncLocal` values, so anything large in them stays in memory while the operation runs.
  End operations promptly (`using` does it for you). An operation that is never ended keeps its
  context until the process exits.
- **A caller that suppresses execution-context flow** (`ExecutionContext.SuppressFlow`) captures
  nothing. Its heartbeat and shutdown lines then run in an empty context, carrying only
  `OperationOptions.Scope` and the operation id. They never borrow the context of whichever
  thread happens to sweep or dispose.
- **The shutdown flush leaves operations open.** A function still draining after
  `ApplicationStopping` can keep submitting items and will log its normal end line. The 9008 line
  just records that shutdown began while it was running. If the operation ends while the flush is
  running, no 9008 is written for it.
- **Logging providers are called under a per-operation lock** when a line comes from the
  sweeper, the shutdown flush or the end of the operation. A provider must not block waiting for
  that same operation to end. `OnEmitted` is not called under the lock, so it may.
- **Getting lines off the machine at shutdown is up to the provider.** Application Insights
  buffers, which is why `Program.cs` flushes its channel on `ApplicationStopped`. A hard kill (out
  of memory, a platform timeout) skips all of this, and the last throttled line is all you have.
  The "never finished" query is how you notice.

## Upgrading from 0.1.0

- **Message text changed** for events 9004, 9005 and 9006: each now reads
  `{OperationName} ({OperationId}) …` where it used to read `{OperationName} …`. This affects
  anything that parses the rendered text or matches on `{OriginalFormat}`. The structured
  properties only gained `OperationId`, and every event id is unchanged.
- **New events:** 9008 (still running at shutdown, Warning), 9009 (a shutdown flush failed,
  Warning), 9010 (the sweeper did not stop within five seconds, Warning), 9011 (reloaded settings
  were rejected, Warning).
- **New API:** `OperationOptions.Scope`; `AddThrottledLogging(IConfiguration, Action<ThrottledLoggingOptions>?)`
  to bind settings from configuration and follow reloads; an `OperationLogger` constructor taking
  `IOptionsMonitor<ThrottledLoggingOptions>`. Nothing was removed and no default changed.
- **New dependency:** `Microsoft.Extensions.Options.ConfigurationExtensions` 8.0.0 or later, for
  the binding overload.
- **Settings are copied when `OperationLogger` is built.** Changing a `ThrottledLoggingOptions`
  object after handing it to the constructor, or `IOptions<ThrottledLoggingOptions>.Value` after
  the container is built, no longer reaches the logger. Set everything, `OnEmitted` included, in
  the configure delegate or in configuration. `AddThrottledLogging` now builds the logger from
  `IOptionsMonitor`, so a registered configuration change does reach it, as described above.
- **Sweep intervals are validated:** `MinimumSweepInterval` must be greater than zero and
  `MaximumSweepInterval` at least as large. Invalid values throw when the logger is built; before,
  they were accepted, and a zero maximum silently stopped the sweeper.
- **Behaviour at shutdown:** disposing `OperationLogger` now writes lines where it used to write
  none.
