using ADBControl.Desktop.Views;
using Microsoft.UI.Xaml;

namespace ADBControl.Desktop;

public sealed class App : Application
{
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
