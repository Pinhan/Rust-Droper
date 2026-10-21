using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Configuration;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.Infrastructure;

/// <summary>Kullanıcıya bildirim gösterir (gerçek: tepsi balonu; test: sahte). Arayüz iş parçacığında çağrılır.</summary>
public interface INotifier
{
    void Show(UserNotification notification);
}

/// <summary>
/// Servisten gelen bildirimleri süzer: "Bildirimler" ayarı kapalıysa gösterme, aynı olayı tekrarlama (<see cref="NotificationGate"/>),
/// göstermeyi arayüz iş parçacığına taşı. Bildirim gösterilemese bile uygulama etkilenmez.
/// </summary>
public sealed class NotificationCoordinator
{
    private readonly SettingsService _settings;
    private readonly INotifier _notifier;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly NotificationGate _gate = new();

    public NotificationCoordinator(SettingsService settings, INotifier notifier, IUiDispatcher ui, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(ui);
        _settings = settings;
        _notifier = notifier;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><see cref="DropperService.Notification"/> dinleyicisi. Herhangi bir iş parçacığından çağrılabilir.</summary>
    public void Handle(UserNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // "Bildirimi dene" kullanıcının açık isteğidir: ayar kapalı olsa da gösterilir (sistem ayarlarını denemek için).
        if (notification.Kind != NotificationKind.Test && !_settings.Current.Notifications)
        {
            return;
        }

        if (!_gate.TryPass(notification, _time.GetUtcNow()))
        {
            return;
        }

        _ui.Post(() =>
        {
            try
            {
                _notifier.Show(notification);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Bildirim gösterilemedi ({Kind})", notification.Kind);
            }
        });
    }

    public void ShowTest() => Handle(UserNotification.Test());
}
