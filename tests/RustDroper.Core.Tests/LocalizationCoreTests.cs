using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Domain;
using RustDroper.Core.Localization;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Tests;

/// <summary>Core katmanı metinlerinin İngilizce karşılıkları (dil yalnız testin akışı için <see cref="Loc.Use"/> ile seçilir).</summary>
public class LocalizationCoreTests
{
    [Fact]
    public void Notifications_AreEnglishAndTurkish()
    {
        var turkish = UserNotification.DropClaimed("d1", "Rug");
        Assert.Equal("Drop claim edildi", turkish.Title);
        Assert.Equal("Rug hesabınıza eklendi.", turkish.Text);

        using var _ = Loc.Use("en");
        var claimed = UserNotification.DropClaimed("d1", "Rug");
        Assert.Equal("Drop claimed", claimed.Title);
        Assert.Equal("Rug was added to your account.", claimed.Text);

        var failed = UserNotification.ClaimFailed("d1", "Rug");
        Assert.Equal("Couldn't claim drop", failed.Title);
        Assert.Equal("Rug: claim it manually from your Twitch inventory (click to open).", failed.Text);
        Assert.Equal("Rug: claim it manually from the Kick rewards page (click to open).", UserNotification.ClaimFailed("d1", "Rug", PlatformProfile.Kick).Text);

        var ready = UserNotification.ClaimReady("d1", "Rug", PlatformProfile.Kick);
        Assert.Equal("Drop ready to claim", ready.Title);
        Assert.Equal("Rug is complete. Auto-claim is off: claim it from the Kick rewards page (click to open).", ready.Text);

        Assert.Equal("Campaign started", UserNotification.CampaignStarted("c", "Base Invaders", "streamer").Title);
        Assert.Equal("Base Invaders — watching streamer.", UserNotification.CampaignStarted("c", "Base Invaders", "streamer").Text);
        Assert.Equal("Your Kick session is no longer valid; sign in again from Rust Droper to keep collecting drops.", UserNotification.LoginRequired(PlatformProfile.Kick).Text);
        Assert.Equal("Internet connection lost", UserNotification.ConnectionLost().Title);
        Assert.Equal("Drop collection will resume automatically when the connection returns.", UserNotification.ConnectionLost().Text);
        Assert.Equal("It will reconnect automatically when the connection returns.", UserNotification.ConnectionLost(collecting: false).Text);
        Assert.Equal("The 2 min 10 s outage is over; drop collection is resuming where it left off.", UserNotification.ConnectionRestored(TimeSpan.FromSeconds(130)).Text);
        Assert.Equal("The 45 s outage is over.", UserNotification.ConnectionRestored(TimeSpan.FromSeconds(45), collecting: false).Text);
        Assert.Equal("1 h 5 min", UserNotification.FormatDuration(TimeSpan.FromMinutes(65)));
        Assert.StartsWith("Notifications are working", UserNotification.Test().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TurkishNotifications_AreUnchanged()
    {
        Assert.Equal("Rug: Twitch envanterinden elle claim edin (tıklayınca açılır).", UserNotification.ClaimFailed("d1", "Rug").Text);
        Assert.Equal("2 dk 10 sn", UserNotification.FormatDuration(TimeSpan.FromSeconds(130)));
        Assert.Equal("Kick oturumu geçersiz; drop toplamaya devam etmek için Rust Droper'dan yeniden giriş yapın.", UserNotification.LoginRequired(PlatformProfile.Kick).Text);
    }

    [Fact]
    public void PlatformErrors_AreLocalized()
    {
        Assert.Equal("Kick oturumu yok (token boş).", Loc.T("err.kick.noToken"));
        using var _ = Loc.Use("en");
        Assert.Equal("There is no Kick session (the token is empty).", Loc.T("err.kick.noToken"));
        Assert.Equal("Twitch rejected 'Inventory' for failing the integrity check.", Loc.T("err.twitch.gqlIntegrity", "Inventory"));
        Assert.Equal("Kick account", RustDroper.Core.Platforms.Kick.KickSessionManager.UnknownLogin);
    }

    [Fact]
    public async Task Engine_Messages_AreEnglish_WhenTheFlowIsEnglish()
    {
        using var _ = Loc.Use("en");
        var platform = new FakePlatform();
        var clock = new FakeTimeProvider(Fx.Start);
        var engine = new DropEngine(platform, new EngineOptions { GameSlug = "rust" }, timeProvider: clock);
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(cts.Token);

        for (var i = 0; i < 3000 && engine.Snapshot.State != EngineState.Idle; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(2);
        }

        Assert.Equal(EngineState.Idle, engine.Snapshot.State);
        Assert.Equal("No suitable campaign; will check again in 5 min", engine.Snapshot.Message);

        platform.RaiseClaim(new ClaimNotice("d1", false, "Rug"));
        Assert.Contains(engine.Snapshot.Recent, e => e.Text == "Drop could NOT be claimed: Rug. You can claim it manually from your Twitch inventory.");
        platform.RaiseClaim(new ClaimNotice("d2", true, "Bed"));
        Assert.Contains(engine.Snapshot.Recent, e => e.Text == "Drop claimed (Bed)");

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
