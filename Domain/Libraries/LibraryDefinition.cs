using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nexora.Services.Downloads;
using System.Windows.Media;

namespace Nexora.Models
{
    public class LibraryDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public string Purpose { get; set; }
        public string ComponentType { get; set; }
        public List<string> Architectures { get; set; } = new List<string>();
        public List<string> RecommendationTags { get; set; } = new List<string>();
        public string Version { get; set; }
        public string VersionRule { get; set; }
        public string SourceUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string FileName { get; set; }
        public string SilentArguments { get; set; }
        public string InstallationType { get; set; }
        public bool CanInstallAutomatically { get; set; }
        public string DetectionType { get; set; }
        public string DetectionValue { get; set; }
        public string MinimumWindows { get; set; }
        public List<string> Requirements { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> UsedBy { get; set; } = new List<string>();

        [System.Text.Json.Serialization.JsonIgnore]
        public string ArchitectureText => Architectures == null || Architectures.Count == 0 ? "Не указано" : string.Join(", ", Architectures);
    }

    public enum LibraryInstallStatus
    {
        Missing,
        Installed,
        UpdateAvailable,
        Manual
    }

    public class LibraryItem : INotifyPropertyChanged
    {
        public LibraryDefinition Definition { get; private set; }
        public LibraryInstallStatus Status { get; set; }
        public string InstalledVersion { get; set; }
        public bool IsSelected { get; set; }
        public bool IsBusy { get; set; }
        public bool IsDeleting { get; set; }
        public bool IsRecommended { get; set; }
        public CancellationTokenSource DownloadCancellation { get; set; }
        public PauseController DownloadPauseController { get; set; }
        public bool IsPaused => DownloadPauseController != null && DownloadPauseController.IsPaused;
        public bool CanPauseDownload => IsBusy && ShowProgress && DownloadCancellation != null && !IsPaused;
        public bool CanResumeDownload => IsBusy && ShowProgress && DownloadCancellation != null && IsPaused;
        public bool IsWindowsFeature => string.Equals(Definition?.InstallationType, "windowsFeature", System.StringComparison.OrdinalIgnoreCase);
        public bool HasDownloadAction => !string.IsNullOrWhiteSpace(Definition?.DownloadUrl) || IsWindowsFeature;
        public string DownloadActionText => IsWindowsFeature ? "Включить" : "Скачать";
        public string RemoveActionText => IsWindowsFeature ? "Отключить" : "Удалить";
        public string DownloadedFilePath { get; set; }
        public bool HasDownloadedFile => !string.IsNullOrWhiteSpace(DownloadedFilePath) && System.IO.File.Exists(DownloadedFilePath);
        public bool ShowProgress { get; set; }
        public double ProgressOpacity { get; set; } = 1;
        public double Progress { get; set; }
        public string ProgressText { get; set; }
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case LibraryInstallStatus.Installed: return "Установлено";
                    case LibraryInstallStatus.UpdateAvailable: return "Доступно обновление";
                    case LibraryInstallStatus.Manual: return "Не установлено";
                    default: return "Не установлено";
                }
            }
        }
        public Brush StatusBrush
        {
            get
            {
                switch (Status)
                {
                    case LibraryInstallStatus.Installed:
                        return new SolidColorBrush(Color.FromRgb(97, 214, 128));
                    case LibraryInstallStatus.UpdateAvailable:
                        return new SolidColorBrush(Color.FromRgb(255, 196, 90));
                    case LibraryInstallStatus.Manual:
                        return new SolidColorBrush(Color.FromRgb(244, 92, 92));
                    default:
                        return new SolidColorBrush(Color.FromRgb(244, 92, 92));
                }
            }
        }
        public string DetailsText => "Подробнее";
        public bool IsInstalled => Status == LibraryInstallStatus.Installed;
        public bool IsMissing => Status == LibraryInstallStatus.Missing;

        public LibraryItem(LibraryDefinition definition, LibraryInstallStatus status, string installedVersion)
        {
            Definition = definition;
            Status = status;
            InstalledVersion = installedVersion;
            ProgressText = string.Empty;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        public void Notify(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
