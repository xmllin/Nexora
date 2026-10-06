using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nexora.Models;
using Nexora.Services;
using Nexora.Services.Downloads;

namespace Nexora.Pages
{
    public partial class AppDetailsPage : UserControl
    {
        private readonly MainWindow _main;
        private readonly DownloadService _downloads = new DownloadService();
        private readonly GitHubDownloadProvider _github = new GitHubDownloadProvider();
        private readonly ChromiumDownloadProvider _chromium = new ChromiumDownloadProvider();
        private readonly OfficialPageDownloadProvider _official = new OfficialPageDownloadProvider();
        private readonly DownloadMetadataService _metadata = new DownloadMetadataService();
        private readonly ApplicationVersionResolver _versionResolver = new ApplicationVersionResolver();
        private readonly AppDefinition _app;
        private IReadOnlyList<AppRelease> _releases;
        private DownloadInfo _displayedInfo;
        private DownloadInfo _resolvedInitialDownload;
        private string _appNameFallback => string.IsNullOrWhiteSpace(_app?.Name) ? "Приложение" : _app.Name;
        private readonly SemaphoreSlim _metadataWarmSemaphore = new SemaphoreSlim(3, 3);

        public AppDetailsPage(MainWindow main, AppDefinition app)
        {
            InitializeComponent();
            _main = main;
            _app = app ?? new AppDefinition { Name = "Приложение" };
            DataContext = _app;

            DownloadInfo initialInfo = null;
            if (_app.Download != null)
            {
                var initialFileName = _app.Download.FileName;
                if (!HasRealExtension(initialFileName) && Uri.TryCreate(_app.Download.Url, UriKind.Absolute, out var initialUri))
                    initialFileName = System.IO.Path.GetFileName(initialUri.AbsolutePath);

                if (_main.TryGetCachedAppDownloadInfo(_app, out var cachedAppInfo))
                    initialInfo = cachedAppInfo;
                else
                {
                    initialInfo = new DownloadInfo
                    {
                        Url = _app.Download.Url,
                        FileName = initialFileName,
                        Source = _app.Download.Type,
                        Version = VersionNormalizer.ExtractMostSpecific(_app.Download.FileName, _app.Download.Url)
                    };
                }
            }
            if (initialInfo != null && _main.TryGetCachedDownloadInfo(initialInfo.Url, out var cachedInfo))
                initialInfo = cachedInfo;

            _resolvedInitialDownload = initialInfo;
            ApplyDownloadDetails(initialInfo);
            Loaded += AppDetailsPage_Loaded;
        }

        private async void AppDetailsPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= AppDetailsPage_Loaded;

            var releaseProvider = GetReleaseProvider();
            VersionComboBox.Visibility = Visibility.Visible;

            IReadOnlyList<AppRelease> cachedReleases;
            if (_main.TryGetCachedReleases(_app, out cachedReleases))
            {
                ApplyReleases(cachedReleases);
                _ = RefreshReleaseCatalogAsync(releaseProvider);
                return;
            }

            DownloadStatusText.Text = "Загрузка списка версий…";
            await RefreshReleaseCatalogAsync(releaseProvider);
        }

