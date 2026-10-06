using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                Mouse.MouseEnterEvent,
                new MouseEventHandler(HandleButtonMouseEnter),
                true);

            EventManager.RegisterClassHandler(
                typeof(Button),
                Mouse.MouseLeaveEvent,
                new MouseEventHandler(HandleButtonMouseLeave),
                true);

            EventManager.RegisterClassHandler(
                typeof(Button),
                Button.IsEnabledChangedEvent,
                new DependencyPropertyChangedEventHandler(HandleButtonEnabledChanged),
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

        private static void HandleButtonMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Button button && !button.IsEnabled)
                Mouse.OverrideCursor = Cursors.No;
        }

        private static void HandleButtonMouseLeave(object sender, MouseEventArgs e)
        {
            if (Mouse.OverrideCursor == Cursors.No)
                Mouse.OverrideCursor = null;
        }

        private static void HandleButtonEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is Button button))
                return;

            if (!button.IsEnabled)
            {
                var position = Mouse.GetPosition(button);
                if (position.X >= 0 && position.Y >= 0 &&
                    position.X <= button.ActualWidth && position.Y <= button.ActualHeight)
                    Mouse.OverrideCursor = Cursors.No;
            }
            else if (Mouse.OverrideCursor == Cursors.No)
            {
                Mouse.OverrideCursor = null;
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
