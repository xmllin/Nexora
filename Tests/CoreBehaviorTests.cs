using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Domain.Apps;
using Nexora.Models;
using Nexora.Services;
using Nexora.Services.Downloads;
using Xunit;

namespace Nexora.Tests
{
    public sealed class CoreBehaviorTests
    {
        [Fact]
        public void LibraryStatus_IsMissingOnlyForMissing()
        {
            var definition = new LibraryDefinition { Id = "test", Name = "Test" };

            var missing = new LibraryItem(definition, LibraryInstallStatus.Missing, null);
            var installed = new LibraryItem(definition, LibraryInstallStatus.Installed, "1.0");
            var update = new LibraryItem(definition, LibraryInstallStatus.UpdateAvailable, "1.0");
            var manual = new LibraryItem(definition, LibraryInstallStatus.Manual, null);

            Assert.True(missing.IsMissing);
            Assert.False(installed.IsMissing);
            Assert.False(update.IsMissing);
            Assert.False(manual.IsMissing);

            Assert.Equal("Не установлено", missing.StatusText);
            Assert.Equal("Установлено", installed.StatusText);
            Assert.Equal("Доступно обновление", update.StatusText);
            Assert.Equal("Ручная установка", manual.StatusText);
        }

        [Fact]
        public void VersionNormalizer_PreservesMostSpecificVersion()
        {
            Assert.Equal("1.2.3.4", VersionNormalizer.ExtractMostSpecific("release-1.2", "tool-1.2.3.4-win64.exe"));
            Assert.True(VersionNormalizer.Compare("1.2.10", "1.2.2") > 0);
            Assert.Equal(string.Empty, VersionNormalizer.ExtractMostSpecific("https://example.test/download/setup.exe?v=1780645729"));
            Assert.Equal("1.0.31", VersionNormalizer.ExtractMostSpecific("LAMZU-Aurora-1.0.31-setup.rar?v=1780645729"));
        }


        [Fact]
        public void WinGetVersionParser_ExtractsAndDeduplicatesVersions()
        {
            var output = @"
Available Versions
------------------
25.01
24.12
25.01
1.2.10-beta
Not a version
";

            var versions = WinGetDownloadProvider.ParseAvailableVersions(output);

            Assert.Equal(3, versions.Count);
            Assert.Contains("25.01", versions);
            Assert.Contains("24.12", versions);
            Assert.Contains("1.2.10-beta", versions);
        }

        [Fact]
        public void FileValidator_RejectsHtmlAndAcceptsExpectedSignature()
        {
            var html = Encoding.UTF8.GetBytes("<!doctype html><html>");
            Assert.True(FileValidator.IsHtmlPrefix(html, html.Length));

            var exe = new byte[] { 0x4D, 0x5A };
            FileValidator.ValidatePrefix("installer.exe", exe, exe.Length);

            Assert.Throws<InvalidOperationException>(() =>
                FileValidator.ValidatePrefix("installer.exe", new byte[] { 0x50, 0x4B }, 2));
        }

        [Fact]
        public void PaginationState_ClampsCurrentPageAfterItemsShrink()
        {
            var state = new PaginationState<int>(2);
            state.SetItems(Enumerable.Range(1, 5), true);
            Assert.Equal("1 / 3", state.PageLabel);

            Assert.True(state.MoveNext());
            Assert.True(state.MoveNext());
            Assert.False(state.MoveNext());
            Assert.Equal("3 / 3", state.PageLabel);

            state.SetItems(new[] { 1 }, false);
            Assert.Equal(1, state.CurrentPage);
            Assert.False(state.HasPreviousPage);
            Assert.False(state.HasNextPage);
        }

        [Fact]
        public async Task PauseController_ResumesAndSupportsCancellation()
        {
            using (var pause = new PauseController())
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                pause.Pause();
                var waiting = pause.WaitIfPausedAsync(cancellation.Token);
                Assert.False(waiting.IsCompleted);

                pause.Resume();
                await waiting;

                pause.Pause();
                var second = pause.WaitIfPausedAsync(cancellation.Token);
                pause.Dispose();
                await second;

                Assert.False(pause.IsPaused);
            }
        }

        [Fact]
        public void AppIconResolver_ResolvesConfiguredAndMappedFiles()
        {
            var root = Path.Combine(Path.GetTempPath(), "Nexora.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "logos"));
            Directory.CreateDirectory(Path.Combine(root, "interface", "white"));

            try
            {
                var mapped = Path.Combine(root, "logos", "Firefox_logo,_2019.svg");
                File.WriteAllText(mapped, "<svg></svg>");
                var custom = Path.Combine(root, "custom.svg");
                File.WriteAllText(custom, "<svg></svg>");
                File.WriteAllText(Path.Combine(root, "interface", "white", "fluent-app-folder.svg"), "<svg></svg>");

                var resolver = new AppIconResolver(root);

                var fromMapped = resolver.Resolve(new AppDefinition { Id = "firefox", Name = "Firefox" });
                Assert.True(Uri.TryCreate(fromMapped, UriKind.Absolute, out var mappedUri));
                Assert.Equal(Path.GetFullPath(mapped), Path.GetFullPath(mappedUri.LocalPath));

                var fromConfigured = resolver.Resolve(new AppDefinition { Id = "custom", Icon = "custom.svg" });
                Assert.True(Uri.TryCreate(fromConfigured, UriKind.Absolute, out var customUri));
                Assert.Equal(Path.GetFullPath(custom), Path.GetFullPath(customUri.LocalPath));

                var alreadyResolved = resolver.Resolve(new AppDefinition { Id = "custom", Icon = new Uri(custom).AbsoluteUri });
                Assert.Equal(new Uri(custom).AbsoluteUri, alreadyResolved);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void DownloadSettings_DefaultFolderIsAbsolute()
        {
            var folder = DownloadSettings.GetDefaultFolder();
            Assert.False(string.IsNullOrWhiteSpace(folder));
            Assert.True(Path.IsPathRooted(folder));
        }
    }
}
