using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Models;
using Nexora.Services.Downloads;

namespace Nexora.Infrastructure.HTTP
{
    public sealed class HttpDownloadClient
    {
        private static readonly HttpClient Client = CreateClient();

        public async Task<string> DownloadFileAsync(
            string url,
            string directory,
            string fileName,
            IProgress<DownloadProgress> progress,
            CancellationToken token,
            PauseController pauseController = null)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Sanitize(fileName));
            var resume = pauseController == null
                ? null
                : new ResumeContext { PauseController = pauseController };
            await DownloadCoreAsync(url, path, progress, token, false, resume).ConfigureAwait(false);
            return path;
        }

        public async Task DownloadResumableAsync(string url, string partial, string metadataPath, long existingLength, DownloadInfo info, DownloadDefinition definition, IProgress<DownloadProgress> progress, CancellationToken token, PauseController pauseController)
        {
            await DownloadCoreAsync(url, partial, progress, token, true, new ResumeContext
            {
                PartialPath = partial,
                MetadataPath = metadataPath,
                ExistingLength = existingLength,
                Info = info,
                Definition = definition,
                PauseController = pauseController
            }).ConfigureAwait(false);
        }

        private async Task DownloadCoreAsync(string url, string target, IProgress<DownloadProgress> progress, CancellationToken token, bool resumable, ResumeContext resume)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Источник загрузки должен использовать HTTP или HTTPS.");
            var existingLength = resume?.ExistingLength ?? 0;
            var resumed = resumable && existingLength > 0;
            var restartedAfterRangeFailure = false;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    if (resumed) request.Headers.Range = new RangeHeaderValue(existingLength, null);
                    HttpResponseMessage response = null;
                    try
                    {
                        response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        if (resumed && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            if (restartedAfterRangeFailure)
                                throw new HttpRequestException("Сервер не принимает запрос на продолжение загрузки.", null, response.StatusCode);
                            DeleteQuiet(resume?.PartialPath);
                            DeleteQuiet(resume?.MetadataPath);
                            existingLength = 0;
                            resumed = false;
                            restartedAfterRangeFailure = true;
                            continue;
                        }
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException("Сервер вернул HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
                        var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
                        if (resumed && !isPartial)
                        {
                            existingLength = 0;
                            resumed = false;
                            DeleteQuiet(resume?.PartialPath);
                            DeleteQuiet(resume?.MetadataPath);
                        }
                        if (IsHtmlResponse(response)) throw new InvalidOperationException("Сервер вернул веб-страницу вместо файла загрузки.");
                        var total = GetTotalLength(response, existingLength, isPartial);
                        CheckFreeSpace(total, existingLength, Path.GetDirectoryName(target));
                        if (resumable && resume != null) File.WriteAllText(resume.MetadataPath, url);

                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(target, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                        {
                            var received = resumed ? existingLength : 0;
                            var start = DateTime.UtcNow;
                            var buffer = new byte[128 * 1024];
                            if (!resumed)
                            {
                                var prefixBuffer = new byte[Math.Min(buffer.Length, 4096)];
                                var prefixRead = 0;
                                while (prefixRead < prefixBuffer.Length)
                                {
                                    if (resume?.PauseController != null) await resume.PauseController.WaitIfPausedAsync(token).ConfigureAwait(false);
                                    var readPrefix = await input.ReadAsync(prefixBuffer, prefixRead, prefixBuffer.Length - prefixRead, token).ConfigureAwait(false);
                                    if (readPrefix <= 0) break;
                                    prefixRead += readPrefix;
                                    if (prefixRead >= 512) break;
                                }
                                if (prefixRead <= 0) throw new InvalidOperationException("Сервер не передал содержимое файла.");
                                if (FileValidator.IsHtmlPrefix(prefixBuffer, prefixRead)) throw new InvalidOperationException("Сервер вернул веб-страницу вместо файла загрузки.");
                                if (resume?.Info != null) FileValidator.ValidatePrefix(resume.Info.FileName, prefixBuffer, prefixRead);
                                await output.WriteAsync(prefixBuffer, 0, prefixRead, token).ConfigureAwait(false);
                                received += prefixRead;
                                Report(progress, received, total, resume?.ExistingLength ?? 0, start);
                            }
                            int read;
                            while (true)
                            {
                                if (resume?.PauseController != null) await resume.PauseController.WaitIfPausedAsync(token).ConfigureAwait(false);
                                read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                                if (read <= 0) break;
                                await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                                received += read;
                                Report(progress, received, total, resume?.ExistingLength ?? 0, start);
                            }
                        }
                        return;
                    }
                    finally { response?.Dispose(); }
                }
            }
        }

        private static void Report(IProgress<DownloadProgress> progress, long received, long? total, long existingLength, DateTime start)
        {
            if (progress == null) return;
            var seconds = Math.Max(0.001, (DateTime.UtcNow - start).TotalSeconds);
            progress.Report(new DownloadProgress
            {
                BytesReceived = received,
                TotalBytes = total,
                Progress = total.HasValue && total.Value > 0 ? received * 100d / total.Value : -1,
                BytesPerSecond = Math.Max(0, (received - existingLength) / seconds)
            });
        }

        private static bool IsHtmlResponse(HttpResponseMessage response)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            return string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
        }

        private static long? GetTotalLength(HttpResponseMessage response, long existingLength, bool isPartial)
        {
            var range = response.Content.Headers.ContentRange;
            if (isPartial && range != null && range.Length.HasValue) return range.Length.Value;
            var length = response.Content.Headers.ContentLength;
            if (!length.HasValue || length.Value <= 0) return null;
            return isPartial ? existingLength + length.Value : length.Value;
        }

        private static void CheckFreeSpace(long? total, long existingLength, string targetDirectory)
        {
            if (!total.HasValue || total.Value <= 0 || string.IsNullOrWhiteSpace(targetDirectory)) return;
            try
            {
                var root = Path.GetPathRoot(targetDirectory);
                if (string.IsNullOrWhiteSpace(root)) return;
                var drive = new DriveInfo(root);
                const long safety = 16L * 1024L * 1024L;
                var need = Math.Max(0, total.Value - existingLength);
                if (drive.IsReady && drive.AvailableFreeSpace < need + safety) throw new IOException("Недостаточно места на диске для сохранения файла.");
            }
            catch (IOException) { throw; }
            catch { }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Nexora/1.1");
            return client;
        }

        private static string Sanitize(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "download" : value;
            foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value;
        }

        private static void DeleteQuiet(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class ResumeContext
        {
            public string PartialPath { get; set; }
            public string MetadataPath { get; set; }
            public long ExistingLength { get; set; }
            public DownloadInfo Info { get; set; }
            public DownloadDefinition Definition { get; set; }
            public PauseController PauseController { get; set; }
        }
    }
}
