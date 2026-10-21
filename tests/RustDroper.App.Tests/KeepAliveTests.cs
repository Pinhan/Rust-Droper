using RustDroper.App.Infrastructure;

namespace RustDroper.App.Tests;

public sealed class KeepAliveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rd-keepalive-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTask _task = new();
    private readonly RunStateStore _store;
    private bool _enabled = true;

    public KeepAliveTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new RunStateStore(Path.Combine(_dir, "run-state.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeTask : IKeepAliveTask
    {
        public int Ensures { get; private set; }

        public int Removes { get; private set; }

        public void Ensure(string exePath) => Ensures++;

        public void Remove() => Removes++;
    }

    private KeepAliveCoordinator Coordinator() => new(_store, _task, () => _enabled, "C:\\x\\RustDroper.exe");

    [Theory]
    [InlineData(false, null, StartupMode.Normal)]
    [InlineData(false, false, StartupMode.Normal)] // önceki çalışma durmuştu
    [InlineData(true, null, StartupMode.ExitSilently)] // gözcü ama yapılacak iş yok
    public void Planner_NoUnexpectedStop(bool keepAlive, bool? wasRunning, StartupMode expected) =>
        Assert.Equal(expected, StartupPlanner.Decide(keepAlive, wasRunning is null ? null : new RunState(wasRunning.Value, CleanExit: false)));

    [Fact]
    public void Planner_UserExitedCleanly_DoesNotRestart()
    {
        Assert.Equal(StartupMode.ExitSilently, StartupPlanner.Decide(true, new RunState(false, true)));
        Assert.Equal(StartupMode.Normal, StartupPlanner.Decide(false, new RunState(false, true)));
        Assert.Equal(StartupMode.ExitSilently, StartupPlanner.Decide(true, new RunState(true, true))); // temiz kapanış her zaman kazanır
    }

    [Fact]
    public void Planner_UnexpectedStopWhileRunning_ResumesMinimized()
    {
        var crashed = new RunState(true, CleanExit: false);
        Assert.Equal(StartupMode.ResumeMinimized, StartupPlanner.Decide(true, crashed));
        Assert.Equal(StartupMode.ResumeMinimized, StartupPlanner.Decide(false, crashed));
    }

    [Fact]
    public void Running_SetsTaskAndMarker_PauseRemovesBoth()
    {
        var coordinator = Coordinator();
        coordinator.OnRunningChanged(true);
        coordinator.OnRunningChanged(true); // yinelenen çağrı yok sayılır

        Assert.Equal(1, _task.Ensures);
        Assert.Equal(new RunState(true, false), _store.Load());

        coordinator.OnRunningChanged(false);
        Assert.Equal(1, _task.Removes);
        Assert.Equal(new RunState(false, true), _store.Load()); // Duraklat = temiz
    }

    [Fact]
    public void UserExit_ClearsTaskAndMarksClean_EvenWhileRunning()
    {
        var coordinator = Coordinator();
        coordinator.OnRunningChanged(true);
        coordinator.OnUserExit();
        coordinator.OnRunningChanged(false); // çıkış sırasında motor durur: yeniden yazılmaz

        Assert.Equal(new RunState(false, true), _store.Load());
        Assert.Equal(StartupMode.ExitSilently, StartupPlanner.Decide(true, _store.Load()));
    }

    [Fact]
    public void WindowsShutdown_FreezesState_SoNextBootResumes()
    {
        var coordinator = Coordinator();
        coordinator.OnRunningChanged(true);
        coordinator.Freeze();
        coordinator.OnRunningChanged(false); // kapanırken motor durdurulur: kaydedilmemeli

        Assert.Equal(new RunState(true, false), _store.Load());
        Assert.Equal(StartupMode.ResumeMinimized, StartupPlanner.Decide(false, _store.Load()));
        Assert.Equal(0, _task.Removes); // görev yerinde kalır
    }

    [Fact]
    public void Disabled_NeverCreatesTask_AndSettingToggleAppliesWhileRunning()
    {
        _enabled = false;
        var coordinator = Coordinator();
        coordinator.OnRunningChanged(true);
        Assert.Equal(0, _task.Ensures);

        _enabled = true;
        coordinator.OnSettingChanged();
        Assert.Equal(1, _task.Ensures);

        _enabled = false;
        coordinator.OnSettingChanged();
        Assert.True(_task.Removes >= 1);
    }

    [Fact]
    public void CorruptStateFile_IsTreatedAsNoState()
    {
        File.WriteAllText(Path.Combine(_dir, "run-state.json"), "{bozuk");
        Assert.Null(_store.Load());
        Assert.Equal(StartupMode.ExitSilently, StartupPlanner.Decide(true, _store.Load()));
    }
}
