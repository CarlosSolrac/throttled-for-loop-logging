using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        (IOperationScope operation, WeakReference captured) = BeginInsideAContextHoldingABigObject(harness.Logger);

        operation.Success();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The operation itself is still referenced, as a caller holding onto it would; what it
        // captured must not be.
        Assert.False(captured.IsAlive);
        GC.KeepAlive(operation);
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
            for (int i = 0; i < 100 && !provider.Lines.Any(l => l.EventId == 9004 && l.Message.Contains("Succeeded", StringComparison.Ordinal)); i++)
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

    /// <summary>
    /// Begins an operation on another thread whose context holds a large object in an AsyncLocal,
    /// and returns the operation with a weak reference to that object. Nothing on the test's own
    /// thread refers to the object, so only the operation's captured context can keep it alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IOperationScope Operation, WeakReference Captured) BeginInsideAContextHoldingABigObject(OperationLogger logger)
        => Task.Run(() =>
        {
            byte[] big = new byte[1024 * 1024];
            RequestState.Value = big;
            return (logger.BeginOperation("ImportOrders"), new WeakReference(big));
        }).GetAwaiter().GetResult();

    private static readonly AsyncLocal<byte[]?> RequestState = new();

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
