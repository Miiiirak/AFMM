using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

[assembly: AssemblyTitle("Abiotic Factor Mod Manager Steam Launch Host")]
[assembly: AssemblyProduct("Abiotic Factor Mod Manager")]
[assembly: AssemblyVersion("0.0.1.0")]
[assembly: AssemblyFileVersion("0.0.1.0")]
[assembly: AssemblyInformationalVersion("0.0.1")]

internal static class SteamLaunchWrapperHost
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string QuoteArgument(string value)
    {
        if (value == null) value = string.Empty;
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
            builder.Append(QuoteArgument(argument));
        }
        return builder.ToString();
    }

    private static string[] ParseWindowsArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return new string[0];

        int count;
        IntPtr argv = CommandLineToArgvW(commandLine, out count);
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                IntPtr item = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(item) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string ResolveWrapperDirectory(string[] args, out int gameArgumentIndex)
    {
        gameArgumentIndex = 0;

        // Steam's launch string retains the .ps1 path as a stable marker/sidecar
        // location, but this native host deliberately does not execute PowerShell.
        if (args.Length > 2 &&
            string.Equals(args[0], "--native-direct", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(args[1]), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            gameArgumentIndex = 2;
            return Path.GetDirectoryName(Path.GetFullPath(args[1]));
        }

        // Accept the earlier helper command during an in-place app update.
        if (args.Length > 1 &&
            string.Equals(Path.GetExtension(args[0]), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            gameArgumentIndex = 1;
            return Path.GetDirectoryName(Path.GetFullPath(args[0]));
        }

        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    }

    private static List<string> ReadManagedArguments(string wrapperDirectory)
    {
        var result = new List<string>();
        try
        {
            var managerPathFile = Path.Combine(wrapperDirectory, "manager-path.txt");
            var managedArgumentsFile = Path.Combine(wrapperDirectory, "steam-modio-args.txt");
            if (!File.Exists(managerPathFile) || !File.Exists(managedArgumentsFile))
                return result;

            var managerExecutable = File.ReadAllText(managerPathFile).Trim();
            if (string.IsNullOrWhiteSpace(managerExecutable) || !File.Exists(managerExecutable))
                return result;

            var line = File.ReadAllText(managedArgumentsFile).Trim();
            if (string.IsNullOrWhiteSpace(line))
                return result;

            var tokens = Regex.Split(line, @"\s+");
            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token))
                    continue;
                if (!Regex.IsMatch(token, @"^-Mods=\d+(;\d+)*$") && token != "-SideloadMods")
                    return new List<string>();
                result.Add(token);
            }
        }
        catch
        {
            return new List<string>();
        }

        return result;
    }

    private static List<string> ReadAdditionalArguments(string wrapperDirectory)
    {
        try
        {
            var path = Path.Combine(wrapperDirectory, "steam-additional-args.txt");
            if (!File.Exists(path))
                return new List<string>();
            return new List<string>(ParseWindowsArguments(File.ReadAllText(path).Trim()));
        }
        catch
        {
            // Invalid optional arguments must never suppress the required vanilla launch.
            return new List<string>();
        }
    }

    private static int LaunchGame(string gamePath, IList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = gamePath,
            Arguments = JoinArguments(arguments),
            WorkingDirectory = Path.GetDirectoryName(gamePath),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(startInfo))
        {
            if (process == null)
                return 1;
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static int LaunchGameWithoutManagerArguments(string[] args, int gameArgumentIndex)
    {
        if (args.Length <= gameArgumentIndex)
            return 1;

        var gamePath = Path.GetFullPath(args[gameArgumentIndex]);
        if (!File.Exists(gamePath))
            return 1;

        var originalArguments = new List<string>();
        for (var i = gameArgumentIndex + 1; i < args.Length; i++)
            originalArguments.Add(args[i]);

        return LaunchGame(gamePath, originalArguments);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        int gameArgumentIndex = 0;
        string wrapperDirectory;

        try
        {
            wrapperDirectory = ResolveWrapperDirectory(args, out gameArgumentIndex);
            if (args.Length <= gameArgumentIndex)
                return 1;

            var gamePath = Path.GetFullPath(args[gameArgumentIndex]);
            if (!File.Exists(gamePath))
                return 1;

            var originalArguments = new List<string>();
            for (var i = gameArgumentIndex + 1; i < args.Length; i++)
                originalArguments.Add(args[i]);

            var launchArguments = new List<string>(originalArguments);
            launchArguments.AddRange(ReadManagedArguments(wrapperDirectory));
            launchArguments.AddRange(ReadAdditionalArguments(wrapperDirectory));

            try
            {
                return LaunchGame(gamePath, launchArguments);
            }
            catch
            {
                // If manager-added args prevent startup, retry with Steam's original
                // arguments only. The fallback still starts the game directly.
                return LaunchGame(gamePath, originalArguments);
            }
        }
        catch
        {
            // Keep a vanilla launch path available even if sidecar/path handling fails.
            try
            {
                if (gameArgumentIndex >= 0)
                    return LaunchGameWithoutManagerArguments(args, gameArgumentIndex);
            }
            catch { }
            return 1;
        }
    }
}
