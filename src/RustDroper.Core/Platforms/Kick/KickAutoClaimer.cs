using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// Kick otomatik claim: periyodik olarak hesap ilerlemesini okur; <b>yalnızca izin verilen (Rust) kampanyalarda</b> süresi dolmuş ve
/// henüz claim edilmemiş ödülleri claim eder. Her ödül için sınırlı sayıda deneme yapılır (reddedilen/başarısız ödül uzun aralıkla
/// yeniden denenir, sonsuz döngü yok); sonuçlar <c>history.jsonl</c>'e yazılır. Bu sınıf yalnızca otomatik claim AÇIKKEN kurulur.
/// </summary>
public sealed class KickAutoClaimer
{
    private readonly KickApiClient _api;
    private readonly JsonLinesLog<ClaimRecord> _history;
    private readonly Func<IReadOnlySet<string>> _allowedCampaignIds;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _retryInterval;
    private readonly int _maxAttempts;
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    public KickAutoClaimer(
        KickApiClient api,
        JsonLinesLog<ClaimRecord> history,
        Func<IReadOnlySet<string>> allowedCampaignIds,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        TimeSpan? pollInterval = null,
        TimeSpan? retryInterval = null,
        int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(allowedCampaignIds);
        _api = api;
        _history = history;
        _allowedCampaignIds = allowedCampaignIds;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(45);
        _retryInterval = retryInterval ?? TimeSpan.FromMinutes(10);
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    /// <summary>Her ödülün sonucunda (başarı ya da başarısızlık) tetiklenir.</summary>
    public event Action<ClaimRecord>? Completed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_pollInterval, _time, cancellationToken).ConfigureAwait(false);
                await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Tek tur: claim edilebilir ödülleri bulup claim eder. Hatalar yutulur (günlüğe yazılır), iptal hariç.</summary>
    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var allowed = _allowedCampaignIds();
            if (allowed.Count == 0)
            {
                return;
            }

            var progress = await _api.GetProgressAsync(cancellationToken).ConfigureAwait(false);
            foreach (var campaign in progress.Items.Where(c => allowed.Contains(c.CampaignId)))
            {
                foreach (var reward in campaign.Rewards.Where(r => r.IsComplete && !r.Claimed))
                {
                    await ClaimAsync(campaign.CampaignId, reward.RewardId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (ex is PlatformAuthException or PlatformDataException or PlatformHttpException or HttpRequestException))
        {
            _logger.LogWarning("Kick otomatik claim turu başarısız: {Reason}", ex.Message);
        }
    }

    private async Task ClaimAsync(string campaignId, string rewardId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var state = _attempts.GetValueOrDefault(rewardId);
        if (state.Done || state.Count >= _maxAttempts || now < state.NextAt)
        {
            return;
        }

        string outcome;
        string? detail;
        try
        {
            var result = await _api.ClaimAsync(rewardId, campaignId, ct).ConfigureAwait(false);
            outcome = result.Success ? "Claimed" : "Rejected";
            detail = result.Detail;
        }
        catch (Exception ex) when (ex is PlatformAuthException or PlatformHttpException or HttpRequestException)
        {
            outcome = "Failed";
            detail = $"{ex.GetType().Name}: {ex.Message}";
        }

        var count = state.Count + 1;
        _attempts[rewardId] = new Attempt(outcome == "Claimed", count, now + _retryInterval);
        var record = new ClaimRecord(now, "Kick", rewardId, campaignId, string.Empty, outcome, detail, count);
        try
        {
            _history.Append(record);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Claim geçmişi yazılamadı: {Reason}", ex.Message);
        }

        _logger.LogInformation("Kick claim: ödül {Reward} → {Outcome}", rewardId, outcome);
        Completed?.Invoke(record);
    }

    private readonly record struct Attempt(bool Done, int Count, DateTimeOffset NextAt);
}
