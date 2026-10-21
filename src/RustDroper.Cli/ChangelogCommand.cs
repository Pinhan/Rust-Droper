using System.Text;
using RustDroper.Core.Changelog;

namespace RustDroper.Cli;

/// <summary>
/// <c>changelog [--json dosya] [--out klasör] [--check]</c>: <c>changelog.json</c>'dan <c>CHANGELOG.md</c> (İngilizce) ve <c>CHANGELOG.tr.md</c> (Türkçe) üretir.
/// <c>--check</c>: dosyaları yazmaz; güncel değilse 1 döner (CI/elle doğrulama). Veri klasörüne/hesaplara dokunmaz.
/// </summary>
internal static class ChangelogCommand
{
    public static int Run(string[] args)
    {
        string? Option(string name) =>
            args.SkipWhile(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();

        var root = FindRepoRoot();
        var json = Option("--json") ?? (root is null ? null : Path.Combine(root, "src", "RustDroper.App", "Resources", "changelog.json"));
        var outDir = Option("--out") ?? root;
        if (json is null || outDir is null || !File.Exists(json))
        {
            Console.WriteLine("changelog.json bulunamadı (depo kökünden çalıştırın ya da --json verin).");
            return 1;
        }

        var parsed = ChangelogParser.Parse(File.ReadAllText(json, Encoding.UTF8));
        foreach (var warning in parsed.Warnings)
        {
            Console.WriteLine($"Uyarı: {warning}");
        }

        if (parsed.Entries.Count == 0)
        {
            Console.WriteLine("Geçerli sürüm girişi yok; dosyalar yazılmadı.");
            return 1;
        }

        var check = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var upToDate = true;
        foreach (var (file, language) in new[] { (ChangelogMarkdown.EnglishFile, "en"), (ChangelogMarkdown.TurkishFile, "tr") })
        {
            var path = Path.Combine(outDir, file);
            var content = ChangelogMarkdown.Render(parsed.Entries, language);
            var current = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
            if (current == content)
            {
                Console.WriteLine($"{file}: güncel.");
                continue;
            }

            upToDate = false;
            if (check)
            {
                Console.WriteLine($"{file}: GÜNCEL DEĞİL (changelog.ps1 çalıştırın).");
            }
            else
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
                Console.WriteLine($"{file}: yazıldı ({parsed.Entries.Count} sürüm).");
            }
        }

        return check && !upToDate ? 1 : 0;
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RustDroper.slnx")))
            {
                dir = dir.Parent;
            }

            if (dir is not null)
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
