using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Governed promotion ladder for distilled scripts (scio_promote_script):
    /// propose -> review -> approve -> activate -> export_pyrevit (active generations only).
    /// </summary>
    public static class PromoteScriptTool
    {
        public const string ToolName = "scio_promote_script";
        private static readonly object StoreLock = new object();
        private static readonly Regex SafeSlugRx = new Regex(@"[^A-Za-z0-9_-]+", RegexOptions.Compiled);

        public static string DefaultStorePath() =>
            Path.Combine(ScioRuntimePaths.DataRoot, "promoted-scripts", "registry.json");

        public static JObject Handle(JObject args, CancellationToken ct, string? storePathOverride = null)
        {
            string op = (args?.Value<string>("operation") ?? "list").Trim().ToLowerInvariant();
            string storePath = string.IsNullOrWhiteSpace(storePathOverride) ? DefaultStorePath() : storePathOverride!;

            switch (op)
            {
                case "list": return HandleList(args, storePath);
                case "show": return HandleShow(args, storePath);
                case "propose": return HandlePropose(args, storePath);
                case "review": return HandleTransition(args, storePath, "review", "proposed", "reviewed");
                case "approve": return HandleTransition(args, storePath, "approve", "reviewed", "approved");
                case "activate": return HandleActivate(args, storePath);
                case "deactivate": return HandleDeactivate(args, storePath);
                case "source": return HandleSource(args, storePath);
                case "resolve": return HandleResolve(args, storePath);
                case "invocation": return HandleInvocation(args, storePath);
                case "export_pyrevit": return HandleExportPyRevit(args, storePath);
                default:
                    throw new ToolRefusalException(
                        $"Unsupported operation '{op}'. Expected: list, show, propose, review, approve, activate, deactivate, source, resolve, invocation, export_pyrevit.");
            }
        }

        private static JObject HandleList(JObject? args, string storePath)
        {
            string? statusFilter = args?.Value<string>("status")?.Trim().ToLowerInvariant();
            var items = LoadItems(storePath)
                .Where(x => string.IsNullOrWhiteSpace(statusFilter) ||
                            string.Equals(x.Value<string>("status"), statusFilter, StringComparison.OrdinalIgnoreCase))
                .Select(Summarize)
                .ToList();

            return new JObject
            {
                ["operation"] = "list",
                ["count"] = items.Count,
                ["generations"] = new JArray(items)
            };
        }

        private static JObject HandleShow(JObject? args, string storePath)
        {
            var item = RequireGeneration(LoadItems(storePath), args);
            return new JObject { ["operation"] = "show", ["generation"] = item.DeepClone() };
        }

        private static JObject HandleSource(JObject? args, string storePath)
        {
            var item = RequireGeneration(LoadItems(storePath), args);
            return new JObject
            {
                ["operation"] = "source",
                ["id"] = item["id"],
                ["name"] = item["name"],
                ["status"] = item["status"],
                ["source"] = item["source"]
            };
        }

        private static JObject HandleResolve(JObject? args, string storePath)
        {
            string name = (args?.Value<string>("name") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new ToolRefusalException("'name' is required for resolve.");

            var active = LoadItems(storePath).FirstOrDefault(x =>
                string.Equals(x.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Value<string>("status"), "active", StringComparison.OrdinalIgnoreCase));

            if (active == null)
                throw new ToolRefusalException($"No active promoted generation found for '{name}'.");

            return new JObject { ["operation"] = "resolve", ["generation"] = Summarize(active) };
        }

        private static JObject HandleInvocation(JObject? args, string storePath)
        {
            var item = RequireGeneration(LoadItems(storePath), args);
            if (!string.Equals(item.Value<string>("status"), "active", StringComparison.OrdinalIgnoreCase))
                throw new ToolRefusalException($"Generation '{item["id"]}' is '{item["status"]}', not 'active'.");

            return new JObject
            {
                ["operation"] = "invocation",
                ["id"] = item["id"],
                ["name"] = item["name"],
                ["min_permission_profile"] = item["min_permission_profile"],
                ["contract"] = item["contract"]
            };
        }

        private static JObject HandlePropose(JObject? args, string storePath)
        {
            string name = (args?.Value<string>("name") ?? string.Empty).Trim();
            string source = args?.Value<string>("source") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
                throw new ToolRefusalException("Both 'name' and 'source' are required for propose.");

            string minPerm = (args?.Value<string>("min_permission_profile") ?? "SafeWrite").Trim();
            string dllVersion = (args?.Value<string>("sutra_dll_version") ?? "1.0.0").Trim();
            string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            lock (StoreLock)
            {
                var items = LoadItemsUnlocked(storePath);
                int nextVer = items
                    .Where(x => string.Equals(x.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Value<int?>("version") ?? 0)
                    .DefaultIfEmpty(0)
                    .Max() + 1;

                var gen = new JObject
                {
                    ["id"] = $"{SanitizeSlug(name)}-v{nextVer}-{Guid.NewGuid().ToString("N").Substring(0, 6)}",
                    ["name"] = name,
                    ["version"] = nextVer,
                    ["status"] = "proposed",
                    ["min_permission_profile"] = minPerm,
                    ["sutra_dll_version"] = dllVersion,
                    ["source"] = source,
                    ["contract"] = args?["contract"]?.DeepClone() ?? new JObject(),
                    ["evidence"] = args?["evidence"]?.DeepClone() ?? new JObject(),
                    ["history"] = new JArray(new JObject
                    {
                        ["action"] = "propose",
                        ["at_utc"] = now,
                        ["note"] = TraceRedactor.RedactString(args?.Value<string>("note") ?? "Proposed via scio_promote_script")
                    }),
                    ["created_utc"] = now,
                    ["updated_utc"] = now
                };

                items.Add(gen);
                SaveItemsUnlocked(storePath, items);
                return new JObject { ["operation"] = "propose", ["generation"] = Summarize(gen) };
            }
        }

        private static JObject HandleTransition(JObject? args, string storePath, string action, string requiredCurrent, string nextStatus)
        {
            lock (StoreLock)
            {
                var items = LoadItemsUnlocked(storePath);
                var target = RequireGeneration(items, args);
                string current = target.Value<string>("status") ?? string.Empty;
                if (!string.Equals(current, requiredCurrent, StringComparison.OrdinalIgnoreCase))
                    throw new ToolRefusalException(
                        $"Cannot '{action}' generation '{target["id"]}': status is '{current}', expected '{requiredCurrent}'.");

                string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                target["status"] = nextStatus;
                target["updated_utc"] = now;
                AppendHistory(target, action, args?.Value<string>("note") ?? string.Empty, now);
                SaveItemsUnlocked(storePath, items);
                return new JObject { ["operation"] = action, ["generation"] = Summarize(target) };
            }
        }

        private static JObject HandleActivate(JObject? args, string storePath)
        {
            lock (StoreLock)
            {
                var items = LoadItemsUnlocked(storePath);
                var target = RequireGeneration(items, args);
                string current = target.Value<string>("status") ?? string.Empty;
                if (!string.Equals(current, "approved", StringComparison.OrdinalIgnoreCase))
                    throw new ToolRefusalException(
                        $"Cannot activate generation '{target["id"]}': status is '{current}', must be 'approved' first.");

                string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                string name = target.Value<string>("name") ?? string.Empty;
                foreach (var other in items.Where(x =>
                    !ReferenceEquals(x, target) &&
                    string.Equals(x.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Value<string>("status"), "active", StringComparison.OrdinalIgnoreCase)))
                {
                    other["status"] = "inactive";
                    other["updated_utc"] = now;
                    AppendHistory(other, "superseded", $"Superseded by {target["id"]}", now);
                }

                target["status"] = "active";
                target["updated_utc"] = now;
                AppendHistory(target, "activate", args?.Value<string>("note") ?? "Activated", now);
                SaveItemsUnlocked(storePath, items);
                return new JObject { ["operation"] = "activate", ["generation"] = Summarize(target) };
            }
        }

        private static JObject HandleDeactivate(JObject? args, string storePath)
        {
            lock (StoreLock)
            {
                var items = LoadItemsUnlocked(storePath);
                var target = RequireGeneration(items, args);
                string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                target["status"] = "inactive";
                target["updated_utc"] = now;
                AppendHistory(target, "deactivate", args?.Value<string>("note") ?? "Deactivated", now);
                SaveItemsUnlocked(storePath, items);
                return new JObject { ["operation"] = "deactivate", ["generation"] = Summarize(target) };
            }
        }

        private static JObject HandleExportPyRevit(JObject? args, string storePath)
        {
            var items = LoadItems(storePath);
            var target = RequireGeneration(items, args);
            string status = target.Value<string>("status") ?? string.Empty;

            // F6 Gate: only ACTIVE generations may be exported to a pyRevit pushbutton.
            if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolRefusalException(
                    $"Refused export_pyrevit: generation '{target["id"]}' has status '{status}'. Only 'active' generations can be exported.");
            }

            string extensionRoot = (args?.Value<string>("extension_root") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(extensionRoot))
                throw new ToolRefusalException("'extension_root' (path to local *.extension directory) is required for export_pyrevit.");

            string tabName = SanitizeSlug(args?.Value<string>("tab_name") ?? "SCIO-Local");
            string panelName = SanitizeSlug(args?.Value<string>("panel_name") ?? "Distilled");
            string flowSlug = SanitizeSlug(target.Value<string>("name") ?? "Flow");
            string buttonDir = Path.Combine(extensionRoot, tabName + ".tab", panelName + ".panel", flowSlug + ".pushbutton");
            Directory.CreateDirectory(buttonDir);

            string scriptPath = Path.Combine(buttonDir, "script.py");
            string bundlePath = Path.Combine(buttonDir, "bundle.yaml");
            string source = target.Value<string>("source") ?? string.Empty;
            string minPerm = target.Value<string>("min_permission_profile") ?? "SafeWrite";
            string dllVersion = target.Value<string>("sutra_dll_version") ?? "1.0.0";

            File.WriteAllText(scriptPath, source, new UTF8Encoding(false));
            string bundleYaml =
                $"title: \"{flowSlug}\"\n" +
                $"tooltip: \"Distilled Sutra flow ({target["id"]}). Runs locally without LLM.\"\n" +
                $"author: \"SCIO DC (scio_distill)\"\n" +
                $"context: doc-project\n" +
                $"# Sutra governance metadata\n" +
                $"# generation_id: {target["id"]}\n" +
                $"# min_permission_profile: {minPerm}\n" +
                $"# sutra_dll_version: {dllVersion}\n";
            File.WriteAllText(bundlePath, bundleYaml, new UTF8Encoding(false));

            return new JObject
            {
                ["operation"] = "export_pyrevit",
                ["generation_id"] = target["id"],
                ["button_dir"] = buttonDir,
                ["script_path"] = scriptPath,
                ["bundle_path"] = bundlePath,
                ["min_permission_profile"] = minPerm,
                ["sutra_dll_version"] = dllVersion
            };
        }

        private static JObject RequireGeneration(List<JObject> items, JObject? args)
        {
            string id = (args?.Value<string>("id") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
                throw new ToolRefusalException("'id' is required.");

            var found = items.FirstOrDefault(x => string.Equals(x.Value<string>("id"), id, StringComparison.Ordinal));
            if (found == null)
                throw new ToolRefusalException($"Generation '{id}' not found.");
            return found;
        }

        private static void AppendHistory(JObject target, string action, string note, string nowUtc)
        {
            var hist = target["history"] as JArray ?? new JArray();
            hist.Add(new JObject
            {
                ["action"] = action,
                ["at_utc"] = nowUtc,
                ["note"] = TraceRedactor.RedactString(note)
            });
            target["history"] = hist;
        }

        private static JObject Summarize(JObject item) => new JObject
        {
            ["id"] = item["id"],
            ["name"] = item["name"],
            ["version"] = item["version"],
            ["status"] = item["status"],
            ["min_permission_profile"] = item["min_permission_profile"],
            ["sutra_dll_version"] = item["sutra_dll_version"],
            ["created_utc"] = item["created_utc"],
            ["updated_utc"] = item["updated_utc"]
        };

        private static string SanitizeSlug(string raw)
        {
            string s = SafeSlugRx.Replace((raw ?? "flow").Trim(), "_").Trim('_');
            return string.IsNullOrEmpty(s) ? "flow" : s;
        }

        private static List<JObject> LoadItems(string storePath)
        {
            lock (StoreLock) return LoadItemsUnlocked(storePath);
        }

        private static List<JObject> LoadItemsUnlocked(string storePath)
        {
            if (!File.Exists(storePath)) return new List<JObject>();
            try
            {
                var arr = JArray.Parse(File.ReadAllText(storePath, Encoding.UTF8));
                return arr.OfType<JObject>().ToList();
            }
            catch { return new List<JObject>(); }
        }

        private static void SaveItemsUnlocked(string storePath, List<JObject> items)
        {
            string? dir = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(storePath, new JArray(items).ToString(Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
