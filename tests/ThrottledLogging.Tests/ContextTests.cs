using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using ThrottledLogging.Internal;

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
        // Read inside ILogger.Log, where a provider such as Application Insights reads it.
        using RecordingProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, time);

        using Activity invocation = new Activity("Invocation").SetIdFormat(ActivityIdFormat.W3C).Start();
        string expected = invocation.TraceId.ToString();
        IOperationScope operation = logger.BeginOperation("ImportOrders");
        using (IItemScope first = operation.BeginItem("order-1"))
        {
            first.Success();
        }

        invocation.Stop();
        Activity.Current = null;
        provider.Clear();

        time.Advance(TimeSpan.FromMinutes(1));
        SweepOnAnotherThreadWithoutFlow(logger);

        RecordedLine heartbeat = Assert.Single(provider.Lines, l => l.EventId == 9004);
        Assert.Equal(expected, heartbeat.TraceId);
        operation.Dispose();
    }

    [Fact]
    public void Scope_option_is_opened_exactly_once_per_line()
    {
        using TestHarness harness = new(o =>
        {
            o.Defaults.EveryItems = 1;
            o.Defaults.FailureEveryItems = 1;
        });
        OperationOptions options = harness.Logger.DefaultOptions;
        options.Scope = new Dictionary<string, object?> { ["BusinessKey"] = "batch-7" };

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", options))
        {
            harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
            harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10), succeed: false);
            operation.Success();
        }

        Assert.All(harness.Records, record => Assert.Single(ScopePairs(record), p => p.Key == "BusinessKey"));
    }

    [Fact]
    public void Scope_of_one_operation_does_not_leak_into_an_operation_begun_from_its_callback()
    {
        using RecordingProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger? logger = null;
        IOperationScope? second = null;
        ThrottledLoggingOptions settings = new()
        {
            EnableSweeper = false,
            OnEmitted = e =>
            {
                if (e.OperationName == "First" && second is null)
                {
                    second = logger!.BeginOperation("Second");
                }
            },
        };
        using OperationLogger created = new(factory, settings, TimeProvider.System);
        logger = created;
        OperationOptions options = created.DefaultOptions;
        options.Scope = new Dictionary<string, object?> { ["BusinessKey"] = "first-only" };

        using (IOperationScope first = created.BeginOperation("First", options))
        {
            using IItemScope item = first.BeginItem("a");
            item.Success();
            first.Success();
        }

        Assert.NotNull(second);
        second.Success();

        Assert.All(provider.Lines.Where(l => l.Category.EndsWith(".Second", StringComparison.Ordinal)), l => Assert.DoesNotContain(l.Pairs, p => p.Key == "BusinessKey"));
        Assert.All(provider.Lines.Where(l => l.Category.EndsWith(".First", StringComparison.Ordinal)), l => Assert.Single(l.Pairs, p => p.Key == "BusinessKey"));
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

    [Fact]
    public void Shutdown_line_never_follows_the_operations_own_end_line()
    {
        // The caller finishes while shutdown is flushing its held event: the observer, told about
        // that flush, ends the operation. Whatever the interleaving, "still running" must come
        // before the success line, never after it.
        IOperationScope? operation = null;
        TestHarness harness = new(o => o.OnEmitted = e =>
        {
            if (e.Outcome == ItemOutcome.Succeeded && e.ItemLabel == "order-2")
            {
                operation!.Success();
            }
        });
        operation = harness.Logger.BeginOperation("ImportOrders");
        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
        harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10));

        harness.Dispose();

        List<int> ids = [.. harness.Records.Select(static r => r.Id.Id)];
        Assert.Single(ids, static id => id == 9001);
        Assert.True(ids.IndexOf(9008) < ids.IndexOf(9001), string.Join(", ", ids));
    }

    [Fact]
    public void Operation_ended_first_is_not_reported_as_running_at_shutdown()
    {
        TestHarness harness = new();
        IOperationScope operation = harness.Logger.BeginOperation("ImportOrders");
        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
        operation.Success();

        harness.Dispose();

        Assert.DoesNotContain(harness.Records, r => r.Id == 9008);
    }

    [Fact]
    public void Observer_waiting_for_another_thread_to_end_the_operation_does_not_deadlock()
    {
        // OnEmitted is user code. Here it hands the end of the operation to another thread and
        // waits for it, from inside a sweep: if the observer ran under the operation's lock, the
        // other thread's Success would wait for that lock forever.
        IOperationScope? operation = null;
        bool finished = false;
        using TestHarness harness = new(o => o.OnEmitted = e =>
        {
            if (e.ItemLabel == "order-2")
            {
                finished = Task.Run(() => operation!.Success()).Wait(TimeSpan.FromSeconds(10));
            }
        });
        operation = harness.Logger.BeginOperation("ImportOrders");
        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
        harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10));   // held

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        harness.Logger.SweepOnce();

        Assert.True(finished);
        Assert.Single(harness.Records, r => r.Id == 9001);
    }

    [Fact]
    public void An_ended_operation_no_longer_keeps_the_callers_context_alive()
    {
        using TestHarness harness = new();
        using Activity caller = new Activity("ImportOrdersFunction").Start();
        IOperationScope operation = harness.Logger.BeginOperation("ImportOrders");
        OperationScope scope = Assert.IsType<OperationScope>(operation);
        Assert.True(scope.HoldsCallerContext);

        operation.Success();

        // The operation itself is still referenced, as a caller holding onto it would be; the
        // context it captured, and with it every AsyncLocal the caller had set, is let go.
        Assert.False(scope.HoldsCallerContext);
    }

    [Fact]
    public void A_held_event_flushed_at_shutdown_is_not_written_again_when_the_operation_ends()
    {
        TestHarness harness = new();
        IOperationScope operation = harness.Logger.BeginOperation("ImportOrders");
        harness.RunItem(operation, "order-1", TimeSpan.FromMilliseconds(10));
        harness.RunItem(operation, "order-2", TimeSpan.FromMilliseconds(10));

        harness.Dispose();
        operation.Success();

        Assert.Single(harness.Records, r => r.Id == 9004 && r.GetStructuredStateValue("ItemLabel") == "order-2");
    }

    [Fact]
    public void Shutdown_of_an_operation_that_captured_nothing_does_not_borrow_the_disposing_threads_scope()
    {
        using RecordingProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);

        IOperationScope operation;
        using (ExecutionContext.SuppressFlow())
        {
            operation = logger.BeginOperation("ImportOrders");
        }

        using (factory.CreateLogger("SomeOtherInvocation").BeginScope(new Dictionary<string, object?> { ["InvocationId"] = "unrelated" }))
        {
            logger.Dispose();
        }

        RecordedLine shutdown = Assert.Single(provider.Lines, l => l.EventId == 9008);
        Assert.DoesNotContain(shutdown.Pairs, p => p.Key == "InvocationId");
        operation.Dispose();
    }

    [Fact]
    public async Task Background_sweeper_does_not_inherit_the_context_that_created_the_logger()
    {
        using RecordingProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ThrottledLoggingOptions settings = new() { EnableSweeper = true, MinimumSweepInterval = TimeSpan.FromSeconds(1) };
        settings.Defaults.EveryInterval = TimeSpan.FromSeconds(4);

        // The first resolver of the singleton happens to be inside some invocation's scope.
        OperationLogger logger;
        using (factory.CreateLogger("FirstInvocation").BeginScope(new Dictionary<string, object?> { ["InvocationId"] = "first" }))
        {
            logger = new OperationLogger(factory, settings, time);
        }

        using (logger)
        {
            IOperationScope operation;
            using (ExecutionContext.SuppressFlow())
            {
                operation = logger.BeginOperation("ImportOrders");
            }

            using (IItemScope first = operation.BeginItem("order-1"))
            {
                first.Success();   // held
            }

            // Tick the real sweeper until the held event goes out as a heartbeat.
            // A real-time bound rather than an iteration count, so a slow machine only makes it slower.
            Stopwatch waited = Stopwatch.StartNew();
            while (!provider.Lines.Any(l => l.EventId == 9004 && l.Message.Contains("Succeeded", StringComparison.Ordinal)) && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            RecordedLine heartbeat = Assert.Single(provider.Lines, l => l.EventId == 9004 && l.Message.Contains("Succeeded", StringComparison.Ordinal));
            Assert.DoesNotContain(heartbeat.Pairs, p => p.Key == "InvocationId");
            operation.Dispose();
        }
    }

    [Fact]
    public void A_provider_that_ends_the_operation_during_the_shutdown_flush_gets_no_still_running_line_after_the_end()
    {
        using RecordingProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);
        IOperationScope operation = logger.BeginOperation("ImportOrders");
        using (IItemScope item = operation.BeginItem("order-1"))   // Started: the first event, written
        {
            item.Success();                                         // Succeeded: held
        }

        // The provider re-enters the library on the thread writing the held line, which already
        // holds the operation's lock; Monitor lets it straight back in.
        provider.AfterLine = line =>
        {
            if (line.EventId == 9004 && line.Message.Contains("Succeeded", StringComparison.Ordinal))
            {
                operation.Success();
            }
        };
        logger.Dispose();

        List<int> ids = [.. provider.Lines.Select(static l => l.EventId)];
        Assert.Contains(9001, ids);
        Assert.DoesNotContain(9008, ids.SkipWhile(static id => id != 9001));
    }

    [Fact]
    public void A_throwing_provider_at_the_end_still_retires_the_operation()
    {
        using RecordingProvider provider = new() { ThrowWhen = l => l.EventId == 9001 };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);
        IOperationScope operation = logger.BeginOperation("ImportOrders");

        Assert.ThrowsAny<Exception>(() => operation.Success());

        Assert.Empty(logger.GetActiveOperations());
        Assert.Equal(1, logger.GetCounters().CompletedOperations);
    }

    [Fact]
    public void A_throwing_provider_at_the_entry_line_leaves_nothing_registered()
    {
        using RecordingProvider provider = new() { ThrowWhen = l => l.EventId == 9000 };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);

        Assert.ThrowsAny<Exception>(() => logger.BeginOperation("ImportOrders"));

        Assert.Empty(logger.GetActiveOperations());
        Assert.Equal(0, logger.GetCounters().ActiveOperations);
    }

    [Fact]
    public void Dispose_never_throws_and_keeps_flushing_when_a_provider_fails_for_one_operation_and_for_its_own_diagnostic()
    {
        using RecordingProvider provider = new()
        {
            ThrowWhen = l => (l.EventId == 9008 && l.Category.EndsWith(".Broken", StringComparison.Ordinal)) || l.EventId == 9009,
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);
        IOperationScope broken = logger.BeginOperation("Broken");
        IOperationScope healthy = logger.BeginOperation("Healthy");

        Exception? thrown = Record.Exception(logger.Dispose);

        Assert.Null(thrown);
        Assert.Single(provider.Lines, l => l.EventId == 9008 && l.Category.EndsWith(".Healthy", StringComparison.Ordinal));
        broken.Dispose();
        healthy.Dispose();
    }

    [Fact]
    public void A_provider_that_throws_during_a_sweep_does_not_stop_the_sweep_for_other_operations()
    {
        // The provider starts failing only once both held lines exist, so only the sweep is affected.
        bool failing = false;
        using RecordingProvider provider = new() { ThrowWhen = l => failing && l.EventId == 9004 && l.Category.EndsWith(".Broken", StringComparison.Ordinal) };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, time);
        using IOperationScope broken = logger.BeginOperation("Broken");
        using IOperationScope healthy = logger.BeginOperation("Healthy");
        foreach (IOperationScope operation in new[] { broken, healthy })
        {
            using (IItemScope first = operation.BeginItem("order-1"))    // logged: the first event
            {
                first.Success();                                          // held
            }
        }

        failing = true;
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Clear();
        Exception? thrown = Record.Exception(logger.SweepOnce);

        Assert.Null(thrown);
        Assert.Single(provider.Lines, l => l.EventId == 9004 && l.Category.EndsWith(".Healthy", StringComparison.Ordinal));
        Assert.Single(provider.Lines, l => l.EventId == 9012);

        // The line that failed is not retried, but once the provider recovers the operation's next
        // held event is swept as usual: the failure did not leave the operation stuck.
        failing = false;
        using (IItemScope second = broken.BeginItem("order-2"))
        {
            second.Success();
        }

        time.Advance(TimeSpan.FromMinutes(1));
        provider.Clear();
        logger.SweepOnce();
        Assert.Contains(provider.Lines, l => l.EventId == 9004 && l.Category.EndsWith(".Broken", StringComparison.Ordinal) && l.Message.Contains("order-2", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispose_gives_up_on_an_operation_a_hung_provider_holds_and_still_flushes_the_others()
    {
        // The provider hangs on the Stuck operation's heartbeat until the test releases it, the way
        // a provider blocked on a dead network connection would.
        using ManualResetEventSlim release = new(initialState: false);
        using ManualResetEventSlim hung = new(initialState: false);
        bool hangNow = false;
        using RecordingProvider provider = new()
        {
            AfterLine = l =>
            {
                if (Volatile.Read(ref hangNow) && l.EventId == 9004 && l.Category.EndsWith(".Stuck", StringComparison.Ordinal))
                {
                    hung.Set();
                    release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
            },
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, time) { ShutdownLockTimeout = TimeSpan.FromMilliseconds(200) };
        IOperationScope stuck = logger.BeginOperation("Stuck");
        IOperationScope healthy = logger.BeginOperation("Healthy");
        using (IItemScope first = stuck.BeginItem("order-1"))
        {
            first.Success();
        }

        using (IItemScope held = stuck.BeginItem("order-2"))
        {
            held.Success();                                              // held, for the sweep to write
        }

        Volatile.Write(ref hangNow, true);
        time.Advance(TimeSpan.FromMinutes(1));
        Thread sweeper = new(logger.SweepOnce) { IsBackground = true };
        sweeper.Start();
        Assert.True(hung.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));   // the sweep now holds Stuck's lock

        Stopwatch elapsed = Stopwatch.StartNew();
        logger.Dispose();
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Dispose took {elapsed.Elapsed}.");
        Assert.Single(provider.Lines, l => l.EventId == 9008 && l.Category.EndsWith(".Healthy", StringComparison.Ordinal));
        Assert.Single(provider.Lines, l => l.EventId == 9009 && l.Message.Contains("Stuck", StringComparison.Ordinal));
        release.Set();
        Assert.True(sweeper.Join(TimeSpan.FromSeconds(10)));
        stuck.Dispose();
        healthy.Dispose();
    }

    [Fact]
    public void Dispose_while_an_operation_is_writing_its_entry_line_flushes_it_after_that_line()
    {
        // The entry line blocks until the test has started Dispose, so Dispose finds the operation
        // registered but not yet started: the window the registry lock and entry lock close.
        using ManualResetEventSlim entering = new(initialState: false);
        using ManualResetEventSlim release = new(initialState: false);
        using RecordingProvider provider = new()
        {
            AfterLine = l =>
            {
                if (l.EventId == 9000)
                {
                    entering.Set();
                    release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
            },
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);

        IOperationScope? late = null;
        Thread begin = new(() => late = logger.BeginOperation("Late")) { IsBackground = true };
        begin.Start();
        Assert.True(entering.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Thread dispose = new(logger.Dispose) { IsBackground = true };
        dispose.Start();

        // Dispose must wait for the entry line rather than write "still running" ahead of it.
        Assert.False(dispose.Join(TimeSpan.FromMilliseconds(300)));
        release.Set();
        Assert.True(dispose.Join(TimeSpan.FromSeconds(10)));
        Assert.True(begin.Join(TimeSpan.FromSeconds(10)));
        Assert.NotNull(late);
        late.Dispose();

        List<int> ids = [.. provider.Lines.Select(static l => l.EventId)];
        Assert.Equal([9000, 9008, 9003], ids);
    }

    [Fact]
    public void Observer_is_not_called_under_the_lock_when_a_provider_ends_the_operation_from_inside_a_sweep()
    {
        // The sweep holds the operation's lock while it writes the heartbeat. The provider ends the
        // operation right there, which takes the (reentrant) lock again and flushes the held failure.
        // That failure's OnEmitted must wait until the sweep has let go too, not just the inner End.
        OperationScope? scope = null;
        List<bool> lockHeldDuringNotification = [];
        IOperationScope? operation = null;
        bool ended = false;
        using RecordingProvider provider = new()
        {
            AfterLine = l =>
            {
                if (l.EventId == 9004 && l.Message.Contains("order-2", StringComparison.Ordinal) && !ended)
                {
                    ended = true;
                    operation!.Success();
                }
            },
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ThrottledLoggingOptions options = new() { EnableSweeper = false, OnEmitted = _ => lockHeldDuringNotification.Add(scope!.IsLockHeldByCurrentThread) };
        using OperationLogger logger = new(factory, options, time);
        operation = logger.BeginOperation("ImportOrders");
        scope = Assert.IsType<OperationScope>(operation);
        using (IItemScope first = operation.BeginItem("order-1"))
        {
            first.Failure(new InvalidOperationException("first"));             // written: first failure
        }

        using (IItemScope second = operation.BeginItem("order-2"))
        {
            second.Failure(new InvalidOperationException("second"));           // held on both channels
        }

        time.Advance(TimeSpan.FromMinutes(1));
        lockHeldDuringNotification.Clear();
        logger.SweepOnce();

        Assert.True(ended);
        Assert.NotEmpty(lockHeldDuringNotification);
        Assert.DoesNotContain(true, lockHeldDuringNotification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shutdown_flush_of_an_operation_that_captured_nothing_runs_on_the_disposing_thread_in_an_empty_context(bool providerThrows)
    {
        // No thread-pool hop, so a starved pool cannot hold up shutdown; and none of the disposing
        // thread's own context leaks into the line, nor is lost from that thread afterwards, even
        // when the provider throws.
        int? flushThread = null;
        string? ambientDuringFlush = "not written";
        string? activityDuringFlush = "not written";
        using RecordingProvider provider = new()
        {
            AfterLine = l =>
            {
                if (l.EventId == 9008)
                {
                    flushThread = Environment.CurrentManagedThreadId;
                    ambientDuringFlush = RequestLabel.Value;
                    activityDuringFlush = Activity.Current?.OperationName;
                    if (providerThrows)
                    {
                        throw new InvalidOperationException("provider failure");
                    }
                }
            },
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);
        IOperationScope operation;
        using (ExecutionContext.SuppressFlow())
        {
            operation = logger.BeginOperation("ImportOrders");
        }

        RequestLabel.Value = "disposing-request";
        using Activity disposing = new Activity("DisposingInvocation").Start();
        using (factory.CreateLogger("Host").BeginScope(new Dictionary<string, object?> { ["InvocationId"] = "disposing" }))
        {
            logger.Dispose();
        }

        Assert.Equal(Environment.CurrentManagedThreadId, flushThread);
        Assert.Null(ambientDuringFlush);
        Assert.Null(activityDuringFlush);
        Assert.DoesNotContain(provider.Lines.Single(static l => l.EventId == 9008).Pairs, static p => p.Key == "InvocationId");
        Assert.Equal("disposing-request", RequestLabel.Value);
        Assert.Same(disposing, Activity.Current);
        operation.Dispose();
    }

    private static readonly AsyncLocal<string?> RequestLabel = new();

    [Fact]
    public void A_second_dispose_that_gives_up_waiting_says_so()
    {
        using ManualResetEventSlim writing = new(initialState: false);
        using ManualResetEventSlim release = new(initialState: false);
        using RecordingProvider provider = new()
        {
            AfterLine = l =>
            {
                if (l.EventId == 9008)
                {
                    writing.Set();
                    release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
            },
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System)
        {
            SweeperStopTimeout = TimeSpan.FromMilliseconds(100),
            ShutdownLockTimeout = TimeSpan.FromMilliseconds(100),
        };
        using IOperationScope operation = logger.BeginOperation("ImportOrders");
        Thread first = new(logger.Dispose) { IsBackground = true };
        first.Start();
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        logger.Dispose();   // gives up after 200 ms: the first is still stuck in the provider

        Assert.Single(provider.Lines, static l => l.EventId == 9013);
        release.Set();
        Assert.True(first.Join(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Observer_hears_about_lines_in_the_order_they_were_written_when_a_provider_ends_the_operation_from_inside_a_sweep()
    {
        List<ItemOutcome> notified = [];
        IOperationScope? operation = null;
        bool ended = false;
        using RecordingProvider provider = new()
        {
            AfterLine = l =>
            {
                if (l.EventId == 9004 && l.Message.Contains("order-2", StringComparison.Ordinal) && !ended)
                {
                    ended = true;
                    operation!.Success();   // writes the held failure after this progress line
                }
            },
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false, OnEmitted = e => notified.Add(e.Outcome) }, time);
        operation = logger.BeginOperation("ImportOrders");
        using (IItemScope first = operation.BeginItem("order-1"))
        {
            first.Failure(new InvalidOperationException("first"));
        }

        using (IItemScope second = operation.BeginItem("order-2"))
        {
            second.Failure(new InvalidOperationException("second"));
        }

        time.Advance(TimeSpan.FromMinutes(1));
        notified.Clear();
        logger.SweepOnce();

        List<int> written = [.. provider.Lines.Where(static l => l.EventId is 9004 or 9005).Select(static l => l.EventId)];
        Assert.Equal([9004, 9005], written[^2..]);
        Assert.Equal([ItemOutcome.Started, ItemOutcome.Failed], notified);
    }

    [Fact]
    public void A_provider_that_disposes_the_logger_from_inside_the_entry_line_still_sees_started_before_still_running()
    {
        OperationLogger? logger = null;
        using RecordingProvider disposing = new()
        {
            AfterLine = l =>
            {
                if (l.EventId == 9000)
                {
                    logger!.Dispose();
                }
            },
        };
        using RecordingProvider watching = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(disposing).AddProvider(watching));
        logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);

        using IOperationScope operation = logger.BeginOperation("ImportOrders");

        Assert.Equal([9000, 9008], [.. watching.Lines.Select(static l => l.EventId)]);
    }

    [Fact]
    public void A_second_dispose_waits_for_the_first_to_finish_writing()
    {
        using ManualResetEventSlim writing = new(initialState: false);
        using ManualResetEventSlim release = new(initialState: false);
        using RecordingProvider provider = new()
        {
            AfterLine = l =>
            {
                if (l.EventId == 9008)
                {
                    writing.Set();
                    release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
            },
        };
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);
        using IOperationScope operation = logger.BeginOperation("ImportOrders");

        Thread first = new(logger.Dispose) { IsBackground = true };
        first.Start();
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Thread second = new(logger.Dispose) { IsBackground = true };
        second.Start();

        Assert.False(second.Join(TimeSpan.FromMilliseconds(300)));
        release.Set();
        Assert.True(second.Join(TimeSpan.FromSeconds(10)));
        Assert.True(first.Join(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void An_item_finished_after_its_operation_ended_writes_nothing()
    {
        using RecordingProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);
        IOperationScope operation = logger.BeginOperation("ImportOrders");
        IItemScope item = operation.BeginItem("order-1");

        operation.Success();
        item.Failure(new InvalidOperationException("too late"));   // would be the failure channel's first event
        item.Dispose();

        Assert.Equal(9001, provider.Lines[^1].EventId);
    }

    [Fact]
    public void Begin_after_dispose_is_refused()
    {
        using ILoggerFactory factory = LoggerFactory.Create(static _ => { });
        OperationLogger logger = new(factory, new ThrottledLoggingOptions { EnableSweeper = false }, TimeProvider.System);
        logger.Dispose();

        Assert.Throws<ObjectDisposedException>(() => logger.BeginOperation("TooLate"));
    }

    /// <summary>
    /// Runs one sweep on a thread pool thread that inherits nothing from the test, the way the
    /// background sweeper's timer thread does.
    /// </summary>
    private static void SweepOnAnotherThreadWithoutFlow(OperationLogger logger)
    {
        // A queued work item rather than a task: a task waited on before it starts may be run
        // inline on this thread, in this thread's context, which would defeat the test.
        using ManualResetEventSlim done = new(initialState: false);
        ThreadPool.UnsafeQueueUserWorkItem(
            _ =>
            {
                try
                {
                    logger.SweepOnce();
                }
                finally
                {
                    done.Set();
                }
            },
            null);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Every key/value pair across all the scopes active when <paramref name="record"/> was written.</summary>
    private static List<KeyValuePair<string, object?>> ScopePairs(FakeLogRecord record)
        => [.. record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany(static pairs => pairs)];
}

/// <summary>One line as a provider saw it, with the ambient state read inside <see cref="ILogger.Log{TState}"/>.</summary>
internal sealed record RecordedLine(int EventId, string Category, string Message, string? TraceId, IReadOnlyList<KeyValuePair<string, object?>> Pairs);

/// <summary>
/// A provider that joins the factory's shared scope provider, as real ones (Application Insights,
/// the console) do, and records what it sees; optionally throws for chosen lines.
/// </summary>
internal sealed class RecordingProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly List<RecordedLine> _lines = [];
    private readonly Lock _gate = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Lines for which Log throws <see cref="InvalidOperationException"/> after recording nothing.</summary>
    public Func<RecordedLine, bool>? ThrowWhen { get; init; }

    /// <summary>Called on the logging thread after each line is recorded, so a test can re-enter the library from a provider.</summary>
    public Action<RecordedLine>? AfterLine { get; set; }

    public IReadOnlyList<RecordedLine> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Recorder(RecordingProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            List<KeyValuePair<string, object?>> pairs = [];
            owner._scopes.ForEachScope(
                static (scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                    {
                        list.AddRange(scopePairs);
                    }
                },
                pairs);

            RecordedLine line = new(eventId.Id, category, formatter(state, exception), Activity.Current?.TraceId.ToString(), pairs);
            if (owner.ThrowWhen?.Invoke(line) == true)
            {
                throw new InvalidOperationException($"Provider failure on event {eventId.Id}.");
            }

            lock (owner._gate)
            {
                owner._lines.Add(line);
            }

            owner.AfterLine?.Invoke(line);
        }
    }
}
