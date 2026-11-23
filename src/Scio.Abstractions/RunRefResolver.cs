#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Local Run Reference Resolver (@run:<id>, @run:latest)
//
// Revit-free and net48-safe sidecar store under %USERPROFILE%\.scio\runs\.
// Scio.Server writes lightweight element-id sidecars upon SQLite ingestion so
// Revit commands resolve "@run:<id>" directly from disk without native sqlite3.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public static class RunRefResolver
    {
        public static string RunsDirectory()
        {
            string dir = Path.Combine(ScioPaths.DataRoot(), "runs");
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
            }
            return dir;
        }

        public static string SanitizeId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            }
            return sb.ToString();
        }

        public static string SidecarPath(string runId)
        {
            string safe = SanitizeId(runId);
            if (safe.Length == 0) throw new ArgumentException("run_id is empty or invalid.");
            return Path.Combine(RunsDirectory(), safe + ".ids.json");
        }

        public static string WriteIdsSidecar(string runId, string command, string modelName, IEnumerable<long> ids)
        {
            string safeId = SanitizeId(runId);
            if (safeId.Length == 0) return null;

            string dir = RunsDirectory();
            var deduped = new List<long>();
            var seen = new HashSet<long>();
            if (ids != null)
            {
                foreach (long id in ids)
                {
                    if (id > 0 && seen.Add(id)) deduped.Add(id);
                }
            }

            var payload = new JObject
            {
                ["run_id"] = safeId,
                ["command"] = command ?? string.Empty,
                ["model_name"] = modelName ?? string.Empty,
                ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["count"] = deduped.Count,
                ["element_ids"] = new JArray(deduped)
            };

            string path = Path.Combine(dir, safeId + ".ids.json");
            WriteJsonAtomic(path, payload);

            var pointer = new JObject
            {
                ["run_id"] = safeId,
                ["command"] = command ?? string.Empty,
                ["model_name"] = modelName ?? string.Empty,
                ["timestamp_utc"] = payload["timestamp_utc"],
                ["count"] = deduped.Count
            };
            WriteJsonAtomic(Path.Combine(dir, "latest.json"), pointer);

            string safeCmd = SanitizeId(command);
            if (safeCmd.Length > 0)
            {
                WriteJsonAtomic(Path.Combine(dir, "latest-" + safeCmd.ToLowerInvariant() + ".json"), pointer);
            }

            return path;
        }

        public static bool IsRunRef(string value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Trim().StartsWith("@run:", StringComparison.OrdinalIgnoreCase);

        public static bool TryResolve(
            string token,
            out List<long> elementIds,
            out string resolvedRunId,
            out string error)
        {
            elementIds = new List<long>();
            resolvedRunId = null;
            error = null;

            if (!IsRunRef(token))
            {
                error = $"'{token}' is not a valid @run:<id> reference.";
                return false;
            }

            string spec = token.Trim().Substring(5).Trim();
            if (spec.Length == 0)
            {
                error = "Missing run id after '@run:'.";
                return false;
            }

            string dir = RunsDirectory();
            string targetId = spec;
            if (string.Equals(spec, "latest", StringComparison.OrdinalIgnoreCase))
            {
                string ptrPath = Path.Combine(dir, "latest.json");
                if (!File.Exists(ptrPath))
                {
                    error = "No local run has been recorded yet ('@run:latest' is empty).";
                    return false;
                }
                try
                {
                    var ptr = JObject.Parse(File.ReadAllText(ptrPath, Encoding.UTF8));
                    targetId = ptr.Value<string>("run_id");
                }
                catch (Exception ex)
                {
                    error = "Could not read latest run pointer: " + ex.Message;
                    return false;
                }
            }
            else if (spec.StartsWith("latest:", StringComparison.OrdinalIgnoreCase))
            {
                string cmdName = SanitizeId(spec.Substring(7)).ToLowerInvariant();
                string ptrPath = Path.Combine(dir, "latest-" + cmdName + ".json");
                if (!File.Exists(ptrPath))
                {
                    error = $"No local run recorded for command '{cmdName}' ('{token}').";
                    return false;
                }
                try
                {
                    var ptr = JObject.Parse(File.ReadAllText(ptrPath, Encoding.UTF8));
                    targetId = ptr.Value<string>("run_id");
                }
                catch (Exception ex)
                {
                    error = $"Could not read latest run pointer for '{cmdName}': " + ex.Message;
                    return false;
                }
            }

            string safeId = SanitizeId(targetId);
            if (safeId.Length == 0)
            {
                error = $"Invalid run id '{targetId}'.";
                return false;
            }

            string sidecar = Path.Combine(dir, safeId + ".ids.json");
            if (!File.Exists(sidecar))
            {
                error = $"Local run '{safeId}' has no stored element_ids sidecar at '{sidecar}'.";
                return false;
            }

            try
            {
                var doc = JObject.Parse(File.ReadAllText(sidecar, Encoding.UTF8));
                resolvedRunId = doc.Value<string>("run_id") ?? safeId;
                var arr = doc["element_ids"] as JArray;
                if (arr != null)
                {
                    foreach (JToken t in arr)
                    {
                        if ((t.Type == JTokenType.Integer || t.Type == JTokenType.Float) &&
                            long.TryParse(t.ToString(Formatting.None), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                        {
                            elementIds.Add(v);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed reading run sidecar '{safeId}': {ex.Message}";
                return false;
            }
        }

        private static void WriteJsonAtomic(string path, JObject obj)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, obj.ToString(Formatting.None), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}

