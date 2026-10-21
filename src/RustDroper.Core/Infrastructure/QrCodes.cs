using System.Text;
using QRCoder;

namespace RustDroper.Core.Infrastructure;

/// <summary>
/// Tamamen yerel QR üretimi (içerik üçüncü taraf bir servise gitmez: Kick giriş bağlantısındaki uuid+kod, onaylanırsa hesaba
/// erişim sağlayan tek kullanımlık bir eşleşmedir ve gizli tutulmalıdır).
/// </summary>
public static class QrCodes
{
    /// <summary>QR'ı PNG olarak üretir (siyah modüller beyaz zeminde, sessiz bölge dahil).</summary>
    public static byte[] ToPng(string text, int pixelsPerModule = 10)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Terminal için QR (yarım blok karakterleri; iki modül satırı = bir metin satırı). Doğru kutupluluk için beyaz zemin + siyah
    /// ön plan ANSI renkleri kullanılır; renk desteklemeyen terminalde <see cref="ToPng"/> dosyası kullanılmalıdır.
    /// </summary>
    public static string ToTerminalText(string text, int quietZone = 2)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var total = size + (2 * quietZone);

        bool Dark(int row, int column)
        {
            var r = row - quietZone;
            var c = column - quietZone;
            return r >= 0 && r < size && c >= 0 && c < size && matrix[r][c];
        }

        var builder = new StringBuilder();
        for (var row = 0; row < total; row += 2)
        {
            builder.Append("\u001b[30;47m");
            for (var column = 0; column < total; column++)
            {
                var top = Dark(row, column);
                var bottom = row + 1 < total && Dark(row + 1, column);
                builder.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }

            builder.Append("\u001b[0m\n");
        }

        return builder.ToString();
    }
}
