using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Watching;

public enum HlsQuality
{
    /// <summary>Video içeren en düşük bant genişlikli varyant (ör. 160p).</summary>
    LowestVideo,

    /// <summary>Yalnız ses varyantı (varsa), yoksa en düşük bant genişlikli.</summary>
    AudioOnly,
}

/// <summary>
/// HLS izleyicinin platformdan bağımsız çekirdeği: ana listeden en düşük kaliteyi seçer, medya listesini yoklar, yeni parçaları
/// indirir (gerçek oynatıcı trafiği; tarayıcı/telemetri yok). Platforma özgü kısım <see cref="OpenAsync"/>: kanalın erişim
/// token'ını alıp ana listeyi bulmak (Twitch: GQL + usher, Kick: kanal API'sindeki <c>playback_url</c>).
/// </summary>
public abstract class HlsWatcherBase : IWatcher
{
    private const int MaxConsecutiveFailures = 6;

    private readonly HlsQuality _quality;

    protected HlsWatcherBase(HttpClient http, HlsQuality quality, TimeProvider? timeProvider, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        Http = http;
        _quality = quality;
        Time = timeProvider ?? TimeProvider.System;
        Logger = logger ?? NullLogger.Instance;
    }

    public WatcherStats Stats { get; } = new();

    protected HttpClient Http { get; }

    protected TimeProvider Time { get; }

    protected ILogger Logger { get; }

    public async Task<WatchResult> RunAsync(LiveChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var open = await OpenAsync(channel, cancellationToken).ConfigureAwait(false);
                if (open.Variant is null)
                {
                    return new WatchResult(open.Offline ? WatchEnd.Offline : WatchEnd.Failed, open.Reason);
                }

                var outcome = await PlayAsync(open.Variant, cancellationToken).ConfigureAwait(false);
                switch (outcome.Kind)
                {
                    case PlayEnd.Reopen:
                        Logger.LogInformation("{Channel}: erişim token'ı yenileniyor.", channel.Login);
                        break;
                    case PlayEnd.Offline:
                        return new WatchResult(WatchEnd.Offline, outcome.Reason);
                    default:
                        return new WatchResult(WatchEnd.Failed, outcome.Reason);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WatchResult(WatchEnd.Cancelled, null);
        }
    }

    /// <summary>Kanalın oynatma erişimini açar ve seçilen varyantın medya listesi adresini döndürür.</summary>
    protected abstract Task<OpenResult> OpenAsync(LiveChannel channel, CancellationToken cancellationToken);

