using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using Scio.Server;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class TelemetryAutoSendTests
    {
        private static void WithIsolatedRoot(Action<string> action)
        {
            string? prev = Environment.GetEnvironmentVariable("SCIO_DATA_ROOT");
            string root = Path.Combine(Path.GetTempPath(), "sutra-tel-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            TelemetryTool.ResetAutoFlushStateForTests();
            TelemetryTool.HttpPostOverride = null;
            try
            {
                Environment.SetEnvironmentVariable("SCIO_DATA_ROOT", root);
                action(root);
            }
            finally
            {
                TelemetryTool.HttpPostOverride = null;
                TelemetryTool.ResetAutoFlushStateForTests();
                Environment.SetEnvironmentVariable("SCIO_DATA_ROOT", prev);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void AutoFlush_WhenOptedOut_DoesNotRecordOrSend()
        {
            WithIsolatedRoot(_ =>
            {
                bool posted = false;
                TelemetryTool.HttpPostOverride = (_, _, _) => { posted = true; return (true, 200); };

                TelemetryTool.RecordToolCall("scio_health", true, 12, "codemode");
                Assert.False(File.Exists(TelemetryTool.BufferPath()));

                JObject res = TelemetryTool.TryAutoFlush(CancellationToken.None, ignoreCooldown: true);
                Assert.False(res.Value<bool>("sent"));
                Assert.Equal("disabled", res.Value<string>("status"));
                Assert.False(posted);
            });
        }

        [Fact]
        public void AutoFlush_WhenOptedIn_PostsAggregatedPayload_AndClearsBuffer()
        {
            WithIsolatedRoot(root =>
            {
                File.WriteAllText(Path.Combine(root, "settings.json"), "{\"telemetry_opt_in\": true}");

                string? capturedEndpoint = null;
                string? capturedBody = null;
                TelemetryTool.HttpPostOverride = (ep, body, _) =>
                {
                    capturedEndpoint = ep;
                    capturedBody = body;
                    return (true, 200);
                };

                TelemetryTool.RecordToolCall("scio_health", true, 20, "codemode");
                TelemetryTool.RecordToolCall("scio_model_scan", true, 180, "codemode");
                TelemetryTool.RecordToolCall("scio_telemetry", true, 5, "codemode"); // ignored

                JObject res = TelemetryTool.Handle(new JObject { ["operation"] = "auto_flush" }, CancellationToken.None);
                Assert.True(res.Value<bool>("sent"));
                Assert.Equal(2, res.Value<int>("events_flushed"));
                Assert.True(res.Value<bool>("buffer_cleared"));
                Assert.Equal(TelemetryTool.DefaultEndpoint, capturedEndpoint);

                Assert.NotNull(capturedBody);
                var payload = JObject.Parse(capturedBody!);
                Assert.Equal("sutra.telemetry/1", payload.Value<string>("schema"));
                Assert.Equal(2, payload.Value<int>("total_events"));
                Assert.Equal(1, payload["by_tool"]!["scio_health"]!.Value<int>("calls"));
                Assert.Equal(1, payload["by_tool"]!["scio_model_scan"]!.Value<int>("calls"));
                Assert.Null(payload["by_tool"]!["scio_telemetry"]);

                JObject status = TelemetryTool.Handle(new JObject { ["operation"] = "status" }, CancellationToken.None);
                Assert.True(status.Value<bool>("auto_flush_enabled"));
                Assert.Equal("ok", status.Value<string>("last_auto_flush_status"));
                Assert.Equal(2, status.Value<int>("last_auto_flush_events"));
                Assert.Equal(0, status.Value<int>("buffered_events"));
            });
        }

        [Fact]
        public void AutoFlush_OnHttpFailure_PreservesBuffer()
        {
            WithIsolatedRoot(root =>
            {
                File.WriteAllText(Path.Combine(root, "settings.json"), "{\"telemetry_opt_in\": true}");
                TelemetryTool.HttpPostOverride = (_, _, _) => (false, 503);

                TelemetryTool.RecordToolCall("scio_health", true, 10, "codemode");

                JObject res = TelemetryTool.TryAutoFlush(CancellationToken.None, ignoreCooldown: true);
                Assert.False(res.Value<bool>("sent"));
                Assert.Equal("http_503", res.Value<string>("status"));
                Assert.True(res.Value<bool>("buffer_preserved"));

                JObject status = TelemetryTool.Handle(new JObject { ["operation"] = "status" }, CancellationToken.None);
                Assert.Equal(1, status.Value<int>("buffered_events"));
            });
        }
    }
}
