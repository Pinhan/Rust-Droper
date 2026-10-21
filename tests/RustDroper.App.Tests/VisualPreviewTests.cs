using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using RustDroper.App.Tests.Player;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Media;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

/// <summary>
/// Görsel önizleme (isteğe bağlı): <c>RUSTDROPER_PREVIEW_DIR=&lt;klasör&gt;</c> verilirse sahte servislerle gerçek <see cref="MainWindow"/>'u
/// ekran dışında açar ve her iki platform ekranını, geçiş karelerini ve ayarları PNG olarak yazar. Ağ/hesap/gerçek veri kullanmaz.
/// </summary>
[Trait("Category", "VisualPreview")]
public class VisualPreviewTests
{
    private static bool _appCreated;

    /// <summary>
    /// WPF'te süreç başına TEK <see cref="Application"/> olabilir; bu yüzden her önizleme AYRI süreçte koşturulur
    /// (<c>--filter "FullyQualifiedName~Render_Cards"</c> gibi). Aynı süreçte ikinci önizleme sessizce atlanır.
    /// </summary>
    private static async Task RunStaAsync(Func<Task> body)
    {
        if (_appCreated)
        {
            return;
        }

        _appCreated = true;
        if (PreviewLanguage is { } language)
        {
            RustDroper.Core.Localization.Loc.Configure(language);
        }

        Environment.SetEnvironmentVariable("RUSTDROPER_SKIP_STARTUP", "1"); // `new App()` gerçek OnStartup'ı (gerçek veri klasörü, tek örnek kilidi) çalıştırmasın
        using var sta = StaDispatcher.Start();
        await sta.InvokeAsync(async () =>
        {
            var app = new App();
            app.InitializeComponent(); // tema + dönüştürücüler (App.xaml)
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await body();
        });
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    /// <summary>RUSTDROPER_PREVIEW_LANG=tr|en verilirse önizleme o dilde çizilir ve dosya adına "-tr"/"-en" eklenir (mevcut görüntüler ezilmez).</summary>
    private static string? PreviewLanguage => Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_LANG") is { Length: > 0 } lang ? lang : null;

    private static string WithLanguage(string path) =>
        PreviewLanguage is { } lang ? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + lang + Path.GetExtension(path)) : path;

    private static void SaveFrame(MainWindow window, string path, int height = 743)
    {
        path = WithLanguage(path);
        // Pencere gösterilmez (HWND yok): kök öğe elle ölçülüp yerleştirilir, böylece yerleşim her karede deterministik güncellenir.
        var root = (FrameworkElement)window.Content;
        var width = 886;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        }

