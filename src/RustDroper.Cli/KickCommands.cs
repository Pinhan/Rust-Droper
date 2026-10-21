using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;
using RustDroper.Core.Watching;

/// <summary>Kick geliştirici komutları. Token'lar ASLA ekrana yazılmaz; kullanıcı hesaba tarayıcıda kendisi giriş yapar.</summary>
internal sealed class KickCommands(ISecretStore secrets, ILogger logger, bool noBrowser, CancellationToken cancellationToken)
{
    private readonly HttpClient _http = KickConstants.CreateHttpClient();

    public KickSessionManager Manager => new(new KickAuthClient(_http), secrets, logger);

    public async Task<int> RunAsync(string verb, string[] rest, Func<string, string?> option)
    {
        try
        {
            switch (verb)
            {
                case "login":
                    return await LoginAsync();
                case "whoami":
                    return await WhoAmIAsync();
                case "logout":
                    Manager.Logout();
                    Console.WriteLine("Kick oturumu kapatıldı.");
                    return 0;
                case "campaigns":
                    return await CampaignsAsync(rest.FirstOrDefault() ?? "rust");
                case "watch":
                    return await WatchAsync(rest, option);
                case "progress":
                    return await ProgressAsync(option("--campaign"));
                default:
                    Console.WriteLine("Kick komutları: login | whoami | logout | campaigns [oyun] | watch <kanal> --campaign <kimlik> --minutes N | progress --campaign <kimlik>  (hepsi: ... kick)");
                    return 1;
            }
        }
        catch (KickLoginException ex)
        {
            Console.WriteLine($"Kick girişi başarısız ({ex.Failure}): {ex.Message}");
            return 2;
        }
    }

