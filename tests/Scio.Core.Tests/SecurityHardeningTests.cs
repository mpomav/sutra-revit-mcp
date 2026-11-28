using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class SecurityHardeningTests
    {
        // --- SEC-01: Inline image path containment & trace verification ---

        [Fact]
        public void Sec01_CodeMode_WithoutCaptureViewInTrace_RefusesInlineImage()
        {
            string fakePath = Path.Combine(Path.GetTempPath(), "sutra-test.png");
            var cmData = new JObject
            {
                ["ok"] = true,
                ["result"] = new JObject { ["image_path"] = fakePath },
                ["trace"] = new JArray(new JObject { ["step"] = 1, ["tool"] = "scio_health", ["ok"] = true })
            };

            string? extracted = McpStdioHost.ExtractCapturedImagePath(CodeModeTool.ToolName, new JObject(), cmData);
            Assert.Null(extracted);
        }

        [Fact]
        public void Sec01_InlineImagePath_RejectsUncAndArbitraryUserDirectories()
        {
            Assert.False(McpStdioHost.IsAllowedInlineImagePath(@"\\attacker.example\share\secret.png"));
            Assert.False(McpStdioHost.IsAllowedInlineImagePath(@"C:\Windows\System32\secret.png"));
            Assert.False(McpStdioHost.IsAllowedInlineImagePath("relative/path.png"));
            Assert.True(McpStdioHost.IsAllowedInlineImagePath(Path.Combine(Path.GetTempPath(), "capture.png")));
        }

        // --- SEC-02: PromoteScriptTool path containment & CodeMode block ---

        [Fact]
        public async Task Sec02_CodeMode_BlocksPromoteScript()
        {
            var reg = new ServerToolRegistry { MockMode = true };
            reg.TraceRecorder.Enabled = false;
            var host = new McpStdioHost(reg, "codemode");

            var resp = await reg.DispatchAsync(new ToolRequest
            {
                RequestId = "sec02-cm",
                Tool = CodeModeTool.ToolName,
                Arguments = new JObject
                {
                    ["read_only"] = false,
                    ["code"] = "return scio.call('scio_promote_script', { operation: 'list' });"
                }
            }, CancellationToken.None);

            Assert.False(resp.Ok);
            Assert.Contains("blocked", resp.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            _ = host;
        }

        [Fact]
        public void Sec02_PromoteScript_RejectsUncAndNonExtensionDirectory()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sutra-sec02-" + Guid.NewGuid().ToString("N"));
            string store = Path.Combine(tmp, "registry.json");
            try
            {
                var prop = PromoteScriptTool.Handle(new JObject
                {
                    ["operation"] = "propose",
                    ["name"] = "TestFlow",
                    ["source"] = "print('ok')\n"
                }, CancellationToken.None, store);
                string id = prop["generation"]!.Value<string>("id")!;
                PromoteScriptTool.Handle(new JObject { ["operation"] = "review", ["id"] = id }, CancellationToken.None, store);
                PromoteScriptTool.Handle(new JObject { ["operation"] = "approve", ["id"] = id }, CancellationToken.None, store);
                PromoteScriptTool.Handle(new JObject { ["operation"] = "activate", ["id"] = id }, CancellationToken.None, store);

                // UNC path must be refused
                Assert.Throws<ToolRefusalException>(() => PromoteScriptTool.Handle(new JObject
                {
                    ["operation"] = "export_pyrevit",
                    ["id"] = id,
                    ["extension_root"] = @"\\attacker\share\evil.extension"
                }, CancellationToken.None, store));

                // Arbitrary non-.extension folder must be refused
                Assert.Throws<ToolRefusalException>(() => PromoteScriptTool.Handle(new JObject
                {
                    ["operation"] = "export_pyrevit",
                    ["id"] = id,
                    ["extension_root"] = tmp
                }, CancellationToken.None, store));
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        // --- SEC-03: Telemetry SSRF endpoint allowlist ---

        [Theory]
        [InlineData("https://scio.pe/api/sutra/telemetry.php", true)]
        [InlineData("https://telemetry.scio.pe/v1/ingest", true)]
        [InlineData("http://scio.pe/api/sutra/telemetry.php", false)]
        [InlineData("https://evil.example/exfil", false)]
        [InlineData("https://127.0.0.1/admin", false)]
        [InlineData("https://169.254.169.254/latest/meta-data", false)]
        [InlineData("https://localhost:8443/hook", false)]
        public void Sec03_TelemetryEndpoint_EnforcesHttpsAndScioPeAllowlist(string url, bool expectedAllowed)
        {
            bool ok = TelemetryTool.IsAllowedTelemetryEndpoint(url, out _);
            Assert.Equal(expectedAllowed, ok);
        }

        // --- SEC-04: Update URL trust validation ---

        [Theory]
        [InlineData("https://github.com/mpomav/sutra-revit-mcp/releases/latest", true)]
        [InlineData("https://github.com/mpomav/sutra-revit-mcp/releases/download/v1.4.0/sutra-revit-mcp-1.4.0-setup.exe", true)]
        [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123", true)]
        [InlineData("http://github.com/mpomav/sutra-revit-mcp/releases/latest", false)]
        [InlineData("https://evil.example/sutra-setup.exe", false)]
        [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
        [InlineData(@"\\attacker\share\evil.exe", false)]
        [InlineData("cmd.exe", false)]
        public void Sec04_UpdateChecker_ValidatesTrustedReleaseUrls(string url, bool expectedTrusted)
        {
            Assert.Equal(expectedTrusted, SutraUpdateChecker.IsTrustedReleaseUrl(url));
        }

        // --- SEC-06: SQLite read-only null-byte guard ---

        [Fact]
        public void Sec06_SqliteReadOnlyGuard_RejectsNullBytesAndWriteKeywords()
        {
            Assert.NotNull(SqliteIngestor.ValidateReadOnlySql("SELECT 1\0; DROP TABLE runs"));
            Assert.NotNull(SqliteIngestor.ValidateReadOnlySql("ATTACH DATABASE 'x.db' AS x"));
            Assert.Null(SqliteIngestor.ValidateReadOnlySql("SELECT run_id FROM runs LIMIT 5"));
        }
    }
}
