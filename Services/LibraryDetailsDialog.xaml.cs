using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Nexora.Models;

namespace Nexora.Services
{
    public partial class LibraryDetailsDialog : Window
    {
        private LibraryDetailsDialog(LibraryItem item)
        {
            InitializeComponent();
            TitleText.Text = item?.Definition?.Name ?? "Компонент";
            BuildRows(item);
        }

        public static void Show(Window owner, LibraryItem item)
        {
            var dialog = new LibraryDetailsDialog(item);
            if (owner != null) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            dialog.ShowDialog();
        }

        private void BuildRows(LibraryItem item)
        {
            var d = item?.Definition;
            if (d == null) return;
            AddRow("Статус", item.StatusText);
            AddRow("Установленная версия", string.IsNullOrWhiteSpace(item.InstalledVersion) ? "—" : item.InstalledVersion);
            AddRow("Версия", string.IsNullOrWhiteSpace(d.Version) ? (d.VersionRule ?? "Актуальная") : d.Version);
            AddRow("Тип", d.ComponentType);
            AddRow("Категория", d.Category);
            AddRow("Архитектура", d.ArchitectureText);
            AddRow("Тип установки", d.InstallationType);
            AddRow("Назначение", d.Purpose);
            AddRow("Описание", d.Description);
            AddRow("Минимальная Windows", d.MinimumWindows);
            AddRow("Источник", d.SourceUrl);
            AddRow("Требования", Join(d.Requirements));
            AddRow("Используется для", Join(d.UsedBy));
            if (d.Warnings != null && d.Warnings.Count > 0) AddRow("Предупреждения", Join(d.Warnings), true);
        }

        private static string Join(List<string> values)
        {
            return values == null || values.Count == 0 ? "—" : string.Join("; ", values);
        }

        private void AddRow(string label, string value, bool warning = false)
        {
            var contentRow = DetailsGrid.RowDefinitions.Count;
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(label) ? "—" : label,
                Style = (Style)FindResource("Label"),
                VerticalAlignment = VerticalAlignment.Top
            };
            var valueText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                Style = (Style)FindResource("Value"),
                VerticalAlignment = VerticalAlignment.Top
            };

            if (warning)
                valueText.Foreground = new SolidColorBrush(Color.FromRgb(255, 211, 78));

            Grid.SetRow(labelText, contentRow);
            Grid.SetColumn(labelText, 0);
            Grid.SetRow(valueText, contentRow);
            Grid.SetColumn(valueText, 1);
            DetailsGrid.Children.Add(labelText);
            DetailsGrid.Children.Add(valueText);

            var lineRow = DetailsGrid.RowDefinitions.Count;
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var line = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(28, 58, 94)),
                Margin = new Thickness(0, 9, 0, 9)
            };
            Grid.SetRow(line, lineRow);
            Grid.SetColumn(line, 0);
            Grid.SetColumnSpan(line, 2);
            DetailsGrid.Children.Add(line);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The window may already be closing.
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}