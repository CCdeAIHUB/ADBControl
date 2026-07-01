using ADBControl.Desktop.Views;
using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace ADBControl.Desktop;

public sealed partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADBControl",
            "logs");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "desktop-crash.log");
        var entry = $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(path, entry);
    }

}
