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
    /// Local redacted failure-and-remedy memory (scio_repair_memory).
    /// Operations: list, advice, observe, remedy, quarantine, release.
    /// </summary>
    public static class RepairMemoryTool
    {
        public const string ToolName = "scio_repair_memory";
        private static readonly object StoreLock = new object();

        public static string DefaultStorePath() =>
            Path.Combine(ScioRuntimePaths.DataRoot, "repair-memory", "memory.json");

        public static JObject Handle(JObject args, CancellationToken ct, string? storePathOverride = null)
        {
            string op = (args?.Value<string>("operation") ?? "list").Trim().ToLowerInvariant();
            string storePath = string.IsNullOrWhiteSpace(storePathOverride) ? DefaultStorePath() : storePathOverride!;

            switch (op)
            {
                case "list": return HandleList(args, storePath);
                case "advice": return HandleAdvice(args, storePath);
                case "observe": return HandleObserve(args, storePath);
                case "remedy": return HandleRemedy(args, storePath);
                case "quarantine": return HandleQuarantine(args, storePath, true);
                case "release": return HandleQuarantine(args, storePath, false);
                default:
                    throw new ToolRefusalException(
                        $"Unsupported operation '{op}'. Expected: list, advice, observe, remedy, quarantine, release.");
            }
        }

        private static JObject HandleList(JObject? args, string storePath)
        {
            string? toolFilter = args?.Value<string>("tool")?.Trim();
            bool includeQuarantined = args?.Value<bool?>("include_quarantined") ?? false;
            var entries = LoadEntries(storePath)
                .Where(e => includeQuarantined || !(e.Value<bool?>("quarantined") ?? false))
                .Where(e => string.IsNullOrWhiteSpace(toolFilter) ||
                            string.Equals(e.Value<string>("tool"), toolFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new JObject
            {
                ["operation"] = "list",
                ["count"] = entries.Count,
                ["entries"] = new JArray(entries)
            };
        }

        private static JObject HandleAdvice(JObject? args, string storePath)
        {
            string tool = (args?.Value<string>("tool") ?? string.Empty).Trim();
            string symptom = TraceRedactor.RedactString(args?.Value<string>("error") ?? args?.Value<string>("symptom") ?? string.Empty);
            string errorCode = (args?.Value<string>("error_code") ?? string.Empty).Trim();

            var active = LoadEntries(storePath)
                .Where(e => !(e.Value<bool?>("quarantined") ?? false))
                .Where(e => !string.IsNullOrWhiteSpace(e.Value<string>("remedy")))
                .ToList();

            var matches = active
                .Select(e => new { Entry = e, Score = ScoreMatch(e, tool, errorCode, symptom) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(5)
                .Select(x => new JObject
                {
                    ["id"] = x.Entry["id"],
                    ["tool"] = x.Entry["tool"],
                    ["error_code"] = x.Entry["error_code"],
                    ["symptom"] = x.Entry["symptom"],
                    ["remedy"] = x.Entry["remedy"],
                    ["occurrences"] = x.Entry["occurrences"],
                    ["score"] = x.Score
                })
                .ToList();

            return new JObject
            {
                ["operation"] = "advice",
                ["match_count"] = matches.Count,
                ["matches"] = new JArray(matches)
            };
        }

        private static JObject HandleObserve(JObject? args, string storePath)
        {
            string tool = (args?.Value<string>("tool") ?? string.Empty).Trim();
            string errorCode = (args?.Value<string>("error_code") ?? string.Empty).Trim();
            string symptom = TraceRedactor.RedactString(args?.Value<string>("symptom") ?? args?.Value<string>("error") ?? string.Empty);

            if (string.IsNullOrWhiteSpace(tool) && string.IsNullOrWhiteSpace(symptom))
                throw new ToolRefusalException("At least 'tool' or 'symptom' is required for observe.");

            lock (StoreLock)
            {
                var entries = LoadEntriesUnlocked(storePath);
                var existing = entries.FirstOrDefault(e =>
                    string.Equals(e.Value<string>("tool"), tool, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Value<string>("error_code"), errorCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Value<string>("symptom"), symptom, StringComparison.OrdinalIgnoreCase));

                string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                if (existing != null)
                {
                    existing["occurrences"] = (existing.Value<int?>("occurrences") ?? 1) + 1;
                    existing["last_seen_utc"] = now;
                    SaveEntriesUnlocked(storePath, entries);
                    return new JObject { ["operation"] = "observe", ["created"] = false, ["entry"] = existing.DeepClone() };
                }

                var entry = new JObject
                {
                    ["id"] = "rm-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    ["tool"] = tool,
                    ["error_code"] = errorCode,
                    ["symptom"] = symptom,
                    ["remedy"] = string.Empty,
                    ["occurrences"] = 1,
                    ["quarantined"] = false,
                    ["created_utc"] = now,
                    ["last_seen_utc"] = now
                };
                entries.Add(entry);
                SaveEntriesUnlocked(storePath, entries);
                return new JObject { ["operation"] = "observe", ["created"] = true, ["entry"] = entry.DeepClone() };
            }
        }

        private static JObject HandleRemedy(JObject? args, string storePath)
        {
            string id = (args?.Value<string>("id") ?? string.Empty).Trim();
            string remedy = TraceRedactor.RedactString(args?.Value<string>("remedy") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(remedy))
                throw new ToolRefusalException("Both 'id' and 'remedy' are required.");

            lock (StoreLock)
            {
                var entries = LoadEntriesUnlocked(storePath);
                var target = entries.FirstOrDefault(e => string.Equals(e.Value<string>("id"), id, StringComparison.Ordinal));
                if (target == null)
                    throw new ToolRefusalException($"Repair memory entry '{id}' not found.");

                target["remedy"] = remedy;
                target["remedy_updated_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveEntriesUnlocked(storePath, entries);
                return new JObject { ["operation"] = "remedy", ["entry"] = target.DeepClone() };
            }
        }

        private static JObject HandleQuarantine(JObject? args, string storePath, bool quarantine)
        {
            string id = (args?.Value<string>("id") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
                throw new ToolRefusalException("'id' is required.");

            lock (StoreLock)
            {
                var entries = LoadEntriesUnlocked(storePath);
                var target = entries.FirstOrDefault(e => string.Equals(e.Value<string>("id"), id, StringComparison.Ordinal));
                if (target == null)
                    throw new ToolRefusalException($"Repair memory entry '{id}' not found.");

                target["quarantined"] = quarantine;
                if (quarantine)
                    target["quarantine_reason"] = TraceRedactor.RedactString(args?.Value<string>("reason") ?? "owner_quarantine");
                SaveEntriesUnlocked(storePath, entries);
                return new JObject
                {
                    ["operation"] = quarantine ? "quarantine" : "release",
                    ["entry"] = target.DeepClone()
                };
            }
        }

        private static int ScoreMatch(JObject entry, string tool, string errorCode, string symptom)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(tool) &&
                string.Equals(entry.Value<string>("tool"), tool, StringComparison.OrdinalIgnoreCase))
                score += 4;
            if (!string.IsNullOrWhiteSpace(errorCode) &&
                string.Equals(entry.Value<string>("error_code"), errorCode, StringComparison.OrdinalIgnoreCase))
                score += 4;

            if (!string.IsNullOrWhiteSpace(symptom))
            {
                string entrySymptom = (entry.Value<string>("symptom") ?? string.Empty).ToLowerInvariant();
                var tokens = symptom.ToLowerInvariant().Split(new[] { ' ', ',', '.', ':', ';', '/', '\\', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length >= 3)
                    .Distinct();
                foreach (string tok in tokens)
                {
                    if (entrySymptom.Contains(tok)) score += 1;
                }
            }
            return score;
        }

        private static List<JObject> LoadEntries(string storePath)
        {
            lock (StoreLock) return LoadEntriesUnlocked(storePath);
        }

        private static List<JObject> LoadEntriesUnlocked(string storePath)
        {
            if (!File.Exists(storePath)) return new List<JObject>();
            try
            {
                var arr = JArray.Parse(File.ReadAllText(storePath, Encoding.UTF8));
                return arr.OfType<JObject>().ToList();
            }
            catch { return new List<JObject>(); }
        }

        private static void SaveEntriesUnlocked(string storePath, List<JObject> entries)
        {
            string? dir = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(storePath, new JArray(entries).ToString(Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