    /// <summary>
    /// Ana listeyi (token'lı adres) indirir; 404 → <c>null</c> (yayın yok), diğer başarısızlıklar <see cref="PlatformHttpException"/>
    /// (istisnaya adresin yalnızca yolu girer: sorgudaki token sızmaz).
    /// </summary>
    protected async Task<string?> FetchMasterAsync(Uri masterUri, CancellationToken cancellationToken) =>
        await Retry.ExecuteAsync<string?>(
            async ct =>
            {
                using var response = await Http.GetAsync(masterUri, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new PlatformHttpException(response.StatusCode, new Uri(masterUri.GetLeftPart(UriPartial.Path)));
                }

                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            },
            timeProvider: Time,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Ana listeden en düşük kaliteli varyantı seçip <see cref="OpenResult"/> üretir.</summary>
    protected OpenResult SelectVariant(string masterText, Uri masterUri, string channelName)
    {
        var variants = HlsParser.ParseMaster(masterText, masterUri);
        var chosen = Choose(variants);
        if (chosen is null)
        {
            return OpenResult.Gone(Loc.T("watch.noVariant"), offline: false);
        }

        Stats.SetVariant(chosen.Name);
        Logger.LogInformation("{Channel}: varyant {Variant} ({Bandwidth} bps) seçildi.", channelName, chosen.Name, chosen.Bandwidth);
        return OpenResult.Ready(chosen.Uri);
    }

    private HlsVariant? Choose(IReadOnlyList<HlsVariant> variants)
    {
        if (variants.Count == 0)
        {
            return null;
        }

        if (_quality == HlsQuality.AudioOnly && variants.FirstOrDefault(v => !v.HasVideo) is { } audio)
        {
            return audio;
        }

        var video = variants.Where(v => v.HasVideo).ToList();
        var lowest = (video.Count > 0 ? video : variants).MinBy(v => v.Bandwidth);

        // Küçük/iş ortağı olmayan kanallarda alt kalite yoktur; "en düşük video" aslında kaynak kalitedir (canlı: 6,7 Mbps ≈ 41 MB/dk,
        // yaklaşık 2,5 GB/sa). Yalnız-ses sunucu dakika sayacını aynı hızda ilerletir (U1) → yalnız-ses varsa onu seç.
        if (lowest is { HasVideo: true } && lowest.Bandwidth > SourceOnlyBandwidthThreshold && variants.FirstOrDefault(v => !v.HasVideo) is { } audioFallback)
        {
            Logger.LogInformation(
                "En düşük video varyantı {Variant} {Bandwidth} bps (alt kalite yok); bant genişliğini korumak için yalnız-ses seçiliyor.",
                lowest.Name,
                lowest.Bandwidth);
            return audioFallback;
        }

        return lowest;
    }

    /// <summary>En düşük video varyantı bunu aşarsa (bps) "alt kalite yok" sayılır ve yalnız-ses tercih edilir.</summary>
    private const int SourceOnlyBandwidthThreshold = 2_000_000;

    private async Task<Outcome> PlayAsync(Uri mediaUri, CancellationToken ct)
    {
        long lastSequence = -1;
        var failures = 0;
        var notFound = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            HlsMediaPlaylist playlist;
            try
            {
                using var response = await Http.GetAsync(mediaUri, ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    return new Outcome(PlayEnd.Reopen, null);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (++notFound >= 3)
                    {
                        return new Outcome(PlayEnd.Offline, Loc.T("watch.media404"));
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), Time, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new PlatformHttpException(response.StatusCode, new Uri(mediaUri.GetLeftPart(UriPartial.Path)));
                }

                playlist = HlsParser.ParseMedia(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), mediaUri);
                failures = 0;
                notFound = 0;
            }
            catch (Exception ex) when (Retry.IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (++failures >= MaxConsecutiveFailures)
                {
                    return new Outcome(PlayEnd.Failed, Loc.T("watch.mediaFailed", failures, ex.Message));
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * failures, 15)), Time, ct).ConfigureAwait(false);
                continue;
            }

            if (playlist.Ended)
            {
                return new Outcome(PlayEnd.Offline, Loc.T("watch.endlist"));
            }

            var fresh = playlist.Segments.Where(s => !s.IsPrefetch && s.Sequence > lastSequence).ToList();
            if (lastSequence < 0 && fresh.Count > 1)
            {
                // İlk açılışta geçmişi doldurmak yerine canlı kenardaki son parçadan başla (gerçek oynatıcı gibi).
                fresh = [fresh[^1]];
            }

            foreach (var segment in fresh)
            {
                await FetchSegmentAsync(segment, ct).ConfigureAwait(false);
                lastSequence = segment.Sequence;
            }

            var wait = Math.Clamp(playlist.TargetDurationSeconds / 2, 1, 3);
            await Task.Delay(TimeSpan.FromSeconds(wait), Time, ct).ConfigureAwait(false);
        }
    }

    private async Task FetchSegmentAsync(HlsSegment segment, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(segment.Uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogDebug("Parça {Sequence} alınamadı: HTTP {Status}", segment.Sequence, (int)response.StatusCode);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            Stats.RecordSegment(total, Time.GetUtcNow());
        }
        catch (HttpRequestException ex)
        {
            Logger.LogDebug("Parça {Sequence} indirilemedi: {Reason}", segment.Sequence, ex.Message);
        }
    }

    private enum PlayEnd
    {
        Reopen,
        Offline,
        Failed,
    }

    private sealed record Outcome(PlayEnd Kind, string? Reason);

    protected sealed record OpenResult(Uri? Variant, bool Offline, string? Reason)
    {
        public static OpenResult Ready(Uri variant) => new(variant, false, null);

        public static OpenResult Gone(string reason, bool offline) => new(null, offline, reason);
    }
}
