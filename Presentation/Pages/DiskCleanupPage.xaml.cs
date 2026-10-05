using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nexora.Domain.WindowsSettings;
using Nexora.Services;

namespace Nexora.Pages
{
    public partial class DiskCleanupPage : UserControl
    {
        private readonly MainWindow _main;
        private readonly DiskCleanupService _service = new DiskCleanupService();
        private readonly ObservableCollection<DiskCleanupItem> _items = new ObservableCollection<DiskCleanupItem>();
        private CancellationTokenSource _cts;
        private bool _busy;

        public DiskCleanupPage(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            CleanupItemsControl.ItemsSource = _items;
            SetPageStatus("Подготовка…", NotificationKind.Info);
            Loaded += DiskCleanupPage_Loaded;
        }

        private async void DiskCleanupPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= DiskCleanupPage_Loaded;
            await ScanAsync();
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

        private async Task ScanAsync()
        {
            if (_busy) return;
            _busy = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetBusy(true, "Сканирование диска…");
            try
            {
                if (_items.Count == 0)
                {
                    foreach (var item in _service.GetCleanupItems())
                    {
                        item.PropertyChanged += (_, __) => Dispatcher.BeginInvoke(new Action(UpdateSummary));
                        _items.Add(item);
                    }
                }

                foreach (var item in _items)
                {
                    item.Status = "Сканирование…";
                    item.SizeBytes = 0;
                    item.FileCount = 0;
                }

                await _service.ScanAllAsync(_items, _cts.Token);
                SetPageStatus("Сканирование завершено.", NotificationKind.Success);
            }
            catch (OperationCanceledException)
            {
                SetPageStatus("Сканирование отменено.", NotificationKind.Warning);
            }
            catch (Exception ex)
            {
                SetPageStatus("Ошибка сканирования: " + ex.Message, NotificationKind.Error);
            }
            finally
            {
                _busy = false;
                SetBusy(false, PageStatusText.Text);
                UpdateSummary();
            }
        }

        private async void CleanItem_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!(sender is Button button) || !(button.Tag is DiskCleanupItem item) || item.SizeBytes <= 0) return;

            var message = item.IsDangerous
                ? $"«{item.Name}» может содержать данные для восстановления Windows. Продолжить очистку?"
                : $"Удалить найденные данные из «{item.Name}»?";

