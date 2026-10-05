using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Models;
using Nexora.Pages;
using Nexora.Infrastructure.HTTP;

namespace Nexora.Services.Downloads
{
    public class DownloadService
    {
        private static readonly HttpDownloadClient HttpDownloads = new HttpDownloadClient();
        private readonly Dictionary<string, IDownloadProvider> _providers;

        public DownloadService()
        {
            _providers = new Dictionary<string, IDownloadProvider>(StringComparer.OrdinalIgnoreCase)
            {
                { "Direct", new DirectDownloadProvider() },
                { "Official", new OfficialDownloadProvider() },
                { "Website", new OfficialPageDownloadProvider() },
                { "GitHub", new GitHubDownloadProvider() },
                { "Chromium", new ChromiumDownloadProvider() },
                { "WinGet", new WinGetDownloadProvider() }
            };
        }

        public async Task<DownloadInfo> ResolveAsync(AppDefinition app, CancellationToken token)
        {
            if (app == null || app.Download == null)
                throw new InvalidOperationException("Для приложения не настроен источник загрузки.");

            ValidateWindowsCompatibility(app.Download);
            if (!_providers.TryGetValue(app.Download.Type ?? string.Empty, out var provider))
                throw new InvalidOperationException("Неизвестный тип загрузки: " + app.Download.Type);

            try
            {
                return await provider.ResolveAsync(app, token);
            }
            catch (Exception ex)
            {
                DownloadLog.Error("Не удалось определить источник загрузки для " + app.Name + ".", ex);
                throw;
            }
        }

        private static void ValidateWindowsCompatibility(DownloadDefinition definition)
        {
            if (definition == null) return;
            var platform = PlatformDetectionService.Current;
            if (!platform.IsWindows)
                throw new InvalidOperationException("Эта загрузка предназначена для Windows.");
            if (definition.MinimumWindowsBuild.HasValue && platform.WindowsBuild > 0 &&
                platform.WindowsBuild < definition.MinimumWindowsBuild.Value)
                throw new InvalidOperationException("Эта версия программы требует более новую версию Windows.");
            if (definition.MaximumWindowsBuild.HasValue && platform.WindowsBuild > 0 &&
                platform.WindowsBuild > definition.MaximumWindowsBuild.Value)
                throw new InvalidOperationException("Эта версия программы не поддерживает текущую сборку Windows.");
        }

        public async Task<string> DownloadAsync(AppDefinition app, IProgress<DownloadProgress> progress, CancellationToken token, PauseController pauseController = null)
        {
            var info = await ResolveAsync(app, token);
            return await DownloadResolvedAsync(app, info, progress, token, pauseController);
        }

        public async Task<string> DownloadAsync(AppDefinition app, DownloadInfo info, IProgress<DownloadProgress> progress, CancellationToken token, PauseController pauseController = null)
        {
            if (app == null || info == null)
                throw new InvalidOperationException("Не выбран файл загрузки.");
            return await DownloadResolvedAsync(app, info, progress, token, pauseController);
        }

