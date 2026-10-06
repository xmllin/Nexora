using System;
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

            var rows = new List<Tuple<string, string, bool>>
            {
                Tuple.Create("Статус", item.StatusText, false),
                Tuple.Create("Установленная версия", string.IsNullOrWhiteSpace(item.InstalledVersion) ? "—" : item.InstalledVersion, false),
                Tuple.Create("Версия", string.IsNullOrWhiteSpace(d.Version) ? (d.VersionRule ?? "Актуальная") : d.Version, false),
                Tuple.Create("Тип", d.ComponentType, false),
                Tuple.Create("Категория", d.Category, false),
                Tuple.Create("Архитектура", d.ArchitectureText, false),
                Tuple.Create("Назначение", d.Purpose, false),
                Tuple.Create("Описание", d.Description, false),
                Tuple.Create("Источник", d.SourceUrl, false),
                Tuple.Create("Требования", Join(d.Requirements), false),
                Tuple.Create("Используется для", Join(d.UsedBy), false)
            };

            if (d.Warnings != null && d.Warnings.Count > 0)
                rows.Add(Tuple.Create("Предупреждения", Join(d.Warnings), true));

            for (var i = 0; i < rows.Count; i++)
                AddRow(rows[i].Item1, rows[i].Item2, rows[i].Item3, i < rows.Count - 1);
        }

        private static string Join(List<string> values)
        {
            return values == null || values.Count == 0 ? "—" : string.Join("; ", values);
        }

        private void AddRow(string label, string value, bool warning, bool showSeparator)
        {
            var contentRow = DetailsGrid.RowDefinitions.Count;
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(label) ? "—" : label,
                Style = (Style)FindResource("Label"),
                VerticalAlignment = VerticalAlignment.Top
            };

            var valueText = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                Style = (Style)FindResource("ValueBox"),
                VerticalAlignment = VerticalAlignment.Top
            };
            valueText.PreviewMouseMove += ValueText_PreviewMouseMove;
            valueText.MouseLeave += ValueText_MouseLeave;

            if (warning)
                valueText.Foreground = new SolidColorBrush(Color.FromRgb(255, 211, 78));

            Grid.SetRow(labelText, contentRow);
            Grid.SetColumn(labelText, 0);
            Grid.SetRow(valueText, contentRow);
            Grid.SetColumn(valueText, 1);
            DetailsGrid.Children.Add(labelText);
            DetailsGrid.Children.Add(valueText);

            if (!showSeparator)
                return;

            var lineRow = DetailsGrid.RowDefinitions.Count;
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Pixel) });

            var line = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(28, 58, 94)),
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetRow(line, lineRow);
            Grid.SetColumn(line, 0);
            Grid.SetColumnSpan(line, 2);
            DetailsGrid.Children.Add(line);
        }

        private void ValueText_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!(sender is TextBox box))
                return;

            var point = e.GetPosition(box);
            var index = box.GetCharacterIndexFromPoint(point, false);
            box.Cursor = index >= 0 ? Cursors.IBeam : Cursors.Arrow;
        }

        private void ValueText_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is TextBox box)
                box.Cursor = Cursors.Arrow;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
                return;

            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is Button)
                    return;
                source = source is Visual
                    ? VisualTreeHelper.GetParent(source)
                    : (source as FrameworkContentElement)?.Parent;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Hand;
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The window may already be closing.
            }
            finally
            {
                Mouse.OverrideCursor = null;
                DragHeader.Cursor = Cursors.Arrow;
            }

            e.Handled = true;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}