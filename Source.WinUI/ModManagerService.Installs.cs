using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    public async Task<int> CheckForUpdatesAsync()
    {
        if (!HasApiKey) throw new InvalidOperationException("Add your Mod.io API key in Settings to check for updates.");
        var candidates = _installed.Where(x => IsModIo(x) && !x.LocalOnly).ToList();
        var updates = 0;
        var checkedCount = 0;
        var failures = new List<string>();
        foreach (var mod in candidates)
        {
            mod.Checking = true;
            InstalledChanged?.Invoke();
            try
            {
                if (!long.TryParse(mod.Id, out var modId)) continue;
                var latest = await GetLatestWindowsFileAsync(modId);
                mod.AvailableFileId = latest.Id;
                mod.AvailableFileName = latest.FileName;
                mod.AvailableBinaryUrl = latest.BinaryUrl;
                mod.AvailableVersion = latest.Version;
                mod.AvailableDateAdded = latest.DateAdded;
                mod.LastUpdateCheck = DateTime.Now;

                if (latest.Id > 0 && mod.FileId > 0)
                    mod.UpdateAvailable = latest.Id != mod.FileId;
                else if (!string.IsNullOrWhiteSpace(latest.Version) && !string.IsNullOrWhiteSpace(mod.Version))
                    mod.UpdateAvailable = !string.Equals(latest.Version.Trim(), mod.Version.Trim(), StringComparison.OrdinalIgnoreCase);
                else
                    mod.UpdateAvailable = latest.DateAdded > ToUnix(mod.InstalledAt);

                if (mod.FileId == 0 && !mod.UpdateAvailable)
                {
                    mod.FileId = latest.Id;
                    mod.FileDateAdded = latest.DateAdded;
                }
                if (mod.UpdateAvailable) updates++;
                checkedCount++;
            }
            catch (Exception ex)
            {
                failures.Add(mod.Name + ": " + ex.Message);
                SetStatus("Couldn't check " + mod.Name + ": " + ex.Message);
            }
            finally
            {
                mod.Checking = false;
                InstalledChanged?.Invoke();
            }
        }
        updates = candidates.Count(mod => mod.UpdateAvailable);
        SaveInstalledState();
        SetStatus("Checked " + checkedCount + " Mod.io mods - " + updates + " update(s) available.");
        if (failures.Count > 0)
            throw new InvalidOperationException("Checked " + checkedCount + " mod(s), but " + failures.Count + " could not be checked:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        return updates;
    }

    public async Task<int> UpdateModsAsync(IEnumerable<InstalledMod> selected)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before updating mods.");
        var targets = selected.Where(x => IsModIo(x) && x.UpdateAvailable).ToList();
        var updated = 0;
        var failures = new List<string>();
        foreach (var mod in targets)
        {
            try
            {
                if (!long.TryParse(mod.Id, out var modId)) continue;
                var file = mod.AvailableFileId > 0 && !string.IsNullOrWhiteSpace(mod.AvailableBinaryUrl)
                    ? new ModFileRecord
                    {
                        Id = mod.AvailableFileId,
                        FileName = mod.AvailableFileName,
                        Version = mod.AvailableVersion,
                        BinaryUrl = mod.AvailableBinaryUrl,
                        DateAdded = mod.AvailableDateAdded
                    }
                    : await GetLatestWindowsFileAsync(modId);
                SetStatus("Updating " + mod.Name + "…");
                var archive = await DownloadToCacheAsync(file.BinaryUrl, file.FileName);
                try { InstallArchive(archive, modId, mod.Name, file, mod.SourceUrl, false); }
                finally { TryDeleteFile(archive); }
                updated++;
            }
            catch (Exception ex)
            {
                failures.Add(mod.Name + ": " + ex.Message);
                SetStatus("Couldn't update " + mod.Name + ": " + ex.Message);
            }
        }
        RefreshInstalled(true);
        if (failures.Count > 0)
            throw new InvalidOperationException("Updated " + updated + " of " + targets.Count + " mod(s). The following update(s) failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        return updated;
    }

    public async Task<InstalledMod> InstallFromModIoAsync(BrowseMod mod)
    {
                if (!HasValidGamePath) throw new InvalidOperationException("Select a valid Abiotic Factor game folder in Settings first.");
if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before installing mods.");
        SetStatus("Finding the latest Windows release for " + mod.Name + "…");
        var file = await GetLatestWindowsFileAsync(mod.Id);
        var archive = await DownloadToCacheAsync(file.BinaryUrl, file.FileName);
        try { InstallArchive(archive, mod.Id, mod.Name, file, mod.Url, true); }
        finally { TryDeleteFile(archive); }
        RefreshInstalled(true);
        return _installed.First(x => x.Id == mod.Id.ToString());
    }

    public async Task<InstalledMod> InstallFromLinkAsync(string text)
    {
                if (!HasValidGamePath) throw new InvalidOperationException("Select a valid Abiotic Factor game folder in Settings first.");
text = text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Paste a complete HTTPS Mod.io page or ZIP download link.");

        var isModPage = IsModIoHost(uri.Host) &&
            uri.AbsolutePath.Contains("/m/", StringComparison.OrdinalIgnoreCase);
        if (isModPage)
        {
            var modId = await ResolveModIdFromPageAsync(uri);
            var mod = await GetModDetailsAsync(modId);
            return await InstallByIdAsync(modId, mod.Name, mod.Url, await GetLatestWindowsFileAsync(modId));
        }

        var id = ParseModId(text);
        if (id <= 0)
            throw new InvalidOperationException("This direct ZIP URL doesn't contain a Mod.io ID. Use the mod page link, or choose a local ZIP and provide the ID only if it cannot be detected from its filename or plugin metadata.");

        if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before installing mods.");
        var details = await GetModDetailsAsync(id);
        var fileName = SafeFileName(Path.GetFileName(uri.LocalPath), "mod_" + id + ".zip");
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) fileName += ".zip";
        var archive = await DownloadToCacheAsync(uri.ToString(), fileName);
        var record = new ModFileRecord { FileName = fileName, Version = "" };
        try { InstallArchive(archive, id, details.Name, record, text, true); }
        finally { TryDeleteFile(archive); }
        RefreshInstalled(true);
        return _installed.First(x => x.Id == id.ToString());
    }

    private async Task<InstalledMod> InstallByIdAsync(long modId, string name, string source, ModFileRecord file)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before installing mods.");
        var archive = await DownloadToCacheAsync(file.BinaryUrl, file.FileName);
        try { InstallArchive(archive, modId, name, file, source, true); }
        finally { TryDeleteFile(archive); }
        RefreshInstalled(true);
        return _installed.First(x => x.Id == modId.ToString());
    }

    public async Task<InstalledMod> InstallLocalZipAsync(string zipPath, long modId, string? name = null)
    {
                if (!HasValidGamePath) throw new InvalidOperationException("Select a valid Abiotic Factor game folder in Settings first.");
if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before installing mods.");
        if (!File.Exists(zipPath)) throw new FileNotFoundException("ZIP archive not found.", zipPath);
        if (modId <= 0) throw new InvalidOperationException("A numeric Mod.io ID is required for this ZIP.");
        var metadata = await Task.Run(() => ReadArchiveMetadata(zipPath));
        if (string.IsNullOrWhiteSpace(name)) name = metadata.FriendlyName;
        if (string.IsNullOrWhiteSpace(name)) name = "Mod " + modId;
        InstallArchive(zipPath, modId, name, new ModFileRecord { FileName = Path.GetFileName(zipPath), Version = metadata.Version }, "Local ZIP: " + Path.GetFileName(zipPath), true);
        RefreshInstalled(true);
        return _installed.First(x => x.Id == modId.ToString());
    }

