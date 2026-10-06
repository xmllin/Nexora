using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nexora.Models;

namespace Nexora.Services.Libraries
{
    public sealed class LibraryInstallationService
    {
        public async Task InstallAsync(LibraryDefinition definition, string installerPath, CancellationToken token)
        {
            if (definition == null || string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath)) throw new InvalidOperationException("Установщик компонента не найден.");
            var startInfo = new ProcessStartInfo { FileName = installerPath, Arguments = definition.SilentArguments ?? string.Empty, UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(installerPath) };
            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить установщик.");
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0 && process.ExitCode != 3010 && process.ExitCode != 1638)
                    throw new InvalidOperationException("Установщик завершился с кодом " + process.ExitCode + ".");
            }
        }

        public async Task InstallWindowsFeatureAsync(LibraryDefinition definition, CancellationToken token)
        {
            await RunPowerShellAsync("Enable-WindowsOptionalFeature -Online -FeatureName NetFx3 -All -NoRestart -ErrorAction Stop", token);
        }

        public async Task UninstallWindowsFeatureAsync(LibraryDefinition definition, CancellationToken token)
        {
            await RunPowerShellAsync("Disable-WindowsOptionalFeature -Online -FeatureName NetFx3 -NoRestart -ErrorAction Stop", token);
        }

        public async Task<string> SetPowerShellScriptsEnabledAsync(bool enabled, CancellationToken token)
        {
            var policy = enabled ? "RemoteSigned" : "Restricted";
            var systemFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(systemFolder, "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy " + policy + " -Force\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить Windows PowerShell.");
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    if (error.IndexOf("ExecutionPolicyOverride", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        error.IndexOf("PermissionDenied", StringComparison.OrdinalIgnoreCase) >= 0)
                        return null;
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "PowerShell вернул ошибку." : error.Trim());
                }
                return policy;
            }
        }

        public async Task<string> GetPowerShellScriptsPolicyAsync(CancellationToken token)
        {
            var systemFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(systemFolder, "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"(Get-ExecutionPolicy -Scope CurrentUser)\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить Windows PowerShell.");
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(token);
                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : "Unknown";
            }
        }

        private static async Task RunPowerShellAsync(string command, CancellationToken token)
        {
            var systemFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(systemFolder, "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; " + command + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = systemFolder
            };
            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить Windows PowerShell.");
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Операция Windows завершилась с кодом " + process.ExitCode + ".");
            }
        }

        public async Task UninstallAsync(LibraryDefinition definition, CancellationToken token)
        {
            if (definition == null) throw new InvalidOperationException("Компонент не указан.");

            var uninstallCommand = FindUninstallCommand(definition);
            if (string.IsNullOrWhiteSpace(uninstallCommand))
                throw new InvalidOperationException("Для этого компонента не найдено корректного удаления из системы.");

            using (var process = Process.Start(CreateUninstallStartInfo(uninstallCommand)))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить удаление компонента.");
                _ = AutomateMaintenanceWindowAsync(process, MaintenanceAction.Uninstall, token);
                await process.WaitForExitAsync(token);
                if (process.ExitCode == 1602) throw new OperationCanceledException();
                if (process.ExitCode != 0) throw new InvalidOperationException("Удаление завершилось с кодом " + process.ExitCode + ".");
            }
        }

        public async Task RepairAsync(LibraryDefinition definition, CancellationToken token)
        {
            if (definition == null) throw new InvalidOperationException("Компонент не указан.");

            var repairCommand = FindRepairCommand(definition);
            if (string.IsNullOrWhiteSpace(repairCommand))
                throw new InvalidOperationException("Для этого компонента не найдена программа восстановления.");

            using (var process = Process.Start(CreateUninstallStartInfo(repairCommand)))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить восстановление компонента.");
                _ = AutomateMaintenanceWindowAsync(process, MaintenanceAction.Repair, token);
                await process.WaitForExitAsync(token);
                if (process.ExitCode == 1602) throw new OperationCanceledException();
                if (process.ExitCode != 0) throw new InvalidOperationException("Восстановление завершилось с кодом " + process.ExitCode + ".");
            }
        }

        private enum MaintenanceAction
        {
            Uninstall,
            Repair
        }

        private static async Task AutomateMaintenanceWindowAsync(Process process, MaintenanceAction action, CancellationToken token)
        {
            for (var attempt = 0; attempt < 120 && !process.HasExited; attempt++)
            {
                token.ThrowIfCancellationRequested();
                foreach (var window in GetProcessWindows(process.Id))
                {
                    if (action == MaintenanceAction.Uninstall)
                    {
                        if (TryClickButton(window, "Uninstall") || TryClickButton(window, "Удалить") ||
                            TryClickButton(window, "Remove") || TryClickButton(window, "Удаление"))
                            return;
                    }
                    else
                    {
                        if (TryClickButton(window, "Repair") || TryClickButton(window, "Восстановить"))
                        {
                            await Task.Delay(180, token);
                            TryClickButton(window, "Next");
                            TryClickButton(window, "Далее");
                            TryClickButton(window, "Repair");
                            TryClickButton(window, "Восстановить");
                            return;
                        }

                        if (TryClickControl(window, "Repair") || TryClickControl(window, "Восстановить"))
                        {
                            await Task.Delay(180, token);
                            TryClickButton(window, "Next");
                            TryClickButton(window, "Далее");
                            TryClickButton(window, "Repair");
                            TryClickButton(window, "Восстановить");
                            return;
                        }
                    }
                }

                await Task.Delay(250, token);
            }
        }

        private static IEnumerable<IntPtr> GetProcessWindows(int processId)
        {
            var windows = new List<IntPtr>();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if ((int)pid == processId && IsWindowVisible(window))
                    windows.Add(window);
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        private static bool TryClickButton(IntPtr root, string text)
        {
            var control = FindChildByText(root, text, "Button");
            if (control == IntPtr.Zero) return false;
            SendMessage(control, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        private static bool TryClickControl(IntPtr root, string text)
        {
            var control = FindChildByText(root, text, null);
            return control != IntPtr.Zero && SendMessage(control, BM_CLICK, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;
        }

        private static IntPtr FindChildByText(IntPtr root, string text, string className)
        {
            IntPtr result = IntPtr.Zero;
            EnumChildWindows(root, (window, _) =>
            {
                var value = GetWindowText(window);
                var classValue = GetClassName(window);
                if (!string.IsNullOrWhiteSpace(value) &&
                    string.Equals(value.Trim(), text, StringComparison.OrdinalIgnoreCase) &&
                    (className == null || string.Equals(classValue, className, StringComparison.OrdinalIgnoreCase)))
                {
                    result = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static string GetWindowText(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0) return string.Empty;
            var buffer = new System.Text.StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string GetClassName(IntPtr window)
        {
            var buffer = new System.Text.StringBuilder(128);
            GetClassName(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private const uint BM_CLICK = 0x00F5;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        public string FindInstallLocation(LibraryDefinition definition)
        {
            if (definition == null) return string.Empty;
            var names = BuildDisplayNameCandidates(definition);

            foreach (var root in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, root))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) continue;
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var item = key.OpenSubKey(subKeyName))
                        {
                            var displayName = item?.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName) || !names.Any(name => displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                            var location = item.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrWhiteSpace(location))
                            {
                                location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
                                if (Directory.Exists(location)) return location;
                            }

                            var displayIcon = item.GetValue("DisplayIcon") as string;
                            var iconPath = ExtractExecutablePath(displayIcon);
                            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                                return Path.GetDirectoryName(iconPath);
                        }
                    }
                }
            }

            return string.Empty;
        }

        private static ProcessStartInfo CreateUninstallStartInfo(string uninstallCommand)
        {
            var command = uninstallCommand.Trim();
            string fileName;
            string arguments;

            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                var closingQuote = command.IndexOf('"', 1);
                if (closingQuote < 0) throw new InvalidOperationException("Некорректная команда удаления компонента.");
                fileName = command.Substring(1, closingQuote - 1);
                arguments = command.Substring(closingQuote + 1).Trim();
            }
            else
            {
                SplitExecutableAndArguments(command, out fileName, out arguments);
            }

            if (Path.GetFileNameWithoutExtension(fileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                arguments = NormalizeMsiUninstallArguments(arguments);

            return new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
        }

        private static void SplitExecutableAndArguments(string command, out string fileName, out string arguments)
        {
            fileName = command.Trim();
            arguments = string.Empty;

            foreach (var extension in new[] { ".exe", ".com", ".bat", ".cmd" })
            {
                var searchStart = 0;
                while (searchStart < command.Length)
                {
                    var index = command.IndexOf(extension, searchStart, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;

                    var end = index + extension.Length;
                    if (end == command.Length || char.IsWhiteSpace(command[end]))
                    {
                        fileName = command.Substring(0, end).Trim().Trim('"');
                        arguments = end < command.Length ? command.Substring(end).Trim() : string.Empty;
                        return;
                    }

                    searchStart = end;
                }
            }

            var separator = command.IndexOf(' ');
            if (separator >= 0)
            {
                fileName = command.Substring(0, separator);
                arguments = command.Substring(separator + 1).Trim();
            }
        }

        private static string NormalizeMsiUninstallArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return arguments;
            var normalized = arguments.Trim();
            if (normalized.StartsWith("/I", StringComparison.OrdinalIgnoreCase))
                return "/X" + normalized.Substring(2);
            if (normalized.StartsWith("/package", StringComparison.OrdinalIgnoreCase))
                return "/X" + normalized.Substring("/package".Length);
            return normalized;
        }

        private static string NormalizeRepairCommand(string command)
        {
            var normalized = command.Trim();
            string fileName;
            string arguments;
            if (normalized.StartsWith("\"", StringComparison.Ordinal))
            {
                var closingQuote = normalized.IndexOf('"', 1);
                if (closingQuote < 0) return normalized;
                fileName = normalized.Substring(1, closingQuote - 1);
                arguments = normalized.Substring(closingQuote + 1).Trim();
            }
            else
            {
                SplitExecutableAndArguments(normalized, out fileName, out arguments);
            }

            if (Path.GetFileNameWithoutExtension(fileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                var brace = arguments.IndexOf('{');
                var productCode = brace >= 0 ? arguments.Substring(brace) : arguments;
                return fileName + " /fa " + productCode;
            }

            if (arguments.IndexOf("/repair", StringComparison.OrdinalIgnoreCase) >= 0)
                return normalized;
            if (arguments.IndexOf("/modify", StringComparison.OrdinalIgnoreCase) >= 0)
                arguments = System.Text.RegularExpressions.Regex.Replace(arguments, "/modify", "/repair", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            else if (arguments.IndexOf("/uninstall", StringComparison.OrdinalIgnoreCase) >= 0)
                arguments = System.Text.RegularExpressions.Regex.Replace(arguments, "/uninstall", "/repair", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            else
                arguments = (arguments + " /repair").Trim();

            return "\"" + fileName + "\" " + arguments;
        }

        private static string ExtractExecutablePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var path = value.Trim().Trim('"');
            var comma = path.LastIndexOf(',');
            if (comma > 0) path = path.Substring(0, comma).Trim().Trim('"');
            return Environment.ExpandEnvironmentVariables(path);
        }

        private static string FindUninstallCommand(LibraryDefinition definition)
        {
            var names = BuildDisplayNameCandidates(definition);

            foreach (var root in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, root))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) continue;
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var item = key.OpenSubKey(subKeyName))
                        {
                            if (item == null) continue;
                            var displayName = item.GetValue("DisplayName") as string;
                            var uninstallString = item.GetValue("UninstallString") as string;
                            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(uninstallString)) continue;
                            if (string.Equals(definition.Category, "Visual C++ Redistributable", StringComparison.OrdinalIgnoreCase) &&
                                displayName.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            if (names.Any(name => displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                                return uninstallString;
                        }
                    }
                }
            }

            return string.Empty;
        }

        private static string FindRepairCommand(LibraryDefinition definition)
        {
            var names = BuildDisplayNameCandidates(definition);

            foreach (var root in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, root))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) continue;
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var item = key.OpenSubKey(subKeyName))
                        {
                            if (item == null) continue;
                            var displayName = item.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName) || !names.Any(name => displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                            if (string.Equals(definition.Category, "Visual C++ Redistributable", StringComparison.OrdinalIgnoreCase) &&
                                displayName.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            var modifyString = item.GetValue("ModifyString") as string;
                            var modifyPath = item.GetValue("ModifyPath") as string;
                            var maintenanceCommand = !string.IsNullOrWhiteSpace(modifyString) ? modifyString : modifyPath;
                            if (!string.IsNullOrWhiteSpace(maintenanceCommand)) return NormalizeRepairCommand(maintenanceCommand);

                            var repairString = item.GetValue("RepairString") as string;
                            if (!string.IsNullOrWhiteSpace(repairString)) return NormalizeRepairCommand(repairString);
                        }
                    }
                }
            }

            return string.Empty;
        }

        private static string[] BuildDisplayNameCandidates(LibraryDefinition definition)
        {
            return new[]
            {
                definition.DetectionValue,
                definition.Name,
                definition.Name?.Replace("Microsoft Visual C++ ", "Microsoft Visual C++ Redistributable "),
                definition.Name?.Replace("Microsoft Visual C++ ", "Microsoft Visual C++ Redistributable " + (definition.Version ?? string.Empty) + " "),
                string.IsNullOrWhiteSpace(definition.Version) ? null : "Microsoft Visual C++ " + definition.Version + " Redistributable",
                string.IsNullOrWhiteSpace(definition.Version) ? null : "Microsoft Visual C++ " + definition.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
