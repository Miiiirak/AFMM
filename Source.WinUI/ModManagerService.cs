using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    public const int SteamAppId = 427410;
    private const int GameId = 13105;
    private const string GameSlug = "abiotic-factor";
    private const string ApiBase = "https://g-13105.modapi.io/v1/";
    public string ModsRoot => HasValidGamePath ? Path.Combine(GameFilesDirectory, "Mods") : Path.Combine(_dataRoot, "UnconfiguredGame", "Mods");
    public string Ue4ssModsRoot => HasValidGamePath ? Path.Combine(GameFilesDirectory, "Binaries", "Win64", "ue4ss", "Mods") : Path.Combine(_dataRoot, "UnconfiguredGame", "Binaries", "Win64", "ue4ss", "Mods");

    private readonly string _baseRoot;
    private readonly string _dataRoot;
    private readonly string _downloadsRoot;
    private readonly string _backupsRoot;
    private readonly string _keyFile;
    private readonly string _stateFile;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private string _apiKey = "";
    private bool _apiKeyValidated;
    private int _gameId;
    private List<InstalledMod> _installed = new();

    public event Action<string>? StatusChanged;
    public event Action? InstalledChanged;
    public event Action? ApiKeyChanged;

    public IReadOnlyList<InstalledMod> Installed => _installed;
    public string ApiKey => _apiKey;
    public bool HasApiKey => _apiKey.Length >= 16;
    public bool IsApiKeyValid => HasApiKey && _apiKeyValidated;
    public string ManagerDownloadsFolder => _downloadsRoot;
    public string ModsFolder => ModsRoot;
    public string BackupsFolder => _backupsRoot;

    public ModManagerService()
    {
        _baseRoot = ResolveBaseRoot();
        _dataRoot = Path.Combine(_baseRoot, "Data");
        _downloadsRoot = Path.Combine(_dataRoot, "Downloads");
        _backupsRoot = Path.Combine(_dataRoot, "Backups");
        _keyFile = Path.Combine(_dataRoot, "modio-key.dat");
        _stateFile = Path.Combine(_dataRoot, "installed-mods.json");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_downloadsRoot);
        Directory.CreateDirectory(_backupsRoot);
        _apiKey = LoadProtectedKey();
        _gameId = LoadCachedGameId();
        InitializeGameSetup();
        RefreshInstalled(false);
        if (IsSteamGameInstall) SynchronizeSteamLaunchOptions();
    }

    private static string ResolveBaseRoot()
    {
        var path = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(path); i++)
        {
            var hasManagerLauncher = File.Exists(Path.Combine(path, "AbioticFactorModManager.exe"));
            var hasPortableMarkers =
                File.Exists(Path.Combine(path, "SteamLaunchWrapper.ps1")) &&
                File.Exists(Path.Combine(path, "Uninstall.ps1"));
            if (hasManagerLauncher && (Directory.Exists(Path.Combine(path, "Data")) || hasPortableMarkers))
                return path;
            path = Directory.GetParent(path)?.FullName ?? "";
        }
        return AppContext.BaseDirectory;
    }

    public void SaveApiKey(string key)
    {
        key = key.Trim();
        if (key.Length < 16) throw new InvalidOperationException("The API key is not correct or valid. Please check it and try again.");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyFile, bytes);
        _apiKey = key;
        _apiKeyValidated = false;
        _gameId = 0;
        ClearBrowsePageCache();
        ApiKeyChanged?.Invoke();
        SetStatus("API key saved.");
    }

    private string LoadProtectedKey()
    {
        try
        {
            if (!File.Exists(_keyFile)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_keyFile), null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    private int LoadCachedGameId()
    {
        try
        {
            var path = Path.Combine(_dataRoot, "game-id.txt");
            return File.Exists(path) && int.TryParse(File.ReadAllText(path), out var id) ? id : 0;
        }
        catch { return 0; }
    }

    public async Task<string> TestApiKeyAsync()
    {
        _apiKeyValidated = false;
        try
        {
            using var doc = await ApiGetAsync("games/" + GameId + "?_minimal=1");
            var id = GetLong(doc.RootElement, "id");
            if (id != GameId) throw new InvalidOperationException("Mod.io did not return the Abiotic Factor game profile.");
            _gameId = GameId;
            _apiKeyValidated = true;
            File.WriteAllText(Path.Combine(_dataRoot, "game-id.txt"), GameId.ToString());
            return GetText(doc.RootElement, "name");
        }
        finally
        {
            ApiKeyChanged?.Invoke();
        }
    }

    private async Task<int> EnsureGameIdAsync()
    {
        if (_gameId == GameId) return _gameId;
        using var doc = await ApiGetAsync("games/" + GameId);
        if (GetLong(doc.RootElement, "id") != GameId)
            throw new InvalidOperationException("The Mod.io API did not return Abiotic Factor.");
        _gameId = GameId;
        File.WriteAllText(Path.Combine(_dataRoot, "game-id.txt"), GameId.ToString());
        return _gameId;
    }
    public async Task<ModSearchPage> SearchModsAsync(string query, string sort, int page, int pageSize = 24)
    {
        var gameId = await EnsureGameIdAsync();
        var offset = Math.Max(0, page - 1) * pageSize;
        var sortValue = sort switch
        {
            "Downloads" => "-downloads_total",
            "Name" => "name",
            _ => "-date_updated"
        };
        var path = $"games/{gameId}/mods?_limit={pageSize}&_offset={offset}&_sort={Uri.EscapeDataString(sortValue)}";
        if (!string.IsNullOrWhiteSpace(query)) path += "&_q=" + Uri.EscapeDataString(query.Trim());
        using var doc = await ApiGetAsync(path);
        var root = doc.RootElement;
        var items = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(ParseMod).Where(x => x.Id > 0).ToList()
            : new List<BrowseMod>();
        var total = (int)GetLong(root, "result_total");
        if (total <= 0 && items.Count > 0) total = offset + items.Count;
        foreach (var mod in items)
        {
            var installed = _installed.FirstOrDefault(x => x.Id == mod.Id.ToString());
            if (installed != null)
                mod.InstalledText = installed.UpdateAvailable
                    ? "Installed " + installed.VersionDisplay + " · update available"
                    : "Installed " + installed.VersionDisplay;
        }
        return new ModSearchPage { Mods = items, Total = total };
    }

    public async Task<BrowseMod> GetModDetailsAsync(long id)
    {
        var gameId = await EnsureGameIdAsync();
        using var doc = await ApiGetAsync($"games/{gameId}/mods/{id}");
        var mod = ParseMod(doc.RootElement);
        if (mod.Id == 0) mod.Id = id;
        return mod;
    }

    public async Task<ModFileRecord> GetLatestWindowsFileAsync(long modId)
    {
        var gameId = await EnsureGameIdAsync();
        using var doc = await ApiGetAsync($"games/{gameId}/mods/{modId}/files?_limit=100&_sort=-date_added");
        if (!doc.RootElement.TryGetProperty("data", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Mod.io did not return release files.");
        var candidates = new List<ModFileRecord>();
        foreach (var element in files.EnumerateArray())
        {
            var platformOk = true;
            if (element.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array)
            {
                platformOk = platforms.EnumerateArray().Any(p =>
                {
                    var platform = GetText(p, "platform").ToUpperInvariant();
                    return (platform == "WINDOWS" || platform == "ALL") && GetLong(p, "status") == 1;
                });
            }
            if (!platformOk) continue;
            var download = element.TryGetProperty("download", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            var url = download.ValueKind == JsonValueKind.Object ? GetText(download, "binary_url") : "";
            var id = GetLong(element, "id");
            if (string.IsNullOrWhiteSpace(url) && id > 0)
                url = $"{ApiBase}games/{gameId}/mods/{modId}/files/{id}/download";
            if (string.IsNullOrWhiteSpace(url)) continue;
            candidates.Add(new ModFileRecord
            {
                Id = id,
                FileName = SafeFileName(GetText(element, "filename"), $"mod_{modId}.zip"),
                Version = GetText(element, "version"),
                BinaryUrl = url,
                DateAdded = GetLong(element, "date_added")
            });
        }
        if (candidates.Count == 0) throw new InvalidOperationException("This mod has no approved Windows release.");
        return candidates.OrderByDescending(x => x.DateAdded).First();
    }

    private async Task<JsonDocument> ApiGetAsync(string path)
    {
        if (!HasApiKey) throw new InvalidOperationException("Add your Mod.io API key in Settings first.");
        var separator = path.Contains('?') ? "&" : "?";
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path + separator + "api_key=" + Uri.EscapeDataString(_apiKey));
        request.Headers.TryAddWithoutValidation("X-Modio-Platform", "windows");
        request.Headers.UserAgent.ParseAdd("AbioticFactorModManager/2.0");
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var normalizedBody = body.ToLowerInvariant();
            var invalidKey = response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                normalizedBody.Contains("invalid api key") ||
                normalizedBody.Contains("invalid_api_key") ||
                normalizedBody.Contains("api key is invalid") ||
                normalizedBody.Contains("api_key_invalid");
            if (invalidKey)
                throw new InvalidOperationException("The API key is not correct or valid. Please check it in Settings and try again.");

            throw new InvalidOperationException("Mod.io could not complete the request. Please check your internet connection and try again.");
        }
        return JsonDocument.Parse(body);
    }

    private async Task<string> ReadTextUrlAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AbioticFactorModManager/2.0");
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static BrowseMod ParseMod(JsonElement element)
    {
        var mod = new BrowseMod
        {
            Id = GetLong(element, "id"),
            Name = FirstNonBlank(GetText(element, "name"), "Untitled mod"),
            Slug = GetText(element, "name_id"),
            Summary = GetText(element, "summary"),
            Description = GetText(element, "description"),
            Url = GetText(element, "profile_url"),
            Updated = GetLong(element, "date_updated")
        };
        if (element.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object)
        {
            mod.ThumbnailUrl = FirstNonBlank(GetText(logo, "thumb_640x360"), GetText(logo, "thumb_320x180"), GetText(logo, "thumb_1280x720"), GetText(logo, "original"));
            mod.ImageUrl = FirstNonBlank(GetText(logo, "original"), GetText(logo, "thumb_1280x720"), mod.ThumbnailUrl);
        }
        if (element.TryGetProperty("submitted_by", out var creator) && creator.ValueKind == JsonValueKind.Object)
            mod.Creator = FirstNonBlank(GetText(creator, "display_name"), GetText(creator, "username"), GetText(creator, "name"));
        if (element.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            mod.Downloads = GetLong(stats, "downloads_total");
        if (string.IsNullOrWhiteSpace(mod.Url)) mod.Url = $"https://mod.io/g/{GameSlug}/m/{mod.Slug}";
        return mod;
    }

    private async Task<string> DownloadToCacheAsync(string url, string fileName)
    {
        const long maxDownloadBytes = 2L * 1024 * 1024 * 1024;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only secure HTTPS download links are accepted.");

        var output = Path.Combine(_downloadsRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" +
            Guid.NewGuid().ToString("N")[..6] + "_" + SafeFileName(fileName, "mod.zip"));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AbioticFactorModManager/2.0");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maxDownloadBytes)
                throw new InvalidOperationException("The mod archive is larger than the 2 GB download limit.");

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                total += read;
                if (total > maxDownloadBytes)
                    throw new InvalidOperationException("The mod archive exceeded the 2 GB download limit.");
                await file.WriteAsync(buffer.AsMemory(0, read));
            }
            await file.FlushAsync();
            return output;
        }
        catch
        {
            TryDeleteFile(output);
            throw;
        }
    }
    public async Task<BrowseMod> ResolveModPageAsync(Uri pageUri)
    {
        var id = await ResolveModIdFromPageAsync(pageUri);
        return await GetModDetailsAsync(id);
    }

    private async Task<long> ResolveModIdFromPageAsync(Uri pageUri)
    {
        if (pageUri.Scheme != Uri.UriSchemeHttps || !IsModIoHost(pageUri.Host))
            throw new InvalidOperationException("For Mod.io pages, use a mod.io URL.");
        var known = ParseModId(pageUri.ToString());
        if (known > 0) return known;
        var html = await ReadTextUrlAsync(pageUri.ToString());
        var image = Regex.Match(html, @"https?://thumb\.modcdn\.io/mods/[^/]+/(?<id>\d+)/", RegexOptions.IgnoreCase);
        if (image.Success && long.TryParse(image.Groups["id"].Value, out var parsed)) return parsed;
        var json = Regex.Match(html, @"""(?:mod_id|modId)""\s*:\s*""?(?<id>\d{4,})", RegexOptions.IgnoreCase);
        if (json.Success && long.TryParse(json.Groups["id"].Value, out parsed)) return parsed;
        throw new InvalidOperationException("Couldn't resolve a Mod.io mod ID from that page. Paste the mod's /m/ page link.");
    }

    public static long ParseModId(string value)
    {
        var match = Regex.Match(value, @"(?:mods?/|modfile[_-])(?<id>\d{4,})", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups["id"].Value, out var id) ? id : 0;
    }

    public static string CleanHtml(string html)
    {
        var text = Regex.Replace(html ?? "", @"<(br|/p|/div|/li|/h[1-6]|/ul|/ol|/pre)\b[^>]*>", Environment.NewLine, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\r?\n", Environment.NewLine);
        text = Regex.Replace(text, @"\r?\n[ \t]+", Environment.NewLine);
        text = Regex.Replace(text, @"\r?\n{3,}", Environment.NewLine + Environment.NewLine);
        return text.Trim();
    }

    private static string ExtractApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return FirstNonBlank(GetText(doc.RootElement, "message"), GetText(doc.RootElement, "error"), body);
        }
        catch { return "Mod.io returned an unreadable error response."; }
    }

    private static string GetText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return long.TryParse(v.ToString(), out var parsed) ? parsed : 0;
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static string SafeFileName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) name = fallback;
        var clean = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return clean.Length > 120 ? clean[..120] : clean;
    }

    private void SetStatus(string value) => StatusChanged?.Invoke(value);
    public void OpenModsFolder() => OpenFolder(ModsRoot);
    public void OpenUe4ssModsFolder() => OpenFolder(Ue4ssModsRoot);
    public void OpenBackupsFolder() => OpenFolder(_backupsRoot);
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }
    private static bool IsModIoHost(string host)
        => string.Equals(host, "mod.io", StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(".mod.io", StringComparison.OrdinalIgnoreCase);

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !IsModIoHost(uri.Host))
            return;

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    public static bool IsGameRunning() => Process.GetProcessesByName("AbioticFactor-Win64-Shipping").Length > 0;
    private static long ToUnix(DateTime value) => value == default ? 0 : new DateTimeOffset(value).ToUnixTimeSeconds();
}