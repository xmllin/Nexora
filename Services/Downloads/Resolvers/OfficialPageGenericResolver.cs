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
        private async Task<DownloadInfo> ResolveGenericAsync(AppDefinition app, CancellationToken token)
        {
            var html = (await GetHtmlAsync(app.Website, token)).Replace("\\/", "/").Replace("\\u0026", "&");
            var selected = SelectBestCandidate(app, ExtractCandidates(app.Website, html));
            if (selected == null)
                throw new InvalidOperationException("Не удалось найти прямой файл загрузки на официальной странице.");
            var nested = await ResolveNestedDownloadAsync(app, selected, token, 0);
            if (nested == null)
                throw new InvalidOperationException("Не удалось разрешить ссылку на установщик.");
            return CreateInfo(nested.Url, nested.FileName, "Official");
        }

        private async Task<IReadOnlyList<AppRelease>> GetGenericReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var html = (await GetHtmlAsync(app.Website, token)).Replace("\\/", "/").Replace("\\u0026", "&");
            var candidates = ExtractCandidates(app.Website, html)
                .Where(c => !IsMobilePackage(c.Url) && !IsMobilePackage(c.FileName))
                .Where(c => HasInstallerExtension(c.Url) || HasInstallerExtension(c.FileName))
                .Where(c => AssetSelector.IsWindowsAsset(c.Url + " " + c.FileName))
                .Where(c => AssetSelector.IsArchitectureCompatible(c.Url + " " + c.FileName, app.Download?.AllowArchitectureFallback ?? false))
                .ToList();

            var releases = new List<AppRelease>();
            foreach (var candidate in candidates)
            {
                var version = VersionNormalizer.ExtractMostSpecific(candidate.FileName, candidate.Url);
                if (string.IsNullOrWhiteSpace(version))
                    version = VersionNormalizer.ExtractMostSpecific(html);
                var nested = await ResolveNestedDownloadAsync(app, candidate, token, 0);
                if (nested == null) continue;
                var info = CreateInfo(nested.Url, nested.FileName, "Official");
                if (string.Equals(app.Id, "hwmonitor", StringComparison.OrdinalIgnoreCase))
                {
                    var probe = await new DownloadMetadataService().ProbeAsync(info.Url, token);
                    if (probe.Success)
                    {
                        if (!string.IsNullOrWhiteSpace(probe.FinalUrl)) info.Url = probe.FinalUrl;
                        if (!string.IsNullOrWhiteSpace(probe.FileName) && HasInstallerExtension(probe.FileName)) info.FileName = probe.FileName;
                        if (probe.SizeBytes.HasValue) info.SizeBytes = probe.SizeBytes;
                    }
                }
                info.Version = VersionNormalizer.ExtractMostSpecific(info.FileName, info.Url, version);
                if (releases.Any(item => string.Equals(item.Download.Url, info.Url, StringComparison.OrdinalIgnoreCase))) continue;
                releases.Add(new AppRelease
                {
                    Version = info.Version,
                    Title = info.FileName ?? info.Version,
                    Download = info
                });
            }

            return SortReleases(releases);
        }

        private static List<AppRelease> SortReleases(IEnumerable<AppRelease> releases)
        {
            return (releases ?? Enumerable.Empty<AppRelease>())
                .Where(item => item != null && item.Download != null)
                .GroupBy(GetReleaseIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.PublishedAt ?? DateTime.MinValue).First())
                .OrderByDescending(item => VersionInfo.Parse(item.Version))
                .ThenByDescending(item => item.PublishedAt ?? DateTime.MinValue)
                .ToList();
        }

        private static string GetReleaseIdentity(AppRelease release)
        {
            // A release/version with the same format should appear only once;
            // mirrors and alternate URLs must not create duplicate menu items.
            return VersionNormalizer.Normalize(release?.Version ?? string.Empty) + "|" +
                   (release?.Download?.Format ?? string.Empty);
        }

        private static string FindFirstVersion(string html, string pattern)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? VersionNormalizer.Normalize(match.Groups["version"].Value) : string.Empty;
        }

        private static async Task<string> GetHtmlAsync(string url, CancellationToken token)
        {
            using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, token))
            {
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(mediaType) && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Официальный источник вернул не HTML-страницу.");
                return await response.Content.ReadAsStringAsync();
            }
        }

        private static async Task<DownloadInfo> RequireBinaryProbeAsync(DownloadInfo info, CancellationToken token)
        {
            if (info == null || !IsHttpUrl(info.Url))
                throw new InvalidOperationException("Некорректный URL установщика.");

            var probe = await new DownloadMetadataService().ProbeAsync(info.Url, token);
            if (!probe.Success)
                throw new InvalidOperationException(probe.Error ?? "Источник не вернул файл установщика.");

            if (!string.IsNullOrWhiteSpace(probe.FinalUrl)) info.Url = probe.FinalUrl;
            if (!string.IsNullOrWhiteSpace(probe.FileName) && HasInstallerExtension(probe.FileName)) info.FileName = probe.FileName;
            if (probe.SizeBytes.HasValue) info.SizeBytes = probe.SizeBytes;
            return info;
        }

        private static async Task<DownloadInfo> ProbeBinaryCandidateAsync(Candidate candidate, CancellationToken token)
        {
            if (candidate == null || !IsHttpUrl(candidate.Url)) return null;

            var metadata = new DownloadMetadataService();
            var probe = await metadata.ProbeAsync(candidate.Url, token);
            if (!probe.Success) return null;

            var finalUrl = string.IsNullOrWhiteSpace(probe.FinalUrl) ? candidate.Url : probe.FinalUrl;
            var fileName = !string.IsNullOrWhiteSpace(probe.FileName) ? probe.FileName : candidate.FileName;
            if (!HasInstallerExtension(fileName) && !HasInstallerExtension(finalUrl)) return null;

            var info = CreateInfo(finalUrl, fileName, "MakuTweaker");
            info.SizeBytes = probe.SizeBytes;
            return info;
        }

        private static async Task<Candidate> ResolveNestedDownloadAsync(AppDefinition app, Candidate candidate, CancellationToken token, int depth)
        {
            if (candidate == null || depth >= 4) return candidate;
            if (!IsHttpUrl(candidate.Url)) return candidate;
            if (!IsLandingPage(candidate.Url) && HasInstallerExtension(candidate.Url)) return candidate;

            using (var response = await Client.GetAsync(candidate.Url, HttpCompletionOption.ResponseContentRead, token))
            {
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                {
                    var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? candidate.Url;
                    var responseFileName = GetResponseFileName(response) ?? candidate.FileName;
                    if (string.IsNullOrWhiteSpace(responseFileName) && Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri))
                        responseFileName = Path.GetFileName(finalUri.AbsolutePath);
                    return new Candidate
                    {
                        Url = finalUrl,
                        FileName = string.IsNullOrWhiteSpace(responseFileName) ? candidate.FileName : WebUtility.UrlDecode(responseFileName),
                        Text = candidate.Text,
                        IsBinaryResponse = true
                    };
                }

                var html = (await response.Content.ReadAsStringAsync())
                    .Replace("\\/", "/")
                    .Replace("\\u0026", "&")
                    .Replace("&amp;", "&");
                var nestedCandidates = ExtractCandidates(candidate.Url, html)
                    .Where(c => !string.Equals(c.Url, candidate.Url, StringComparison.OrdinalIgnoreCase))
                    .Where(c => !IsMobilePackage(c.Url) && !IsMobilePackage(c.FileName))
                    .ToList();
                var nested = SelectBestCandidate(app, nestedCandidates);
                if (nested == null) return candidate;
                if (!HasInstallerExtension(nested.Url) && !HasInstallerExtension(nested.FileName))
                    return await ResolveNestedDownloadAsync(app, nested, token, depth + 1);
                return nested;
            }
        }

        private static bool IsLandingPage(string url)
        {
            return ContainsToken(url, "mediafire.com/file/") || ContainsToken(url, "pixeldrain.com/u/") ||
                   ContainsToken(url, "pixeldrain.com/l/") || ContainsToken(url, "github.com/") ||
                   ContainsToken(url, "get.videolan.org/vlc/") ||
                   ContainsToken(url, "/download") && !HasInstallerExtension(url);
        }

        private static int ScoreMakuCandidate(Candidate candidate)
        {
            var url = (candidate?.Url ?? string.Empty).ToLowerInvariant();
            var file = (candidate?.FileName ?? string.Empty).ToLowerInvariant();
            var score = 0;
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) score += 100;
            if (url.Contains("mediafire.com")) score += 40;
            if (url.Contains("pixeldrain.com")) score += 30;
            if (file.Contains("makutweaker")) score += 50;
            if (file.Contains("portable")) score -= 80;
            if (file.Contains("win7") || file.Contains("windows7") || file.Contains("8.1")) score -= 120;
            return score;
        }

        private static string GetResponseFileName(HttpResponseMessage response)
        {
            try
            {
                var disposition = response?.Content?.Headers?.ContentDisposition;
                if (disposition != null)
                {
                    if (!string.IsNullOrWhiteSpace(disposition.FileNameStar))
                        return disposition.FileNameStar.Trim('"');
                    if (!string.IsNullOrWhiteSpace(disposition.FileName))
                        return disposition.FileName.Trim('"');
                }
            }
            catch { }
            return null;
        }

        private static bool HasInstallerExtension(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var clean = value.Split(new[] { '?', '#' }, 2)[0];
            var extension = Path.GetExtension(clean);
            if (string.IsNullOrWhiteSpace(extension)) return false;
            switch (extension.ToLowerInvariant())
            {
                case ".exe":
                case ".msi":
                case ".zip":
                case ".7z":
                case ".rar":
                case ".msix":
                case ".appx":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsMobilePackage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var lower = value.ToLowerInvariant();
            return lower.EndsWith(".click", StringComparison.OrdinalIgnoreCase) || lower.Contains("/mobile/") ||
                   lower.Contains("android") || lower.Contains("ios") || lower.Contains("macos") || lower.Contains("linux");
        }

        private static bool IsHttpUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string ResolveTemplate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var architecture = PlatformDetectionService.Current.Architecture;
            var discordArchitecture = architecture == "arm64" ? "arm64" : architecture == "x86" ? "x86" : "x64";
            var vlcArchitecture = architecture == "arm64" ? "winarm64" : architecture == "x86" ? "win32" : "win64";
            return value
                .Replace("{arch}", architecture, StringComparison.OrdinalIgnoreCase)
                .Replace("{osArch}", architecture, StringComparison.OrdinalIgnoreCase)
                .Replace("{discordArch}", discordArchitecture, StringComparison.OrdinalIgnoreCase)
                .Replace("{vlcArch}", vlcArchitecture, StringComparison.OrdinalIgnoreCase);
        }

        private static DownloadInfo CreateInfo(string url, string configuredName, string source)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Источник загрузки не задан.");
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Источник загрузки должен использовать HTTP или HTTPS.");

            var fileName = configuredName;
            if (string.IsNullOrWhiteSpace(fileName)) fileName = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName == "/") fileName = "installer.download";
            return new DownloadInfo
            {
                Url = uri.AbsoluteUri,
                FileName = WebUtility.UrlDecode(fileName),
                Source = source,
                Version = VersionNormalizer.ExtractMostSpecific(fileName, uri.AbsoluteUri)
            };
        }

        private static List<Candidate> ExtractCandidates(string pageUrl, string html)
        {
            var result = new List<Candidate>();
            if (string.IsNullOrWhiteSpace(html)) return result;
            var patterns = new[]
            {
                "(?:href|data-href|data-url|data-download-url)\\s*=\\s*[\\\"'](?<url>[^\\\"']+)[\\\"']",
                "(?:downloadUrl|download_url|fileUrl|file_url|url)\\s*[:=]\\s*[\\\"'](?<url>https?://[^\\\"']+)[\\\"']",
                "(?:window\\.location|location\\.href|window\\.open)\\s*[(=]\\s*[\\\"'](?<url>https?://[^\\\"']+)[\\\"']"
            };

            foreach (var pattern in patterns)
            {
                foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
                    AddCandidate(result, pageUrl, match.Groups["url"].Value, match.Value);
            }

            foreach (Match match in Regex.Matches(html, "https?://[^\\s\"'<>]+", RegexOptions.IgnoreCase))
                AddCandidate(result, pageUrl, match.Value, match.Value);

            foreach (Match match in Regex.Matches(html, "(?<![\"'])/(?:download|files?)/[^\\s\"'<>]+", RegexOptions.IgnoreCase))
                AddCandidate(result, pageUrl, match.Value, match.Value);

            return result;
        }

        private static void AddCandidate(List<Candidate> result, string pageUrl, string rawUrl, string text)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return;
            rawUrl = WebUtility.HtmlDecode(rawUrl.Trim())
                .Replace("\\/", "/")
                .Replace("\\u0026", "&")
                .Replace("&amp;", "&")
                .Trim('\\', '"', '\'');
            if (rawUrl.StartsWith("#", StringComparison.Ordinal) || rawUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return;

            Uri absolute;
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out absolute))
            {
                if (!Uri.TryCreate(new Uri(pageUrl), rawUrl, out absolute)) return;
            }
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) return;

            var url = absolute.AbsoluteUri;
            if (!LooksLikeDownload(url)) return;
            if (result.Any(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase))) return;
            var fileName = Path.GetFileName(absolute.AbsolutePath);
            result.Add(new Candidate
            {
                Url = url,
                FileName = string.IsNullOrWhiteSpace(fileName) ? null : WebUtility.UrlDecode(fileName),
                Text = text
            });
        }

        private static bool LooksLikeDownload(string url)
        {
            var lower = (url ?? string.Empty).ToLowerInvariant();
            return lower.Contains(".exe") || lower.Contains(".msi") || lower.Contains(".zip") || lower.Contains(".7z") || lower.Contains(".rar") ||
                   lower.Contains("/download") || lower.Contains("download=") || lower.Contains("download?") ||
                   lower.Contains("pixeldrain.com/u/") || lower.Contains("pixeldrain.com/api/file/") || lower.Contains("mediafire.com/file/") ||
                   lower.Contains("data-cdn.mbamupdates.com/") || lower.Contains("download.torproject.org/") ||
                   lower.Contains("dist.torproject.org/") || Regex.IsMatch(lower, @"https?://download\d+\.mediafire\.com/");
        }

        private static Candidate SelectBestCandidate(AppDefinition app, IEnumerable<Candidate> candidates)
        {
            var list = (candidates ?? Enumerable.Empty<Candidate>())
                .Where(c => !IsMobilePackage(c.Url) && !IsMobilePackage(c.FileName))
                .Where(c => AssetSelector.IsArchitectureCompatible(c.Url + " " + c.FileName, app?.Download?.AllowArchitectureFallback ?? false))
                .ToList();
            var installers = list.Where(c => HasInstallerExtension(c.Url) || HasInstallerExtension(c.FileName)).ToList();
            if (installers.Count > 0) list = installers;
            if (list.Count == 0) return null;

            var tokens = NormalizeTokens((app?.Name ?? string.Empty) + " " + (app?.Id ?? string.Empty));
            var domain = Uri.TryCreate(app?.Website, UriKind.Absolute, out var site) ? site.Host.ToLowerInvariant() : string.Empty;
            return list.Select(c => new { Candidate = c, Score = Score(c, tokens, domain) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Candidate.Url.Length)
                .Select(x => x.Candidate)
                .FirstOrDefault();
        }

        private static int Score(Candidate candidate, HashSet<string> tokens, string pageDomain)
        {
            var url = (candidate?.Url ?? string.Empty).ToLowerInvariant();
            var file = (candidate?.FileName ?? string.Empty).ToLowerInvariant();
            var text = (candidate?.Text ?? string.Empty).ToLowerInvariant();
            var score = 0;
            if (file.EndsWith(".exe") || url.Contains(".exe")) score += 100;
            else if (file.EndsWith(".msi") || url.Contains(".msi")) score += 95;
            else if (file.EndsWith(".zip") || url.Contains(".zip")) score += 70;
            else if (file.EndsWith(".7z") || url.Contains(".7z")) score += 65;
            else if (file.EndsWith(".rar") || url.Contains(".rar")) score += 60;
            else if (url.Contains("/download")) score += 30;
            if (url.Contains("windows") || file.Contains("windows")) score += 25;
            if (url.Contains("arm64") || file.Contains("arm64")) score += PlatformDetectionService.Current.Architecture == "arm64" ? 30 : -30;
            if (url.Contains("x64") || file.Contains("x64") || url.Contains("amd64") || file.Contains("amd64")) score += PlatformDetectionService.Current.Architecture == "x64" ? 25 : -25;
            if (url.Contains("x86") || file.Contains("x86") || url.Contains("win32") || file.Contains("win32")) score += PlatformDetectionService.Current.Architecture == "x86" ? 25 : -25;
            foreach (var token in tokens)
            {
                if (token.Length < 3) continue;
                if (url.Contains(token)) score += 10;
                if (file.Contains(token)) score += 15;
                if (text.Contains(token)) score += 5;
            }
            if (!string.IsNullOrWhiteSpace(pageDomain) && url.Contains(pageDomain)) score += 8;
            if (url.Contains("release-notes") || url.Contains("readme") || url.Contains("help") || url.Contains("faq")) score -= 50;
            return score;
        }

        private static HashSet<string> NormalizeTokens(string text)
        {
            var normalized = new string((text ?? string.Empty).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
            return new HashSet<string>(normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool ContainsToken(string value, string token)
        {
            return !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(token) && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }
}