        private async Task<string> DownloadResolvedAsync(
            AppDefinition app, DownloadInfo info, IProgress<DownloadProgress> progress, CancellationToken token, PauseController pauseController)
        {
            if (IsManagedDownload(info))
            {
                var provider = ResolveManagedProvider(app.Download?.Type);
                if (provider == null)
                    throw new InvalidOperationException("Для типа загрузки «" + app.Download?.Type + "» не найден managed-провайдер.");

                var targetDir = DownloadSettings.GetFolder();
                Directory.CreateDirectory(targetDir);
                var target = GetUniquePath(Path.Combine(targetDir, SanitizeFileName(info.FileName)));

                try
                {
                    var result = await provider.DownloadAsync(app, info, target, progress, token);
                    await FileValidator.ValidateAsync(result, app.Download, token);
                    AddHistory(app, result, info.Source ?? app.Download.Type);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    TryDeleteFile(target);
                    throw;
                }
                catch (Exception ex)
                {
                    DownloadLog.Error("Ошибка managed-загрузки файла для " + app.Name + ".", ex);
                    TryDeleteFile(target);
                    throw;
                }
            }

            if (string.IsNullOrWhiteSpace(info.Url) ||
                !Uri.TryCreate(info.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("У выбранной загрузки отсутствует корректный HTTP/HTTPS URL.");

            var targetDirHttp = DownloadSettings.GetFolder();
            Directory.CreateDirectory(targetDirHttp);

            if (!HasRealExtension(info.FileName))
            {
                try
                {
                    var metadata = new DownloadMetadataService();
                    var remoteName = await metadata.GetFileNameAsync(info.Url, token);
                    if (!string.IsNullOrWhiteSpace(remoteName)) info.FileName = remoteName;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            var safeName = SanitizeFileName(info.FileName);
            var target = GetUniquePath(Path.Combine(targetDirHttp, safeName));
            var partial = target + ".part";
            var partialMeta = partial + ".json";
            var existingLength = PreparePartialFile(partial, partialMeta, info.Url);

            try
            {
                await HttpDownloads.DownloadResumableAsync(info.Url, partial, partialMeta, existingLength, info, app.Download, progress, token, pauseController);
                await FileValidator.ValidateAsync(partial, app.Download, token);
                File.Move(partial, target, true);
                TryDeleteFile(partialMeta);
                AddHistory(app, target, info.Source ?? app.Download.Type);
                return target;
            }
            catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Время ожидания загрузки истекло.", ex);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(partial);
                TryDeleteFile(partialMeta);
                TryDeleteFile(target);
                throw;
            }
            catch (Exception ex)
            {
                DownloadLog.Error("Ошибка загрузки файла для " + app.Name + ".", ex);
                throw;
            }
        }

        private static bool IsManagedDownload(DownloadInfo info)
        {
            return info != null && !string.IsNullOrWhiteSpace(info.PackageId);
        }

        private static IManagedDownloadProvider ResolveManagedProvider(string type)
        {
            if (string.Equals(type, "WinGet", StringComparison.OrdinalIgnoreCase))
                return new WinGetDownloadProvider();
            return null;
        }

        private static long PreparePartialFile(string partial, string metadataPath, string url)
        {
            try
            {
                if (!File.Exists(partial))
                {
                    TryDeleteFile(metadataPath);
                    return 0;
                }
                var storedUrl = File.Exists(metadataPath) ? File.ReadAllText(metadataPath) : string.Empty;
                if (!string.Equals(storedUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(partial);
                    TryDeleteFile(metadataPath);
                    return 0;
                }
                return new FileInfo(partial).Length;
            }
            catch
            {
                TryDeleteFile(partial);
                TryDeleteFile(metadataPath);
                return 0;
            }
        }

        private static bool HasRealExtension(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var ext = Path.GetExtension(value);
            return !string.IsNullOrWhiteSpace(ext) && ext.Length > 1;
        }

        private static void AddHistory(AppDefinition app, string path, string source)
        {
            var fileInfo = new FileInfo(path);
            DownloadHistoryService.Add(new DownloadRecord
            {
                Name = app.Name,
                FileName = fileInfo.Name,
                FullPath = path,
                Source = source,
                Status = "Завершено",
                SizeText = FormatSize(fileInfo.Length),
                DownloadedAt = DateTime.Now
            });

            if (MainWindow.Current != null && MainWindow.Current.MainContent.Content is DownloadsPage page)
                page.RefreshHistory();
        }

        private static string FormatSize(long bytes)
        {
            const double kb = 1024d;
            const double mb = kb * 1024d;
            const double gb = mb * 1024d;
            if (bytes >= gb) return string.Format("{0:0.0} ГБ", bytes / gb);
            if (bytes >= mb) return string.Format("{0:0.0} МБ", bytes / mb);
            if (bytes >= kb) return string.Format("{0:0.0} КБ", bytes / kb);
            return string.Format("{0} Б", bytes);
        }

        private static string GetUniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var i = 2;
            while (File.Exists(Path.Combine(dir, name + " (" + i + ")" + ext))) i++;
            return Path.Combine(dir, name + " (" + i + ")" + ext);
        }

        private static string SanitizeFileName(string name)
        {
            name = name ?? string.Empty;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