        private async Task RefreshReleaseCatalogAsync(IReleaseDownloadProvider provider)
        {
            try
            {
                var selectedVersion = (VersionComboBox.SelectedItem as AppRelease)?.Version;
                IReadOnlyList<AppRelease> freshReleases;
                using (var refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    if (provider != null)
                        freshReleases = await provider.GetReleasesAsync(_app, refreshCts.Token);
                    else
                    {
                        var singleRelease = await ResolveSingleReleaseAsync(refreshCts.Token);
                        freshReleases = singleRelease == null
                            ? new List<AppRelease>()
                            : new[] { singleRelease };
                    }
                }

                _main.CacheReleases(_app, freshReleases);

                if (_main.TryGetCachedReleases(_app, out var mergedReleases))
                    ApplyReleases(mergedReleases, selectedVersion);

                DownloadStatusText.Text = string.Empty;
            }
            catch (OperationCanceledException)
            {
                if (!_main.TryGetCachedReleases(_app, out _))
                {
                    VersionComboBox.IsEnabled = false;
                    DownloadStatusText.Text = "Не удалось загрузить список версий.";
                    _main.ShowNotification(
                        "Не удалось загрузить список версий для «" + _app.Name + "». Проверьте подключение к интернету.",
                        NotificationKind.Warning,
                        "releases-timeout:" + (_app.Id ?? _app.Name));
                }
            }
            catch (Exception ex)
            {
                if (_main.TryGetCachedReleases(_app, out _))
                {
                    // Cached catalog remains immediately usable; refresh is best-effort.
                    DownloadStatusText.Text = string.Empty;
                    return;
                }

                VersionComboBox.IsEnabled = false;
                DownloadStatusText.Text = "Не удалось получить версии.";
                _main.ShowNotification(
                    "Не удалось получить версии «" + _app.Name + "»: " +
                    NotificationFormatter.FormatGeneralError(ex),
                    NotificationKind.Error,
                    "releases-error:" + (_app.Id ?? _app.Name) + ":" + ex.GetType().FullName);
            }
        }

        private void ApplyReleases(IReadOnlyList<AppRelease> releases, string selectedVersion = null)
        {
            _releases = PrepareReleaseItems(releases);
            VersionComboBox.ItemsSource = _releases;
            VersionComboBox.IsEnabled = _releases.Count > 0;

            var selectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(selectedVersion))
            {
                var normalized = VersionNormalizer.Normalize(selectedVersion);
                var preserved = _releases
                    .Select((item, index) => new { item, index })
                    .FirstOrDefault(x => string.Equals(
                        VersionNormalizer.Normalize(x.item.Version ?? string.Empty),
                        normalized,
                        StringComparison.OrdinalIgnoreCase));
                if (preserved != null)
                    selectedIndex = preserved.index;
            }

            if (_releases.Count > 0)
            {
                VersionComboBox.SelectedIndex = selectedIndex;
                var selected = _releases[selectedIndex];
                ReleaseVersionText.Text = GetReleaseDisplayVersion(selected);
                ApplyDownloadDetails(selected.Download);

            }
            else
            {
                ReleaseVersionText.Text = "Не определена";
                ApplyDownloadDetails(null);
            }

            DownloadStatusText.Text = _releases.Count == 0
                ? "Стабильные версии не найдены."
                : string.Empty;

