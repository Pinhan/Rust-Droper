using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using static RustDroper.Core.Tests.TestTime;

namespace RustDroper.Core.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task PermitsWithinWindow_ThenBlocksUntilWindowPasses()
    {
        var fake = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromSeconds(1), fake);
        for (var i = 0; i < 3; i++)
        {
            await limiter.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        var fourth = limiter.WaitAsync();
        await Task.Delay(30);
        Assert.False(fourth.IsCompleted);

        fake.Advance(TimeSpan.FromSeconds(1));
        await fourth.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancellation_AbortsWaiting()
    {
        var fake = new FakeTimeProvider();
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromSeconds(10), fake);
        await limiter.WaitAsync();
        using var cts = new CancellationTokenSource();

        var second = limiter.WaitAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }
}

public class RetryTests
{
    private static readonly Uri Url = new("https://example.test/x");

    private static RetryOptions NoJitter(int attempts = 5) => new() { MaxAttempts = attempts, JitterFraction = 0 };

    [Fact]
    public async Task TransientFailures_AreRetriedWithExponentialBackoff_ThenSucceed()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var delays = new List<TimeSpan>();

        var result = await PumpAsync(
            Retry.ExecuteAsync(
                _ => ++calls < 3 ? throw new HttpRequestException("boom") : Task.FromResult(42),
                NoJitter(),
                fake,
                onRetry: (_, delay, _) => delays.Add(delay)),
            fake);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried()
    {
        var calls = 0;

        await Assert.ThrowsAsync<PlatformDataException>(() => Retry.ExecuteAsync<int>(
            _ =>
            {
                calls++;
                throw new PlatformDataException("şema değişti");
            },
            NoJitter()));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustedAttempts_ThrowLastException()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;

        var task = Retry.ExecuteAsync<int>(
            _ =>
            {
                calls++;
                throw new HttpRequestException("down");
            },
            NoJitter(3),
            fake);

        await Assert.ThrowsAsync<HttpRequestException>(() => PumpAsync(task, fake));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RetryAfterHeader_IsRespected()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var delays = new List<TimeSpan>();

        await PumpAsync(
            Retry.ExecuteAsync(
                _ => ++calls < 2
                    ? throw new PlatformHttpException(HttpStatusCode.TooManyRequests, Url, TimeSpan.FromSeconds(30))
                    : Task.FromResult(1),
                NoJitter(),
                fake,
                onRetry: (_, delay, _) => delays.Add(delay)),
            fake);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(delays));
    }

    [Fact]
    public async Task Cancellation_DuringBackoff_Aborts()
    {
        var fake = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var task = Retry.ExecuteAsync<int>(_ => throw new HttpRequestException("x"), NoJitter(), fake, cancellationToken: cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Delay_IsCappedAtMaxDelay()
    {
        var options = new RetryOptions { JitterFraction = 0, MaxDelay = TimeSpan.FromSeconds(60) };

        Assert.Equal(TimeSpan.FromSeconds(60), Retry.ComputeDelay(12, options, new HttpRequestException(), new Random(1)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void HttpStatus_Classification(HttpStatusCode status, bool transient)
    {
        Assert.Equal(transient, Retry.IsTransient(new PlatformHttpException(status, Url)));
    }

    [Fact]
    public void HttpClientTimeout_IsTransient()
    {
        Assert.True(Retry.IsTransient(new TaskCanceledException("t", new TimeoutException())));
        Assert.False(Retry.IsTransient(new InvalidOperationException()));
    }
}

public class RollingFileLoggerTests
{
    private static FakeTimeProvider ClockAt(string utc)
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse(utc));
        fake.SetLocalTimeZone(TimeZoneInfo.Utc);
        return fake;
    }

    [Fact]
    public void WritesLevelMessageAndMasksSecrets()
    {
        using var dir = new TempDir();
        using var provider = new RollingFileLoggerProvider(dir.Path, LogLevel.Information, 7, ClockAt("2026-10-15T10:00:00Z"));
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("Giriş yapıldı Authorization: Bearer abcdefghijklmnop1234 {Count}", 3);
        logger.LogDebug("görünmemeli");
        logger.LogError(new InvalidOperationException("patladı"), "hata");

        // Günlük dosyası yazıcı tarafından açık tutulur (FileShare.ReadWrite) → okuyucu da paylaşımlı açmalı.
        using var stream = new FileStream(Path.Combine(dir.Path, "rustdroper-20261015.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        Assert.Contains("[INF] Test: Giriş yapıldı", text, StringComparison.Ordinal);
        Assert.Contains("3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop1234", text, StringComparison.Ordinal);
        Assert.DoesNotContain("görünmemeli", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RollsOverOnNewDay_AndDeletesFilesOlderThanRetention()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "rustdroper-20260101.log"), "old");
        File.WriteAllText(Path.Combine(dir.Path, "rustdroper-20261013.log"), "recent");
        var clock = ClockAt("2026-10-15T23:59:00Z");
        using var provider = new RollingFileLoggerProvider(dir.Path, LogLevel.Information, 7, clock);
        var logger = provider.CreateLogger("T");

        logger.LogInformation("gün 1");
        clock.Advance(TimeSpan.FromMinutes(2));
        logger.LogInformation("gün 2");

        Assert.True(File.Exists(Path.Combine(dir.Path, "rustdroper-20261015.log")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "rustdroper-20261016.log")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "rustdroper-20261013.log")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "rustdroper-20260101.log")));
    }
}
