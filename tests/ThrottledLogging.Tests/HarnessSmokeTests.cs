using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ThrottledLogging.Tests;

/// <summary>
/// Proves the red/green loop and the two test doubles the throttling tests will lean on:
/// <c>FakeLogger</c> to capture what was written, and <c>FakeTimeProvider</c> to advance the
/// clock so time-based thresholds can be tested without sleeping.
/// </summary>
public sealed class HarnessSmokeTests
{
    [Fact]
    public void FakeLogger_captures_what_was_written()
    {
        FakeLogger<HarnessSmokeTests> logger = new();

        logger.LogInformation("Processing {Item}", "order-1");

        FakeLogRecord record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal("Processing order-1", record.Message);
    }

    [Fact]
    public void FakeTimeProvider_advances_without_sleeping()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        DateTimeOffset start = time.GetUtcNow();

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), time.GetUtcNow() - start);
    }
}
