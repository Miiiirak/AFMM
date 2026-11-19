using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    private const int MaxModConfigBytes = 256 * 1024;

    public IReadOnlyList<ModConfigFile> GetModConfigFiles(InstalledMod mod)
    {
        var root = GetModFolder(mod);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<ModConfigFile>();

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var results = new List<ModConfigFile>();
        IEnumerable<string> paths;

        try
        {
            paths = Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories).ToList();
        }
        catch
        {
            return results;
        }

        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) continue;

                var info = new FileInfo(fullPath);
                if (info.Length > MaxModConfigBytes) continue;

                var fileName = info.Name;
                var lowerName = fileName.ToLowerInvariant();
                var extension = info.Extension.ToLowerInvariant();
                var relative = Path.GetRelativePath(rootFull, fullPath);
                var inConfigDirectory = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part.Equals("Config", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("Configs", StringComparison.OrdinalIgnoreCase));
                var recognizedName = lowerName is "config.ini" or "settings.ini" or
                    "config.json" or "settings.json" or "modconfig.ini" or "modsettings.ini";

                if (extension is not ".ini" and not ".json") continue;
                if (!recognizedName && !inConfigDirectory) continue;
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part.Equals("Backup", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("Backups", StringComparison.OrdinalIgnoreCase) ||
                                 part.StartsWith(".", StringComparison.Ordinal)))
                    continue;

                results.Add(new ModConfigFile
                {
                    Name = relative.Replace(Path.DirectorySeparatorChar, '/'),
                    FullPath = fullPath,
                    Content = File.ReadAllText(fullPath, Encoding.UTF8)
                });
            }
            catch
            {
                // Ignore inaccessible or transient files. Other configs remain available.
            }
        }

        return results
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? GetModConfigValue(InstalledMod mod, string fileName, string key)
    {
        var config = GetModConfigFiles(mod).FirstOrDefault(x =>
            Path.GetFileName(x.FullPath).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (config == null) return null;

        foreach (var line in config.Content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
            var equals = line.IndexOf('=');
            if (equals < 0) continue;
            if (line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(equals + 1)..].Split(';', '#')[0].Trim();
        }

        return null;
    }

    public bool GetModConfigBoolean(InstalledMod mod, string fileName, string key, bool defaultValue)
    {
        var value = GetModConfigValue(mod, fileName, key);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            value == "1")
            return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value == "0")
            return false;
        return defaultValue;
    }

    public void SetModConfigValue(InstalledMod mod, string fileName, string key, string value)
    {
        var config = GetModConfigFiles(mod).FirstOrDefault(x =>
            Path.GetFileName(x.FullPath).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (config == null)
            throw new FileNotFoundException("The requested mod configuration file was not found.", fileName);

        var lines = config.Content.Replace("\r\n", "\n").Split('\n').ToList();
        var output = new List<string>(lines.Count + 1);
        var found = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#"))
            {
                output.Add(line);
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals >= 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                if (!found)
                {
                    output.Add(line[..(equals + 1)] + value);
                    found = true;
                }
                // Drop any duplicate definitions of this key so the saved
                // config always has one unambiguous value.
                continue;
            }

            output.Add(line);
        }

        if (!found) output.Add(key + "=" + value);
        SaveModConfig(mod, config.FullPath, string.Join(Environment.NewLine, output));
    }

    public void SaveModConfig(InstalledMod mod, string fullPath, string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxModConfigBytes)
            throw new InvalidOperationException("The configuration file is too large to edit in the manager (maximum 256 KB).");

        if (Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { using var _ = JsonDocument.Parse(content); }
            catch (JsonException ex) { throw new InvalidOperationException("This JSON configuration is not valid: " + ex.Message, ex); }
        }
        var config = GetModConfigFiles(mod).FirstOrDefault(x =>
            Path.GetFullPath(x.FullPath).Equals(Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase));
        if (config == null)
            throw new InvalidOperationException("The selected file is not a recognized configuration file inside this mod.");

        var safeName = Regex.Replace(mod.PluginName.Length > 0 ? mod.PluginName : mod.Id, "[^A-Za-z0-9_-]", "_");
        var backupFolder = Path.Combine(_backupsRoot, "ModConfigs");
        Directory.CreateDirectory(backupFolder);
        var backup = Path.Combine(backupFolder,
            safeName + "_" + Path.GetFileName(config.FullPath) + "_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".bak");
        File.Copy(config.FullPath, backup, false);

        var temp = config.FullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(config.FullPath))
                File.Replace(temp, config.FullPath, null, true);
            else
                File.Move(temp, config.FullPath);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private string? GetModFolder(InstalledMod mod)
    {
        if (string.Equals(mod.SourceType, "UE4SS", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Ue4ssModsRoot, mod.PluginName);
        if (string.Equals(mod.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(ModsRoot, mod.Id);
        return null;
    }
}
