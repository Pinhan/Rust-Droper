using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Platforms.Twitch.PubSub;
using RustDroper.Core.Watching;

// Geliştirici aracı: giriş/oturum denemeleri. Token'lar ASLA ekrana yazılmaz.
// Veri klasörü: RUSTDROPER_DATA ortam değişkeni, yoksa %LOCALAPPDATA%\RustDroper (uygulamayla ortak).
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (string.Equals(args.FirstOrDefault(a => a != "--"), "changelog", StringComparison.OrdinalIgnoreCase)) // PowerShell "--" ayracını da iletebilir
{
    return RustDroper.Cli.ChangelogCommand.Run(args); // veri klasörüne/hesaplara dokunmaz
}

var paths = AppPaths.ForPortable();
paths.EnsureCreated();
using var logs = new RollingFileLoggerProvider(paths.LogsDirectory);
var logger = logs.CreateLogger("cli");
var secrets = new DpapiSecretStore(paths.SecretsFile);

// --client smartbox|mobileweb (varsayılan smartbox). Hangi profilin drop sorgularıyla çalıştığı canlı sınanır (U2).
var clientArg = args.SkipWhile(a => !string.Equals(a, "--client", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
var profile = clientArg is null ? TwitchClients.SmartboxTv : TwitchClients.ByName(clientArg);
if (profile is null)
{
    Console.WriteLine($"Bilinmeyen istemci '{clientArg}'. Seçenekler: {string.Join(", ", TwitchClients.All.Select(p => p.Name))}");
    return 1;
}

using var http = TwitchConstants.CreateHttpClient(profile);
var manager = new TwitchSessionManager(new TwitchAuthClient(http, profile), secrets, logger);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var noBrowser = args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);
string? OptionValue(string name) =>
    args.SkipWhile(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();

// Konumsal argümanlar: "--x" seçenekleri ve değer alan seçeneklerin değerleri hariç.
var valueOptions = new[] { "--client", "--match", "--minutes", "--campaign" };
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (valueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase))
    {
        i++;
    }
    else if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        positional.Add(args[i]);
    }
}

var command = string.Join(' ', positional).Trim().ToLowerInvariant();

