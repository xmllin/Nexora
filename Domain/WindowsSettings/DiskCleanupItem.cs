using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nexora.Domain.WindowsSettings
{
    public sealed class DiskCleanupItem : INotifyPropertyChanged
    {
        private long _sizeBytes;
        private long _fileCount;
        private bool _isSelected = false;
        private bool _isScanning;
        private bool _isCleaning;
        private string _status = "Не проверено";

        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string IconPath { get; init; } = "interface/white/package.svg";
        public bool IsCommand { get; init; }
        public bool IsDangerous { get; init; }

        public long SizeBytes
        {
            get => _sizeBytes;
            set { if (_sizeBytes == value) return; _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeText)); }
        }

        public long FileCount
        {
            get => _fileCount;
            set { if (_fileCount == value) return; _fileCount = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
        }

        public bool IsScanning
        {
            get => _isScanning;
            set { if (_isScanning == value) return; _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionVisible)); }
        }

        public bool IsCleaning
        {
            get => _isCleaning;
            set { if (_isCleaning == value) return; _isCleaning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionVisible)); }
        }

        public string Status
        {
            get => _status;
            set { if (_status == value) return; _status = value; OnPropertyChanged(); }
        }

        public bool HasData => SizeBytes > 0;
        public bool ActionVisible => HasData && !IsScanning && !IsCleaning;
        public string SizeText => FormatBytes(SizeBytes);

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return $"{bytes / 1024d / 1024d / 1024d:0.00} ГБ";
            if (bytes >= 1024L * 1024L)
                return $"{bytes / 1024d / 1024d:0.0} МБ";
            if (bytes >= 1024L)
                return $"{bytes / 1024d:0.0} КБ";
            return $"{Math.Max(0, bytes)} Б";
        }
    }
}
