using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AbioticFactorModManager;

public sealed partial class ModManagerService
{
    // Deliberately memory-only: all cached pages disappear when the app exits.
    private const int MaxBrowseCacheSearches = 12;

    private sealed class BrowseSearchCache
    {
        public Dictionary<int, ModSearchPage> Pages { get; } = new();
        public DateTimeOffset LastAccessUtc { get; set; }
    }

    private readonly Dictionary<string, BrowseSearchCache> _browsePageCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ModSearchPage> SearchModsCachedAsync(
        string query,
        string sort,
        int page,
        bool forceRefresh = false,
        int pageSize = 24)
    {
        if (!IsApiKeyValid)
            throw new InvalidOperationException("The API key is not correct or valid. Please check it in Settings and try again.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        query = (query ?? "").Trim();
        sort = string.IsNullOrWhiteSpace(sort) ? "Updated" : sort;

        var cacheKey = sort + "\u001f" + query + "\u001f" + pageSize;
        if (!_browsePageCache.TryGetValue(cacheKey, out var cache))
        {
            cache = new BrowseSearchCache();
            _browsePageCache[cacheKey] = cache;
        }

        cache.LastAccessUtc = DateTimeOffset.UtcNow;

        // Explicit Search/Refresh bypasses the cache and replaces this search's pages.
        if (forceRefresh)
            cache.Pages.Clear();

        if (cache.Pages.TryGetValue(page, out var cachedPage))
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            RefreshCachedInstalledLabels(cachedPage);
            return cachedPage;
        }

        var result = await SearchModsAsync(query, sort, page, pageSize);
        RefreshCachedInstalledLabels(result);
        cache.Pages[page] = result;
        cache.LastAccessUtc = DateTimeOffset.UtcNow;
        TrimBrowseCache(cacheKey);
        return result;
    }

    private void RefreshCachedInstalledLabels(ModSearchPage page)
    {
        foreach (var mod in page.Mods)
        {
            var installed = _installed.FirstOrDefault(item => item.Id == mod.Id.ToString());
            mod.InstalledText = installed == null
                ? ""
                : installed.UpdateAvailable
                    ? "Installed " + installed.VersionDisplay + " - update available"
                    : "Installed " + installed.VersionDisplay;
        }
    }

    private void TrimBrowseCache(string activeKey)
    {
        while (_browsePageCache.Count > MaxBrowseCacheSearches)
        {
            var oldest = _browsePageCache
                .Where(pair => !string.Equals(pair.Key, activeKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Value.LastAccessUtc)
                .FirstOrDefault();

            if (oldest.Key == null) break;
            _browsePageCache.Remove(oldest.Key);
        }
    }

    private void ClearBrowsePageCache() => _browsePageCache.Clear();
}