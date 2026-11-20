using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace AbioticFactorModManager;

public partial class App : Application
{
    private Window? _window;
    public static MainWindow? MainWindow { get; private set; }

    private static void AppendStartupLog(Exception exception)
    {
        try
        {
            var appRoot = AppContext.BaseDirectory;
            var parent = Directory.GetParent(appRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) &&
                File.Exists(Path.Combine(parent, "AbioticFactorModManager.exe")) &&
                File.Exists(Path.Combine(parent, "SteamLaunchWrapper.ps1")) &&
                File.Exists(Path.Combine(parent, "Uninstall.ps1")))
            {
                appRoot = parent;
            }
            var logDirectory = Path.Combine(appRoot, "Data", "Logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "WinUI-startup.log"),
                DateTime.Now + Environment.NewLine + exception + Environment.NewLine);
        }
        catch { }
    }
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try { AppendStartupLog(e.Exception); } catch { }
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            _window = MainWindow;
            _window.Activate();
        }
        catch (Exception ex)
        {
            try { AppendStartupLog(ex); } catch { }
            throw;
        }
    }
}