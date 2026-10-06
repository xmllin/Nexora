using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Nexora.Models;
using Nexora.Services;
using Nexora.Services.Downloads;
using Nexora.Services.Libraries;

namespace Nexora.Pages
{
    public partial class LibrariesPage : UserControl
    {
        private readonly LibraryCatalogService _catalog = new LibraryCatalogService();
        private readonly LibraryDetectionService _detection = new LibraryDetectionService();
        private readonly LibraryDownloadService _downloads = new LibraryDownloadService();
        private readonly LibraryInstallationService _installation = new LibraryInstallationService();
        private readonly ObservableCollection<LibraryItem> _items = new ObservableCollection<LibraryItem>();
        private ICollectionView _view;
        private CancellationTokenSource _installCts;
        private bool _loading;
        private Window _hostWindow;
        private readonly HashSet<Task> _activeDownloads = new HashSet<Task>();
        private bool _closingAfterDownloadCancellation;
        private Task _activeInstallTask;

        public LibrariesPage()
        {
            InitializeComponent();
            Loaded += LibrariesPage_Loaded;
            Unloaded += LibrariesPage_Unloaded;
        }

        private async void LibrariesPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var definitions = await _catalog.LoadAsync();
                foreach (var definition in definitions)
                {
                    var item = _detection.Detect(definition);
                    if (item == null) continue;

                    item.IsRecommended = IsRecommendedForSystem(definition) && item.Status != LibraryInstallStatus.Installed;
                    _items.Add(item);
                }
                BuildFilters();
                _view = CollectionViewSource.GetDefaultView(_items);
                _view.Filter = FilterItem;
                LibrariesItems.ItemsSource = _view;
                UpdateSummary();
                _hostWindow = Window.GetWindow(this);
                if (_hostWindow != null)
                {
                    _hostWindow.Activated += HostWindow_Activated;
                    _hostWindow.Closing += HostWindow_Closing;
                }
            }
            catch (Exception ex)
            {
                InstallStatusText.Text = "Не удалось загрузить каталог: " + ex.Message;
            }
        }

        private void LibrariesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_hostWindow != null)
            {
                _hostWindow.Activated -= HostWindow_Activated;
                _hostWindow.Closing -= HostWindow_Closing;
            }
            _hostWindow = null;
        }

        private async void HostWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closingAfterDownloadCancellation)
                return;

            var active = _activeDownloads.Where(task => task != null && !task.IsCompleted).ToList();
            if (_activeInstallTask != null && !_activeInstallTask.IsCompleted)
                active.Add(_activeInstallTask);

            if (active.Count == 0)
                return;

            e.Cancel = true;

            try { _installCts?.Cancel(); } catch { }

            foreach (var item in _items.Where(item => item.IsBusy))
            {
                try { item.DownloadCancellation?.Cancel(); } catch { }
                try { item.DownloadPauseController?.Resume(); } catch { }
            }

            try
            {
                await Task.WhenAll(active);
            }
            catch { }

            foreach (var item in _items)
                DeleteDownloadedFileNow(item);

            _closingAfterDownloadCancellation = true;
            if (_hostWindow != null)
                _hostWindow.Close();
        }

        private void HostWindow_Activated(object sender, EventArgs e)
        {
            if (_loading && _items.Count > 0 && _installCts == null)
                RefreshDetectedStatuses();
        }

        private void RefreshDetectedStatuses()
        {
            foreach (var item in _items)
            {
                if (item.IsWindowsFeature)
                    continue;

                var detected = _detection.Detect(item.Definition);
                var wasInstalled = item.Status == LibraryInstallStatus.Installed;
                item.Status = detected.Status;
                item.InstalledVersion = detected.InstalledVersion;
                item.IsRecommended = IsRecommendedForSystem(item.Definition) && item.Status != LibraryInstallStatus.Installed;
                if (!wasInstalled && item.Status == LibraryInstallStatus.Installed)
                    DeleteDownloadedInstaller(item.Definition);
                item.Refresh();
            }
            if (_view != null) _view.Refresh();
            UpdateSummary();
        }

        private void BuildFilters()
        {
            CategoryComboBox.ItemsSource = new[] { "Все категории" }.Concat(_items.Select(x => x.Definition.Category).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();
            CategoryComboBox.SelectedIndex = 0;
            StatusComboBox.ItemsSource = new[] { "Все статусы", "Установлено", "Не установлено", "Доступно обновление", "Рекомендуемое" };
            StatusComboBox.SelectedIndex = 0;
        }

        private bool FilterItem(object value)
        {
            var item = value as LibraryItem;
            if (item == null) return false;
            var category = CategoryComboBox?.SelectedItem as string;
            var status = StatusComboBox?.SelectedItem as string;
            var matchesCategory = string.IsNullOrWhiteSpace(category) || category == "Все категории" || string.Equals(item.Definition.Category, category, StringComparison.OrdinalIgnoreCase);
            var matchesRecommended = status != "Рекомендуемое" || item.IsRecommended;
            var matchesStatusValue = status == "Рекомендуемое" || string.IsNullOrWhiteSpace(status) || status == "Все статусы" || string.Equals(item.StatusText, status, StringComparison.OrdinalIgnoreCase);
            return matchesCategory && matchesStatusValue && matchesRecommended;
        }

        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            if (_view != null) _view.Refresh();
            EmptyText.Visibility = _view != null && _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateSummary()
        {
            QueueText.Text = "Выбрано: " + _items.Count(x => x.IsSelected);
        }

        private void SelectMissing_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items.Where(x => x.IsMissing)) item.IsSelected = true;
            UpdateSummary();
        }

        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items) item.IsSelected = false;
            UpdateSummary();
        }

        private void LibraryCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is Button) return;
                source = source is Visual
                    ? VisualTreeHelper.GetParent(source)
                    : (source as FrameworkContentElement)?.Parent;
            }

            var item = (sender as Border)?.DataContext as LibraryItem;
            if (item == null) return;

            item.IsSelected = !item.IsSelected;
            item.Refresh();
            UpdateSummary();
        }

        private void OpenInstallerFolder_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as MenuItem)?.Tag as LibraryItem;
            if (item == null) return;

            // The installer cache is always stored here. Do not probe File.Exists
            // on the UI thread: a stale or inaccessible path can make Explorer
            // appear to hang while Windows resolves the file.
            var folder = Path.Combine(Path.GetTempPath(), "Nexora", "Libraries");
            try
            {
                Directory.CreateDirectory(folder);
                OpenFolder(folder);
            }
            catch { }
        }

        private void MoreActions_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.ContextMenu == null) return;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        private static void DeleteDownloadedFileNow(LibraryItem item)
        {
            if (item == null)
                return;

            var paths = new[]
            {
                item.DownloadedFilePath,
                string.IsNullOrWhiteSpace(item.Definition?.FileName)
                    ? null
                    : Path.Combine(Path.GetTempPath(), "Nexora", "Libraries", item.Definition.FileName)
            };

            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch { }
            }

            item.DownloadedFilePath = null;
            item.Notify(nameof(item.DownloadedFilePath));
            item.Notify(nameof(item.HasDownloadedFile));
        }

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as LibraryItem;
            if (item == null) return;
            LibraryDetailsDialog.Show(Window.GetWindow(this), item);
        }

        private async void Reinstall_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as LibraryItem;
            if (item == null || item.IsBusy) return;

            try
            {
                item.IsBusy = true;
                item.ShowProgress = false;
                item.Notify(nameof(item.IsBusy));
                item.Notify(nameof(item.ShowProgress));
                InstallStatusText.Text = "Подготовка установщика: " + item.Definition.Name;

                // Reinstall means opening the actual installer. If the cached
                // installer was removed, download a fresh official copy first.
                var installerPath = _installation.GetCachedInstallerPath(item.Definition);
                if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
                {
                    if (string.IsNullOrWhiteSpace(item.Definition.DownloadUrl))
                        throw new InvalidOperationException("Для переустановки не задан источник установщика.");

                    item.ShowProgress = true;
                    item.ProgressOpacity = 1;
                    item.Progress = 0;
                    item.ProgressText = "Скачивание установщика…";
                    item.DownloadCancellation?.Dispose();
                    item.DownloadPauseController?.Dispose();
                    item.DownloadCancellation = new CancellationTokenSource();
                    item.DownloadPauseController = new PauseController();
                    item.Notify(nameof(item.ShowProgress));
                    item.Notify(nameof(item.ProgressOpacity));
                    item.Notify(nameof(item.Progress));
                    item.Notify(nameof(item.ProgressText));

                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        item.Progress = p.Progress < 0 ? 0 : p.Progress;
                        item.ProgressText = FormatProgress(p);
                        item.Notify(nameof(item.Progress));
                        item.Notify(nameof(item.ProgressText));
                    });

                    installerPath = await _downloads.DownloadAsync(
                        item.Definition,
                        progress,
                        item.DownloadCancellation.Token,
                        item.DownloadPauseController);

                    item.DownloadedFilePath = installerPath;
                    item.Progress = 100;
                    item.ProgressText = "Установщик загружен";
                    item.Notify(nameof(item.DownloadedFilePath));
                    item.Notify(nameof(item.HasDownloadedFile));
                    item.Notify(nameof(item.Progress));
                    item.Notify(nameof(item.ProgressText));
                }

                InstallStatusText.Text = "Запуск установщика: " + item.Definition.Name;
                await _installation.LaunchInstallerAsync(item.Definition, installerPath, CancellationToken.None);

                var detected = _detection.Detect(item.Definition);
                item.Status = detected.Status;
                item.InstalledVersion = detected.InstalledVersion;
                item.IsSelected = false;
                item.IsBusy = false;
                item.Notify(nameof(item.Status));
                item.Notify(nameof(item.InstalledVersion));
                item.Notify(nameof(item.IsSelected));
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = detected.Status == LibraryInstallStatus.Installed
                    ? "Переустановка завершена: " + item.Definition.Name
                    : "Установщик завершён: " + item.Definition.Name;
                item.Refresh();

                await HideProgressAfterDelayAsync(item);
            }
            catch (OperationCanceledException)
            {
                item.IsBusy = false;
                item.ShowProgress = false;
                item.Notify(nameof(item.IsBusy));
                item.Notify(nameof(item.ShowProgress));
                item.Notify(nameof(item.CanPauseDownload));
                item.Notify(nameof(item.CanResumeDownload));
                InstallStatusText.Text = "Переустановка отменена: " + item.Definition.Name;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC cancellation is a normal user action, not an installer error.
                item.IsBusy = false;
                item.ShowProgress = false;
                item.Notify(nameof(item.IsBusy));
                item.Notify(nameof(item.ShowProgress));
                InstallStatusText.Text = "Переустановка отменена: " + item.Definition.Name;
            }
            catch (Exception ex)
            {
                item.IsBusy = false;
                item.ShowProgress = false;
                item.Notify(nameof(item.IsBusy));
                item.Notify(nameof(item.ShowProgress));
                InstallStatusText.Text = "Ошибка переустановки: " + ex.Message;
                AppDialog.ShowInfo(Window.GetWindow(this), item.Definition.Name, ex.Message);
            }
            finally
            {
                item.DownloadPauseController?.Dispose();
                item.DownloadCancellation?.Dispose();
                item.DownloadPauseController = null;
                item.DownloadCancellation = null;
                item.Notify(nameof(item.IsPaused));
                item.Notify(nameof(item.CanPauseDownload));
                item.Notify(nameof(item.CanResumeDownload));
            }
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as LibraryItem;
            if (item == null) return;
            var task = DownloadLibraryAsync(item, installAfterDownload: false);
            _activeDownloads.Add(task);
            try { await task; } finally { _activeDownloads.Remove(task); }
        }

        private async void DownloadAndInstall_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as LibraryItem;
            if (item == null) return;
            var task = DownloadLibraryAsync(item, installAfterDownload: true);
            _activeDownloads.Add(task);
            try { await task; } finally { _activeDownloads.Remove(task); }
        }

        private async void WindowsFeatureToggle_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var toggle = sender as ToggleButton;
            var item = toggle?.Tag as LibraryItem;
            if (item == null || !item.IsWindowsFeature || item.IsBusy) return;

            var enabled = toggle.IsChecked == true;
            var action = enabled ? "Включить" : "Отключить";
            if (!AppDialog.ShowConfirm(Window.GetWindow(this), action + " компонент", action + " компонент \"" + item.Definition.Name + "\" в Windows?"))
            {
                toggle.IsChecked = !enabled;
                return;
            }

            try
            {
                item.IsBusy = true;
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = action + ": " + item.Definition.Name;
                if (enabled)
                    await _installation.InstallWindowsFeatureAsync(item.Definition, CancellationToken.None);
                else
                    await _installation.UninstallWindowsFeatureAsync(item.Definition, CancellationToken.None);

                item.Status = enabled ? LibraryInstallStatus.Installed : LibraryInstallStatus.Missing;
                item.IsBusy = false;
                item.Refresh();
                InstallStatusText.Text = action + " завершено: " + item.Definition.Name;
            }
            catch (Exception ex)
            {
                item.IsBusy = false;
                item.Notify(nameof(item.IsBusy));
                toggle.IsChecked = !enabled;
                InstallStatusText.Text = "Ошибка: " + ex.Message;
                AppDialog.ShowInfo(Window.GetWindow(this), item.Definition.Name, ex.Message);
            }
        }

        private async void Delete_Click (object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = (sender as Button)?.Tag as LibraryItem;
            if (item == null || item.Definition == null || item.IsDeleting) return;

            var actionText = item.IsWindowsFeature
                ? $"Отключить компонент \"{item.Definition.Name}\" в Windows?"
                : $"Удалить компонент \"{item.Definition.Name}\" из системы?\n\nЭто удалит установленные файлы и запись из реестра Windows.";
            if (!AppDialog.ShowConfirm(Window.GetWindow(this), item.IsWindowsFeature ? "Отключение компонента" : "Удаление компонента", actionText)) return;

            try
            {
                item.IsDeleting = true;
                item.IsBusy = true;
                item.Notify(nameof(item.IsDeleting));
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = "Удаление: " + item.Definition.Name;

                if (item.IsWindowsFeature)
                {
                    await _installation.UninstallWindowsFeatureAsync(item.Definition, CancellationToken.None);
                }
                else
                {
                    await _installation.UninstallAsync(item.Definition, CancellationToken.None);
                }

                var detected = item.IsWindowsFeature
                    ? _detection.Detect(item.Definition)
                    : await DetectAfterUninstallAsync(item.Definition);

                // A successful uninstall is authoritative for the UI. If Windows
                // is still flushing the uninstall registry entry, RefreshDetectedStatuses
                // will reconcile it on the next activation.
                item.Status = item.IsWindowsFeature
                    ? detected.Status
                    : LibraryInstallStatus.Missing;
                item.InstalledVersion = item.Status == LibraryInstallStatus.Installed
                    ? detected.InstalledVersion
                    : null;
                item.IsSelected = false;
                item.IsBusy = false;
                item.IsDeleting = false;
                item.Notify(nameof(item.Status));
                item.Notify(nameof(item.InstalledVersion));
                item.IsRecommended = IsRecommendedForSystem(item.Definition) && item.Status != LibraryInstallStatus.Installed;
                item.Notify(nameof(item.ShowRecommended));
                item.Notify(nameof(item.IsSelected));
                item.Notify(nameof(item.IsDeleting));
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = "Удаление завершено: " + item.Definition.Name;
            }
            catch (OperationCanceledException)
            {
                item.IsBusy = false;
                item.IsDeleting = false;
                item.Notify(nameof(item.IsDeleting));
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = "Удаление отменено: " + item.Definition.Name;
            }
            catch (Exception ex)
            {
                item.IsBusy = false;
                item.IsDeleting = false;
                item.Notify(nameof(item.IsDeleting));
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = "Ошибка удаления: " + ex.Message;
                AppDialog.ShowInfo(Window.GetWindow(this), item.Definition.Name, ex.Message);
            }
        }

        private async Task<LibraryItem> DetectAfterUninstallAsync(LibraryDefinition definition)
        {
            LibraryItem detected = null;

            // Деинсталляторы иногда завершают основной процесс раньше,
            // чем запись Uninstall исчезает из реестра.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                detected = _detection.Detect(definition);
                if (detected.Status != LibraryInstallStatus.Installed)
                    return detected;

                await Task.Delay(250);
            }

            return detected ?? _detection.Detect(definition);
        }

        private async void InstallSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _items.Where(x => x.IsSelected &&
                (x.IsWindowsFeature || x.Definition.CanInstallAutomatically) &&
                x.Status != LibraryInstallStatus.Installed).ToList();
            if (selected.Count == 0)
            {
                AppDialog.ShowInfo(Window.GetWindow(this), "Библиотеки", "Выберите устанавливаемые компоненты. Компоненты без автоматической установки отображаются как «Не установлено».");
                return;
            }
            if (!AppDialog.ShowConfirm(Window.GetWindow(this), "Подтверждение", "Установить выбранные компоненты? Установщики будут запущены с правами администратора.")) return;
            var installTask = InstallItemsAsync(selected);
            _activeInstallTask = installTask;
            try { await installTask; }
            finally { _activeInstallTask = null; }
        }

        private bool IsRecommendedForSystem(LibraryDefinition definition)
        {
            if (definition == null)
                return false;

            var platform = PlatformDetectionService.Current;
            var architecture = platform.Architecture ?? "x64";
            var id = (definition.Id ?? string.Empty).ToLowerInvariant();

            if (id == "dotnet-framework-481")
            {
                // .NET Framework 4.8.1 is installable on Windows 10 20H2+;
                // ARM64 support starts with Windows 11.
                if (!platform.IsWindows10OrNewer || platform.WindowsBuild < 19042)
                    return false;
                if (architecture == "arm64" && platform.WindowsBuild < 22000)
                    return false;

                var detected = _detection.Detect(definition);
                return detected.Status != LibraryInstallStatus.Installed;
            }

            if (!string.Equals(definition.Category, "Visual C++ Redistributable", StringComparison.OrdinalIgnoreCase))
                return false;

            if (definition.RecommendationTags != null &&
                definition.RecommendationTags.Contains("vcpp-v14-x64", StringComparer.OrdinalIgnoreCase))
                return (architecture == "x64" || architecture == "arm64");

            if (definition.RecommendationTags != null &&
                definition.RecommendationTags.Contains("vcpp-v14-x86", StringComparer.OrdinalIgnoreCase))
                return architecture == "x64";

            return false;
        }

        private async Task InstallItemsAsync(IEnumerable<LibraryItem> source)
        {
            if (_installCts != null) return;
            _installCts = new CancellationTokenSource();
            var items = source.ToList();
            var completed = 0;
            try
            {
                foreach (var item in items)
                {
                    _installCts.Token.ThrowIfCancellationRequested();
                    if (item.IsWindowsFeature)
                    {
                        InstallStatusText.Text = "Включение компонента Windows: " + item.Definition.Name;
                        await _installation.InstallWindowsFeatureAsync(item.Definition, _installCts.Token);
                        var featureDetected = _detection.Detect(item.Definition);
                        item.Status = featureDetected.Status;
                        item.InstalledVersion = featureDetected.InstalledVersion;
                        item.IsSelected = false;
                        item.Refresh();
                        completed++;
                        continue;
                    }
                    item.IsBusy = true; item.ShowProgress = true; item.ProgressOpacity = 1; item.Progress = 0; item.ProgressText = "Подготовка…";
                    item.Notify(nameof(item.IsBusy)); item.Notify(nameof(item.ShowProgress)); item.Notify(nameof(item.ProgressOpacity)); item.Notify(nameof(item.Progress)); item.Notify(nameof(item.ProgressText)); item.Refresh();
                    InstallStatusText.Text = "Подготовка: " + item.Definition.Name + " (" + completed + "/" + items.Count + ")";
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        item.Progress = p.Progress < 0 ? 0 : p.Progress;
                        item.ProgressText = FormatProgress(p);
                        item.Notify(nameof(item.Progress)); item.Notify(nameof(item.ProgressText));
                    });
                    var path = await _downloads.DownloadAsync(item.Definition, progress, _installCts.Token);
                    InstallStatusText.Text = "Установка: " + item.Definition.Name;
                    await _installation.InstallAsync(item.Definition, path, _installCts.Token);
                    var detected = _detection.Detect(item.Definition);
                    item.Status = detected.Status;
                    item.InstalledVersion = detected.InstalledVersion;
                    item.IsSelected = false; item.IsBusy = false;
                    item.Notify(nameof(item.ShowRecommended)); item.Progress = item.Status == LibraryInstallStatus.Installed ? 100 : 0;
                    item.ProgressText = item.Status == LibraryInstallStatus.Installed ? "Загружено и установлено" : "Установщик завершён";
                    if (item.Status == LibraryInstallStatus.Installed)
                    {
                        DeleteDownloadedInstaller(item.Definition);
                        item.DownloadedFilePath = null;
                        item.Notify(nameof(item.DownloadedFilePath));
                        item.Notify(nameof(item.HasDownloadedFile));
                    }
                    item.Notify(nameof(item.IsBusy)); item.Notify(nameof(item.ProgressText)); item.Refresh();
                    _ = HideProgressAfterDelayAsync(item);
                    completed++;
                }
                InstallStatusText.Text = "Готово: установлено " + completed + " из " + items.Count + ".";
            }
            catch (OperationCanceledException)
            {
                InstallStatusText.Text = "Установка отменена.";
            }
            catch (Exception ex)
            {
                InstallStatusText.Text = "Ошибка установки: " + ex.Message;
            }
            finally
            {
                foreach (var item in items) { item.IsBusy = false; item.Notify(nameof(item.IsBusy)); item.Refresh(); }
                _installCts.Dispose(); _installCts = null; UpdateSummary();
            }
        }

        private void CancelInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_installCts != null) _installCts.Cancel();
        }

        private async Task DownloadLibraryAsync(LibraryItem item, bool installAfterDownload)
        {
            if (item == null || item.Definition == null || item.IsBusy)
                return;

            if (string.IsNullOrWhiteSpace(item.Definition.DownloadUrl))
            {
                if (item.IsWindowsFeature)
                {
                    await InstallWindowsFeatureAsync(item);
                    return;
                }
                OpenSource(item);
                return;
            }

            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    item.Progress = p.Progress < 0 ? 0 : p.Progress;
                    item.ProgressText = FormatProgress(p);
                    item.Notify(nameof(item.Progress));
                    item.Notify(nameof(item.ProgressText));
                });

                item.DownloadCancellation?.Dispose();
                item.DownloadPauseController?.Dispose();
                item.DownloadCancellation = new CancellationTokenSource();
                item.DownloadPauseController = new PauseController();
                item.DownloadedFilePath = _downloads.GetDownloadPath(item.Definition);

                item.IsBusy = true;
                item.ShowProgress = true;
                item.ProgressOpacity = 1;
                item.Progress = 0;
                item.ProgressText = "Подготовка…";
                item.Notify(nameof(item.IsBusy));
                item.Notify(nameof(item.ShowProgress));
                item.Notify(nameof(item.ProgressOpacity));
                item.Notify(nameof(item.Progress));
                item.Notify(nameof(item.ProgressText));
                item.Notify(nameof(item.DownloadedFilePath));
                item.Notify(nameof(item.HasDownloadedFile));
                item.Notify(nameof(item.CanPauseDownload));
                item.Notify(nameof(item.CanResumeDownload));
                item.Notify(nameof(item.CanPauseDownload));
                item.Notify(nameof(item.CanResumeDownload));
                InstallStatusText.Text = installAfterDownload ? "Скачивание и запуск установщика: " + item.Definition.Name : "Скачивание: " + item.Definition.Name;

                var path = await _downloads.DownloadAsync(item.Definition, progress, item.DownloadCancellation.Token, item.DownloadPauseController);
                item.DownloadedFilePath = path;
                item.Progress = 100;
                item.ProgressText = "Загружено: " + FormatBytes(new FileInfo(path).Length);
                // The file is ready, so pause/play/cancel controls are no longer needed.
                item.ShowProgress = false;
                item.Notify(nameof(item.Progress));
                item.Notify(nameof(item.ProgressText));
                item.Notify(nameof(item.DownloadedFilePath));
                item.Notify(nameof(item.HasDownloadedFile));
                item.Notify(nameof(item.ShowProgress));

                InstallStatusText.Text = installAfterDownload ? "Установка: " + item.Definition.Name : "Загружено: " + path;

                if (installAfterDownload)
                {
                    await _installation.InstallAsync(item.Definition, path, item.DownloadCancellation.Token);
                    var detected = _detection.Detect(item.Definition);
                    item.Status = detected.Status;
                    item.InstalledVersion = detected.InstalledVersion;
                    item.IsSelected = false;
                    item.IsBusy = false;
                    item.Notify(nameof(item.ShowRecommended));
                    item.Progress = item.Status == LibraryInstallStatus.Installed ? 100 : 0;
                    if (item.Status == LibraryInstallStatus.Installed)
                    {
                        DeleteDownloadedInstaller(item.Definition);
                        item.DownloadedFilePath = null;
                        item.Notify(nameof(item.DownloadedFilePath));
                        item.Notify(nameof(item.HasDownloadedFile));
                    }
                    item.Notify(nameof(item.IsBusy));
                    item.Notify(nameof(item.ShowProgress));
                    item.Refresh();
                    UpdateSummary();
                    InstallStatusText.Text = item.Status == LibraryInstallStatus.Installed
                        ? "Установлено: " + item.Definition.Name
                        : "Установщик завершён, но компонент не обнаружен: " + item.Definition.Name;
                }
                else
                {
                    item.IsBusy = false;
                    item.Notify(nameof(item.IsBusy));
                    item.Notify(nameof(item.ShowProgress));
                }
                await HideProgressAfterDelayAsync(item);
            }
            catch (OperationCanceledException)
            {
                item.IsBusy = false;
                item.ProgressText = "Загрузка отменена";
                item.ShowProgress = false;
                item.Notify(nameof(item.IsBusy));
                item.Notify(nameof(item.ProgressText));
                item.Notify(nameof(item.ShowProgress));
                item.Notify(nameof(item.CanPauseDownload));
                item.Notify(nameof(item.CanResumeDownload));
                DeleteDownloadedFileNow(item);
                InstallStatusText.Text = "Загрузка отменена: " + item.Definition.Name;
            }
            catch (Exception ex)
            {
                item.IsBusy = false;
                item.ShowProgress = false;
                item.Notify(nameof(item.IsBusy));
                item.Notify(nameof(item.ShowProgress));
                item.Notify(nameof(item.CanPauseDownload));
                item.Notify(nameof(item.CanResumeDownload));
                InstallStatusText.Text = "Ошибка загрузки: " + ex.Message;
                AppDialog.ShowInfo(Window.GetWindow(this), item.Definition.Name, ex.Message);
            }
            finally
            {
                item.DownloadPauseController?.Dispose();
                item.DownloadCancellation?.Dispose();
                item.DownloadPauseController = null;
                item.DownloadCancellation = null;
                item.Notify(nameof(item.IsPaused));
                item.Notify(nameof(item.CanPauseDownload));
                item.Notify(nameof(item.CanResumeDownload));
            }
        }

        private void PauseResumeLibraryDownload_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as LibraryItem;
            if (item?.DownloadPauseController == null || !item.IsBusy)
                return;

            if (item.DownloadPauseController.IsPaused)
                item.DownloadPauseController.Resume();
            else
                item.DownloadPauseController.Pause();

            item.Refresh();
        }

        private void CancelLibraryDownload_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as LibraryItem;
            if (item?.DownloadCancellation == null)
                return;

            item.DownloadCancellation.Cancel();
            if (item.DownloadPauseController != null)
                item.DownloadPauseController.Resume();
            DeleteDownloadedFileNow(item);
        }

        private async Task InstallWindowsFeatureAsync(LibraryItem item)
        {
            try
            {
                item.IsBusy = true;
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = "Включение компонента Windows: " + item.Definition.Name;
                await _installation.InstallWindowsFeatureAsync(item.Definition, CancellationToken.None);
                var detected = _detection.Detect(item.Definition);
                item.Status = detected.Status;
                item.InstalledVersion = detected.InstalledVersion;
                item.IsBusy = false;
                item.Notify(nameof(item.IsBusy));
                item.Refresh();
                InstallStatusText.Text = "Компонент Windows включён: " + item.Definition.Name;
            }
            catch (Exception ex)
            {
                item.IsBusy = false;
                item.Notify(nameof(item.IsBusy));
                InstallStatusText.Text = "Ошибка включения компонента: " + ex.Message;
                AppDialog.ShowInfo(Window.GetWindow(this), item.Definition.Name, ex.Message);
            }
        }

        private async Task HideProgressAfterDelayAsync(LibraryItem item)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (item.IsBusy) return;
            for (var opacity = 1d; opacity >= 0; opacity -= 0.1d)
            {
                item.ProgressOpacity = Math.Max(0, opacity);
                item.Notify(nameof(item.ProgressOpacity));
                await Task.Delay(40);
            }
            item.ShowProgress = false;
            item.ProgressOpacity = 1;
            item.Notify(nameof(item.ShowProgress));
            item.Notify(nameof(item.ProgressOpacity));
        }

        private static string FormatProgress(DownloadProgress progress)
        {
            var received = FormatBytes(progress.BytesReceived);
            return progress.TotalBytes.HasValue
                ? "Загружено: " + received + " / " + FormatBytes(progress.TotalBytes.Value)
                : "Загружено: " + received;
        }

        private static string FormatBytes(long value)
        {
            if (value < 1024) return value + " Б";
            if (value < 1024 * 1024) return (value / 1024d).ToString("0.0") + " КБ";
            if (value < 1024L * 1024 * 1024) return (value / (1024d * 1024)).ToString("0.0") + " МБ";
            return (value / (1024d * 1024 * 1024)).ToString("0.0") + " ГБ";
        }

        private static void OpenFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            try
            {
                // Do not query the directory contents before opening it.
                // Explorer can handle a folder that was just created, while
                // Directory.Exists can synchronously block on an unavailable path.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\""+folder.Replace("\"", "\\\"")+"\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static void DeleteDownloadedInstaller(LibraryDefinition definition)
        {
            if (definition == null) return;
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Nexora", "Libraries");
            var candidates = new[]
            {
                definition.FileName,
                definition.Id + ".exe",
                definition.Id + ".msi",
                System.IO.Path.GetFileName(definition.DownloadUrl ?? string.Empty)
            };

            foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var path = System.IO.Path.Combine(folder, candidate);
                if (System.IO.File.Exists(path))
                    try { System.IO.File.Delete(path); } catch { }
            }
        }

        private static void OpenSource(LibraryItem item)
        {
            if (item?.Definition == null || string.IsNullOrWhiteSpace(item.Definition.SourceUrl)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = item.Definition.SourceUrl, UseShellExecute = true }); }
            catch { }
        }

    }
}