        target.Render(background);
        target.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class WpfDispatcher(Dispatcher dispatcher) : RustDroper.App.Infrastructure.IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(action);
    }

    private static async Task SettleAsync(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(8);
        }
    }

    [Fact]
    public async Task Render_Screens_Transition_AndSettings()
    {
        var outDir = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(outDir) || Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_SCREENS") != "1")
        {
            // Dispatcher turları gerektirir; bu sürümde önizleme Application'ı turlar sırasında kapanıyor → varsayılan kapalı
            // (01–07 görüntüleri ilk tasarım turundan, commit 426b32e). Denemek için RUSTDROPER_PREVIEW_SCREENS=1.
            return;
        }

        Directory.CreateDirectory(outDir);
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var twitchAccount = new StubAccount { Restore = new(TwitchRestoreStatus.Restored, new TwitchSession("t", "1", "demo_twitch", null)) };
        var kickAccount = new StubAccount { Restore = new(TwitchRestoreStatus.Restored, new TwitchSession("t", "2", "demo_kick", null)) };
        var twitchPlatform = new StubPlatform();
        var kickPlatform = new StubPlatform { Session = new DropSession("kd1", 47, 120) };
        EngineOptions Options(AppSettings s) => new() { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(50) };
        using var twitch = new DropperService(twitchAccount, new StubFactory(twitchPlatform), settings, engineOptions: Options);
        using var kick = new DropperService(kickAccount, new StubFactory(kickPlatform), settings, engineOptions: Options, platform: PlatformProfile.Kick);
        twitchPlatform.Snapshot = Dx.Snapshot(
            (Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Now.AddDays(9), null, null, ("d1", 60), ("d2", 180), ("d3", 300)), [Dx.Channel("1", "rustfan")]));
        kickPlatform.Snapshot = Dx.Snapshot(
            (Dx.Campaign("k1", "Rust Gece Drop", CampaignScope.General, null, Now.AddDays(5), null, null, ("kd1", 120), ("kd2", 240)),
                [new LiveChannel(Platform.Kick, "9", "gecekusu", "gecekusu", 100, null, new Uri("https://kick.com/gecekusu"), null)]));

                await RunStaAsync(async () =>
        {

            var vm = new MainViewModel(
                settings, new FakeAutostart(), new RecordingBrowser(), twitch, new WpfDispatcher(Dispatcher.CurrentDispatcher), new FakeTimeProvider(Now), TimeZoneInfo.Utc,
                kickService: kick, clipboard: _ => { });
            await vm.InitializeAsync();
            vm.IsWindowVisible = true;
            var window = new MainWindow { DataContext = vm, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = 0, ShowInTaskbar = false };
            await SettleAsync(400);
            SaveFrame(window, Path.Combine(outDir, "01-twitch-bosta.png"));

            vm.StartPauseCommand.Execute(null);
            var deadline = Environment.TickCount64 + 15000;
            while (vm.Dashboard.Now.Percent <= 0 && Environment.TickCount64 < deadline)
            {
                await SettleAsync(50);
            }

            await SettleAsync(900);
            SaveFrame(window, Path.Combine(outDir, "02-twitch-izleniyor.png"));

            vm.SelectPlatformCommand.Execute("Kick");
            await SettleAsync(900); // geçiş animasyonu çerçeveleri için gerçek uygulama görüntüsü: 11-gercek-gecis.png
            SaveFrame(window, Path.Combine(outDir, "05-kick-izleniyor.png"));

            vm.ToggleSettingsCommand.Execute(null);
            await SettleAsync(500);
            SaveFrame(window, Path.Combine(outDir, "06-ayarlar.png"));

            vm.SelectPlatformCommand.Execute("Twitch");
            await SettleAsync(500);
            vm.StartPauseCommand.Execute(null);
            await SettleAsync(300);
            window.Close();
        });
    }

    /// <summary>
    /// Giriş panelleri (Twitch cihaz kodu ortalı + Kopyala, "Kopyalandı ✓" geri bildirimi, Kick QR + Kopyala). Senkron: kart durumları doğrudan verilir ve
    /// hiçbir Dispatcher turu beklenmez (bu sürümde WPF Application'ı ilk turlarda kapanıyor).
    /// </summary>
    [Fact]
    public async Task Render_LoginPanels_CopyButtons()
    {
        var outDir = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            return;
        }

        Directory.CreateDirectory(outDir);
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        using var twitch = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings);
        using var kick = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings, platform: PlatformProfile.Kick);
        var kickLink = new Uri("https://kick.com/tv/login?uuid=A1B2C3D4-0000-4000-8000-000000000001&code=123456");

        await RunStaAsync(() =>
        {
            var copied = new List<string>();
            var vm = new MainViewModel(
                settings, new FakeAutostart(), new RecordingBrowser(), twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), TimeZoneInfo.Utc,
                kickService: kick, clipboard: copied.Add);
            var window = new MainWindow { DataContext = vm };

            vm.Twitch.Update(new AccountSnapshot(
                AccountStatus.LoggingIn, null, new TwitchDeviceCode("dev", "SHJVHZVG", new Uri("https://www.twitch.tv/activate"), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4))));
            SaveFrame(window, Path.Combine(outDir, "08-twitch-cihaz-kodu.png"));
            vm.Twitch.CopyCodeCommand!.Execute(null);
            SaveFrame(window, Path.Combine(outDir, "09-twitch-kod-kopyalandi.png"));

            vm.SelectedPlatform = Platform.Kick;
            vm.Kick.Update(new AccountSnapshot(
                AccountStatus.LoggingIn, null, new TwitchDeviceCode("A1B2C3D4-0000-4000-8000-000000000001", "123456", kickLink, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4))));
            SaveFrame(window, Path.Combine(outDir, "10-kick-qr-paneli.png"));
            return Task.CompletedTask;
        });
    }

    /// <summary>Başlık çubuğu (ortada geçiş), kart içinde Facepunch/Steam bağlantı satırı + site düğmesi, drop sayıları, sönük claim kartları, bilgi satırı. Senkron/pencere açılmadan çizilir.</summary>
    [Fact]
    public async Task Render_HeaderAndPolish()
    {
        var outDir = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_DIR");
        var assets = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_ASSETS");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(assets))
        {
            return;
        }

        Directory.CreateDirectory(outDir);
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        using var twitch = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings);
        using var kick = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings, platform: PlatformProfile.Kick);
        var media = new FakeMedia();
        string Asset(string n) => Path.Combine(assets, n + ".jpg");
        DropMedia Entry(string name, string file, int minutes, bool streamer = false) => new(
            name, minutes, null, new Uri("https://files.facepunch.com/" + file + ".jpg"), null, Asset(file), streamer, streamer ? "TwitchRivals" : null,
            streamer ? new Uri("https://www.twitch.tv/TwitchRivals") : null);
        media.Catalogs[Platform.Twitch] =
        [
            Entry("Rug", "rug", 60), Entry("Bed", "bed", 120), Entry("Garage Door", "garage-door", 180), Entry("Thompson", "thompson", 240),
            Entry("Large Wood Box", "large-wood-box", 300), Entry("Backpack", "backpack", 30, streamer: true),
        ];
        media.Events[Platform.Twitch] = new FacepunchEvent(
            "Base Invaders", ["Twitch Drops", "54"], "Hosted by Rustoria", "is-pending", "Upcoming", Now.AddDays(4).AddHours(3), Now.AddDays(13).AddHours(6));
        var turkey = TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "TR", "TR");
        var files = new[] { "rug", "bed", "garage-door", "thompson", "large-wood-box", "backpack", "rug" };
        var claimed = Enumerable.Range(1, 7).Select(i =>
        {
            var url = new Uri($"https://static-cdn.jtvnw.net/twitch-quests-assets/REWARD/fake-{i}.jpeg");
            media.Cached[url.AbsoluteUri] = Asset(files[i - 1]);
            return new ClaimedReward(Platform.Twitch, "fake-" + i, "Sahte Ödül " + i, Now.AddDays(-i), 1, true, new Uri("https://twitch.facepunch.com/connect"), url);
        }).ToList();

        await RunStaAsync(() =>
        {
            var vm = new MainViewModel(
                settings, new FakeAutostart(), new RecordingBrowser(), twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), turkey,
                kickService: kick, clipboard: _ => { }, media: media);
            vm.IsWindowVisible = true;
            var window = new MainWindow { DataContext = vm };
            var general = new Campaign(
                Platform.Twitch, "c1", "Base Invaders", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(9), CampaignScope.General, [],
                [new DropReward("a", "Rug", 60, null), new DropReward("b", "Bed", 120, null), new DropReward("c", "Garage Door", 180, null),
                 new DropReward("d", "Thompson", 240, null), new DropReward("e", "Large Wood Box", 300, null)], null);
            vm.Twitch.Update(new AccountSnapshot(AccountStatus.LoggedIn, "demo_twitch"));
            vm.Dashboard.Update(new DashboardInput(
                Dx.Engine(EngineState.Watching, "izleniyor", new Target(general, Dx.Channel("1", "rustfan")), new DropSession("c", 74, 180), Now.AddSeconds(-40)),
                true, Dx.Snapshot((general, [Dx.Channel("1", "rustfan")])),
                new TwitchInventorySnapshot(claimed, [Dx.Progress("c1", "a", 60, 60, claimed: true)]), new HashSet<string>(), Now));
            SaveFrame(window, Path.Combine(outDir, "30-baslik-ve-duzeltmeler-twitch.png"), 1500);

            // Bağlı değil + katalog kartları (etkinlik öncesi)
            vm.Dashboard.Update(new DashboardInput(
                Dx.Engine(), false, null,
                new TwitchInventorySnapshot([new ClaimedReward(Platform.Twitch, "x", "Sahte Ödül", Now, 1, false, new Uri("https://twitch.facepunch.com/connect"), null)], []),
                new HashSet<string>(), Now));
            SaveFrame(window, Path.Combine(outDir, "31-baglanti-yok-katalog-kartlari.png"), 1500);
            return Task.CompletedTask;
        });
    }

    /// <summary>Drop kartı ızgarası: tüm durumlar (sırada, devam, izleniyor, claim'e hazır, claim edildi) + yayıncı kartı + Kick (görselsiz yer tutucu). Görseller RUSTDROPER_PREVIEW_ASSETS klasöründen (rug.jpg...).</summary>
    [Fact]
    public async Task Render_Cards_AllStates()
    {
        var outDir = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_DIR");
        var assets = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_ASSETS");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(assets))
        {
            return;
        }

        Directory.CreateDirectory(outDir);
        string Asset(string n) => Path.Combine(assets, n + ".jpg");
        var media = new FakeMedia();
        DropMedia Entry(string name, string file, int minutes, bool streamer = false) => new(
            name, minutes, null, new Uri("https://files.facepunch.com/" + file + ".jpg"), null, Asset(file), streamer, streamer ? "TwitchRivals" : null,
            streamer ? new Uri("https://www.twitch.tv/TwitchRivals") : null);
        media.Catalogs[Platform.Twitch] =
        [
            Entry("Rug", "rug", 60), Entry("Bed", "bed", 120), Entry("Garage Door", "garage-door", 180), Entry("Thompson", "thompson", 240),
            Entry("Large Wood Box", "large-wood-box", 300), Entry("Backpack", "backpack", 30, streamer: true),
        ];

                await RunStaAsync(async () =>
        {

            var dashboard = new DashboardViewModel(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, media);
            var general = new Campaign(
                Platform.Twitch, "c1", "Base Invaders", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(9), CampaignScope.General, [],
                [new DropReward("a", "Rug", 60, null), new DropReward("b", "Bed", 120, null), new DropReward("c", "Garage Door", 180, null),
                 new DropReward("d", "Thompson", 240, null), new DropReward("e", "Large Wood Box", 300, null)], null);
            var rivals = new Streamer("s1", "twitchrivals", "TwitchRivals", new Uri("https://www.twitch.tv/TwitchRivals"), null);
            var streamerCampaign = new Campaign(
                Platform.Twitch, "c2", "Rivals", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(5), CampaignScope.Streamer, [rivals],
                [new DropReward("r1", "Backpack", 30, null)], null);
            // Claim edilen ödüller (SAHTE adlar/tarihler; görseller yalnızca önizleme varlıkları)
            var files = new[] { "rug", "bed", "garage-door", "thompson", "large-wood-box", "backpack", "rug" };
            var claimedFake = Enumerable.Range(1, 7).Select(i =>
            {
                var url = new Uri($"https://static-cdn.jtvnw.net/twitch-quests-assets/REWARD/fake-{i}.jpeg");
                if (i != 4)
                {
                    media.Cached[url.AbsoluteUri] = Asset(files[i - 1]); // 4. kart: görsel yok → yer tutucu
                }

                return new ClaimedReward(
                    Platform.Twitch, "fake-" + i, i == 3 ? "Sahte Ödül Uzun Adlı Bir Örnek" : "Sahte Ödül " + i, Now.AddDays(-i), i == 2 ? 3 : 1, true,
                    i == 5 ? null : new Uri("https://twitch.facepunch.com/connect"), url, i == 1 ? "Örnek Oyun" : null);
            }).ToList();
            var inventory = new TwitchInventorySnapshot(
                claimedFake,
                [Dx.Progress("c1", "a", 60, 60, claimed: true), Dx.Progress("c1", "b", 120, 120, claimId: "claim-b"), Dx.Progress("c1", "d", 240, 70), Dx.Progress("c2", "r1", 30, 12)]);
            var engine = Dx.Engine(EngineState.Watching, "izleniyor", new Target(general, Dx.Channel("1", "rustfan")), new DropSession("c", 74, 180));
            dashboard.Update(new DashboardInput(
                engine, true,
                Dx.Snapshot((general, [Dx.Channel("1", "rustfan")]), (streamerCampaign, [Dx.Channel("s1", "twitchrivals")])),
                inventory, new HashSet<string>(), Now));

            // Pencere açılmadan (HWND yok) çizim: kök öğe elle ölçülüp yerleştirilir; ItemsControl üretimi bu yerleşim geçişinde olur.
            var host = new Border
            {
                Width = 900,
                Height = 1150,
                Background = (Brush)Application.Current!.FindResource("BgBrush"),
                Child = new RustDroper.App.Views.DashboardView { DataContext = dashboard, Margin = new Thickness(22) },
            };
            SaveVisual(host, Path.Combine(outDir, "20-kartlar-twitch-durumlar.png"));
            host.Height = 1700; // claim kartları sayfanın altında
            SaveVisual(host, Path.Combine(outDir, "24-claim-kartlari-twitch.png"));
            host.Height = 1150;

            // Kick: kendi kampanyası, görseli olmayan ödüller yer tutucu ikonla
            var kickCampaign = new Campaign(
                Platform.Kick, "k1", "Kick Rust Drops", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(5), CampaignScope.General, [],
                [new DropReward("k1", "Emote", 60, null), new DropReward("k2", "Rozet", 120, null), new DropReward("k3", "Kask", 240, null)], null);
            dashboard.Update(new DashboardInput(
                Dx.Engine(), false, Dx.Snapshot((kickCampaign, [])),
                new TwitchInventorySnapshot(
                    [new ClaimedReward(Platform.Kick, "kf1", "Sahte Kick Ödülü", null, 1, true, new Uri("https://kick.facepunch.com/connect"), null)],
                    [Dx.Progress("k1", "k1", 60, 25)]),
                new HashSet<string>(), Now, Platform.Kick));
            SaveVisual(host, Path.Combine(outDir, "21-kartlar-kick.png"));
            host.Height = 1700;
            SaveVisual(host, Path.Combine(outDir, "25-claim-kartlari-kick.png"));
        });
    }

    /// <summary>Yayıncı kartları ızgarası + sürükle-bırak: sürükleme ortası (kart imleci izler, diğerleri kayar) ve bırakıldıktan sonraki sıra. Pencere açmadan çizilir.</summary>
    [Fact]
    public async Task Render_StreamerDrag()
    {
        var outDir = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_DIR");
        var assets = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_ASSETS");
        if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(assets))
        {
            return;
        }

        Directory.CreateDirectory(outDir);
        string Asset(string n) => Path.Combine(assets, n + ".jpg");
        var media = new FakeMedia();
        var names = new[] { ("Backpack", "backpack"), ("Rug", "rug"), ("Bed", "bed"), ("Thompson", "thompson"), ("Large Wood Box", "large-wood-box"), ("Garage Door", "garage-door") };
        media.Catalogs[Platform.Twitch] = [.. names.Select(n => new DropMedia(n.Item1, 30, null, new Uri("https://files.facepunch.com/" + n.Item2 + ".jpg"), null, Asset(n.Item2), true, null, null))];

        await RunStaAsync(() =>
        {
            var priority = new List<string>();
            var dashboard = new DashboardViewModel(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, media)
            {
                PriorityProvider = () => priority,
                PriorityChanged = list => { priority.Clear(); priority.AddRange(list); },
            };
            var campaigns = names.Select((n, i) => (
                new Campaign(
                    Platform.Twitch, "s" + i, "Kampanya " + n.Item1, "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(5), CampaignScope.Streamer,
                    [new Streamer("id" + i, "login" + i, "Yayıncı" + (i + 1), new Uri("https://www.twitch.tv/login" + i), null)],
                    [new DropReward("r" + i, n.Item1, 30, null)], null),
                new[] { Dx.Channel("c" + i, "login" + i) })).ToArray();
            dashboard.Update(new DashboardInput(Dx.Engine(), false, Dx.Snapshot(campaigns), null, new HashSet<string>(), Now));

            var host = new Border
            {
                Width = 900,
                Height = 760,
                Background = (Brush)Application.Current!.FindResource("BgBrush"),
                Child = new RustDroper.App.Views.DashboardView { DataContext = dashboard, Margin = new Thickness(22) },
            };
            host.Measure(new Size(900, 760));
            host.Arrange(new Rect(0, 0, 900, 760));
            host.UpdateLayout();
            var grid = Descendants(host).OfType<ItemsControl>().First(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "StreamerGrid");
            SaveVisual(host, Path.Combine(outDir, "26-yayinci-karti-siralama-oncesi.png"));

            var origin = grid.TranslatePoint(new Point(0, 0), host);
            var from = grid.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement;
            var start = new Point(from!.TranslatePoint(new Point(0, 0), grid).X + 60, from.TranslatePoint(new Point(0, 0), grid).Y + 40);
            var session = new RustDroper.App.Views.CardDrag.DragSession(grid, 1, start);
            session.Move(new Point(start.X + 320, start.Y + 18)); // 3. kartın üstü: aradakiler kayar
            host.UpdateLayout();
            SaveVisual(host, Path.Combine(outDir, "26-yayinci-karti-surukleme.png"));
            session.End(commit: true);
            host.UpdateLayout();
            SaveVisual(host, Path.Combine(outDir, "27-yayinci-karti-siralama-sonrasi.png"));
            _ = origin;
            return Task.CompletedTask;
        });
    }

    /// <summary>Başlat uyarısı içeriği (pencere açmadan çizilir).</summary>
    [Fact]
    public async Task Render_StartNotice()
    {
        var outDir = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            return;
        }

        Directory.CreateDirectory(outDir);
        await RunStaAsync(() =>
        {
            var host = new Border
            {
                Width = 492,
                Height = 260,
                Padding = new Thickness(16),
                Background = (Brush)Application.Current!.FindResource("BgBrush"),
                Child = new RustDroper.App.Views.StartNoticeView(),
            };
            SaveVisual(host, Path.Combine(outDir, "28-baslat-uyarisi.png"));
            return Task.CompletedTask;
        });
    }

    /// <summary>Güncelleme notları sayfası, ÖRNEK veriyle (çok sürüm, tüm bölümler, tüm türler) yerleşim denemesi. Gerçek changelog.json değildir.</summary>
    [Fact]
    public async Task Render_ReleaseNotes_Sample()
    {
        var outDir = Environment.GetEnvironmentVariable("RUSTDROPER_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            return;
        }

        const string sample = """
            { "releases": [
              { "version": "2.0.0", "date": "2027-02-01", "type": "major", "tag": { "tr": "Yeni mimari", "en": "New architecture" },
                "summary": { "tr": "Örnek: motor ve arayüz katmanları yeniden düzenlendi; eski ayar dosyaları dönüştürülür.", "en": "Sample: the engine and UI layers were reorganized; old settings files are converted." },
                "added": [ { "tr": "Örnek eklenen madde bir", "en": "Sample added item one" }, { "tr": "Örnek eklenen madde iki, biraz daha uzun bir cümle olsun ki satır kırılması görülebilsin ve yerleşim denensin.", "en": "Sample added item two, a somewhat longer sentence so that line wrapping can be seen and the layout can be tested." } ],
                "changed": [ { "tr": "Örnek değişen madde", "en": "Sample changed item" } ],
                "fixed": [ { "tr": "Örnek düzeltilen madde", "en": "Sample fixed item" } ],
                "removed": [ { "tr": "Örnek kaldırılan madde", "en": "Sample removed item" } ] },
              { "version": "1.1.0", "date": "2026-11-12", "type": "minor", "tag": { "tr": "Yeni sayfa", "en": "New page" },
                "summary": { "tr": "Örnek minor sürüm.", "en": "Sample minor release." },
                "added": [ { "tr": "Örnek yeni özellik", "en": "Sample new feature" } ] },
              { "version": "1.0.1", "date": "2026-10-25", "type": "patch", "tag": { "tr": "Düzeltme", "en": "Fix" },
                "summary": { "tr": "Örnek patch sürüm.", "en": "Sample patch release." },
                "fixed": [ { "tr": "Örnek hata düzeltmesi", "en": "Sample bug fix" } ] },
              { "version": "1.0.0", "date": "2026-10-20", "type": "baseline", "tag": { "tr": "İlk sürüm", "en": "Initial release" },
                "summary": { "tr": "Örnek ilk sürüm.", "en": "Sample initial release." },
                "added": [ { "tr": "Örnek ilk özellik", "en": "Sample first feature" } ] }
            ] }
            """;
        Directory.CreateDirectory(outDir);
        await RunStaAsync(() =>
        {
            var vm = new ReleaseNotesViewModel(() => sample);
            vm.Load();
            var host = new Border
            {
                Width = 840,
                Height = 1500,
                Padding = new Thickness(16),
                Background = (Brush)Application.Current!.FindResource("BgBrush"),
                Child = new RustDroper.App.Views.ReleaseNotesView { DataContext = vm },
            };
            SaveVisual(host, Path.Combine(outDir, "29-guncelleme-notlari-ornek.png"));
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void SaveVisual(Border root, string path)
    {
        path = WithLanguage(path);
        var size = new Size(root.Width, root.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var target = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
