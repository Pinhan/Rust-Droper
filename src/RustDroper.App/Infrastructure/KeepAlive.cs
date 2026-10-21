using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RustDroper.App.Infrastructure;

/// <summary>Son çalışma durumu (<c>data\run-state.json</c>): motor çalışıyor muydu, kapanış kullanıcı isteğiyle (temiz) miydi.</summary>
public sealed record RunState(bool Running, bool CleanExit);

/// <summary>Başlangıçta ne yapılacağı.</summary>
public enum StartupMode
{
    /// <summary>Normal açılış (pencere ayarlara göre).</summary>
    Normal,

    /// <summary>Gözcü (<c>--keepalive</c>) başlattı ama yapılacak iş yok: sessizce çık, pencere gösterme.</summary>
    ExitSilently,

    /// <summary>Önceki çalışma beklenmedik bitti: tepsiye küçülü aç ve motoru kendiliğinden yeniden başlat.</summary>
    ResumeMinimized,
}

public static class StartupPlanner
{
    public const string KeepAliveArgument = "--keepalive";

    /// <param name="keepAlive"><c>--keepalive</c> ile (zamanlanmış görev) başlatıldı.</param>
    /// <param name="prior">Önceki çalışmanın durumu (dosya yoksa <c>null</c>).</param>
    public static StartupMode Decide(bool keepAlive, RunState? prior)
    {
        var unexpectedStop = prior is { Running: true, CleanExit: false };
        if (keepAlive)
        {
            return unexpectedStop ? StartupMode.ResumeMinimized : StartupMode.ExitSilently;
        }

        // Elle/Windows ile açılış: önceki çalışma çalışırken beklenmedik bittiyse (çökme, yeniden başlatma) kaldığı yerden devam et.
        return unexpectedStop ? StartupMode.ResumeMinimized : StartupMode.Normal;
    }
}

/// <summary>Gözcü başlatmalarının kısa tanı günlüğü (<c>data\keepalive.log</c>, son ~100 satır): çökme sonrası yeniden başlatma zincirini sonradan incelemek için.</summary>
public static class KeepAliveLog
{
    public static void Append(string path, string line)
    {
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).TakeLast(99).ToList() : [];
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Çalışma durumunu saklar. Hata fırlatmaz (kritik olmayan).</summary>
public sealed class RunStateStore(string path)
{
    /// <summary>Son <see cref="Load"/> başarısızsa nedeni (tanı için); aksi halde <c>null</c>.</summary>
    public string? LastLoadError { get; private set; }

    public RunState? Load()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                var names = Directory.Exists(dir) ? string.Join(",", Directory.EnumerateFiles(dir).Select(Path.GetFileName).Take(30)) : "(klasör yok)";
                LastLoadError = $"dosya yok: {path}; klasördeki dosyalar: {names}";
                return null;
            }

            return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastLoadError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    public void Save(RunState state)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Çöküş sonrası yeniden başlatmayı sağlayan zamanlanmış görev (yalnız motor çalışırken var; Duraklat/Çıkış'ta silinir).</summary>
public interface IKeepAliveTask
{
    void Ensure(string exePath);

    void Remove();
}

/// <summary>
/// <c>schtasks</c> ile kullanıcıya özel, yükseltmesiz görev: 5 dakikada bir <c>RustDroper.exe --keepalive</c> çalıştırır. Uygulama çalışıyorsa yeni örnek
/// hemen (sessizce) kapanır; çökmüşse ve motor çalışıyorduysa tepside kendiliğinden yeniden başlar.
/// </summary>
public sealed class SchtasksKeepAliveTask(ILogger? logger = null) : IKeepAliveTask
{
    public const string TaskName = "RustDroperKeepAlive";

    public void Ensure(string exePath) =>
        Run($"/Create /TN {TaskName} /TR \"\\\"{exePath}\\\" {StartupPlanner.KeepAliveArgument}\" /SC MINUTE /MO 5 /F");

    public void Remove() => Run($"/Delete /TN {TaskName} /F");

    private void Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger?.LogWarning(ex, "Zamanlanmış görev (gözcü) ayarlanamadı");
        }
    }
}

/// <summary>
/// Motorun çalışma niyetini izler: çalışırken durum dosyasına "çalışıyor" yazar ve (ayar açıksa) gözcü görevi kurar; kullanıcı Duraklat/Çıkış
/// yapınca görevi siler ve temiz kapanış kaydeder. Windows kapanması/çökme "çalışıyor" bırakır → bir sonraki açılış devam eder.
/// </summary>
public sealed class KeepAliveCoordinator(RunStateStore store, IKeepAliveTask task, Func<bool> autoRestartEnabled, string exePath, ILogger? logger = null)
{
    private bool? _running;
    private bool _frozen;

    /// <summary>Windows kapanırken: sonraki durum değişikliklerini (motorun durdurulması) kaydetme; açılışta devam edilsin.</summary>
    public void Freeze() => _frozen = true;

    /// <summary>Motor durumu değişince çağrılır (yinelenen çağrılar yok sayılır).</summary>
    public void OnRunningChanged(bool running)
    {
        if (_frozen || _running == running)
        {
            return;
        }

        _running = running;
        store.Save(new RunState(running, CleanExit: !running));
        if (running && autoRestartEnabled())
        {
            task.Ensure(exePath);
            logger?.LogInformation("Çökme koruması: gözcü görev kuruldu (5 dk).");
        }
        else
        {
            task.Remove();
        }
    }

    /// <summary>Ayar değişince: motor çalışıyorsa görevi kur/sil.</summary>
    public void OnSettingChanged()
    {
        if (_running == true)
        {
            if (autoRestartEnabled())
            {
                task.Ensure(exePath);
            }
            else
            {
                task.Remove();
            }
        }
    }

    /// <summary>Kullanıcı çıkışı (tepsi Çıkış): temiz kapanış; görev silinir.</summary>
    public void OnUserExit()
    {
        store.Save(new RunState(false, CleanExit: true));
        task.Remove();
        _running = false;
    }

    /// <summary>İşlenmemiş ölümcül istisna: durum "çalışıyor/temiz değil" kalır ve gözcüden bağımsız olarak kısa gecikmeyle yeniden başlatılır.</summary>
    public void RelaunchAfterCrash()
    {
        if (_running != true || !autoRestartEnabled())
        {
            return;
        }

        try
        {
            // Ölen süreç tek-örnek kilidini bırakana kadar bekle (6 sn), sonra yeniden aç.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 6 /nobreak >nul & start \"\" \"{exePath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger?.LogError(ex, "Çökme sonrası yeniden başlatma başlatılamadı");
        }
    }
}
