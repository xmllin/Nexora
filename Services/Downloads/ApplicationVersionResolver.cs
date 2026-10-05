using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Models;

namespace Nexora.Services.Downloads
{
    /// <summary>
    /// Finds an application's latest real version without treating arbitrary
    /// numeric values in URLs (for example CDN timestamps) as a version.
    /// WinGet is preferred because it maintains package metadata; the already
    /// resolved official download remains the fallback.
    /// </summary>
    public sealed class ApplicationVersionResolver
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

        private static readonly Regex VersionFieldRegex = new Regex(
            @"(?im)^\s*(?:Version|Версия)\s*[:=]\s*(?<version>v?[0-9]+(?:\.[0-9]+){0,15}(?:[-+][0-9A-Za-z.-]+)?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public async Task<string> ResolveAsync(AppDefinition app, DownloadInfo info, CancellationToken token)
        {
            if (app == null)
                return string.Empty;

            var packageVersion = await TryResolveWinGetAsync(app, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(packageVersion))
                return packageVersion;

            return VersionNormalizer.ExtractMostSpecific(
                info?.Version,
                info?.FileName,
                info?.Url);
        }

        private static async Task<string> TryResolveWinGetAsync(AppDefinition app, CancellationToken token)
        {
            if (!IsWindows())
                return string.Empty;

            if (string.IsNullOrWhiteSpace(app.Download?.PackageId))
                return await TryShowByNameAsync(app.Name, token).ConfigureAwait(false);

            return await TryShowByIdAsync(app.Download.PackageId, token).ConfigureAwait(false);
        }

        private static async Task<string> TryShowByIdAsync(string packageId, CancellationToken token)
        {
            var result = await RunWingetAsync(
                new[] { "show", "--id", packageId.Trim(), "--exact", "--source", "winget", "--locale", "en-US", "--accept-source-agreements", "--disable-interactivity" },
                token).ConfigureAwait(false);

            return result.ExitCode == 0
                ? ExtractVersion(result.StandardOutput)
                : string.Empty;
        }

        private static async Task<string> TryShowByNameAsync(string name, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var exact = await RunWingetAsync(
                new[] { "show", "--name", name.Trim(), "--exact", "--source", "winget", "--locale", "en-US", "--accept-source-agreements", "--disable-interactivity" },
                token).ConfigureAwait(false);

            if (exact.ExitCode == 0)
            {
                var version = ExtractVersion(exact.StandardOutput);
                if (!string.IsNullOrWhiteSpace(version))
                    return version;
            }

            var fuzzy = await RunWingetAsync(
                new[] { "show", "--name", name.Trim(), "--source", "winget", "--locale", "en-US", "--accept-source-agreements", "--disable-interactivity" },
                token).ConfigureAwait(false);

            return fuzzy.ExitCode == 0 ? ExtractVersion(fuzzy.StandardOutput) : string.Empty;
        }

        private static string ExtractVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            var field = VersionFieldRegex.Match(output);
            if (field.Success)
                return VersionNormalizer.Normalize(field.Groups["version"].Value);

            return VersionNormalizer.ExtractMostSpecific(output);
        }

        private static async Task<ProcessResult> RunWingetAsync(string[] arguments, CancellationToken token)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                foreach (var argument in arguments ?? Array.Empty<string>())
                    process.StartInfo.ArgumentList.Add(argument);

                try
                {
                    if (!process.Start())
                        return new ProcessResult();

                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeoutCts.CancelAfter(CommandTimeout);

                        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

                        try
                        {
                            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                            return new ProcessResult
                            {
                                ExitCode = process.ExitCode,
                                StandardOutput = await stdoutTask.ConfigureAwait(false),
                                StandardError = await stderrTask.ConfigureAwait(false)
                            };
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

                            return new ProcessResult { ExitCode = -1 };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return new ProcessResult { ExitCode = -1 };
                }
            }
        }

        private static bool IsWindows()
        {
            return OperatingSystem.IsWindows();
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }
    }
}
