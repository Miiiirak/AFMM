using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: AssemblyTitle("Abiotic Factor Mod Manager Steam Launch Host")]
[assembly: AssemblyProduct("Abiotic Factor Mod Manager")]
[assembly: AssemblyVersion("0.0.1.0")]
[assembly: AssemblyFileVersion("0.0.1.0")]
[assembly: AssemblyInformationalVersion("0.0.1")]

internal static class SteamLaunchWrapperHost
{
    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            return value;

        var result = new StringBuilder();
        result.Append('"');
        var backslashes = 0;

        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
            }
            else
            {
                if (backslashes > 0)
                    result.Append('\\', backslashes);
                result.Append(character);
            }

            backslashes = 0;
        }

        if (backslashes > 0)
            result.Append('\\', backslashes * 2);

        result.Append('"');
        return result.ToString();
    }

    private static string JoinArguments(IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(QuoteArgument(argument ?? string.Empty));
        }
        return builder.ToString();
    }

    private static int RunAndWait(ProcessStartInfo startInfo)
    {
        using (var process = Process.Start(startInfo))
        {
            if (process == null)
                return 1;
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static int LaunchGameWithoutManagerArguments(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
            return 1;

        var gameArguments = new List<string>();
        for (var i = 2; i < args.Length; i++)
            gameArguments.Add(args[i]);

        var gamePath = Path.GetFullPath(args[1]);
        var startInfo = new ProcessStartInfo
        {
            FileName = gamePath,
            Arguments = JoinArguments(gameArguments),
            WorkingDirectory = Path.GetDirectoryName(gamePath),
            UseShellExecute = false
        };

        return RunAndWait(startInfo);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Steam passes the wrapper script path first, followed by %command%'s
        // expanded game executable and original arguments.
        if (args.Length < 2)
            return 1;

        var scriptPath = Path.GetFullPath(args[0]);
        if (!File.Exists(scriptPath))
            return LaunchGameWithoutManagerArguments(args);

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return LaunchGameWithoutManagerArguments(args);

        var powershellArguments = new List<string>
        {
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-WindowStyle",
            "Hidden",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };

        for (var i = 1; i < args.Length; i++)
            powershellArguments.Add(args[i]);

        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = JoinArguments(powershellArguments),
            WorkingDirectory = Path.GetDirectoryName(scriptPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            return RunAndWait(startInfo);
        }
        catch
        {
            // If PowerShell itself cannot be started, make a vanilla launch
            // attempt rather than preventing the player from opening the game.
            return LaunchGameWithoutManagerArguments(args);
        }
    }
}
