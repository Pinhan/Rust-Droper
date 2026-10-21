using System.Text;
using RustDroper.Core.Infrastructure;

namespace RustDroper.Core.Tests;

public class AtomicFileTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static bool StartsWithV(byte[] bytes) => bytes.Length > 0 && bytes[0] == (byte)'v';

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        using var dir = new TempDir();
        AtomicFile.WriteAllBytes(dir.File("a.bin"), Bytes("v1"));

        var read = AtomicFile.ReadWithFallback(dir.File("a.bin"), StartsWithV);

        Assert.Equal(FileSource.Primary, read.Source);
        Assert.Equal("v1", Encoding.UTF8.GetString(read.Content!));
        Assert.Null(read.Warning);
    }

    [Fact]
    public void SecondWrite_KeepsPreviousVersionAsBackup()
    {
        using var dir = new TempDir();
        AtomicFile.WriteAllBytes(dir.File("a.bin"), Bytes("v1"));
        AtomicFile.WriteAllBytes(dir.File("a.bin"), Bytes("v2"));

        Assert.Equal("v2", File.ReadAllText(dir.File("a.bin")));
        Assert.Equal("v1", File.ReadAllText(dir.File("a.bin.bak")));
        Assert.False(File.Exists(dir.File("a.bin.tmp")));
    }

    [Fact]
    public void CorruptPrimary_FallsBackToBackup_QuarantinesCorrupt_AndNextWriteKeepsGoodBackup()
    {
        using var dir = new TempDir();
        AtomicFile.WriteAllBytes(dir.File("a.bin"), Bytes("v1"));
        AtomicFile.WriteAllBytes(dir.File("a.bin"), Bytes("v2"));
        File.WriteAllText(dir.File("a.bin"), "garbage");

        var read = AtomicFile.ReadWithFallback(dir.File("a.bin"), StartsWithV);

        Assert.Equal(FileSource.Backup, read.Source);
        Assert.Equal("v1", Encoding.UTF8.GetString(read.Content!));
        Assert.NotNull(read.Warning);
        Assert.Single(Directory.GetFiles(dir.Path, "a.bin.corrupt-*"));

        AtomicFile.WriteAllBytes(dir.File("a.bin"), Bytes("v3"));
        Assert.Equal("v1", File.ReadAllText(dir.File("a.bin.bak")));
    }

    [Fact]
    public void BothCorrupt_ReturnsMissingWithWarning()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.bin"), "xx");
        File.WriteAllText(dir.File("a.bin.bak"), "yy");

        var read = AtomicFile.ReadWithFallback(dir.File("a.bin"), StartsWithV);

        Assert.Equal(FileSource.Missing, read.Source);
        Assert.Null(read.Content);
        Assert.NotNull(read.Warning);
    }

    [Fact]
    public void MissingFile_IsNotAWarning()
    {
        using var dir = new TempDir();

        var read = AtomicFile.ReadWithFallback(dir.File("nope.bin"), StartsWithV);

        Assert.Equal(FileSource.Missing, read.Source);
        Assert.Null(read.Warning);
    }

    [Fact]
    public void LeftoverTempFile_DoesNotBreakWrite()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.bin.tmp"), "half-written");

        AtomicFile.WriteAllBytes(dir.File("a.bin"), Bytes("v1"));

        Assert.Equal("v1", File.ReadAllText(dir.File("a.bin")));
    }
}

public class AtomicJsonStoreTests
{
    public sealed class Sample
    {
        public string Name { get; set; } = "default";

        public int Count { get; set; }

        public List<string> Items { get; set; } = [];
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDir();
        var store = new AtomicJsonStore<Sample>(dir.File("s.json"), schemaVersion: 1);
        store.Save(new Sample { Name = "x", Count = 3, Items = ["a", "b"] });

        var loaded = new AtomicJsonStore<Sample>(dir.File("s.json"), 1).Load();

        Assert.Equal(FileSource.Primary, loaded.Source);
        Assert.Equal("x", loaded.Value.Name);
        Assert.Equal(["a", "b"], loaded.Value.Items);
    }

    [Fact]
    public void MissingFile_ReturnsDefaultWithoutWarning()
    {
        using var dir = new TempDir();

        var loaded = new AtomicJsonStore<Sample>(dir.File("s.json"), 1).Load();

        Assert.Equal("default", loaded.Value.Name);
        Assert.Equal(FileSource.Missing, loaded.Source);
        Assert.Null(loaded.Warning);
    }

