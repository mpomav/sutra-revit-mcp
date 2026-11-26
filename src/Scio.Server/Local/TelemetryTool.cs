// -----------------------------------------------------------------------------
// Sutra — Revit MCP (by SCIO DC)
// Strictly Opt-In Anonymous Usage Telemetry (scio_telemetry)
//
// OFF by default (telemetry_opt_in=false in settings.json). Never collects IP
// addresses, document titles, file paths, element IDs, or model content.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    public static class TelemetryTool
    {
        public const string ToolName = "scio_telemetry";
        public const string DefaultEndpoint = "https://scio.pe/api/sutra/telemetry.php";
        private static readonly object FileLock = new object();
        private static readonly HttpClient SharedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public static string SettingsPath() => Path.Combine(ScioRuntimePaths.DataRoot, "settings.json");
        public static string BufferPath() => Path.Combine(ScioRuntimePaths.DataRoot, "telemetry", "usage-buffer.jsonl");

        public static bool IsOptedIn()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return false;
                var obj = JObject.Parse(File.ReadAllText(path));
                return obj["telemetry_opt_in"]?.Type == JTokenType.Boolean && (bool)obj["telemetry_opt_in"]!;
            }
            catch { return false; }
        }

        public static string? ConfiguredEndpoint()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return null;
                var obj = JObject.Parse(File.ReadAllText(path));
                string? url = obj["telemetry_endpoint"]?.ToString()?.Trim();
                return string.IsNullOrWhiteSpace(url) ? null : url;
            }
            catch { return null; }
        }

        private static string CurrentVersion { get; } = ResolveAssemblyVersion();

        private static string ResolveAssemblyVersion()
        {
            var asm = typeof(TelemetryTool).Assembly;
            var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                int plus = info.InformationalVersion.IndexOf('+');
                return plus > 0 ? info.InformationalVersion.Substring(0, plus) : info.InformationalVersion;
            }
            var v = asm.GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.1.0";
        }

        public static void RecordToolCall(string toolName, bool ok, long elapsedMs, string schemaMode, string? version = null)
        {
            try
            {
                if (!IsOptedIn()) return;
                if (string.IsNullOrWhiteSpace(toolName)) return;

                string path = BufferPath();
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                DateTime now = DateTime.UtcNow;
                string hourBucket = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc)
                    .ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture);

                var entry = new JObject
                {
                    ["hour_utc"] = hourBucket,
                    ["tool"] = toolName,
                    ["ok"] = ok,
                    ["ms"] = Math.Max(0, elapsedMs),
                    ["schema_mode"] = string.IsNullOrWhiteSpace(schemaMode) ? "codemode" : schemaMode,
                    ["version"] = string.IsNullOrWhiteSpace(version) ? CurrentVersion : version
                };

                lock (FileLock)
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) return;
                    File.AppendAllText(path, entry.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static JObject Handle(JObject args, CancellationToken ct)
        {
            string op = (args?.Value<string>("operation") ?? "status").Trim().ToLowerInvariant();
            switch (op)
            {
                case "status": return HandleStatus();
                case "preview": return HandlePreview();
                case "insights": return DistillTool.Handle(new JObject { ["operation"] = "insights", ["limit"] = args?.Value<int?>("limit") ?? 100 }, ct);
                case "flush": return HandleFlush(args, ct);
                case "clear": return HandleClear();
                default:
                    throw new ToolRefusalException("Unsupported operation '" + op + "'. Expected: status, preview, insights, flush, clear.");
            }
        }

        private static JObject HandleStatus()
        {
            bool optIn = IsOptedIn();
            int buffered = ReadEvents().Count;
            string? endpoint = ResolveEndpoint(null);

            return new JObject
            {
                ["operation"] = "status",
                ["telemetry_opt_in"] = optIn,
                ["active"] = optIn,
                ["buffered_events"] = buffered,
                ["endpoint"] = endpoint ?? "local_export_bundle",
                ["guarantees"] = new JArray(
                    "OFF by default; zero bytes recorded or sent until owner opts in (telemetry_opt_in=true in settings.json).",
                    "Never collects IP addresses, document names, file paths, element IDs, parameters, or model content.",
                    "Collects only anonymous tool names, success counts, timings, schema mode, 2-letter OS region, and Sutra version.")
            };
        }

        private static JObject HandlePreview()
        {
            List<JObject> events = ReadEvents();
            return new JObject
            {
                ["operation"] = "preview",
                ["telemetry_opt_in"] = IsOptedIn(),
                ["buffered_events"] = events.Count,
                ["payload"] = BuildAggregatePayload(events)
            };
        }

        private static JObject HandleFlush(JObject? args, CancellationToken ct)
        {
            if (!IsOptedIn())
            {
                throw new ToolRefusalException(
                    "Telemetry is disabled (telemetry_opt_in=false). Nothing was recorded or sent.");
            }

            bool dryRun = args?["dry_run"] == null || args.Value<bool>("dry_run");
            List<JObject> events = ReadEvents();
            JObject payload = BuildAggregatePayload(events);
            string? endpoint = ResolveEndpoint(args?.Value<string>("endpoint"));

            if (dryRun)
            {
                return new JObject
                {
                    ["operation"] = "flush",
                    ["dry_run"] = true,
                    ["telemetry_opt_in"] = true,
                    ["buffered_events"] = events.Count,
                    ["destination"] = !string.IsNullOrWhiteSpace(endpoint) ? endpoint : "local_export_bundle",
                    ["payload"] = payload
                };
            }

            if (events.Count == 0)
            {
                return new JObject
                {
                    ["operation"] = "flush",
                    ["dry_run"] = false,
                    ["sent"] = false,
                    ["buffered_events"] = 0
                };
            }

            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                if (!endpoint!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new ToolRefusalException("Telemetry endpoint must use HTTPS.");

                using var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                HttpResponseMessage resp = SharedHttp.PostAsync(endpoint, content, ct).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    throw new ToolRefusalException("Telemetry endpoint returned HTTP " + (int)resp.StatusCode + "; buffer preserved.");

                ClearBuffer();
                return new JObject
                {
                    ["operation"] = "flush",
                    ["dry_run"] = false,
                    ["sent"] = true,
                    ["mode"] = "https_post",
                    ["events_flushed"] = events.Count,
                    ["buffer_cleared"] = true
                };
            }

            string dir = Path.Combine(ScioRuntimePaths.DataRoot, "telemetry");
            Directory.CreateDirectory(dir);
            string exportFile = Path.Combine(dir, "usage-export-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(exportFile, payload.ToString(Formatting.Indented), new UTF8Encoding(false));
            ClearBuffer();

            return new JObject
            {
                ["operation"] = "flush",
                ["dry_run"] = false,
                ["sent"] = true,
                ["mode"] = "local_export_bundle",
                ["export_path"] = exportFile,
                ["events_flushed"] = events.Count,
                ["buffer_cleared"] = true
            };
        }

        private static JObject HandleClear()
        {
            int prev = ReadEvents().Count;
            ClearBuffer();
            return new JObject
            {
                ["operation"] = "clear",
                ["cleared_events"] = prev,
                ["buffer_cleared"] = true
            };
        }

        private static JObject BuildAggregatePayload(List<JObject> events)
        {
            var byTool = new JObject();
            var byMode = new JObject();
            string latestVersion = CurrentVersion;

            foreach (var group in events.GroupBy(e => e.Value<string>("tool") ?? "unknown", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int total = group.Count();
                int okCount = group.Count(e => e.Value<bool?>("ok") == true);
                double avgMs = Math.Round(group.Average(e => (double)(e.Value<long?>("ms") ?? 0)), 1);
                byTool[group.Key] = new JObject
                {
                    ["calls"] = total,
                    ["ok"] = okCount,
                    ["avg_ms"] = avgMs
                };
            }

            foreach (var mg in events.GroupBy(e => e.Value<string>("schema_mode") ?? "codemode", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                byMode[mg.Key] = mg.Count();
            }

            JObject? last = events.LastOrDefault();
            if (last != null && !string.IsNullOrWhiteSpace(last.Value<string>("version")))
                latestVersion = last.Value<string>("version")!;

            return new JObject
            {
                ["schema"] = "sutra.telemetry/1",
                ["generated_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture),
                ["product"] = "sutra-revit-mcp",
                ["version"] = latestVersion,
                ["region"] = ReadRegionCode(),
                ["total_events"] = events.Count,
                ["by_schema_mode"] = byMode,
                ["by_tool"] = byTool
            };
        }

        private static string? ResolveEndpoint(string? explicitArg)
        {
            string? chosen = !string.IsNullOrWhiteSpace(explicitArg)
                ? explicitArg!.Trim()
                : (ConfiguredEndpoint() ?? DefaultEndpoint);
            if (string.Equals(chosen, "local", StringComparison.OrdinalIgnoreCase)) return null;
            return string.IsNullOrWhiteSpace(chosen) ? null : chosen;
        }

        private static string ReadRegionCode()
        {
            try
            {
                string? code = RegionInfo.CurrentRegion?.TwoLetterISORegionName;
                if (!string.IsNullOrWhiteSpace(code) && code!.Length == 2)
                    return code.ToUpperInvariant();
            }
            catch { }
            return "ZZ";
        }

        private static List<JObject> ReadEvents()
        {
            var list = new List<JObject>();
            string path = BufferPath();
            lock (FileLock)
            {
                if (!File.Exists(path)) return list;
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { list.Add(JObject.Parse(line)); } catch { }
                }
            }
            return list;
        }

        private static void ClearBuffer()
        {
            string path = BufferPath();
            lock (FileLock)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }
}
