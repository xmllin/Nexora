using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Nexora.Models;
using Nexora.Services;
using Nexora.Services.Apps;
using Nexora.Services.Downloads;
using Nexora.Domain.Apps;

namespace Nexora.Pages
{
    public partial class AppsPage : UserControl
    {
        private readonly MainWindow _main;
        private readonly AppRepository _repository = new AppRepository();
        private readonly AppIconResolver _iconResolver = new AppIconResolver();
        private readonly DownloadService _downloads = new DownloadService();
        private readonly List<AppDefinition> _allApps = new List<AppDefinition>();
        private string _category = "Все";
        private bool _isSelectMode = false;
        private readonly PaginationState<AppDefinition> _pagination = new PaginationState<AppDefinition>(8);
        private int _adaptivePageSize = 8;
        private bool _adaptivePageSizeInitialized;

        private Task _loadTask;
        private Task _hardwareTask;
        private readonly HashSet<AppDefinition> _selectedApps = new HashSet<AppDefinition>();
        private readonly HashSet<string> _systemRecommendationTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _showRecommended;
        private bool _updatingCategories;
        private SystemHardwareInfo _systemHardwareInfo;
        private static readonly string[] CategoryOrder =
        {
            "Все", "Браузеры", "Общение", "Игры", "Архиваторы", "Загрузки",
            "Сеть", "Безопасность", "Мультимедиа", "Система", "Драйверы",
            "Разработка", "Настройка Windows", "Устройства", "Офис", "Утилиты"
        };

        public AppsPage(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            Loaded += async (_, __) => await EnsureAppsLoadedAsync();
            Unloaded += (_, __) => ResetSelectionState();
        }

        private Task EnsureAppsLoadedAsync()
        {
            if (_repository.State == AppRepositoryState.Loaded) return Task.CompletedTask;
            if (_loadTask != null && !_loadTask.IsFaulted && !_loadTask.IsCanceled) return _loadTask;
            return _loadTask = LoadAppsAsync();
        }

        private async Task LoadAppsAsync()
        {
            LoadingText.Text = "Загрузка каталога…";
            try
            {
                var apps = await _repository.LoadAsync();
                _allApps.Clear();
                _allApps.AddRange(apps);
                ResolveExistingLogos();
                NormalizeCategories();

                // Показываем каталог сразу. Определение железа идёт независимо в фоне.
                BuildCategories();
                _systemHardwareInfo = SystemRecommendationService.LoadCachedHardwareInfo();
                _systemRecommendationTags.Clear();
                if (_systemHardwareInfo != null)
                {
                    foreach (var tag in SystemRecommendationService.GetTags(_systemHardwareInfo))
                        _systemRecommendationTags.Add(tag);
                }
                ApplyFilter();
                LoadingText.Text = "";
                await Dispatcher.InvokeAsync(
                    UpdateAdaptivePageSize,
                    System.Windows.Threading.DispatcherPriority.Loaded);

                _hardwareTask = LoadSystemRecommendationTagsAsync();
            }
            catch (Exception ex)
            {
                _loadTask = null;
                LoadingText.Text = "Не удалось загрузить каталог приложений.";
                MainWindow.Current?.ShowNotification(
                    "Не удалось загрузить каталог приложений: " + NotificationFormatter.FormatGeneralError(ex),
                    NotificationKind.Error,
                    "apps-load:" + ex.GetType().FullName);
            }
        }


        private void ResolveExistingLogos()
        {
            foreach (var app in _allApps)
                if (app != null) app.Icon = _iconResolver.Resolve(app);
        }

        private void NormalizeCategories()
        {
            foreach (var app in _allApps)
            {
                var c = (app.Category ?? "").Trim();
                if (c.Equals("Утилиты", StringComparison.OrdinalIgnoreCase)) app.Category = "Утилиты";
                else if (c.Equals("Архивы", StringComparison.OrdinalIgnoreCase) || c.Equals("Архиваторы", StringComparison.OrdinalIgnoreCase)) app.Category = "Архиваторы";
                else if (c.Equals("Мессенджеры", StringComparison.OrdinalIgnoreCase) || c.Equals("Общение", StringComparison.OrdinalIgnoreCase)) app.Category = "Общение";
                else if (c.Equals("Настройка Windows", StringComparison.OrdinalIgnoreCase)) app.Category = "Настройка Windows";
                else if (!CategoryOrder.Skip(1).Contains(c, StringComparer.OrdinalIgnoreCase)) app.Category = "Утилиты";
            }
        }

