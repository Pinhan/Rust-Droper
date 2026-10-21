using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Watching;

/// <summary>Kick gizli-oynatıcı izleyicisinin zamanlama/yeniden başlatma ayarları.</summary>
public sealed record KickWebViewOptions
{
    /// <summary>Oynatıcı durumunun yoklanma aralığı.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Sayfa açıldıktan sonra oynatmanın başlaması için beklenen en uzun süre; aşılırsa yeniden başlatılır.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Oynatma durduktan (donma/duraklatma/tamponlama) bu kadar sonra sayfa yeniden başlatılır.</summary>
    public TimeSpan StallRestartAfter { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary><see cref="RestartWindow"/> içinde en çok bu kadar yeniden başlatma; aşılırsa izleyici vazgeçer (motor kanalı değiştirir).</summary>
    public int MaxRestarts { get; init; } = 4;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Kick için Tier B izleyici: <see cref="IKickPlayerHost"/> aracılığıyla Kick'in <b>gerçek web oynatıcısını</b> (gizli pencere, sessiz, en düşük kalite)
/// çalıştırır; izleme kredisini Kick'in kendi oynatıcısı üretir. Bu sınıf yalnızca politika uygular: başlat, oynatmayı izle, donarsa yeniden başlat,
/// yayın kapanırsa bildir, vazgeçerse nedenini söyle. Kick kimliği/çerezi/belirteci bu katmandan hiç geçmez.
/// </summary>
public sealed class KickWebViewWatcher : IWatcher
{
    public static string VariantName => Loc.T("watch.variant.webview");

    private readonly IKickPlayerHost _host;
    private readonly KickWebViewOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public KickWebViewWatcher(IKickPlayerHost host, KickWebViewOptions? options = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _options = options ?? new KickWebViewOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public WatcherStats Stats { get; } = new();

    public async Task<WatchResult> RunAsync(LiveChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var slug = channel.Login;
        Stats.SetVariant(VariantName);
        var restarts = new Queue<DateTimeOffset>();
        try
        {
            await _host.OpenAsync(slug, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Channel}: gizli Kick oynatıcısı başlatıldı.", slug);

            var phaseSince = _time.GetUtcNow(); // son (yeniden) başlatma zamanı
            DateTimeOffset? stalledSince = null;
            while (true)
            {
                await Task.Delay(_options.CheckInterval, _time, cancellationToken).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                var status = _host.Status;

                if (status.State != KickPlayerState.Idle
                    && status.Slug is { } statusSlug
                    && !string.Equals(statusSlug, slug, StringComparison.OrdinalIgnoreCase))
                {
                    return new WatchResult(WatchEnd.Failed, Loc.T("watch.kick.switched", statusSlug));
                }

                switch (status.State)
                {
                    case KickPlayerState.Playing:
                        stalledSince = null;
                        Stats.RecordPlaying(now);
                        continue;

                    case KickPlayerState.Offline:
                        return new WatchResult(WatchEnd.Offline, status.Detail ?? Loc.T("watch.kick.notLive"));

                    case KickPlayerState.LoginRequired:
                        return new WatchResult(WatchEnd.Failed, status.Detail ?? Loc.T("watch.kick.noSession"));

                    case KickPlayerState.Unavailable:
                        return new WatchResult(WatchEnd.Failed, status.Detail ?? Loc.T("watch.kick.unavailable"));

                    case KickPlayerState.Starting:
                        if (now - phaseSince < _options.StartTimeout)
                        {
                            continue;
                        }

                        break;

                    case KickPlayerState.Stalled:
                        stalledSince ??= now;
                        if (now - stalledSince < _options.StallRestartAfter)
                        {
                            continue;
                        }

                        break;

                    case KickPlayerState.Failed:
                    case KickPlayerState.Idle:
                        break;
                }

                // Buraya gelindi: başlatma zaman aşımı, uzun donma, hata ya da pencere beklenmedik biçimde kapandı → yeniden başlat (bütçe dahilinde).
                while (restarts.Count > 0 && now - restarts.Peek() > _options.RestartWindow)
                {
                    restarts.Dequeue();
                }

                if (restarts.Count >= _options.MaxRestarts)
                {
                    return new WatchResult(
                        WatchEnd.Failed,
                        Loc.T("watch.kick.restarted", _options.RestartWindow.TotalMinutes, restarts.Count, status.State, status.Detail is null ? string.Empty : $" — {status.Detail}"));
                }

                restarts.Enqueue(now);
                _logger.LogWarning("{Channel}: oynatıcı yeniden başlatılıyor (durum: {State}).", slug, status.State);
                await _host.RestartAsync(cancellationToken).ConfigureAwait(false);
                phaseSince = _time.GetUtcNow();
                stalledSince = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WatchResult(WatchEnd.Cancelled, null);
        }
        catch (Exception ex)
        {
            // Ana bilgisayar sözleşmesi istisna fırlatmamayı vaat eder; yine de beklenmedik bir hata motoru çökertmemeli.
            _logger.LogError(ex, "{Channel}: Kick oynatıcı ana bilgisayarı beklenmedik hata verdi.", slug);
            return new WatchResult(WatchEnd.Failed, Loc.T("watch.kick.error", ex.GetType().Name));
        }
        finally
        {
            try
            {
                await _host.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Channel}: oynatıcı kapatılırken hata.", slug);
            }
        }
    }
}
