using RustDroper.Core.Localization;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.ViewModels;

/// <summary>"Şu an" bölümü: motorun hedefi (kanal + kampanya), canlı drop ilerlemesi ve motor durumu rozeti.</summary>
public sealed class NowViewModel : ViewModelBase
{
    private string _badgeText = Loc.T("badge.stopped");
    private Severity _badgeSeverity = Severity.Neutral;
    private string _message = Loc.T("now.notStarted");
    private bool _hasTarget;
    private string _channelName = string.Empty;
    private Uri? _channelUrl;
    private string _campaignName = string.Empty;
    private string _dropName = string.Empty;
    private double _percent;
    private string _percentText = Fmt.Percent(0);
    private string _watchedText = string.Empty;
    private string _requiredText = string.Empty;
    private string _remainingText = string.Empty;
    private string _finishText = string.Empty;
    private string _lastProgressText = string.Empty;
    private DateTimeOffset? _lastProgressAt;
    private string _methodText = "HLS";
    private string _infoText = string.Empty;
    private DateTimeOffset? _lastCheckAt;

    public string BadgeText
    {
        get => _badgeText;
        private set => SetProperty(ref _badgeText, value);
    }

    public Severity BadgeSeverity
    {
        get => _badgeSeverity;
        private set => SetProperty(ref _badgeSeverity, value);
    }

    /// <summary>Motorun durum mesajı (ör. "kanal: 12/180 dk").</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool HasTarget
    {
        get => _hasTarget;
        private set => SetProperty(ref _hasTarget, value);
    }

    public string ChannelName
    {
        get => _channelName;
        private set => SetProperty(ref _channelName, value);
    }

    public Uri? ChannelUrl
    {
        get => _channelUrl;
        private set => SetProperty(ref _channelUrl, value);
    }

    public string CampaignName
    {
        get => _campaignName;
        private set => SetProperty(ref _campaignName, value);
    }

    public string DropName
    {
        get => _dropName;
        private set => SetProperty(ref _dropName, value);
    }

    public double Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => SetProperty(ref _percentText, value);
    }

    public string WatchedText
    {
        get => _watchedText;
        private set => SetProperty(ref _watchedText, value);
    }

    public string RequiredText
    {
        get => _requiredText;
        private set => SetProperty(ref _requiredText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => SetProperty(ref _remainingText, value);
    }

    /// <summary>Sönük bilgi satırı: son kontrol saati + yenileme aralıkları (ayrıntı: <see cref="InfoTooltip"/>).</summary>
    public string InfoText
    {
        get => _infoText;
        private set => SetProperty(ref _infoText, value);
    }

    /// <summary>Gerçek yenileme aralıkları (DropEngine.PollInterval 60 sn, RediscoverInterval 10 dk, IdleRecheck 5 dk; envanter 5 dk; Facepunch sayfası 6 sa).</summary>
    public string InfoTooltip => string.Join(
        Environment.NewLine,
        Loc.T("now.tip.progress"),
        Loc.T("now.tip.campaigns"),
        Loc.T("now.tip.inventory"),
        Loc.T("now.tip.catalog"));

    /// <summary>Kampanyanın tahmini bitişi ("bugün 23:40").</summary>
    public string FinishText
    {
        get => _finishText;
        private set => SetProperty(ref _finishText, value);
    }

    public string LastProgressText
    {
        get => _lastProgressText;
        private set => SetProperty(ref _lastProgressText, value);
    }

    /// <summary>Yöntem etiketi: Twitch HLS akışı; Kick gizli gerçek oynatıcı.</summary>
    public string MethodText
    {
        get => _methodText;
        private set => SetProperty(ref _methodText, value);
    }

    /// <param name="campaignRemainingMinutes">Kampanyada kalan izleme (bilinmiyorsa <c>null</c>: oturumdan hesaplanır).</param>
    public void Update(EngineSnapshot engine, bool running, DateTimeOffset now, TimeZoneInfo zone, int? campaignRemainingMinutes, string? dropName, bool kick = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        MethodText = kick ? Loc.T("now.method.hidden") : "HLS";
        var (text, severity) = Fmt.EngineBadge(engine.State, running);
        BadgeText = text;
        BadgeSeverity = severity;
        Message = running ? engine.Message : Loc.T("now.notStartedPaused");

        var target = running ? engine.Target : null;
        HasTarget = target is not null;
        ChannelName = target?.Channel.DisplayName ?? string.Empty;
        ChannelUrl = target?.Channel.Url;
        CampaignName = target?.Campaign.Name ?? string.Empty;
        DropName = dropName ?? string.Empty;

        var session = running ? engine.Session : null;
        if (session is null)
        {
            Percent = 0;
            PercentText = Fmt.Percent(0);
            WatchedText = string.Empty;
            RequiredText = string.Empty;
            RemainingText = string.Empty;
            FinishText = string.Empty;
        }
        else
        {
            Percent = session.Percent;
            PercentText = Fmt.Percent(session.Percent);
            WatchedText = Fmt.Minutes(session.CurrentMinutes);
            RequiredText = Fmt.Minutes(session.RequiredMinutes);
            RemainingText = Fmt.Minutes(Math.Max(0, session.RequiredMinutes - session.CurrentMinutes));
            var remaining = campaignRemainingMinutes ?? Math.Max(0, session.RequiredMinutes - session.CurrentMinutes);
            FinishText = remaining <= 0 ? Loc.T("now.finish.done") : Fmt.When(now.AddMinutes(remaining), now, zone);
        }

        _lastProgressAt = running ? engine.LastProgressAt : null;
        var lastEvent = engine.Recent.Count > 0 ? engine.Recent[^1].At : (DateTimeOffset?)null;
        _lastCheckAt = running ? (engine.LastProgressAt is { } p && (lastEvent is null || p > lastEvent) ? p : lastEvent) : null;
        InfoText = _lastCheckAt is { } at
            ? Loc.T("now.info.lastCheck", TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", Loc.Culture)) + Loc.T("now.info.intervals")
            : Loc.T("now.info.intervalsAlone");
        Tick(now);
    }

    /// <summary>Göreli süreyi ("12 sn önce") yeniler; pencere açıkken saniyede bir çağrılır.</summary>
    public void Tick(DateTimeOffset now) =>
        LastProgressText = _lastProgressAt is { } at ? Fmt.Ago(now - at) : string.Empty;
}
