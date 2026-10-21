using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms.Twitch.PubSub;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Twitch;

public enum ClaimOutcome
{
    Claimed,
    AlreadyClaimed,

    /// <summary>Sunucu claim'i kabul etmedi (<see cref="ClaimResult.Status"/> nedenini içerir).</summary>
    Rejected,
}

public sealed record ClaimResult(ClaimOutcome Outcome, string? Status)
{
    public bool IsSuccess => Outcome is ClaimOutcome.Claimed or ClaimOutcome.AlreadyClaimed;
}

public interface IDropClaimer
{
    Task<ClaimResult> ClaimAsync(string dropInstanceId, CancellationToken cancellationToken);
}

/// <summary>
/// <c>DropsPage_ClaimDropRewards</c> mutasyonu. <b>Canlıda sınandı (2026-10-16, SMARTBOX profili):</b> hash geçerli, integrity istemedi,
/// yanıt <c>claimDropRewards { status, isUserAccountConnected, dropType { id, campaign { id, detailsURL } } }</c>; tamamlanmış drop için
/// <c>status = ELIGIBLE_FOR_ALL</c> (aynı kimlik tekrar denenince yine <c>ELIGIBLE_FOR_ALL</c>, ödül çoğalmadı); geçersiz kimlik →
/// <c>claimDropRewards: null</c>. <c>ELIGIBLE_FOR_ALL</c> ve <c>DROP_INSTANCE_ALREADY_CLAIMED</c> (referans projeden) başarı sayılır.
/// </summary>
public sealed class TwitchClaimService : IDropClaimer
{
    private readonly TwitchGqlClient _gql;

    public TwitchClaimService(TwitchGqlClient gql)
    {
        ArgumentNullException.ThrowIfNull(gql);
        _gql = gql;
    }

    public async Task<ClaimResult> ClaimAsync(string dropInstanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dropInstanceId);
        using var result = await _gql.ExecuteAsync(
            GqlOperations.ClaimDrop,
            new Dictionary<string, object?> { ["input"] = new Dictionary<string, object?> { ["dropInstanceID"] = dropInstanceId } },
            cancellationToken).ConfigureAwait(false);

        return Parse(result.Data);
    }

    internal static ClaimResult Parse(System.Text.Json.JsonElement data)
    {
        if (data.ValueKind == System.Text.Json.JsonValueKind.Object
            && data.TryGetProperty("claimDropRewards", out var nullable)
            && nullable.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            // Canlı (2026-10-16): geçersiz/tanınmayan dropInstanceID için sunucu hata değil `claimDropRewards: null` döner.
            // Drop henüz hazır değilse de aynı olabilir → kalıcı veri hatası sayılmaz, otomatik claim'in yeniden denemesine bırakılır.
            return new ClaimResult(ClaimOutcome.Rejected, "NULL_PAYLOAD");
        }

        if (!data.TryGetProp("claimDropRewards", out var claim))
        {
            throw new PlatformDataException(Loc.T("err.twitch.noClaimRewards"));
        }

        var status = claim.GetStringOrNull("status");
        return status switch
        {
            "ELIGIBLE_FOR_ALL" => new ClaimResult(ClaimOutcome.Claimed, status),
            "DROP_INSTANCE_ALREADY_CLAIMED" => new ClaimResult(ClaimOutcome.AlreadyClaimed, status),
            _ => new ClaimResult(ClaimOutcome.Rejected, status ?? Loc.T("err.twitch.noStatus")),
        };
    }
}

/// <summary>Claim geçmişi satırı (<c>history.jsonl</c>).</summary>
public sealed record ClaimRecord(
    DateTimeOffset At,
    string Platform,
    string DropId,
    string DropInstanceId,
    string ChannelId,
    string Outcome,
    string? Status,
    int Attempts);

/// <summary>
/// PubSub <c>drop-claim</c> olayını dinler ve drop'u otomatik claim eder: sunucunun claim'i hemen kabul etmediği durumlara karşı
/// birkaç kez (artan beklemeyle) dener; her sonucu geçmişe yazar; aynı <c>dropInstanceID</c>'yi iki kez claim etmez.
/// </summary>
public sealed class TwitchAutoClaimer
{
    private readonly IDropClaimer _claimer;
    private readonly JsonLinesLog<ClaimRecord> _history;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly Channel<DropClaimEvent> _queue = Channel.CreateUnbounded<DropClaimEvent>();
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);

    public TwitchAutoClaimer(
        IDropClaimer claimer,
        JsonLinesLog<ClaimRecord> history,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        int maxAttempts = 5,
        TimeSpan? baseDelay = null)
    {
        ArgumentNullException.ThrowIfNull(claimer);
        ArgumentNullException.ThrowIfNull(history);
        _claimer = claimer;
        _history = history;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _maxAttempts = Math.Max(1, maxAttempts);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Her denemenin sonunda (başarı ya da kalıcı başarısızlık) tetiklenir.</summary>
    public event Action<ClaimRecord>? Completed;

    /// <summary>PubSub istemcisine bağlanır; yalnızca <see cref="DropClaimEvent"/> işlenir.</summary>
    public IDisposable Attach(TwitchPubSubClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.Subscribe(Enqueue);
    }

    public void Enqueue(TwitchPubSubEvent pubSubEvent)
    {
        if (pubSubEvent is DropClaimEvent claim)
        {
            _queue.Writer.TryWrite(claim);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var claim in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await HandleAsync(claim, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(DropClaimEvent claim, CancellationToken cancellationToken)
    {
        if (_done.Contains(claim.DropInstanceId))
        {
            return;
        }

        ClaimResult? last = null;
        string? error = null;
        var attempts = 0;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            attempts = attempt;
            try
            {
                last = await _claimer.ClaimAsync(claim.DropInstanceId, cancellationToken).ConfigureAwait(false);
                error = null;
                if (last.IsSuccess)
                {
                    _done.Add(claim.DropInstanceId);
                    break;
                }
            }
            catch (Exception ex) when (Retry.IsTransient(ex))
            {
                error = ex.Message;
            }
            catch (Exception ex) when (ex is PlatformDataException or TwitchUnauthorizedException)
            {
                // Hash kayması / integrity / yetki: tekrar denemek çözmez, kalıcı başarısızlık say.
                error = $"{ex.GetType().Name}: {ex.Message}";
                break;
            }

            if (attempt < _maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, _baseDelay.TotalSeconds * Math.Pow(2, attempt - 1))), _time, cancellationToken).ConfigureAwait(false);
            }
        }

        var success = last?.IsSuccess == true;
        var record = new ClaimRecord(
            _time.GetUtcNow(),
            "twitch",
            claim.DropId,
            claim.DropInstanceId,
            claim.ChannelId,
            success ? last!.Outcome.ToString() : "Failed",
            success ? last!.Status : error ?? last?.Status,
            attempts);
        _history.Append(record);
        if (success)
        {
            _logger.LogInformation("Drop claim edildi ({Drop}, {Outcome}, {Attempts} deneme).", claim.DropId, record.Outcome, attempts);
        }
        else
        {
            _logger.LogError("Drop claim EDİLEMEDİ ({Drop}): {Reason}", claim.DropId, record.Status);
        }

        Completed?.Invoke(record);
    }
}