try
{
    switch (command)
    {
        case "login kick":
        case "whoami kick":
        case "logout kick":
            return await new KickCommands(secrets, logger, noBrowser, cts.Token).RunAsync(command.Split(' ')[0], [], OptionValue);
        case var kickCommand when kickCommand.StartsWith("campaigns kick", StringComparison.Ordinal) || kickCommand.StartsWith("watch kick", StringComparison.Ordinal) || kickCommand.StartsWith("progress kick", StringComparison.Ordinal):
            return await new KickCommands(secrets, logger, noBrowser, cts.Token).RunAsync(
                kickCommand.Split(' ')[0],
                kickCommand[(kickCommand.IndexOf("kick", StringComparison.Ordinal) + 4)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                OptionValue);
        case "login twitch":
            await LoginTwitchAsync();
            return 0;
        case "whoami twitch":
            return await WhoAmITwitchAsync();
        case "logout twitch":
            manager.Logout();
            Console.WriteLine("Twitch oturumu kapatıldı.");
            return 0;
        case "probe twitch":
            return await ProbeTwitchAsync();
        case var campaigns when campaigns.StartsWith("campaigns twitch", StringComparison.Ordinal):
            return await CampaignsTwitchAsync(campaigns["campaigns twitch".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        case var run when run.StartsWith("run twitch", StringComparison.Ordinal):
            return await RunEngineTwitchAsync(run["run twitch".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        case var watch when watch.StartsWith("watch twitch", StringComparison.Ordinal):
            return await WatchTwitchAsync(watch["watch twitch".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        case var scan when scan.StartsWith("scan twitch", StringComparison.Ordinal):
            return await ScanTwitchAsync(scan["scan twitch".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        case var claim when claim.StartsWith("claim twitch", StringComparison.Ordinal):
            return await ClaimTwitchAsync(positional.Skip(2).ToArray());
        default:
            Console.WriteLine("Kullanım: RustDroper.Cli <login|whoami|logout> <twitch|kick> [--no-browser]");
            Console.WriteLine($"Veri klasörü: {paths.DataDirectory}");
            return 1;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("İptal edildi.");
    return 130;
}
catch (TwitchLoginException ex)
{
    Console.WriteLine($"Giriş başarısız ({ex.Failure}): {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is PlatformHttpException or PlatformDataException or PlatformAuthException or HttpRequestException)
{
    Console.WriteLine($"Platform hatası: {ex.Message}");
    logger.LogError(ex, "CLI komutu başarısız");
    return 3;
}

async Task LoginTwitchAsync()
{
    var session = await manager.LoginAsync(
        code =>
        {
            Console.WriteLine();
            Console.WriteLine("Twitch girişi — tarayıcıda şu sayfayı aç ve kodu gir:");
            Console.WriteLine($"  Adres: {code.VerificationUri}");
            Console.WriteLine($"  Kod  : {code.UserCode}");
            Console.WriteLine($"  (Kod {code.ExpiresIn.TotalMinutes:0} dk geçerli. Onay bekleniyor...)");
            if (!noBrowser)
            {
                Process.Start(new ProcessStartInfo(code.VerificationUri.ToString()) { UseShellExecute = true });
            }
        },
        cts.Token);
    Console.WriteLine($"Giriş tamam: {session.Login} (kullanıcı no {session.UserId}).");
}

// Canlı GQL sınaması (U2): ham cevaplar data\probe\ altına yazılır (git dışı, kişisel veri içerebilir).
async Task<int> ProbeTwitchAsync()
{
    var restore = await manager.RestoreAsync(cts.Token);
    if (restore.Session is not { } session)
    {
        Console.WriteLine("Twitch: önce 'login twitch' çalıştırın.");
        return 1;
    }

    var gql = new TwitchGqlClient(
        http, profile, () => session.AccessToken, new GqlHashRegistry(paths.HashesFile), TwitchDeviceId.GetOrCreate(secrets), logger: logger);
    var outDir = Path.Combine(paths.DataDirectory, "probe");
    Directory.CreateDirectory(outDir);
    var indented = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
    var failures = 0;

    async Task<System.Text.Json.JsonDocument?> RunAsync(string label, GqlOperation op, Dictionary<string, object?> variables)
    {
        try
        {
            using var result = await gql.ExecuteAsync(op, variables, cts.Token);
            var json = System.Text.Json.JsonSerializer.Serialize(result.Data, indented);
            var file = Path.Combine(outDir, $"{label}.json");
            await File.WriteAllTextAsync(file, json, cts.Token);
            Console.WriteLine($"[{profile.Name}] {label,-10}: OK  {json.Length / 1024.0:0.0} KB  → {file}");
            foreach (var partial in result.PartialErrors)
            {
                Console.WriteLine($"    kısmi hata: {partial}");
            }

            return System.Text.Json.JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException or TwitchUnauthorizedException or TwitchTransientException)
        {
            failures++;
            Console.WriteLine($"[{profile.Name}] {label,-10}: HATA  {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    var variablesNoRewards = new Dictionary<string, object?> { ["fetchRewardCampaigns"] = false };
    using var dashboard = await RunAsync("dashboard", GqlOperations.ViewerDropsDashboard, variablesNoRewards);
    using var inventory = await RunAsync("inventory", GqlOperations.Inventory, variablesNoRewards);
    using var dashboardRewards = await RunAsync("dashboard2", GqlOperations.ViewerDropsDashboard, new Dictionary<string, object?> { ["fetchRewardCampaigns"] = true });
    if (dashboardRewards is not null)
    {
        var dumped = System.Text.Json.JsonSerializer.Serialize(dashboardRewards.RootElement);
        Console.WriteLine($"    dashboard2 (fetchRewardCampaigns=true) içerik: {dumped[..Math.Min(300, dumped.Length)]}");
    }

    if (dashboard is not null
        && dashboard.RootElement.TryGetProperty("currentUser", out var user)
        && user.TryGetProperty("dropCampaigns", out var campaigns)
        && campaigns.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        var all = campaigns.EnumerateArray().ToList();
        Console.WriteLine($"  Kampanya sayısı: {all.Count}");
        foreach (var group in all.GroupBy(c => c.TryGetProperty("status", out var s) ? s.GetString() : "?"))
        {
            Console.WriteLine($"    {group.Key}: {group.Count()}");
        }

        string? Game(System.Text.Json.JsonElement c) =>
            c.TryGetProperty("game", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.Object && g.TryGetProperty("displayName", out var n)
                ? n.GetString()
                : null;

        var rust = all.Where(c => string.Equals(Game(c), "Rust", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"  Rust kampanyası: {rust.Count}");
        var sample = rust.Concat(all).Select(c => c.TryGetProperty("id", out var id) ? id.GetString() : null).FirstOrDefault(id => id is not null);
        if (sample is not null)
        {
            _ = await RunAsync(
                "details",
                GqlOperations.DropCampaignDetails,
                new Dictionary<string, object?> { ["channelLogin"] = session.UserId, ["dropID"] = sample });
        }
    }
    else if (dashboard is not null)
    {
        var keys = string.Join(", ", dashboard.RootElement.EnumerateObject().Select(p => p.Name));
        Console.WriteLine($"  Beklenen yol (currentUser.dropCampaigns) yok; üst düzey anahtarlar: {keys}");
    }

    // Alternatif keşif yolu: Rust'ın Drops-Enabled yayınları → kanal başına AvailableDrops.
    var directoryVariables = DirectoryVariables("rust");
    using var directory = await RunAsync("directory", GqlOperations.GameDirectory, directoryVariables);
    string? firstChannelId = null;
    if (directory is not null
        && directory.RootElement.TryGetProperty("game", out var game) && game.ValueKind == System.Text.Json.JsonValueKind.Object
        && game.TryGetProperty("streams", out var streams) && streams.ValueKind == System.Text.Json.JsonValueKind.Object
        && streams.TryGetProperty("edges", out var edges) && edges.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        var nodes = edges.EnumerateArray().Select(e => e.TryGetProperty("node", out var n) ? n : default).Where(n => n.ValueKind == System.Text.Json.JsonValueKind.Object).ToList();
        Console.WriteLine($"  Drops-Enabled Rust yayını: {nodes.Count}");
        foreach (var node in nodes.Take(5))
        {
            var b = node.TryGetProperty("broadcaster", out var bc) && bc.ValueKind == System.Text.Json.JsonValueKind.Object ? bc : default;
            var login = b.ValueKind == System.Text.Json.JsonValueKind.Object && b.TryGetProperty("login", out var l) ? l.GetString() : "?";
            var viewers = node.TryGetProperty("viewersCount", out var v) ? v.ToString() : "?";
            Console.WriteLine($"    {login} ({viewers} izleyici)");
            firstChannelId ??= b.ValueKind == System.Text.Json.JsonValueKind.Object && b.TryGetProperty("id", out var bid) ? bid.GetString() : null;
        }
    }
    else if (directory is not null)
    {
        Console.WriteLine("  Dizin cevabında game.streams.edges yok (şema farklı olabilir).");
    }

    if (firstChannelId is not null)
    {
        using var available = await RunAsync("available", GqlOperations.AvailableDrops, new Dictionary<string, object?> { ["channelID"] = firstChannelId });
        if (available is not null
            && available.RootElement.TryGetProperty("channel", out var channel) && channel.ValueKind == System.Text.Json.JsonValueKind.Object
            && channel.TryGetProperty("viewerDropCampaigns", out var vdc) && vdc.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            Console.WriteLine($"  Bu kanalda kazanılabilir kampanya: {vdc.GetArrayLength()}");
            foreach (var c in vdc.EnumerateArray().Take(5))
            {
                Console.WriteLine($"    {(c.TryGetProperty("id", out var cid) ? cid.GetString() : "?")}  {(c.TryGetProperty("name", out var cn) ? cn.GetString() : "?")}");
            }

            var firstCampaign = vdc.EnumerateArray().Select(c => c.TryGetProperty("id", out var id) ? id.GetString() : null).FirstOrDefault(id => id is not null);
            if (firstCampaign is not null)
            {
                _ = await RunAsync(
                    "details",
                    GqlOperations.DropCampaignDetails,
                    new Dictionary<string, object?> { ["channelLogin"] = session.UserId, ["dropID"] = firstCampaign });
            }
        }
    }

    return failures == 0 ? 0 : 3;
}

static Dictionary<string, object?> DirectoryVariables(string slug) => TwitchCampaignDiscovery.DirectoryVariables(slug);

// Kullanıcıya dönük özet: Steam bağlantısı, talep edilmiş ödüller, canlı kampanyalar (kanal üzerinden keşif).
async Task<int> CampaignsTwitchAsync(string[] slugs)
{
    var restore = await manager.RestoreAsync(cts.Token);
    if (restore.Session is not { } session)
    {
        Console.WriteLine("Twitch: önce 'login twitch' çalıştırın.");
        return 1;
    }

    var gql = new TwitchGqlClient(
        http, profile, () => session.AccessToken, new GqlHashRegistry(paths.HashesFile), TwitchDeviceId.GetOrCreate(secrets), logger: logger);
    var slug = slugs.FirstOrDefault() ?? "rust";

    using (var inventory = await gql.ExecuteAsync(GqlOperations.Inventory, new Dictionary<string, object?> { ["fetchRewardCampaigns"] = false }, cts.Token))
    {
        var snapshot = TwitchInventoryParser.Parse(inventory.Data);
        var linked = snapshot.IsLinkedTo("twitch.facepunch.com") switch
        {
            true => "BAĞLI",
            false => "BAĞLI DEĞİL (twitch.facepunch.com/connect)",
            _ => "bilinmiyor",
        };
        Console.WriteLine($"Hesap: {session.Login} · Facepunch/Steam bağlantısı: {linked}");
        Console.WriteLine($"Talep edilmiş ödül: {snapshot.Claimed.Count} · ilerlemede olan kampanya: {snapshot.CampaignsInProgress}");
        foreach (var reward in snapshot.Claimed.Take(3))
        {
            Console.WriteLine($"  - {reward.Name} ({reward.LastAwardedAt:yyyy-MM-dd})");
        }
    }

    var discovery = await new TwitchCampaignDiscovery(gql, logger).DiscoverAsync(slug, channelsToProbe: ProbeCount(args, 5), cts.Token);
    Console.WriteLine();
    Console.WriteLine($"'{slug}' için Drops-Enabled canlı yayın: {discovery.Channels.Count} · izleme süreli kampanya: {discovery.Campaigns.Count}");
    foreach (var campaign in discovery.Campaigns)
    {
        var ends = campaign.EndsAt is { } e ? e.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?";
        var scope = campaign.Scope == CampaignScope.General ? "GENEL" : $"yayıncı: {string.Join(", ", campaign.Streamers.Select(s => s.DisplayName))}";
        Console.WriteLine($"  [{scope}] {campaign.Name} — bitiş {ends}");
        Console.WriteLine($"      oyun: {campaign.GameName} · hesap bağlantısı: {(campaign.ConnectUrl is null ? "yok" : campaign.ConnectUrl.ToString())}");
        foreach (var reward in campaign.Rewards)
        {
            Console.WriteLine($"      {reward.RequiredMinutes,5} dk  {reward.Name}  [{reward.ImageUrl}]");
        }

        var watchable = discovery.ChannelsByCampaign[campaign.Id].Select(c => c.Login);
        Console.WriteLine($"      izlenebilecek kanallar: {string.Join(", ", watchable)}");
    }

    foreach (var warning in discovery.Warnings)
    {
        Console.WriteLine($"  uyarı: {warning}");
    }

    return 0;
}

// Başsız motor: keşfet → planla → izle → ilerlemeyi yokla → watchdog → (isteğe bağlı) otomatik claim. Ctrl+C ile durur.
// Kullanım: run twitch <oyun-slug> [--autoclaim] [--audio]    (--autoclaim varsayılan KAPALI: hiçbir şey claim edilmez)
async Task<int> RunEngineTwitchAsync(string[] slugs)
{
    var restore = await manager.RestoreAsync(cts.Token);
    if (restore.Session is not { } session)
    {
        Console.WriteLine("Twitch: önce 'login twitch' çalıştırın.");
        return 1;
    }

    var gql = new TwitchGqlClient(
        http, profile, () => session.AccessToken, new GqlHashRegistry(paths.HashesFile), TwitchDeviceId.GetOrCreate(secrets), logger: logger);
    var pubsub = new TwitchPubSubClient(() => session.AccessToken, logger: logger);
    var claimHistory = new JsonLinesLog<ClaimRecord>(paths.HistoryFile);
    var autoClaim = args.Contains("--autoclaim", StringComparer.OrdinalIgnoreCase);
    var claimer = autoClaim ? new TwitchAutoClaimer(new TwitchClaimService(gql), claimHistory, logger: logger) : null;
    var platform = new TwitchDropPlatform(
        gql,
        TwitchConstants.CreateHttpClient(profile),
        session.UserId,
        pubsub,
        claimer,
        args.Contains("--audio", StringComparer.OrdinalIgnoreCase) ? HlsQuality.AudioOnly : HlsQuality.LowestVideo,
        logger: logger);

    var slug = slugs.FirstOrDefault() ?? "rust";
    var engine = new DropEngine(
        platform,
        new EngineOptions { GameSlug = slug },
        alreadyClaimedDropIds: claimHistory.ReadAll().Where(r => r.Outcome != "Failed").Select(r => r.DropId),
        logger: logger);

    var printed = new HashSet<(DateTimeOffset, string)>();
    var lastLine = string.Empty;
    engine.Changed += snapshot =>
    {
        foreach (var e in snapshot.Recent.Where(e => printed.Add((e.At, e.Text))))
        {
            Console.WriteLine($"{e.At.ToLocalTime():HH:mm:ss} [{e.Level}] {e.Text}");
        }

        var line = $"{snapshot.State}: {snapshot.Message}";
        if (line != lastLine)
        {
            lastLine = line;
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} ● {line}");
        }
    };

    // --minutes N: N dakika sonra otomatik durdur (sınama için); verilmezse Ctrl+C'ye kadar çalışır.
    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    if (int.TryParse(OptionValue("--minutes"), out var limitMinutes) && limitMinutes > 0)
    {
        limit.CancelAfter(TimeSpan.FromMinutes(limitMinutes));
    }

    Console.WriteLine($"Motor başlıyor: oyun='{slug}', otomatik claim={(autoClaim ? "AÇIK" : "kapalı")}. Durdurmak için Ctrl+C.");
    await engine.RunAsync(limit.Token);
    Console.WriteLine($"Durdu. Bu çalışmada claim edilen: {engine.Snapshot.ClaimedThisRun}");
    return 0;
}

// U1 deneyi: bir kampanyada kanal izler (Tier A / HLS), her dakika SUNUCU tarafı dakika sayacını okur.
// Kullanım: watch twitch <oyun-slug> [--match kampanya-adı-parçası] [--minutes N] [--audio]
async Task<int> WatchTwitchAsync(string[] slugs)
{
    var restore = await manager.RestoreAsync(cts.Token);
    if (restore.Session is not { } session)
    {
        Console.WriteLine("Twitch: önce 'login twitch' çalıştırın.");
        return 1;
    }

    var gql = new TwitchGqlClient(
        http, profile, () => session.AccessToken, new GqlHashRegistry(paths.HashesFile), TwitchDeviceId.GetOrCreate(secrets), logger: logger);
    var slug = slugs.FirstOrDefault() ?? "rust";
    var match = OptionValue("--match");
    var minutes = int.TryParse(OptionValue("--minutes"), out var m) && m > 0 ? m : 8;
    var audio = args.Contains("--audio", StringComparer.OrdinalIgnoreCase);

    var discovery = await new TwitchCampaignDiscovery(gql, logger).DiscoverAsync(slug, channelsToProbe: 6, cts.Token);
    var campaign = discovery.Campaigns
        .Where(c => match is null || c.Name.Contains(match, StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.Rewards.Min(r => r.RequiredMinutes))
        .FirstOrDefault();
    if (campaign is null)
    {
        Console.WriteLine($"'{slug}' için uygun izleme süreli kampanya bulunamadı (eşleşme: {match ?? "-"}).");
        return 4;
    }

    var channel = discovery.ChannelsByCampaign[campaign.Id].OrderByDescending(c => c.Viewers).First();
    var firstDrop = campaign.Rewards[0];
    Console.WriteLine($"Seçilen kampanya: {campaign.Name} (ilk drop: {firstDrop.Name}, {firstDrop.RequiredMinutes} dk)");
    Console.WriteLine($"Kanal: {channel.DisplayName} ({channel.Viewers} izleyici) · yöntem: HLS {(audio ? "yalnız ses" : "en düşük video")} · süre: {minutes} dk");

    async Task<(TwitchCurrentSession? Session, DropProgress? Progress, string InventoryJson)> PollAsync()
    {
        TwitchCurrentSession? current = null;
        DropProgress? progress = null;
        var inventoryJson = string.Empty;
        try
        {
            using var sessionResult = await gql.ExecuteAsync(GqlOperations.CurrentDrop, TwitchCurrentSessionParser.Variables(channel.Id), cts.Token);
            current = TwitchCurrentSessionParser.Parse(sessionResult.Data);
            if (current is not null)
            {
                var dir = Path.Combine(paths.DataDirectory, "probe");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "watch-session.json"), System.Text.Json.JsonSerializer.Serialize(sessionResult.Data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            using var inventoryResult = await gql.ExecuteAsync(GqlOperations.Inventory, new Dictionary<string, object?> { ["fetchRewardCampaigns"] = false }, cts.Token);
            inventoryJson = System.Text.Json.JsonSerializer.Serialize(inventoryResult.Data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            progress = TwitchInventoryParser.Parse(inventoryResult.Data).InProgress.FirstOrDefault(p => p.CampaignId == campaign.Id && p.DropId == firstDrop.Id);
        }
        catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException or TwitchUnauthorizedException or TwitchTransientException)
        {
            Console.WriteLine($"  (ilerleme okunamadı: {ex.GetType().Name}: {ex.Message})");
        }

        return (current, progress, inventoryJson);
    }

    var baseline = await PollAsync();
    Console.WriteLine($"Başlangıç — sunucu oturumu: {DescribeSession(baseline.Session)} · envanter: {DescribeProgress(baseline.Progress)}");

    var hlsHttp = TwitchConstants.CreateHttpClient(profile);
    var watcher = new TwitchHlsWatcher(gql, hlsHttp, audio ? HlsQuality.AudioOnly : HlsQuality.LowestVideo, logger: logger);
    using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    watchCts.CancelAfter(TimeSpan.FromMinutes(minutes));

    // --pubsub: gerçek zamanlı olayları yazdırır (U7). --autoclaim: drop-claim olayında OTOMATİK CLAIM eder (varsayılan kapalı).
    Task? pubsubRun = null;
    Task? claimRun = null;
    if (args.Contains("--pubsub", StringComparer.OrdinalIgnoreCase) || args.Contains("--autoclaim", StringComparer.OrdinalIgnoreCase))
    {
        var pubsub = new TwitchPubSubClient(() => session.AccessToken, logger: logger);
        pubsub.Subscribe(e => Console.WriteLine($"  [PubSub] {e}"));
        pubsub.StatusChanged += (status, reason) => Console.WriteLine($"  [PubSub] durum: {status} {reason}");
        await pubsub.SetTopicsAsync([TwitchPubSubProtocol.UserDropTopic(session.UserId), TwitchPubSubProtocol.PlaybackTopic(channel.Id)], cts.Token);
        pubsubRun = pubsub.RunAsync(watchCts.Token);

        if (args.Contains("--autoclaim", StringComparer.OrdinalIgnoreCase))
        {
            var claimer = new TwitchAutoClaimer(new TwitchClaimService(gql), new JsonLinesLog<ClaimRecord>(paths.HistoryFile), logger: logger);
            claimer.Attach(pubsub);
            new TwitchClaimFallback(
                session.UserId, claimer.Enqueue, dropId => campaign.Rewards.Any(r => r.Id == dropId) ? campaign.Id : null, logger: logger).Attach(pubsub);
            claimer.Completed += record => Console.WriteLine($"  [CLAIM] {record.Outcome} {record.DropId} ({record.Attempts} deneme) {record.Status}");
            claimRun = claimer.RunAsync(watchCts.Token);
            Console.WriteLine("  Otomatik claim AÇIK.");
        }
    }

    var clock = System.Diagnostics.Stopwatch.StartNew();
    var run = watcher.RunAsync(channel, watchCts.Token);
    var best = baseline.Progress?.WatchedMinutes ?? 0;
    var interval = TimeSpan.FromSeconds(30);

    while (!run.IsCompleted)
    {
        try
        {
            await Task.WhenAny(run, Task.Delay(interval, watchCts.Token));
        }
        catch (OperationCanceledException)
        {
            break;
        }

        interval = TimeSpan.FromSeconds(60);
        if (run.IsCompleted)
        {
            break;
        }

        var poll = await PollAsync();
        best = Math.Max(best, Math.Max(poll.Progress?.WatchedMinutes ?? 0, poll.Session?.CurrentMinutes ?? 0));
        Console.WriteLine(
            $"[{clock.Elapsed:mm\\:ss}] parça={watcher.Stats.SegmentsFetched} ({watcher.Stats.BytesFetched / 1024.0:0} KB, {watcher.Stats.Variant}) · sunucu oturumu: {DescribeSession(poll.Session)} · envanter: {DescribeProgress(poll.Progress)}");
    }

    var result = await run;
    await watchCts.CancelAsync();
    foreach (var background in new[] { pubsubRun, claimRun }.OfType<Task>())
    {
        try
        {
            await background.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    var final = await PollAsync();
    best = Math.Max(best, Math.Max(final.Progress?.WatchedMinutes ?? 0, final.Session?.CurrentMinutes ?? 0));
    if (final.InventoryJson.Length > 0)
    {
        File.WriteAllText(Path.Combine(paths.DataDirectory, "probe", "watch-inventory.json"), final.InventoryJson);
    }

    Console.WriteLine();
    Console.WriteLine($"SONUÇ: izleyici bitiş={result.End} ({result.Reason ?? "-"}) · süre={clock.Elapsed:mm\\:ss} · parça={watcher.Stats.SegmentsFetched} · {watcher.Stats.BytesFetched / 1024.0:0} KB");
    Console.WriteLine($"       sunucu dakika sayacı: başlangıç {baseline.Progress?.WatchedMinutes ?? baseline.Session?.CurrentMinutes ?? 0} → en yüksek {best} dk");
    Console.WriteLine(best > (baseline.Progress?.WatchedMinutes ?? baseline.Session?.CurrentMinutes ?? 0)
        ? "       U1: HLS trafiği drop ilerlemesi SAĞLADI."
        : "       U1: sunucu dakika sayacı ARTMADI (HLS tek başına yetmiyor olabilir).");
    return 0;

    static string DescribeSession(TwitchCurrentSession? s) => s is null ? "yok" : $"{s.CurrentMinutes}/{s.RequiredMinutes} dk";
    static string DescribeProgress(DropProgress? p) => p is null ? "yok" : $"{p.WatchedMinutes}/{p.RequiredMinutes} dk (%{p.Percent:0})";
}

// Salt okunur tarama: verilen oyun slug'larında Drops-Enabled yayınları bulur, kanalların kazanılabilir kampanyalarını
// listeler; izleme süresiyle kazanılanları (requiredMinutesWatched > 0) işaretler.
async Task<int> ScanTwitchAsync(string[] slugs)
{
    var restore = await manager.RestoreAsync(cts.Token);
    if (restore.Session is not { } session)
    {
        Console.WriteLine("Twitch: önce 'login twitch' çalıştırın.");
        return 1;
    }

    var gql = new TwitchGqlClient(
        http, profile, () => session.AccessToken, new GqlHashRegistry(paths.HashesFile), TwitchDeviceId.GetOrCreate(secrets), logger: logger);
    if (slugs.Length == 0)
    {
        slugs = ["rust"];
    }

    var capture = args.Contains("--capture", StringComparer.OrdinalIgnoreCase);
    var indented = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

    void Save(string slug, string name, System.Text.Json.JsonElement data)
    {
        if (!capture)
        {
            return;
        }

        var dir = Path.Combine(paths.DataDirectory, "probe", "capture-" + slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), System.Text.Json.JsonSerializer.Serialize(data, indented));
    }

    foreach (var slug in slugs)
    {
        try
        {
            using var dir = await gql.ExecuteAsync(GqlOperations.GameDirectory, DirectoryVariables(slug), cts.Token);
            Save(slug, "directory", dir.Data);
            var nodes = new List<(string Id, string Login, string Viewers)>();
            if (dir.Data.TryGetProperty("game", out var game) && game.ValueKind == System.Text.Json.JsonValueKind.Object
                && game.TryGetProperty("streams", out var streams) && streams.ValueKind == System.Text.Json.JsonValueKind.Object
                && streams.TryGetProperty("edges", out var edges) && edges.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    if (edge.TryGetProperty("node", out var node) && node.TryGetProperty("broadcaster", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        nodes.Add((b.GetProperty("id").GetString() ?? "", b.TryGetProperty("login", out var l) ? l.GetString() ?? "?" : "?", node.TryGetProperty("viewersCount", out var v) ? v.ToString() : "?"));
                    }
                }
            }
            else
            {
                Console.WriteLine($"{slug,-28} oyun/dizin bulunamadı");
                continue;
            }

            var timeBased = new Dictionary<string, string>();
            foreach (var channel in nodes.Take(ProbeCount(args, 3)))
            {
                using var available = await gql.ExecuteAsync(GqlOperations.AvailableDrops, new Dictionary<string, object?> { ["channelID"] = channel.Id }, cts.Token);
                Save(slug, "available-" + channel.Login, available.Data);
                if (available.Data.TryGetProperty("channel", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Object
                    && ch.TryGetProperty("viewerDropCampaigns", out var vdc) && vdc.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var c in vdc.EnumerateArray())
                    {
                        var watch = c.TryGetProperty("timeBasedDrops", out var tbd) && tbd.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? tbd.EnumerateArray().Where(d => d.TryGetProperty("requiredMinutesWatched", out var m) && m.GetInt32() > 0).ToList()
                            : [];
                        if (watch.Count > 0)
                        {
                            var id = c.GetProperty("id").GetString() ?? "?";
                            var name = c.TryGetProperty("name", out var n) ? n.GetString() : "?";
                            timeBased[id] = $"{name} | {watch.Count} drop, en fazla {watch.Max(d => d.GetProperty("requiredMinutesWatched").GetInt32())} dk | kanal {channel.Login}";
                        }
                    }
                }
            }

            Console.WriteLine($"{slug,-28} yayın={nodes.Count,3}  izleme-süreli kampanya={timeBased.Count}");
            foreach (var entry in timeBased)
            {
                Console.WriteLine($"    {entry.Key}  {entry.Value}");
            }

            if (capture && timeBased.Keys.FirstOrDefault() is { } firstCampaign)
            {
                using var details = await gql.ExecuteAsync(
                    GqlOperations.DropCampaignDetails,
                    new Dictionary<string, object?> { ["channelLogin"] = session.UserId, ["dropID"] = firstCampaign },
                    cts.Token);
                Save(slug, "details-" + firstCampaign[..8], details.Data);
                Console.WriteLine($"    detay kaydedildi ({firstCampaign[..8]})");
            }
        }
        catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException or TwitchUnauthorizedException or TwitchTransientException)
        {
            Console.WriteLine($"{slug,-28} HATA {ex.GetType().Name}: {ex.Message}");
        }
    }

    return 0;
}

// Elle claim / mutasyon yoklaması: claim twitch <dropInstanceID>. Kimlik (positional) KÜÇÜLTÜLMEDEN iletilir; sonuç durumu yazdırılır.
// Sahte kimlikle çalıştırmak zararsızdır (U8: mutasyon hash'i, giriş şekli ve durum değeri sınaması).
async Task<int> ClaimTwitchAsync(string[] ids)
{
    var restore = await manager.RestoreAsync(cts.Token);
    if (restore.Session is not { } session)
    {
        Console.WriteLine("Twitch: önce 'login twitch' çalıştırın.");
        return 1;
    }

    if (ids.Length == 0)
    {
        Console.WriteLine("Kullanım: claim twitch <dropInstanceID>");
        return 1;
    }

    var gql = new TwitchGqlClient(
        http, profile, () => session.AccessToken, new GqlHashRegistry(paths.HashesFile), TwitchDeviceId.GetOrCreate(secrets), logger: logger);
    var service = new TwitchClaimService(gql);
    var raw = args.Contains("--raw", StringComparer.OrdinalIgnoreCase);
    foreach (var id in ids)
    {
        try
        {
            if (raw)
            {
                // Ham cevap yoklaması: yalnızca 'data' gövdesi yazdırılır (durum değeri; sır içermez). Sonuç ayrıştırılmaz → tek istek.
                using var rawResult = await gql.ExecuteAsync(
                    GqlOperations.ClaimDrop,
                    new Dictionary<string, object?> { ["input"] = new Dictionary<string, object?> { ["dropInstanceID"] = id } },
                    cts.Token);
                Console.WriteLine($"claim {id}: ham data = {rawResult.Data.GetRawText()}");
                continue;
            }

            var result = await service.ClaimAsync(id, cts.Token);
            Console.WriteLine($"claim {id}: {result.Outcome} (durum: {result.Status ?? "-"})");
        }
        catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException or TwitchUnauthorizedException or TwitchTransientException)
        {
            Console.WriteLine($"claim {id}: HATA {ex.GetType().Name}: {ex.Message}");
        }
    }

    return 0;
}

async Task<int> WhoAmITwitchAsync()
{
    var result = await manager.RestoreAsync(cts.Token);
    switch (result.Status)
    {
        case TwitchRestoreStatus.Restored:
            Console.WriteLine($"Twitch: giriş yapılmış — {result.Session!.Login} (kullanıcı no {result.Session.UserId}).");
            return 0;
        case TwitchRestoreStatus.Offline:
            Console.WriteLine("Twitch: durum doğrulanamadı (ağ/sunucu sorunu). Kayıtlı oturum korundu.");
            return 3;
        case TwitchRestoreStatus.NeedsLogin:
            Console.WriteLine("Twitch: oturum geçersizdi ve temizlendi. 'login twitch' ile yeniden giriş yapın.");
            return 2;
        default:
            Console.WriteLine("Twitch: giriş yapılmamış. 'login twitch' çalıştırın.");
            return 1;
    }
}

// --probe N: AvailableDrops'ı kaç kanalda sorgula (varsayılan çağıran verir).
static int ProbeCount(string[] args, int fallback)
{
    var index = Array.FindIndex(args, a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var n) && n is >= 1 and <= 100 ? n : fallback;
}
