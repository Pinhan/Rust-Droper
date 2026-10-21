using Microsoft.Win32;
using RustDroper.App.Infrastructure;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;

namespace RustDroper.App.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void AutoClaim_DefaultOn_AndToggleIsPersisted()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var vm = new SettingsViewModel(settings, new FakeAutostart());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.AutoClaim);
        vm.AutoClaim = false;

        Assert.False(settings.Current.AutoClaim);
        Assert.False(new SettingsService(dir.File("settings.json")).Current.AutoClaim);
        Assert.Contains(nameof(SettingsViewModel.AutoClaim), changed);
    }

    [Fact]
    public void StartWithWindows_UpdatesAutostartAndSettings()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var autostart = new FakeAutostart();
        var vm = new SettingsViewModel(settings, autostart);

        vm.StartWithWindows = true;
        Assert.True(autostart.IsEnabled);
        Assert.True(settings.Current.StartWithWindows);

        vm.StartWithWindows = false;
        Assert.False(autostart.IsEnabled);
        Assert.False(settings.Current.StartWithWindows);
    }

    [Fact]
    public void StartWithWindows_FailureKeepsSettingOffAndShowsError()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var vm = new SettingsViewModel(settings, new FakeAutostart { FailWith = new InvalidOperationException("exe yok") });

        vm.StartWithWindows = true;

        Assert.False(settings.Current.StartWithWindows);
        Assert.False(vm.StartWithWindows);
        Assert.Contains("exe yok", vm.Error);
    }
}

public class RelayCommandTests
{
    [Fact]
    public async Task AsyncCommand_BlocksReentry_UntilFinished()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var command = new RelayCommand(async _ =>
        {
            runs++;
            await gate.Task;
        });

        command.Execute(null);
        Assert.False(command.CanExecute(null));
        command.Execute(null); // yutulmalı
        gate.SetResult();
        await Task.Delay(50);

        Assert.Equal(1, runs);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task FailingCommand_ReportsViaFailed_AndDoesNotThrow()
    {
        var command = new RelayCommand(_ => throw new InvalidOperationException("boom"));
        Exception? seen = null;
        command.Failed += ex => seen = ex;

        command.Execute(null);
        await Task.Delay(50);

        Assert.IsType<InvalidOperationException>(seen);
        Assert.True(command.CanExecute(null));
    }
}

public class RegistryAutostartTests
{
    [Fact]
    public void SetEnabled_WritesAndRemovesRunValue()
    {
        var keyPath = @"Software\RustDroperTests\" + Guid.NewGuid().ToString("N");
        try
        {
            var autostart = new RegistryAutostart(keyPath, "RustDroper", () => @"C:\Apps\Rust Droper\RustDroper.exe");

            Assert.False(autostart.IsEnabled);
            autostart.SetEnabled(true);
            Assert.True(autostart.IsEnabled);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\"C:\\Apps\\Rust Droper\\RustDroper.exe\"", key!.GetValue("RustDroper"));
            }

            autostart.SetEnabled(false);
            Assert.False(autostart.IsEnabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void SetEnabled_WithoutLaunchableExe_Throws()
    {
        var autostart = new RegistryAutostart(@"Software\RustDroperTests\never", "RustDroper", () => null);

        Assert.Throws<InvalidOperationException>(() => autostart.SetEnabled(true));
    }

    [Fact]
    public void Repair_RewritesStalePath()
    {
        var keyPath = @"Software\RustDroperTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("RustDroper", "\"D:\\old\\RustDroper.exe\"");
            }

            new RegistryAutostart(keyPath, "RustDroper", () => @"E:\new\RustDroper.exe").Repair();

            using var check = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.Equal("\"E:\\new\\RustDroper.exe\"", check!.GetValue("RustDroper"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void SecondInstance_CannotAcquire_AndSignalsFirst()
    {
        var key = "test-" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstanceGuard(key);
        Assert.True(first.TryAcquire());
        using var signalled = new ManualResetEventSlim(false);
        first.StartListening(signalled.Set);

        using var second = new SingleInstanceGuard(key);
        Assert.False(second.TryAcquire());
        Assert.True(second.SignalExisting());

        Assert.True(signalled.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SignalExisting_WithoutRunningInstance_ReturnsFalse()
    {
        using var guard = new SingleInstanceGuard("test-" + Guid.NewGuid().ToString("N"));

        Assert.False(guard.SignalExisting());
    }

    [Fact]
    public void KeyFor_IsStableAndPerDirectory()
    {
        Assert.Equal(SingleInstanceGuard.KeyFor(@"C:\A\data"), SingleInstanceGuard.KeyFor(@"c:\a\data\"));
        Assert.NotEqual(SingleInstanceGuard.KeyFor(@"C:\A\data"), SingleInstanceGuard.KeyFor(@"C:\B\data"));
    }
}
