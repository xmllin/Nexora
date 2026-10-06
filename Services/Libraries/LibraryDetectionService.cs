using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Nexora.Models;
using Nexora.Services.Downloads;

namespace Nexora.Services.Libraries
{
    public sealed class LibraryDetectionService
    {
        public LibraryItem Detect(LibraryDefinition definition)
        {
            if (definition == null) return null;

            string installedVersion = null;
            var installed = false;
            var detectionType = (definition.DetectionType ?? string.Empty).ToLowerInvariant();

            try
            {
                if (string.Equals(definition.Id, "dotnet-framework-481", StringComparison.OrdinalIgnoreCase))
                {
                    installed = TryDetectDotNetFramework481(out installedVersion);
                }
                else if (string.Equals(definition.Id, "vcpp-2015-2026-x64", StringComparison.OrdinalIgnoreCase))
                {
                    installed = TryDetectVcppV14("x64", out installedVersion);
                }
                else if (string.Equals(definition.Id, "vcpp-2015-2026-x86", StringComparison.OrdinalIgnoreCase))
                {
                    installed = TryDetectVcppV14("x86", out installedVersion);
                }
                else if (string.Equals(definition.InstallationType, "windowsFeature", StringComparison.OrdinalIgnoreCase))
                {
                    installed = IsNetFx3Enabled(out installedVersion);
                }
                else if (detectionType == "manual")
                {
                    var candidates = BuildDetectionCandidates(definition);
                    foreach (var candidate in candidates)
                    {
                        installedVersion = FindUninstallEntry(candidate, out installed);
                        if (installed) break;
                    }
                }
                else if (detectionType == "registrydisplayname")
                {
                    installedVersion = FindUninstallEntry(definition.DetectionValue, out installed);
                }
                else if (detectionType == "registrykey")
                {
                    installed = RegistryKeyExists(definition.DetectionValue);
                }
                else if (detectionType == "file")
                {
                    installed = File.Exists(Environment.ExpandEnvironmentVariables(definition.DetectionValue ?? string.Empty));
                }
            }
            catch
            {
                installed = false;
            }

            var status = installed
                ? LibraryInstallStatus.Installed
                : LibraryInstallStatus.Missing;
            return new LibraryItem(definition, status, installedVersion);
        }

        private static bool TryDetectDotNetFramework481(out string installedVersion)
        {
            installedVersion = null;
            const int minimumRelease = 533320;
            const string keyPath = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(keyPath))
                {
                    if (key == null)
                        continue;

                    var releaseValue = key.GetValue("Release");
                    if (releaseValue == null)
                        continue;

                    var release = Convert.ToInt32(releaseValue);
                    if (release < minimumRelease)
                        continue;

                    installedVersion = "4.8.1";
                    return true;
                }
            }

            return false;
        }

        private static bool TryDetectVcppV14(string architecture, out string installedVersion)
        {
            installedVersion = null;

            // Microsoft stores the v14 Runtime state in these registry keys.
            // Prefer the exact runtime key because it distinguishes x86/x64/ARM64
            // even when the uninstall display names are localized.
            var keyPaths = new[]
            {
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + architecture,
                @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\" + architecture
            };

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                {
                    foreach (var keyPath in keyPaths)
                    {
                        using (var key = baseKey.OpenSubKey(keyPath))
                        {
                            if (key == null)
                                continue;

                            var installed = Convert.ToInt32(key.GetValue("Installed", 0));
                            if (installed != 1)
                                continue;

                            var major = Convert.ToInt32(key.GetValue("Major", 0));
                            if (major < 14)
                                continue;

                            installedVersion = key.GetValue("Version") as string;
                            if (string.IsNullOrWhiteSpace(installedVersion))
                            {
                                var minor = Convert.ToInt32(key.GetValue("Minor", 0));
                                var build = Convert.ToInt32(key.GetValue("Bld", 0));
                                var revision = Convert.ToInt32(key.GetValue("Rbld", 0));
                                installedVersion = "14." + minor + "." + build + "." + revision;
                            }

                            return true;
                        }
                    }
                }
            }

            // Fallback for machines where the runtime key is missing but the
            // package registered normally in Add/Remove Programs.
            var wanted = architecture == "x86" ? "(x86)" : "(x64)";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using (var item = key.OpenSubKey(subKeyName))
                        {
                            if (item == null) continue;
                            var name = item.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(name) ||
                                name.IndexOf("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase) < 0 ||
                                name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) < 0 ||
                                name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            var version = item.GetValue("DisplayVersion") as string;
                            if (!string.IsNullOrWhiteSpace(version) && Version.TryParse(version, out var parsed) && parsed.Major >= 14)
                            {
                                installedVersion = version;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsNetFx3Enabled(out string installedVersion)
        {
            installedVersion = null;
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5"))
            {
                if (key == null || Convert.ToInt32(key.GetValue("Install", 0)) != 1) return false;
                installedVersion = key.GetValue("Version") as string ?? "3.5";
                return true;
            }
        }

        private static string[] BuildDetectionCandidates(LibraryDefinition definition)
        {
            var names = new[]
            {
                definition.DetectionValue,
                definition.Name,
                definition.Name?.Replace("Microsoft Visual C++ ", "Microsoft Visual C++ Redistributable "),
                definition.Name?.Replace("Microsoft Visual C++ ", "Microsoft Visual C++ Redistributable " + (definition.Version ?? string.Empty) + " ")
            };

            return names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string FindUninstallEntry(string displayName, out bool found)
        {
            found = false;
            if (string.IsNullOrWhiteSpace(displayName)) return null;
            foreach (var root in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, root))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using (var item = key.OpenSubKey(name))
                        {
                            var current = item?.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(current) || current.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            found = true;
                            return item.GetValue("DisplayVersion") as string;
                        }
                    }
                }
            }
            return null;
        }

        private static bool RegistryKeyExists(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var separator = value.LastIndexOf('\\');
            var path = separator > 0 ? value.Substring(0, separator) : value;
            var valueName = separator > 0 ? value.Substring(separator + 1) : null;
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(path))
                {
                    if (key != null && (string.IsNullOrWhiteSpace(valueName) || key.GetValue(valueName) != null))
                        return true;
                }
            }
            return false;
        }
    }
}
