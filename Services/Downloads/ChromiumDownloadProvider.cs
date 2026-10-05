using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Models;

namespace Nexora.Services.Downloads
{
    /// <summary>
    /// Chromium continuous snapshot provider. LAST_CHANGE is the authoritative
    /// current revision; the storage API is used only to populate the version menu.
    /// </summary>
    public sealed class ChromiumDownloadProvider : IReleaseDownloadProvider, IDownloadProvider
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly TimeSpan CurrentRevisionCacheLifetime = TimeSpan.FromHours(12);
        private static readonly string CurrentRevisionCacheDirectory = UserDataPath.Subfolder("Cache", "Chromium");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Nexora/1.0 (Chromium snapshot resolver)");
            return client;
        }

        public async Task<DownloadInfo> ResolveAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetReleasesAsync(app, token);
            var first = releases.FirstOrDefault(r => r?.Download != null);
            if (first?.Download == null)
                throw new InvalidOperationException("Не удалось определить текущую сборку Chromium.");
            return first.Download;
        }

        public async Task<IReadOnlyList<AppRelease>> GetReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var platform = PlatformDetectionService.Current;
            if (!platform.IsWindows)
                throw new InvalidOperationException("Chromium provider предназначен для Windows.");

            var storagePlatform = platform.Architecture == "arm64" ? "Win_Arm64" : "Win_x64";
            if (platform.Architecture == "x86")
                storagePlatform = "Win";

            var currentRevision = await GetCurrentRevisionAsync(storagePlatform, token);
            if (!long.TryParse(currentRevision, out var currentNumber))
                throw new InvalidOperationException("Не удалось определить текущую сборку Chromium: LAST_CHANGE имеет неверный формат.");

            var revisions = await GetRevisionListAsync(storagePlatform, currentNumber, token);
            // Always include LAST_CHANGE itself. The storage listing can be
            // paged/truncated, so the current snapshot must never disappear
            // from the version selector just because the listing returned older entries.
            if (!revisions.Any(x => x.Number == currentNumber))
                revisions.Add(new SnapshotRevision { Value = currentRevision, Number = currentNumber });

            return revisions
                .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(x => x.Number)
                .Take(30)
                .Select(x => CreateRelease(x.Value, storagePlatform))
                .ToList();
        }

        private static async Task<string> GetCurrentRevisionAsync(string storagePlatform, CancellationToken token)
        {
            var urls = new[]
            {
                "https://commondatastorage.googleapis.com/chromium-browser-snapshots/" + storagePlatform + "/LAST_CHANGE",
                "https://storage.googleapis.com/chromium-browser-snapshots/" + storagePlatform + "/LAST_CHANGE",
                "https://www.googleapis.com/download/storage/v1/b/chromium-browser-snapshots/o/" + Uri.EscapeDataString(storagePlatform + "/LAST_CHANGE") + "?alt=media"
            };

            foreach (var url in urls)
            {
                try
                {
                    using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, token))
                    {
                        if (!response.IsSuccessStatusCode) continue;
                        var value = (await response.Content.ReadAsStringAsync()).Trim();
                        long number;
                        if (long.TryParse(value, out number) && number > 0)
                        {
                            SaveCurrentRevision(storagePlatform, value);
                            return value;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            // LAST_CHANGE can be temporarily unavailable even while the snapshot
            // bucket itself remains accessible. In that case find the newest
            // numeric revision directly from the bucket and cache it for offline use.
            try
            {
                var latest = await FindLatestRevisionInBucketAsync(storagePlatform, token);
                if (!string.IsNullOrWhiteSpace(latest))
                {
                    SaveCurrentRevision(storagePlatform, latest);
                    return latest;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            var cached = LoadCurrentRevision(storagePlatform);
            if (!string.IsNullOrWhiteSpace(cached))
                return cached;

            throw new InvalidOperationException("Не удалось определить текущую сборку Chromium. Сервер не вернул LAST_CHANGE и список снимков недоступен.");
        }

        private static async Task<string> FindLatestRevisionInBucketAsync(string storagePlatform, CancellationToken token)
        {
            var api = "https://www.googleapis.com/storage/v1/b/chromium-browser-snapshots/o?prefix=" +
                      Uri.EscapeDataString(storagePlatform + "/") + "&delimiter=/&maxResults=1000";
            using (var response = await Client.GetAsync(api, HttpCompletionOption.ResponseContentRead, token))
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using (var document = JsonDocument.Parse(json))
                {
                    long max = 0;
                    if (document.RootElement.TryGetProperty("prefixes", out var prefixes) && prefixes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var prefix in prefixes.EnumerateArray())
                        {
                            long number;
                            if (long.TryParse(ExtractRevision(prefix.GetString()), out number) && number > max)
                                max = number;
                        }
                    }
                    return max > 0 ? max.ToString() : null;
                }
            }
        }

        private static string CurrentRevisionCacheFile(string storagePlatform)
        {
            return Path.Combine(CurrentRevisionCacheDirectory, storagePlatform + ".txt");
        }

        private static void SaveCurrentRevision(string storagePlatform, string revision)
        {
            try
            {
                Directory.CreateDirectory(CurrentRevisionCacheDirectory);
                File.WriteAllText(CurrentRevisionCacheFile(storagePlatform), revision + "\n" + DateTime.UtcNow.Ticks);
            }
            catch { }
        }

        private static string LoadCurrentRevision(string storagePlatform)
        {
            try
            {
                var file = CurrentRevisionCacheFile(storagePlatform);
                if (!File.Exists(file)) return null;
                var lines = File.ReadAllLines(file);
                if (lines.Length == 0) return null;
                long revision;
                if (!long.TryParse(lines[0].Trim(), out revision) || revision <= 0) return null;
                long ticks;
                if (lines.Length > 1 && long.TryParse(lines[1].Trim(), out ticks))
                {
                    var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                    if (age > CurrentRevisionCacheLifetime) return null;
                }
                return revision.ToString();
            }
            catch { return null; }
        }

        private static async Task<List<SnapshotRevision>> GetRevisionListAsync(string storagePlatform, long currentNumber, CancellationToken token)
        {
            var result = new List<SnapshotRevision>();
            var api = "https://www.googleapis.com/storage/v1/b/chromium-browser-snapshots/o?prefix=" + Uri.EscapeDataString(storagePlatform + "/") + "&delimiter=/&maxResults=1000";
            try
            {
                using (var response = await Client.GetAsync(api, HttpCompletionOption.ResponseContentRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync();
                    using (var document = JsonDocument.Parse(json))
                    {
                        if (document.RootElement.TryGetProperty("prefixes", out var prefixes) && prefixes.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var prefix in prefixes.EnumerateArray())
                            {
                                var revision = ExtractRevision(prefix.GetString());
                                if (long.TryParse(revision, out var number) && number <= currentNumber)
                                    result.Add(new SnapshotRevision { Value = revision, Number = number });
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // The current revision is still enough to offer a valid latest download.
            }
            return result;
        }

        private static AppRelease CreateRelease(string revision, string storagePlatform)
        {
            var archive = "chrome-win.zip";
            var suffix = storagePlatform.IndexOf("Arm64", StringComparison.OrdinalIgnoreCase) >= 0 ? "arm64" : storagePlatform == "Win_x64" ? "x64" : "x86";
            var url = "https://commondatastorage.googleapis.com/chromium-browser-snapshots/" + storagePlatform + "/" + revision + "/" + archive;
            var fileName = "chromium-win-" + suffix + "-" + revision + ".zip";
            return new AppRelease
            {
                // Chromium snapshot revision is a build identifier, not a semantic
                // product version. Keep it numeric for sorting and expose it with
                // an explicit "Revision" label so it cannot be mistaken for 1.2.3.
                Version = revision,
                DisplayVersion = "Revision " + revision,
                Title = "Chromium snapshot " + revision,
                Download = new DownloadInfo
                {
                    Url = url,
                    FileName = fileName,
                    Source = "Chromium",
                    Version = revision
                }
            };
        }

        private static string ExtractRevision(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var parts = value.TrimEnd('/').Split('/');
            return parts.Length == 0 ? null : parts[parts.Length - 1];
        }

        private sealed class SnapshotRevision
        {
            public string Value { get; set; }
            public long Number { get; set; }
        }
    }
}
