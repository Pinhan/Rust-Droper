using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;
using RustDroper.Core.Platforms;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Orchestration;

public sealed record EngineOptions
{
    public required string GameSlug { get; init; }

    /// <summary>Sunucu ilerlemesinin okunma aralığı (ilerleme dakikada bir artar).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Kullanıcının yayıncı drop'ları öncelik sırası (kampanya kimlikleri). Her planlamada çağrılır: sıra değişince izleme sırasında bir sonraki yoklamada
    /// (≤60 sn) yeniden değerlendirilir. <c>null</c> = sıra yok.
    /// </summary>
    public Func<IReadOnlyList<string>>? StreamerPriority { get; init; }

    public TimeSpan RediscoverInterval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan IdleRecheck { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>İlk ilerleme gecikmesine tolerans (gözlem: ilk dakika ~1,5 dk sonra sayılıyor).</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>Bu süre ilerleme olmazsa kanal takılmış sayılır.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Drop tamamlandıktan sonra claim + sonraki drop'un başlaması için ek tolerans.</summary>
    public TimeSpan ClaimWait { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan ChannelBlock { get; init; } = TimeSpan.FromMinutes(20);

    public TimeSpan OfflineBlock { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan CampaignCooldown { get; init; } = TimeSpan.FromMinutes(60);

    public TimeSpan AuthRetry { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Tamamlandı işaretinin yeniden başlatmalar arasında saklanma süresi (kampanya bitişinden önce biter). Yanlış "tamamlandı"
    /// (ör. geçici bir sunucu aksaklığı) kullanıcıyı kampanya boyunca dışarıda bırakmasın diye sınırlıdır.
    /// </summary>
    public TimeSpan CompletedRetention { get; init; } = TimeSpan.FromHours(12);

    /// <summary>İlerleme gördükten sonra art arda bu kadar "oturum yok" yoklaması = kampanya tamamlandı.</summary>
    public int CompletionNullPolls { get; init; } = 3;

    /// <summary>Aynı kampanyada bu kadar kanal takılırsa kampanya bekletmeye alınır (ör. hesap bağlı değil).</summary>
    public int StallsBeforeCooldown { get; init; } = 2;

    public int MaxConsecutivePollFailures { get; init; } = 5;
}

public enum EngineState
{
    Starting,
    Discovering,
    Idle,
    Watching,

    /// <summary>Drop tamamlandı; claim ve sonraki drop bekleniyor.</summary>
    ClaimPending,

    /// <summary>Platform geçici/yapısal sorun yaşıyor (ağ, şema/hash); motor yeniden dener.</summary>
    Degraded,

    /// <summary>Kimlik geçersiz; kullanıcının yeniden giriş yapması gerekiyor.</summary>
    AuthRequired,

    Stopped,
}

public sealed record EngineEvent(DateTimeOffset At, string Level, string Text);

public sealed record EngineSnapshot(
    EngineState State,
    string Message,
    Target? Target,
    DropSession? Session,
    DateTimeOffset? LastProgressAt,
    int ClaimedThisRun,
    IReadOnlyList<EngineEvent> Recent);

/// <summary>
/// Başsız motor (tek platform): keşfet → planla → izle → sunucu ilerlemesini yokla → watchdog → claim/tamamlanma → sonraki hedef.
/// Hiçbir platform hatasında çökmez; her durumu <see cref="Snapshot"/> ile dışarı bildirir (UI/tepsi/CLI bunu gösterir).
/// </summary>
public sealed class DropEngine
{
    private const int MaxRecentEvents = 50;

    private readonly IDropPlatform _platform;
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly IEngineStateStore? _stateStore;
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _completedUntil = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimedDropIds;
    private readonly Dictionary<string, DateTimeOffset> _campaignCooldown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _channelBlocked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _campaignStalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reclaimed = new(StringComparer.Ordinal); // kanal → yeniden başlatma anındaki dakika
    private readonly List<EngineEvent> _events = [];
    private TaskCompletionSource<string>? _offline;
    private EngineSnapshot _snapshot;
    private int _claimedThisRun;

    public DropEngine(
        IDropPlatform platform,
        EngineOptions options,
        IEnumerable<string>? alreadyClaimedDropIds = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        IEngineStateStore? stateStore = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(options);
        _platform = platform;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _stateStore = stateStore;
        _claimedDropIds = new HashSet<string>(alreadyClaimedDropIds ?? [], StringComparer.Ordinal);
        _snapshot = new EngineSnapshot(EngineState.Starting, Loc.T("engine.starting"), null, null, null, 0, []);
        RestoreMemory();
    }

    public EngineSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<EngineSnapshot>? Changed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _platform.ChannelWentOffline += OnChannelOffline;
        _platform.DropClaimed += OnDropClaimed;
        var background = RunBackgroundSafeAsync(cancellationToken);
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PlatformSnapshot snapshot;
                try
                {
                    Publish(EngineState.Discovering, Loc.T("engine.discovering"), null, null);
                    snapshot = await _platform.DiscoverAsync(_options.GameSlug, cancellationToken).ConfigureAwait(false);
                    failures = 0;
                }
                catch (PlatformAuthException ex)
                {
                    Note("error", Loc.T("engine.authInvalid", ex.Message));
                    Publish(EngineState.AuthRequired, Loc.T("engine.authRequired"), null, null);
                    await DelayAsync(_options.AuthRetry, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    failures++;
                    var wait = Backoff(failures);
                    Note("warn", Loc.T("engine.discoveryFailed", ex.GetType().Name, ex.Message));
                    Publish(EngineState.Degraded, Loc.T("engine.degraded", wait.TotalSeconds), null, null);
                    await DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var plan = PlanFor(snapshot);
                if (plan.Count == 0)
                {
                    Publish(EngineState.Idle, Loc.T("engine.idle", _options.IdleRecheck.TotalMinutes), null, null);
                    await DelayAsync(_options.IdleRecheck, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WatchAsync(plan[0], cancellationToken).ConfigureAwait(false);
                await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _platform.ChannelWentOffline -= OnChannelOffline;
            _platform.DropClaimed -= OnDropClaimed;
            Publish(EngineState.Stopped, "Durduruldu", null, null);
            await background.ConfigureAwait(false);
        }
    }

    private async Task WatchAsync(Target target, CancellationToken ct)
    {
        var channel = target.Channel;
        var campaign = target.Campaign;
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var offline = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _offline = offline;
        var watcher = _platform.CreateWatcher();
        _platform.OnTargetChanged(channel);
        Note("info", Loc.T("engine.watching", channel.DisplayName, campaign.Name));

        var startedAt = Now;
        var lastProgressAt = startedAt;
        var lastMinutes = -1;
        string? lastDropId = null;
        var hadSession = false;
        var nullPolls = 0;
        var pollFailures = 0;
        DateTimeOffset? completeSince = null;
        string? completeDropId = null;
        var maxRequired = campaign.Rewards.Count == 0 ? int.MaxValue : campaign.Rewards.Max(r => r.RequiredMinutes);
        DropSession? session = null;
        var rediscoverAt = startedAt + _options.RediscoverInterval;
        var prioritySignature = PrioritySignature();
        var lastPollAt = startedAt;
        var watchTask = watcher.RunAsync(channel, watchCts.Token);
        Publish(EngineState.Watching, Loc.T("engine.watchingState", channel.DisplayName), target, null);

        try
        {
            while (true)
            {
                await Task.WhenAny(Task.Delay(_options.PollInterval, _time, ct), watchTask, offline.Task).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (watchTask.IsCompleted)
                {
                    var result = await watchTask.ConfigureAwait(false);
                    var block = result.End == WatchEnd.Offline ? _options.OfflineBlock : _options.ChannelBlock;
                    Block(channel.Id, block);
                    Note("warn", Loc.T("engine.watcherEnded", channel.DisplayName, result.End, result.Reason ?? "-"));
                    return;
                }

                if (offline.Task.IsCompleted)
                {
                    Block(channel.Id, _options.OfflineBlock);
                    Note("warn", Loc.T("engine.channelOffline", channel.DisplayName));
                    return;
                }

                var pollOk = true;
                try
                {
                    session = await _platform.GetSessionAsync(channel, ct).ConfigureAwait(false);
                    pollFailures = 0;
                }
                catch (PlatformAuthException ex)
                {
                    Note("error", Loc.T("engine.authInvalid", ex.Message));
                    Publish(EngineState.AuthRequired, Loc.T("engine.authRequired"), target, null);
                    return;
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    pollOk = false;
                    Note("warn", Loc.T("engine.progressUnread", ex.Message));
                    if (++pollFailures >= _options.MaxConsecutivePollFailures)
                    {
                        Note("error", Loc.T("engine.progressLost"));
                        return;
                    }
                }

                var now = Now;
                if (!pollOk)
                {
                    // İlerlemeyi gözleyemediğimiz süre (ağ kesintisi) takılma sayılmaz: yoksa kesinti sonrası kanal/kampanya haksız yere engellenir.
                    lastProgressAt += now - lastPollAt;
                }

                lastPollAt = now;
                if (pollOk)
                {
                    if (session is not null)
                    {
                        hadSession = true;
                        nullPolls = 0;
                        if (session.DropId != lastDropId || session.CurrentMinutes != lastMinutes)
                        {
                            lastProgressAt = now;
                            _campaignStalls[campaign.Id] = 0;
                            if (_reclaimed.TryGetValue(channel.Id, out var reclaimedAt) && session.CurrentMinutes > reclaimedAt)
                            {
                                _reclaimed.Remove(channel.Id); // dakika gerçekten arttı: bir sonraki kesinti için yeniden başlatma hakkı yenilenir
                            }
                        }

                        lastDropId = session.DropId;
                        lastMinutes = session.CurrentMinutes;

                        // Otomatik verilen (reward) ödüllerde sunucu oturumu eşikten sonra da saymaya devam eder ve hiç boşalmaz (canlı soak, 2026-10-17:
                        // 4 sa 9 dk / 4 sa). Kampanyanın SON drop'u tamamlanmış ve claim bekleme süresi geçmişse kampanya bitmiştir → sıradakine geç.
                        // Son olmayan drop'larda (ör. Rust 1/2/3/4/5 sa) erken bitirme yapılmaz: sıradaki drop claim ile açılır.
                        if (session.IsComplete)
                        {
                            if (completeSince is null || completeDropId != session.DropId)
                            {
                                completeSince = now;
                                completeDropId = session.DropId;
                            }

                            if (now - completeSince >= _options.ClaimWait && session.RequiredMinutes >= maxRequired)
                            {
                                MarkCompleted(campaign);
                                Note("ok", Loc.T("engine.campaignDoneThreshold", campaign.Name));
                                return;
                            }
                        }
                        else
                        {
                            completeSince = null;
                            completeDropId = null;
                        }

                        Publish(
                            session.IsComplete ? EngineState.ClaimPending : EngineState.Watching,
                            session.IsComplete ? Loc.T("engine.dropCompleted") : Loc.T("engine.progress", channel.DisplayName, session.CurrentMinutes, session.RequiredMinutes),
                            target,
                            session,
                            lastProgressAt);
                    }
                    else if (hadSession && ++nullPolls >= _options.CompletionNullPolls)
                    {
                        MarkCompleted(campaign);
                        Note("ok", Loc.T("engine.campaignDone", campaign.Name));
                        return;
                    }
                }

                var allowed = hadSession ? _options.StallTimeout : _options.Grace + _options.StallTimeout;
                if (session?.IsComplete == true)
                {
                    allowed += _options.ClaimWait;
                }

                if (now - lastProgressAt > allowed)
                {
                    HandleStall(campaign, channel, now, hadSession, lastMinutes);
                    return;
                }

                if (PrioritySignature() is var signature && signature != prioritySignature)
                {
                    prioritySignature = signature;
                    rediscoverAt = now; // sıra değişti: bir sonraki yoklamada hemen yeniden değerlendir (kesintisiz izleme bozulmaz; ShouldKeep karar verir)
                }

                if (now >= rediscoverAt)
                {
                    rediscoverAt = now + _options.RediscoverInterval;
                    if (!await ShouldKeepAsync(campaign, session, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            await watchCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException || IsRecoverable(ex))
            {
            }

            _platform.OnTargetChanged(null);
            _offline = null;
        }
    }

    private void HandleStall(Domain.Campaign campaign, Domain.LiveChannel channel, DateTimeOffset now, bool hadProgress, int lastMinutes)
    {
        // İlerleme akarken durduysa (kanal drop'suz değil, daha önce ilerlemişti) olası neden hesabın başka bir yerden (tarayıcı/başka cihaz)
        // izleme yapması: sunucudaki geçerli oturum o yayına kaymıştır. Kanalı hemen engellemek yerine AYNI kanalda izlemeyi bir kez yeniden
        // başlat (oturum bize geri alınır); yeniden de ilerlemezse kanal normal yoldan engellenir.
        if (hadProgress && _reclaimed.TryAdd(channel.Id, lastMinutes))
        {
            Note("warn", Loc.T("engine.foreignSession", channel.DisplayName));
            return;
        }

        Block(channel.Id, _options.ChannelBlock);
        var stalls = _campaignStalls.GetValueOrDefault(campaign.Id) + 1;
        _campaignStalls[campaign.Id] = stalls;
        Note("warn", Loc.T("engine.channelBlocked", channel.DisplayName, _options.ChannelBlock.TotalMinutes));
        if (stalls >= _options.StallsBeforeCooldown)
        {
            _campaignStalls[campaign.Id] = 0;
            _campaignCooldown[campaign.Id] = now + _options.CampaignCooldown;
            Note("error", Loc.T("engine.campaignCooldown", campaign.Name, _options.CampaignCooldown.TotalMinutes));
            PersistMemory();
        }
    }

    private void MarkCompleted(Domain.Campaign campaign)
    {
        _completed.Add(campaign.Id);
        var until = Now + _options.CompletedRetention;
        _completedUntil[campaign.Id] = campaign.EndsAt is { } end && end < until ? end : until;
        PersistMemory();
    }

    /// <summary>Önceki çalışmadan kalan tamamlanma/bekletme kayıtlarını (süresi geçmemiş olanları) yükler.</summary>
    private void RestoreMemory()
    {
        if (_stateStore is null)
        {
            return;
        }

        try
        {
            var memory = _stateStore.Load();
            var now = Now;
            foreach (var (id, until) in memory.CompletedUntil ?? [])
            {
                if (until > now)
                {
                    _completed.Add(id);
                    _completedUntil[id] = until;
                }
            }

            foreach (var (id, until) in memory.CooldownUntil ?? [])
            {
                if (until > now)
                {
                    _campaignCooldown[id] = until;
                }
            }

            if (_completed.Count + _campaignCooldown.Count > 0)
            {
                Note("info", Loc.T("engine.remembered", _completed.Count, _campaignCooldown.Count));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Motor hafızası yüklenemedi; boş başlanıyor");
        }
    }

    private void PersistMemory()
    {
        if (_stateStore is null)
        {
            return;
        }

        try
        {
            var now = Now;
            _stateStore.Save(new EngineMemory
            {
                CompletedUntil = _completedUntil.Where(p => p.Value > now).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                CooldownUntil = _campaignCooldown.Where(p => p.Value > now).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Hafıza yazılamıyorsa motor yine de çalışır (yalnız yeniden başlatmada bu bilgi kaybolur).
            _logger.LogWarning(ex, "Motor hafızası kaydedilemedi");
        }
    }

    private async Task<bool> ShouldKeepAsync(Domain.Campaign current, DropSession? session, CancellationToken ct)
    {
        try
        {
            var snapshot = await _platform.DiscoverAsync(_options.GameSlug, ct).ConfigureAwait(false);
            var top = PlanFor(snapshot).FirstOrDefault();
            if (top is null)
            {
                return false;
            }

            if (top.Campaign.Id == current.Id)
            {
                return true;
            }

            // Geçiş yalnızca (1) genel kampanyadayken bir yayıncı kampanyası için ya da (2) yayıncı kampanyasındayken kullanıcının sırasında DAHA ÖNCELİKLİ bir
            // yayıncı kampanyası için, ve mevcut drop bitmeye yakın değilse (%90) yapılır; aksi halde mevcut izleme kesintisiz sürer (gereksiz kanal oynaması yok).
            if (top.Campaign.Scope == Domain.CampaignScope.Streamer && (session?.Percent ?? 0) < 90)
            {
                var priority = _options.StreamerPriority?.Invoke() ?? [];
                var better = current.Scope == Domain.CampaignScope.General
                    || DropPlanner.RankOf(priority, top.Campaign.Id) < DropPlanner.RankOf(priority, current.Id);
                if (better)
                {
                    Note("info", Loc.T("engine.priorityFound", top.Campaign.Name));
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return true; // Kontrol başarısız: mevcut izlemeyi bozma.
        }
    }

    private IReadOnlyList<Target> PlanFor(PlatformSnapshot snapshot)
    {
        HashSet<string> claimed;
        lock (_gate)
        {
            claimed = [.. _claimedDropIds];
        }

        return DropPlanner.Plan(new PlannerInput(snapshot, Now, _completed, _campaignCooldown, _channelBlocked, claimed, _options.StreamerPriority?.Invoke()));
    }

    private string PrioritySignature() => string.Join('|', _options.StreamerPriority?.Invoke() ?? []);

    private async Task RunBackgroundSafeAsync(CancellationToken ct)
    {
        try
        {
            await _platform.RunBackgroundAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Arka plan işi (PubSub/claim) çökerse motor çökmez; durum olarak raporlanır.
            _logger.LogError(ex, "Platform arka plan işi sonlandı");
            Note("error", Loc.T("engine.background", ex.Message));
        }
    }

    private void OnChannelOffline(string channelId)
    {
        var current = Snapshot.Target?.Channel.Id;
        if (current == channelId)
        {
            _offline?.TrySetResult(channelId);
        }
    }

    private void OnDropClaimed(ClaimNotice notice)
    {
        if (notice.Success)
        {
            lock (_gate)
            {
                _claimedDropIds.Add(notice.DropId);
            }

            Interlocked.Increment(ref _claimedThisRun);
            Note("ok", Loc.T("engine.claimed", notice.Detail ?? notice.DropId));
        }
        else
        {
            Note("error", Loc.T("engine.claimFailed", notice.Detail ?? notice.DropId, PlatformProfile.For(_platform.Platform).DisplayName));
        }

        Publish(Snapshot.State, Snapshot.Message, Snapshot.Target, Snapshot.Session, Snapshot.LastProgressAt);
    }

    private void Block(string channelId, TimeSpan duration) => _channelBlocked[channelId] = Now + duration;

    private static bool IsRecoverable(Exception ex) => Retry.IsTransient(ex) || ex is PlatformDataException;

    private TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(_options.MaxBackoff.TotalSeconds, 30 * Math.Pow(2, failures - 1)));

    private DateTimeOffset Now => _time.GetUtcNow();

    private Task DelayAsync(TimeSpan span, CancellationToken ct) => Task.Delay(span, _time, ct);

    private void Note(string level, string text)
    {
        lock (_gate)
        {
            _events.Add(new EngineEvent(Now, level, text));
            if (_events.Count > MaxRecentEvents)
            {
                _events.RemoveAt(0);
            }
        }

        _logger.Log(level switch { "error" => LogLevel.Error, "warn" => LogLevel.Warning, _ => LogLevel.Information }, "{Message}", text);
    }

    private void Publish(EngineState state, string message, Target? target, DropSession? session, DateTimeOffset? lastProgressAt = null)
    {
        EngineSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new EngineSnapshot(
                state,
                message,
                target,
                session,
                lastProgressAt,
                Volatile.Read(ref _claimedThisRun),
                [.. _events.TakeLast(30)]);
            _snapshot = snapshot;
        }

        Changed?.Invoke(snapshot);
    }
}
