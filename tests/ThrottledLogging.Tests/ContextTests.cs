using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace ThrottledLogging.Tests;

/// <summary>
/// Context that reaches every line: the caller's own logging scope and <see cref="Activity"/>, the
/// operation's <see cref="OperationOptions.Scope"/>, and the operation id. Hosts such as Azure
/// Functions put the invocation id and trace context there, so a line that loses them cannot be
/// joined back to the run that wrote it.
/// </summary>
public sealed class ContextTests
{
    [Fact]
    public void Scope_option_is_attached_to_every_line_the_operation_writes()
    {
        using TestHarness harness = new(o => o.Defaults.EveryItems = 1);
        OperationOptions options = harness.Logger.DefaultOptions;
        options.Scope = new Dictionary<string, object?> { ["BusinessKey"] = "batch-7", ["Attempt"] = 2 };

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", options))
        {
            harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
            harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10), succeed: false);
            operation.Success();
        }

        Assert.NotEmpty(harness.Records);
        Assert.All(harness.Records, record =>
        {
            IReadOnlyList<KeyValuePair<string, object?>> pairs = ScopePairs(record);
            Assert.Contains(new KeyValuePair<string, object?>("BusinessKey", "batch-7"), pairs);
            Assert.Contains(new KeyValuePair<string, object?>("Attempt", 2), pairs);
        });
    }

    [Fact]
    public void Scope_option_is_snapshotted_so_later_changes_do_not_leak_into_a_running_operation()
    {
        using TestHarness harness = new();
        Dictionary<string, object?> scope = new() { ["BusinessKey"] = "batch-7" };
        OperationOptions options = harness.Logger.DefaultOptions;
        options.Scope = scope;

        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", options);
        scope["BusinessKey"] = "changed";
        operation.Success();

        Assert.All(harness.Records, record => Assert.Contains(new KeyValuePair<string, object?>("BusinessKey", "batch-7"), ScopePairs(record)));
    }

    [Fact]
    public void Scope_state_renders_readably_for_text_sinks()
    {
        using TestHarness harness = new();
        OperationOptions options = harness.Logger.DefaultOptions;
        options.Scope = new Dictionary<string, object?> { ["BusinessKey"] = "batch-7", ["Attempt"] = 2 };

        using (harness.Logger.BeginOperation("ImportOrders", options))
        {
        }

        object? state = Assert.Single(harness.Records[0].Scopes);
        Assert.Equal("BusinessKey:batch-7, Attempt:2", state?.ToString());
    }

    [Fact]
    public void Without_a_scope_option_no_scope_is_opened()
    {
        using TestHarness harness = new();

        using (harness.Logger.BeginOperation("ImportOrders"))
        {
        }

        Assert.All(harness.Records, record => Assert.Empty(record.Scopes));
    }

    [Fact]
    public void Heartbeat_written_by_the_sweeper_keeps_the_callers_logging_scope()
    {
        // A real factory, so every logger shares one scope provider the way a host's does.
        FakeLogCollector collector = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(new FakeLoggerProvider(collector)));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, time);
        ILogger functionLogger = factory.CreateLogger("ImportOrdersFunction");

        IOperationScope operation;
        using (functionLogger.BeginScope(new Dictionary<string, object?> { ["InvocationId"] = "inv-42" }))
        {
            operation = logger.BeginOperation("ImportOrders");
            using IItemScope first = operation.BeginItem("order-1");   // logged: the first event
            first.Success();                                          // held
        }

        time.Advance(TimeSpan.FromMinutes(1));
        collector.Clear();
        SweepOnAnotherThreadWithoutFlow(logger);

        FakeLogRecord heartbeat = Assert.Single(collector.GetSnapshot(), r => r.Id == 9004);
        Assert.Contains(heartbeat.Scopes, s => s is IEnumerable<KeyValuePair<string, object?>> pairs && pairs.Contains(new("InvocationId", "inv-42")));
        operation.Dispose();
    }

    [Fact]
    public void Heartbeat_written_by_the_sweeper_keeps_the_callers_activity()
    {
        string? seen = null;
        using TestHarness observed = new(o => o.OnEmitted = e => seen = Activity.Current?.TraceId.ToString());

        using Activity invocation = new Activity("Invocation").SetIdFormat(ActivityIdFormat.W3C).Start();
        string expected = invocation.TraceId.ToString();
        IOperationScope operation = observed.Logger.BeginOperation("ImportOrders");
        observed.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
        observed.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10));
        invocation.Stop();
        Activity.Current = null;

        seen = null;
        observed.Time.Advance(TimeSpan.FromMinutes(1));
        SweepOnAnotherThreadWithoutFlow(observed.Logger);

        Assert.Equal(expected, seen);
        operation.Dispose();
    }

    [Fact]
    public void Item_and_failure_lines_carry_the_operation_id()
    {
        using TestHarness harness = new(o =>
        {
            o.Defaults.EveryItems = 1;
            o.Defaults.FailureEveryItems = 1;
        });

        Guid id;
        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders"))
        {
            id = operation.Id;
            harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
            harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10), succeed: false);
            operation.Success();
        }

        IReadOnlyList<FakeLogRecord> itemLines = [.. harness.Records.Where(static r => r.Id.Id is 9004 or 9005)];
        Assert.Contains(itemLines, r => r.Id == 9004);
        Assert.Contains(itemLines, r => r.Id == 9005);
        Assert.All(itemLines, record =>
        {
            Assert.Equal(id.ToString(), record.GetStructuredStateValue("OperationId"));
            Assert.Contains(id.ToString(), record.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Failure_summary_carries_the_operation_id()
    {
        using TestHarness harness = new();

        Guid id;
        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders"))
        {
            id = operation.Id;
            harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10), succeed: false);
            operation.Success();
        }

        FakeLogRecord summary = Assert.Single(harness.Records, r => r.Id == 9006);
        Assert.Equal(id.ToString(), summary.GetStructuredStateValue("OperationId"));
    }

    [Fact]
    public void Dispose_flushes_held_events_and_notes_operations_still_running()
    {
        TestHarness harness = new();
        OperationOptions options = harness.Logger.DefaultOptions;
        options.Scope = new Dictionary<string, object?> { ["BusinessKey"] = "batch-7" };
        IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", options);
        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));   // "order-1 Started" is the first progress event: logged
        harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10));
        harness.RunItem(operation, "order-3", TimeSpan.FromMilliseconds(10), succeed: false);   // first failure: logged
        harness.RunItem(operation, "order-4", TimeSpan.FromMilliseconds(10), succeed: false);   // latest failure: held
        harness.Collector.Clear();

        harness.Dispose();

        // A failing item still starts on the progress channel, so the latest progress event held is "order-4 Started".
        IReadOnlyList<FakeLogRecord> records = harness.Records;
        Assert.Contains(records, r => r.Id == 9004 && r.GetStructuredStateValue("ItemLabel") == "order-4" && r.GetStructuredStateValue("Outcome") == nameof(ItemOutcome.Started));
        Assert.Contains(records, r => r.Id == 9005 && r.GetStructuredStateValue("ItemLabel") == "order-4");
        FakeLogRecord shutdown = Assert.Single(records, r => r.Id == 9008);
        Assert.Equal(LogLevel.Warning, shutdown.Level);
        Assert.Equal(operation.Id.ToString(), shutdown.GetStructuredStateValue("OperationId"));
        Assert.Equal("2", shutdown.GetStructuredStateValue("Processed"));
        Assert.Equal("2", shutdown.GetStructuredStateValue("Failed"));
        Assert.All(records, r => Assert.Contains(new KeyValuePair<string, object?>("BusinessKey", "batch-7"), ScopePairs(r)));
    }

    [Fact]
    public void Operation_keeps_working_after_the_logger_is_disposed_and_still_logs_its_end()
    {
        TestHarness harness = new();
        IOperationScope operation = harness.Logger.BeginOperation("ImportOrders");
        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));

        harness.Dispose();
        harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10));
        operation.Success();

        Assert.Single(harness.Records, r => r.Id == 9008);
        Assert.Single(harness.Records, r => r.Id == 9001);
        Assert.Equal(1, harness.Logger.GetCounters().CompletedOperations);
    }

    [Fact]
    public void Dispose_with_nothing_running_writes_nothing()
    {
        TestHarness harness = new();
        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders"))
        {
            operation.Success();
        }

        harness.Collector.Clear();
        harness.Dispose();

        Assert.Empty(harness.Records);
    }

    /// <summary>
    /// Runs one sweep on a thread pool thread that inherits nothing from the test, the way the
    /// background sweeper's timer thread does.
    /// </summary>
    private static void SweepOnAnotherThreadWithoutFlow(OperationLogger logger)
    {
        Task sweep;
        using (ExecutionContext.SuppressFlow())
        {
            sweep = Task.Run(logger.SweepOnce);
        }

        sweep.GetAwaiter().GetResult();
    }

    /// <summary>Every key/value pair across all the scopes active when <paramref name="record"/> was written.</summary>
    private static List<KeyValuePair<string, object?>> ScopePairs(FakeLogRecord record)
        => [.. record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany(static pairs => pairs)];
}
