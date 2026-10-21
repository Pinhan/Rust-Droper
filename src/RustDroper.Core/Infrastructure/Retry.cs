using RustDroper.Core.Platforms;

namespace RustDroper.Core.Infrastructure;

public sealed record RetryOptions
{
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gecikmeye ±bu oranda rastgelelik (eşzamanlı yeniden denemelerin üst üste binmemesi için).</summary>
    public double JitterFraction { get; init; } = 0.2;
}

/// <summary>Üstel geri çekilmeli yeniden deneme. Yalnızca geçici hatalar tekrarlanır.</summary>
public static class Retry
{
    /// <summary>Geçici = tekrar denemeye değer (ağ, zaman aşımı, 5xx/429). Şema/kimlik/kalıcı hatalar değil.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        PlatformHttpException http => http.IsTransient,
        PlatformDataException => false,
        Platforms.Twitch.TwitchTransientException => true,
        HttpRequestException => true,
        TimeoutException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        IOException => true,
        _ => false,
    };

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<Exception, bool>? isTransient = null,
        Action<int, TimeSpan, Exception>? onRetry = null,
        Random? random = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        options ??= new RetryOptions();
        timeProvider ??= TimeProvider.System;
        isTransient ??= IsTransient;
        random ??= Random.Shared;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && attempt < options.MaxAttempts
                                       && isTransient(ex))
            {
                var delay = ComputeDelay(attempt, options, ex, random);
                onRetry?.Invoke(attempt, delay, ex);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        RetryOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<Exception, bool>? isTransient = null,
        Action<int, TimeSpan, Exception>? onRetry = null,
        Random? random = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync(
            async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            },
            options,
            timeProvider,
            isTransient,
            onRetry,
            random,
            cancellationToken);
    }

    internal static TimeSpan ComputeDelay(int failedAttempt, RetryOptions options, Exception exception, Random random)
    {
        var exponential = options.BaseDelay.TotalSeconds * Math.Pow(2, failedAttempt - 1);
        var seconds = Math.Min(exponential, options.MaxDelay.TotalSeconds);
        var jitter = 1 + ((random.NextDouble() * 2) - 1) * options.JitterFraction;
        var delay = TimeSpan.FromSeconds(seconds * jitter);

        // Sunucu açıkça bekletiyorsa (Retry-After) ondan önce tekrar denemeyiz.
        if (exception is PlatformHttpException { RetryAfter: { } retryAfter } && retryAfter > delay)
        {
            delay = retryAfter;
        }

        return delay;
    }
}
