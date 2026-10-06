using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexora.Pages;
using Nexora.Models;
using Nexora.Services;
using Nexora.Application.Navigation;
using Nexora.Services.Downloads;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Threading;
using System.Linq;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace Nexora
{
    public partial class MainWindow : Window
    {
        public static MainWindow Current { get; private set; }
        private bool _wallpaperFullscreen;
        private bool _searchPlaceholderChanging;
        private int _searchChangeVersion;
        private readonly System.Threading.Timer _clockTimer;
        private readonly Dictionary<string, UserControl> _pageCache = new Dictionary<string, UserControl>();
        private readonly Dictionary<string, IReadOnlyList<AppRelease>> _releaseCache = new Dictionary<string, IReadOnlyList<AppRelease>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _releaseCacheUpdated = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DownloadInfo> _downloadInfoCache = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DownloadInfo> _appDownloadInfoCache = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ReleaseUiCacheLifetime = TimeSpan.FromDays(3650);
        private readonly Dictionary<string, ActiveDownloadEntry> _activeDownloads = new Dictionary<string, ActiveDownloadEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _downloadToastTimer;
        private readonly Queue<NotificationEntry> _downloadToastQueue = new Queue<NotificationEntry>();
        private readonly HashSet<string> _queuedNotificationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _notificationCooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private string _visibleNotificationKey;
        private bool _downloadToastVisible;
        private bool _downloadToastPause;
        private readonly List<string> _downloadBatchNames = new List<string>();
        private int _downloadBatchExpected;
        private int _downloadBatchStarted;
        private bool _downloadBatchActive;
        private bool _downloadBatchEndRequested;
        private bool _batchStartNotificationVisible;
        private bool _isShuttingDown;
        private int _downloadBatchTerminal;
        private readonly DispatcherTimer _downloadBatchTimer;
        private readonly NavigationService _navigation = new NavigationService();

        private sealed class NotificationEntry
        {
            public string Key { get; set; }
            public string Message { get; set; }
            public NotificationKind Kind { get; set; }
        }
        private const int DownloadCacheSchemaVersion = 9;
        private static readonly string CacheFile = UserDataPath.File("download_metadata_cache.json");
        private string _previousPage = "home";
        private string _currentPage = "home";
        private string _appDetailsOrigin = "home";
        public ObservableCollection<ActiveDownloadEntry> ActiveDownloadsList { get; } = new ObservableCollection<ActiveDownloadEntry>();

        public WallpaperItem SelectedWallpaper { get; set; }
        public AppDefinition SelectedApp { get; set; }

        public bool TryGetCachedReleases(AppDefinition app, out IReadOnlyList<AppRelease> releases)
        {
            var key = GetReleaseCacheKey(app);
            if (_releaseCache.TryGetValue(key, out releases) && releases != null && releases.Count > 0)
            {
                var sanitized = SanitizeReleaseHistory(app, releases.ToList());
                if (sanitized.Count != releases.Count)
                {
                    _releaseCache[key] = sanitized;
                    releases = sanitized;
                    SaveDownloadCache();
                }
                else
                {
                    releases = sanitized;
                }
                return releases.Count > 0;
            }

            releases = null;
            return false;
        }

        public void CacheReleases(AppDefinition app, IReadOnlyList<AppRelease> releases)
        {
            var key = GetReleaseCacheKey(app);
            var incoming = (releases ?? new List<AppRelease>())
                .Where(item => item != null && item.Download != null)
                .ToList();

            if (!_releaseCache.TryGetValue(key, out var existing) || existing == null)
            {
                _releaseCache[key] = SanitizeReleaseHistory(app, incoming);
            }
            else
            {
                var merged = SanitizeReleaseHistory(app, existing.ToList());

                // When an application switches to WinGet as its authoritative
                // source, discard stale Website/Direct entries from the old catalog.
                if (string.Equals(app?.Download?.Type, "WinGet", StringComparison.OrdinalIgnoreCase))
                {
                    merged = merged
                        .Where(item => string.Equals(item?.Download?.Source, "WinGet", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                foreach (var fresh in incoming)
                {
                    var identity = GetReleaseIdentity(fresh);
                    var index = merged.FindIndex(item => string.Equals(GetReleaseIdentity(item), identity, StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                    {
                        merged.Add(fresh);
                    }
                    else
                    {
                        merged[index] = MergeRelease(merged[index], fresh);
                    }
                }

                _releaseCache[key] = SanitizeReleaseHistory(app, merged);
            }

            _releaseCacheUpdated[key] = DateTime.UtcNow;
            SaveDownloadCache();
        }

        private static List<AppRelease> SanitizeReleaseHistory(AppDefinition app, IEnumerable<AppRelease> releases)
        {
            var result = (releases ?? Enumerable.Empty<AppRelease>())
                .Where(item => item != null && item.Download != null)
                .ToList();

            var id = (app?.Id ?? string.Empty).Trim().ToLowerInvariant();

            if (string.Equals(app?.Download?.Type, "WinGet", StringComparison.OrdinalIgnoreCase))
            {
                result = result
                    .Where(item => string.Equals(item?.Download?.Source, "WinGet", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (id == "firefox")
            {
                result = result
                    .Where(item => !VersionInfo.Parse(item.Version).IsValid ||
                                   VersionInfo.Parse(item.Version).CompareTo(VersionInfo.Parse("42.0")) >= 0)
                    .ToList();
            }
            else if (id == "opera")
            {
                result = result
                    .Where(item => !VersionInfo.Parse(item.Version).IsValid ||
                                   VersionInfo.Parse(item.Version).CompareTo(VersionInfo.Parse("42.0.2393.85")) >= 0)
                    .ToList();
            }
            else if (id == "opera-gx")
            {
                result = result
                    .Where(item => !VersionInfo.Parse(item.Version).IsValid ||
                                   VersionInfo.Parse(item.Version).CompareTo(VersionInfo.Parse("42.0.2393.85")) >= 0)
                    .ToList();
            }
            else if (id == "telegram")
            {
                // Old parser could read the x64 architecture marker as the first
                // version component (e.g. 64.7.2.5). Such entries are invalid.
                result = result
                    .Where(item => !RegexStartsWith64Version(item?.Version))
                    .ToList();
            }
            else if (id == "chromium")
            {
                // Old cache entries exposed raw snapshot revisions as menu items.
                // They should never be shown to users.
                result = result
                    .Where(item => !string.Equals(item?.DisplayVersion, "Revision " + item?.Version, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var item in result)
                {
                    if (!string.IsNullOrWhiteSpace(item.DisplayVersion))
                    {
                        var markerIndex = item.DisplayVersion.IndexOf(" - rev ", StringComparison.OrdinalIgnoreCase);
                        if (markerIndex > 0)
                            item.DisplayVersion = item.DisplayVersion.Substring(0, markerIndex);
                    }
                }
            }
            else if (id == "aida64")
            {
                result = result
                    .Where(item => !string.Equals(item.Version, "840", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else if (id == "steam")
            {
                result = result
                    .Where(item => !string.Equals(item.Version, "2.10.91.91", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return result;
        }

        private static bool RegexStartsWith64Version(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.TrimStart('v', 'V');
            return trimmed.StartsWith("64.", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetReleaseIdentity(AppRelease release)
        {
            var version = VersionNormalizer.Normalize(release?.Version ?? string.Empty);
            var format = release?.Download?.Format ?? string.Empty;
            return version + "|" + format;
        }

        private static AppRelease MergeRelease(AppRelease oldRelease, AppRelease freshRelease)
        {
            if (oldRelease == null) return freshRelease;
            if (freshRelease == null) return oldRelease;

            var fresh = freshRelease.Download;
            var old = oldRelease.Download;
            if (fresh != null && old != null)
            {
                if (!fresh.SizeBytes.HasValue && old.SizeBytes.HasValue)
                    fresh.SizeBytes = old.SizeBytes;
                if (string.IsNullOrWhiteSpace(fresh.Url))
                    fresh.Url = old.Url;
                if (string.IsNullOrWhiteSpace(fresh.FileName))
                    fresh.FileName = old.FileName;
                if (string.IsNullOrWhiteSpace(fresh.Version))
                    fresh.Version = old.Version;
                if (string.IsNullOrWhiteSpace(fresh.Source))
                    fresh.Source = old.Source;
                if (string.IsNullOrWhiteSpace(fresh.PackageId))
                    fresh.PackageId = old.PackageId;
                if (string.IsNullOrWhiteSpace(fresh.PackageSource))
                    fresh.PackageSource = old.PackageSource;
                if (string.IsNullOrWhiteSpace(fresh.Architecture))
                    fresh.Architecture = old.Architecture;
                if (string.IsNullOrWhiteSpace(fresh.InstallerType))
                    fresh.InstallerType = old.InstallerType;
            }

            if (!freshRelease.PublishedAt.HasValue)
                freshRelease.PublishedAt = oldRelease.PublishedAt;
            if (string.IsNullOrWhiteSpace(freshRelease.Title))
                freshRelease.Title = oldRelease.Title;
            freshRelease.IsPrerelease = freshRelease.IsPrerelease || oldRelease.IsPrerelease;
            return freshRelease;
        }

        public bool TryGetCachedDownloadInfo(string url, out DownloadInfo info)
        {
            if (_downloadInfoCache.TryGetValue(url ?? string.Empty, out info))
                return info != null;
            return false;
        }

        public void CacheDownloadInfo(DownloadInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info?.Url))
            {
                _downloadInfoCache[info.Url] = info;
                SaveDownloadCache();
            }
        }

        public bool TryGetCachedAppDownloadInfo(AppDefinition app, out DownloadInfo info)
        {
            var key = GetAppDownloadCacheKey(app);
            if (_appDownloadInfoCache.TryGetValue(key, out info) && info != null)
                return true;
            info = null;
            return false;
        }

        public void CacheAppDownloadInfo(AppDefinition app, DownloadInfo info)
        {
            var key = GetAppDownloadCacheKey(app);
            if (string.IsNullOrWhiteSpace(key) || info == null)
                return;
            _appDownloadInfoCache[key] = info;
            SaveDownloadCache();
        }

        private static string GetAppDownloadCacheKey(AppDefinition app)
        {
            if (app == null) return string.Empty;
            var platform = PlatformDetectionService.Current;
            return (app.Id ?? app.Name ?? string.Empty) + "|" + platform.Architecture + "|" + platform.ProcessArchitecture + "|" + platform.WindowsBuild;
        }

        private sealed class DownloadCacheState
        {
            public int SchemaVersion { get; set; }
            public Dictionary<string, List<AppRelease>> Releases { get; set; } = new Dictionary<string, List<AppRelease>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, DateTime> ReleaseUpdated { get; set; } = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, DownloadInfo> Downloads { get; set; } = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, DownloadInfo> AppDownloads { get; set; } = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
        }

        private void LoadDownloadCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                var state = JsonSerializer.Deserialize<DownloadCacheState>(File.ReadAllText(CacheFile));
                if (state == null || state.SchemaVersion != DownloadCacheSchemaVersion) return;
                var updatedMap = state.ReleaseUpdated ?? new Dictionary<string, DateTime>();
                foreach (var item in state.Releases ?? new Dictionary<string, List<AppRelease>>())
                {
                    _releaseCache[item.Key] = item.Value ?? new List<AppRelease>();
                    DateTime updated;
                    if (updatedMap.TryGetValue(item.Key, out updated))
                        _releaseCacheUpdated[item.Key] = updated;
                }
                foreach (var item in state.Downloads ?? new Dictionary<string, DownloadInfo>())
                    _downloadInfoCache[item.Key] = item.Value;
                foreach (var item in state.AppDownloads ?? new Dictionary<string, DownloadInfo>())
                    _appDownloadInfoCache[item.Key] = item.Value;
            }
            catch { }
        }

        private void SaveDownloadCache()
        {
            try
            {
                var directory = Path.GetDirectoryName(CacheFile);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var state = new DownloadCacheState
                {
                    SchemaVersion = DownloadCacheSchemaVersion,
                    Releases = _releaseCache.ToDictionary(item => item.Key, item => item.Value.ToList(), StringComparer.OrdinalIgnoreCase),
                    ReleaseUpdated = new Dictionary<string, DateTime>(_releaseCacheUpdated, StringComparer.OrdinalIgnoreCase),
                    Downloads = new Dictionary<string, DownloadInfo>(_downloadInfoCache, StringComparer.OrdinalIgnoreCase),
                    AppDownloads = new Dictionary<string, DownloadInfo>(_appDownloadInfoCache, StringComparer.OrdinalIgnoreCase)
                };
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static string GetReleaseCacheKey(AppDefinition app)
        {
            var platform = PlatformDetectionService.Current;
            return (app?.Id ?? app?.Name ?? string.Empty) + "|" + platform.Architecture + "|" +
                   platform.ProcessArchitecture + "|" + platform.WindowsBuild;
        }
        
        public string GetPreviousPage() => _previousPage;

        public string GetAppDetailsOrigin() => _appDetailsOrigin;

        public void NavigateToAppDetails(string origin)
        {
            _appDetailsOrigin = string.IsNullOrWhiteSpace(origin) ? "home" : origin;
            Navigate("appdetail");
        }

        public WallpaperPage GetWallpaperPage()
        {
            return GetOrCreatePage("wallpapers") as WallpaperPage;
        }

        public string GetSearchText() => "";

        public sealed class ActiveDownloadEntry : IDisposable, INotifyPropertyChanged
        {
            private string _label;
            private double _progressValue;
            private string _downloadedText = "0 Б";
            private string _speedText = "0 Б/с";
            private bool _isPaused;
            private bool _hasStarted;
            private bool _terminalNotificationShown;
            private long _lastSpeedBytes;
            private DateTime _lastSpeedTimestamp = DateTime.MinValue;

            public string Key { get; set; }
            public string Label { get => _label; set { if (_label != value) { _label = value; OnPropertyChanged(nameof(Label)); } } }
            public double ProgressValue { get => _progressValue; set { if (Math.Abs(_progressValue - value) > 0.0001) { _progressValue = value; OnPropertyChanged(nameof(ProgressValue)); } } }
            public string DownloadedText { get => _downloadedText; set { if (_downloadedText != value) { _downloadedText = value; OnPropertyChanged(nameof(DownloadedText)); } } }
            public string SpeedText { get => _speedText; set { if (_speedText != value) { _speedText = value; OnPropertyChanged(nameof(SpeedText)); } } }
            public bool IsPaused { get => _isPaused; set { if (_isPaused != value) { _isPaused = value; OnPropertyChanged(nameof(IsPaused)); } } }
            public bool HasStarted { get => _hasStarted; set { if (_hasStarted != value) { _hasStarted = value; OnPropertyChanged(nameof(HasStarted)); } } }
            public bool TerminalNotificationShown { get => _terminalNotificationShown; set { if (_terminalNotificationShown != value) { _terminalNotificationShown = value; OnPropertyChanged(nameof(TerminalNotificationShown)); } } }
            public long LastSpeedBytes { get => _lastSpeedBytes; set => _lastSpeedBytes = value; }
            public DateTime LastSpeedTimestamp { get => _lastSpeedTimestamp; set => _lastSpeedTimestamp = value; }
            public CancellationTokenSource CancellationToken { get; set; }
            public PauseController PauseController { get; set; }
            public DispatcherTimer HideTimer { get; set; }
            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            public void Dispose()
            {
                try { PauseController?.Dispose(); } catch { }
                try { CancellationToken?.Dispose(); } catch { }
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            Current = this;
            LoadDownloadCache();
            ApplyWindowChrome(false);
            StateChanged += (_, __) => UpdateMaximizeButton();
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            UpdateMaximizeButton();
            VersionText.Text = "Версия " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            _clockTimer = new System.Threading.Timer(UpdateClock, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            _downloadToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _downloadToastTimer.Tick += DownloadToastTimer_Tick;
            _downloadBatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _downloadBatchTimer.Tick += DownloadBatchTimer_Tick;
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
            ConfigureNavigation();
            GetOrCreatePage("wallpapers");
            Navigate("home");
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // The window is leaving, so do not enqueue toast notifications that
            // would try to render after the visual tree starts shutting down.
            _isShuttingDown = true;
            CancelAllDownloads(false);
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _clockTimer.Dispose();
            _downloadToastTimer.Stop();
            _downloadBatchTimer.Stop();
            foreach (var entry in _activeDownloads.Values.ToList())
                entry.Dispose();
            _activeDownloads.Clear();
        }

        public void ShowDownloadToast(string message)
        {
            ShowNotification(message, NotificationKind.Info);
        }

        public void ShowNotification(string message, NotificationKind kind = NotificationKind.Info, string dedupKey = null)
        {
            if (_isShuttingDown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (string.IsNullOrWhiteSpace(message)) return;

            dedupKey = string.IsNullOrWhiteSpace(dedupKey)
                ? NormalizeNotificationKey(message)
                : NormalizeNotificationKey(dedupKey);

            if (dedupKey.StartsWith("download-start:", StringComparison.OrdinalIgnoreCase) && _batchStartNotificationVisible)
                return;

            var now = DateTime.UtcNow;
            if (_notificationCooldowns.TryGetValue(dedupKey, out var lastShown) && now - lastShown < TimeSpan.FromSeconds(4))
                return;

            if (_visibleNotificationKey == dedupKey || _queuedNotificationKeys.Contains(dedupKey))
                return;

            _notificationCooldowns[dedupKey] = now;
            TrimNotificationCooldowns(now);

            var entry = new NotificationEntry
            {
                Key = dedupKey,
                Message = message,
                Kind = kind
            };

            if (_downloadToastVisible || _downloadToastPause)
            {
                _downloadToastQueue.Enqueue(entry);
                _queuedNotificationKeys.Add(dedupKey);
                return;
            }

            DisplayNotification(entry);
        }

        private static string NormalizeNotificationKey(string value)
        {
            var chars = (value ?? string.Empty).Trim().ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsWhiteSpace(chars[i])) chars[i] = ' ';
            }
            return new string(chars);
        }

        private void TrimNotificationCooldowns(DateTime now)
        {
            var expired = _notificationCooldowns
                .Where(x => now - x.Value > TimeSpan.FromMinutes(1))
                .Select(x => x.Key)
                .ToList();
            foreach (var key in expired)
                _notificationCooldowns.Remove(key);
        }

        private void DisplayNotification(NotificationEntry entry)
        {
            _visibleNotificationKey = entry.Key;
            DownloadToastText.Text = entry.Message;

            var borderBrush = entry.Kind == NotificationKind.Error ? "#D34F5F"
                : entry.Kind == NotificationKind.Warning ? "#D99B35"
                : entry.Kind == NotificationKind.Success ? "#4DBE78"
                : "#3D8FE8";
            var iconBackground = entry.Kind == NotificationKind.Error ? "#9E2738"
                : entry.Kind == NotificationKind.Warning ? "#9A6A22"
                : entry.Kind == NotificationKind.Success ? "#247A49"
                : "#176BE0";
            var iconPath = entry.Kind == NotificationKind.Error
                ? "interface/white/exclamation-circle.svg"
                : entry.Kind == NotificationKind.Warning
                    ? "interface/white/exclamation-circle.svg"
                    : entry.Kind == NotificationKind.Success
                        ? "interface/white/category-check.svg"
                        : "interface/white/fluent-arrow-download.svg";

            DownloadToast.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderBrush));
            DownloadToastIconHost.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconBackground));
            DownloadToastIcon.Source = SvgImageLoader.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, iconPath.Replace('/', Path.DirectorySeparatorChar)));
            DownloadToast.Visibility = Visibility.Visible;
            DownloadToast.Opacity = 1;
            _downloadToastVisible = true;
            _downloadToastTimer.Stop();
            _downloadToastTimer.Interval = TimeSpan.FromSeconds(3.5);
            _downloadToastTimer.Start();
        }

        private void DownloadToastTimer_Tick(object sender, EventArgs e)
        {
            if (_downloadToastPause)
            {
                _downloadToastPause = false;
                if (_downloadToastQueue.Count > 0)
                {
                    var next = _downloadToastQueue.Dequeue();
                    _queuedNotificationKeys.Remove(next.Key);
                    DisplayNotification(next);
                }
                else
                {
                    _downloadToastTimer.Stop();
                }
                return;
            }

            _downloadToastTimer.Stop();
            DownloadToast.Visibility = Visibility.Collapsed;
            DownloadToast.Opacity = 0;
            _downloadToastVisible = false;
            _visibleNotificationKey = null;
            _batchStartNotificationVisible = false;

            if (_downloadToastQueue.Count > 0)
            {
                _downloadToastPause = true;
                _downloadToastTimer.Interval = TimeSpan.FromMilliseconds(300);
                _downloadToastTimer.Start();
            }
        }

        public bool TryRegisterDownload(string appName, string key, out CancellationTokenSource cancellationToken)
        {
            PauseController ignored;
            return TryRegisterDownload(appName, key, out cancellationToken, out ignored);
        }

        public bool TryRegisterDownload(string appName, string key, out CancellationTokenSource cancellationToken, out PauseController pauseController)
        {
            key = (string.IsNullOrWhiteSpace(key) ? appName : key).ToLowerInvariant();
            if (_activeDownloads.ContainsKey(key))
            {
                ShowNotification("Загрузка «" + appName + "» уже выполняется.", NotificationKind.Info, "download-active:" + appName);
                cancellationToken = null;
                pauseController = null;
                return false;
            }

            cancellationToken = new CancellationTokenSource();
            pauseController = new PauseController();
            var entry = new ActiveDownloadEntry
            {
                Key = key,
                Label = "Загрузка: " + appName,
                ProgressValue = 0,
                DownloadedText = "0 Б",
                SpeedText = "0 Б/с",
                CancellationToken = cancellationToken,
                PauseController = pauseController
            };
            _activeDownloads[key] = entry;
            ActiveDownloadsList.Add(entry);
            RefreshDownloadsPage();
            return true;
        }

        public void BeginDownloadBatch(int expectedCount = 0, IEnumerable<string> names = null)
        {
            _downloadBatchTimer.Stop();
            _downloadBatchNames.Clear();
            if (names != null)
            {
                foreach (var name in names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    _downloadBatchNames.Add(name);
            }
            _downloadBatchExpected = Math.Max(0, expectedCount);
            _downloadBatchStarted = _downloadBatchNames.Count;
            _downloadBatchEndRequested = false;
            _downloadBatchTerminal = 0;
            _downloadBatchActive = true;
        }

        public void EndDownloadBatch()
        {
            _downloadBatchEndRequested = true;
            if (_downloadBatchNames.Count >= _downloadBatchExpected && _downloadBatchExpected > 0)
            {
                FlushDownloadBatchNotification();
                return;
            }
            if (_downloadBatchStarted >= _downloadBatchExpected)
            {
                FlushDownloadBatchNotification();
                return;
            }
            _downloadBatchTimer.Stop();
            _downloadBatchTimer.Start();
        }

        private void DownloadBatchTimer_Tick(object sender, EventArgs e)
        {
            _downloadBatchTimer.Stop();
            if (_downloadBatchActive && _downloadBatchEndRequested)
                FlushDownloadBatchNotification();
        }

        private void FlushDownloadBatchNotification()
        {
            _downloadBatchActive = false;
            _downloadBatchEndRequested = false;
            if (_downloadBatchNames.Count == 0) return;
            var names = _downloadBatchNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var message = names.Count == 1
                ? "Загрузка «" + names[0] + "» начата."
                : "Начаты загрузки: " + string.Join(", ", names) + ".";
            _batchStartNotificationVisible = true;
            ShowNotification(message, NotificationKind.Info, "download-start-batch:" + string.Join("|", names.OrderBy(x => x)));
            _downloadBatchNames.Clear();
        }

        public void NotifyDownloadStarted(string key, string appName)
        {
            if (_downloadBatchActive)
            {
                if (!_downloadBatchNames.Contains(appName, StringComparer.OrdinalIgnoreCase))
                {
                    _downloadBatchNames.Add(appName);
                    _downloadBatchStarted++;
                }
                if (_downloadBatchStarted >= _downloadBatchExpected)
                {
                    _downloadBatchTimer.Stop();
                    if (_downloadBatchEndRequested) FlushDownloadBatchNotification();
                }
                else if (_downloadBatchEndRequested)
                {
                    _downloadBatchTimer.Stop();
                    _downloadBatchTimer.Start();
                }
                return;
            }
            ShowNotification("Загрузка «" + appName + "» начата.", NotificationKind.Info, "download-start:" + (key ?? appName));
        }

        public void NotifyDownloadCancelled(string key, string appName)
        {
            if (!string.IsNullOrWhiteSpace(key) && _activeDownloads.TryGetValue(key.ToLowerInvariant(), out var activeEntry))
            {
                if (activeEntry.TerminalNotificationShown) return;
                activeEntry.TerminalNotificationShown = true;
            }

            if (_downloadBatchActive && _downloadBatchEndRequested)
            {
                _downloadBatchTerminal++;
                if (_downloadBatchStarted + _downloadBatchTerminal >= _downloadBatchExpected)
                {
                    _downloadBatchTimer.Stop();
                    FlushDownloadBatchNotification();
                }
                else
                {
                    _downloadBatchTimer.Stop();
                    _downloadBatchTimer.Start();
                }
            }
            ShowNotification("Загрузка «" + appName + "» отменена.", NotificationKind.Warning, "download-cancel:" + (key ?? appName));
        }

        public void NotifyDownloadError(string key, string appName, Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(key) && _activeDownloads.TryGetValue(key.ToLowerInvariant(), out var activeEntry))
            {
                if (activeEntry.TerminalNotificationShown) return;
                activeEntry.TerminalNotificationShown = true;
            }

            if (_downloadBatchActive && _downloadBatchEndRequested)
            {
                _downloadBatchTerminal++;
                if (_downloadBatchStarted + _downloadBatchTerminal >= _downloadBatchExpected)
                {
                    _downloadBatchTimer.Stop();
                    FlushDownloadBatchNotification();
                }
                else
                {
                    _downloadBatchTimer.Stop();
                    _downloadBatchTimer.Start();
                }
            }
            ShowNotification("Не удалось скачать «" + appName + "»: " + NotificationFormatter.FormatDownloadError(exception), NotificationKind.Error, "download-error:" + (key ?? appName) + ":" + (exception?.GetType().FullName ?? "error"));
        }

        public bool TryCancelDownload(string key, bool notify = true)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            key = key.ToLowerInvariant();
            if (!_activeDownloads.TryGetValue(key, out var entry) || entry.CancellationToken == null) return false;
            try
            {
                if (!entry.CancellationToken.IsCancellationRequested)
                    entry.CancellationToken.Cancel();
                if (notify) NotifyDownloadCancelled(key, GetDownloadDisplayName(entry));
                return true;
            }
            catch (ObjectDisposedException) { return false; }
        }

        private static string GetDownloadDisplayName(ActiveDownloadEntry entry)
        {
            var label = entry?.Label ?? string.Empty;
            const string prefix = "Загрузка: ";
            return label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? label.Substring(prefix.Length)
                : (string.IsNullOrWhiteSpace(label) ? (entry?.Key ?? "файла") : label);
        }

        public bool TryTogglePauseDownload(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (!_activeDownloads.TryGetValue(key.ToLowerInvariant(), out var entry) || entry.PauseController == null) return false;
            if (entry.IsPaused)
            {
                entry.PauseController.Resume();
                entry.IsPaused = false;
                // A pause creates a gap in the timing window; start a fresh
                // speed sample after resuming instead of averaging the pause.
                entry.LastSpeedTimestamp = DateTime.UtcNow;
                entry.SpeedText = "0 Б/с";
            }
            else
            {
                entry.PauseController.Pause();
                entry.IsPaused = true;
                entry.SpeedText = "Пауза";
            }
            return true;
        }

        public void CancelAllDownloads(bool notify = true)
        {
            foreach (var key in _activeDownloads.Keys.ToList())
                TryCancelDownload(key, notify);
        }

        public void UpdateDownloadProgress(string key, DownloadProgress details, string appName = null)
        {
            if (details == null || !_activeDownloads.TryGetValue(key.ToLowerInvariant(), out var entry)) return;
            if (!string.IsNullOrWhiteSpace(appName)) entry.Label = "Загрузка: " + appName;
            if (details.Progress >= 0) entry.ProgressValue = Math.Max(0, Math.Min(100, details.Progress));
            entry.DownloadedText = details.TotalBytes.HasValue
                ? FormatSize(details.BytesReceived) + " / " + FormatSize(details.TotalBytes.Value)
                : FormatSize(details.BytesReceived);

            if (!entry.IsPaused)
            {
                var now = DateTime.UtcNow;
                if (entry.LastSpeedTimestamp == DateTime.MinValue)
                {
                    entry.LastSpeedBytes = details.BytesReceived;
                    entry.LastSpeedTimestamp = now;
                    entry.SpeedText = FormatSpeed(details.BytesPerSecond);
                }
                else
                {
                    var elapsed = (now - entry.LastSpeedTimestamp).TotalSeconds;
                    if (elapsed >= 0.25)
                    {
                        var delta = Math.Max(0, details.BytesReceived - entry.LastSpeedBytes);
                        var speed = delta / elapsed;
                        entry.SpeedText = FormatSpeed(speed > 0 ? speed : details.BytesPerSecond);
                        entry.LastSpeedBytes = details.BytesReceived;
                        entry.LastSpeedTimestamp = now;
                    }
                }
            }
            else
            {
                entry.SpeedText = "Пауза";
            }
            if (details.BytesReceived > 0 && !entry.HasStarted)
            {
                entry.HasStarted = true;
                NotifyDownloadStarted(key, !string.IsNullOrWhiteSpace(appName) ? appName : (appName ?? key));
            }
        }

        public void CompleteDownload(string key, string appName = null)
        {
            var displayName = string.IsNullOrWhiteSpace(appName) ? key : appName;
            RemoveDownload(key);
            ShowNotification("Загрузка «" + displayName + "» успешно завершена.", NotificationKind.Success, "download-success:" + key);
        }

        public void RemoveDownload(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            key = key.ToLowerInvariant();
            if (!_activeDownloads.TryGetValue(key, out var entry)) return;
            entry.HideTimer?.Stop();
            _activeDownloads.Remove(key);
            ActiveDownloadsList.Remove(entry);
            entry.Dispose();
            RefreshDownloadsPage();
        }

        private void RefreshDownloadsPage()
        {
            if (_pageCache.TryGetValue("downloads", out var page) && page is DownloadsPage dpage)
                dpage.RefreshActiveDownloads();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024L * 1024L) return (bytes / 1024d).ToString("0.0") + " КБ";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024d * 1024d)).ToString("0.0") + " МБ";
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.0") + " ГБ";
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024) return Math.Max(0, bytesPerSecond).ToString("0") + " Б/с";
            if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024d).ToString("0.0") + " КБ/с";
            if (bytesPerSecond < 1024 * 1024 * 1024) return (bytesPerSecond / (1024d * 1024d)).ToString("0.0") + " МБ/с";
            return (bytesPerSecond / (1024d * 1024d * 1024d)).ToString("0.0") + " ГБ/с";
        }

        private void CloseDownloadToast_Click(object sender, RoutedEventArgs e)
        {
            HideCurrentNotification(true);
        }

        private void HideCurrentNotification(bool advance)
        {
            var currentKey = _visibleNotificationKey;
            _downloadToastTimer.Stop();
            DownloadToast.Visibility = Visibility.Collapsed;
            DownloadToast.Opacity = 0;
            _downloadToastVisible = false;
            _visibleNotificationKey = null;
            if (!string.IsNullOrWhiteSpace(currentKey) && currentKey.StartsWith("download-start-batch:", StringComparison.OrdinalIgnoreCase))
                _batchStartNotificationVisible = false;
            if (advance && _downloadToastQueue.Count > 0)
            {
                var next = _downloadToastQueue.Dequeue();
                _queuedNotificationKeys.Remove(next.Key);
                DisplayNotification(next);
            }
            else if (!string.IsNullOrWhiteSpace(currentKey) && currentKey.StartsWith("download-start-batch:", StringComparison.OrdinalIgnoreCase))
            {
                _batchStartNotificationVisible = false;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // The search box lives inside the title bar. Never treat a click
            // inside it as a window-drag or as a request to clear the query.
            if (e.ClickCount > 1)
            {
                e.Handled = true;
                return;
            }

            if (e.ButtonState != MouseButtonState.Pressed) return;

            try
            {
                Cursor = Cursors.Hand;
                DragMove();
            }
            finally
            {
                Cursor = Cursors.Arrow;
            }
            e.Handled = true;
        }

        private void UpdateClock(object state)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            Dispatcher.BeginInvoke(new Action(() => ClockText.Text = time), DispatcherPriority.Background);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            UpdateMaximizeButton();
        }

        private void UpdateMaximizeButton()
        {
            if (MaximizeIcon == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                (maximized ? "interface/white/restore_down.svg" : "interface/white/fluent-square.svg")
                .Replace('/', System.IO.Path.DirectorySeparatorChar));
            MaximizeIcon.Source = SvgImageLoader.Load(iconPath);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && MainContent.Content is AppDetailsPage appDetails)
            {
                appDetails.GoBack();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _wallpaperFullscreen && MainContent.Content is WallpaperPreviewPage preview)
            {
                preview.CloseFullscreen();
                e.Handled = true;
            }
        }

        private void ApplyWindowChrome(bool fullscreen)
        {
            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = fullscreen ? new Thickness(0) : new Thickness(6),
                CornerRadius = fullscreen ? new CornerRadius(0) : new CornerRadius(12),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);
        }

        public void EnterWallpaperFullscreen()
        {
            if (_wallpaperFullscreen) return;
            _wallpaperFullscreen = true;

            ApplyWindowChrome(true);
            if (MainContent.Content is WallpaperPreviewPage preview)
                preview.SetFullscreenScrollOffset(true);
            TitleBar.Visibility = Visibility.Collapsed;
            SidebarBorder.Visibility = Visibility.Collapsed;
            var rows = ((Grid)TitleBar.Parent).RowDefinitions;
            if (rows.Count > 0) rows[0].Height = new GridLength(0);
            if (rows.Count > 2) rows[2].Height = new GridLength(28);
            if (rows.Count > 3) rows[3].Height = new GridLength(8);
            SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
            SidebarColumn.Width = new GridLength(0);
        }

        public void ExitWallpaperFullscreen()
        {
            if (!_wallpaperFullscreen) return;
            _wallpaperFullscreen = false;

            ApplyWindowChrome(false);
            if (MainContent.Content is WallpaperPreviewPage preview)
                preview.SetFullscreenScrollOffset(false);
            TitleBar.Visibility = Visibility.Visible;
            SidebarBorder.Visibility = Visibility.Visible;
            var rows = ((Grid)TitleBar.Parent).RowDefinitions;
            if (rows.Count > 0) rows[0].Height = new GridLength(64);
            if (rows.Count > 2) rows[2].Height = new GridLength(26);
            if (rows.Count > 3) rows[3].Height = new GridLength(0);
            SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
            SidebarColumn.Width = new GridLength(260);
        }

        private void ShowPage(UserControl page) => MainContent.Content = page;

        private void ConfigureNavigation()
        {
            _navigation.Register("home", () => new HomePage(this));
            _navigation.Register("apps", () => new AppsPage(this));
            _navigation.Register("uwp", () => new UwpAppsPage());
            _navigation.Register("libraries", () => new LibrariesPage());
            _navigation.Register("windows", () => new WindowsSettingsPage(this));
            _navigation.Register("activation", () => new WindowsActivationPage());
            _navigation.Register("wallpapers", () => new WallpaperPage(this));
            _navigation.Register("downloads", () => new DownloadsPage(this));
            _navigation.Register("diskcleanup", () => new DiskCleanupPage(this));
            _navigation.Register("settings", () => new SettingsPage(this));
            _navigation.Register("profile", () => new ProfilePage(this));
        }

        private UserControl GetOrCreatePage(string page)
        {
            if (_pageCache.TryGetValue(page, out var cached))
                return cached;

            UserControl result = _navigation.Resolve(page);

            if (result != null)
                _pageCache[page] = result;

            return result;
        }

        public void Navigate(string page)
        {
            // Don't track detail pages as "current" for navigation history
            if (page != "appdetail" && page != "wallpaperdetail")
            {
                _previousPage = _currentPage;
                _currentPage = page;
            }

            // Update nav button selection
            foreach (var item in NavPanel.Children)
                if (item is Button b) b.Tag = "Normal";
            SettingsNav.Tag = "Normal";
            ProfileNav.Tag = "Normal";

            if (page == "home") HomeNav.Tag = "Selected";
            else if (page == "apps") AppsNav.Tag = "Selected";
            else if (page == "uwp") UwpAppsNav.Tag = "Selected";
            else if (page == "libraries") LibrariesNav.Tag = "Selected";
            else if (page == "windows") WindowsNav.Tag = "Selected";
            else if (page == "activation") WindowsActivationNav.Tag = "Selected";
            else if (page == "wallpapers") WallpapersNav.Tag = "Selected";
            else if (page == "downloads") DownloadsNav.Tag = "Selected";
            else if (page == "diskcleanup") DiskCleanupNav.Tag = "Selected";
            else if (page == "settings") SettingsNav.Tag = "Selected";
            else if (page == "profile") ProfileNav.Tag = "Selected";


            if (!string.Equals(page, "apps", StringComparison.OrdinalIgnoreCase) &&
                _pageCache.TryGetValue("apps", out var appsPage) && appsPage is AppsPage cachedAppsPage)
            {
                cachedAppsPage.ResetSelectionState();
            }

            if (page == "appdetail")
            {
                ShowPage(new AppDetailsPage(this, SelectedApp));
                return;
            }

            if (page == "wallpaperdetail")
            {
                ShowPage(new WallpaperPreviewPage(this));
                return;
            }

            var target = GetOrCreatePage(page);
            if (target != null)
                ShowPage(target);
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (_pageCache.TryGetValue("apps", out var appsPage) && appsPage is AppsPage cachedAppsPage)
                cachedAppsPage.ResetSelectionState();

            foreach (var item in NavPanel.Children)
                if (item is Button b) b.Tag = "Normal";
            SettingsNav.Tag = "Normal";
            ProfileNav.Tag = "Normal";

            if (sender is Button button)
            {
                button.Tag = "Selected";
                if (button == HomeNav) Navigate("home");
                else if (button == AppsNav) Navigate("apps");
                else if (button == UwpAppsNav) Navigate("uwp");
                else if (button == LibrariesNav) Navigate("libraries");
                else if (button == WindowsNav) Navigate("windows");
                else if (button == WindowsActivationNav) Navigate("activation");
                else if (button == WallpapersNav) Navigate("wallpapers");
                else if (button == DownloadsNav) Navigate("downloads");
                else if (button == DiskCleanupNav) Navigate("diskcleanup");
                else if (button == SettingsNav) Navigate("settings");
                else if (button == ProfileNav) Navigate("profile");
            }
        }

        private sealed class GridLengthAnimation : AnimationTimeline
        {
            public GridLength From { get; set; }
            public GridLength To { get; set; }
            public IEasingFunction EasingFunction { get; set; }
            public override Type TargetPropertyType => typeof(GridLength);
            protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
            public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
            {
                var progress = animationClock.CurrentProgress ?? 0;
                if (EasingFunction != null) progress = EasingFunction.Ease(progress);
                var from = From.Value; var to = To.Value;
                return new GridLength(from + ((to - from) * progress), GridUnitType.Pixel);
            }
        }
    }
}
