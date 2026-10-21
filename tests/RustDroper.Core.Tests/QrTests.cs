using RustDroper.Core.Infrastructure;

namespace RustDroper.Core.Tests;

public class QrCodeTests
{
    private const string Link = "https://kick.com/tv/login?uuid=A1B2C3D4-0000-4000-8000-000000000001&code=123456";

    [Fact]
    public void Png_HasPngSignature_AndGrowsWithModuleSize()
    {
        var small = QrCodes.ToPng(Link, 4);
        var large = QrCodes.ToPng(Link, 12);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], small.Take(8));
        Assert.True(large.Length > small.Length);
    }

    [Fact]
    public void TerminalText_IsSquareishBlock_WithAnsiColors_AndNoGaps()
    {
        var text = QrCodes.ToTerminalText(Link);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length >= 15);
        Assert.All(lines, l => Assert.StartsWith("\u001b[30;47m", l, StringComparison.Ordinal));
        var widths = lines.Select(l => l.Replace("\u001b[30;47m", string.Empty).Replace("\u001b[0m", string.Empty).Length).Distinct().ToList();
        Assert.Single(widths); // her satır aynı genişlikte
        Assert.Contains('█', text);
    }

    [Fact]
    public void SameInput_GivesSameOutput_DifferentInput_Differs()
    {
        Assert.Equal(QrCodes.ToTerminalText(Link), QrCodes.ToTerminalText(Link));
        Assert.NotEqual(QrCodes.ToTerminalText(Link), QrCodes.ToTerminalText(Link + "7"));
    }
}
