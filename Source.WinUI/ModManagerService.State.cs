using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    public void RefreshInstalled(bool syncSteam = false)
    {
        if (!HasValidGamePath)
        {
            _installed = new List<InstalledMod>();
            InstalledChanged?.Invoke();
            SetStatus("Select a valid Abiotic Factor game folder in Settings to manage installed mods.");
            return;
        }
        try
        {
            Directory.CreateDirectory(ModsRoot);
            Directory.CreateDirectory(Ue4ssModsRoot);
            var saved = ReadInstalledState();
            var found = new List<InstalledMod>();

            foreach (var folder in Directory.GetDirectories(ModsRoot))
            {
                var id = Path.GetFileName(folder);
                if (!long.TryParse(id, out _)) continue;
                var descriptor = Directory.GetFiles(folder, "*.uplugin", SearchOption.AllDirectories).FirstOrDefault();
                if (descriptor == null) continue;
                var metadata = ReadPluginInfo(descriptor, Path.GetFileNameWithoutExtension(descriptor), "");
                var previous = saved.FirstOrDefault(x => x.Id == id && IsModIo(x));
                var record = previous ?? new InstalledMod { Id = id, Name = metadata.FriendlyName, Version = metadata.Version };
                if (string.IsNullOrWhiteSpace(record.Name) || record.Name.StartsWith("Mod ", StringComparison.Ordinal))
                    record.Name = metadata.FriendlyName;
                if (string.IsNullOrWhiteSpace(record.Version)) record.Version = metadata.Version;
                record.PluginName = metadata.PluginName;
                record.LocalOnly = metadata.LocalOnly;
                record.SourceType = "Mod.io";
                found.Add(record);
            }

            foreach (var folder in Directory.GetDirectories(Ue4ssModsRoot))
            {
                if (!IsUe4ssModFolder(folder)) continue;
                var name = Path.GetFileName(folder);
                var id = "ue4ss:" + name;
                var previous = saved.FirstOrDefault(x => x.Id == id && string.Equals(x.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase));
                var record = previous ?? new InstalledMod { Id = id, Name = FormatUe4ssName(name), PluginName = name, SourceType = "UE4SS" };
                record.Id = id;
                record.Name = FormatUe4ssName(name);
                record.PluginName = name;
                record.Version = "1.0";
                record.LocalOnly = false;
                record.SourceType = "UE4SS";
                record.Enabled = IsUe4ssModEnabled(name);
                record.UpdateAvailable = false;
                found.Add(record);
            }

            _installed = found.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            SaveInstalledState();
            InstalledChanged?.Invoke();
            if (syncSteam) SynchronizeSteamLaunchOptions();
            SetStatus($"{_installed.Count} installed mods · {CountUpdates()} updates available.");
        }
        catch (Exception ex) { SetStatus("Couldn't refresh installed mods: " + ex.Message); }
    }

    private List<InstalledMod> ReadInstalledState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return new();
            return JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(_stateFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }

    private void SaveInstalledState()
    {
        var temp = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp,
                JsonSerializer.Serialize(_installed, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _stateFile, true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            SetStatus("Couldn't save the mod list: " + ex.Message);
        }
    }

    public int CountUpdates() => _installed.Count(x => x.UpdateAvailable && IsModIo(x));

    public void SetEnabled(InstalledMod mod, bool enabled)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before changing mod enablement.");
        if (!_installed.Contains(mod)) return;
        if (string.Equals(mod.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase))
            SetUe4ssModEnabled(mod.PluginName, enabled);
        mod.Enabled = enabled;
        SaveInstalledState();
        InstalledChanged?.Invoke();
        if (IsModIo(mod)) SynchronizeSteamLaunchOptions();
        SetStatus(mod.Name + (enabled ? " enabled" : " disabled") + " · applies on the next game launch.");
    }

    private static bool IsModIo(InstalledMod mod)
        => !string.Equals(mod.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase) && long.TryParse(mod.Id, out _);

    private bool IsUe4ssModFolder(string folder)
    {
        var name = Path.GetFileName(folder);
        if (string.IsNullOrWhiteSpace(name) || name.Equals("shared", StringComparison.OrdinalIgnoreCase)) return false;
        var scripts = Path.Combine(folder, "Scripts");
        var lua = Directory.Exists(scripts) && Directory.GetFiles(scripts, "*.lua", SearchOption.AllDirectories).Length > 0;
        var dlls = Path.Combine(folder, "dlls");
        var dllMod = Directory.Exists(dlls) && Directory.GetFiles(dlls, "*.dll", SearchOption.AllDirectories).Length > 0;
        var rootDll = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).Length > 0;
        var rootLua = File.Exists(Path.Combine(folder, "main.lua"));
        return lua || dllMod || rootDll || rootLua;
    }

    private bool IsUe4ssModEnabled(string name)
    {
        var folder = Path.Combine(Ue4ssModsRoot, name);
        if (File.Exists(Path.Combine(folder, "disabled.txt"))) return false;
        if (File.Exists(Path.Combine(folder, "enabled.txt"))) return true;
        var config = Path.Combine(Ue4ssModsRoot, "mods.txt");
        if (File.Exists(config))
        {
            var pattern = @"^\s*" + Regex.Escape(name) + @"\s*:\s*(?<enabled>[01])\s*$";
            foreach (var line in File.ReadAllLines(config))
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups["enabled"].Value == "1";
            }
        }
        return false;
    }

    private void SetUe4ssModEnabled(string name, bool enabled)
    {
        var folder = Path.Combine(Ue4ssModsRoot, name);
        if (!Directory.Exists(folder) || !IsUe4ssModFolder(folder))
            throw new DirectoryNotFoundException("UE4SS mod folder not found: " + folder);

        var marker = Path.Combine(folder, "enabled.txt");
        var disabled = Path.Combine(folder, "disabled.txt");
        var config = Path.Combine(Ue4ssModsRoot, "mods.txt");
        var markerSnapshot = CaptureFile(marker);
        var disabledSnapshot = CaptureFile(disabled);
        var configSnapshot = CaptureFile(config);

        try
        {
            if (enabled)
            {
                if (!File.Exists(marker)) File.WriteAllBytes(marker, Array.Empty<byte>());
                if (File.Exists(disabled)) File.Delete(disabled);
            }
            else
            {
                if (File.Exists(marker)) File.Delete(marker);
                if (File.Exists(disabled)) File.Delete(disabled);
            }

            var lines = File.Exists(config) ? File.ReadAllLines(config).ToList() : new List<string>();
            var pattern = @"^\s*" + Regex.Escape(name) + @"\s*:\s*[01]\s*$";
            var matched = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!Regex.IsMatch(lines[i], pattern, RegexOptions.IgnoreCase)) continue;
                lines[i] = name + " : " + (enabled ? "1" : "0");
                matched = true;
            }
            if (!matched) lines.Add(name + " : " + (enabled ? "1" : "0"));

            var temp = config + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, lines, new UTF8Encoding(false));
                File.Move(temp, config, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch (Exception operationError)
        {
            try
            {
                RestoreFile(marker, markerSnapshot);
                RestoreFile(disabled, disabledSnapshot);
                RestoreFile(config, configSnapshot);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("UE4SS mod enablement failed and the previous state could not be fully restored.", operationError, rollbackError);
            }
            throw;
        }
    }

    private static (bool Exists, byte[] Content) CaptureFile(string path)
        => (File.Exists(path), File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>());

    private static void RestoreFile(string path, (bool Exists, byte[] Content) snapshot)
    {
        if (!snapshot.Exists)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, snapshot.Content);
    }
    private static string FormatUe4ssName(string value)
    {
        var spaced = Regex.Replace(value, @"([a-z0-9])([A-Z])", "$1 $2");
        spaced = Regex.Replace(spaced, @"([A-Z])([A-Z][a-z])", "$1 $2");
        return Regex.Replace(spaced.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
    }

    public void UninstallMods(IEnumerable<InstalledMod> selected)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before uninstalling mods.");
        var targets = selected.Where(_installed.Contains).Distinct().ToList();
        if (targets.Count == 0) return;

        var originalInstalled = _installed.ToList();
        var syncSteam = targets.Any(IsModIo);
        var movedFolders = new List<(string Source, string Backup)>();
        var capturedFiles = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);

        void CaptureFile(string path)
        {
            if (!capturedFiles.ContainsKey(path))
                capturedFiles[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        void RestoreCapturedFiles()
        {
            foreach (var pair in capturedFiles)
            {
                if (pair.Value == null)
                {
                    if (File.Exists(pair.Key)) File.Delete(pair.Key);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                File.WriteAllBytes(pair.Key, pair.Value);
            }
        }

        var hasUe4ss = targets.Any(mod => string.Equals(mod.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase));
        if (hasUe4ss)
            CaptureFile(Path.Combine(Ue4ssModsRoot, "mods.txt"));

        try
        {
            foreach (var mod in targets)
            {
                var ue4ss = string.Equals(mod.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase);
                var folder = ue4ss ? Path.Combine(Ue4ssModsRoot, mod.PluginName) : Path.Combine(ModsRoot, mod.Id);
                if (ue4ss)
                {
                    CaptureFile(Path.Combine(folder, "enabled.txt"));
                    CaptureFile(Path.Combine(folder, "disabled.txt"));
                    if (Directory.Exists(folder)) SetUe4ssModEnabled(mod.PluginName, false);
                }

                if (Directory.Exists(folder))
                {
                    var safe = SafeFileName(mod.PluginName, mod.Id);
                    var backup = Path.Combine(_backupsRoot, "uninstall_" + safe + "_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6]);
                    Directory.CreateDirectory(_backupsRoot);
                    Directory.Move(folder, backup);
                    movedFolders.Add((folder, backup));
                }
                _installed.RemoveAll(x => x.Id == mod.Id);
            }

            SaveInstalledState();
            RefreshInstalled(syncSteam);
            SetStatus("Uninstalled " + targets.Count + " mod(s). Backups were retained.");
        }
        catch (Exception uninstallError)
        {
            var rollbackErrors = new List<Exception>();
            for (var i = movedFolders.Count - 1; i >= 0; i--)
            {
                var move = movedFolders[i];
                try
                {
                    if (Directory.Exists(move.Backup))
                    {
                        if (Directory.Exists(move.Source))
                            throw new IOException("The original mod folder was recreated during rollback: " + move.Source);
                        Directory.Move(move.Backup, move.Source);
                    }
                }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }

            try { RestoreCapturedFiles(); }
            catch (Exception ex) { rollbackErrors.Add(ex); }

            _installed = originalInstalled;
            SaveInstalledState();
            RefreshInstalled(syncSteam);
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Mod uninstall failed and automatic rollback was incomplete. Uninstall backups were retained.", new[] { uninstallError }.Concat(rollbackErrors));
            throw;
        }
    }
}
