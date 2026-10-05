using System;

namespace Nexora.Models
{
    public class DownloadInfo
    {
        public string Url { get; set; }
        public string FileName { get; set; }
        public string Source { get; set; }
        public string Version { get; set; }
        public long? SizeBytes { get; set; }
        public string PackageId { get; set; }
        public string PackageSource { get; set; }
        public string Architecture { get; set; }
        public string InstallerType { get; set; }

        public string Format
        {
            get
            {
                var candidate = FileName;
                if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(Url) &&
                    Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                    candidate = System.IO.Path.GetFileName(uri.AbsolutePath);

                if (string.IsNullOrWhiteSpace(candidate)) return "Не указан";
                var extension = System.IO.Path.GetExtension(candidate);
                return string.IsNullOrWhiteSpace(extension) ? "Файл" : "." + extension.TrimStart('.').ToUpperInvariant();
            }
        }
    }

    public class AppRelease
    {
        public string Version { get; set; }
        public string Title { get; set; }
        public DateTime? PublishedAt { get; set; }
        public DownloadInfo Download { get; set; }
        public bool IsPrerelease { get; set; }
        public string DisplayVersion { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayVersion) ? (Version ?? string.Empty) : DisplayVersion;
        }
    }
}
