using Avalonia;
using Avalonia.Headless;
using BybitDownloader.Gui;

[assembly: AvaloniaTestApplication(typeof(BybitDownloader.Gui.Tests.TestAppBuilder))]

namespace BybitDownloader.Gui.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
