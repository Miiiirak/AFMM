using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Abiotic Factor Mod Manager Launcher")]
[assembly: AssemblyProduct("Abiotic Factor Mod Manager")]
[assembly: AssemblyVersion("0.0.1.0")]
[assembly: AssemblyFileVersion("0.0.1.0")]
[assembly: AssemblyInformationalVersion("0.0.1")]

internal static class LaunchApp
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    private static void ShowError(string message)
    {
        MessageBox(IntPtr.Zero, message, "Abiotic Factor Mod Manager", 0x10);
    }

    [STAThread]
    private static int Main()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var appDirectory = Path.Combine(root, "App");
        var appExecutable = Path.Combine(appDirectory, "AbioticFactorModManager.exe");

        if (!File.Exists(appExecutable))
        {
            ShowError("The application files could not be found. Make sure you extracted the complete download and kept the App folder beside this launcher.");
            return 1;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appExecutable,
                WorkingDirectory = appDirectory,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return 0;
        }
        catch (Win32Exception exception)
        {
            // The user cancelling the application's Windows administrator prompt is not an error.
            if (exception.NativeErrorCode == 1223) return 0;
            ShowError("The manager could not be started.\r\n\r\n" + exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            ShowError("The manager could not be started.\r\n\r\n" + exception.Message);
            return 1;
        }
    }
}
