using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Platforms.Twitch.PubSub;

namespace RustDroper.Core.Platforms.Twitch;

/// <summary>
/// PubSub <c>drop-claim</c> olayına bağlı kalmayan yedek claim yolu (U8, canlı gözlem 2026-10-16).
/// <para>
/// Gerçek hesapla iki farklı kampanya sınıfında (yayıncı "reward" rozeti ve oyun kampanyasındaki Twitch sohbet rozeti) drop tamamlandı:
/// sunucu <c>drop-progress</c> (<c>current == required</c>) gönderdi, ödül hesaba verildi, ama <b>hiç <c>drop-claim</c> olayı gelmedi</b>.
/// Oyun-teslimli (Steam/Facepunch) ödüllerde olayın gelip gelmeyeceği 23 Ekim'e kadar bilinmiyor. Bu sınıf, sunucu "tamamlandı" dedikten
/// sonra <see cref="Grace"/> süresi içinde olay gelmezse claim kuyruğuna <c>dropInstanceID</c>'yi kendisi kurarak koyar.
/// </para>
/// <para>
/// Kimlik biçimi <c>{kullanıcıNo}#{kampanyaId}#{dropId}</c> (canlıda sunucu bu biçimi kabul etti: <c>ELIGIBLE_FOR_ALL</c>; geçersiz kimlik
/// <c>claimDropRewards: null</c> döner). Yalnızca <b>sunucunun tamamlandığını bildirdiği</b> drop için denenir; claim idempotenttir
/// (aynı kimlik tekrar <c>ELIGIBLE_FOR_ALL</c> döner, ödül çoğalmaz) ve <see cref="TwitchAutoClaimer"/> aynı kimliği iki kez işlemez.
/// </para>
/// </summary>
public sealed class TwitchClaimFallback : IDisposable
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);

    private readonly string _userId;
    private readonly Action<DropClaimEvent> _enqueue;
    private readonly Func<string, string?> _campaignOfDrop;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _claimEventSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scheduled = new(StringComparer.Ordinal);

    /// <param name="userId">Twitch kullanıcı numarası (kimliğin ilk parçası).</param>
    /// <param name="enqueue">Kurulan <see cref="DropClaimEvent"/>'in teslim edileceği yer (<see cref="TwitchAutoClaimer.Enqueue"/>).</param>
    /// <param name="campaignOfDrop">Drop kimliğinden kampanya kimliği (keşfedilmiş kampanyalardan); bilinmiyorsa <c>null</c>.</param>
    public TwitchClaimFallback(
        string userId,
        Action<DropClaimEvent> enqueue,
        Func<string, string?> campaignOfDrop,
        TimeProvider? timeProvider = null,
        TimeSpan? grace = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(enqueue);
        ArgumentNullException.ThrowIfNull(campaignOfDrop);
        _userId = userId;
        _enqueue = enqueue;
        _campaignOfDrop = campaignOfDrop;
        _time = timeProvider ?? TimeProvider.System;
        Grace = grace ?? DefaultGrace;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Tamamlanma bildiriminden sonra <c>drop-claim</c> olayı için beklenen süre.</summary>
    public TimeSpan Grace { get; }

    public static string BuildDropInstanceId(string userId, string campaignId, string dropId) => $"{userId}#{campaignId}#{dropId}";

    public IDisposable Attach(TwitchPubSubClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.Subscribe(Handle);
    }

    public void Handle(TwitchPubSubEvent pubSubEvent)
    {
        switch (pubSubEvent)
        {
            case DropClaimEvent claim:
                lock (_gate)
                {
                    _claimEventSeen.Add(claim.DropId);
                }

                break;
            case DropProgressEvent { RequiredMinutes: > 0 } progress when progress.CurrentMinutes >= progress.RequiredMinutes:
                Schedule(progress);
                break;
        }
    }

    public void Dispose() => _cts.Cancel();

    private void Schedule(DropProgressEvent progress)
    {
        lock (_gate)
        {
            if (_claimEventSeen.Contains(progress.DropId) || !_scheduled.Add(progress.DropId))
            {
                return;
            }
        }

        _ = Task.Run(() => ClaimAfterGraceAsync(progress), CancellationToken.None);
    }

    private async Task ClaimAfterGraceAsync(DropProgressEvent progress)
    {
        try
        {
            await Task.Delay(Grace, _time, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_claimEventSeen.Contains(progress.DropId))
            {
                return;
            }
        }

        var campaignId = _campaignOfDrop(progress.DropId);
        if (campaignId is null)
        {
            _logger.LogWarning("Drop {Drop} tamamlandı ama kampanyası bilinmiyor; yedek claim denenmedi.", progress.DropId);
            return;
        }

        _logger.LogInformation(
            "Drop {Drop} tamamlandı ama {Seconds:0} sn içinde drop-claim olayı gelmedi; yedek claim kuyruğa alındı.",
            progress.DropId,
            Grace.TotalSeconds);
        _enqueue(new DropClaimEvent(progress.DropId, progress.ChannelId, BuildDropInstanceId(_userId, campaignId, progress.DropId)));
    }
}
