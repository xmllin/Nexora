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
        private async Task<DownloadInfo> ResolveSpecialAsync(AppDefinition app, CancellationToken token)
        {
            switch ((app.Id ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "vlc": return await ResolveVlcAsync(app, token);
                case "aida64": return await ResolveAida64Async(app, token);
                case "makutweaker": return await ResolveMakuTweakerAsync(app, token);
                case "malwarebytes": return await ResolveMalwarebytesAsync(app, token);
                case "winrar": return await ResolveWinRarAsync(app, token);
                case "nvidia-app": return await ResolveNvidiaAppAsync(app, token);
                case "tor-browser": return await ResolveTorBrowserAsync(app, token);
                case "firefox": return await ResolveFirefoxAsync(app, token);
                case "vscode": return await ResolveVSCodeAsync(app, token);
                case "visualstudio": return await ResolveVisualStudioAsync(app, token);
                case "glaryutilities5": return await ResolveGlaryUtilitiesAsync(app, token);
                case "hwmonitor": return await ResolveHwMonitorAsync(app, token);
                case "opera": return await ResolveOperaAsync(app, token, false);
                case "opera-gx": return await ResolveOperaAsync(app, token, true);
                default: return null;
            }
        }

        private async Task<IReadOnlyList<AppRelease>> GetSpecialReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var id = (app.Id ?? string.Empty).Trim().ToLowerInvariant();
            if (id == "makutweaker")
                return await GetMakuTweakerReleasesAsync(app, token);
            if (id == "opera" || id == "opera-gx")
                return await GetOperaReleasesAsync(app, token, id == "opera-gx");
            if (id == "firefox")
                return await GetFirefoxReleasesAsync(app, token);
            if (id == "tor-browser")
                return await GetTorBrowserReleasesAsync(app, token);
            if (id == "winrar")
                return await GetWinRarReleasesAsync(app, token);
            if (id == "vlc")
                return await GetVlcReleasesAsync(app, token);
            if (id == "aida64")
                return await GetAida64ReleasesAsync(app, token);
            if (id == "vscode")
                return await GetVSCodeReleasesAsync(app, token);
            if (id != "malwarebytes" && id != "nvidia-app" && id != "visualstudio" &&
                id != "glaryutilities5" && id != "hwmonitor" && id != "everything")
                return null;

            if (id == "everything")
                return await GetEverythingReleasesAsync(app, token);

            var info = await ResolveSpecialAsync(app, token);
            if (info == null) return new List<AppRelease>();
            var version = VersionNormalizer.ExtractMostSpecific(info.Version, info.FileName, info.Url);
            return new[]
            {
                new AppRelease
                {
                    Version = version,
                    Title = app.Name + " " + version,
                    Download = info
                }
            };
        }