            if (Nexora.Services.StyledMessageDialog.Show(message, "Очистка диска", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await CleanItemsAsync(new[] { item });
        }

        private async void CleanSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var selected = _items.Where(i => i.IsSelected && i.SizeBytes > 0).ToList();
            if (selected.Count == 0)
            {
                SetPageStatus("Нет выбранных данных для очистки.", NotificationKind.Warning);
                return;
            }

            var total = selected.Sum(i => i.SizeBytes);
            if (Nexora.Services.StyledMessageDialog.Show(
                    $"Будет обработано элементов: {selected.Count}\nМожно освободить примерно {DiskCleanupItem.FormatBytes(total)}.\n\nПродолжить?",
                    "Очистка диска",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await CleanItemsAsync(selected);
        }

        private async Task CleanItemsAsync(System.Collections.Generic.IEnumerable<DiskCleanupItem> selected)
        {
            var scrollOffset = CleanupScrollViewer?.VerticalOffset ?? 0;
            _busy = true;
            SetBusy(true, "Очистка…");
            long freed = 0;
            try
            {
                foreach (var item in selected)
                    freed += await _service.CleanAsync(item, _cts?.Token ?? CancellationToken.None);

                SetPageStatus(
                    freed > 0
                        ? $"Освобождено {DiskCleanupItem.FormatBytes(freed)}. Очистка завершена."
                        : "Очистка завершена. Заблокированные файлы пропущены.",
                    NotificationKind.Success);

                await _service.ScanAllAsync(_items, _cts?.Token ?? CancellationToken.None);
                RestoreCleanupScrollPosition(scrollOffset);
            }
            catch (OperationCanceledException)
            {
                SetPageStatus("Очистка отменена.", NotificationKind.Warning);
            }
            catch (Exception ex)
            {
                SetPageStatus("Ошибка очистки: " + ex.Message, NotificationKind.Error);
            }
            finally
            {
                _busy = false;
                SetBusy(false, PageStatusText.Text);
                UpdateSummary();
                RestoreCleanupScrollPosition(scrollOffset);
            }
        }

        private void RestoreCleanupScrollPosition(double offset)
        {
            if (CleanupScrollViewer == null)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var maxOffset = Math.Max(0, CleanupScrollViewer.ExtentHeight - CleanupScrollViewer.ViewportHeight);
                CleanupScrollViewer.ScrollToVerticalOffset(Math.Min(offset, maxOffset));
            }), DispatcherPriority.Background);
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            var shouldSelect = _items.Any(i => i.SizeBytes > 0 && !i.IsSelected);
            foreach (var item in _items)
                item.IsSelected = shouldSelect && item.SizeBytes > 0 && !item.IsDangerous;
            UpdateSummary();
        }


        private void CleanupItemCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_busy || !(sender is Border card) || !(card.DataContext is DiskCleanupItem item) || !item.HasData)
                return;

            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is Button || source is CheckBox)
                    return;
                source = VisualTreeHelper.GetParent(source);
            }

            item.IsSelected = !item.IsSelected;
            UpdateSummary();
            e.Handled = true;
        }


        private void CleanupMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null)
                return;

            button.ContextMenu.DataContext = button.DataContext;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        private void OpenCleanupFolder_Click(object sender, RoutedEventArgs e)
        {
            DiskCleanupItem item = null;
            if (sender is Button button)
                item = button.Tag as DiskCleanupItem;
            else if (sender is MenuItem menuItem)
                item = menuItem.Tag as DiskCleanupItem;

            if (item == null)
                return;

            try
            {
                if (item.IsCommand && string.Equals(item.Id, "RecycleBin", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "shell:RecycleBinFolder",
                        UseShellExecute = true
                    });
                    return;
                }

                if (string.IsNullOrWhiteSpace(item.Path) || !Directory.Exists(item.Path))
                    throw new DirectoryNotFoundException("Папка для этого пункта не найдена.");

                Process.Start(new ProcessStartInfo
                {
                    FileName = item.Path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Nexora.Services.StyledMessageDialog.Show("Не удалось открыть папку: " + ex.Message, "Очистка диска", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetBusy(bool busy, string status)
        {
            ScanButton.IsEnabled = !busy;
            if (busy)
                SetPageStatus(status, NotificationKind.Info);
            else
                PageStatusText.Text = status;
        }

        private void SetPageStatus(string status, NotificationKind kind)
        {
            PageStatusText.Text = status;

            var background = kind == NotificationKind.Error
                ? "#32141A"
                : kind == NotificationKind.Warning
                    ? "#3A2D15"
                    : kind == NotificationKind.Success
                        ? "#10321F"
                        : "#0C1D30";

            var border = kind == NotificationKind.Error
                ? "#D34F5F"
                : kind == NotificationKind.Warning
                    ? "#D99B35"
                    : kind == NotificationKind.Success
                        ? "#4DBE78"
                        : "#21476E";

            var foreground = kind == NotificationKind.Error
                ? "#FFD6DB"
                : kind == NotificationKind.Warning
                    ? "#FFE0A3"
                    : kind == NotificationKind.Success
                        ? "#CFFFE0"
                        : "#B9CAE2";

            PageStatusHost.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
            PageStatusHost.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
            PageStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        }

        private void UpdateSummary()
        {
            var selected = _items.Where(i => i.IsSelected && i.SizeBytes > 0).ToList();
            var bytes = selected.Sum(i => i.SizeBytes);
            var count = selected.Sum(i => i.FileCount);
            SummaryText.Text = DiskCleanupItem.FormatBytes(bytes);
            SummaryDetailsText.Text = $"{selected.Count} элементов · {count:N0} файлов";
        }
    }
}
