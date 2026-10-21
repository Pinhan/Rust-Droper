using System.Collections.ObjectModel;

namespace RustDroper.App.ViewModels;

public sealed record StreamerLink(string Name, Uri Url, bool IsLive);

/// <summary>Drop kartının durumu (ikon/renk/animasyon buna bağlanır).</summary>
public enum DropState
{
    /// <summary>Sırada / kilitli (soluk).</summary>
    Queued,

    /// <summary>Etkinlik henüz başlamadı (kampanya yok; yalnız Facepunch'tan çekilen kart).</summary>
    Waiting,

    /// <summary>Etkinlik bitti (yalnız Facepunch'tan gelen katalog kartı; soluk).</summary>
    Ended,

    /// <summary>Kısmen izlendi, şu an izlenmiyor.</summary>
    InProgress,

    /// <summary>Şu an izleniyor (nabız).</summary>
    Watching,

    /// <summary>Claim'e hak kazanıldı (belirgin ikon + parıltı).</summary>
    ClaimReady,

    /// <summary>Claim edildi (tik).</summary>
    Claimed,
}

/// <summary>Bir drop kartının ham değerleri (<see cref="DropRow.Apply"/> ile kalıcı karta işlenir).</summary>
/// <param name="Percent">0–100 (ilerleme çubuğu değeri).</param>
public sealed record DropRowData(
    string Id,
    string Name,
    double Percent,
    string PercentText,
    string ProgressText,
    string RemainingText,
    string StateText,
    Severity StateSeverity,
    bool IsClaimed,
    bool IsCurrent,
    DropState State = DropState.Queued,
    string DurationText = "",
    string? ImagePath = null,
    Uri? ImageUrl = null,
    string? StreamerName = null,
    Uri? StreamerUrl = null,
    bool StreamerLive = false,
    string? CampaignId = null);

/// <summary>
/// Kalıcı drop kartı (ızgarada tek bir kart). Güncellemeler yerinde yapılır: kart öğeleri yeniden oluşmaz, ilerleme çubuğu akar,
/// görsel yeniden yüklenmez. Hiçbir zamanlayıcı/animasyon içermez (görünüm katmanı yönetir).
/// </summary>
public sealed class DropRow : ViewModelBase
{
    private string _name = string.Empty;
    private double _percent;
    private string _percentText = "%0";
    private string _progressText = string.Empty;
    private string _remainingText = string.Empty;
    private string _stateText = string.Empty;
    private Severity _stateSeverity;
    private bool _isClaimed;
    private bool _isCurrent;
    private DropState _state;
    private string _durationText = string.Empty;
    private string? _imagePath;
    private string? _streamerName;
    private Uri? _streamerUrl;
    private bool _streamerLive;

    public DropRow(string id) => Id = id;

    public string Id { get; }

    /// <summary>Kartın ait olduğu kampanya (yayıncı kartlarının sürükle-bırak öncelik sırası kampanya kimliğiyledir).</summary>
    public string? CampaignId { get; private set; }

