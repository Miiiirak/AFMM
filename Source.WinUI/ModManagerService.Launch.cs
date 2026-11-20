using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    public string CurrentLaunchOptions => BuildLaunchOptions();
    public string LastLaunchSyncStatus { get; private set; } = "";
    public bool IsSteamWrapperConfigured
    {
        get
        {
            if (!IsSteamGameInstall) return false;
            try
            {
                var configPath = FindSteamConfigPath();
                var options = ReadSteamLaunchOptionsFromConfig(configPath);
                return FindSteamWrapperMarker(options) >= 0 &&
                    options.IndexOf("SteamLaunchWrapperHost.exe", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    options.IndexOf("--native-direct", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }

    public async Task ApplySteamLaunchOptionsAsync(bool restartSteam)
    {
        if (!IsSteamGameInstall)
            throw new InvalidOperationException("Abiotic Factor is not verified as installed through this Steam account and its configured libraries.");

        var steamExe = ResolveSteamExecutable();
        if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
            throw new FileNotFoundException("Steam could not be found. Check your Steam installation and try again.");

        string configPath;
        try { configPath = FindSteamConfigPath(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Steam was found, but its launch-options configuration could not be accessed. Make sure this app is running as Administrator. " + ex.Message, ex);
        }

        // Install the static wrapper and refresh the two live sidecars: manager-required Mod.io flags
        // and the user's optional additional launch options. Neither changes Steam's stored command.
        EnsureSteamWrapperFiles();
        var wrapperOptions = BuildSteamWrapperLaunchOptions(configPath);

        if (!restartSteam)
        {
            var package = new DataPackage();
            package.SetText(wrapperOptions);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            LastLaunchSyncStatus = "Launch options copied to clipboard. Add them in Steam game properties for Abiotic Factor, then you can launch the game in Steam.";
            SetStatus(LastLaunchSyncStatus);
            return;
        }

        if (IsGameRunning())
            throw new InvalidOperationException("Close Abiotic Factor before restarting Steam to apply launch options.");

        CaptureOriginalSteamLaunchOptions(configPath);

        LastLaunchSyncStatus = "Steam restarting... (Applying launch options)";
        SetStatus(LastLaunchSyncStatus);

        try
        {
            if (Process.GetProcessesByName("steam").Length > 0)
            {
                var shutdown = new ProcessStartInfo
                {
                    FileName = steamExe,
                    WorkingDirectory = Path.GetDirectoryName(steamExe)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = "-shutdown"
                };
                using var shutdownProcess = Process.Start(shutdown);
                if (shutdownProcess == null)
                    throw new InvalidOperationException("Windows could not send Steam the shutdown command.");

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
                while (Process.GetProcessesByName("steam").Length > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(500);
                if (Process.GetProcessesByName("steam").Length > 0)
                    throw new TimeoutException("Steam could not be closed. Close Steam manually, then try Set Up Steam again.");
            }

            WriteSteamLaunchOptions(wrapperOptions, configPath);

            var restart = new ProcessStartInfo
            {
                FileName = steamExe,
                WorkingDirectory = Path.GetDirectoryName(steamExe)!,
                UseShellExecute = false
            };
            var steamProcess = Process.Start(restart);
            if (steamProcess == null)
                throw new InvalidOperationException("Steam launch options were applied, but Windows could not restart Steam.");

            var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            while (Process.GetProcessesByName("steam").Length == 0 && DateTime.UtcNow < startDeadline)
                await Task.Delay(500);
            if (Process.GetProcessesByName("steam").Length == 0)
                throw new InvalidOperationException("Steam launch options were applied, but Steam could not be started. Open Steam manually.");

            LastLaunchSyncStatus = "Steam restarted. You should now be able to launch through Steam with your mods.";
            SetStatus(LastLaunchSyncStatus);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("Steam was found, but Windows denied access to its launch-options file. Make sure this app is running as Administrator. " + ex.Message, ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Windows could not access, close, or restart Steam. Make sure Steam is installed and this app is running as Administrator. " + ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new IOException("Steam was found, but its launch-options file could not be updated. Close Steam and retry Set Up Steam. " + ex.Message, ex);
        }
    }
    public void LaunchGame()
    {
        if (!HasValidGamePath)
            throw new InvalidOperationException("Select a valid Abiotic Factor game folder in Settings first.");

        if (IsGameRunning())
        {
            SetStatus("Abiotic Factor is already running.");
            return;
        }

        if (IsSteamGameInstall)
        {
            var steamExe = ResolveSteamExecutable();
            if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
                throw new FileNotFoundException("Steam is installed for this game, but steam.exe could not be located.");

            EnsureSteamWrapperFiles();
            var start = new ProcessStartInfo
            {
                FileName = steamExe,
                WorkingDirectory = Path.GetDirectoryName(steamExe)!,
                UseShellExecute = false,
                Arguments = "-applaunch " + SteamAppId
            };
            var steamProcess = Process.Start(start);
            if (steamProcess == null) throw new InvalidOperationException("Windows did not start Steam.");
            SetStatus("Launching Abiotic Factor through Steam with the latest enabled Mod.io mods.");
            return;
        }

        var rootExecutable = Path.Combine(GameDirectory, "AbioticFactor.exe");
        var executable = File.Exists(rootExecutable) ? rootExecutable : GameExecutablePath;
        if (!File.Exists(executable))
            throw new FileNotFoundException("The Abiotic Factor game executable could not be found.", executable);

        var directStart = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            Arguments = BuildLaunchOptions()
        };
        var gameProcess = Process.Start(directStart);
        if (gameProcess == null) throw new InvalidOperationException("Windows did not start Abiotic Factor.");
        SetStatus("Launching the non-Steam installation with the current mod and custom launch options.");
    }
    public void SynchronizeSteamLaunchOptions()
    {
        try
        {
            if (!IsSteamGameInstall)
            {
                LastLaunchSyncStatus = "Non-Steam install: Launch Game applies custom launch options directly to the game executable.";
                return;
            }

            EnsureSteamWrapperFiles();
            LastLaunchSyncStatus = "";
        }
        catch (Exception ex)
        {
            LastLaunchSyncStatus = "Managed Mod.io launch arguments could not be updated: " + ex.Message;
        }
    }

    private string SteamWrapperDirectory => Path.Combine(_dataRoot, "Steam");
    private string SteamWrapperScriptPath => Path.Combine(SteamWrapperDirectory, "SteamLaunchWrapper.ps1");
    private string SteamWrapperHostPath => Path.Combine(SteamWrapperDirectory, "SteamLaunchWrapperHost.exe");
    private string SteamManagedArgumentsPath => Path.Combine(SteamWrapperDirectory, "steam-modio-args.txt");
    private string SteamAdditionalArgumentsPath => Path.Combine(SteamWrapperDirectory, "steam-additional-args.txt");
    private string SteamManagerPathFile => Path.Combine(SteamWrapperDirectory, "manager-path.txt");
    private string SteamOriginalLaunchOptionsPath => Path.Combine(SteamWrapperDirectory, "steam-launch-options-original.txt");

    private void EnsureSteamWrapperFiles()
    {
        Directory.CreateDirectory(SteamWrapperDirectory);
        var sourceScript = Path.Combine(_baseRoot, "SteamLaunchWrapper.ps1");
        if (!File.Exists(sourceScript))
            throw new FileNotFoundException("The Steam launch wrapper is missing from the manager installation.", sourceScript);

        var sourceHost = Path.Combine(_baseRoot, "SteamLaunchWrapperHost.exe");
        if (!File.Exists(sourceHost))
            throw new FileNotFoundException("The hidden Steam launch host is missing from the manager installation.", sourceHost);

        File.Copy(sourceScript, SteamWrapperScriptPath, true);
        File.Copy(sourceHost, SteamWrapperHostPath, true);
        WriteManagedSteamLaunchArguments();
    }

    private void CaptureOriginalSteamLaunchOptions(string configPath)
    {
        if (File.Exists(SteamOriginalLaunchOptionsPath)) return;

        var existing = ReadSteamLaunchOptionsFromConfig(configPath);
        var original = existing;
        const string commandToken = "%command%";
        if (FindSteamWrapperMarker(existing) >= 0)
        {
            var commandIndex = existing.IndexOf(commandToken, StringComparison.OrdinalIgnoreCase);
            original = commandIndex >= 0
                ? existing[(commandIndex + commandToken.Length)..].Trim()
                : "";
            original = StripManagedLaunchArguments(original);
        }

        WriteAtomicText(SteamOriginalLaunchOptionsPath, original);
    }
    private void WriteManagedSteamLaunchArguments()
    {
        Directory.CreateDirectory(SteamWrapperDirectory);
        var managerExecutable = Path.Combine(_baseRoot, "AbioticFactorModManager.exe");
        if (!File.Exists(managerExecutable))
            managerExecutable = Path.Combine(AppContext.BaseDirectory, "AbioticFactorModManager.exe");

        WriteAtomicText(SteamManagerPathFile, managerExecutable);
        WriteAtomicText(SteamManagedArgumentsPath, BuildManagedModLaunchOptions());
        WriteAtomicText(SteamAdditionalArgumentsPath, _customLaunchOptions.Trim());
    }

    private static void WriteAtomicText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The managed launch-options path is invalid.");
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                var backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
                try { File.Replace(temp, path, backup, true); }
                finally { if (File.Exists(backup)) File.Delete(backup); }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static int FindSteamWrapperMarker(string options)
    {
        foreach (var marker in new[] { "SteamLaunchWrapper.ps1", "SteamLaunchWrapper.cmd", "afmm-launch.ps1", "afmm-launch.cmd" })
        {
            var index = options.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return index;
        }
        return -1;
    }
    private string BuildSteamWrapperLaunchOptions(string configPath)
    {
        var existing = ReadSteamLaunchOptionsFromConfig(configPath);
        var extraGameArguments = existing;
        const string commandToken = "%command%";
        if (FindSteamWrapperMarker(existing) >= 0)
        {
            var commandIndex = existing.IndexOf(commandToken, StringComparison.OrdinalIgnoreCase);
            extraGameArguments = commandIndex >= 0
                ? existing[(commandIndex + commandToken.Length)..].Trim()
                : "";
        }
        extraGameArguments = StripManagedLaunchArguments(extraGameArguments);
        var savedCustomOptions = _customLaunchOptions.Trim();
        if (!string.IsNullOrWhiteSpace(savedCustomOptions))
        {
            extraGameArguments = Regex.Replace(
                extraGameArguments,
                Regex.Escape(savedCustomOptions),
                " ",
                RegexOptions.IgnoreCase);
            extraGameArguments = Regex.Replace(extraGameArguments, @"\s{2,}", " ").Trim();
        }

        if (!File.Exists(SteamWrapperHostPath))
            throw new FileNotFoundException("The hidden Steam launch host is missing. Re-extract the complete manager release.", SteamWrapperHostPath);

        var wrapper = "\"" + SteamWrapperHostPath + "\" --native-direct \"" + SteamWrapperScriptPath + "\" %command%";
        return string.IsNullOrWhiteSpace(extraGameArguments) ? wrapper : wrapper + " " + extraGameArguments;
    }

    private string BuildManagedModLaunchOptions()
    {
        var modIds = _installed
            .Where(mod => IsModIo(mod) && mod.Enabled)
            .Select(mod => mod.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id) && Regex.IsMatch(id, @"^\d+$"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return modIds.Length == 0 ? "" : "-Mods=" + string.Join(";", modIds) + " -SideloadMods";
    }
    private string BuildLaunchOptions()
    {
        var managed = BuildManagedModLaunchOptions();
        return string.Join(" ", new[] { managed, _customLaunchOptions.Trim() }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
    private string ResolveSteamExecutable()
    {
        var configured = string.IsNullOrWhiteSpace(SteamRoot) ? "" : Path.Combine(SteamRoot, "steam.exe");
        if (File.Exists(configured)) return configured;

        foreach (var process in Process.GetProcessesByName("steam"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                    return executable;
            }
            catch { }
        }

        var discoveredRoot = FindSteamRoot();
        var discovered = string.IsNullOrWhiteSpace(discoveredRoot) ? "" : Path.Combine(discoveredRoot, "steam.exe");
        return File.Exists(discovered) ? discovered : "";
    }

    private string ResolveSteamRoot()
    {
        if (!string.IsNullOrWhiteSpace(SteamRoot) && Directory.Exists(SteamRoot)) return SteamRoot;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                    return Path.GetDirectoryName(executable)!;
            }
            catch { }
        }
        throw new DirectoryNotFoundException("A Steam installation could not be found.");
    }

    private string FindSteamConfigPath()
    {
        var steamRoot = ResolveSteamRoot();
        var userdataRoot = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdataRoot))
            throw new DirectoryNotFoundException("Steam's userdata folder was not found: " + userdataRoot);

        // Some Steam clients write MostRecent; others only keep a timestamp in loginusers.vdf.
        var loginUsers = Path.Combine(steamRoot, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            try
            {
                var text = File.ReadAllText(loginUsers);
                var users = FindVdfObject(text, "users", 0, text.Length);
                if (users.HasValue)
                {
                    var userText = text.Substring(users.Value.Open + 1, users.Value.Close - users.Value.Open - 1);
                    var ids = Regex.Matches(userText, @"""(?<id>\d{17})""\s*\{");
                    var candidates = new List<(bool MostRecent, long Timestamp, bool AutoLogin, string Config)>();
                    foreach (Match match in ids)
                    {
                        var steamId = match.Groups["id"].Value;
                        var user = FindVdfObject(text, steamId, users.Value.Open + 1, users.Value.Close);
                        if (!user.HasValue) continue;
                        var userBlock = text.Substring(user.Value.Open, user.Value.Close - user.Value.Open + 1);
                        var mostRecent = Regex.IsMatch(userBlock, @"""MostRecent""\s*""1""", RegexOptions.IgnoreCase);
                        var timestampMatch = Regex.Match(userBlock, @"""timestamp""\s*""(?<value>\d+)""", RegexOptions.IgnoreCase);
                        var autoLogin = Regex.IsMatch(userBlock, @"""AutoLogin""\s*""1""", RegexOptions.IgnoreCase);
                        _ = long.TryParse(timestampMatch.Groups["value"].Value, out var timestamp);
                        if (!ulong.TryParse(steamId, out var id64) || id64 < 76561197960265728UL) continue;
                        var accountId = (id64 - 76561197960265728UL).ToString();
                        var candidate = Path.Combine(userdataRoot, accountId, "config", "localconfig.vdf");
                        if (File.Exists(candidate)) candidates.Add((mostRecent, timestamp, autoLogin, candidate));
                    }
                    var selected = candidates.OrderByDescending(item => item.MostRecent)
                        .ThenByDescending(item => item.Timestamp)
                        .ThenByDescending(item => item.AutoLogin)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(selected.Config)) return selected.Config;
                }
            }
            catch { /* Fall back to the newest existing user config below. */ }
        }

        var found = Directory.GetFiles(userdataRoot, "localconfig.vdf", SearchOption.AllDirectories)
            .Where(path => path.Contains(Path.DirectorySeparatorChar + "config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !path.Contains(Path.DirectorySeparatorChar + "anonymous" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return found ?? throw new FileNotFoundException("Steam localconfig.vdf was not found under " + userdataRoot);
    }
    private string ReadSteamLaunchOptions()
    {
        var config = FindSteamConfigPath();
        var text = File.ReadAllText(config, Encoding.UTF8);
        var apps = FindVdfObject(text, "apps", 0, text.Length);
        if (!apps.HasValue) return "";
        var app = FindVdfObject(text, SteamAppId.ToString(), apps.Value.Open + 1, apps.Value.Close);
        if (!app.HasValue) return "";
        var block = text.Substring(app.Value.Open, app.Value.Close - app.Value.Open + 1);
        var match = Regex.Match(block,
            @"(?m)^(?<indent>[ \t]*)""LaunchOptions""[ \t]+""(?<value>(?:\\.|[^""\\])*)""");
        if (!match.Success) return "";
        var savedOptions = UnescapeVdf(match.Groups["value"].Value);
        // The static Steam wrapper is infrastructure, not a custom game argument.
        var wrapperIndex = FindSteamWrapperMarker(savedOptions);
        if (wrapperIndex >= 0)
        {
            const string commandToken = "%command%";
            var commandIndex = savedOptions.IndexOf(commandToken, wrapperIndex, StringComparison.OrdinalIgnoreCase);
            savedOptions = commandIndex >= 0 ? savedOptions[(commandIndex + commandToken.Length)..].Trim() : "";
        }
        return StripManagedLaunchArguments(savedOptions);
    }

    private void WriteSteamLaunchOptions(string options)
        => WriteSteamLaunchOptions(options, FindSteamConfigPath());

    private void WriteSteamLaunchOptions(string options, string config)
    {
        if (!File.Exists(config))
            throw new FileNotFoundException("Steam's local launch-options file was not found.", config);

        var original = File.ReadAllText(config, Encoding.UTF8);
        var updated = UpdateLocalConfigVdf(original, options);
        if (!string.Equals(original, updated, StringComparison.Ordinal))
        {
            var backup = config + ".before-afmm-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6] + ".bak";
            var temp = config + ".afmm.tmp";
            try
            {
                File.WriteAllText(temp, updated, new UTF8Encoding(true));
                File.Replace(temp, config, backup, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        var verified = ReadSteamLaunchOptionsFromConfig(config);
        if (!string.Equals(verified, options, StringComparison.Ordinal))
            throw new IOException("Steam's LaunchOptions value did not match the requested arguments after the write. Config: " + config);
    }

    private static string ReadSteamLaunchOptionsFromConfig(string config)
    {
        var text = File.ReadAllText(config, Encoding.UTF8);
        var apps = FindVdfObject(text, "apps", 0, text.Length);
        if (!apps.HasValue) throw new InvalidDataException("Steam's apps block could not be found in " + config);
        var app = FindVdfObject(text, SteamAppId.ToString(), apps.Value.Open + 1, apps.Value.Close);
        if (!app.HasValue) return "";
        var block = text.Substring(app.Value.Open, app.Value.Close - app.Value.Open + 1);
        var match = Regex.Match(block,
            @"(?m)^(?<indent>[ \t]*)""LaunchOptions""[ \t]+""(?<value>(?:\\.|[^""\\])*)""");
        return match.Success ? UnescapeVdf(match.Groups["value"].Value) : "";
    }
    private static string UpdateLocalConfigVdf(string text, string options)
    {
        var apps = FindVdfObject(text, "apps", 0, text.Length);
        if (!apps.HasValue)
            throw new InvalidOperationException("Couldn't find the Steam apps block.");

        var app = FindVdfObject(text, SteamAppId.ToString(), apps.Value.Open + 1, apps.Value.Close);
        if (!app.HasValue)
        {
            if (string.IsNullOrWhiteSpace(options)) return text;
            var insert = Environment.NewLine + "\t\t\t\"" + SteamAppId + "\"" + Environment.NewLine +
                "\t\t\t{" + Environment.NewLine + "\t\t\t\t\"LaunchOptions\"\t\"" + EscapeVdf(options) + "\"" +
                Environment.NewLine + "\t\t\t}";
            return text.Insert(apps.Value.Close, insert);
        }

        var block = text.Substring(app.Value.Open, app.Value.Close - app.Value.Open + 1);
        var regex = new Regex(@"(?m)^(?<indent>[ \t]*)""LaunchOptions""[ \t]+""(?<value>(?:\\.|[^""\\])*)""");
        var match = regex.Match(block);
        if (match.Success)
        {
            var replacement = match.Groups["indent"].Value + "\"LaunchOptions\"\t\"" + EscapeVdf(options) + "\"";
            block = block.Remove(match.Index, match.Length).Insert(match.Index, replacement);
        }
        else if (!string.IsNullOrWhiteSpace(options))
        {
            var insert = Environment.NewLine + "\t\t\t\t\"LaunchOptions\"\t\"" + EscapeVdf(options) + "\"" +
                Environment.NewLine + "\t\t\t";
            block = block.Insert(block.Length - 1, insert);
        }

        return text.Substring(0, app.Value.Open) + block + text.Substring(app.Value.Close + 1);
    }

    private static (int Open, int Close)? FindVdfObject(string text, string key, int start, int end)
    {
        var token = "\"" + key + "\"";
        var index = start;
        while (index < end)
        {
            var found = text.IndexOf(token, index, StringComparison.Ordinal);
            if (found < 0 || found >= end) return null;
            var p = found + token.Length;
            while (p < end && char.IsWhiteSpace(text[p])) p++;
            if (p < end && text[p] == '{')
            {
                var close = FindMatchingBrace(text, p, end);
                if (close >= 0) return (p, close);
            }
            index = found + token.Length;
        }
        return null;
    }

    private static int FindMatchingBrace(string text, int open, int end)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var i = open; i < end; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string EscapeVdf(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string UnescapeVdf(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                result.Append(next);
            }
            else result.Append(value[i]);
        }
        return result.ToString();
    }

    private static string StripManagedLaunchArguments(string value)
    {
        var clean = Regex.Replace(value, @"(?:^|\s)-Mods=\S+", " ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"(?:^|\s)-SideloadMods(?:\s|$)", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(clean, @"\s{2,}", " ").Trim();
    }
}
