using Microsoft.Win32;
using RustDroper.Core.Localization;

namespace RustDroper.App.Infrastructure;

/// <summary>"Windows ile başlat" (HKCU\...\Run). Uygulamanın kayıt defterine yazdığı tek yer.</summary>
public interface IAutostart
{
    bool IsEnabled { get; }

    /// <summary>Kayıt ekler/siler. Başlatılabilir bir exe yolu yoksa (ör. <c>dotnet.exe</c> ile geliştirme) <see cref="InvalidOperationException"/> fırlatır.</summary>
    void SetEnabled(bool enabled);
}

public sealed class RegistryAutostart(string keyPath, string valueName, Func<string?> executablePath) : IAutostart
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static RegistryAutostart ForCurrentProcess() =>
        new(DefaultKeyPath, "RustDroper", () => IsApphost(Environment.ProcessPath) ? Environment.ProcessPath : null);

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string { Length: > 0 };
        }
    }

    /// <summary>Kayıt var ama başka bir exe'ye işaret ediyorsa (klasör taşındı) güncel yola çeker.</summary>
    public void Repair()
    {
        if (executablePath() is not { } path)
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue(valueName) is string current && !string.Equals(current, Quote(path), StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(valueName, Quote(path), RegistryValueKind.String);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var path = executablePath() ?? throw new InvalidOperationException(Loc.T("err.autostart.path"));
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(valueName, Quote(path), RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static bool IsApphost(string? path) =>
        !string.IsNullOrEmpty(path)
        && !string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
}
