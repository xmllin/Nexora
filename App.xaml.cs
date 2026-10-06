using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nexora.Services;
using Nexora.Services.Apps;
using Nexora.Services.Downloads;

namespace Nexora
{
    public partial class App : System.Windows.Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RuntimeImageCache.Initialize();

            FrameworkElement.FocusVisualStyleProperty.OverrideMetadata(
                typeof(Control),
                new FrameworkPropertyMetadata(null));

            EventManager.RegisterClassHandler(
                typeof(Control),
                Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(SuppressKeyboardFocusVisual),
                true);

            EventManager.RegisterClassHandler(
                typeof(Button),
                Mouse.QueryCursorEvent,
                new QueryCursorEventHandler(HandleButtonQueryCursor),
                true);

            EventManager.RegisterClassHandler(
                typeof(Window),
                Mouse.PreviewMouseMoveEvent,
                new MouseEventHandler(HandleWindowPreviewMouseMove),
                true);

            if (e.Args.Any(arg => string.Equals(arg, "--diagnose-catalog", StringComparison.OrdinalIgnoreCase)))
            {
                await RunCatalogDiagnosticsAsync();
                Shutdown(_diagnosticExitCode);
                return;
            }

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        private static void HandleWindowPreviewMouseMove(object sender, MouseEventArgs e)
        {
            var over = Mouse.DirectlyOver as DependencyObject;
            var disabledButton = FindDisabledButton(over);

            if (disabledButton != null)
                Mouse.OverrideCursor = Cursors.No;
            else if (Mouse.OverrideCursor == Cursors.No)
                Mouse.OverrideCursor = null;
        }

        private static Button FindDisabledButton(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is Button button)
                    return button.IsEnabled ? null : button;

                current = current is Visual visual
                    ? VisualTreeHelper.GetParent(visual)
                    : (current as FrameworkContentElement)?.Parent;
            }

            return null;
        }

        private static void HandleButtonQueryCursor(object sender, QueryCursorEventArgs e)
        {
            if (sender is Button button && !button.IsEnabled)
            {
                e.Cursor = Cursors.No;
                e.Handled = true;
            }
        }

        private static void SuppressKeyboardFocusVisual(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is Control control)
                control.FocusVisualStyle = null;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            RuntimeImageCache.Clear();
            base.OnExit(e);
        }

        private int _diagnosticExitCode;

        private async Task RunCatalogDiagnosticsAsync()
        {
            try
            {
                var repository = new AppRepository();
                var apps = await repository.LoadAsync();
                var service = new CatalogDiagnosticService();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
                {
                    var results = await service.ProbeAllAsync(apps, timeout.Token, 4);
                    var failed = results.Count(result => !result.Success);
                    var report = new
                    {
                        GeneratedAtUtc = DateTime.UtcNow,
                        Platform = PlatformDetectionService.Current,
                        Total = results.Count,
                        Success = results.Count - failed,
                        Failed = failed,
                        Results = results
                    };

                    var reportPath = UserDataPath.File("catalog-diagnostic.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    _diagnosticExitCode = failed == 0 && results.Count > 0 ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                var reportPath = UserDataPath.File("catalog-diagnostic-error.txt");
                try { File.WriteAllText(reportPath, ex.ToString()); } catch { }
                _diagnosticExitCode = 2;
            }
        }
    }
}