    // Kick'in TV-login bağlantısı masaüstü tarayıcıda 404 verir (gerçek sınama, 2026-10-16): bağlantı telefondaki KICK UYGULAMASIYLA
    // açılmalıdır (referans proje de QR gösterir). Bu yüzden QR yerelde üretilir; `--no-browser` QR resmini açmayı kapatır.
    private async Task<int> LoginAsync()
    {
        string? qrFile = null;
        try
        {
            var session = await Manager.LoginAsync(
                request =>
                {
                    var link = request.VerificationUri.ToString();
                    Console.WriteLine();
                    Console.WriteLine("Kick girişi — TELEFONUNUZDA Kick uygulaması açık ve hesabınızda oturum açıkken şu QR kodu okutun");
                    Console.WriteLine("(bağlantı masaüstü tarayıcıda 404 verir; Kick uygulamasıyla açılmalıdır):");
                    Console.WriteLine();
                    Console.Write(QrCodes.ToTerminalText(link));
                    Console.WriteLine();
                    Console.WriteLine($"  Bağlantı (telefonunuza gönderip Kick uygulamasıyla açabilirsiniz): {link}");
                    Console.WriteLine($"  (Kod {request.Code}; {request.ExpiresIn.TotalMinutes:0} dk geçerli. Onay bekleniyor...)");
                    if (!noBrowser)
                    {
                        qrFile = Path.Combine(Path.GetTempPath(), $"rustdroper-kick-qr-{Guid.NewGuid():N}.png");
                        File.WriteAllBytes(qrFile, QrCodes.ToPng(link));
                        Process.Start(new ProcessStartInfo(qrFile) { UseShellExecute = true });
                    }
                },
                cancellationToken);
            Console.WriteLine($"Kick girişi tamam: {session.Login}.");
            return 0;
        }
        finally
        {
            if (qrFile is not null)
            {
                try
                {
                    File.Delete(qrFile);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task<int> WhoAmIAsync()
    {
        var restore = await Manager.RestoreAsync(cancellationToken);
        switch (restore.Status)
        {
            case KickRestoreStatus.Restored:
                Console.WriteLine($"Kick: {restore.Session!.Login} (oturum geçerli).");
                return 0;
            case KickRestoreStatus.NotLoggedIn:
                Console.WriteLine("Kick: giriş yapılmamış ('login kick' çalıştırın).");
                return 1;
            case KickRestoreStatus.NeedsLogin:
                Console.WriteLine("Kick: kayıtlı oturum geçersiz, yeniden giriş gerekli.");
                return 1;
            default:
                Console.WriteLine("Kick: oturum doğrulanamadı (ağ/sunucu); token korundu.");
                return 3;
        }
    }
    /// <summary>Salt okunur: hesabın bir kampanyadaki sunucu ilerlemesi (<c>progress/summary</c>); uygulama çalışırken dışarıdan ölçmek için.</summary>
    private async Task<int> ProgressAsync(string? campaignId)
    {
        if (string.IsNullOrWhiteSpace(campaignId))
        {
            Console.WriteLine("Kullanım: progress kick --campaign <kampanya-kimliği>");
            return 1;
        }

        var restore = await Manager.RestoreAsync(cancellationToken);
        if (restore.Status != KickRestoreStatus.Restored)
        {
            Console.WriteLine($"Kick oturumu yok/doğrulanamadı ({restore.Status}).");
            return 1;
        }

        var api = new KickApiClient(_http, () => restore.Session!.AccessToken, logger: logger);
        var summary = await api.GetSummaryAsync(campaignId, cancellationToken);
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} progress_units={summary?.ProgressUnits?.ToString() ?? "(kayıt yok)"}");
        return 0;
    }

    // Kamusal keşif (giriş gerekmez): sahne arkası = motorun kullandığı KickDropPlatform keşfi.
    private async Task<int> CampaignsAsync(string game)
    {
        var api = new KickApiClient(_http, () => null, logger: logger);
        var platform = new KickDropPlatform(new KickPublicClient(_http), api, _http, logger: logger);
        var snapshot = await platform.DiscoverAsync(game, cancellationToken);
        Console.WriteLine($"Kick '{game}' kampanyaları: {snapshot.Campaigns.Count}");
        foreach (var campaign in snapshot.Campaigns)
        {
            Console.WriteLine($"  [{campaign.Status}] {campaign.Name}  ({campaign.Scope}, kimlik {campaign.Id}, bitiş {campaign.EndsAt:yyyy-MM-dd HH:mm} UTC)");
            Console.WriteLine($"    ödüller: {string.Join(", ", campaign.Rewards.Select(r => $"{r.Name} ({r.RequiredMinutes} dk)"))}");
            if (snapshot.ChannelsByCampaign.TryGetValue(campaign.Id, out var live))
            {
                Console.WriteLine($"    canlı kanallar: {(live.Count == 0 ? "(yok)" : string.Join(", ", live.Take(6).Select(c => $"{c.Login} ({c.Viewers})")))}");
            }
        }

        return 0;
    }

    // U3 deneyi: HLS izleyici sunucu dakika sayacını (progress_units) ilerletiyor mu? Claim ASLA yapılmaz.
    private async Task<int> WatchAsync(string[] rest, Func<string, string?> option)
    {
        if (rest.Length == 0 || option("--campaign") is not { Length: > 0 } campaignId)
        {
            Console.WriteLine("Kullanım: watch kick <kanal-slug> --campaign <kampanya-kimliği> [--minutes N]");
            return 1;
        }

        var minutes = int.TryParse(option("--minutes"), out var m) && m > 0 ? m : 10;
        var restore = await Manager.RestoreAsync(cancellationToken);
        if (restore.Session is not { } session)
        {
            Console.WriteLine("Kick: önce 'login kick' çalıştırın (oturum yok/geçersiz).");
            return 1;
        }

        var api = new KickApiClient(_http, () => session.AccessToken, logger: logger);
        var info = await api.GetChannelAsync(rest[0], cancellationToken);
        if (info is null || !info.IsLive)
        {
            Console.WriteLine($"Kanal '{rest[0]}' canlı değil.");
            return 1;
        }

        var channel = new RustDroper.Core.Domain.LiveChannel(
            RustDroper.Core.Domain.Platform.Kick, info.Id, info.Slug, info.DisplayName, info.Viewers, info.Title, new Uri(KickConstants.SiteBase, info.Slug), info.AvatarUrl);
        var before = await api.GetSummaryAsync(campaignId, cancellationToken);
        Console.WriteLine($"Başlangıç: progress_units={before?.ProgressUnits?.ToString() ?? "(kayıt yok)"}  — {info.DisplayName} izlenecek, {minutes} dk (HLS, en düşük kalite, claim YOK).");

        using var hls = KickConstants.CreateHttpClient();
        var watcher = new KickHlsWatcher(api, hls, logger: logger);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchTask = watcher.RunAsync(channel, watchCts.Token);
        var end = DateTimeOffset.UtcNow.AddMinutes(minutes);
        var started = DateTimeOffset.UtcNow;
        try
        {
            while (DateTimeOffset.UtcNow < end && !watchTask.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(60), cancellationToken), watchTask);
                var summary = await api.GetSummaryAsync(campaignId, cancellationToken);
                Console.WriteLine(
                    $"  +{(DateTimeOffset.UtcNow - started).TotalMinutes:0.0} dk: progress_units={summary?.ProgressUnits?.ToString() ?? "(kayıt yok)"}  parça={watcher.Stats.SegmentsFetched} ({watcher.Stats.BytesFetched / 1024.0 / 1024.0:0.0} MB) varyant={watcher.Stats.Variant}");
            }
        }
        finally
        {
            await watchCts.CancelAsync();
            var result = await watchTask;
            Console.WriteLine($"İzleyici bitti: {result.End} {result.Reason}");
        }

        var after = await api.GetSummaryAsync(campaignId, cancellationToken);
        Console.WriteLine($"Sonuç: progress_units {before?.ProgressUnits?.ToString() ?? "(yok)"} → {after?.ProgressUnits?.ToString() ?? "(yok)"}");
        return 0;
    }
}