    public string Name { get => _name; private set => SetProperty(ref _name, value); }

    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }

    public string PercentText { get => _percentText; private set => SetProperty(ref _percentText, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }

    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }

    public Severity StateSeverity { get => _stateSeverity; private set => SetProperty(ref _stateSeverity, value); }

    public bool IsClaimed { get => _isClaimed; private set => SetProperty(ref _isClaimed, value); }

    public bool IsCurrent { get => _isCurrent; private set => SetProperty(ref _isCurrent, value); }

    public DropState State { get => _state; private set => SetProperty(ref _state, value); }

    /// <summary>"1 sa" gibi süre (kartın alt satırı: "Rug · 1 sa").</summary>
    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }

    /// <summary>Yerel görsel dosyası (önbellek); yoksa kart yer tutucu ikon çizer.</summary>
    public string? ImagePath
    {
        get => _imagePath;
        private set
        {
            if (SetProperty(ref _imagePath, value))
            {
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => !string.IsNullOrEmpty(_imagePath);

    public string? StreamerName
    {
        get => _streamerName;
        private set
        {
            if (SetProperty(ref _streamerName, value))
            {
                OnPropertyChanged(nameof(HasStreamer));
                OnPropertyChanged(nameof(IsDraggable));
            }
        }
    }

    public Uri? StreamerUrl { get => _streamerUrl; private set => SetProperty(ref _streamerUrl, value); }

    public bool StreamerLive { get => _streamerLive; private set => SetProperty(ref _streamerLive, value); }

    /// <summary>Yayıncı drop'u: kartta ek satır (yayıncı adı + CANLI noktası + bağlantı).</summary>
    public bool HasStreamer => !string.IsNullOrEmpty(_streamerName);

    /// <summary>Sürükleyerek sıralanabilir (yayıncı drop'ları): kartta tutamaç görünür.</summary>
    public bool IsDraggable => HasStreamer;

    public void Apply(DropRowData data)
    {
        Name = data.Name;
        Percent = data.Percent;
        PercentText = data.PercentText;
        ProgressText = data.ProgressText;
        RemainingText = data.RemainingText;
        StateText = data.StateText;
        StateSeverity = data.StateSeverity;
        IsClaimed = data.IsClaimed;
        IsCurrent = data.IsCurrent;
        State = data.State;
        DurationText = data.DurationText;
        ImagePath = data.ImagePath;
        StreamerName = data.StreamerName;
        StreamerUrl = data.StreamerUrl;
        StreamerLive = data.StreamerLive;
        CampaignId = data.CampaignId;
    }
}

/// <summary>Bir kampanyanın ham değerleri (<see cref="CampaignItem.Apply"/> ile kalıcı öğeye işlenir).</summary>
public sealed record CampaignData(
    string Id,
    string Name,
    string StatusText,
    Severity StatusSeverity,
    string TimeText,
    IReadOnlyList<StreamerLink> Streamers,
    string WatchableText,
    string SummaryText,
    bool IsCurrent,
    IReadOnlyList<DropRowData> Drops);

/// <summary>Kalıcı kampanya öğesi: başlık satırı + drop kartları ızgarası (<see cref="Drops"/>).</summary>
public sealed class CampaignItem : ViewModelBase
{
    private string _name = string.Empty;
    private string _statusText = string.Empty;
    private Severity _statusSeverity;
    private string _timeText = string.Empty;
    private IReadOnlyList<StreamerLink> _streamers = [];
    private string _watchableText = string.Empty;
    private string _summaryText = string.Empty;
    private bool _isCurrent;

    public CampaignItem(string id) => Id = id;

    public string Id { get; }

    public string Name { get => _name; private set => SetProperty(ref _name, value); }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public Severity StatusSeverity { get => _statusSeverity; private set => SetProperty(ref _statusSeverity, value); }

    public string TimeText { get => _timeText; private set => SetProperty(ref _timeText, value); }

    public IReadOnlyList<StreamerLink> Streamers { get => _streamers; private set => SetProperty(ref _streamers, value); }

    public string WatchableText { get => _watchableText; private set => SetProperty(ref _watchableText, value); }

    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }

    public bool IsCurrent { get => _isCurrent; private set => SetProperty(ref _isCurrent, value); }

    public ObservableCollection<DropRow> Drops { get; } = [];

    public void Apply(CampaignData data)
    {
        Name = data.Name;
        StatusText = data.StatusText;
        StatusSeverity = data.StatusSeverity;
        TimeText = data.TimeText;
        Streamers = data.Streamers;
        WatchableText = data.WatchableText;
        SummaryText = data.SummaryText;
        IsCurrent = data.IsCurrent;
        Reconcile.Items(Drops, data.Drops, row => row.Id, d => d.Id, id => new DropRow(id), (row, d) => row.Apply(d));
    }
}

/// <summary>Koleksiyonu kimliğe göre yerinde günceller: var olan öğeler korunur (yeniden oluşmaz), sıra/ekleme/silme uygulanır.</summary>
internal static class Reconcile
{
    public static void Items<TItem, TData>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TData> data,
        Func<TItem, string> itemKey,
        Func<TData, string> dataKey,
        Func<string, TItem> create,
        Action<TItem, TData> apply)
        where TItem : class
    {
        var wanted = new HashSet<string>(data.Select(dataKey), StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(itemKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < data.Count; i++)
        {
            var key = dataKey(data[i]);
            var existing = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(itemKey(target[j]), key, StringComparison.Ordinal))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                target.Insert(i, create(key));
            }
            else if (existing != i)
            {
                target.Move(existing, i);
            }

            apply(target[i], data[i]);
        }
    }
}

/// <summary>Bir claim edilmiş ödülün ham değerleri (<see cref="ClaimedItem.Apply"/> ile kalıcı karta işlenir).</summary>
public sealed record ClaimedData(string Id, string Name, string SourceText, string DetailText, string? ImagePath);

/// <summary>
/// "Claim edilen ödüller" kartı (kalıcı: güncellemeler yerinde; görsel yeniden yüklenmez).
/// <see cref="SourceText"/>: oyun adı varsa o, yoksa Facepunch bağlantılı ödülde "Rust · Facepunch", yoksa platform adı.
/// <see cref="DetailText"/>: "×N · 22 Eki 2026" (sayı yalnız 1'den çoksa; tarih biliniyorsa).
/// </summary>
public sealed class ClaimedItem : ViewModelBase
{
    private string _name = string.Empty;
    private string _sourceText = string.Empty;
    private string _detailText = string.Empty;
    private string? _imagePath;

    public ClaimedItem(string id) => Id = id;

    public string Id { get; }

    public string Name { get => _name; private set => SetProperty(ref _name, value); }

    public string SourceText { get => _sourceText; private set => SetProperty(ref _sourceText, value); }

    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }

    public string? ImagePath
    {
        get => _imagePath;
        private set
        {
            if (SetProperty(ref _imagePath, value))
            {
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => !string.IsNullOrEmpty(_imagePath);

    public void Apply(ClaimedData data)
    {
        Name = data.Name;
        SourceText = data.SourceText;
        DetailText = data.DetailText;
        ImagePath = data.ImagePath;
    }
}

public sealed record EventItem(string TimeText, Severity Severity, string Text);
