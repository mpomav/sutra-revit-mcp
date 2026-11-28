using System;
using System.IO;
using System.Threading.Tasks;
using Scio.Abstractions;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class SutraUpdateCheckerTests : IDisposable
    {
        private readonly string _tempRoot;

        public SutraUpdateCheckerTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "sutra-update-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            SutraUpdateChecker.DataRootOverride = _tempRoot;
            SutraUpdateChecker.FetchLatestJsonOverride = null;
        }

        public void Dispose()
        {
            SutraUpdateChecker.DataRootOverride = null;
            SutraUpdateChecker.FetchLatestJsonOverride = null;
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("1.3.0", "1.3.1", true)]
        [InlineData("1.3.0", "v1.4.0", true)]
        [InlineData("1.3.0", "2.0.0", true)]
        [InlineData("1.3.0", "1.3.0", false)]
        [InlineData("1.3.0", "1.2.9", false)]
        [InlineData("v1.3.0+abc123", "v1.3.0", false)]
        public void IsNewerVersion_ComparesSemVerCleanly(string current, string candidate, bool expected)
        {
            Assert.Equal(expected, SutraUpdateChecker.IsNewerVersion(current, candidate));
        }

        [Fact]
        public async Task CheckForUpdateAsync_ParsesReleaseCachesAndSupportsDismiss()
        {
            int fetchCount = 0;
            const string sampleRelease = @"{
                ""tag_name"": ""v1.4.0"",
                ""html_url"": ""https://github.com/mpomav/sutra-revit-mcp/releases/tag/v1.4.0"",
                ""body"": ""- Fix Claude Desktop Microsoft Store MSIX\n- Update alerts"",
                ""published_at"": ""2026-11-28T00:00:00Z"",
                ""draft"": false,
                ""prerelease"": false,
                ""assets"": [
                    { ""name"": ""sutra-revit-mcp-1.4.0-win-x64.zip"", ""browser_download_url"": ""https://github.com/mpomav/sutra-revit-mcp/releases/download/v1.4.0/sutra-revit-mcp-1.4.0-win-x64.zip"" },
                    { ""name"": ""sutra-revit-mcp-1.4.0-setup.exe"", ""browser_download_url"": ""https://github.com/mpomav/sutra-revit-mcp/releases/download/v1.4.0/sutra-revit-mcp-1.4.0-setup.exe"" }
                ]
            }";

            SutraUpdateChecker.FetchLatestJsonOverride = _ =>
            {
                fetchCount++;
                return Task.FromResult<string?>(sampleRelease);
            };

            var first = await SutraUpdateChecker.CheckForUpdateAsync("1.3.0");
            Assert.NotNull(first);
            Assert.True(first!.UpdateAvailable);
            Assert.Equal("1.4.0", first.LatestVersion);
            Assert.Equal("https://github.com/mpomav/sutra-revit-mcp/releases/download/v1.4.0/sutra-revit-mcp-1.4.0-setup.exe", first.DownloadUrl);
            Assert.Equal(1, fetchCount);

            // Second call within 24h should hit disk cache, not network
            var second = await SutraUpdateChecker.CheckForUpdateAsync("1.3.0");
            Assert.NotNull(second);
            Assert.True(second!.UpdateAvailable);
            Assert.Equal(1, fetchCount);

            // Cached read reflects newer local version once upgraded
            var afterUpgrade = SutraUpdateChecker.TryGetCachedUpdate("1.4.0");
            Assert.NotNull(afterUpgrade);
            Assert.False(afterUpgrade!.UpdateAvailable);

            // Dismissing v1.4.0 persists to settings.json
            Assert.False(SutraUpdateChecker.IsVersionDismissed("1.4.0"));
            Assert.True(SutraUpdateChecker.DismissVersion("v1.4.0"));
            Assert.True(SutraUpdateChecker.IsVersionDismissed("1.4.0"));
            Assert.False(SutraUpdateChecker.IsVersionDismissed("1.5.0"));
        }
    }
}