    [Fact]
    public void CorruptPrimary_RecoversFromBackup()
    {
        using var dir = new TempDir();
        var store = new AtomicJsonStore<Sample>(dir.File("s.json"), 1);
        store.Save(new Sample { Name = "good" });
        store.Save(new Sample { Name = "newer" });
        File.WriteAllText(dir.File("s.json"), "{ not json");

        var loaded = store.Load();

        Assert.Equal(FileSource.Backup, loaded.Source);
        Assert.Equal("good", loaded.Value.Name);
        Assert.NotNull(loaded.Warning);
    }

    [Fact]
    public void NewerSchemaVersion_IsNotOverwrittenBlindly_AndFallsBackToDefault()
    {
        using var dir = new TempDir();
        new AtomicJsonStore<Sample>(dir.File("s.json"), schemaVersion: 5).Save(new Sample { Name = "from-future" });

        var loaded = new AtomicJsonStore<Sample>(dir.File("s.json"), schemaVersion: 1).Load();

        Assert.Equal("default", loaded.Value.Name);
        Assert.NotNull(loaded.Warning);
        Assert.Single(Directory.GetFiles(dir.Path, "s.json.corrupt-*"));
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s.json"), """{"schemaVersion":1,"data":{"name":"n","brandNew":{"a":1}},"extra":true}""");

        var loaded = new AtomicJsonStore<Sample>(dir.File("s.json"), 1).Load();

        Assert.Equal("n", loaded.Value.Name);
        Assert.Null(loaded.Warning);
    }

    [Fact]
    public void ConcurrentSaves_LeaveAValidFile()
    {
        using var dir = new TempDir();
        var store = new AtomicJsonStore<Sample>(dir.File("s.json"), 1);

        Parallel.For(0, 40, i => store.Save(new Sample { Name = "n" + i, Count = i }));

        var loaded = store.Load();
        Assert.Equal(FileSource.Primary, loaded.Source);
        Assert.StartsWith("n", loaded.Value.Name, StringComparison.Ordinal);
    }
}

public class SecretStoreTests
{
    [Fact]
    public void Dpapi_PersistsAcrossInstances_AndNeverStoresPlaintext()
    {
        using var dir = new TempDir();
        const string secret = "tok-9f8e7d6c5b4a3210-very-secret";
        new DpapiSecretStore(dir.File("secrets.dat")).Set("twitch.token", secret);

        var again = new DpapiSecretStore(dir.File("secrets.dat"));

        Assert.Equal(secret, again.Get("twitch.token"));
        var raw = File.ReadAllBytes(dir.File("secrets.dat"));
        Assert.DoesNotContain("very-secret", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("twitch.token", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void Dpapi_Remove_Persists()
    {
        using var dir = new TempDir();
        var store = new DpapiSecretStore(dir.File("secrets.dat"));
        store.Set("a", "value-a-123456");
        store.Set("b", "value-b-123456");
        store.Remove("a");

        var again = new DpapiSecretStore(dir.File("secrets.dat"));

        Assert.Null(again.Get("a"));
        Assert.Equal("value-b-123456", again.Get("b"));
    }

    [Fact]
    public void Dpapi_UndecryptableFile_ActsAsEmptyStore_WithoutThrowing()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("secrets.dat"), [1, 2, 3, 4, 5]);

        var store = new DpapiSecretStore(dir.File("secrets.dat"));

        Assert.Null(store.Get("anything"));
        store.Set("k", "value-k-123456");
        Assert.Equal("value-k-123456", new DpapiSecretStore(dir.File("secrets.dat")).Get("k"));
    }

    [Fact]
    public void Masker_HidesRegisteredValuesAndTokenPatterns()
    {
        SecretMasker.Register("my-registered-secret-value");

        var masked = SecretMasker.Apply(
            "ok my-registered-secret-value | Authorization: Bearer abcdefghijklmnop1234 | oauth:zzzzzzzzzzzzzzzz1111 | \"access_token\":\"AAAAAAAAAAAAAAAAAAAA\"");

        Assert.DoesNotContain("my-registered-secret-value", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop1234", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("zzzzzzzzzzzzzzzz1111", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAAAAAAAAAAAAAAAAAA", masked, StringComparison.Ordinal);
        Assert.StartsWith("ok ***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Masker_LeavesOrdinaryTextAlone()
    {
        Assert.Equal("Kampanya yüklendi: 12 öğe", SecretMasker.Apply("Kampanya yüklendi: 12 öğe"));
    }
}
