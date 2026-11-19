using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    // Session-only thumbnail metadata used by the optional Installed Mods waffle view.
    private readonly Dictionary<string, string> _installedThumbnailUrls = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, string>> GetInstalledModThumbnailUrlsAsync(IEnumerable<InstalledMod> installed)
    {
        var ids = installed
            .Where(mod => string.Equals(mod.SourceType, "Mod.io", StringComparison.OrdinalIgnoreCase))
            .Select(mod => long.TryParse(mod.Id, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var missing = ids.Where(id => !_installedThumbnailUrls.ContainsKey(id.ToString())).ToList();
        if (missing.Count > 0)
        {
            var gameId = await EnsureGameIdAsync();

            // Mod.io supports the id-in filter, allowing all currently installed
            // Mod.io thumbnails to be resolved with one request per 100 IDs.
            foreach (var batch in missing.Chunk(100))
            {
                var idList = string.Join(",", batch);
                var path = $"games/{gameId}/mods?id-in={Uri.EscapeDataString(idList)}&_limit={batch.Length}";
                using var doc = await ApiGetAsync(path);

                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var mod = ParseMod(item);
                        if (mod.Id > 0)
                            _installedThumbnailUrls[mod.Id.ToString()] = mod.ThumbnailUrl ?? "";
                    }
                }

                foreach (var id in batch)
                    _installedThumbnailUrls.TryAdd(id.ToString(), "");
            }
        }

        return ids
            .Where(id => _installedThumbnailUrls.ContainsKey(id.ToString()))
            .ToDictionary(id => id.ToString(), id => _installedThumbnailUrls[id.ToString()], StringComparer.OrdinalIgnoreCase);
    }
}