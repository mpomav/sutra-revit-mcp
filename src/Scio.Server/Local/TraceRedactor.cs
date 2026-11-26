using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Redacts local file paths, project filenames, and email addresses while preserving
    /// engineering numeric values needed by scio_distill and scio_repair_memory.
    /// </summary>
    public static class TraceRedactor
    {
        private static readonly Regex WindowsPathRx = new Regex(@"(?i)(?:[a-z]:\\|\\\\[a-z0-9_.-]+\\)[^\s""'<>|]+", RegexOptions.Compiled);
        private static readonly Regex UnixPathRx = new Regex(@"(?:/(?:Users|home|var|tmp|mnt|opt)/[^\s""'<>|]+)", RegexOptions.Compiled);
        private static readonly Regex ProjectFileRx = new Regex(@"(?i)\b[\w.-]+\.(?:rvt|rfa|rte|dwg|dxf|ifc|nwc|nwd|pdf|xlsx|csv)\b", RegexOptions.Compiled);
        private static readonly Regex EmailRx = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);

        public static string RedactString(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string s = WindowsPathRx.Replace(input!, "<redacted_path>");
            s = UnixPathRx.Replace(s, "<redacted_path>");
            s = EmailRx.Replace(s, "<redacted_email>");
            s = ProjectFileRx.Replace(s, "<redacted_file>");
            return s;
        }

        public static JToken RedactToken(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return JValue.CreateNull();
            switch (token.Type)
            {
                case JTokenType.String:
                    return new JValue(RedactString(token.Value<string>()));
                case JTokenType.Object:
                    var obj = new JObject();
                    foreach (var p in ((JObject)token).Properties())
                    {
                        if (string.Equals(p.Name, "target_document", StringComparison.OrdinalIgnoreCase))
                        {
                            obj[p.Name] = HashShort(p.Value?.ToString());
                        }
                        else
                        {
                            obj[p.Name] = RedactToken(p.Value);
                        }
                    }
                    return obj;
                case JTokenType.Array:
                    var arr = new JArray();
                    foreach (var item in (JArray)token) arr.Add(RedactToken(item));
                    return arr;
                default:
                    // Numbers, booleans, etc. are preserved intact for scio_distill.
                    return token.DeepClone();
            }
        }

        public static string HashShort(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value!.Trim()));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Records redacted per-session JSONL traces outside the repository (ScioRuntimePaths.DataRoot\traces)
    /// for scio_distill and local usage insights.
    /// </summary>
    public sealed class SessionTraceRecorder
    {
        private readonly object _lock = new object();
        private readonly string _tracesDir;
        private readonly int _ttlDays;
        private int _seq;

        public string SessionId { get; }
        public bool Enabled { get; set; } = true;

        public SessionTraceRecorder(string? tracesDir = null, string? sessionId = null, int ttlDays = 30)
        {
            _tracesDir = string.IsNullOrWhiteSpace(tracesDir)
                ? Path.Combine(ScioRuntimePaths.DataRoot, "traces")
                : tracesDir!;
            SessionId = string.IsNullOrWhiteSpace(sessionId)
                ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)
                : sessionId!;
            _ttlDays = Math.Max(1, ttlDays);
        }

        public string SessionFilePath => Path.Combine(_tracesDir, SessionId + ".jsonl");

        public void Record(ToolRequest request, ToolResponse response, long durationMs)
        {
            if (!Enabled || request == null || response == null) return;
            if (string.IsNullOrWhiteSpace(request.Tool)) return;
            // Skip meta-introspection tools so traces stay focused on BIM workflows.
            if (request.Tool.StartsWith("_scio_", StringComparison.Ordinal)) return;

            try
            {
                Directory.CreateDirectory(_tracesDir);
                int seq;
                lock (_lock)
                {
                    seq = ++_seq;
                    var entry = new JObject
                    {
                        ["schema"] = "sutra.trace/1",
                        ["session_id"] = SessionId,
                        ["seq"] = seq,
                        ["timestamp_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        ["tool"] = request.Tool,
                        ["arguments"] = TraceRedactor.RedactToken(request.Arguments ?? new JObject()),
                        ["target_document_hash"] = TraceRedactor.HashShort(request.TargetDocument),
                        ["dry_run"] = request.DryRun ?? request.Arguments?.Value<bool?>("dry_run"),
                        ["ok"] = response.Ok,
                        ["error_code"] = response.Error?.Code,
                        ["verification"] = response.Verification ?? string.Empty,
                        ["result_shape"] = SummarizeShape(response.Data),
                        ["duration_ms"] = Math.Max(0, durationMs)
                    };

                    File.AppendAllText(SessionFilePath, entry.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                }

                if (seq == 1) PruneExpiredTraces();
            }
            catch
            {
                // Tracing must never break tool execution.
            }
        }

        public static List<JObject> ReadSession(string tracesDir, string sessionId)
        {
            var list = new List<JObject>();
            if (string.IsNullOrWhiteSpace(sessionId)) return list;
            string safeId = Path.GetFileNameWithoutExtension(sessionId.Trim());
            string path = Path.Combine(tracesDir, safeId + ".jsonl");
            if (!File.Exists(path)) return list;

            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { list.Add(JObject.Parse(line)); } catch { }
            }
            return list;
        }

        public static List<string> ListSessionIds(string tracesDir, int limit = 50)
        {
            if (!Directory.Exists(tracesDir)) return new List<string>();
            return Directory.GetFiles(tracesDir, "*.jsonl")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(Math.Max(1, limit))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToList();
        }

        private void PruneExpiredTraces()
        {
            try
            {
                if (!Directory.Exists(_tracesDir)) return;
                DateTime cutoff = DateTime.UtcNow.AddDays(-_ttlDays);
                foreach (string file in Directory.GetFiles(_tracesDir, "*.jsonl"))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        private static JObject SummarizeShape(JToken? data)
        {
            if (data == null || data.Type == JTokenType.Null)
                return new JObject { ["type"] = "null" };

            if (data is JObject obj)
            {
                var shape = new JObject
                {
                    ["type"] = "object",
                    ["keys"] = new JArray(obj.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
                };
                foreach (string k in new[] { "count", "total_sets", "quantity", "updated_hosts", "rebar_id", "host_id" })
                {
                    if (obj[k] != null && (obj[k]!.Type == JTokenType.Integer || obj[k]!.Type == JTokenType.Float))
                        shape[k] = obj[k];
                }
                return shape;
            }

            if (data is JArray arr)
            {
                return new JObject { ["type"] = "array", ["count"] = arr.Count };
            }

            return new JObject { ["type"] = data.Type.ToString().ToLowerInvariant() };
        }
    }
}
