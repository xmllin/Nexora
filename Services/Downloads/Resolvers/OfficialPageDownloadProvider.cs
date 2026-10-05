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
    /// <summary>
    /// Resolves installers from official vendor pages. Special resolvers are
    /// used where a page hands off to a CDN/redirect service instead of exposing
    /// the installer URL directly in a simple anchor.
    /// </summary>
    public sealed partial class OfficialPageDownloadProvider : IDownloadProvider, IReleaseDownloadProvider
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, ReleaseCacheEntry> Cache = new Dictionary<string, ReleaseCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan MemoryCacheLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PersistentCacheLifetime = TimeSpan.FromDays(7);
        private const int PersistentCacheSchemaVersion = 8;
        private static readonly string PersistentCacheFile = UserDataPath.File("official_releases_cache.json");

        private sealed class ReleaseCacheEntry
        {
            public int SchemaVersion { get; set; }
            public DateTime CreatedAt { get; set; }
            public List<AppRelease> Releases { get; set; }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36 Nexora/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/octet-stream;q=0.9,*/*;q=0.8");
            return client;
        }

        public async Task<DownloadInfo> ResolveAsync(AppDefinition app, CancellationToken cancellationToken)
        {
            ValidateApp(app);
            ValidateWindowsCompatibility(app.Download);

            var cacheKey = BuildCacheKey(app);
            try
            {
                var special = await ResolveSpecialAsync(app, cancellationToken);
                if (special != null)
                    return special;

                if (app.Download != null && !string.IsNullOrWhiteSpace(app.Download.Url) && IsHttpUrl(ResolveTemplate(app.Download.Url)))
                    return await RequireBinaryProbeAsync(CreateInfo(ResolveTemplate(app.Download.Url), app.Download.FileName, "Official"), cancellationToken);

                return await ResolveGenericAsync(app, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var cached = LoadPersistentCache(cacheKey);
                var cachedInfo = cached?.Releases?.FirstOrDefault()?.Download;
                if (cachedInfo != null && DateTime.UtcNow - cached.CreatedAt < PersistentCacheLifetime)
                    return cachedInfo;
                throw new InvalidOperationException("Не удалось определить официальный файл загрузки.", ex);
            }
        }

        public async Task<IReadOnlyList<AppRelease>> GetReleasesAsync(AppDefinition app, CancellationToken cancellationToken)
        {
            ValidateApp(app);
            ValidateWindowsCompatibility(app.Download);

            var cacheKey = BuildCacheKey(app);
            ReleaseCacheEntry stale = null;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var memory))
                {
                    if (DateTime.UtcNow - memory.CreatedAt < MemoryCacheLifetime)
                        return memory.Releases;
                    stale = memory;
                }
                else
                {
                    stale = LoadPersistentCache(cacheKey);
                    if (stale != null) Cache[cacheKey] = stale;
                }
            }

            try
            {
                IReadOnlyList<AppRelease> result;
                var specialReleases = await GetSpecialReleasesAsync(app, cancellationToken);
                result = specialReleases ?? await GetGenericReleasesAsync(app, cancellationToken);
                var normalized = result?.ToList() ?? new List<AppRelease>();
                var entry = new ReleaseCacheEntry { SchemaVersion = PersistentCacheSchemaVersion, CreatedAt = DateTime.UtcNow, Releases = normalized };
                lock (CacheLock) Cache[cacheKey] = entry;
                SavePersistentCache(cacheKey, entry);
                return normalized;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (stale != null && DateTime.UtcNow - stale.CreatedAt < PersistentCacheLifetime)
                    return SortReleases(stale.Releases);
                throw new InvalidOperationException("Не удалось получить список версий с официального источника.", ex);
            }
        }

        private static void ValidateApp(AppDefinition app)
        {
            if (app == null)
                throw new InvalidOperationException("Приложение не задано.");
            if (string.IsNullOrWhiteSpace(app.Website) || !IsHttpUrl(app.Website))
                throw new InvalidOperationException("Для приложения не задан официальный источник загрузки.");
        }

        private static void ValidateWindowsCompatibility(DownloadDefinition definition)
        {
            if (definition == null) return;
            var platform = PlatformDetectionService.Current;
            if (!platform.IsWindows) throw new InvalidOperationException("Эта загрузка предназначена для Windows.");
            if (definition.MinimumWindowsBuild.HasValue && platform.WindowsBuild > 0 && platform.WindowsBuild < definition.MinimumWindowsBuild.Value)
                throw new InvalidOperationException("Эта версия программы требует более новую версию Windows.");
            if (definition.MaximumWindowsBuild.HasValue && platform.WindowsBuild > 0 && platform.WindowsBuild > definition.MaximumWindowsBuild.Value)
                throw new InvalidOperationException("Эта версия программы не поддерживает текущую сборку Windows.");
        }

        private static string BuildCacheKey(AppDefinition app)
        {
            var platform = PlatformDetectionService.Current;
            return (app.Id ?? app.Name ?? string.Empty) + "|" + platform.Architecture + "|" + platform.ProcessArchitecture +
                   "|" + platform.WindowsBuild + "|" + (app.Website ?? string.Empty);
        }

        private static ReleaseCacheEntry LoadPersistentCache(string cacheKey)
        {
            try
            {
                if (!File.Exists(PersistentCacheFile)) return null;
                var all = JsonSerializer.Deserialize<Dictionary<string, ReleaseCacheEntry>>(File.ReadAllText(PersistentCacheFile));
                if (all == null || !all.TryGetValue(cacheKey, out var entry)) return null;
                return entry != null && entry.SchemaVersion == PersistentCacheSchemaVersion ? entry : null;
            }
            catch { return null; }
        }

        private static void SavePersistentCache(string cacheKey, ReleaseCacheEntry entry)
        {
            try
            {
                var directory = Path.GetDirectoryName(PersistentCacheFile);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                lock (CacheLock)
                {
                    var all = File.Exists(PersistentCacheFile)
                        ? JsonSerializer.Deserialize<Dictionary<string, ReleaseCacheEntry>>(File.ReadAllText(PersistentCacheFile))
                        : null;
                    all = all ?? new Dictionary<string, ReleaseCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    all[cacheKey] = entry;
                    File.WriteAllText(PersistentCacheFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }

        private sealed class Candidate
        {
            public string Url { get; set; }
            public string FileName { get; set; }
            public string Text { get; set; }
            public bool IsBinaryResponse { get; set; }
        }
    }
}
