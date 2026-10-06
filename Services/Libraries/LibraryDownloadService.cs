using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Infrastructure.HTTP;
using Nexora.Models;
using Nexora.Services.Downloads;

namespace Nexora.Services.Libraries
{
    public sealed class LibraryDownloadService
    {
        private readonly HttpDownloadClient _http;

        public LibraryDownloadService(HttpDownloadClient http = null)
        {
            _http = http ?? new HttpDownloadClient();
        }

        public async Task<string> DownloadAsync(LibraryDefinition definition, IProgress<DownloadProgress> progress, CancellationToken token, PauseController pauseController = null)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.DownloadUrl))
                throw new InvalidOperationException("Для компонента не задан URL загрузки.");
            if (!Uri.TryCreate(definition.DownloadUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Источник компонента должен использовать HTTP или HTTPS.");

            var folder = Path.Combine(Path.GetTempPath(), "Nexora", "Libraries");
            var name = string.IsNullOrWhiteSpace(definition.FileName) ? Path.GetFileName(uri.AbsolutePath) : definition.FileName;
            if (string.IsNullOrWhiteSpace(name)) name = definition.Id + ".download";
            var path = await _http.DownloadFileAsync(definition.DownloadUrl, folder, name, progress, token, pauseController).ConfigureAwait(false);
            await FileValidator.ValidateAsync(path, new DownloadDefinition { FileName = name }, token).ConfigureAwait(false);
            return path;
        }
    }
}
