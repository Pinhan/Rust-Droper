using Microsoft.Extensions.Logging;
using RustDroper.App.Infrastructure;

namespace RustDroper.App.Tests;

internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

public class MemoryTests
{
    [Fact]
    public void Report_CapturesPositiveValues_AndFormatsReadably()
    {
        var report = MemoryReport.Capture();

        Assert.True(report.PrivateBytes > 0);
        Assert.True(report.WorkingSet > 0);
        Assert.True(report.Handles > 0);
        Assert.True(report.Threads > 0);
        Assert.Contains("özel=", report.ToString());
        Assert.Contains("tutamaç=", report.ToString());
    }

    [Fact]
    public void Tick_WritesAMemoryLine_ForBothWindowStates_AndNeverThrows()
    {
        var logger = new CapturingLogger();
        var windowOpen = false;
        using var monitor = new MemoryMonitor(() => windowOpen, TimeSpan.FromHours(1), logger);

        monitor.Tick();
        windowOpen = true;
        monitor.Tick();

        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("tepside", logger.Entries[0].Message);
        Assert.Contains("pencere açık", logger.Entries[1].Message);
    }

    [Fact]
    public void Tick_SwallowsFailures()
    {
        var logger = new CapturingLogger();
        using var monitor = new MemoryMonitor(() => throw new InvalidOperationException("boom"), TimeSpan.FromHours(1), logger);

        monitor.Tick();

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }
}
