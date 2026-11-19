using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    private const string DefaultGameExecutableName = "AbioticFactor.exe";
    private const string ShippingGameExecutableName = "AbioticFactor-Win64-Shipping.exe";
    private string _steamRoot = "";
    private string _gameDirectory = "";
    private bool _gamePathValid;
    private bool _ue4ssInstalled;
    private string _customLaunchOptions = "";
    private bool _launchOptionsConfigured;

    public event Action? GameSetupChanged;
    public string SteamRoot => _steamRoot;
    // This is the main install folder: the directory containing AbioticFactor.exe.
    public string GameDirectory => _gameDirectory;
    // Some Steam builds place the actual Unreal game files in a nested AbioticFactor directory.
    public string GameFilesDirectory => HasValidGamePath &&
        File.Exists(Path.Combine(GameDirectory, DefaultGameExecutableName)) &&
        File.Exists(Path.Combine(GameDirectory, "AbioticFactor", "Binaries", "Win64", ShippingGameExecutableName))
            ? Path.Combine(GameDirectory, "AbioticFactor")
            : GameDirectory;
    public bool HasValidGamePath => _gamePathValid;
    public bool IsSteamGameInstall => HasValidGamePath && IsVerifiedSteamGameInstall(GameDirectory);
    public bool IsUe4ssInstalled => _ue4ssInstalled;
    public string CustomLaunchOptions => _customLaunchOptions;
    public string GamePathDisplay => string.IsNullOrWhiteSpace(_gameDirectory) ? "Not configured" : _gameDirectory;
    public string GameExecutablePath => HasValidGamePath
        ? Path.Combine(GameFilesDirectory, "Binaries", "Win64", ShippingGameExecutableName)
        : "";

    private string GameSettingsPath => Path.Combine(_dataRoot, "app-settings.json");

    private sealed class ManagerAppSettings
    {
        public string GameDirectory { get; set; } = "";
        public string? LaunchOptions { get; set; }
        public bool LaunchOptionsConfigured { get; set; }
    }

    private void InitializeGameSetup()
    {
        _steamRoot = FindSteamRoot();
        ManagerAppSettings? settings = null;
        try
        {
            if (File.Exists(GameSettingsPath))
                settings = JsonSerializer.Deserialize<ManagerAppSettings>(File.ReadAllText(GameSettingsPath));
        }
        catch { }

        _customLaunchOptions = settings?.LaunchOptions ?? "";
        _launchOptionsConfigured = settings?.LaunchOptionsConfigured ?? false;

        if (settings != null && !string.IsNullOrWhiteSpace(settings.GameDirectory))
        {
            // Normalize an older saved inner path to the main folder containing AbioticFactor.exe.
            _gameDirectory = settings.GameDirectory;
            _gamePathValid = TryNormalizeGameDirectory(settings.GameDirectory, out var normalized);
            if (_gamePathValid)
            {
                _gameDirectory = normalized;
                if (!string.Equals(settings.GameDirectory, normalized, StringComparison.OrdinalIgnoreCase))
                    SaveGamePathSetting();
            }
        }
        else
        {
            var discovered = DiscoverSteamGameDirectory();
            if (!string.IsNullOrWhiteSpace(discovered))
            {
                _gameDirectory = discovered;
                _gamePathValid = true;
                SaveGamePathSetting();
            }
        }

        if (IsSteamGameInstall && !_launchOptionsConfigured)
        {
            try
            {
                ImportSteamLaunchOptionsIfNeeded();
                SaveGamePathSetting();
            }
            catch { /* Steam may not have a local user config yet. */ }
        }

        RefreshUe4ssStatus();
    }

    public void SetGameDirectory(string path)
    {
        if (!TryNormalizeGameDirectory(path, out var normalized))
            throw new InvalidOperationException(
                "Select either AbioticFactor.exe or AbioticFactor-Win64-Shipping.exe from a valid Abiotic Factor installation.");

        _gameDirectory = normalized;
        _gamePathValid = true;
        try { ImportSteamLaunchOptionsIfNeeded(); }
        catch { /* A missing Steam config must not block game-folder selection. */ }
        SaveGamePathSetting();
        RefreshUe4ssStatus();
        RefreshInstalled(false);
        if (IsSteamGameInstall) SynchronizeSteamLaunchOptions();
        GameSetupChanged?.Invoke();
        SetStatus("Game folder set to " + _gameDirectory + ".");
    }

    public void SaveLaunchOptions(string options)
    {
        options = (options ?? "").Trim();
        if (options.Length > 4096)
            throw new InvalidOperationException("Launch options are limited to 4096 characters.");
        if (options.IndexOf((char)13) >= 0 || options.IndexOf((char)10) >= 0)
            throw new InvalidOperationException("Enter launch options on a single line.");
        if (Regex.IsMatch(options, @"(^|\s)(?:-Mods(?:=|\s)|-SideloadMods(?:\s|$))", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("The -Mods and -SideloadMods options are managed automatically from your enabled Mod.io mods.");

        _customLaunchOptions = options;
        _launchOptionsConfigured = true;
        SaveGamePathSetting();
        if (IsSteamGameInstall) EnsureSteamWrapperFiles();
        SetStatus("Additional launch options saved.");
    }
    private void ImportSteamLaunchOptionsIfNeeded()
    {
        if (_launchOptionsConfigured || !IsSteamGameInstall) return;

        _customLaunchOptions = ReadSteamLaunchOptions();
        _launchOptionsConfigured = true;
    }

    private void SaveGamePathSetting()
    {
        Directory.CreateDirectory(_dataRoot);
        var settings = new ManagerAppSettings { GameDirectory = _gameDirectory, LaunchOptions = _customLaunchOptions, LaunchOptionsConfigured = _launchOptionsConfigured };
        File.WriteAllText(GameSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool ContainsShippingExecutable(string gameFilesDirectory)
        => File.Exists(Path.Combine(gameFilesDirectory, "Binaries", "Win64", ShippingGameExecutableName));

    private static bool IsValidMainGameDirectory(string directory)
    {
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, DefaultGameExecutableName)))
            return false;

        return ContainsShippingExecutable(directory) ||
            ContainsShippingExecutable(Path.Combine(directory, "AbioticFactor"));
    }

    private static bool TryNormalizeGameDirectory(string input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        try
        {
            var full = Path.GetFullPath(input.Trim().Trim('"'));
            var candidates = new List<string>();

            if (File.Exists(full))
            {
                var leaf = Path.GetFileName(full);
                var exeDirectory = Path.GetDirectoryName(full)!;

                if (leaf.Equals(DefaultGameExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(exeDirectory);
                }
                else if (leaf.Equals(ShippingGameExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    var win64 = new DirectoryInfo(exeDirectory);
                    var gameFiles = win64.Parent?.Parent?.FullName;
                    if (!string.IsNullOrWhiteSpace(gameFiles))
                    {
                        candidates.Add(Directory.GetParent(gameFiles)?.FullName ?? gameFiles);
                        candidates.Add(gameFiles);
                    }
                }
                else
                {
                    return false;
                }
            }
            else if (Directory.Exists(full))
            {
                var directory = new DirectoryInfo(full);

                if (File.Exists(Path.Combine(full, DefaultGameExecutableName)))
                    candidates.Add(full);

                // An older saved path or a manually selected nested game-files folder.
                var parent = directory.Parent?.FullName;
                if (!string.IsNullOrWhiteSpace(parent) && File.Exists(Path.Combine(parent, DefaultGameExecutableName)))
                    candidates.Add(parent);

                // When a user supplies Binaries, Win64, or the content directory, normalize it
                // back to the parent directory containing the default executable.
                var gameFiles = directory;
                if (directory.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase))
                    gameFiles = directory.Parent?.Parent ?? directory;
                else if (directory.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase))
                    gameFiles = directory.Parent ?? directory;

                if (File.Exists(Path.Combine(gameFiles.FullName, DefaultGameExecutableName)))
                    candidates.Add(gameFiles.FullName);
                var gameFilesParent = gameFiles.Parent?.FullName;
                if (!string.IsNullOrWhiteSpace(gameFilesParent) &&
                    File.Exists(Path.Combine(gameFilesParent, DefaultGameExecutableName)))
                    candidates.Add(gameFilesParent);
            }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!IsValidMainGameDirectory(candidate)) continue;
                normalized = Path.GetFullPath(candidate);
                return true;
            }
        }
        catch { }

        return false;
    }

    private string DiscoverSteamGameDirectory()
    {
        return TryGetInstalledSteamGameDirectory(out var gameDirectory) ? gameDirectory : "";
    }

    private List<string> GetSteamLibraryRoots()
    {
        var libraries = new List<string>();
        if (!string.IsNullOrWhiteSpace(_steamRoot) && Directory.Exists(_steamRoot))
            libraries.Add(Path.GetFullPath(_steamRoot));

        if (string.IsNullOrWhiteSpace(_steamRoot))
            return libraries;

        // Current Steam clients use config\libraryfolders.vdf; older clients used
        // steamapps\libraryfolders.vdf. Read both so custom library drives are found.
        var libraryFiles = new[]
        {
            Path.Combine(_steamRoot, "config", "libraryfolders.vdf"),
            Path.Combine(_steamRoot, "steamapps", "libraryfolders.vdf")
        };

        foreach (var libraryFile in libraryFiles)
        {
            if (!File.Exists(libraryFile)) continue;
            try
            {
                var text = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(text,
                    @"""path""\s+""(?<path>(?:\\.|[^""\\])*)""",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var path = UnescapeVdf(match.Groups["path"].Value).Trim();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    path = Environment.ExpandEnvironmentVariables(path);
                    if (Directory.Exists(path))
                        libraries.Add(Path.GetFullPath(path));
                }

                // Legacy libraryfolders files stored each library as a numeric key/value
                // rather than a { path = ... } object. Use this only when no modern path
                // entries were present to avoid reading app IDs inside nested "apps" blocks.
                if (!text.Contains("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    var root = FindVdfObject(text, "libraryfolders", 0, text.Length);
                    if (root.HasValue)
                    {
                        var rootText = text.Substring(root.Value.Open + 1,
                            root.Value.Close - root.Value.Open - 1);
                        foreach (Match match in Regex.Matches(rootText,
                            @"(?m)^\s*""\d+""\s+""(?<path>(?:\\.|[^""\\])*)"""))
                        {
                            var path = Environment.ExpandEnvironmentVariables(
                                UnescapeVdf(match.Groups["path"].Value).Trim());
                            if (Directory.Exists(path))
                                libraries.Add(Path.GetFullPath(path));
                        }
                    }
                }
            }
            catch { /* Ignore unreadable library files and keep searching other known libraries. */ }
        }

        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool TryGetInstalledSteamGameDirectory(out string gameDirectory)
    {
        gameDirectory = "";
        foreach (var library in GetSteamLibraryRoots())
        {
            var manifest = Path.Combine(library, "steamapps", "appmanifest_" + SteamAppId + ".acf");
            if (!TryReadSteamInstallDirectory(manifest, out var installDirectory))
                continue;

            var candidate = Path.Combine(library, "steamapps", "common", installDirectory);
            if (!TryNormalizeGameDirectory(candidate, out var normalized))
                continue;

            gameDirectory = normalized;
            return true;
        }
        return false;
    }

    private bool IsVerifiedSteamGameInstall(string selectedGameDirectory)
    {
        string selected;
        try { selected = Path.GetFullPath(selectedGameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return false; }

        foreach (var library in GetSteamLibraryRoots())
        {
            var manifest = Path.Combine(library, "steamapps", "appmanifest_" + SteamAppId + ".acf");
            if (!TryReadSteamInstallDirectory(manifest, out var installDirectory))
                continue;

            var candidate = Path.Combine(library, "steamapps", "common", installDirectory);
            if (!TryNormalizeGameDirectory(candidate, out var normalized))
                continue;

            var installed = Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(selected, installed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryReadSteamInstallDirectory(string manifestPath, out string installDirectory)
    {
        installDirectory = "";
        if (!File.Exists(manifestPath)) return false;

        try
        {
            var text = File.ReadAllText(manifestPath);
            var appId = Regex.Match(text, @"""appid""\s+""(?<id>\d+)""", RegexOptions.IgnoreCase);
            if (!appId.Success || appId.Groups["id"].Value != SteamAppId.ToString())
                return false;

            var flags = Regex.Match(text, @"""StateFlags""\s+""(?<flags>\d+)""", RegexOptions.IgnoreCase);
            if (!flags.Success || !int.TryParse(flags.Groups["flags"].Value, out var stateFlags) ||
                (stateFlags & 4) == 0)
                return false;

            var install = Regex.Match(text, @"""installdir""\s+""(?<directory>(?:\\.|[^""\\])*)""",
                RegexOptions.IgnoreCase);
            if (!install.Success) return false;

            var value = UnescapeVdf(install.Groups["directory"].Value).Trim();
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
                value is "." or "..")
                return false;

            installDirectory = value;
            return true;
        }
        catch { return false; }
    }

    private static string FindSteamRoot()
    {
        var roots = new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"C:\Steam"
        };

        foreach (var value in roots)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var path = Environment.ExpandEnvironmentVariables(value).Replace('/', Path.DirectorySeparatorChar);
            try
            {
                path = Path.GetFullPath(path);
                if (File.Exists(Path.Combine(path, "steam.exe")) || Directory.Exists(Path.Combine(path, "steamapps")))
                    return path;
            }
            catch { }
        }
        return "";
    }

    private void RefreshUe4ssStatus()
    {
        _ue4ssInstalled = false;
        if (_gamePathValid)
        {
            var win64 = Path.Combine(GameFilesDirectory, "Binaries", "Win64");
            _ue4ssInstalled = File.Exists(Path.Combine(win64, "dwmapi.dll")) &&
                File.Exists(Path.Combine(win64, "ue4ss", "UE4SS.dll"));
        }
        GameSetupChanged?.Invoke();
    }
}