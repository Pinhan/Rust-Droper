using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;

namespace RustDroper.Core.Tests;

/// <summary>
/// Canlı soak bulgusu (2026-10-17): otomatik verilen ödüllerde sunucu oturumu eşikten sonra da sayar ve hiç boşalmaz;
/// motor "oturum yok = tamamlandı" kuralına takılıp sonsuza dek "Claim bekleniyor"da kalıyordu.
/// </summary>
public class EngineCompletionTests
{
    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition, int maxSteps = 3000, int stepSeconds = 10)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(stepSeconds));
            await Task.Delay(2);
        }

        Assert.True(condition(), "Beklenen durum (sahte saatle) oluşmadı.");
    }

    /// <summary>İlk görüldüğünden itibaren dakikada +1 sayan, <b>hiç boşalmayan</b> oturum (eşikten sonra da artar).</summary>
    private static Func<LiveChannel, DropSession?> NeverEnding(FakeTimeProvider clock, string dropId, int required)
    {
        var firstSeen = new ConcurrentDictionary<string, DateTimeOffset>();
        return channel =>
        {
            var seen = firstSeen.GetOrAdd(channel.Id, _ => clock.GetUtcNow());
            var minutes = (int)(clock.GetUtcNow() - seen).TotalMinutes - 1;
            return minutes < 1 ? null : new DropSession(dropId, minutes, required);
        };
    }

    [Fact]
    public async Task SessionCountingPastTheLastThreshold_CompletesCampaign_AndMovesToTheNext()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var platform = new FakePlatform();
        var alpha = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 5));
        var beta = Fx.Camp("B", "Beta", CampaignScope.General, Fx.Start.AddDays(2), ("dB", 600));
        platform.Snapshot = Fx.Snap((alpha, [Fx.Ch("1", "cha")]), (beta, [Fx.Ch("2", "chb")]));
        var never = NeverEnding(clock, "x", 5);
        platform.Session = ch => ch.Id == "1" ? never(ch) is { } s ? s with { DropId = "dA" } : null : new DropSession("dB", 1, 600);
        var engine = new DropEngine(platform, new EngineOptions { GameSlug = "rust" }, timeProvider: clock);
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(cts.Token);

        await AdvanceUntilAsync(clock, () => platform.Watched.Contains("chb"));

        Assert.Contains(engine.Snapshot.Recent, e => e.Text.Contains("son drop eşiği aşıldı", StringComparison.Ordinal));
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletedDropThatIsNotTheLastOne_KeepsWatching_NoEarlyCompletion()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var platform = new FakePlatform();
        var alpha = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("d1", 5), ("d2", 120));
        var beta = Fx.Camp("B", "Beta", CampaignScope.General, Fx.Start.AddDays(2), ("dB", 600));
        platform.Snapshot = Fx.Snap((alpha, [Fx.Ch("1", "cha")]), (beta, [Fx.Ch("2", "chb")]));
        var never = NeverEnding(clock, "d1", 5);
        platform.Session = ch => ch.Id == "1" ? never(ch) : new DropSession("dB", 1, 600);
        var engine = new DropEngine(platform, new EngineOptions { GameSlug = "rust" }, timeProvider: clock);
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(cts.Token);

        // İlk drop (5 dk) tamamlanır; claim bekleme süresinin (3 dk) çok ötesine geç.
        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.ClaimPending);
        for (var i = 0; i < 90; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(2);
        }

        Assert.DoesNotContain("chb", platform.Watched);
        Assert.DoesNotContain(engine.Snapshot.Recent, e => e.Text.Contains("Kampanya tamamlandı", StringComparison.Ordinal));
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
