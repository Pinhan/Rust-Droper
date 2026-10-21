using System.Globalization;
using System.Text.RegularExpressions;

namespace RustDroper.Core.Watching;

/// <param name="HasVideo">RESOLUTION bildirmeyen varyant "yalnız ses" (audio_only) kabul edilir.</param>
public sealed record HlsVariant(string Name, int Bandwidth, bool HasVideo, Uri Uri);

public sealed record HlsSegment(long Sequence, double DurationSeconds, Uri Uri, bool IsPrefetch);

public sealed record HlsMediaPlaylist(double TargetDurationSeconds, long MediaSequence, bool Ended, IReadOnlyList<HlsSegment> Segments);

/// <summary>Twitch'in kullandığı HLS alt kümesi için hafif ve toleranslı ayrıştırıcı (harici kütüphane yok).</summary>
public static partial class HlsParser
{
    public static IReadOnlyList<HlsVariant> ParseMaster(string text, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseUri);
        var variants = new List<HlsVariant>();
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = ParseAttributes(lines[i]["#EXT-X-STREAM-INF:".Length..]);
            var uriLine = lines.Skip(i + 1).FirstOrDefault(l => !l.StartsWith('#'));
            if (uriLine is null || !Uri.TryCreate(baseUri, uriLine, out var uri))
            {
                continue;
            }

            attributes.TryGetValue("BANDWIDTH", out var bandwidthText);
            _ = int.TryParse(bandwidthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bandwidth);
            var name = attributes.GetValueOrDefault("VIDEO") ?? attributes.GetValueOrDefault("NAME") ?? bandwidth.ToString(CultureInfo.InvariantCulture);
            variants.Add(new HlsVariant(name, bandwidth, attributes.ContainsKey("RESOLUTION"), uri));
        }

        return variants;
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseUri);
        double target = 0;
        long mediaSequence = 0;
        var ended = false;
        double? pendingDuration = null;
        var segments = new List<HlsSegment>();

        foreach (var line in SplitLines(text))
        {
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                _ = double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out target);
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                _ = long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSequence);
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..].Split(',')[0];
                pendingDuration = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            }
            else if (line.StartsWith("#EXT-X-TWITCH-PREFETCH:", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(baseUri, line["#EXT-X-TWITCH-PREFETCH:".Length..], out var prefetch))
                {
                    segments.Add(new HlsSegment(mediaSequence + segments.Count, 0, prefetch, IsPrefetch: true));
                }
            }
            else if (line == "#EXT-X-ENDLIST")
            {
                ended = true;
            }
            else if (!line.StartsWith('#') && pendingDuration is { } duration)
            {
                if (Uri.TryCreate(baseUri, line, out var uri))
                {
                    segments.Add(new HlsSegment(mediaSequence + segments.Count, duration, uri, IsPrefetch: false));
                }

                pendingDuration = null;
            }
        }

        return new HlsMediaPlaylist(target, mediaSequence, ended, segments);
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern().Matches(text))
        {
            result[match.Groups["key"].Value] = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
        }

        return result;
    }

    [GeneratedRegex("""(?<key>[A-Z0-9\-]+)=(?:"(?<quoted>[^"]*)"|(?<plain>[^,]*))""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();
}
