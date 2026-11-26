using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Stateful guided BIM procedure engine (scio_run_procedure):
    /// start, advance, decide, record, reconcile, status, abandon.
    /// </summary>
    public static class ProcedureRunnerTool
    {
        public const string ToolName = "scio_run_procedure";
        private static readonly object StoreLock = new object();

        private static readonly Dictionary<string, string[]> BuiltInPlaybooks = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["steel_rebar_qa"] = new[] { "inspect_rebar_and_hosts", "verify_exact_bar_types", "check_clear_cover", "preview_rebar_writes", "commit_and_verify" },
            ["finish_schedule_governance"] = new[] { "inspect_room_boundaries", "audit_finish_parameters", "generate_finish_preview", "commit_and_verify" },
            ["sheet_layout_qa"] = new[] { "plan_unplaced_views", "check_viewport_overlaps", "resolve_overlaps_preview", "sync_sheet_references" },
            ["room_numbering"] = new[] { "list_placed_rooms", "validate_numbering_rules", "preview_param_writes", "commit_and_verify" }
        };

        public static string DefaultStorePath() => Path.Combine(ScioRuntimePaths.DataRoot, "procedures", "state.json");

        public static JObject Handle(JObject args, CancellationToken ct, string? storePathOverride = null)
        {
            string op = (args?.Value<string>("operation") ?? "status").Trim().ToLowerInvariant();
            string storePath = string.IsNullOrWhiteSpace(storePathOverride) ? DefaultStorePath() : storePathOverride!;

            switch (op)
            {
                case "start": return HandleStart(args, storePath);
                case "advance": return HandleAdvance(args, storePath);
                case "decide": return HandleDecide(args, storePath);
                case "record": return HandleRecord(args, storePath);
                case "reconcile": return HandleReconcile(args, storePath);
                case "status": return HandleStatus(args, storePath);
                case "abandon": return HandleAbandon(args, storePath);
                default:
                    throw new ToolRefusalException($"Unsupported operation '{op}'. Expected: start, advance, decide, record, reconcile, status, abandon.");
            }
        }

        private static JObject HandleStart(JObject? args, string storePath)
        {
            string name = (args?.Value<string>("procedure") ?? "custom").Trim();
            var customSteps = (args?["steps"] as JArray)?.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            string[] steps = customSteps != null && customSteps.Count > 0
                ? customSteps.ToArray()
                : (BuiltInPlaybooks.TryGetValue(name, out var preset) ? preset : new[] { "inspect", "preview", "apply", "verify" });

            string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var proc = new JObject
            {
                ["id"] = "proc-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ["procedure"] = name,
                ["status"] = "active",
                ["current_step"] = 0,
                ["steps"] = new JArray(steps),
                ["decisions"] = new JArray(),
                ["records"] = new JArray(),
                ["started_utc"] = now,
                ["updated_utc"] = now
            };

            lock (StoreLock)
            {
                var list = LoadUnlocked(storePath);
                list.Add(proc);
                SaveUnlocked(storePath, list);
            }
            return new JObject { ["operation"] = "start", ["procedure"] = proc.DeepClone() };
        }

        private static JObject HandleAdvance(JObject? args, string storePath)
        {
            lock (StoreLock)
            {
                var list = LoadUnlocked(storePath);
                var proc = RequireProc(list, args);
                if (!string.Equals(proc.Value<string>("status"), "active", StringComparison.OrdinalIgnoreCase))
                    throw new ToolRefusalException($"Procedure '{proc["id"]}' is '{proc["status"]}', not active.");

                var steps = proc["steps"] as JArray ?? new JArray();
                int next = (proc.Value<int?>("current_step") ?? 0) + 1;
                proc["current_step"] = next;
                if (next >= steps.Count) proc["status"] = "completed";
                proc["updated_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnlocked(storePath, list);
                return new JObject { ["operation"] = "advance", ["procedure"] = proc.DeepClone() };
            }
        }

        private static JObject HandleDecide(JObject? args, string storePath)
        {
            lock (StoreLock)
            {
                var list = LoadUnlocked(storePath);
                var proc = RequireProc(list, args);
                var decisions = proc["decisions"] as JArray ?? new JArray();
                decisions.Add(new JObject
                {
                    ["step"] = proc["current_step"],
                    ["choice"] = TraceRedactor.RedactString(args?.Value<string>("choice") ?? string.Empty),
                    ["rationale"] = TraceRedactor.RedactString(args?.Value<string>("rationale") ?? string.Empty),
                    ["at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                });
                proc["decisions"] = decisions;
                proc["updated_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnlocked(storePath, list);
                return new JObject { ["operation"] = "decide", ["procedure"] = proc.DeepClone() };
            }
        }

        private static JObject HandleRecord(JObject? args, string storePath)
        {
            lock (StoreLock)
            {
                var list = LoadUnlocked(storePath);
                var proc = RequireProc(list, args);
                var recs = proc["records"] as JArray ?? new JArray();
                recs.Add(new JObject
                {
                    ["step"] = proc["current_step"],
                    ["evidence"] = TraceRedactor.RedactToken(args?["evidence"] ?? new JValue(args?.Value<string>("note") ?? string.Empty)),
                    ["at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                });
                proc["records"] = recs;
                proc["updated_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnlocked(storePath, list);
                return new JObject { ["operation"] = "record", ["procedure"] = proc.DeepClone() };
            }
        }

        private static JObject HandleReconcile(JObject? args, string storePath)
        {
            var list = Load(storePath);
            var proc = RequireProc(list, args);
            int cur = proc.Value<int?>("current_step") ?? 0;
            int total = (proc["steps"] as JArray)?.Count ?? 0;
            int recCount = (proc["records"] as JArray)?.Count ?? 0;
            return new JObject
            {
                ["operation"] = "reconcile",
                ["id"] = proc["id"],
                ["status"] = proc["status"],
                ["steps_completed"] = Math.Min(cur, total),
                ["steps_total"] = total,
                ["evidence_records"] = recCount,
                ["complete_with_evidence"] = cur >= total && recCount > 0
            };
        }

        private static JObject HandleStatus(JObject? args, string storePath)
        {
            var list = Load(storePath);
            string? id = args?.Value<string>("id")?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                var proc = RequireProc(list, args);
                return new JObject { ["operation"] = "status", ["procedure"] = proc.DeepClone() };
            }
            return new JObject
            {
                ["operation"] = "status",
                ["available_playbooks"] = new JArray(BuiltInPlaybooks.Keys),
                ["count"] = list.Count,
                ["procedures"] = new JArray(list.Select(p => new JObject
                {
                    ["id"] = p["id"],
                    ["procedure"] = p["procedure"],
                    ["status"] = p["status"],
                    ["current_step"] = p["current_step"]
                }))
            };
        }

        private static JObject HandleAbandon(JObject? args, string storePath)
        {
            lock (StoreLock)
            {
                var list = LoadUnlocked(storePath);
                var proc = RequireProc(list, args);
                proc["status"] = "abandoned";
                proc["updated_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnlocked(storePath, list);
                return new JObject { ["operation"] = "abandon", ["procedure"] = proc.DeepClone() };
            }
        }

        private static JObject RequireProc(List<JObject> list, JObject? args)
        {
            string id = (args?.Value<string>("id") ?? string.Empty).Trim();
            var found = list.FirstOrDefault(p => string.Equals(p.Value<string>("id"), id, StringComparison.Ordinal));
            if (found == null) throw new ToolRefusalException($"Procedure '{id}' not found.");
            return found;
        }

        private static List<JObject> Load(string path) { lock (StoreLock) return LoadUnlocked(path); }
        private static List<JObject> LoadUnlocked(string path)
        {
            if (!File.Exists(path)) return new List<JObject>();
            try { return JArray.Parse(File.ReadAllText(path, Encoding.UTF8)).OfType<JObject>().ToList(); }
            catch { return new List<JObject>(); }
        }
        private static void SaveUnlocked(string path, List<JObject> list)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, new JArray(list).ToString(Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
