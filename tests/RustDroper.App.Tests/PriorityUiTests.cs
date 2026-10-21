using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.Time.Testing;
using RustDroper.App.Tests.Player;
using RustDroper.App.ViewModels;
using RustDroper.App.Views;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.Tests;

/// <summary>Yayıncı drop'ları öncelik sırası (arayüz): tek ızgara, kampanya sırası, kalıcı liste, platform ayrımı, sürükleme geometrisi/oturumu.</summary>
public class PriorityUiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    private static Campaign StreamerCampaign(string id, params string[] rewards) => new(
        Platform.Twitch, id, "Kampanya " + id, "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(5), CampaignScope.Streamer,
        [new Streamer("s-" + id, "login" + id, "Yayıncı " + id, new Uri("https://www.twitch.tv/login" + id), null)],
        [.. (rewards.Length == 0 ? [id + "-d"] : rewards).Select((r, i) => new DropReward(r, "Ödül " + r, 60 * (i + 1), null))], null);

    private static (DashboardViewModel Dashboard, List<IReadOnlyList<string>> Saved) Create(params string[] campaignIds)
    {
        var saved = new List<IReadOnlyList<string>>();
        var dashboard = new DashboardViewModel(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc);
        dashboard.PriorityChanged = saved.Add;
        Update(dashboard, campaignIds);
        return (dashboard, saved);
    }

    private static void Update(DashboardViewModel dashboard, params string[] campaignIds)
    {
        var snapshot = Dx.Snapshot([.. campaignIds.Select(id => (StreamerCampaign(id), new[] { Dx.Channel(id, "login" + id) }))]);
        dashboard.Update(new DashboardInput(Dx.Engine(), false, snapshot, null, new HashSet<string>(), Now));
    }

    private static string[] Order(DashboardViewModel d) => [.. d.StreamerCards.Select(c => c.CampaignId!)];

    [Fact]
    public void StreamerCards_AreOneFlatGrid_InDefaultOrderWhenThereIsNoPriority()
    {
        var (dashboard, _) = Create("a", "b", "c");

        Assert.Equal(3, dashboard.StreamerCards.Count);
        Assert.True(dashboard.HasStreamerOrderHint);
        Assert.All(dashboard.StreamerCards, c => Assert.True(c.IsDraggable)); // tutamaç görünür
        Assert.Equal(new[] { "a", "b", "c" }.Order(), Order(dashboard).Order());
    }

    [Fact]
    public void ProviderOrder_IsApplied_UnrankedGoLast_NewOnesAppendedWithoutLosingTheOrder()
    {
        var saved = new List<string> { "c", "a" };
        var dashboard = new DashboardViewModel(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc) { PriorityProvider = () => saved };

        Update(dashboard, "a", "b", "c");
        Assert.Equal(["c", "a", "b"], Order(dashboard)); // b listede yok → sona

        Update(dashboard, "a", "b", "c", "d"); // yeni etkinlik
        Assert.Equal(["c", "a", "b", "d"], Order(dashboard));
    }

    [Fact]
    public void MoveStreamerCard_ForwardAndBackward_PersistsTheWholeList()
    {
        var (dashboard, saved) = Create("a", "b", "c", "d");
        dashboard.PriorityProvider = null;
        var start = Order(dashboard);
        var first = dashboard.StreamerCards.First(c => c.CampaignId == start[0]);

        dashboard.MoveStreamerCard(first, 2); // ileri: hedef kartın arkasına
        var order = Order(dashboard);

        Assert.Equal([start[1], start[2], start[0], start[3]], order);
        Assert.Equal(order, saved[^1]);

        var last = dashboard.StreamerCards.First(c => c.CampaignId == start[3]);
        dashboard.MoveStreamerCard(last, 0); // geri: hedef kartın önüne
        Assert.Equal([start[3], start[1], start[2], start[0]], Order(dashboard));
        Assert.Equal(Order(dashboard), saved[^1]);
    }

    [Fact]
    public void Move_ToTheSameCampaignOrOutOfRange_IsHandled()
    {
        var (dashboard, saved) = Create("a", "b");
        var card = dashboard.StreamerCards[0];

        dashboard.MoveStreamerCard(card, 0);
        Assert.Empty(saved); // yerinde bırakma: kayıt yok

        dashboard.MoveStreamerCard(card, 99); // sınır dışı: en sona kenetlenir
        Assert.Equal(card.CampaignId, Order(dashboard)[^1]);
        Assert.Single(saved);
    }

    [Fact]
    public void CardsOfTheSameCampaign_MoveTogether()
    {
        var saved = new List<IReadOnlyList<string>>();
        var dashboard = new DashboardViewModel(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc) { PriorityChanged = saved.Add };
        var snapshot = Dx.Snapshot((StreamerCampaign("a", "a1", "a2"), [Dx.Channel("1", "x")]), (StreamerCampaign("b"), [Dx.Channel("2", "y")]));
        dashboard.Update(new DashboardInput(Dx.Engine(), false, snapshot, null, new HashSet<string>(), Now));
        var start = Order(dashboard);
        Assert.Equal(3, start.Length);
        var b = dashboard.StreamerCards.Single(c => c.CampaignId == "b");

        dashboard.MoveStreamerCard(b, 0);

        Assert.Equal("b", Order(dashboard)[0]);
        Assert.Equal(["a", "a"], Order(dashboard).Skip(1));
    }

    [Fact]
    public void SavedOrderOfCurrentlyHiddenCampaigns_IsKept()
    {
        var stored = new List<string> { "gone", "a", "b" };
        var dashboard = new DashboardViewModel(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc) { PriorityProvider = () => stored };
        IReadOnlyList<string>? last = null;
        dashboard.PriorityChanged = list => { last = list; stored = [.. list]; };
        Update(dashboard, "a", "b");

        dashboard.MoveStreamerCard(dashboard.StreamerCards.First(c => c.CampaignId == "b"), 0);

        Assert.Equal(["b", "a", "gone"], last); // görünmeyen kampanyanın kaydı silinmez
    }

    [Fact]
    public void MainViewModel_SavesPerPlatform_AndRestoresIt()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        using var twitch = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings);
        using var kick = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings, platform: PlatformProfile.Kick);
        var vm = new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, kickService: kick);
        vm.IsWindowVisible = true;
        var snapshot = Dx.Snapshot((StreamerCampaign("a"), [Dx.Channel("1", "x")]), (StreamerCampaign("b"), [Dx.Channel("2", "y")]));
        vm.Dashboard.Update(new DashboardInput(Dx.Engine(), false, snapshot, null, new HashSet<string>(), Now));
        var b = vm.Dashboard.StreamerCards.First(c => c.CampaignId == "b");

        vm.Dashboard.MoveStreamerCard(b, 0);

        Assert.Equal("b", settings.Current.TwitchStreamerPriority[0]);
        Assert.Empty(settings.Current.KickStreamerPriority); // Kick listesi ayrı

        vm.SelectedPlatform = Platform.Kick;
        vm.Dashboard.Update(new DashboardInput(Dx.Engine(), false, snapshot, null, new HashSet<string>(), Now, Platform.Kick));
        vm.Dashboard.MoveStreamerCard(vm.Dashboard.StreamerCards.First(c => c.CampaignId == "a"), 1);

        Assert.Equal("b", settings.Current.TwitchStreamerPriority[0]); // dokunulmadı
        Assert.Equal(["b", "a"], settings.Current.KickStreamerPriority.ToArray()); // Kick'in kendi listesi (varsayılan a,b → a ikinci sıraya)
    }

    [Theory]
    [InlineData(10, 10, 0)]
    [InlineData(250, 10, 1)]
    [InlineData(990, 10, 4)]
    [InlineData(10, 250, 5)]
    [InlineData(990, 400, 6)] // 7 kart: son satırın son hücresinden sonrası en yakın geçerli hücre
    [InlineData(-50, -50, 0)]
    public void GridMath_TargetIndex_UsesFiveColumnsOfEqualCells(double x, double y, int expected) =>
        Assert.Equal(expected, GridMath.TargetIndex(new Point(x, y), new Size(1000, 400), 5, 7));

    [Fact]
    public void GridMath_Cells_AndSlotsAfterAMove()
    {
        var cell = GridMath.Cell(6, new Size(1000, 400), 5, 7);

        Assert.Equal(new Rect(200, 200, 200, 200), cell);
        Assert.Equal(3, GridMath.SlotAfterMove(0, 0, 3)); // sürüklenen kendi hedefine
        Assert.Equal([0, 1, 2, 4], new[] { 1, 2, 3, 4 }.Select(i => GridMath.SlotAfterMove(i, 0, 3))); // ileri taşıma: aradakiler bir geri
        Assert.Equal([3, 4, 5, 1], new[] { 2, 3, 4, 1 }.Select(i => GridMath.SlotAfterMove(i, 5, 2))); // geri taşıma: aradakiler bir ileri
        Assert.Equal(6, GridMath.SlotAfterMove(6, 0, 3)); // aralık dışı: yerinde
    }

    [Fact]
    public async Task DragSession_MovesTheCard_ThroughTheRealItemsControl()
    {
        var (dashboard, saved) = Create("a", "b", "c", "d", "e", "f");
        dashboard.PriorityProvider = null;
        var before = Order(dashboard);
        using var sta = StaDispatcher.Start();

        await sta.InvokeAsync(() =>
        {
            var template = new DataTemplate();
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DropRow.Name)));
            text.SetValue(FrameworkElement.HeightProperty, 100.0);
            template.VisualTree = text;
            var panel = new FrameworkElementFactory(typeof(UniformGrid));
            panel.SetValue(UniformGrid.ColumnsProperty, 5);
            var items = new ItemsControl { ItemsSource = dashboard.StreamerCards, ItemTemplate = template, ItemsPanel = new ItemsPanelTemplate(panel), DataContext = dashboard, Width = 1000, Height = 200 };
            items.ApplyTemplate();
            items.Measure(new Size(1000, 200));
            items.Arrange(new Rect(0, 0, 1000, 200));
            items.UpdateLayout();

            var session = new CardDrag.DragSession(items, 0, new Point(50, 50));
            session.Move(new Point(52, 52)); // eşik altı: sürükleme başlamaz
            Assert.False(session.IsDragging);
            session.Move(new Point(430, 60)); // 3. hücre (200 px'lik hücrelerde 400–600)
            Assert.True(session.IsDragging);
            Assert.Equal(2, session.Target);
            session.End(commit: true);
        });

        Assert.Equal([before[1], before[2], before[0], before[3], before[4], before[5]], Order(dashboard));
        Assert.Equal(Order(dashboard), saved[^1]);
    }

    [Fact]
    public async Task DragSession_Cancel_ChangesNothing()
    {
        var (dashboard, saved) = Create("a", "b", "c");
        var before = Order(dashboard);
        using var sta = StaDispatcher.Start();

        await sta.InvokeAsync(() =>
        {
            var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(Border)) };
            var panel = new FrameworkElementFactory(typeof(UniformGrid));
            panel.SetValue(UniformGrid.ColumnsProperty, 5);
            var items = new ItemsControl { ItemsSource = dashboard.StreamerCards, ItemTemplate = template, ItemsPanel = new ItemsPanelTemplate(panel), DataContext = dashboard, Width = 1000, Height = 100 };
            items.ApplyTemplate();
            items.Measure(new Size(1000, 100));
            items.Arrange(new Rect(0, 0, 1000, 100));
            items.UpdateLayout();

            var session = new CardDrag.DragSession(items, 0, new Point(10, 10));
            session.Move(new Point(500, 10));
            session.End(commit: false);
        });

        Assert.Equal(before, Order(dashboard));
        Assert.Empty(saved);
    }
}
