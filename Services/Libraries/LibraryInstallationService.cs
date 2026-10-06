using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                if (process.ExitCode == 1638)
                {
                    // MSI: another version is already installed. The caller will
                    // run component detection and keep the installed version.
                    return;
                }

                // 1602/1223 are normal user-cancel results. Do not surface them
                // as an installation error notification.
                if (process.ExitCode == 1602 || process.ExitCode == 1223)
                    throw new OperationCanceledException();

                if (process.ExitCode != 0 && process.ExitCode != 3010)
                    throw new InvalidOperationException("Установщик завершился с кодом " + process.ExitCode + ".");
            }
        }

        public string GetCachedInstallerPath(LibraryDefinition definition)
        {
            if (definition == null) return string.Empty;

            var folder = Path.Combine(Path.GetTempPath(), "Nexora", "Libraries");
            var candidates = new[]
            {
                definition.FileName,
                definition.Id + ".exe",
                definition.Id + ".msi"
            };

            foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var path = Path.Combine(folder, candidate);
                if (File.Exists(path)) return path;
            }

            return string.Empty;
        }

        public async Task LaunchInstallerAsync(LibraryDefinition definition, string installerPath, CancellationToken token)
        {
            if (definition == null || string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
                throw new InvalidOperationException("Файл установщика не найден.");

            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installerPath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.System)
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("Не удалось открыть установщик.");
                await process.WaitForExitAsync(token);

                if (process.ExitCode == 1602 || process.ExitCode == 1223)
                    throw new OperationCanceledException();

                if (process.ExitCode != 0 && process.ExitCode != 3010 && process.ExitCode != 1638)
                    throw new InvalidOperationException("Установщик завершился с кодом " + process.ExitCode + ".");
            }
        }


        public async Task InstallWindowsFeatureAsync(LibraryDefinition definition, CancellationToken token)
        {
            var exitCode = await RunHiddenPowerShellAsync(
                "$ErrorActionPreference='Stop'; Enable-WindowsOptionalFeature -Online -FeatureName NetFx3 -All -NoRestart -ErrorAction Stop",
                token);
            if (exitCode != 0)
                throw new InvalidOperationException("Операция Windows завершилась с кодом " + exitCode + ".");
        }

        public async Task UninstallWindowsFeatureAsync(LibraryDefinition definition, CancellationToken token)
        {
            var exitCode = await RunHiddenPowerShellAsync(
                "$ErrorActionPreference='Stop'; Disable-WindowsOptionalFeature -Online -FeatureName NetFx3 -NoRestart -ErrorAction Stop",
                token);
            if (exitCode != 0)
                throw new InvalidOperationException("Операция Windows завершилась с кодом " + exitCode + ".");
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


        public async Task UninstallAsync(LibraryDefinition definition, CancellationToken token)
        {
            if (definition == null)
                throw new InvalidOperationException("Компонент не указан.");

            var names = BuildDisplayNameCandidates(definition);
            if (names.Length == 0)
                throw new InvalidOperationException("Не удалось определить название компонента для удаления.");

            var script = BuildHiddenUninstallScript(names, definition.Category);
            var exitCode = await RunHiddenPowerShellAsync(script, token);

            if (exitCode == 1602 || exitCode == 1223)
                throw new OperationCanceledException();

            if (exitCode == 3010)
                return;

            if (exitCode != 0)
                throw new InvalidOperationException("Удаление завершилось с кодом " + exitCode + ".");
        }

        private static async Task<int> RunHiddenPowerShellAsync(string script, CancellationToken token)
        {
            var systemFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(systemFolder, "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encodedCommand,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = systemFolder
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("Не удалось запустить PowerShell для удаления компонента.");

                await process.WaitForExitAsync(token);
                return process.ExitCode;
            }
        }

        private static string BuildHiddenUninstallScript(string[] names, string category)
        {
            var nameLiterals = string.Join(", ",
                names.Select(name => "'" + EscapePowerShellSingleQuoted(name) + "'"));
            var categoryLiteral = EscapePowerShellSingleQuoted(category ?? string.Empty);

            const string template = """
$ErrorActionPreference = 'Stop'
$names = @({{NAMES}})
$category = '{{CATEGORY}}'
$roots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

$target = $null
foreach ($root in $roots) {
    try {
        $items = @(Get-ItemProperty -Path $root -ErrorAction SilentlyContinue)
        foreach ($item in $items) {
            $displayName = [string]$item.DisplayName
            if ([string]::IsNullOrWhiteSpace($displayName)) { continue }

            if ($category -eq 'Visual C++ Redistributable' -and
                $displayName.IndexOf('Redistributable', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }

            $matched = $false
            foreach ($candidate in $names) {
                if ($displayName.IndexOf($candidate, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $matched = $true
                    break
                }
            }

            if ($matched) {
                $target = $item
                break
            }
        }
    } catch {}

    if ($null -ne $target) { break }
}

if ($null -eq $target) {
    throw 'В реестре Windows не найден компонент для удаления.'
}

$command = [string]$target.QuietUninstallString
if ([string]::IsNullOrWhiteSpace($command)) {
    $command = [string]$target.UninstallString
}
if ([string]::IsNullOrWhiteSpace($command)) {
    throw 'Для компонента не найдена команда удаления.'
}

$guidMatch = [regex]::Match($command, '(?i)\{[0-9a-f-]{36}\}')
if ($guidMatch.Success -and $command -match '(?i)(^|[\s\\/])msiexec(?:\.exe)?([\s]|$)') {
    $p = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/x', $guidMatch.Value, '/qn', '/norestart') -Wait -PassThru -WindowStyle Hidden
    exit $p.ExitCode
}

if ($command -match '^\s*"([^"]+)"\s*(.*)$') {
    $file = $Matches[1]
    $args = $Matches[2]
}
else {
    $parts = $command.Trim() -split '\s+', 2
    $file = $parts[0]
    $args = if ($parts.Count -gt 1) { $parts[1] } else { '' }
}

$file = [Environment]::ExpandEnvironmentVariables($file)
if (-not (Test-Path -LiteralPath $file)) {
    throw "Файл удаления не найден: $file"
}

$p = Start-Process -FilePath $file -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
exit $p.ExitCode
""";

            return template
                .Replace("{{NAMES}}", nameLiterals)
                .Replace("{{CATEGORY}}", categoryLiteral);
        }

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
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
