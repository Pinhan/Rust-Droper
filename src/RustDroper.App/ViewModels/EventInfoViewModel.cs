using System.Text.RegularExpressions;
using RustDroper.Core.Localization;
using RustDroper.Core.Media;

namespace RustDroper.App.ViewModels;

/// <summary>Etkinlik bilgisinin ham değerleri (kampanya verisi ve/veya Facepunch sayfası; tarihler UTC).</summary>
/// <param name="BadgeClass">Tarih yoksa durum yedeği ("is-pending"/"is-active"/"is-ended").</param>
public sealed record EventData(string? Title, string? Detail, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? BadgeClass);

/// <summary>
/// Platform kartının ortasındaki etkinlik bilgisi: durum rozeti (Yakında/Aktif/Bitti), ad, ayrıntı, yerel saatte tarih aralığı, kalan süre.
/// Kalan süre saniye saniye akmaz: <see cref="Tick"/> yalnız dakika değişince yeniden hesaplar (pencere açıkken çağrılır).
/// </summary>
public sealed partial class EventInfoViewModel : ViewModelBase
{
    private EventData? _data;
    private TimeZoneInfo _zone = TimeZoneInfo.Local;
    private long _minute = long.MinValue;
    private bool _hasEvent;
    private string _statusText = string.Empty;
    private Severity _statusSeverity;
    private string _title = string.Empty;
    private string _detailText = string.Empty;
    private string _datesText = string.Empty;
    private string _countdownText = string.Empty;
    private FacepunchEventStatus _status;

    public bool HasEvent { get => _hasEvent; private set => SetProperty(ref _hasEvent, value); }

    /// <summary>"Yakında" / "Aktif" / "Bitti".</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public Severity StatusSeverity { get => _statusSeverity; private set => SetProperty(ref _statusSeverity, value); }

    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(HasTitle));
            }
        }
    }

    public bool HasTitle => _title.Length > 0;

    /// <summary>"Twitch Drops · 54 · Rustoria sunar".</summary>
    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }

    /// <summary>"23 Eki 21:00 → 2 Kas 02:59" (yerel saat).</summary>
    public string DatesText { get => _datesText; private set => SetProperty(ref _datesText, value); }

    /// <summary>"Başlamasına 4 gün 3 sa" / "Bitmesine 3 gün 5 sa" / (bitti: boş).</summary>
    public string CountdownText { get => _countdownText; private set => SetProperty(ref _countdownText, value); }

    public FacepunchEventStatus Status => _status;

    public void Update(EventData? data, DateTimeOffset now, TimeZoneInfo zone)
    {
        _data = data;
        _zone = zone;
        _minute = long.MinValue;
        Refresh(now, force: true);
    }

    /// <summary>Saniyelik pencere zamanlayıcısından çağrılır; dakika değişmediyse hiçbir şey yapmaz.</summary>
    public void Tick(DateTimeOffset now) => Refresh(now, force: false);

    private void Refresh(DateTimeOffset now, bool force)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        if (!force && minute == _minute)
        {
            return;
        }

        _minute = minute;
        if (_data is not { } d)
        {
            HasEvent = false;
            StatusText = Title = DetailText = DatesText = CountdownText = string.Empty;
            _status = FacepunchEventStatus.Unknown;
            return;
        }

        _status = new FacepunchEvent(d.Title, [], null, d.BadgeClass, null, d.StartsAt, d.EndsAt).StatusAt(now);
        (StatusText, StatusSeverity) = _status switch
        {
            FacepunchEventStatus.Upcoming => (Loc.T("event.upcoming"), Severity.Info),
            FacepunchEventStatus.Active => (Loc.T("event.active"), Severity.Ok),
            FacepunchEventStatus.Ended => (Loc.T("event.ended"), Severity.Neutral),
            _ => (string.Empty, Severity.Neutral),
        };
        Title = d.Title ?? string.Empty;
        DetailText = d.Detail ?? string.Empty;
        DatesText = Fmt.DateRange(d.StartsAt, d.EndsAt, now, _zone);
        CountdownText = _status switch
        {
            FacepunchEventStatus.Upcoming when d.StartsAt is { } start => Loc.T("event.startsIn", Fmt.Remaining(start - now)),
            FacepunchEventStatus.Active when d.EndsAt is { } end => Loc.T("event.endsIn", Fmt.Remaining(end - now)),
            _ => string.Empty,
        };
        HasEvent = true;
    }

    /// <summary>Facepunch alt başlığı ("Hosted by Rustoria") ve tur etiketleri → "Twitch Drops · 54 · Rustoria sunar".</summary>
    public static string? DetailFrom(FacepunchEvent e)
    {
        var parts = new List<string>(e.Rounds);
        if (!string.IsNullOrWhiteSpace(e.Subtitle))
        {
            var hosted = HostedBy().Match(e.Subtitle);
            parts.Add(hosted.Success ? Loc.T("event.hostedBy", hosted.Groups[1].Value.Trim()) : e.Subtitle);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    [GeneratedRegex(@"^\s*hosted\s+by\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HostedBy();
}