            if (_releases.Count > 0)
                _ = WarmReleaseMetadataAsync(_releases, selectedIndex);
        }

        private async Task WarmReleaseMetadataAsync(IReadOnlyList<AppRelease> releases, int selectedIndex)
        {
            var targets = new List<AppRelease>();
            if (releases != null)
            {
                if (selectedIndex >= 0 && selectedIndex < releases.Count)
                    targets.Add(releases[selectedIndex]);

                foreach (var release in releases.Take(12))
                {
                    if (!targets.Contains(release))
                        targets.Add(release);
                }
            }

            var tasks = targets.Select(async release =>
            {
                if (release?.Download == null || release.Download.SizeBytes.HasValue)
                    return;

                await _metadataWarmSemaphore.WaitAsync().ConfigureAwait(true);
                try
                {
                    if (string.Equals(_app.Download?.Type, "WinGet", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await LoadWinGetDownloadMetadataAsync(release.Download).ConfigureAwait(true);
                        }
                        catch { }
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(release.Download.Url) &&
                        Uri.TryCreate(release.Download.Url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        try { await LoadDownloadSizeAsync(release.Download).ConfigureAwait(true); }
                        catch { }
                    }
                }
                finally
                {
                    _metadataWarmSemaphore.Release();
                }
            });

            try { await Task.WhenAll(tasks).ConfigureAwait(true); }
            catch { }
        }

        private async Task<AppRelease> ResolveSingleReleaseAsync(CancellationToken token)
        {
            var info = await _downloads.ResolveAsync(_app, token).ConfigureAwait(true);
            if (info == null)
                return null;

            // Providers are responsible for authoritative versions. Only WinGet
            // needs a separate metadata lookup because it has the package catalog.
            if (string.Equals(_app.Download?.Type, "WinGet", StringComparison.OrdinalIgnoreCase))
            {
                var version = await _versionResolver.ResolveAsync(_app, info, token).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(version))
                    info.Version = version;
            }

            if (string.IsNullOrWhiteSpace(info.Version))
                info.Version = VersionNormalizer.ExtractMostSpecific(info.FileName);

            return new AppRelease
            {
                Version = info.Version ?? string.Empty,
                Title = _app.Name,
                DisplayVersion = string.IsNullOrWhiteSpace(info.Version) ? _app.Name : info.Version,
                Download = info
            };
        }

        private List<AppRelease> PrepareReleaseItems(IEnumerable<AppRelease> releases)
        {
            var unique = (releases ?? Enumerable.Empty<AppRelease>())
                .Where(item => item != null && item.Download != null)
                .GroupBy(item => VersionNormalizer.Normalize(item.Version ?? string.Empty) + "|" + (item.Download.Format ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.PublishedAt ?? DateTime.MinValue).First())
                .OrderByDescending(item => VersionInfo.Parse(item.Version))
                .ThenByDescending(item => item.PublishedAt ?? DateTime.MinValue)
                .ToList();

            foreach (var group in unique.GroupBy(item => VersionNormalizer.Normalize(item.Version ?? string.Empty), StringComparer.OrdinalIgnoreCase))
            {
                var formats = group.Select(item => item.Download.Format)
                    .Where(format => !string.IsNullOrWhiteSpace(format) && !string.Equals(format, "Не указан", StringComparison.OrdinalIgnoreCase) && !string.Equals(format, "Файл", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var item in group)
                {
                    var version = string.IsNullOrWhiteSpace(item.Version)
                        ? (string.IsNullOrWhiteSpace(item.Title) ? _appNameFallback : item.Title)
                        : item.Version;
                    var displayVersion = string.IsNullOrWhiteSpace(item.DisplayVersion) ? version : item.DisplayVersion;
                    if (!string.IsNullOrWhiteSpace(item.Download.Format))
                    {
                        var suffix = " - " + item.Download.Format;
                        if (displayVersion.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            displayVersion = displayVersion.Substring(0, displayVersion.Length - suffix.Length).TrimEnd();
                    }

                    item.DisplayVersion = formats.Count > 1 && !string.IsNullOrWhiteSpace(item.Download.Format)
                        ? displayVersion + " - " + item.Download.Format
                        : displayVersion;
                }
            }
            return unique;
        }

        private void ApplyDownloadDetails(DownloadInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info?.Url) && _main.TryGetCachedDownloadInfo(info.Url, out var cachedInfo))
                info = cachedInfo;
            _displayedInfo = info;
            if (info == null)
            {
                ReleaseFormatText.Text = "Не указан";
                ReleaseSizeText.Text = "Не указан";
                return;
            }

            if (string.IsNullOrWhiteSpace(info.Version))
                info.Version = VersionNormalizer.ExtractMostSpecific(info.FileName);

            if (!string.IsNullOrWhiteSpace(info.Url) && !_main.TryGetCachedAppDownloadInfo(_app, out _))
                _main.CacheAppDownloadInfo(_app, info);

            ReleaseFormatText.Text = info.Format;
            ReleaseSizeText.Text = info.SizeBytes.HasValue ? FormatSize(info.SizeBytes.Value) : "—";
            if (!HasRealExtension(info.FileName) && !string.IsNullOrWhiteSpace(info.Url))
                _ = LoadDownloadFileNameAsync(info);

            if (!info.SizeBytes.HasValue)
            {
                if (string.Equals(_app.Download?.Type, "WinGet", StringComparison.OrdinalIgnoreCase))
                    _ = LoadWinGetDownloadMetadataAsync(info);
                else if (!string.IsNullOrWhiteSpace(info.Url) &&
                         Uri.TryCreate(info.Url, UriKind.Absolute, out var infoUri) &&
                         (infoUri.Scheme == Uri.UriSchemeHttp || infoUri.Scheme == Uri.UriSchemeHttps))
                    _ = LoadDownloadSizeAsync(info);
            }
        }

        private async Task LoadWinGetDownloadMetadataAsync(DownloadInfo info)
        {
            try
            {
                var provider = new WinGetDownloadProvider();
                await provider.EnrichDownloadInfoAsync(_app, info, CancellationToken.None).ConfigureAwait(true);
                _main.CacheDownloadInfo(info);
                _main.CacheAppDownloadInfo(_app, info);
                _main.CacheReleases(_app, new[]
                {
                    new AppRelease
                    {
                        Version = info.Version,
                        Title = _app.Name,
                        Download = info
                    }
                });

                if (ReferenceEquals(_displayedInfo, info))
                {
                    ReleaseFormatText.Text = info.Format;
                    if (info.SizeBytes.HasValue)
                        ReleaseSizeText.Text = FormatSize(info.SizeBytes.Value);
                }
            }
            catch { }
        }

        private async Task LoadDownloadFileNameAsync(DownloadInfo info)
        {
            try
            {
                var name = await _metadata.GetFileNameAsync(info.Url, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    info.FileName = name;
                    _main.CacheDownloadInfo(info);
                    if (ReferenceEquals(_displayedInfo, info))
                        ReleaseFormatText.Text = info.Format;
                }
                if (!info.SizeBytes.HasValue)
                    await LoadDownloadSizeAsync(info);
            }
            catch { }
        }

        private async Task LoadDownloadSizeAsync(DownloadInfo info)
        {
            try
            {
                var size = await _metadata.GetSizeAsync(info.Url, CancellationToken.None);
                if (!size.HasValue) return;
                info.SizeBytes = size.Value;
                _main.CacheDownloadInfo(info);
                _main.CacheReleases(_app, new[]
                {
                    new AppRelease
                    {
                        Version = info.Version,
                        Title = _app.Name,
                        Download = info
                    }
                });
                if (ReferenceEquals(_displayedInfo, info))
                    ReleaseSizeText.Text = FormatSize(size.Value);
            }
            catch { }
        }

        private async void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized) return;
            if (!(VersionComboBox.SelectedItem is AppRelease release))
                return;

            ReleaseVersionText.Text = GetReleaseDisplayVersion(release);
            ApplyDownloadDetails(release.Download);

            if (release.Download != null && !release.Download.SizeBytes.HasValue)
            {
                try
                {
                    if (string.Equals(_app.Download?.Type, "WinGet", StringComparison.OrdinalIgnoreCase))
                        await LoadWinGetDownloadMetadataAsync(release.Download);
                    else if (!string.IsNullOrWhiteSpace(release.Download.Url))
                        await LoadDownloadSizeAsync(release.Download);
                }
                catch { }
            }
        }

        private static string GetReleaseDisplayVersion(AppRelease release)
        {
            if (release == null)
                return "Не определена";

            // Chromium keeps the snapshot revision internally, while the UI
            // shows only the mapped product version. Other applications keep
            // their normal published version here.
            if (string.Equals(release.Download?.Source, "Chromium", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(release.DisplayVersion))
            {
                var revisionMarker = " - rev ";
                var markerIndex = release.DisplayVersion.IndexOf(revisionMarker, StringComparison.OrdinalIgnoreCase);
                return markerIndex > 0
                    ? release.DisplayVersion.Substring(0, markerIndex)
                    : release.DisplayVersion;
            }

            return string.IsNullOrWhiteSpace(release.Version)
                ? (string.IsNullOrWhiteSpace(release.Title) ? "Приложение" : release.Title)
                : release.Version;
        }

        private static bool HasRealExtension(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var extension = System.IO.Path.GetExtension(value);
            return !string.IsNullOrWhiteSpace(extension) && extension.Length > 1;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return string.Format("{0:0.0} ГБ", bytes / (1024d * 1024d * 1024d));
            if (bytes >= 1024L * 1024L) return string.Format("{0:0.0} МБ", bytes / (1024d * 1024d));
            if (bytes >= 1024L) return string.Format("{0:0.0} КБ", bytes / 1024d);
            return bytes + " Б";
        }

        private IReleaseDownloadProvider GetReleaseProvider()
        {
            if (string.Equals(_app.Download?.Type, "GitHub", StringComparison.OrdinalIgnoreCase)) return _github;
            if (string.Equals(_app.Download?.Type, "Chromium", StringComparison.OrdinalIgnoreCase)) return _chromium;
            if (string.Equals(_app.Download?.Type, "Website", StringComparison.OrdinalIgnoreCase)) return _official;
            if (string.Equals(_app.Download?.Type, "WinGet", StringComparison.OrdinalIgnoreCase)) return new WinGetDownloadProvider();
            return null;
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            var selectedRelease = VersionComboBox.SelectedItem as AppRelease;
            var selectedInfo = selectedRelease?.Download;
            var downloadKey = (_app.Name ?? _app.Id ?? "download").ToLowerInvariant();
            CancellationTokenSource cts;
            PauseController pauseController;
            if (!_main.TryRegisterDownload(_app.Name, downloadKey, out cts, out pauseController)) return;

            DownloadButton.IsEnabled = false;
            DownloadStatusText.Text = "Загрузка…";
            try
            {
                var progress = new Progress<DownloadProgress>(details => _main.UpdateDownloadProgress(downloadKey, details, _app.Name));
                if (selectedInfo != null)
                    await _downloads.DownloadAsync(_app, selectedInfo, progress, cts.Token, pauseController);
                else if (_resolvedInitialDownload != null && !string.Equals(_app.Id, "discord", StringComparison.OrdinalIgnoreCase))
                    await _downloads.DownloadAsync(_app, _resolvedInitialDownload, progress, cts.Token, pauseController);
                else
                    await _downloads.DownloadAsync(_app, progress, cts.Token, pauseController);

                _main.CompleteDownload(downloadKey, _app.Name);
                DownloadStatusText.Text = "Загрузка завершена.";
            }
            catch (OperationCanceledException)
            {
                _main.NotifyDownloadCancelled(downloadKey, _app.Name);
                _main.RemoveDownload(downloadKey);
                DownloadStatusText.Text = "Загрузка отменена.";
            }
            catch (Exception ex)
            {
                _main.NotifyDownloadError(downloadKey, _app.Name, ex);
                _main.RemoveDownload(downloadKey);
                DownloadStatusText.Text = "Не удалось скачать файл.";
            }
            finally
            {
                DownloadButton.IsEnabled = true;
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            var previousPage = _main.GetAppDetailsOrigin();
            _main.Navigate(previousPage ?? "home");
        }

        public void GoBack()
        {
            Back_Click(null, null);
        }

        private void VersionField_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            VersionComboBox.IsDropDownOpen = !VersionComboBox.IsDropDownOpen;
            e.Handled = true;
        }

        private void Website_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            OpenWebsite(e.Uri?.ToString());
            e.Handled = true;
        }

        private void OpenWebsite_Click(object sender, RoutedEventArgs e)
        {
            OpenWebsite(_app.Website);
            Keyboard.ClearFocus();
        }

        private void WebsiteTextBox_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenWebsite(_app.Website);
            e.Handled = true;
        }

        private static void OpenWebsite(string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
    }
}
