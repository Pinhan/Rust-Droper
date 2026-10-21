using RustDroper.App.Infrastructure;

namespace RustDroper.App.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rd-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Anında çalıştıran arayüz iş parçacığı sahtesi.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeAutostart : IAutostart
{
    public bool IsEnabled { get; private set; }

    public Exception? FailWith { get; set; }

    public void SetEnabled(bool enabled)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        IsEnabled = enabled;
    }
}

internal sealed class RecordingBrowser : IBrowserLauncher
{
    public List<Uri> Opened { get; } = [];

    public void Open(Uri url) => Opened.Add(url);
}
