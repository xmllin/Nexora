using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Models;
using Nexora.Services;

namespace Nexora.Services.Downloads
{
    public class GitHubDownloadProvider : IDownloadProvider, IReleaseDownloadProvider
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly Dictionary<string, ReleaseCacheEntry> ReleaseCache = new Dictionary<string, ReleaseCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ReleaseCacheLock = new object();
        private static readonly TimeSpan ReleaseCacheLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PersistentStaleCacheLifetime = TimeSpan.FromDays(7);
        private const int PersistentCacheSchemaVersion = 5;
        private static readonly string PersistentCacheFile = UserDataPath.File("github_releases_cache.json");

        private sealed class ReleaseCacheEntry
        {
            public int SchemaVersion { get; set; }
            public DateTime CreatedAt { get; set; }
            public List<AppRelease> Releases { get; set; }
        }


        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Nexora/1.0 (GitHub release resolver)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public async Task<DownloadInfo> ResolveAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetReleasesAsync(app, token);
            var release = releases.FirstOrDefault(item => item.Download != null);
            if (release?.Download != null) return release.Download;
            throw new InvalidOperationException("Подходящий Windows-файл не найден в последних GitHub Releases.");
        }

        public async Task<IReadOnlyList<AppRelease>> GetReleasesAsync(AppDefinition app, CancellationToken token)
        {
            if (app?.Download == null)
                throw new InvalidOperationException("Не указаны данные загрузки приложения.");

            var owner = !string.IsNullOrWhiteSpace(app.Download.Owner)
                ? app.Download.Owner.Trim()
                : GetOwnerFromRepository(app.Download.Repository);
            var repository = GetRepositoryFromRepository(app.Download.Repository);
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
                throw new InvalidOperationException("Не указан GitHub owner/repository.");

            var cacheKey = BuildCacheKey(owner, repository, app.Download);
            ReleaseCacheEntry staleCache = null;

            lock (ReleaseCacheLock)
            {
                ReleaseCacheEntry cached;
                if (ReleaseCache.TryGetValue(cacheKey, out cached))
                {
                    if (DateTime.UtcNow - cached.CreatedAt < ReleaseCacheLifetime)
                        return cached.Releases;
                    staleCache = cached;
                }
                else
                {
                    cached = LoadPersistentCache(cacheKey);
                    if (cached != null)
                    {
                        ReleaseCache[cacheKey] = cached;
                        staleCache = cached;
                    }
                }
            }

            var api = $"https://api.github.com/repos/{owner}/{repository}/releases?per_page=100";
            try
            {
                await EnsureCanonicalRepositoryAsync(owner, repository, token);
                using (var response = await Client.GetAsync(api, token))
                {
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync();
                    using (var document = JsonDocument.Parse(json))
                    {
                        var result = new List<AppRelease>();
                        foreach (var release in document.RootElement.EnumerateArray())
                        {
                            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                            var prerelease = release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
                            if (prerelease) continue;
                            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

                            var selectedAssets = SelectAssets(app.Download, assets.EnumerateArray().ToList());
                            if (selectedAssets.Count == 0) continue;

                            var tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                            var title = release.TryGetProperty("name", out var titleProp) ? titleProp.GetString() : null;
                            DateTime? published = null;
                            if (release.TryGetProperty("published_at", out var publishedProp) &&
                                DateTime.TryParse(publishedProp.GetString(), out var parsed))
                                published = parsed;

                            foreach (var selected in selectedAssets)
                            {
                                var version = VersionNormalizer.ExtractMostSpecific(tag, title, selected.Name);
                                if (string.IsNullOrWhiteSpace(version))
                                    version = VersionNormalizer.Normalize(tag ?? title ?? selected.Name);

                                result.Add(new AppRelease
                                {
                                    Version = string.IsNullOrWhiteSpace(version) ? "Последняя" : version,
                                    Title = string.IsNullOrWhiteSpace(title) ? tag : title,
                                    PublishedAt = published,
                                    IsPrerelease = prerelease,
                                    Download = new DownloadInfo
                                    {
                                        Url = selected.Url,
                                        FileName = selected.Name,
                                        Source = "GitHub",
                                        Version = string.IsNullOrWhiteSpace(version) ? tag : version,
                                        SizeBytes = selected.Size
                                    }
                                });
                            }
                        }

                        var releases = SortReleases(result);
                        var cacheEntry = new ReleaseCacheEntry { SchemaVersion = PersistentCacheSchemaVersion, CreatedAt = DateTime.UtcNow, Releases = releases };
                        lock (ReleaseCacheLock)
                            ReleaseCache[cacheKey] = cacheEntry;
                        SavePersistentCache(cacheKey, cacheEntry);
                        return releases;
                    }
                }
            }
            catch (Exception apiException) when (!token.IsCancellationRequested)
            {
                try
                {
                    var pageReleases = await GetReleasesFromPageAsync(owner, repository, app.Download, token);
                    if (pageReleases.Count > 0)
                    {
                        var pageEntry = new ReleaseCacheEntry { SchemaVersion = PersistentCacheSchemaVersion, CreatedAt = DateTime.UtcNow, Releases = pageReleases.ToList() };
                        lock (ReleaseCacheLock) ReleaseCache[cacheKey] = pageEntry;
                        SavePersistentCache(cacheKey, pageEntry);
                        return pageReleases;
                    }
                }
                catch
                {
                    // Keep the API exception below when the HTML fallback is unavailable.
                }

                if (staleCache != null && DateTime.UtcNow - staleCache.CreatedAt < PersistentStaleCacheLifetime)
                    return SortReleases(staleCache.Releases);

                throw new InvalidOperationException("GitHub не вернул список релизов через API или страницу репозитория.", apiException);
            }
        }

        private static string BuildCacheKey(string owner, string repository, DownloadDefinition download)
        {
            var assets = download.Assets ?? new Dictionary<string, string>();
            var assetMap = string.Join(";", assets.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Key + "=" + item.Value));
            var platform = PlatformDetectionService.Current;
            return owner + "/" + repository + "|" + platform.Architecture + "|" + platform.ProcessArchitecture + "|" + platform.WindowsBuild + "|" +
                   (download.AssetPattern ?? string.Empty) + "|" + assetMap;
        }

        private static List<AppRelease> SortReleases(IEnumerable<AppRelease> releases)
        {
            return (releases ?? Enumerable.Empty<AppRelease>())
                .Where(item => item != null && item.Download != null)
                .GroupBy(item => VersionNormalizer.Normalize(item.Version ?? string.Empty) + "|" +
                                  (item.Download.Format ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.PublishedAt ?? DateTime.MinValue)
                    .First())
                .OrderByDescending(item => VersionInfo.Parse(item.Version))
                .ThenByDescending(item => item.PublishedAt ?? DateTime.MinValue)
                .ToList();
        }

        private static async Task<IReadOnlyList<AppRelease>> GetReleasesFromPageAsync(
            string owner, string repository, DownloadDefinition download, CancellationToken token)
        {
            var pageUrl = "https://github.com/" + owner + "/" + repository + "/releases";
            using (var response = await Client.GetAsync(pageUrl, token))
            {
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync();
                var pattern = ResolveAssetPattern(download.AssetPattern);
                var releases = new List<AppRelease>();
                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var links = Regex.Matches(html,
                    "href=\\\"(/" + Regex.Escape(owner) + "/" + Regex.Escape(repository) + "/releases/download/([^/]+)/([^\\\"]+))\\\"",
                    RegexOptions.IgnoreCase);

                foreach (Match link in links)
                {
                    var path = link.Groups[1].Value;
                    var tag = WebUtility.HtmlDecode(Uri.UnescapeDataString(link.Groups[2].Value));
                    var name = WebUtility.HtmlDecode(Uri.UnescapeDataString(link.Groups[3].Value));
                    if (!AssetSelector.WildcardMatch(name, pattern) || !tags.Add(tag + "|" + name)) continue;

                    var version = VersionNormalizer.ExtractMostSpecific(tag, name);
                    releases.Add(new AppRelease
                    {
                        Version = string.IsNullOrWhiteSpace(version) ? tag : version,
                        Title = tag,
                        Download = new DownloadInfo
                        {
                            Url = "https://github.com" + path,
                            FileName = name,
                            Source = "GitHub",
                            Version = string.IsNullOrWhiteSpace(version) ? tag : version
                        }
                    });
                }

                if (releases.Count == 0 && string.Equals(owner, "qbittorrent", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(repository, "qBittorrent", StringComparison.OrdinalIgnoreCase))
                    return await GetQbittorrentReleasesAsync(download, token);

                return SortReleases(releases);
            }
        }

        private static async Task<IReadOnlyList<AppRelease>> GetQbittorrentReleasesAsync(DownloadDefinition download, CancellationToken token)
        {
            using (var response = await Client.GetAsync("https://www.qbittorrent.org/download", token))
            {
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync();
                var pattern = ResolveAssetPattern(download.AssetPattern);
                var releases = new List<AppRelease>();
                var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var links = Regex.Matches(html,
                    "https://sourceforge\\.net/projects/qbittorrent/files/qbittorrent-win32/qbittorrent-([^/]+)/([^\\\"' ]+_x64_setup\\.exe)/download",
                    RegexOptions.IgnoreCase);

                foreach (Match link in links)
                {
                    var version = WebUtility.HtmlDecode(Uri.UnescapeDataString(link.Groups[1].Value));
                    var name = WebUtility.HtmlDecode(Uri.UnescapeDataString(link.Groups[2].Value));
                    if (!AssetSelector.WildcardMatch(name, pattern) || !versions.Add(version)) continue;
                    releases.Add(new AppRelease
                    {
                        Version = VersionNormalizer.ExtractMostSpecific(version, name),
                        Title = version,
                        Download = new DownloadInfo
                        {
                            Url = link.Value,
                            FileName = name,
                            Source = "qBittorrent",
                            Version = version
                        }
                    });
                }
                return SortReleases(releases);
            }
        }

        private static async Task EnsureCanonicalRepositoryAsync(string owner, string repository, CancellationToken token)
        {
            var api = "https://api.github.com/repos/" + owner + "/" + repository;
            using (var response = await Client.GetAsync(api, token))
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using (var document = JsonDocument.Parse(json))
                {
                    var fullName = document.RootElement.TryGetProperty("full_name", out var fullNameProperty) ? fullNameProperty.GetString() : null;
                    var archived = document.RootElement.TryGetProperty("archived", out var archivedProperty) && archivedProperty.GetBoolean();
                    var expected = owner + "/" + repository;
                    if (!string.Equals(fullName, expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("GitHub вернул другой репозиторий вместо настроенного официального источника.");
                    if (archived)
                        throw new InvalidOperationException("Официальный GitHub-репозиторий архивирован и не используется для загрузки.");
                }
            }
        }

        private static ReleaseCacheEntry LoadPersistentCache(string cacheKey)
        {
            try
            {
                if (!File.Exists(PersistentCacheFile)) return null;
                var all = JsonSerializer.Deserialize<Dictionary<string, ReleaseCacheEntry>>(File.ReadAllText(PersistentCacheFile));
                ReleaseCacheEntry entry;
                if (all == null || !all.TryGetValue(cacheKey, out entry)) return null;
                return entry != null && entry.SchemaVersion == PersistentCacheSchemaVersion ? entry : null;
            }
            catch { return null; }
        }

        private static void SavePersistentCache(string cacheKey, ReleaseCacheEntry cacheEntry)
        {
            try
            {
                var directory = Path.GetDirectoryName(PersistentCacheFile);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                lock (ReleaseCacheLock)
                {
                    var all = File.Exists(PersistentCacheFile)
                        ? JsonSerializer.Deserialize<Dictionary<string, ReleaseCacheEntry>>(File.ReadAllText(PersistentCacheFile))
                        : null;
                    all = all ?? new Dictionary<string, ReleaseCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    all[cacheKey] = cacheEntry;
                    File.WriteAllText(PersistentCacheFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }

        private static List<AssetCandidate> SelectAssets(DownloadDefinition download, List<JsonElement> assets)
        {
            var mapped = assets.Select(ToAsset)
                .Where(asset => asset != null && !string.IsNullOrWhiteSpace(asset.Url))
                .Where(asset => AssetSelector.IsWindowsAsset(asset.Name + " " + asset.Url))
                .Where(asset => string.IsNullOrWhiteSpace(download.Architecture) ||
                                AssetSelector.IsExplicitArchitectureCompatible(download.Architecture, download.AllowArchitectureFallback))
                .Where(asset => AssetSelector.IsArchitectureCompatible(asset.Name + " " + asset.Url, download.AllowArchitectureFallback))
                .ToList();
            if (mapped.Count == 0) return new List<AssetCandidate>();

            if (download.Assets != null && download.Assets.Count > 0)
            {
                var preferredKey = AssetSelector.SelectPreferredKey(download.Assets, download.AllowArchitectureFallback);
                if (!string.IsNullOrWhiteSpace(preferredKey))
                {
                    string exactName;
                    if (download.Assets.TryGetValue(preferredKey, out exactName))
                    {
                        var exact = mapped.FirstOrDefault(asset => string.Equals(asset.Name, exactName, StringComparison.OrdinalIgnoreCase));
                        return exact == null ? new List<AssetCandidate>() : new List<AssetCandidate> { exact };
                    }
                }
                if (!download.AllowArchitectureFallback)
                    return new List<AssetCandidate>();
            }

            var pattern = AssetSelector.ResolveArchitecturePattern(download.AssetPattern);
            var candidates = string.IsNullOrWhiteSpace(pattern)
                ? mapped
                : mapped.Where(asset => AssetSelector.WildcardMatch(asset.Name, pattern)).ToList();

            if (candidates.Count == 0 && download.AllowArchitectureFallback &&
                string.Equals(PlatformDetectionService.Current.Architecture, "arm64", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(download.AssetPattern) &&
                download.AssetPattern.IndexOf("{arch}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var fallbackPattern = download.AssetPattern.Replace("{arch}", "x64", StringComparison.OrdinalIgnoreCase);
                candidates = mapped.Where(asset => AssetSelector.WildcardMatch(asset.Name, fallbackPattern)).ToList();
            }

            if (candidates.Count == 0) return new List<AssetCandidate>();

            foreach (var candidate in candidates)
                candidate.Score = AssetSelector.Score(candidate.Name, download.PreferNativeArchitecture);

            // One entry per actual format, keeping the best asset for each format.
            return candidates
                .GroupBy(GetAssetFormat, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(asset => asset.Score).ThenBy(asset => asset.Name.Length).First())
                .OrderByDescending(asset => asset.Score)
                .ThenBy(asset => asset.Name.Length)
                .ToList();
        }

        private static AssetCandidate SelectAsset(DownloadDefinition download, List<JsonElement> assets)
        {
            return SelectAssets(download, assets).FirstOrDefault();
        }

        private static string GetAssetFormat(AssetCandidate asset)
        {
            var extension = Path.GetExtension(asset?.Name ?? string.Empty);
            return string.IsNullOrWhiteSpace(extension) ? "" : extension.ToUpperInvariant();
        }

        private static AssetCandidate ToAsset(JsonElement asset)
        {
            return new AssetCandidate
            {
                Name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
                Url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty,
                Size = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : (long?)null
            };
        }

        private static string ResolveAssetPattern(string pattern)
        {
            return AssetSelector.ResolveArchitecturePattern(pattern);
        }

        private static string GetOwnerFromRepository(string repository)
        {
            if (string.IsNullOrWhiteSpace(repository)) return null;
            var parts = repository.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0].Trim() : null;
        }

        private static string GetRepositoryFromRepository(string repository)
        {
            if (string.IsNullOrWhiteSpace(repository)) return null;
            var parts = repository.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[1].Trim() : repository.Trim();
        }

    }
}
