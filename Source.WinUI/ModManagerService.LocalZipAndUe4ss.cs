using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    public (string Type, long Id, string Name, string Version, string[] ModNames) InspectLocalZip(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("ZIP archive not found.", zipPath);

        var temp = Path.Combine(_dataRoot, "inspect_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ExtractZipSafely(zipPath, temp);

            // The AF-UE4SS runtime is installed from Settings, never as a mod ZIP.
            if (File.Exists(Path.Combine(temp, "dwmapi.dll")) ||
                File.Exists(Path.Combine(temp, "ue4ss", "UE4SS.dll")) ||
                File.Exists(Path.Combine(temp, "UE4SS.dll")))
                throw new InvalidOperationException("This ZIP contains the UE4SS runtime installer, not a mod. Install UE4SS from Settings.");

            var descriptors = Directory.GetFiles(temp, "*.uplugin", SearchOption.AllDirectories);
            if (descriptors.Length > 0)
            {
                if (descriptors.Length != 1)
                    throw new InvalidOperationException("A Mod.io ZIP must contain exactly one .uplugin descriptor.");

                var descriptor = descriptors[0];
                var pluginRoot = Path.GetDirectoryName(descriptor)!;
                var windowsPaks = FindWindowsPakFolder(pluginRoot);
                if (windowsPaks == null)
                    throw new InvalidOperationException("The .uplugin archive is not a Windows Mod.io package. It must include Content/Paks/Windows.");

                if (Directory.GetFiles(windowsPaks, "*.pak").Length == 0 ||
                    Directory.GetFiles(windowsPaks, "*.ucas").Length == 0 ||
                    Directory.GetFiles(windowsPaks, "*.utoc").Length == 0)
                    throw new InvalidOperationException("The cooked Mod.io plugin is incomplete: .pak, .ucas and .utoc files are required.");

                var metadata = ReadPluginInfo(descriptor, "", "");
                using var document = JsonDocument.Parse(File.ReadAllText(descriptor));
                var id = new[] { "ModId", "ModID", "ModIoId", "ModIOId", "modio_id" }
                    .Select(key => GetLong(document.RootElement, key))
                    .FirstOrDefault(value => value > 0);
                if (id <= 0)
                    id = ParseModId(Path.GetFileName(zipPath));

                return ("Mod.io", id, metadata.FriendlyName, metadata.Version, Array.Empty<string>());
            }

            var modRoots = FindUe4ssModRoots(temp);
            if (modRoots.Count > 0)
                return ("UE4SS", 0, modRoots.Count == 1 ? Path.GetFileName(modRoots[0]) : modRoots.Count + " UE4SS mods", "1.0",
                    modRoots.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray());

            throw new InvalidOperationException("This ZIP isn't a supported Abiotic Factor mod. Choose a Mod.io cooked .uplugin ZIP or a UE4SS mod ZIP containing Scripts or DLL files.");
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    private List<string> FindUe4ssModRoots(string extractionRoot)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scripts in Directory.GetDirectories(extractionRoot, "Scripts", SearchOption.AllDirectories))
        {
            if (Directory.GetFiles(scripts, "*.lua", SearchOption.AllDirectories).Length == 0) continue;
            var parent = Directory.GetParent(scripts)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent)) candidates.Add(parent);
        }

        foreach (var dlls in Directory.GetDirectories(extractionRoot, "dlls", SearchOption.AllDirectories))
        {
            if (Directory.GetFiles(dlls, "*.dll", SearchOption.AllDirectories).Length == 0) continue;
            var parent = Directory.GetParent(dlls)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent)) candidates.Add(parent);
        }

        foreach (var mainLua in Directory.GetFiles(extractionRoot, "main.lua", SearchOption.AllDirectories))
        {
            var parent = Path.GetDirectoryName(mainLua);
            if (string.Equals(Path.GetFileName(parent), "Scripts", StringComparison.OrdinalIgnoreCase))
                parent = Directory.GetParent(parent!)?.FullName ?? parent;
            if (!string.IsNullOrWhiteSpace(parent)) candidates.Add(parent);
        }

        foreach (var dll in Directory.GetFiles(extractionRoot, "*.dll", SearchOption.AllDirectories))
        {
            var parent = Path.GetDirectoryName(dll);
            if (string.IsNullOrWhiteSpace(parent)) continue;
            if (Path.GetFileName(parent).Equals("dlls", StringComparison.OrdinalIgnoreCase))
                parent = Directory.GetParent(parent)?.FullName ?? parent;
            candidates.Add(parent);
        }

        return candidates
            .Where(path => !Path.GetFileName(path).Equals("shared", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals("Scripts", StringComparison.OrdinalIgnoreCase))
            .Where(IsUe4ssModFolder)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<string>> InstallUe4ssModsFromZipAsync(string zipPath)
    {
        if (!HasValidGamePath)
            throw new InvalidOperationException("Select a valid game folder in Settings first.");
        if (!IsUe4ssInstalled)
            throw new InvalidOperationException("UE4SS is not installed in the selected game folder. Install it from Settings before installing UE4SS mods.");
        if (IsGameRunning())
            throw new InvalidOperationException("Close Abiotic Factor before installing mods.");
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("ZIP archive not found.", zipPath);

        var temp = Path.Combine(_dataRoot, "ue4ss_mod_stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var installedNames = new List<string>();
        var committed = new List<(string Destination, string Backup)>();
        var configPath = Path.Combine(Ue4ssModsRoot, "mods.txt");
        var hadConfig = File.Exists(configPath);
        var originalConfig = hadConfig ? File.ReadAllBytes(configPath) : Array.Empty<byte>();

        try
        {
            ExtractZipSafely(zipPath, temp);
            var roots = FindUe4ssModRoots(temp);
            if (roots.Count == 0)
                throw new InvalidOperationException("No UE4SS mod folders were found in this ZIP.");

            // Preserve the exact pre-manager UE4SS mod configuration for full uninstall.
            var uninstallRestore = Path.Combine(_dataRoot, "uninstall-restore");
            var configBaseline = Path.Combine(uninstallRestore, "ue4ss-mods.txt");
            var configWasMissing = Path.Combine(uninstallRestore, "ue4ss-mods.was-missing");
            if (!File.Exists(configBaseline) && !File.Exists(configWasMissing))
            {
                Directory.CreateDirectory(uninstallRestore);
                if (hadConfig)
                    File.WriteAllBytes(configBaseline, originalConfig);
                else
                    File.WriteAllText(configWasMissing, "mods.txt did not exist before this manager installed UE4SS mods.");
            }

            Directory.CreateDirectory(Ue4ssModsRoot);
            foreach (var source in roots)
            {
                var name = SafeFileName(Path.GetFileName(source), "");
                if (string.IsNullOrWhiteSpace(name) || name.Equals("shared", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The archive contains an invalid UE4SS mod folder name.");

                var destination = Path.Combine(Ue4ssModsRoot, name);
                if (committed.Any(item => string.Equals(item.Destination, destination, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The archive contains duplicate UE4SS mod folder names.");

                var stage = Path.Combine(Ue4ssModsRoot, ".stage-" + Guid.NewGuid().ToString("N"));
                var previous = _installed.FirstOrDefault(mod =>
                    string.Equals(mod.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(mod.PluginName, name, StringComparison.OrdinalIgnoreCase));
                bool wasEnabled = previous?.Enabled ?? !File.Exists(Path.Combine(source, "disabled.txt"));

                Directory.CreateDirectory(stage);
                try
                {
                    CopyDirectoryContents(source, stage);
                    if (!IsUe4ssModFolder(stage))
                        throw new InvalidOperationException("The extracted folder '" + name + "' is not a recognized UE4SS mod.");

                    string backup = "";
                    if (Directory.Exists(destination))
                    {
                        Directory.CreateDirectory(_backupsRoot);
                        backup = Path.Combine(_backupsRoot, "ue4ss_" + SafeFileName(name, "mod") + "_" +
                            DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6]);
                        Directory.Move(destination, backup);
                    }

                    try
                    {
                        Directory.Move(stage, destination);
                        committed.Add((destination, backup));
                        SetUe4ssModEnabled(name, wasEnabled);
                        installedNames.Add(name);
                    }
                    catch
                    {
                        // The record is added immediately after the move so the outer rollback
                        // also catches failures while updating enabled markers or mods.txt.
                        if (committed.Count == 0 || !string.Equals(committed[^1].Destination, destination, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(backup) && Directory.Exists(backup) && !Directory.Exists(destination))
                                Directory.Move(backup, destination);
                        }
                        throw;
                    }
                }
                finally
                {
                    TryDeleteDirectory(stage);
                }
            }

            foreach (var name in installedNames)
            {
                var record = _installed.FirstOrDefault(mod =>
                    string.Equals(mod.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(mod.PluginName, name, StringComparison.OrdinalIgnoreCase));
                if (record == null)
                {
                    record = new InstalledMod
                    {
                        Id = "ue4ss:" + name,
                        Name = FormatUe4ssName(name),
                        PluginName = name,
                        SourceType = "UE4SS",
                        InstalledAt = DateTime.Now
                    };
                    _installed.Add(record);
                }
                record.InstalledByManager = true;
            }
            SaveInstalledState();

            RefreshInstalled(false);
            SetStatus("Installed " + installedNames.Count + " UE4SS mod(s): " + string.Join(", ", installedNames) + ".");
            return Task.FromResult<IReadOnlyList<string>>(installedNames);
        }
        catch
        {
            var rollbackErrors = new List<Exception>();
            for (var i = committed.Count - 1; i >= 0; i--)
            {
                var item = committed[i];
                try
                {
                    TryDeleteDirectory(item.Destination);
                    if (!string.IsNullOrWhiteSpace(item.Backup) && Directory.Exists(item.Backup))
                        Directory.Move(item.Backup, item.Destination);
                }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }

            try
            {
                if (hadConfig)
                    File.WriteAllBytes(configPath, originalConfig);
                else if (File.Exists(configPath))
                    File.Delete(configPath);
            }
            catch (Exception ex) { rollbackErrors.Add(ex); }

            RefreshInstalled(false);
            if (rollbackErrors.Count > 0)
                throw new AggregateException("UE4SS batch installation failed and one or more rollback steps also failed. See the retained backups.", rollbackErrors);
            throw;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }
    public string UninstallUe4ssRuntime()
    {
        if (!HasValidGamePath)
            throw new InvalidOperationException("Select a valid Abiotic Factor game folder in Settings first.");
        if (IsGameRunning())
            throw new InvalidOperationException("Close Abiotic Factor before uninstalling UE4SS.");

        var win64 = Path.Combine(GameFilesDirectory, "Binaries", "Win64");
        var ue4ssRoot = Path.Combine(win64, "ue4ss");
        var loader = Path.Combine(win64, "dwmapi.dll");
        if (!Directory.Exists(ue4ssRoot) && !File.Exists(loader))
            throw new InvalidOperationException("UE4SS is not installed in the selected game folder.");

        var backupRoot = Path.Combine(_backupsRoot, "AF-UE4SS-uninstalled-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var snapshot = Path.Combine(backupRoot, "original-full-install");
        var moved = Path.Combine(backupRoot, "removed-runtime");
        Directory.CreateDirectory(snapshot);
        Directory.CreateDirectory(moved);

        if (Directory.Exists(ue4ssRoot))
            CopyDirectoryRecursive(ue4ssRoot, Path.Combine(snapshot, "ue4ss"));
        if (File.Exists(loader))
            File.Copy(loader, Path.Combine(snapshot, "dwmapi.dll"), true);

        try
        {
            if (Directory.Exists(ue4ssRoot))
            {
                var movedUe4ss = Path.Combine(moved, "ue4ss");
                Directory.CreateDirectory(movedUe4ss);

                foreach (var file in Directory.GetFiles(ue4ssRoot, "*", SearchOption.TopDirectoryOnly))
                    File.Move(file, Path.Combine(movedUe4ss, Path.GetFileName(file)));

                foreach (var directory in Directory.GetDirectories(ue4ssRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    // Retain Mods in place: uninstalling the loader should not discard or hide
                    // installed mods. They become active again when UE4SS is reinstalled.
                    if (Path.GetFileName(directory).Equals("Mods", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.Move(directory, Path.Combine(movedUe4ss, Path.GetFileName(directory)));
                }
            }

            if (File.Exists(loader))
                File.Move(loader, Path.Combine(moved, "dwmapi.dll"));
        }
        catch
        {
            // Roll back to the full snapshot if the move could not finish.
            var snapshotRoot = Path.Combine(snapshot, "ue4ss");
            if (Directory.Exists(snapshotRoot))
                CopyDirectoryRecursive(snapshotRoot, ue4ssRoot);
            var snapshotLoader = Path.Combine(snapshot, "dwmapi.dll");
            if (File.Exists(snapshotLoader))
                File.Copy(snapshotLoader, loader, true);
            throw;
        }

        RefreshUe4ssStatus();
        RefreshInstalled(false);
        GameSetupChanged?.Invoke();
        SetStatus("UE4SS runtime uninstalled. Full backup saved to " + snapshot + ". Existing UE4SS mods were retained.");
        return snapshot;
    }
    public async Task<string> InstallUe4ssRuntimeAsync()
    {
        if (!HasValidGamePath)
            throw new InvalidOperationException("Select a valid Abiotic Factor game folder in Settings first.");
        if (IsGameRunning())
            throw new InvalidOperationException("Close Abiotic Factor before installing or updating UE4SS.");

        SetStatus("Checking the latest AF-UE4SS release...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AbioticFactorModManager/0.0.1");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var releaseResponse = await http.GetAsync("https://api.github.com/repos/igromanru/AF-UE4SS/releases/latest");
        releaseResponse.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await releaseResponse.Content.ReadAsStringAsync());

        var tag = release.RootElement.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "latest" : "latest";
        if (!release.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub did not provide AF-UE4SS release assets.");

        string? assetUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() ?? "" : "";
            if (name.Contains("_Abiotic_Factor.zip", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Debug", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                assetUrl = url;
                break;
            }
        }

        if (assetUrl == null)
            throw new InvalidOperationException("The latest AF-UE4SS release did not contain its Abiotic Factor runtime ZIP.");

        SetStatus("Downloading AF-UE4SS " + tag + "...");
        var archivePath = Path.Combine(_downloadsRoot, "AF-UE4SS-" + SafeFileName(tag, "latest") + ".zip");
        await DownloadFileLimitedAsync(http, assetUrl, archivePath, 100L * 1024 * 1024);

        var temp = Path.Combine(_dataRoot, "ue4ss_runtime_stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await Task.Run(() => ExtractZipSafely(archivePath, temp));
            var sourceRoot = Path.Combine(temp, "ue4ss");
            var sourceLoader = Path.Combine(temp, "dwmapi.dll");
            if (!File.Exists(Path.Combine(sourceRoot, "UE4SS.dll")) || !File.Exists(sourceLoader))
                throw new InvalidOperationException("The AF-UE4SS archive did not contain ue4ss/UE4SS.dll and the dwmapi.dll loader.");

            var win64 = Path.Combine(GameFilesDirectory, "Binaries", "Win64");
            var destinationRoot = Path.Combine(win64, "ue4ss");
            var destinationLoader = Path.Combine(win64, "dwmapi.dll");
            var backupRoot = Path.Combine(_backupsRoot, "AF-UE4SS-before-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(backupRoot);

            bool hadRuntime = Directory.Exists(destinationRoot);
            bool hadLoader = File.Exists(destinationLoader);
            if (hadRuntime)
                CopyDirectoryRecursive(destinationRoot, Path.Combine(backupRoot, "ue4ss"));
            if (hadLoader)
                File.Copy(destinationLoader, Path.Combine(backupRoot, "dwmapi.dll"), true);

            try
            {
                MergeAfUe4ssRuntime(sourceRoot, destinationRoot);
                File.Copy(sourceLoader, destinationLoader, true);

                RefreshUe4ssStatus();
                if (!_ue4ssInstalled)
                    throw new InvalidOperationException("UE4SS files were copied, but the runtime verification check did not pass.");
            }
            catch (Exception installError)
            {
                try
                {
                    if (Directory.Exists(destinationRoot))
                        Directory.Delete(destinationRoot, true);
                    if (hadRuntime && Directory.Exists(Path.Combine(backupRoot, "ue4ss")))
                        CopyDirectoryRecursive(Path.Combine(backupRoot, "ue4ss"), destinationRoot);

                    if (hadLoader && File.Exists(Path.Combine(backupRoot, "dwmapi.dll")))
                        File.Copy(Path.Combine(backupRoot, "dwmapi.dll"), destinationLoader, true);
                    else if (File.Exists(destinationLoader))
                        File.Delete(destinationLoader);

                    RefreshUe4ssStatus();
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("UE4SS installation failed and automatic rollback was incomplete. The pre-install backup was retained at " + backupRoot + ".", installError, rollbackError);
                }
                throw;
            }

            File.WriteAllText(Path.Combine(_dataRoot, "ue4ss-runtime-installed.flag"), GameFilesDirectory, new UTF8Encoding(false));
            RefreshInstalled(false);
            GameSetupChanged?.Invoke();
            SetStatus("Installed AF-UE4SS " + tag + ". Existing custom UE4SS mods were preserved.");
            return tag;
        }
        finally
        {
            TryDeleteDirectory(temp);
            TryDeleteFile(archivePath);
        }
    }

    private static void MergeAfUe4ssRuntime(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir);
            var target = Path.Combine(destination, name);
            if (name.Equals("Mods", StringComparison.OrdinalIgnoreCase))
            {
                MergeAfUe4ssMods(dir, target);
            }
            else
            {
                CopyDirectoryRecursive(dir, target);
            }
        }
    }

    private static void MergeAfUe4ssMods(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (Path.GetFileName(file).Equals("mods.txt", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            {
                var existing = File.ReadAllLines(target).ToList();
                var names = new HashSet<string>(existing.Select(ParseUe4ssConfigName).Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(file))
                {
                    var name = ParseUe4ssConfigName(line);
                    if (!string.IsNullOrWhiteSpace(name) && names.Add(name))
                        existing.Add(line);
                }
                File.WriteAllLines(target, existing, new UTF8Encoding(false));
            }
            else if (!File.Exists(target))
            {
                File.Copy(file, target, false);
            }
        }

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(destination, Path.GetFileName(dir));
            // Preserve any existing mod directory, including user edits and custom mods.
            if (Directory.Exists(target)) continue;
            CopyDirectoryRecursive(dir, target);
        }
    }

    private static async Task DownloadFileLimitedAsync(HttpClient http, string url, string output, long maxBytes)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only secure HTTPS download links are accepted.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maxBytes))
                throw new InvalidOperationException("The AF-UE4SS download had an unexpected size.");

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException("The AF-UE4SS download exceeded the 100 MB size limit.");
                await file.WriteAsync(buffer.AsMemory(0, read));
            }
            if (total == 0)
                throw new InvalidOperationException("The AF-UE4SS download was empty.");
            await file.FlushAsync();
        }
        catch
        {
            TryDeleteFile(output);
            throw;
        }
    }
    private static string ParseUe4ssConfigName(string line)
    {
        var match = Regex.Match(line ?? "", @"^\s*(?<name>[^:#]+?)\s*:\s*[01]\s*$");
        return match.Success ? match.Groups["name"].Value.Trim() : "";
    }
}