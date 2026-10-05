using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Domain.WindowsSettings;

namespace Nexora.Services
{
    public sealed class DiskCleanupService
    {
        private const string RecycleBinId = "RecycleBin";
        private static readonly string DotNetTempPath =
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), ".net"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        public IReadOnlyList<DiskCleanupItem> GetCleanupItems()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var systemDrive = Path.GetPathRoot(windows) ?? "C:\\";

            return new[]
            {
                new DiskCleanupItem
                {
                    Id = "TempFiles",
                    Name = "Временные файлы пользователя",
                    Description = "Файлы из %TEMP% и вложенных каталогов.",
                    Path = Path.GetTempPath(),
                    IconPath = "interface/white/package.svg"
                },
                new DiskCleanupItem
                {
                    Id = "SystemTemp",
                    Name = "Системные временные файлы",
                    Description = "Содержимое папки %WINDIR%\\Temp.",
                    Path = Path.Combine(windows, "Temp"),
                    IconPath = "interface/white/brand-windows.svg"
                },
                new DiskCleanupItem
                {
                    Id = "WindowsUpdate",
                    Name = "Кэш Windows Update",
                    Description = "Загруженные пакеты обновлений, которые больше не нужны Windows Update.",
                    Path = Path.Combine(windows, @"SoftwareDistribution\Download"),
                    IconPath = "interface/white/fluent-arrow-download.svg"
                },
                new DiskCleanupItem
                {
                    Id = "Prefetch",
                    Name = "Prefetch",
                    Description = "Старые данные предварительной выборки приложений.",
                    Path = Path.Combine(windows, "Prefetch"),
                    IconPath = "interface/white/package.svg"
                },
                new DiskCleanupItem
                {
                    Id = "Thumbnails",
                    Name = "Кэш эскизов",
                    Description = "Кэш миниатюр Проводника.",
                    Path = Path.Combine(localAppData, @"Microsoft\Windows\Explorer"),
                    IconPath = "interface/white/category-media.svg"
                },
                new DiskCleanupItem
                {
                    Id = RecycleBinId,
                    Name = "Корзина",
                    Description = "Объекты, находящиеся в корзине Windows.",
                    Path = string.Empty,
                    IconPath = "interface/white/package.svg",
                    IsCommand = true
                },
                new DiskCleanupItem
                {
                    Id = "ErrorReports",
                    Name = "Дампы сбоев",
                    Description = "Локальные дампы аварийно завершившихся приложений.",
                    Path = Path.Combine(localAppData, "CrashDumps"),
                    IconPath = "interface/white/package.svg"
                },
                new DiskCleanupItem
                {
                    Id = "OldWindowsInstallation",
                    Name = "Windows.old",
                    Description = "Старая установка Windows. Может быть нужна для восстановления предыдущей системы.",
                    Path = Path.Combine(systemDrive, "Windows.old"),
                    IconPath = "interface/white/brand-windows.svg",
                    IsDangerous = true,
                    IsSelected = false
                }
            };
        }

        public async Task ScanAllAsync(IEnumerable<DiskCleanupItem> items, CancellationToken token = default)
        {
            var tasks = items.Select(item => ScanAsync(item, token));
            await Task.WhenAll(tasks);
        }

        public async Task ScanAsync(DiskCleanupItem item, CancellationToken token = default)
        {
            item.IsScanning = true;
            item.Status = "Сканирование…";
            try
            {
                token.ThrowIfCancellationRequested();

                if (item.Id == RecycleBinId)
                {
                    var info = QueryRecycleBin();
                    item.SizeBytes = info.Size;
                    item.FileCount = info.Count;
                }
                else
                {
                    var pattern = item.Id == "Thumbnails" ? "thumbcache_*" : "*";
                    var recursive = item.Id != "Thumbnails";
                    var metrics = await Task.Run(() => CalculateDirectoryMetrics(item.Path, pattern, recursive, item.Id), token);
                    item.SizeBytes = metrics.Size;
                    item.FileCount = metrics.Count;
                }

                if (item.SizeBytes <= 0) item.IsSelected = false;
                item.Status = item.SizeBytes > 0
                    ? $"{item.SizeText} · {item.FileCount:N0} файлов"
                    : "Ничего для очистки";
            }
            catch (OperationCanceledException)
            {
                item.Status = "Операция отменена";
            }
            catch (Exception ex)
            {
                item.SizeBytes = 0;
                item.FileCount = 0;
                item.Status = "Не удалось просканировать: " + ex.Message;
            }
            finally
            {
                item.IsScanning = false;
            }
        }

        public async Task<long> CleanAsync(DiskCleanupItem item, CancellationToken token = default)
        {
            item.IsCleaning = true;
            item.Status = "Очистка…";
            try
            {
                token.ThrowIfCancellationRequested();
                long freed;
                if (item.Id == RecycleBinId)
                {
                    var before = QueryRecycleBin().Size;
                    EmptyRecycleBin();
                    var after = QueryRecycleBin().Size;
                    freed = Math.Max(0, before - after);
                }
                else
                {
                    freed = await Task.Run(() => DeleteFilesInDirectory(item.Path, item.Id, token), token);
                }

                item.SizeBytes = 0;
                item.FileCount = 0;
                item.IsSelected = false;
                item.Status = freed > 0 ? $"Освобождено {DiskCleanupItem.FormatBytes(freed)}" : "Очистка завершена";
                return freed;
            }
            catch (OperationCanceledException)
            {
                item.Status = "Операция отменена";
                return 0;
            }
            catch (Exception ex)
            {
                item.Status = "Ошибка очистки: " + ex.Message;
                return 0;
            }
            finally
            {
                item.IsCleaning = false;
            }
        }

        public async Task<long> CleanSelectedAsync(IEnumerable<DiskCleanupItem> items, CancellationToken token = default)
        {
            long total = 0;
            foreach (var item in items.Where(i => i.IsSelected && i.SizeBytes > 0).ToList())
            {
                token.ThrowIfCancellationRequested();
                total += await CleanAsync(item, token);
            }
            return total;
        }

        private static (long Size, long Count) CalculateDirectoryMetrics(string path, string searchPattern, bool recursive, string itemId)
        {
            if (!Directory.Exists(path)) return (0, 0);

            long size = 0;
            long count = 0;
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false
            };

            try
            {
                var root = new DirectoryInfo(path);
                var needsDotNetFilter = recursive &&
                    DotNetTempPath.StartsWith(
                        path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);

                foreach (var file in root.EnumerateFiles(searchPattern, options))
                {
                    try
                    {
                        if (needsDotNetFilter)
                        {
                            var dir = (file.DirectoryName ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (dir.StartsWith(DotNetTempPath, StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        size += file.Length;
                        count++;
                    }
                    catch { }
                }
            }
            catch { }

            return (size, count);
        }

        private static long DeleteFilesInDirectory(string path, string itemId, CancellationToken token)
        {
            if (!Directory.Exists(path)) return 0;

            long freed = 0;
            var pattern = itemId == "Thumbnails" ? "thumbcache_*" : "*";
            var recursive = itemId != "Thumbnails";
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false
            };

            var root = new DirectoryInfo(path);
            var needsDotNetFilter = recursive &&
                DotNetTempPath.StartsWith(
                    path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);

            foreach (var file in root.EnumerateFiles(pattern, options))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (needsDotNetFilter)
                    {
                        var dir = (file.DirectoryName ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (dir.StartsWith(DotNetTempPath, StringComparison.OrdinalIgnoreCase)) continue;
                    }

                    var length = file.Length;
                    file.IsReadOnly = false;
                    file.Delete();
                    freed += length;
                }
                catch { }
            }

            if (recursive)
            {
                try
                {
                    var dirs = root.EnumerateDirectories("*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        ReturnSpecialDirectories = false
                    }).OrderByDescending(d => d.FullName.Length);

                    foreach (var dir in dirs)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var full = dir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (full.StartsWith(DotNetTempPath, StringComparison.OrdinalIgnoreCase)) continue;
                            dir.Delete(false);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return freed;
        }

        private static (long Size, long Count) QueryRecycleBin()
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            var hr = SHQueryRecycleBin(null, ref info);
            if (hr != 0) return (0, 0);
            return (Math.Max(0, info.i64Size), Math.Max(0, info.i64NumItems));
        }

        private static void EmptyRecycleBin()
        {
            const uint SHERB_NOCONFIRMATION = 0x00000001;
            const uint SHERB_NOPROGRESSUI = 0x00000002;
            const uint SHERB_NOSOUND = 0x00000004;
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
    }
}
