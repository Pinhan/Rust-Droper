using RustDroper.App.ViewModels;

namespace RustDroper.App.Tests;

public class AboutTests
{
    [Fact]
    public void CopyMail_PutsExactlyTheContactAddressOnTheClipboard()
    {
        var copied = new List<string>();
        var about = new AboutViewModel();
        about.Configure("C:\\veri", "C:\\veri\\logs", copied.Add, _ => { });

        about.CopyMailCommand.Execute(null);

        Assert.Equal(["thepinhan@gmail.com"], copied);
        Assert.NotNull(about.Copied);
    }

    [Fact]
    public void FolderButtons_AreDisabledUntilConfigured_ThenOpenTheRightFolders()
    {
        var opened = new List<string>();
        var about = new AboutViewModel();
        Assert.False(about.OpenDataFolderCommand.CanExecute(null));

        about.Configure("C:\\veri", "C:\\veri\\logs", _ => { }, opened.Add);
        about.OpenDataFolderCommand.Execute(null);
        about.OpenLogsFolderCommand.Execute(null);

        Assert.Equal(["C:\\veri", "C:\\veri\\logs"], opened);
    }

    [Fact]
    public void AboutText_HasAuthorVersionAndNoSecrets()
    {
        var about = new AboutViewModel();
        Assert.Equal("Rust Droper", about.Title);
        Assert.Contains("Pinhan", about.Developer, StringComparison.Ordinal);
        Assert.Contains("thepinhan@gmail.com", about.Contact, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(AboutViewModel.Version));
        Assert.Contains("bağlantılı değildir", about.Disclaimer, StringComparison.Ordinal);
        Assert.Contains("telemetri", about.Privacy, StringComparison.OrdinalIgnoreCase);
    }
}
