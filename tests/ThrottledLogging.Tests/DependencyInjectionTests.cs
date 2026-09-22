using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace ThrottledLogging.Tests;

/// <summary>R3: the library is reached through dependency injection, like any other ILogger consumer.</summary>
public sealed class DependencyInjectionTests
{
    private static ServiceProvider Build(Action<ThrottledLoggingOptions>? configure = null)
    {
        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddProvider(new FakeLoggerProvider()).SetMinimumLevel(LogLevel.Trace));
        if (configure is null)
        {
            services.AddThrottledLogging();
        }
        else
        {
            services.AddThrottledLogging(configure);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Both_interfaces_resolve_to_one_instance()
    {
        using ServiceProvider provider = Build();

        IOperationLogger logger = provider.GetRequiredService<IOperationLogger>();
        IOperationRegistry registry = provider.GetRequiredService<IOperationRegistry>();

        // Two singletons would each see only the operations they created, which would make the
        // registry useless.
        Assert.Same(logger, registry);
    }

    [Fact]
    public void An_injected_logger_creates_operations_the_registry_can_see()
    {
        using ServiceProvider provider = Build(static options => options.EnableSweeper = false);
        IOperationLogger logger = provider.GetRequiredService<IOperationLogger>();
        IOperationRegistry registry = provider.GetRequiredService<IOperationRegistry>();

        using IOperationScope operation = logger.BeginOperation("ImportOrders");

        OperationSnapshot only = Assert.Single(registry.GetActiveOperations());
        Assert.Equal("ImportOrders", only.Name);
    }

    [Fact]
    public void Configured_defaults_apply_to_operations_that_supply_none()
    {
        using ServiceProvider provider = Build(static options =>
        {
            options.EnableSweeper = false;
            options.Defaults.EveryItems = 3;
            options.Defaults.EveryInterval = TimeSpan.FromHours(1);
        });

        List<ThrottledEvent> emitted = [];
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ThrottledLoggingOptions>>().Value.OnEmitted = emitted.Add;

        IOperationLogger logger = provider.GetRequiredService<IOperationLogger>();
        using IOperationScope operation = logger.BeginOperation("ImportOrders");

        for (int i = 0; i < 9; i++)
        {
            using IItemScope item = operation.BeginItem($"order-{i}");
            item.Success();
        }

        // Eighteen progress events at one line per three.
        Assert.Equal(6, emitted.Count);
    }

    [Fact]
    public void Invalid_options_are_rejected_when_the_operation_starts()
    {
        using TestHarness harness = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => harness.Logger.BeginOperation("ImportOrders", new OperationOptions { EveryItems = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => harness.Logger.BeginOperation("ImportOrders", new OperationOptions { EtaConfidence = 1.0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => harness.Logger.BeginOperation("ImportOrders", new OperationOptions { EveryInterval = TimeSpan.Zero }));
    }

    [Fact]
    public void Options_are_copied_so_a_caller_cannot_change_them_underneath_a_running_operation()
    {
        using TestHarness harness = new();
        OperationOptions options = new() { EveryItems = 1_000_000, EveryInterval = TimeSpan.FromHours(1) };

        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", options);
        options.EveryItems = 1;

        for (int i = 0; i < 20; i++)
        {
            harness.RunItem(operation, $"order-{i}", TimeSpan.FromMilliseconds(1));
        }

        Assert.Single(harness.Progress);
    }
}
