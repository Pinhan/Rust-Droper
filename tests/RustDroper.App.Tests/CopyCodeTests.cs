using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

/// <summary>Giriş kodu panelindeki "Kopyala": panoya yalnız kodu yazar, kısa "Kopyalandı ✓" geri bildirimi verir.</summary>
public class CopyCodeTests
{
    private static readonly Uri Activate = new("https://www.twitch.tv/activate");
    private static readonly Uri KickLink = new("https://kick.com/tv/login?uuid=A1B2C3D4-0000-4000-8000-000000000001&code=123456");

    private static RelayCommand Noop() => new(_ => { });

    private static PlatformCardViewModel TwitchCard(List<string> clipboard, FakeTimeProvider? time = null) =>
        new("Twitch", Noop(), Noop(), Noop(), Noop(), clipboard: clipboard.Add, time: time);

    private static AccountSnapshot LoggingIn(string userCode) => new(
        AccountStatus.LoggingIn, null, new TwitchDeviceCode("dev", userCode, Activate, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4)));

    [Fact]
    public void CopyCode_PutsExactlyTheCodeOnTheClipboard()
    {
        var clipboard = new List<string>();
        var card = TwitchCard(clipboard);
        card.Update(LoggingIn("SHJVHZVG"));

        card.CopyCodeCommand!.Execute(null);

        Assert.Equal(["SHJVHZVG"], clipboard); // bağlantı/ek metin yok
    }

    [Fact]
    public void CopyCode_WithoutCode_WritesNothing()
    {
        var clipboard = new List<string>();
        var card = TwitchCard(clipboard);

        card.CopyCodeCommand!.Execute(null);

        Assert.Empty(clipboard);
        Assert.False(card.CodeCopied);
    }

    [Fact]
    public void CopyCode_ShowsCopiedFeedback_ThenClearsAfterTwoSeconds()
    {
        var time = new FakeTimeProvider();
        var card = TwitchCard([], time);
        card.Update(LoggingIn("ABCD-EFGH"));

        card.CopyCodeCommand!.Execute(null);
        Assert.True(card.CodeCopied);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(card.CodeCopied);
        SpinWait.SpinUntil(() => { time.Advance(TimeSpan.FromSeconds(1)); return !card.CodeCopied; }, 5000);
        Assert.False(card.CodeCopied);
    }

    [Fact]
    public void SecondCopy_RestartsTheFeedbackWindow()
    {
        var time = new FakeTimeProvider();
        var card = TwitchCard([], time);
        card.Update(LoggingIn("ABCD-EFGH"));
        card.CopyCodeCommand!.Execute(null);
        time.Advance(TimeSpan.FromMilliseconds(1500));

        card.CopyCodeCommand.Execute(null); // eski zamanlayıcı yeni geri bildirimi silmemeli
        time.Advance(TimeSpan.FromMilliseconds(1000));
        Thread.Sleep(50);

        Assert.True(card.CodeCopied);
    }

    [Fact]
    public void FeedbackClears_WhenLoginEnds()
    {
        var card = TwitchCard([]);
        card.Update(LoggingIn("ABCD-EFGH"));
        card.CopyCodeCommand!.Execute(null);

        card.Update(new AccountSnapshot(AccountStatus.LoggedIn, "fan"));

        Assert.False(card.CodeCopied);
        Assert.Null(card.DeviceCode);
    }

    [Fact]
    public void KickCard_CopyLink_CopiesLinkWithFeedback_AndNoCodeCommandNeeded()
    {
        var clipboard = new List<string>();
        var card = new PlatformCardViewModel("Kick", Noop(), Noop(), Noop(), Noop(), Platform.Kick, clipboard.Add);

        card.CopyLinkCommand!.Execute(KickLink);

        Assert.Equal([KickLink.ToString()], clipboard);
        Assert.True(card.LinkCopied);
        Assert.False(card.CodeCopied);
    }

    [Fact]
    public void WithoutClipboard_NoCopyCommands()
    {
        var card = new PlatformCardViewModel("Twitch", Noop(), Noop(), Noop(), Noop());

        Assert.Null(card.CopyCodeCommand);
        Assert.Null(card.CopyLinkCommand);
    }

    [Fact]
    public async Task MainViewModel_TwitchLogin_CopyCodeGivesTheDisplayedCode()
    {
        using var dir = new TempDir();
        var settings = new RustDroper.Core.Configuration.SettingsService(dir.File("s.json"));
        var account = new StubAccount();
        var gate = new TaskCompletionSource<TwitchSession>();
        account.Login = (onCode, _) =>
        {
            onCode(new TwitchDeviceCode("dev", "SHJVHZVG", Activate, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4)));
            return gate.Task;
        };
        using var service = new DropperService(account, new StubFactory(new StubPlatform()), settings);
        var clipboard = new List<string>();
        var vm = new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), service, new ImmediateDispatcher(), clipboard: clipboard.Add);

        await vm.InitializeAsync();
        vm.Twitch.ActionCommand!.Execute(null);
        var started = Environment.TickCount64;
        while (vm.Twitch.DeviceCode is null && Environment.TickCount64 - started < 10000)
        {
            await Task.Delay(10);
        }

        vm.Twitch.CopyCodeCommand!.Execute(null);

        Assert.Equal("SHJVHZVG", vm.Twitch.DeviceCode);
        Assert.Equal(["SHJVHZVG"], clipboard);
        gate.TrySetCanceled();
    }
}