        private void BuildCategories()
        {
            _updatingCategories = true;
            CategoryComboBox.Items.Clear();
            var existing = new HashSet<string>(_allApps.Select(a => a.Category), StringComparer.OrdinalIgnoreCase);
            foreach (var category in CategoryOrder)
            {
                if (category != "Все" && !existing.Contains(category)) continue;
                var item = new ComboBoxItem
                {
                    Tag = category,
                    Content = CreateCategoryMenuContent(category)
                };
                CategoryComboBox.Items.Add(item);
            }
            CategoryComboBox.SelectedItem = CategoryComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _category, StringComparison.OrdinalIgnoreCase));
            _updatingCategories = false;
        }

        private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingCategories || !(CategoryComboBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string category))
                return;

            _category = category;
            _showRecommended = false;
            RecommendedButton.Content = "Рекомендуемые";
            UpdateRecommendedButtonVisibility();
            UpdateSystemHardwarePanel();
            ApplyFilter(true);
        }

        private static StackPanel CreateCategoryMenuContent(string category)
        {
            string iconPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                IconForCategory(category).Replace('/', System.IO.Path.DirectorySeparatorChar));

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Image
                    {
                        Source = SvgImageLoader.Load(iconPath),
                        Width = 16,
                        Height = 16,
                        Margin = new Thickness(0, 0, 8, 0)
                    },
                    new TextBlock
                    {
                        Text = category,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
        }

        private static string IconForCategory(string category)
        {
            switch (category)
            {
                case "Все": return "interface/white/fluent-apps-list.svg";
                case "Браузеры": return "interface/white/category-browser.svg";
                case "Общение": return "interface/white/fluent-person.svg";
                case "Игры": return "interface/white/category-gamepad.svg";
                case "Архиваторы": return "interface/white/fluent-folder-open.svg";
                case "Загрузки": return "interface/white/fluent-arrow-download.svg";
                case "Сеть": return "interface/white/fluent-cloud.svg";
                case "Безопасность": return "interface/white/category-shield.svg";
                case "Мультимедиа": return "interface/white/category-media.svg";
                case "Система": return "interface/white/cpu.svg";
                case "Драйверы": return "interface/white/icons8-motherboard-50.svg";
                case "Разработка": return "interface/white/fluent-app-folder.svg";
                case "Настройка Windows": return "interface/white/brand-windows.svg";
                case "Устройства": return "interface/white/icons8-storage-50.svg";
                case "Офис": return "interface/white/category-office.svg";
                case "Утилиты": return "interface/white/analyze-drive.svg";
                default: return "interface/white/fluent-app-folder.svg";
            }
        }

        private void AddCategory(string category)
        {
        }

        private static System.Windows.Media.Color CategoryIconColor(string category)
        {
            switch (category)
            {
                case "Все": return ColorFromHex("#1687F8");
                case "Браузеры": return ColorFromHex("#1677C8");
                case "Общение": return ColorFromHex("#16A77F");
                case "Игры": return ColorFromHex("#6652D7");
                case "Архиваторы": return ColorFromHex("#D66532");
                case "Загрузки": return ColorFromHex("#087EE8");
                case "Сеть": return ColorFromHex("#00A9B8");
                case "Безопасность": return ColorFromHex("#8749D6");
                case "Мультимедиа": return ColorFromHex("#BD4389");
                case "Система": return ColorFromHex("#547AA4");
                case "Драйверы": return ColorFromHex("#00A5A5");
                case "Разработка": return ColorFromHex("#7350D7");
                case "Настройка Windows": return ColorFromHex("#009DC5");
                case "Устройства": return ColorFromHex("#475BB0");
                case "Офис": return ColorFromHex("#C69A2B");
                default: return ColorFromHex("#2A71C9");
            }
        }

        private static System.Windows.Media.Color ColorFromHex(string value)
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
        }

        private void Category_Click(object sender, RoutedEventArgs e)
        {
            _category = (string)((Button)sender).Tag;
            _showRecommended = false;
            RecommendedButton.Content = "Рекомендуемые";
            BuildCategories();
            UpdateRecommendedButtonVisibility();
            UpdateSystemHardwarePanel();
            ApplyFilter(true);
        }

        private void Recommended_Click(object sender, RoutedEventArgs e)
        {
            _showRecommended = !_showRecommended;
            RecommendedButton.Content = _showRecommended
                ? "Показать все"
                : "Рекомендуемые";
            ApplyFilter(true);
        }

        private void AppsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (AppsSearchBox.Text == "Поиск программ...") return;
            ApplySearch(AppsSearchBox.Text, true);
        }

        private void AppsSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            AppsSearchBox.Foreground = new SolidColorBrush(Color.FromRgb(241, 246, 255));
            if (AppsSearchBox.Text == "Поиск программ...") AppsSearchBox.Clear();
        }

        private void AppsSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(AppsSearchBox.Text))
            {
                AppsSearchBox.Text = "Поиск программ...";
                AppsSearchBox.Foreground = new SolidColorBrush(Color.FromRgb(241, 246, 255));
            }
            else
            {
                AppsSearchBox.Foreground = new SolidColorBrush(Color.FromRgb(241, 246, 255));
            }
        }

        private void AppsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAdaptivePageSize();
        }

        private void UpdateAdaptivePageSize()
        {
            if (!IsLoaded || AppsScrollViewer == null) return;

            // Размер страницы зависит от количества столбцов карточек.
            // 1 столбец = 8, 2 = 12, 3 = 18, 4 = 20, 5+ = 25.
            // Карточка занимает 300px в WrapPanel (ItemWidth).
            const double cardWidth = 300.0;
            var availableWidth = AppsItems?.ActualWidth ?? 0;
            if (availableWidth <= 0)
                availableWidth = Math.Max(0, AppsScrollViewer.ViewportWidth - 72.0);

            var columns = Math.Max(1, (int)Math.Floor(availableWidth / cardWidth));
            columns = Math.Min(columns, 5);

            var pageSize = columns == 1 ? 8
                : columns == 2 ? 12
                : columns == 3 ? 18
                : columns == 4 ? 20
                : 25;

            if (_adaptivePageSizeInitialized && pageSize == _adaptivePageSize)
                return;

            _adaptivePageSize = pageSize;
            _adaptivePageSizeInitialized = true;
            _pagination.SetPageSize(pageSize, true);
            ApplySearch(AppsSearchBox?.Text, true);
        }

        public void ApplySearch(string text, bool resetPage = true)
        {
            var query = (text ?? "").Trim();
            if (string.Equals(query, "Поиск программ...", StringComparison.OrdinalIgnoreCase)) query = "";

            foreach (var app in _allApps)
                app.IsRecommended = IsRecommendedForSystem(app);

            IEnumerable<AppDefinition> result = _allApps.Where(a =>
                _category == "Все" || string.Equals(a.Category, _category, StringComparison.OrdinalIgnoreCase));

            if (_showRecommended)
                result = result.Where(IsRecommendedForSystem);

            if (!string.IsNullOrWhiteSpace(query))
                result = result.Where(a => MatchesSearch(a, query));

            _pagination.SetItems(result, resetPage);
            var list = _pagination.GetCurrentPageItems();
            AppsItems.ItemsSource = list;
            CountText.Text = (_category == "Все" ? "Программы" : _category) + " · " + result.Count();
            PageText.Text = _pagination.PageLabel;
            TopPageText.Text = PageText.Text;
            var hasPreviousPage = _pagination.HasPreviousPage;
            var hasNextPage = _pagination.HasNextPage;
            PreviousPageButton.Visibility = hasPreviousPage ? Visibility.Visible : Visibility.Hidden;
            TopPreviousPageButton.Visibility = hasPreviousPage ? Visibility.Visible : Visibility.Hidden;
            NextPageButton.Visibility = hasNextPage ? Visibility.Visible : Visibility.Hidden;
            TopNextPageButton.Visibility = hasNextPage ? Visibility.Visible : Visibility.Hidden;
            PreviousPageButton.IsEnabled = hasPreviousPage;
            NextPageButton.IsEnabled = hasNextPage;
            TopPreviousPageButton.IsEnabled = hasPreviousPage;
            TopNextPageButton.IsEnabled = hasNextPage;
            BottomPaginationPanel.Visibility = _pagination.HasMultiplePages ? Visibility.Visible : Visibility.Collapsed;
            if (_isSelectMode)
                Dispatcher.BeginInvoke(new Action(RestoreSelectionVisuals), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private async Task LoadSystemRecommendationTagsAsync()
        {
            SystemHardwareInfo detectedInfo = null;
            try
            {
                detectedInfo = await Task.Run(SystemRecommendationService.GetHardwareInfo);
                var tags = await Task.Run(() => SystemRecommendationService.GetTags(detectedInfo));
                var hasChanged = !SystemRecommendationService.AreHardwareInfoEqual(_systemHardwareInfo, detectedInfo);
                await Task.Run(() => SystemRecommendationService.SaveCachedHardwareInfo(detectedInfo));

                if (!hasChanged) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    _systemHardwareInfo = detectedInfo;
                    _systemRecommendationTags.Clear();
                    foreach (var tag in tags)
                        _systemRecommendationTags.Add(tag);
                });
            }
            catch
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                UpdateSystemHardwarePanel();
                UpdateRecommendedButtonVisibility();
                ApplyFilter(false);
            });
        }

        private void UpdateSystemHardwarePanel()
        {
            bool visible = _category == "Драйверы";
            SystemHardwarePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;

            if (_systemHardwareInfo == null)
            {
                SystemHardwareText.Text = "Определение оборудования…";
                RecommendedButton.IsEnabled = false;
                return;
            }

            var summary = _systemHardwareInfo.Summary;
            SystemHardwareText.Text = string.IsNullOrWhiteSpace(summary)
                ? "Оборудование не удалось определить автоматически."
                : summary;
            RecommendedButton.IsEnabled = true;
        }

        private void UpdateRecommendedButtonVisibility()
        {
            bool isDriverCategory = string.Equals(_category, "Драйверы", StringComparison.OrdinalIgnoreCase);
            RecommendedButton.Visibility = isDriverCategory && _allApps.Any(a =>
                string.Equals(a.Category, "Драйверы", StringComparison.OrdinalIgnoreCase))
                ? Visibility.Visible : Visibility.Collapsed;
            if (isDriverCategory) UpdateSystemHardwarePanel();
            else SystemHardwarePanel.Visibility = Visibility.Collapsed;
        }

        private bool IsRecommendedForSystem(AppDefinition app)
        {
            if (app == null || !string.Equals(app.Category, "Драйверы", StringComparison.OrdinalIgnoreCase))
                return false;
            return app.RecommendationTags != null && app.RecommendationTags.Any(tag => _systemRecommendationTags.Contains(tag));
        }

        private static string CleanCpuName(string value)
        {
            return SystemRecommendationService.CleanCpuName(value);
        }

        private void ApplyFilter(bool resetPage = true) => ApplySearch(AppsSearchBox?.Text, resetPage);

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pagination.MovePrevious()) ApplySearch(AppsSearchBox?.Text, false);
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pagination.MoveNext()) ApplySearch(AppsSearchBox?.Text, false);
        }

        private static bool MatchesSearch(AppDefinition app, string query)
        {
            if (app == null) return false;

            var normalizedQuery = NormalizeSearchText(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery)) return true;

            var searchable = NormalizeSearchText(string.Join(" ", new[]
            {
                app.Id,
                app.Name,
                app.Description,
                app.LongDescription,
                app.Category,
                app.Website,
                app.Download?.Type,
                app.Download?.Repository,
                app.Download?.Owner,
                app.Download?.AssetPattern,
                app.Download?.FileName,
                app.Tags == null ? string.Empty : string.Join(" ", app.Tags)
            }));

            return normalizedQuery.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .All(searchTerm => searchable.Contains(searchTerm));
        }

        private static string NormalizeSearchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var chars = value.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]))
                    chars[i] = ' ';
            }

            return new string(chars);
        }

        private void DownloadButton_Loaded(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var app = button == null ? null : button.Tag as AppDefinition;
            if (button == null || app == null) return;

            var panel = button.Content as StackPanel;
            if (panel == null) return;
            foreach (var child in panel.Children)
            {
                var text = child as TextBlock;
                if (text != null)
                {
                    text.Text = "Скачать";
                    break;
                }
            }
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is AppDefinition app)) return;

            var old = button.Content;
            button.Content = "Загрузка…";
            try
            {
                await DownloadAppAsync(app);
            }
            finally
            {
                button.Content = old;
            }
        }

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AppDefinition app)
            {
                _main.SelectedApp = app;
                HomePage.AddRecentApp(app);
                _main.NavigateToAppDetails("apps");
            }
        }

        private void Website_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            if (Uri.TryCreate(e.Uri?.ToString(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }

            e.Handled = true;
        }

        private void AppNameText_Loaded(object sender, RoutedEventArgs e)
        {
            ResetAppNameAnimation(sender as TextBlock);
        }

        private void AppNameText_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ResetAppNameAnimation(sender as TextBlock);
        }

        private void AppNameViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!(sender is FrameworkElement viewport))
                return;

            var textBlock = FindChild<TextBlock>(viewport, "AppNameText");
            ResetAppNameAnimation(textBlock);

            if (viewport.IsMouseOver)
                StartAppNameAnimation(textBlock);
        }

        private void AppNameViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            var outer = FindVisualParent<ScrollViewer>(sender as DependencyObject);
            if (outer == null) return;

            var targetOffset = outer.VerticalOffset - e.Delta;
            targetOffset = Math.Max(0, Math.Min(targetOffset, outer.ScrollableHeight));
            outer.ScrollToVerticalOffset(targetOffset);
            e.Handled = true;
        }

        private void AppCard_MouseEnter(object sender, MouseEventArgs e)
        {
            var card = sender as Border;
            StartAppNameAnimation(FindChild<TextBlock>(card, "AppNameText"));
        }

        private void AppCard_MouseLeave(object sender, MouseEventArgs e)
        {
            ResetAppNameAnimation(FindChild<TextBlock>(sender as Border, "AppNameText"));
        }

        private static void ResetAppNameAnimation(TextBlock textBlock)
        {
            if (textBlock == null) return;
            var transform = textBlock.RenderTransform as TranslateTransform;
            if (transform == null) return;
            if (transform.IsFrozen)
            {
                transform = transform.Clone();
                textBlock.RenderTransform = transform;
            }
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        }

        private void StartAppNameAnimation(TextBlock textBlock)
        {
            var viewport = FindVisualParent<FrameworkElement>(textBlock);
            if (textBlock == null || viewport == null) return;
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var fullTextWidth = textBlock.DesiredSize.Width;

            var viewportWidth = Math.Max(0, viewport.ActualWidth);
            var overflow = fullTextWidth - viewportWidth;
            var transform = textBlock.RenderTransform as TranslateTransform;
            if (transform == null) return;

            if (transform.IsFrozen)
            {
                transform = transform.Clone();
                textBlock.RenderTransform = transform;
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            if (overflow <= 2) return;

            const double pixelsPerSecond = 32;
            const double minimumDurationSeconds = 0.8;
            var animation = new DoubleAnimation
            {
                From = 0,
                To = -overflow,
                BeginTime = TimeSpan.FromSeconds(0.5),
                Duration = new Duration(TimeSpan.FromSeconds(Math.Max(minimumDurationSeconds, overflow / pixelsPerSecond))),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void AppCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Border card) || !(card.DataContext is AppDefinition app)) return;
            if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) != null ||
                FindVisualParent<CheckBox>(e.OriginalSource as DependencyObject) != null)
                return;

            if (!_isSelectMode)
            {
                _isSelectMode = true;
            }
            ToggleAppSelection(app);
            e.Handled = true;
        }

        private void ToggleAppSelection(AppDefinition app)
        {
            if (_selectedApps.Contains(app))
                _selectedApps.Remove(app);
            else
                _selectedApps.Add(app);

            UpdateSelectionActions();
            RestoreSelectionVisuals();
        }

        private void UpdateSelectionActions()
        {
            var hasSelection = _selectedApps.Count > 0;
            DownloadSelectedButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Hidden;
            CancelSelectButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Hidden;
        }

        private void RestoreSelectionVisuals()
        {
            foreach (var container in GetVisualChildren<Border>(AppsItems))
            {
                var checkbox = FindChild<CheckBox>(container, "SelectCheckBox");
                if (checkbox?.Tag is AppDefinition app)
                {
                    container.Tag = _selectedApps.Contains(app) ? "Selected" : null;
                    checkbox.Visibility = Visibility.Collapsed;
                    checkbox.IsChecked = _selectedApps.Contains(app);
                }
            }
        }

        public void ResetSelectionState()
        {
            CancelSelect_Click(null, null);
        }

        private void CancelSelect_Click(object sender, RoutedEventArgs e)
        {
            _isSelectMode = false;
            _selectedApps.Clear();
            UpdateSelectionActions();

            foreach (var container in GetVisualChildren<Border>(AppsItems))
            {
                var checkbox = FindChild<CheckBox>(container, "SelectCheckBox");
                if (checkbox != null)
                {
                    container.Tag = null;
                    checkbox.Visibility = Visibility.Collapsed;
                    checkbox.IsChecked = false;
                }
            }
        }

        private void AppCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkbox && checkbox.Tag is AppDefinition app)
            {
                _selectedApps.Add(app);
                RestoreSelectionVisuals();
            }
        }

        private void AppCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkbox && checkbox.Tag is AppDefinition app)
            {
                _selectedApps.Remove(app);
                RestoreSelectionVisuals();
            }
        }

        private T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = child == null ? null : System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T typedParent) return typedParent;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }

            return null;
        }

        private async void DownloadSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApps.Count == 0) return;

            var appsToDownload = _selectedApps.ToList();
            CancelSelect_Click(null, null);

            _main.BeginDownloadBatch(appsToDownload.Count, appsToDownload.Select(app => app.Name));
            var downloadTasks = appsToDownload.Select(DownloadAppAsync).ToList();
            _main.EndDownloadBatch();
            await Task.WhenAll(downloadTasks);
        }

        private async Task DownloadAppAsync(AppDefinition app)
        {
            if (app == null) return;

            var downloadKey = (app.Id ?? app.Name ?? "download").ToLowerInvariant();
            CancellationTokenSource cts = null;
            PauseController pauseController = null;
            try
            {
                if (MainWindow.Current == null ||
                    !MainWindow.Current.TryRegisterDownload(app.Name, downloadKey, out cts, out pauseController))
                    return;

                var progress = new Progress<DownloadProgress>(details =>
                    MainWindow.Current?.UpdateDownloadProgress(downloadKey, details, app.Name));

                await _downloads.DownloadAsync(app, progress, cts.Token, pauseController);
                MainWindow.Current?.CompleteDownload(downloadKey, app.Name);
            }
            catch (OperationCanceledException)
            {
                MainWindow.Current?.NotifyDownloadCancelled(downloadKey, app.Name);
                MainWindow.Current?.RemoveDownload(downloadKey);
            }
            catch (Exception ex)
            {
                MainWindow.Current?.NotifyDownloadError(downloadKey, app.Name, ex);
                MainWindow.Current?.RemoveDownload(downloadKey);
            }
        }

        private T FindChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild)
                {
                    if (childName == "" || (child is FrameworkElement fe && fe.Name == childName))
                        return typedChild;
                }

                var result = FindChild<T>(child, childName);
                if (result != null) return result;
            }

            return null;
        }

        private IEnumerable<T> GetVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    yield return typedChild;

                foreach (var subChild in GetVisualChildren<T>(child))
                    yield return subChild;
            }
        }
    }
}
