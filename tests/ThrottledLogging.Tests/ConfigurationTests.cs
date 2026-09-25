using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ThrottledLogging.Tests;

/// <summary>
/// Settings read from configuration, and reloaded when it changes: new operations take the new
/// defaults, running ones keep their own, and a bad reload never replaces good settings.
/// </summary>
public sealed class ConfigurationTests
{
    private const string Json = """
        {
          "ThrottledLogging": {
            "EnableSweeper": false,
            "MinimumSweepInterval": "00:00:02",
            "MaximumSweepInterval": "00:00:20",
            "Defaults": {
              "EveryItems": 1000,
              "EveryInterval": "01:00:00",
              "Level": "Debug",
              "FailureEveryItems": 7,
              "FailureLevel": "Error",
              "Scope": { "Team": "orders" }
            }
          }
        }
        """;

    [Fact]
    public void Settings_bind_from_a_json_file()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Json)))
            .Build();
        using Host host = new(configuration);

        OperationOptions defaults = host.Logger.DefaultOptions;

        Assert.Equal(1000, defaults.EveryItems);
        Assert.Equal(TimeSpan.FromHours(1), defaults.EveryInterval);
        Assert.Equal(LogLevel.Debug, defaults.Level);
        Assert.Equal(7, defaults.FailureEveryItems);
        Assert.Equal(LogLevel.Error, defaults.FailureLevel);
        Assert.Equal("orders", Assert.Single(defaults.Scope!, p => p.Key == "Team").Value);
        Assert.False(host.Logger.IsSweeperRunning);
    }

    [Fact]
    public void A_reload_changes_the_defaults_of_operations_begun_afterwards_only()
    {
        using Host host = new(Memory(("Defaults:EveryItems", "1000"), ("Defaults:EveryInterval", "01:00:00")));
        using IOperationScope before = host.Logger.BeginOperation("Before");

        host.Set("Defaults:EveryItems", "1");
        using IOperationScope after = host.Logger.BeginOperation("After");

        Assert.Equal(1, host.Logger.DefaultOptions.EveryItems);
        for (int i = 0; i < 5; i++)
        {
            RunItem(before, $"b-{i}");
            RunItem(after, $"a-{i}");
        }

        // Ten progress events each. At one line per 1,000 only the first is written; at one per
        // item, all ten are.
        Assert.Single(host.Emitted, e => e.OperationName == "Before");
        Assert.Equal(10, host.Emitted.Count(e => e.OperationName == "After"));
    }

    [Fact]
    public void An_invalid_reload_is_rejected_with_a_warning_and_the_last_good_settings_stay()
    {
        using Host host = new(Memory(("Defaults:EveryItems", "1000")));

        host.Set("Defaults:EveryItems", "0");

        Assert.Equal(1000, host.Logger.DefaultOptions.EveryItems);
        FakeLogRecord rejected = Assert.Single(host.Collector.GetSnapshot(), r => r.Id.Id == 9011);
        Assert.Equal(LogLevel.Warning, rejected.Level);
        Assert.IsType<ArgumentOutOfRangeException>(rejected.Exception);

        // Still listening: the next good reload is taken.
        host.Set("Defaults:EveryItems", "3");
        Assert.Equal(3, host.Logger.DefaultOptions.EveryItems);
    }

    [Fact]
    public void An_invalid_sweep_interval_is_rejected_like_an_invalid_default()
    {
        using Host host = new(Memory(("MinimumSweepInterval", "00:00:01"), ("MaximumSweepInterval", "00:00:30")));

        host.Set("MaximumSweepInterval", "00:00:00.5");

        Assert.Single(host.Collector.GetSnapshot(), r => r.Id.Id == 9011);
    }

    [Fact]
    public void A_value_that_cannot_be_converted_is_rejected_like_an_invalid_one()
    {
        using Host host = new(Memory(("Defaults:EveryItems", "1000")));

        // Must not throw into whoever raised the reload (a file watcher, in a real host).
        host.Set("Defaults:EveryItems", "abc");

        Assert.Equal(1000, host.Logger.DefaultOptions.EveryItems);
        FakeLogRecord rejected = Assert.Single(host.Collector.GetSnapshot(), r => r.Id.Id == 9011);
        Assert.IsType<InvalidOperationException>(rejected.Exception);

        host.Set("Defaults:EveryItems", "3");
        Assert.Equal(3, host.Logger.DefaultOptions.EveryItems);
    }

    [Fact]
    public void A_value_that_cannot_be_converted_at_startup_still_throws()
    {
        IConfigurationRoot configuration = Memory(("Defaults:EveryItems", "abc"));

        Assert.Throws<InvalidOperationException>(() => new Host(configuration));
    }

    [Theory]
    [InlineData("00:00:00.0001", "00:00:00.0001")]   // below PeriodicTimer's one millisecond
    [InlineData("00:00:01", "60.00:00:00")]          // above its limit of about 49.7 days
    public void A_sweep_interval_the_timer_cannot_run_at_is_rejected(string minimum, string maximum)
    {
        using Host host = new(Memory(("MinimumSweepInterval", "00:00:01"), ("MaximumSweepInterval", "00:00:30")));

        host.Set("MinimumSweepInterval", minimum);
        host.Set("MaximumSweepInterval", maximum);

        Assert.Contains(host.Collector.GetSnapshot(), r => r.Id.Id == 9011);
    }

    [Fact]
    public void A_change_to_a_named_instance_of_the_options_is_ignored()
    {
        IConfigurationRoot other = Memory(("Defaults:EveryItems", "5"));
        using Host host = new(
            Memory(("Defaults:EveryItems", "1000")),
            services => services.AddOptions<ThrottledLoggingOptions>("Other").Bind(other.GetSection("ThrottledLogging")));

        other["ThrottledLogging:Defaults:EveryItems"] = "6";
        other.Reload();

        Assert.Equal(1000, host.Logger.DefaultOptions.EveryItems);
    }

    [Fact]
    public void An_observer_set_in_code_survives_a_reload()
    {
        using Host host = new(Memory(("Defaults:EveryItems", "1")));

        host.Set("Defaults:EveryItems", "2");
        using (IOperationScope operation = host.Logger.BeginOperation("ImportOrders"))
        {
            RunItem(operation, "order-1");
        }

        Assert.NotEmpty(host.Emitted);
    }

    [Fact]
    public async Task Turning_the_sweeper_on_and_off_by_reload_starts_and_stops_it()
    {
        using Host host = new(Memory(("EnableSweeper", "false"), ("Defaults:EveryInterval", "00:00:04")));
        Assert.False(host.Logger.IsSweeperRunning);

        host.Set("EnableSweeper", "true");
        Assert.True(host.Logger.IsSweeperRunning);

        // The sweeper that was switched on produces a heartbeat for a held event.
        using IOperationScope operation = host.Logger.BeginOperation("ImportOrders");
        RunItem(operation, "order-1");   // Started: the first event, written; Succeeded: held.
        // The sweeper runs on its own thread, so the test cannot step it directly; it keeps the fake
        // clock moving until the heartbeat appears, with a generous real-time bound for slow machines.
        Stopwatch waited = Stopwatch.StartNew();
        while (!host.Emitted.Any(e => e.Outcome == ItemOutcome.Succeeded) && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Contains(host.Emitted, e => e.Outcome == ItemOutcome.Succeeded);

        host.Set("EnableSweeper", "false");
        Assert.False(host.Logger.IsSweeperRunning);
    }

    [Fact]
    public void Dispose_stops_a_sweeper_started_by_reload_and_ignores_later_reloads()
    {
        Host host = new(Memory(("EnableSweeper", "false")));
        host.Set("EnableSweeper", "true");

        host.Logger.Dispose();
        Assert.False(host.Logger.IsSweeperRunning);

        host.Set("EnableSweeper", "true");
        host.Set("Defaults:EveryItems", "0");
        Assert.False(host.Logger.IsSweeperRunning);
        Assert.DoesNotContain(host.Collector.GetSnapshot(), r => r.Id.Id == 9011);
        host.Dispose();
    }

    [Fact]
    public void A_change_callback_handed_stale_settings_still_applies_the_current_ones()
    {
        // Two reloads racing: the callback for an older change runs after the one for the newer
        // change, handing over the older value. The newest settings must stay in force.
        StubMonitor monitor = new(new ThrottledLoggingOptions { EnableSweeper = false, Defaults = new OperationOptions { EveryItems = 1 } });
        using ILoggerFactory factory = LoggerFactory.Create(static _ => { });
        using OperationLogger logger = new(factory, monitor, TimeProvider.System);

        ThrottledLoggingOptions older = new() { EnableSweeper = false, Defaults = new OperationOptions { EveryItems = 2 } };
        ThrottledLoggingOptions newer = new() { EnableSweeper = false, Defaults = new OperationOptions { EveryItems = 3 } };
        monitor.CurrentValue = newer;
        monitor.Raise(newer);
        monitor.Raise(older);

        Assert.Equal(3, logger.DefaultOptions.EveryItems);
    }

    private static void RunItem(IOperationScope operation, string label)
    {
        using IItemScope item = operation.BeginItem(label);
        item.Success();
    }

    /// <summary>An in-memory configuration whose values can be changed and reloaded.</summary>
    private static IConfigurationRoot Memory(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(static s => new KeyValuePair<string, string?>("ThrottledLogging:" + s.Key, s.Value)))
            .Build();

    /// <summary>An options monitor whose change callbacks the test raises by hand, with any value.</summary>
    private sealed class StubMonitor(ThrottledLoggingOptions initial) : IOptionsMonitor<ThrottledLoggingOptions>
    {
        private Action<ThrottledLoggingOptions, string?>? _listener;

        public ThrottledLoggingOptions CurrentValue { get; set; } = initial;

        public ThrottledLoggingOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ThrottledLoggingOptions, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void Raise(ThrottledLoggingOptions handed) => _listener?.Invoke(handed, Options.DefaultName);
    }

    /// <summary>
    /// A container wired the way a host wires it: AddThrottledLogging bound to the
    /// "ThrottledLogging" section, with an observer set in code, a captured log and a fake clock.
    /// </summary>
    private sealed class Host : IDisposable
    {
        private readonly IConfigurationRoot _configuration;
        private readonly ServiceProvider _provider;
        private readonly List<ThrottledEvent> _emitted = [];
        private readonly Lock _gate = new();

        public Host(IConfigurationRoot configuration, Action<IServiceCollection>? moreServices = null)
        {
            _configuration = configuration;
            Collector = new FakeLogCollector();
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

            ServiceCollection services = new();
            services.AddLogging(builder => builder.AddProvider(new FakeLoggerProvider(Collector)).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton<TimeProvider>(Time);
            services.AddThrottledLogging(configuration.GetSection("ThrottledLogging"), options => options.OnEmitted = Record);
            moreServices?.Invoke(services);
            _provider = services.BuildServiceProvider();
            Logger = _provider.GetRequiredService<OperationLogger>();
        }

        public FakeLogCollector Collector { get; }

        public FakeTimeProvider Time { get; }

        public OperationLogger Logger { get; }

        public IReadOnlyList<ThrottledEvent> Emitted
        {
            get
            {
                lock (_gate)
                {
                    return [.. _emitted];
                }
            }
        }

        /// <summary>Changes one value and reloads, as a changed appsettings.json would.</summary>
        public void Set(string key, string value)
        {
            _configuration["ThrottledLogging:" + key] = value;
            _configuration.Reload();
        }

        public void Dispose() => _provider.Dispose();

        private void Record(ThrottledEvent emitted)
        {
            lock (_gate)
            {
                _emitted.Add(emitted);
            }
        }
    }
}