private void InstallArchive(string archivePath, long modId, string name, ModFileRecord file, string sourceUrl, bool enableNew)
    {
                if (!HasValidGamePath) throw new InvalidOperationException("Select a valid Abiotic Factor game folder in Settings first.");
if (modId <= 0) throw new InvalidOperationException("A numeric Mod.io ID is required.");
        if (IsGameRunning()) throw new InvalidOperationException("Close Abiotic Factor before installing or replacing mods.");
        var extraction = Path.Combine(_dataRoot, "extract_" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(ModsRoot, ".stage-" + modId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extraction);
        Directory.CreateDirectory(ModsRoot);
        var movedOld = "";
        try
        {
            ExtractZipSafely(archivePath, extraction);
            var descriptors = Directory.GetFiles(extraction, "*.uplugin", SearchOption.AllDirectories);
            if (descriptors.Length != 1)
                throw new InvalidOperationException("A Mod.io Windows mod ZIP must contain exactly one .uplugin descriptor.");
            var pluginRoot = Path.GetDirectoryName(descriptors[0])!;
            var windowsPaks = FindWindowsPakFolder(pluginRoot);
            if (windowsPaks == null)
                throw new InvalidOperationException("This archive isn't a Windows cooked mod. It must include Content/Paks/Windows.");
            var paks = Directory.GetFiles(windowsPaks, "*.pak");
            var ucas = Directory.GetFiles(windowsPaks, "*.ucas");
            var utoc = Directory.GetFiles(windowsPaks, "*.utoc");
            if (paks.Length == 0 || ucas.Length == 0 || utoc.Length == 0)
                throw new InvalidOperationException("The cooked plugin is incomplete: .pak, .ucas and .utoc are all required.");
            var metadata = ReadPluginInfo(descriptors[0], name, file.Version);
            Directory.CreateDirectory(stage);
            CopyDirectoryContents(pluginRoot, stage);

            var target = Path.Combine(ModsRoot, modId.ToString());
            var previous = _installed.FirstOrDefault(x => x.Id == modId.ToString());
            var wasEnabled = previous?.Enabled ?? enableNew;
            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(_backupsRoot);
                movedOld = Path.Combine(_backupsRoot, "update_" + modId + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, movedOld);
            }
            try { Directory.Move(stage, target); }
            catch
            {
                if (!string.IsNullOrEmpty(movedOld) && Directory.Exists(movedOld) && !Directory.Exists(target))
                    Directory.Move(movedOld, target);
                throw;
            }

            var record = previous ?? new InstalledMod { Id = modId.ToString(), InstalledAt = DateTime.Now, SourceType = "Mod.io" };
            record.Name = !string.IsNullOrWhiteSpace(name) && !name.StartsWith("Mod ", StringComparison.Ordinal) ? name : metadata.FriendlyName;
            record.Version = !string.IsNullOrWhiteSpace(file.Version) ? file.Version : metadata.Version;
            record.PluginName = metadata.PluginName;
            record.LocalOnly = metadata.LocalOnly;
            record.SourceUrl = sourceUrl;
            record.Enabled = wasEnabled;
            record.SourceType = "Mod.io";
            record.InstalledByManager = true;
            record.FileId = file.Id;
            record.FileDateAdded = file.DateAdded;
            record.UpdateAvailable = false;
            record.AvailableFileId = 0;
            record.AvailableFileName = "";
            record.AvailableBinaryUrl = "";
            record.AvailableVersion = "";
            record.AvailableDateAdded = 0;
            _installed.RemoveAll(x => x.Id == record.Id);
            _installed.Add(record);
            SaveInstalledState();
            SetStatus("Installed " + record.Name + " · " + paks.Length + " PAK, " + ucas.Length + " UCAS, " + utoc.Length + " UTOC file(s).");
        }
        finally
        {
            TryDeleteDirectory(extraction);
            TryDeleteDirectory(stage);
        }
    }

    private static (long ModId, string FriendlyName, string Version) ReadArchiveMetadata(string zip)
    {
        var temp = Path.Combine(Path.GetTempPath(), "AFMM-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ExtractZipSafely(zip, temp);
            var descriptor = Directory.GetFiles(temp, "*.uplugin", SearchOption.AllDirectories).FirstOrDefault();
            if (descriptor == null) return (0, "", "");
            var info = ReadPluginInfo(descriptor, "", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(descriptor));
            var root = doc.RootElement;
            var id = new[] { "ModId", "ModID", "ModIoId", "ModIOId", "modio_id" }
                .Select(key => GetLong(root, key)).FirstOrDefault(x => x > 0);
            return (id, info.FriendlyName, info.Version);
        }
        finally { TryDeleteDirectory(temp); }
    }

    private static (string FriendlyName, string Version, string PluginName, bool LocalOnly) ReadPluginInfo(string descriptor, string name, string version)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(descriptor));
        var root = doc.RootElement;
        var friendly = FirstNonBlank(GetText(root, "FriendlyName"), Path.GetFileNameWithoutExtension(descriptor), name);
        var pluginVersion = FirstNonBlank(GetText(root, "VersionName"), GetText(root, "Version"), version);
        var localOnly = root.TryGetProperty("LocalOnly", out var local) && local.ValueKind == JsonValueKind.True;
        return (friendly, pluginVersion, Path.GetFileNameWithoutExtension(descriptor), localOnly);
    }

    private static string? FindWindowsPakFolder(string root)
    {
        var exact = Path.Combine(root, "Content", "Paks", "Windows");
        if (Directory.Exists(exact)) return exact;
        return Directory.GetDirectories(root, "Windows", SearchOption.AllDirectories)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(Directory.GetParent(p)?.FullName), "Paks", StringComparison.OrdinalIgnoreCase));
    }

    private static void ExtractZipSafely(string zip, string destination)
    {
        const int maxEntries = 20000;
        const long maxArchiveBytes = 2L * 1024 * 1024 * 1024;
        const long maxEntryBytes = 2L * 1024 * 1024 * 1024;
        const long maxTotalExpandedBytes = 4L * 1024 * 1024 * 1024;
        const double maxCompressionRatio = 1000.0;

        var archiveInfo = new FileInfo(zip);
        if (!archiveInfo.Exists) throw new FileNotFoundException("ZIP archive not found.", zip);
        if (archiveInfo.Length > maxArchiveBytes)
            throw new InvalidOperationException("The ZIP archive is larger than the 2 GB compressed-size limit.");

        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > maxEntries)
            throw new InvalidOperationException("The ZIP contains too many entries (limit: 20,000).");

        long totalExpandedBytes = 0;
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName)) continue;
            if (entry.Length < 0 || entry.Length > maxEntryBytes)
                throw new InvalidOperationException("The ZIP contains an entry larger than the 2 GB extraction limit.");

            totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
            if (totalExpandedBytes > maxTotalExpandedBytes)
                throw new InvalidOperationException("The ZIP expands beyond the 4 GB total extraction limit.");

            var compressed = Math.Max(1L, entry.CompressedLength);
            if (entry.Length > 64L * 1024 * 1024 && entry.Length / (double)compressed > maxCompressionRatio)
                throw new InvalidOperationException("The ZIP contains an entry with a suspicious compression ratio.");

            var attributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || unixType == 0xA000)
                throw new InvalidOperationException("ZIP symbolic links and reparse points are not supported.");

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Contains(':') ||
                relative.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
                throw new InvalidOperationException("ZIP contains an absolute path or path traversal.");

            var output = Path.GetFullPath(Path.Combine(destination, relative));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ZIP path traversal was rejected.");
            if (!extractedPaths.Add(output))
                throw new InvalidOperationException("The ZIP contains duplicate file or directory paths.");

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(output);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var input = entry.Open();
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            var buffer = new byte[65536];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written = checked(written + read);
                if (written > entry.Length || written > maxEntryBytes)
                    throw new InvalidOperationException("ZIP entry data exceeded its declared size.");
                file.Write(buffer, 0, read);
            }
            if (written != entry.Length)
                throw new InvalidOperationException("ZIP entry data did not match its declared size.");
        }
    }
    private static void CopyDirectoryContents(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
            CopyDirectoryRecursive(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static void TryDeleteFile(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
    private static void TryDeleteDirectory(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
}