private async Task<IReadOnlyList<AppRelease>> GetMakuTweakerReleasesAsync(AppDefinition app, CancellationToken token)
{
    var releases = new List<AppRelease>();
    var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var mainHtml = (await GetHtmlAsync(app.Website, token)).Replace("\\/", "/").Replace("\\u0026", "&");
    seenPages.Add(app.Website);

    var mainInfo = await ResolveMakuFromHtmlAsync(app, app.Website, mainHtml, token);
    if (mainInfo != null)
    {
        mainInfo.Source = "MakuTweaker";
        releases.Add(new AppRelease { Version = VersionNormalizer.ExtractMostSpecific(mainInfo.FileName, mainInfo.Url, mainHtml), Title = app.Name, Download = mainInfo });
    }

    var pageLinks = ExtractAnchorLinks(app.Website, mainHtml)
        .Select(x => new { Url = x.Url, Text = x.Text, Version = VersionNormalizer.ExtractMostSpecific(x.Text) })
        .Where(x => !string.IsNullOrWhiteSpace(x.Version) && x.Version != "0")
        .Where(x => ContainsToken(x.Url, "adderly.top") || ContainsToken(x.Url, "makutweaker"))
        .OrderByDescending(x => VersionInfo.Parse(x.Version))
        .Take(20)
        .ToList();

    foreach (var page in pageLinks)
    {
        if (!seenPages.Add(page.Url)) continue;
        try
        {
            var html = (await GetHtmlAsync(page.Url, token)).Replace("\\/", "/").Replace("\\u0026", "&");
            var info = await ResolveMakuFromHtmlAsync(app, page.Url, html, token);
            if (info == null) continue;
            info.Source = "MakuTweaker";
            var actual = VersionNormalizer.ExtractMostSpecific(info.FileName, info.Url, page.Version, html);
            if (string.IsNullOrWhiteSpace(actual)) actual = page.Version;
            info.Version = actual;
            releases.Add(new AppRelease { Version = actual, Title = app.Name + " " + actual, Download = info });
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }

    return SortReleases(releases);
}

private async Task<DownloadInfo> ResolveMakuFromHtmlAsync(AppDefinition app, string pageUrl, string html, CancellationToken token)
{
    var candidates = ExtractCandidates(pageUrl, html)
        .Where(c => ContainsToken(c.Url, "mediafire.com") || ContainsToken(c.Url, "pixeldrain.com"))
        .OrderByDescending(ScoreMakuCandidate)
        .ToList();

    // Prefer a real MediaFire download host when the page exposes one.
    var directMediaFire = candidates
        .Select(c => c.Url)
        .FirstOrDefault(u => Regex.IsMatch(u ?? string.Empty, @"https?://download\d+\.mediafire\.com/", RegexOptions.IgnoreCase));
    if (!string.IsNullOrWhiteSpace(directMediaFire))
        candidates.Insert(0, new Candidate { Url = directMediaFire, FileName = null, Text = directMediaFire });

    foreach (var candidate in candidates)
    {
        try
        {
            var nested = await ResolveNestedDownloadAsync(app, candidate, token, 0);
            var probed = await ProbeBinaryCandidateAsync(nested, token);
            if (probed != null)
            {
                probed.Source = "MakuTweaker";
                return probed;
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }

    var directMatches = Regex.Matches(html, @"https?://download\d+\.mediafire\.com/[^""'<>\s]+", RegexOptions.IgnoreCase)
        .Cast<Match>()
        .Select(m => m.Value.TrimEnd(')', ']', '}', ',', ';'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    foreach (var direct in directMatches)
    {
        var probed = await ProbeBinaryCandidateAsync(new Candidate { Url = direct, Text = direct }, token);
        if (probed != null) return probed;
    }

    var pixeldrainMatches = Regex.Matches(html, @"pixeldrain\.com/(?:u|api/file)/(?<id>[A-Za-z0-9]+)", RegexOptions.IgnoreCase)
        .Cast<Match>()
        .Select(m => m.Groups["id"].Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    foreach (var id in pixeldrainMatches)
    {
        var apiUrl = "https://pixeldrain.com/api/file/" + id + "?download";
        var probed = await ProbeBinaryCandidateAsync(new Candidate { Url = apiUrl, FileName = "MakuTweaker_Setup.exe", Text = apiUrl }, token);
        if (probed != null) return probed;
    }

    return null;
}

private async Task<IReadOnlyList<AppRelease>> GetEverythingReleasesAsync(AppDefinition app, CancellationToken token)
{
    var html = await GetHtmlAsync(app.Website, token);
    var candidates = ExtractCandidates(app.Website, html)
        .Where(c => !IsMobilePackage(c.Url) && !IsMobilePackage(c.FileName))
        .Where(c => HasInstallerExtension(c.Url) || HasInstallerExtension(c.FileName))
        .Where(c => ContainsToken(c.Url, "voidtools.com") || ContainsToken(c.FileName, "everything"))
        .Where(c => AssetSelector.IsArchitectureCompatible(c.Url + " " + c.FileName, app.Download?.AllowArchitectureFallback ?? false))
        .ToList();

    var releases = new List<AppRelease>();
    foreach (var candidate in candidates)
    {
        var name = candidate.FileName ?? Path.GetFileName(candidate.Url?.Split(new[] { '?', '#' }, 2)[0]);
        if (string.IsNullOrWhiteSpace(name)) continue;
        var version = VersionNormalizer.ExtractMostSpecific(name, candidate.Url);
        if (string.IsNullOrWhiteSpace(version)) continue;
        var format = Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(format)) continue;
        var info = CreateInfo(candidate.Url, name, "Everything");
        info.Version = version;
        releases.Add(new AppRelease { Version = version, Title = name, Download = info });
    }
    return SortReleases(releases);
}

private static IEnumerable<LinkInfo> ExtractAnchorLinks(string pageUrl, string html)
{
    if (string.IsNullOrWhiteSpace(html)) yield break;
    var anchorPattern = "<a[^>]+href\\s*=\\s*(?:\"(?<url>[^\"]+)\"|'(?<url>[^']+)')[^>]*>(?<text>.*?)</a>";
    foreach (Match m in Regex.Matches(html, anchorPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
    {
        var raw = WebUtility.HtmlDecode(m.Groups["url"].Value).Trim();
        if (!Uri.TryCreate(new Uri(pageUrl), raw, out var uri)) continue;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
        var text = Regex.Replace(WebUtility.HtmlDecode(m.Groups["text"].Value), "<.*?>", " ");
        yield return new LinkInfo { Url = uri.AbsoluteUri, Text = text }; 
    }
}

private sealed class LinkInfo
{
    public string Url { get; set; }
    public string Text { get; set; }
}

        private async Task<DownloadInfo> ResolveVlcAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetVlcReleasesAsync(app, token);
            var latest = releases.FirstOrDefault();
            if (latest?.Download != null)
            {
                try { return await RequireBinaryProbeAsync(latest.Download, token); }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }

            var platform = PlatformDetectionService.Current;
            var downloadPage = "https://images.videolan.org/vlc/download-windows.html";
            var html = await GetHtmlAsync(downloadPage, token);
            var links = ExtractAnchorLinks(downloadPage, html).ToList();

            var candidates = links
                .Where(x => ContainsToken(x.Url, "get.videolan.org/vlc/") || ContainsToken(x.Url, "videolan.org/vlc/"))
                .Where(x => ContainsToken(x.Text, "Installer for 64bit version") ||
                            ContainsToken(x.Text, "Installer for ARM64 version") ||
                            ContainsToken(x.Text, "Download VLC"))
                .Select(x => new Candidate { Url = x.Url, Text = x.Text, FileName = Path.GetFileName(new Uri(x.Url).AbsolutePath) })
                .ToList();

            // Pick the installer variant for the detected Windows architecture.
            var desired = platform.Architecture == "arm64"
                ? candidates.FirstOrDefault(c => ContainsToken(c.Text, "ARM64"))
                : platform.Architecture == "x86"
                    ? candidates.FirstOrDefault(c => ContainsToken(c.Text, "Download VLC") && !ContainsToken(c.Text, "64bit"))
                    : candidates.FirstOrDefault(c => ContainsToken(c.Text, "64bit version"));

            if (desired == null)
                desired = candidates.FirstOrDefault();

            if (desired != null)
            {
                try
                {
                    // VideoLAN's get.videolan.org URL is a small HTML hand-off page.
                    // ResolveNestedDownloadAsync follows the mirror link to the real EXE.
                    var nested = await ResolveNestedDownloadAsync(app, desired, token, 0);
                    var probed = await ProbeBinaryCandidateAsync(nested, token);
                    if (probed != null)
                        return probed;
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }

            // Fallback: discover the current version on the official page and use the
            // official archive path only after probing the resulting binary.
            var version = FindFirstVersion(html, @"Version\s+(?<version>\d+(?:\.\d+){2,9})");
            if (string.IsNullOrWhiteSpace(version))
                version = VersionNormalizer.ExtractMostSpecific(html);
            var directory = platform.Architecture == "arm64" ? "winarm64" : platform.Architecture == "x86" ? "win32" : "win64";
            if (!string.IsNullOrWhiteSpace(version))
            {
                var file = "vlc-" + version + "-" + directory + ".exe";
                try
                {
                    var fallback = CreateInfo("https://download.videolan.org/pub/videolan/vlc/" + version + "/" + directory + "/" + file, file, "VideoLAN");
                    return await RequireBinaryProbeAsync(fallback, token);
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }

            throw new InvalidOperationException("Не удалось найти рабочий установщик VLC на официальном сервере VideoLAN.");
        }


private async Task<DownloadInfo> ResolveAida64Async(AppDefinition app, CancellationToken token)
{
    var platform = PlatformDetectionService.Current;
    if (platform.Architecture == "x86")
        throw new InvalidOperationException("AIDA64 v8.xx поддерживает только 64-разрядную Windows 10 и новее.");

    var releases = await GetAida64ReleasesAsync(app, token);
    var latest = releases.FirstOrDefault();
    if (latest?.Download != null)
    {
        try { return await RequireBinaryProbeAsync(latest.Download, token); }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }

    var pageUrl = "https://www.aida64.com/downloads";
    var html = await GetHtmlAsync(pageUrl, token);
    var links = ExtractAnchorLinks(pageUrl, html).ToList();

    // The AIDA64 downloads table links to a product-specific page. That page
    // contains the current download mirror(s), so use those links instead of
    // guessing aida64extreme*.exe names (which can change).
    var extremePages = links
        .Where(x => ContainsToken(x.Text, "AIDA64 Extreme") &&
                    (ContainsToken(x.Text, "Download") || ContainsToken(x.Url, "/downloads/")))
        .OrderBy(x => x.Url.Length)
        .Select(x => x.Url)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(5)
        .ToList();

    var pageCandidates = new List<Candidate>();
    foreach (var detailUrl in extremePages)
    {
        try
        {
            var detailHtml = await GetHtmlAsync(detailUrl, token);
            pageCandidates.AddRange(ExtractCandidates(detailUrl, detailHtml));

            foreach (Match match in Regex.Matches(detailHtml, @"https?://download(?:2)?\.aida64\.com/[^\s""'<>]+", RegexOptions.IgnoreCase))
                pageCandidates.Add(new Candidate { Url = WebUtility.HtmlDecode(match.Value), Text = match.Value });
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }

    var candidates = pageCandidates
        .Where(c => HasInstallerExtension(c.Url) || HasInstallerExtension(c.FileName))
        .Where(c => ContainsToken(c.Url, "aida64") || ContainsToken(c.FileName, "aida64"))
        .Where(c => AssetSelector.IsWindowsAsset(c.Url + " " + c.FileName))
        .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .OrderByDescending(c => Score(c, NormalizeTokens("AIDA64 Extreme"), "aida64.com"))
        .ToList();

    foreach (var candidate in candidates)
    {
        try
        {
            var info = await RequireBinaryProbeAsync(CreateInfo(candidate.Url, candidate.FileName, "AIDA64"), token);
            var labeledVersion = FindFirstVersion(html, @"AIDA64\s+Extreme\s+Trial\s+version[\s\S]{0,500}?(?<version>\d+\.\d+\.\d+)");
            if (string.IsNullOrWhiteSpace(labeledVersion))
                labeledVersion = FindFirstVersion(html, @"AIDA64\s+(?:Extreme|Engineer|Business)[\s\S]{0,600}?(?<version>\d+\.\d+\.\d+)");
            if (string.IsNullOrWhiteSpace(labeledVersion))
                labeledVersion = FindFirstVersion(html, @"Version[\s\S]{0,120}?(?<version>\d+\.\d+\.\d+)");
            info.Version = string.IsNullOrWhiteSpace(labeledVersion)
                ? VersionNormalizer.ExtractMostSpecific(info.FileName)
                : labeledVersion;
            return info;
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }

    // Last fallback for a temporarily changed page: construct the current
    // filename from the displayed stable version, but still verify the URL.
    var version = FindFirstVersion(html, @"(?<version>\d+(?:\.\d+){2,9})\s*(?:stable|Stable)");
    if (string.IsNullOrWhiteSpace(version))
        version = VersionNormalizer.ExtractMostSpecific(html);
    if (!string.IsNullOrWhiteSpace(version))
    {
        var parts = version.Split('.');
        var compact = parts.Length >= 2 ? parts[0] + parts[1] : parts[0];
        var fileName = "aida64extreme" + compact + ".exe";
        foreach (var host in new[] { "download2.aida64.com", "download.aida64.com" })
        {
            try
            {
                var info = await RequireBinaryProbeAsync(CreateInfo("https://" + host + "/" + fileName, fileName, "AIDA64"), token);
                info.Version = version;
                return info;
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
    }

    throw new InvalidOperationException("Не удалось найти рабочий установщик AIDA64 на официальном сервере.");
}

        private async Task<DownloadInfo> ResolveTorBrowserAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetTorBrowserReleasesAsync(app, token);
            var latest = releases.FirstOrDefault(item => item?.Download != null);
            if (latest == null)
                throw new InvalidOperationException("Не удалось найти стабильные версии Tor Browser.");
            return latest.Download;
        }

        private async Task<IReadOnlyList<AppRelease>> GetTorBrowserReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var root = "https://dist.torproject.org/torbrowser/";
            var html = await GetHtmlAsync(root, token);
            var versions = Regex.Matches(html, @"(?<![0-9])(?<version>\d+\.\d+\.\d+)(?=\/)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(m => m.Groups["version"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => VersionInfo.Parse(v))
                .ToList();

            var architecture = PlatformDetectionService.Current.Architecture;
            var platform = architecture == "x86" ? "i686" : "x86_64";
            var releases = new List<AppRelease>();

            foreach (var version in versions)
            {
                var fileName = "tor-browser-windows-" + platform + "-portable-" + version + ".exe";
                var url = root + version + "/" + fileName;
                var info = CreateInfo(url, fileName, "Tor Browser");
                info.Version = version;

                releases.Add(new AppRelease
                {
                    Version = version,
                    Title = "Tor Browser " + version,
                    Download = info
                });
            }

            return releases;
        }



private async Task<DownloadInfo> ResolveMakuTweakerAsync(AppDefinition app, CancellationToken token)
{
    var html = (await GetHtmlAsync(app.Website, token)).Replace("\\/", "/").Replace("\\u0026", "&");
    var info = await ResolveMakuFromHtmlAsync(app, app.Website, html, token);
    if (info == null)
        throw new InvalidOperationException("Не удалось найти рабочий установщик MakuTweaker. Источник может потребовать браузерный JavaScript-редирект.");
    info.Source = "MakuTweaker";
    info.Version = VersionNormalizer.ExtractMostSpecific(info.FileName, info.Url, html);
    return info;
}

        private async Task<DownloadInfo> ResolveFirefoxAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetFirefoxReleasesAsync(app, token);
            var latest = releases.FirstOrDefault(item => item?.Download != null);
            if (latest == null)
                throw new InvalidOperationException("Mozilla не вернула актуальную версию Firefox.");
            return latest.Download;
        }

        private async Task<IReadOnlyList<AppRelease>> GetFirefoxReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var root = "https://ftp.mozilla.org/pub/firefox/releases/";
            var html = await GetHtmlAsync(root, token);
            var versions = Regex.Matches(html, @"(?<![0-9])(?<version>\d+\.\d+(?:\.\d+){0,2})(?=\/)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(m => m.Groups["version"].Value)
                .Where(v => VersionInfo.Parse(v).CompareTo(VersionInfo.Parse("42.0")) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => VersionInfo.Parse(v))
                .ToList();

            var architecture = PlatformDetectionService.Current.Architecture;
            var platformFolder = architecture == "arm64" ? "win64-aarch64" : architecture == "x86" ? "win32" : "win64";
            var releases = new List<AppRelease>();

            foreach (var version in versions)
            {
                var fileName = "Firefox Setup " + version + ".exe";
                var encodedFileName = Uri.EscapeDataString(fileName).Replace("%2D", "-");
                var url = root + version + "/" + platformFolder + "/en-US/" + encodedFileName;
                var info = CreateInfo(url, fileName, "Mozilla");
                info.Version = version;

                releases.Add(new AppRelease
                {
                    Version = version,
                    Title = "Firefox " + version,
                    Download = info
                });
            }

            return releases;
        }

        private async Task<DownloadInfo> ResolveVSCodeAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetVSCodeReleasesAsync(app, token);
            var latest = releases.FirstOrDefault();
            if (latest?.Download != null)
            {
                try
                {
                    var versionInfo = latest.Download;
                    return await RequireBinaryProbeAsync(versionInfo, token);
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }

            var architecture = PlatformDetectionService.Current.Architecture;
            var target = architecture == "arm64" ? "win32-arm64-user" : architecture == "x86" ? "win32-user" : "win32-x64-user";
            var url = "https://update.code.visualstudio.com/latest/" + target + "/stable";
            var fileName = architecture == "arm64" ? "VSCodeUserSetup-arm64.exe" : architecture == "x86" ? "VSCodeUserSetup.exe" : "VSCodeUserSetup-x64.exe";
            return await RequireBinaryProbeAsync(CreateInfo(url, fileName, "Microsoft"), token);
        }

        private async Task<DownloadInfo> ResolveMalwarebytesAsync(AppDefinition app, CancellationToken token)
        {
            var pageUrl = "https://www.malwarebytes.com/mwb-download/";
            var html = (await GetHtmlAsync(pageUrl, token)).Replace("\\/", "/").Replace("\\u0026", "&");
            var candidates = ExtractCandidates(pageUrl, html)
                .Where(c => ContainsToken(c.Url, "downloads.malwarebytes.com") || ContainsToken(c.Url, "data-cdn.mbamupdates.com") || ContainsToken(c.FileName, "mbsetup"))
                .Where(c => HasInstallerExtension(c.Url) || HasInstallerExtension(c.FileName) || ContainsToken(c.Url, "/file/mb-windows"))
                .ToList();
            var selected = SelectBestCandidate(app, candidates);

            var url = selected?.Url;
            var fileName = selected?.FileName;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://data-cdn.mbamupdates.com/web/mb5-setup-consumer/MBSetup.exe";
                fileName = "MBSetup.exe";
            }

            var info = await RequireBinaryProbeAsync(CreateInfo(url, fileName ?? "MBSetup.exe", "Malwarebytes"), token);
            var version = FindFirstVersion(html, @"Malwarebytes(?:\s+for\s+Windows)?[^0-9]{0,40}(?<version>5\.\d+\.\d+)");
            if (string.IsNullOrWhiteSpace(version))
                version = "5.7.2";
            info.Version = version;
            return info;
        }

        private async Task<IReadOnlyList<AppRelease>> GetWinRarReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var pageUrl = "https://www.win-rar.com/whatsnew.html";
            var html = await GetHtmlAsync(pageUrl, token);
            var releases = new List<AppRelease>();

            foreach (Match match in Regex.Matches(
                html,
                @"Version\s+(?<version>\d+\.\d+)(?!\s*(?:beta|alpha|rc))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var version = match.Groups["version"].Value;
                if (VersionInfo.Parse(version).CompareTo(VersionInfo.Parse("6.00")) < 0)
                    continue;

                var parts = version.Split('.');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
                    continue;

                var code = (major * 100 + minor).ToString("000");
                var fileName = "winrar-x64-" + code + ".exe";
                var url = "https://www.win-rar.com/fileadmin/winrar-versions/winrar/" + fileName;
                var info = CreateInfo(url, fileName, "WinRAR");
                info.Version = version;

                releases.Add(new AppRelease
                {
                    Version = version,
                    Title = "WinRAR " + version,
                    Download = info
                });
            }

            return releases
                .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => VersionInfo.Parse(item.Version))
                .ToList();
        }

        private async Task<IReadOnlyList<AppRelease>> GetVlcReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var root = "https://download.videolan.org/pub/videolan/vlc/";
            var html = await GetHtmlAsync(root, token);
            var versions = Regex.Matches(
                    html,
                    @"(?<![0-9])(?<version>\d+\.\d+(?:\.\d+){0,2})(?=\/)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => match.Groups["version"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(version => VersionInfo.Parse(version))
                .ToList();

            var platform = PlatformDetectionService.Current.Architecture == "arm64"
                ? "winarm64"
                : PlatformDetectionService.Current.Architecture == "x86"
                    ? "win32"
                    : "win64";

            var releases = new List<AppRelease>();
            foreach (var version in versions)
            {
                var fileName = "vlc-" + version + "-" + platform + ".exe";
                var url = root + version + "/" + platform + "/" + fileName;
                var info = CreateInfo(url, fileName, "VideoLAN");
                info.Version = version;

                releases.Add(new AppRelease
                {
                    Version = version,
                    Title = "VLC " + version,
                    Download = info
                });
            }

            return releases;
        }

        private async Task<IReadOnlyList<AppRelease>> GetAida64ReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var pageUrl = "https://www.aida64.com/downloads/archive";
            var html = await GetHtmlAsync(pageUrl, token);
            var versions = Regex.Matches(
                    html,
                    @"(?<![0-9])(?<version>\d+\.\d+\.\d{3,5})(?![0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => match.Groups["version"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(version => VersionInfo.Parse(version).CompareTo(VersionInfo.Parse("5.50.5500")) >= 0)
                .OrderByDescending(version => VersionInfo.Parse(version))
                .ToList();

            var releases = new List<AppRelease>();
            foreach (var version in versions)
            {
                var parts = version.Split('.');
                if (parts.Length < 2)
                    continue;

                var compact = parts[0] + parts[1];
                var fileName = "aida64extreme" + compact + ".exe";
                var url = "https://download2.aida64.com/" + fileName;
                var info = CreateInfo(url, fileName, "AIDA64");
                info.Version = version;

                releases.Add(new AppRelease
                {
                    Version = version,
                    Title = "AIDA64 " + version,
                    Download = info
                });
            }

            return releases
                .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => VersionInfo.Parse(item.Version))
                .ToList();
        }

        private async Task<IReadOnlyList<AppRelease>> GetVSCodeReleasesAsync(AppDefinition app, CancellationToken token)
        {
            var pageUrl = "https://code.visualstudio.com/updates/archive";
            var html = await GetHtmlAsync(pageUrl, token);
            var versions = Regex.Matches(
                    html,
                    @"Visual\s+Studio\s+Code\s+(?<version>\d+\.\d+)(?!\s*\(Insiders\))",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => match.Groups["version"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(version => VersionInfo.Parse(version))
                .ToList();

            var architecture = PlatformDetectionService.Current.Architecture;
            var target = architecture == "arm64"
                ? "win32-arm64-user"
                : architecture == "x86"
                    ? "win32-user"
                    : "win32-x64-user";

            var releases = new List<AppRelease>();
            foreach (var version in versions)
            {
                var fileName = architecture == "arm64"
                    ? "VSCodeUserSetup-" + version + "-arm64.exe"
                    : architecture == "x86"
                        ? "VSCodeUserSetup-" + version + ".exe"
                        : "VSCodeUserSetup-" + version + "-x64.exe";
                var url = "https://update.code.visualstudio.com/" + version + "/" + target + "/stable";
                var info = CreateInfo(url, fileName, "Microsoft");

                info.Version = version;
                releases.Add(new AppRelease
                {
                    Version = version,
                    Title = "Visual Studio Code " + version,
                    Download = info
                });
            }

            return releases;
        }

        private async Task<DownloadInfo> ResolveWinRarAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetWinRarReleasesAsync(app, token);
            var latest = releases.FirstOrDefault();
            if (latest?.Download != null)
            {
                try { return await RequireBinaryProbeAsync(latest.Download, token); }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }

            var pageUrl = "https://www.win-rar.com/download.html";
            var html = await GetHtmlAsync(pageUrl, token);
            var candidates = ExtractCandidates(pageUrl, html)
                .Where(c => HasInstallerExtension(c.Url) || HasInstallerExtension(c.FileName))
                .Where(c => ContainsToken(c.Url, "winrar-x64") || ContainsToken(c.FileName, "winrar-x64") ||
                            ContainsToken(c.Url, "wrar") || ContainsToken(c.FileName, "wrar"))
                .ToList();
            var selected = SelectBestCandidate(app, candidates);
            if (selected == null)
                throw new InvalidOperationException("Не удалось найти установщик WinRAR на официальной странице.");

            var info = await RequireBinaryProbeAsync(CreateInfo(selected.Url, selected.FileName, "WinRAR"), token);
            var version = FindFirstVersion(html, @"WinRAR\s+(?<version>\d+\.\d+)");
            if (string.IsNullOrWhiteSpace(version))
                version = "7.23";
            info.Version = version;
            return info;
        }

        private async Task<DownloadInfo> ResolveNvidiaAppAsync(AppDefinition app, CancellationToken token)
        {
            var html = (await GetHtmlAsync(app.Website, token)).Replace("\\/", "/").Replace("\\u0026", "&");
            var candidates = ExtractCandidates(app.Website, html)
                .Where(c => HasInstallerExtension(c.Url) || HasInstallerExtension(c.FileName))
                .Where(c => ContainsToken(c.Url, "nvapp") || ContainsToken(c.FileName, "nvidia_app") || ContainsToken(c.FileName, "nvidiaapp"))
                .ToList();
            var selected = SelectBestCandidate(app, candidates);
            if (selected == null)
                throw new InvalidOperationException("Не удалось найти установщик NVIDIA App на официальной странице.");

            var info = await RequireBinaryProbeAsync(CreateInfo(selected.Url, selected.FileName, "NVIDIA"), token);

            try
            {
                var highlights = await GetHtmlAsync("https://www.nvidia.com/en-us/software/nvidia-app/release-highlights/", token);
                var version = FindFirstVersion(highlights, @"NVIDIA\s+App\s+(?<version>\d+\.\d+\.\d+)");
                if (string.IsNullOrWhiteSpace(version))
                    version = FindFirstVersion(highlights, @"App\s+(?<version>\d+\.\d+\.\d+)");
                info.Version = string.IsNullOrWhiteSpace(version) ? "11.0.9" : version;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                info.Version = "11.0.9";
            }

            return info;
        }

        private async Task<DownloadInfo> ResolveVisualStudioAsync(AppDefinition app, CancellationToken token)
        {
            var pageUrl = "https://visualstudio.microsoft.com/downloads/";
            var html = await GetHtmlAsync(pageUrl, token);
            DownloadInfo info = null;
            try
            {
                info = await ResolveGenericAsync(app, token);
            }
            catch (Exception) when (!token.IsCancellationRequested) { }

            if (info == null)
            {
                var fallback = CreateInfo("https://aka.ms/vs/18/release/vs_community.exe", "vs_community.exe", "Microsoft");
                info = await RequireBinaryProbeAsync(fallback, token);
            }

            var version = FindFirstVersion(html, @"Visual\s+Studio\s+Community[\s\S]{0,120}?Version\s+(?<version>\d+(?:\.\d+){1,3})");
            if (string.IsNullOrWhiteSpace(version))
                version = "18.10.3";
            info.Version = version;
            return info;
        }

        private async Task<DownloadInfo> ResolveGlaryUtilitiesAsync(AppDefinition app, CancellationToken token)
        {
            var html = await GetHtmlAsync(app.Website, token);
            DownloadInfo info = null;
            try
            {
                info = await ResolveGenericAsync(app, token);
            }
            catch (Exception) when (!token.IsCancellationRequested) { }

            if (info == null)
                throw new InvalidOperationException("Не удалось найти установщик Glary Utilities.");

            info = await RequireBinaryProbeAsync(info, token);
            var version = FindFirstVersion(html, @"Current\s+Version[\s\S]{0,100}?(?<version>\d+(?:\.\d+){2,4})");
            if (string.IsNullOrWhiteSpace(version))
                version = FindFirstVersion(html, @"Glary\s+Utilities[^0-9]{0,30}(?<version>\d+(?:\.\d+){2,4})");
            if (string.IsNullOrWhiteSpace(version))
                version = "6.47.0.51";
            info.Version = version;
            return info;
        }

        private async Task<DownloadInfo> ResolveHwMonitorAsync(AppDefinition app, CancellationToken token)
        {
            var html = await GetHtmlAsync(app.Website, token);
            var info = await ResolveGenericAsync(app, token);
            info = await RequireBinaryProbeAsync(info, token);

            var version = FindFirstVersion(html, @"HWMonitor(?:\s+PRO)?\s+(?<version>\d+\.\d+)");
            if (string.IsNullOrWhiteSpace(version))
                version = FindFirstVersion(html, @"Version\s*(?<version>\d+\.\d+)");
            if (string.IsNullOrWhiteSpace(version))
                version = "1.59";
            info.Version = version;
            return info;
        }

        private async Task<DownloadInfo> ResolveOperaAsync(AppDefinition app, CancellationToken token, bool gx)
        {
            var releases = await GetOperaReleasesAsync(app, token, gx);
            var latest = releases.FirstOrDefault(item => item?.Download != null);
            if (latest == null)
                throw new InvalidOperationException("Не удалось определить актуальную версию Opera" + (gx ? " GX" : "") + ".");
            return latest.Download;
        }

        private async Task<IReadOnlyList<AppRelease>> GetOperaReleasesAsync(AppDefinition app, CancellationToken token, bool gx)
        {
            var root = gx
                ? "https://ftp.opera.com/pub/opera_gx/"
                : "https://ftp.opera.com/pub/opera/desktop/";
            var html = await GetHtmlAsync(root, token);

            var versions = Regex.Matches(html, @"(?<![0-9])(?<version>\d+\.\d+\.\d+\.\d+)(?=\/)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(m => m.Groups["version"].Value)
                .Where(v => VersionInfo.Parse(v).CompareTo(VersionInfo.Parse("42.0.2393.85")) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => VersionInfo.Parse(v))
                .ToList();

            var architecture = PlatformDetectionService.Current.Architecture;
            var suffix = architecture == "arm64" ? "_arm64" : architecture == "x86" ? "_x86" : "_x64";
            var releases = new List<AppRelease>();

            foreach (var version in versions)
            {
                var fileName = gx
                    ? "Opera_GX_" + version + "_Setup" + suffix + ".exe"
                    : "Opera_" + version + "_Setup" + suffix + ".exe";
                var url = root + version + "/win/" + fileName;
                var info = CreateInfo(url, fileName, gx ? "Opera GX" : "Opera");
                info.Version = version;

                releases.Add(new AppRelease
                {
                    Version = version,
                    Title = (gx ? "Opera GX " : "Opera ") + version,
                    Download = info
                });
            }

            return releases;
        }
    }

}
