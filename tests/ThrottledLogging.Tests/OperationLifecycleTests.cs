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
}
