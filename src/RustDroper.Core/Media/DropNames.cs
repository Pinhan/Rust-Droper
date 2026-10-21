using System.Globalization;
using System.Text;

namespace RustDroper.Core.Media;

/// <summary>Drop adı eşleştirme anahtarı: küçük harf, aksansız, yalnız harf/rakam ("Large Wood Box" = "large-wood-box" = "LARGE_WOOD_BOX").</summary>
public static class DropNames
{
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        // "ı" (noktasız i) ve benzeri FormD'de ayrışmaz: ASCII'ye yakın eşleme
        return builder.ToString().Replace('ı', 'i').Replace('ß', 's');
    }
}
