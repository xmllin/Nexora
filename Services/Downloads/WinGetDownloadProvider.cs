using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Models;

namespace Nexora.Services.Downloads
{
    /// <summary>
    /// Resolves exact application versions and downloads the selected installer
    /// through the Windows Package Manager (WinGet).
    ///
    /// The catalog supplies a package id instead of a hard-coded installer URL.
    /// WinGet is then the source of truth for available versions, architecture,
    /// installer type and the actual installer payload.
    /// </summary>
    public sealed class WinGetDownloadProvider : IDownloadProvider, IReleaseDownloadProvider, IManagedDownloadProvider
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);
        private static readonly Regex PackageIdRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static string CachedWingetPath;
        private static readonly object WingetPathLock = new object();

        private static readonly Regex VersionLineRegex = new Regex(
            @"^v?([0-9]+(?:\.[0-9]+){0,15}(?:[-+][0-9A-Za-z.-]+)?)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public async Task<DownloadInfo> ResolveAsync(AppDefinition app, CancellationToken token)
        {
            var releases = await GetReleasesAsync(app, token).ConfigureAwait(false);
            var first = releases.FirstOrDefault(x => x?.Download != null);
            if (first?.Download == null)
                throw new InvalidOperationException("WinGet не вернул подходящую версию приложения.");
            return first.Download;
        }

        public async Task<string> GetLatestVersionAsync(AppDefinition app, CancellationToken token)
        {
            ValidateDefinition(app);

            var winget = await ResolveWingetAsync(token).ConfigureAwait(false);
            var args = new List<string>
            {
                "show",
                "--id", app.Download.PackageId,
                "--exact",
                "--locale", "en-US",
                "--accept-source-agreements",
                "--disable-interactivity"
            };

            var source = string.IsNullOrWhiteSpace(app.Download.PackageSource)
                ? "winget"
                : app.Download.PackageSource.Trim();

            if (!string.IsNullOrWhiteSpace(source))
            {
                args.Add("--source");
                args.Add(source);
            }

            var result = await RunProcessAsync(winget, args, token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    "WinGet не смог получить последнюю версию для " + app.Download.PackageId +
                    "." + FormatCommandError(result));

            var version = ParseLatestVersion(result.StandardOutput);
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException(
                    "WinGet не вернул поле Version для " + app.Download.PackageId + ".");

            return version;
        }

        public async Task EnrichDownloadInfoAsync(AppDefinition app, DownloadInfo info, CancellationToken token)
        {
            ValidateDefinition(app);
            if (info == null)
                throw new InvalidOperationException("Не выбрана версия WinGet.");
            if (string.IsNullOrWhiteSpace(info.Version))
                throw new InvalidOperationException("У загрузки WinGet не указана версия.");

            var winget = await ResolveWingetAsync(token).ConfigureAwait(false);
            var args = new List<string>
            {
                "show",
                "--id", info.PackageId ?? app.Download.PackageId,
                "--exact",
                "--version", info.Version,
                "--accept-source-agreements",
                "--disable-interactivity",
                "--locale", "en-US"
            };

            var source = string.IsNullOrWhiteSpace(info.PackageSource)
                ? (string.IsNullOrWhiteSpace(app.Download.PackageSource) ? "winget" : app.Download.PackageSource.Trim())
                : info.PackageSource.Trim();

            if (!string.IsNullOrWhiteSpace(source))
            {
                args.Add("--source");
                args.Add(source);
            }

            var architecture = !string.IsNullOrWhiteSpace(info.Architecture)
                ? info.Architecture
                : app.Download.Architecture;
            if (!string.IsNullOrWhiteSpace(architecture))
            {
                args.Add("--architecture");
                args.Add(MapArchitecture(architecture));
            }

            var installerType = !string.IsNullOrWhiteSpace(info.InstallerType)
                ? info.InstallerType
                : app.Download.InstallerType;
            if (!string.IsNullOrWhiteSpace(installerType))
            {
                args.Add("--installer-type");
                args.Add(installerType);
            }

            var result = await RunProcessAsync(winget, args, token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                // Some manifests reject an explicit architecture/installer-type
                // combination even though the package/version itself is valid.
                var relaxedArgs = new List<string>
                {
                    "show",
                    "--id", info.PackageId ?? app.Download.PackageId,
                    "--exact",
                    "--version", info.Version,
                    "--locale", "en-US",
                    "--accept-source-agreements",
                    "--disable-interactivity"
                };
                if (!string.IsNullOrWhiteSpace(source))
                {
                    relaxedArgs.Add("--source");
                    relaxedArgs.Add(source);
                }

                result = await RunProcessAsync(winget, relaxedArgs, token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }

            if (result.ExitCode != 0)
                return;

            var installerUrl = ParseField(result.StandardOutput, "Installer Url", "Installer URL", "Install Url", "Install URL");
            if (string.IsNullOrWhiteSpace(installerUrl))
                installerUrl = ExtractInstallerUrl(result.StandardOutput);

            var resolvedType = ParseField(result.StandardOutput, "Installer Type");
            if (!string.IsNullOrWhiteSpace(resolvedType))
                info.InstallerType = resolvedType;

            var actualVersion = ParseLatestVersion(result.StandardOutput);
            if (!string.IsNullOrWhiteSpace(actualVersion))
                info.Version = actualVersion;

            if (string.IsNullOrWhiteSpace(installerUrl))
                return;

            var metadata = new DownloadMetadataService();
            var probe = await metadata.ProbeAsync(installerUrl, token).ConfigureAwait(false);
            info.Url = string.IsNullOrWhiteSpace(probe.FinalUrl) ? installerUrl : probe.FinalUrl;
            if (!string.IsNullOrWhiteSpace(probe.FileName))
                info.FileName = probe.FileName;
            if (probe.SizeBytes.HasValue)
                info.SizeBytes = probe.SizeBytes;
            else
                info.SizeBytes = await metadata.GetSizeAsync(info.Url, token).ConfigureAwait(false);
        }

        private static string ExtractInstallerUrl(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            foreach (Match match in Regex.Matches(output, @"https?://[^\s<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var url = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
                if (url.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf(".msi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf(".msix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf(".appx", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf(".zip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf(".7z", StringComparison.OrdinalIgnoreCase) >= 0)
                    return url;
            }

            return string.Empty;
        }

        private static string ParseField(string output, params string[] fieldNames)
        {
            if (string.IsNullOrWhiteSpace(output) || fieldNames == null || fieldNames.Length == 0)
                return string.Empty;

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim();
                var separator = value.IndexOf(':');
                if (separator <= 0)
                    continue;

                var field = value.Substring(0, separator).Trim();
                if (!fieldNames.Any(name => string.Equals(field, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                return value.Substring(separator + 1).Trim();
            }

            return string.Empty;
        }

        public async Task<IReadOnlyList<AppRelease>> GetReleasesAsync(AppDefinition app, CancellationToken token)
        {
            ValidateDefinition(app);

            var winget = await ResolveWingetAsync(token).ConfigureAwait(false);
            var args = new List<string>
            {
                "show",
                "--id", app.Download.PackageId,
                "--exact",
                "--versions",
                "--locale", "en-US",
                "--accept-source-agreements",
                "--disable-interactivity"
            };

            var source = string.IsNullOrWhiteSpace(app.Download.PackageSource)
                ? "winget"
                : app.Download.PackageSource.Trim();

            if (!string.IsNullOrWhiteSpace(source))
            {
                args.Add("--source");
                args.Add(source);
            }

            var result = await RunProcessAsync(winget, args, token, CommandTimeout).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    "WinGet не смог получить список версий для " + app.Download.PackageId +
                    "." + FormatCommandError(result));

            var versions = ParseAvailableVersions(result.StandardOutput);
            if (versions.Count == 0)
                throw new InvalidOperationException(
                    "WinGet не вернул список версий для " + app.Download.PackageId + ".");

            return versions
                .OrderByDescending(version => version, Comparer<string>.Create(CompareVersionStrings))
                .Select(version => CreateRelease(app, source, version))
                .ToList();
        }

        public async Task<string> DownloadAsync(
            AppDefinition app,
            DownloadInfo info,
            string destinationPath,
            IProgress<DownloadProgress> progress,
            CancellationToken token)
        {
            ValidateDefinition(app);
            if (info == null)
                throw new InvalidOperationException("Не выбрана версия WinGet.");
            if (string.IsNullOrWhiteSpace(info.PackageId))
                throw new InvalidOperationException("У загрузки WinGet не указан PackageId.");

            var winget = await ResolveWingetAsync(token).ConfigureAwait(false);
            var staging = Path.Combine(
                Path.GetTempPath(),
                "Nexora",
                "WinGet",
                SanitizePathPart(info.PackageId),
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(staging);

            try
            {
                progress?.Report(new DownloadProgress
                {
                    BytesReceived = 0,
                    TotalBytes = null,
                    Progress = 0,
                    BytesPerSecond = 0
                });

                var args = new List<string>
                {
                    "download",
                    "--id", info.PackageId,
                    "--exact",
                    "--version", info.Version,
                    "--download-directory", staging,
                    "--accept-source-agreements",
                    "--accept-package-agreements",
                    "--skip-license",
                    "--disable-interactivity",
                    "--locale", "en-US"
                };

                var source = string.IsNullOrWhiteSpace(info.PackageSource)
                    ? (string.IsNullOrWhiteSpace(app.Download.PackageSource) ? "winget" : app.Download.PackageSource.Trim())
                    : info.PackageSource.Trim();

                if (!string.IsNullOrWhiteSpace(source))
                {
                    args.Add("--source");
                    args.Add(source);
                }

                if (!string.IsNullOrWhiteSpace(info.Architecture))
                {
                    args.Add("--architecture");
                    args.Add(MapArchitecture(info.Architecture));
                }
                else if (!string.IsNullOrWhiteSpace(app.Download.Architecture))
                {
                    args.Add("--architecture");
                    args.Add(MapArchitecture(app.Download.Architecture));
                }

                if (!string.IsNullOrWhiteSpace(info.InstallerType))
                {
                    args.Add("--installer-type");
                    args.Add(info.InstallerType);
                }
                else if (!string.IsNullOrWhiteSpace(app.Download.InstallerType))
                {
                    args.Add("--installer-type");
                    args.Add(app.Download.InstallerType);
                }

                var result = await RunProcessAsync(winget, args, token, CommandTimeout, line =>
                {
                    var match = Regex.Match(line ?? string.Empty, @"(?<![0-9])([0-9]{1,3})%(?![0-9])");
                    if (!match.Success) return;

                    if (!int.TryParse(match.Groups[1].Value, out var percent)) return;

                    progress?.Report(new DownloadProgress
                    {
                        BytesReceived = 0,
                        TotalBytes = null,
                        Progress = Math.Max(0, Math.Min(100, percent)),
                        BytesPerSecond = 0
                    });
                }).ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    await TryRefreshWingetSourceAsync(winget, token).ConfigureAwait(false);
                    result = await RunProcessAsync(winget, args, token, CommandTimeout, line =>
                    {
                        var match = Regex.Match(line ?? string.Empty, @"(?<![0-9])([0-9]{1,3})%(?![0-9])");
                        if (!match.Success) return;

                        if (!int.TryParse(match.Groups[1].Value, out var percent)) return;

                        progress?.Report(new DownloadProgress
                        {
                            BytesReceived = 0,
                            TotalBytes = null,
                            Progress = Math.Max(0, Math.Min(100, percent)),
                            BytesPerSecond = 0
                        });
                    }).ConfigureAwait(false);
                }

                if (result.ExitCode != 0)
                    throw new InvalidOperationException(
                        "WinGet не смог скачать " + info.PackageId + " " + info.Version + "." +
                        FormatCommandError(result));

                var downloaded = FindInstallerFile(staging, info);
                if (downloaded == null)
                    throw new InvalidOperationException(
                        "WinGet завершился без найденного установочного файла для " +
                        info.PackageId + " " + info.Version + ".");

                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                    throw new InvalidOperationException("Не удалось определить папку назначения загрузки.");

                Directory.CreateDirectory(destinationDirectory);
                var finalPath = destinationPath;

                if (File.Exists(finalPath))
                    File.Delete(finalPath);

                File.Move(downloaded, finalPath);

                var size = new FileInfo(finalPath).Length;
                progress?.Report(new DownloadProgress
                {
                    BytesReceived = size,
                    TotalBytes = size,
                    Progress = 100,
                    BytesPerSecond = size
                });

                info.FileName = Path.GetFileName(finalPath);
                info.SizeBytes = size;
                return finalPath;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(staging))
                        Directory.Delete(staging, true);
                }
                catch
                {
                    // A stale temp directory is harmless and can be cleaned later.
                }
            }
        }

        /// <summary>
        /// Parses the version-only lines emitted by "winget show --versions".
        /// It intentionally ignores localized headings and unrelated metadata.
        /// </summary>
        private static string ParseLatestVersion(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
                return string.Empty;

            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim();
                var separator = value.IndexOf(':');
                if (separator <= 0)
                    continue;

                var field = value.Substring(0, separator).Trim();
                if (!string.Equals(field, "Version", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(field, "Версия", StringComparison.OrdinalIgnoreCase))
                    continue;

                var version = value.Substring(separator + 1).Trim();
                var match = VersionLineRegex.Match(version);
                if (match.Success)
                    return version.TrimStart('v', 'V');
            }

            return string.Empty;
        }

        public static List<string> ParseAvailableVersions(string stdout)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(stdout)) return result;

            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim().TrimStart('-', '*', ' ', '\t');
                if (value.EndsWith(".", StringComparison.Ordinal))
                    value = value.TrimEnd('.').Trim();

                if (string.IsNullOrWhiteSpace(value)) continue;

                var match = VersionLineRegex.Match(value);
                if (!match.Success) continue;

                var normalized = value.TrimStart('v', 'V');
                if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    result.Add(normalized);
            }

            return result;
        }

        private static AppRelease CreateRelease(AppDefinition app, string source, string version)
        {
            var architecture = !string.IsNullOrWhiteSpace(app.Download.Architecture)
                ? app.Download.Architecture
                : PlatformDetectionService.Current.Architecture;

            var installerType = app.Download.InstallerType;
            return new AppRelease
            {
                Version = version,
                Title = app.Name ?? app.Download.PackageId,
                Download = new DownloadInfo
                {
                    Url = BuildPseudoUrl(source, app.Download.PackageId, version, architecture, installerType),
                    FileName = BuildDisplayFileName(app, version, installerType),
                    Source = "WinGet",
                    Version = version,
                    PackageId = app.Download.PackageId,
                    PackageSource = source,
                    Architecture = architecture,
                    InstallerType = installerType
                }
            };
        }

        private static string BuildDisplayFileName(AppDefinition app, string version, string installerType)
        {
            var baseName = SanitizeFilePart(app?.Download?.PackageId);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = SanitizeFilePart(app?.Name);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "installer";

            var extension = InstallerTypeToExtension(installerType);
            return baseName + "-" + version + extension;
        }

        private static string BuildPseudoUrl(
            string source,
            string packageId,
            string version,
            string architecture,
            string installerType)
        {
            return "winget://" +
                   Uri.EscapeDataString(source ?? "winget") + "/" +
                   Uri.EscapeDataString(packageId ?? string.Empty) + "/" +
                   Uri.EscapeDataString(version ?? string.Empty) +
                   "?arch=" + Uri.EscapeDataString(architecture ?? string.Empty) +
                   "&type=" + Uri.EscapeDataString(installerType ?? string.Empty);
        }

        private static string InstallerTypeToExtension(string installerType)
        {
            if (string.IsNullOrWhiteSpace(installerType)) return ".exe";

            switch (installerType.Trim().ToLowerInvariant())
            {
                case "msi": return ".msi";
                case "msix": return ".msix";
                case "appx": return ".appx";
                case "zip": return ".zip";
                case "portable": return ".zip";
                default: return ".exe";
            }
        }

        private static string FindInstallerFile(string directory, DownloadInfo info)
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(IsInstallerFile)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0)
                .ToList();

            if (files.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(info.FileName))
            {
                var exact = files.FirstOrDefault(file =>
                    string.Equals(file.Name, info.FileName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact.FullName;
            }

            if (!string.IsNullOrWhiteSpace(info.InstallerType))
            {
                var preferredExtension = InstallerTypeToExtension(info.InstallerType);
                var byType = files
                    .Where(file => string.Equals(file.Extension, preferredExtension, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(file => file.Length)
                    .ThenByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (byType != null) return byType.FullName;
            }

            return files
                .OrderByDescending(file => file.Length)
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .First()
                .FullName;
        }

        private static bool IsInstallerFile(string path)
        {
            var extension = Path.GetExtension(path);
            switch (extension.ToLowerInvariant())
            {
                case ".exe":
                case ".msi":
                case ".msix":
                case ".appx":
                case ".msixbundle":
                case ".appxbundle":
                case ".zip":
                case ".7z":
                case ".rar":
                    return true;
                default:
                    return false;
            }
        }

        private static async Task<string> ResolveWingetAsync(CancellationToken token)
        {
            lock (WingetPathLock)
            {
                if (!string.IsNullOrWhiteSpace(CachedWingetPath))
                    return CachedWingetPath;
            }

            var candidates = new List<string> { "winget" };

            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
                candidates.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe"));

            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (string.IsNullOrWhiteSpace(programFiles))
                programFiles = "C:\\Program Files";

            var windowsApps = Path.Combine(programFiles, "WindowsApps");
            try
            {
                if (Directory.Exists(windowsApps))
                {
                    var packages = Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe")
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);

                    foreach (var package in packages)
                        candidates.Add(Path.Combine(package, "winget.exe"));
                }
            }
            catch
            {
                // The directory may be protected; the PATH/AppData candidates are enough.
            }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var result = await RunProcessAsync(candidate,
                        new[] { "--version" },
                        token,
                        TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                    if (result.ExitCode == 0)
                    {
                        lock (WingetPathLock)
                            CachedWingetPath = candidate;
                        return candidate;
                    }
                }
                catch
                {
                    // Try the next candidate.
                }
            }

            throw new InvalidOperationException(
                "WinGet не найден. Установите или восстановите App Installer (winget) в Windows.");
        }

        private static async Task TryRefreshWingetSourceAsync(string winget, CancellationToken token)
        {
            try
            {
                await RunProcessAsync(
                    winget,
                    new[] { "source", "update", "--name", "winget", "--disable-interactivity" },
                    token,
                    TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch
            {
                // The subsequent retry will return the original WinGet error.
            }
        }

        private static void ValidateDefinition(AppDefinition app)
        {
            if (app == null || app.Download == null)
                throw new InvalidOperationException("Для WinGet не настроены данные загрузки.");

            if (string.IsNullOrWhiteSpace(app.Download.PackageId))
                throw new InvalidOperationException("Для WinGet не указан PackageId.");
            if (!PackageIdRegex.IsMatch(app.Download.PackageId.Trim()))
                throw new InvalidOperationException("Некорректный PackageId WinGet: " + app.Download.PackageId);
        }

        private static string MapArchitecture(string architecture)
        {
            if (string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
            if (string.Equals(architecture, "arm", StringComparison.OrdinalIgnoreCase)) return "arm";
            if (string.Equals(architecture, "x86", StringComparison.OrdinalIgnoreCase)) return "x86";
            return "x64";
        }

        private static int CompareVersionStrings(string left, string right)
        {
            var leftCore = ExtractNumericParts(left);
            var rightCore = ExtractNumericParts(right);
            var count = Math.Max(leftCore.Count, rightCore.Count);

            for (var i = 0; i < count; i++)
            {
                var l = i < leftCore.Count ? leftCore[i] : 0;
                var r = i < rightCore.Count ? rightCore[i] : 0;
                if (l != r) return l.CompareTo(r);
            }

            var leftSuffix = GetVersionSuffix(left);
            var rightSuffix = GetVersionSuffix(right);
            if (string.Equals(leftSuffix, rightSuffix, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.IsNullOrEmpty(leftSuffix)) return 1;
            if (string.IsNullOrEmpty(rightSuffix)) return -1;
            return string.Compare(leftSuffix, rightSuffix, StringComparison.OrdinalIgnoreCase);
        }

        private static List<int> ExtractNumericParts(string value)
        {
            var match = VersionLineRegex.Match(value?.Trim() ?? string.Empty);
            if (!match.Success) return new List<int>();

            var numeric = match.Groups[1].Value;
            var separator = numeric.IndexOfAny(new[] { '-', '+' });
            if (separator >= 0) numeric = numeric.Substring(0, separator);

            return numeric.Split('.')
                .Select(part => int.TryParse(part, out var number) ? number : 0)
                .ToList();
        }

        private static string GetVersionSuffix(string value)
        {
            var clean = value?.TrimStart('v', 'V') ?? string.Empty;
            var index = clean.IndexOfAny(new[] { '-', '+' });
            return index >= 0 ? clean.Substring(index + 1) : string.Empty;
        }

        private static string FormatCommandError(ProcessResult result)
        {
            var error = (result.StandardError ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(error))
                error = (result.StandardOutput ?? string.Empty).Trim();

            if (error.Length > 400)
                error = error.Substring(Math.Max(0, error.Length - 400));

            return string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error;
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string fileName,
            IEnumerable<string> arguments,
            CancellationToken token,
            TimeSpan timeout,
            Action<string> outputLine = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var argument in arguments ?? Enumerable.Empty<string>())
                psi.ArgumentList.Add(argument);

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeoutCts.CancelAfter(timeout);

                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException("Не удалось запустить WinGet.");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Не удалось запустить WinGet.", ex);
                }

                var stdout = new List<string>();
                var stderr = new List<string>();

                var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, outputLine, timeoutCts.Token);
                var stderrTask = ReadLinesAsync(process.StandardError, stderr, null, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(true);
                    }
                    catch { }

                    if (token.IsCancellationRequested)
                        throw;

                    throw new TimeoutException("WinGet не завершился за отведённое время.");
                }

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = string.Join(Environment.NewLine, stdout),
                    StandardError = string.Join(Environment.NewLine, stderr)
                };
            }
        }

        private static async Task ReadLinesAsync(
            StreamReader reader,
            List<string> target,
            Action<string> onLine,
            CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null) break;

                target.Add(line);
                onLine?.Invoke(line);

                if (target.Count > 5000)
                    target.RemoveAt(0);
            }
        }

        private static string SanitizePathPart(string value)
        {
            var result = SanitizeFilePart(value);
            return string.IsNullOrWhiteSpace(result) ? "package" : result;
        }

        private static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            return value.Trim();
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }
    }
}
