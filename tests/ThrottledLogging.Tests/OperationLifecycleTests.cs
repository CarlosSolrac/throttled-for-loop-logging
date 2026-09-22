using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace ThrottledLogging.Tests;

/// <summary>R1 and R2: entry, success and failure of the function itself.</summary>
public sealed class OperationLifecycleTests
{
    [Fact]
    public void BeginOperation_logs_entry_once()
    {
        using TestHarness harness = new();

        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders");

        Assert.Single(harness.Records, r => r.Id == 9000);
        Assert.Contains("ImportOrders", harness.Records[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Operation_logs_success_on_Success()
    {
        using TestHarness harness = new();

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders"))
        {
            operation.Success();
        }

        Assert.Single(harness.Records, r => r.Id == 9001);
        Assert.DoesNotContain(harness.Records, r => r.Id == 9002 || r.Id == 9003);
    }

    [Fact]
    public void Operation_logs_success_only_once_even_if_called_twice()
    {
        using TestHarness harness = new();

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders"))
        {
            operation.Success();
            operation.Success();
        }

        Assert.Single(harness.Records, r => r.Id == 9001);
    }

    [Fact]
    public void Operation_logs_failure_with_exception_on_Failure()
    {
        using TestHarness harness = new();
        InvalidOperationException error = new("the database went away");

        using (IOperationScope operation = harness.Logger.BeginOperation("ImportOrders"))
        {
            operation.Failure(error);
        }

        FakeLogRecord record = Assert.Single(harness.Records, r => r.Id == 9002);
        Assert.Same(error, record.Exception);
        Assert.Equal(LogLevel.Warning, record.Level);
    }

    [Fact]
    public void Operation_disposed_without_an_outcome_is_logged_as_incomplete()
    {
        using TestHarness harness = new();

        using (harness.Logger.BeginOperation("ImportOrders"))
        {
            // Neither Success nor Failure: the caller returned, or threw, without saying which.
        }

        Assert.Single(harness.Records, r => r.Id == 9003);
    }

    [Fact]
    public void Operation_name_becomes_the_log_category_so_it_can_be_filtered()
    {
        using TestHarness harness = new();

        using (harness.Logger.BeginOperation("ImportOrders"))
        {
        }

        Assert.Contains(harness.Records, r => r.Category == "ThrottledLogging.ImportOrders");
    }

    [Fact]
    public void Options_handed_to_BeginOperation_replace_the_configured_defaults()
    {
        using TestHarness harness = new(options => options.Defaults.EveryItems = 10_000);

        // The caller sets one property on a brand-new instance, so every other setting is the
        // type's own default rather than the configured one. This is the behaviour the
        // configure overload below exists to avoid.
        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", new OperationOptions { TotalItems = 100 });

        for (int item = 1; item <= 1_200; item++)
        {
            harness.RunItem(operation, $"item-{item}", TimeSpan.FromMilliseconds(1));
        }

        // Two events an item against the type's default of 500, not the configured 10,000.
        Assert.True(harness.Progress.Count > 2, $"expected the type default of 500 to apply, but only {harness.Progress.Count} lines were written");
    }

    [Fact]
    public void The_configure_overload_starts_from_the_configured_defaults()
    {
        using TestHarness harness = new(options => options.Defaults.EveryItems = 10_000);

        using IOperationScope operation = harness.Logger.BeginOperation("ImportOrders", options => options.TotalItems = 100);

        for (int item = 1; item <= 1_200; item++)
        {
            harness.RunItem(operation, $"item-{item}", TimeSpan.FromMilliseconds(1));
        }

        // 2,400 events against a threshold of 10,000: only the first event clears it.
        Assert.Single(harness.Progress);
        Assert.Equal(100, operation.Snapshot().Total);
    }

    [Fact]
    public void DefaultOptions_hands_out_a_copy_so_a_caller_cannot_change_the_defaults()
    {
        using TestHarness harness = new(options => options.Defaults.EveryItems = 10_000);

        OperationOptions first = harness.Logger.DefaultOptions;
        Assert.Equal(10_000, first.EveryItems);

        first.EveryItems = 7;

        Assert.Equal(10_000, harness.Logger.DefaultOptions.EveryItems);
    }
}
