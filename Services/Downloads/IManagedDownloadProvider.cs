using System.Threading;
using System.Threading.Tasks;
using Nexora.Models;

namespace Nexora.Services.Downloads
{
    /// <summary>
    /// A provider that resolves a release normally but uses its own download
    /// transport instead of the HTTP downloader (for example WinGet).
    /// </summary>
    public interface IManagedDownloadProvider
    {
        Task<string> DownloadAsync(
            AppDefinition app,
            DownloadInfo info,
            string destinationPath,
            IProgress<DownloadProgress> progress,
            CancellationToken token);
    }
}
