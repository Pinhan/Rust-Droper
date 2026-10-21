using RustDroper.App.Infrastructure;

namespace RustDroper.App.Tests;

public class SystemEventsTests
{
    [Fact]
    public void WindowsSystemEvents_CanBeCreatedAndDisposedRepeatedly()
    {
        // Gerçek uyku/uyanış tetiklenemez; en azından Windows olay kancaları kurulup sökülebilmeli ve çift Dispose zararsız olmalı.
        var events = new WindowsSystemEvents();
        var raised = 0;
        events.Suspending += () => raised++;
        events.Resumed += () => raised++;
        events.NetworkAvailabilityChanged += _ => raised++;

        events.Dispose();
        events.Dispose();

        Assert.Equal(0, raised);
    }
}
