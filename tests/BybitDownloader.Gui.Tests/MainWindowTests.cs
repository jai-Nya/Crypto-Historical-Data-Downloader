using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BybitDownloader.Gui.ViewModels;

namespace BybitDownloader.Gui.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindow_LoadsXamlAndBindsToTheViewModel()
    {
        var vm = new MainWindowViewModel(new FakeDownloadService(), post: a => a());
        var window = new MainWindow { DataContext = vm };

        window.Show();

        Assert.True(window.IsVisible);
        Assert.Same(vm, window.DataContext);
        Assert.NotNull(window.Content);

        window.Close();
    }
}
