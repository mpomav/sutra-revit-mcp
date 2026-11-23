#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Host-Resident Local Takeoff Comparison (scio_progress_takeoff)
//
// Compares two metrados runs stored in scio_local.db by partida (and optional
// group_by dimension) without touching Revit. Optional export writes a CSV or
// HTML delta report under Settings.AllowsExternalSideEffect.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    internal static class ProgressTakeoffTool
    {
        public static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();

            bool exportReport = args.Value<bool?>("export_report") == true;
            if (exportReport && !ScioRuntimePaths.AllowsExternalSideEffect(PermissionProfile.SafeWrite, out string deny))
                throw new ToolRefusalException(deny + " Omit export_report or set export_report=false.");

            string baselineId = RunRefResolver.SanitizeId(args.Value<string>("baseline_run_id"));
            string currentId = RunRefResolver.SanitizeId(args.Value<string>("current_run_id"));
            string modelName = args.Value<string>("model_name");

            if (string.IsNullOrEmpty(baselineId) || string.IsNullOrEmpty(currentId))
            {
                JObject listRes = SqliteIngestor.ListRuns("scio_metrado_partidas", modelName, 10);
                var runRows = listRes["rows"] as JArray;
                if (runRows != null && runRows.Count >= 2)
                {
                    if (string.IsNullOrEmpty(currentId)) currentId = runRows[0].Value<string>("run_id");
                    if (string.IsNullOrEmpty(baselineId)) baselineId = runRows[1].Value<string>("run_id");
                }
            }

            if (string.IsNullOrEmpty(baselineId) || string.IsNullOrEmpty(currentId))
                throw new ToolRefusalException("baseline_run_id and current_run_id are required (or at least 2 scio_metrado_partidas runs in scio_local.db).");

            string groupBy = (args.Value<string>("group_by") ?? string.Empty).Trim().ToLowerInvariant();
            if (groupBy.Length > 0 && groupBy != "nivel" && groupBy != "categoria")
                throw new ToolRefusalException("group_by must be empty, 'nivel', or 'categoria'.");

            int maxRows = Math.Max(1, Math.Min(500, args.Value<int?>("max_rows") ?? 50));

            var baseTotals = LoadAggregated(baselineId, groupBy);
            var currTotals = LoadAggregated(currentId, groupBy);

            var allKeys = new HashSet<string>(baseTotals.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string k in currTotals.Keys) allKeys.Add(k);

            int added = 0, removed = 0, changed = 0, unchanged = 0;
            var items = new List<JObject>();

            foreach (string key in allKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                baseTotals.TryGetValue(key, out BucketAgg b);
                currTotals.TryGetValue(key, out BucketAgg c);
                b ??= new BucketAgg { Partida = c.Partida, GroupValue = c.GroupValue };
                c ??= new BucketAgg { Partida = b.Partida, GroupValue = b.GroupValue };

                double dVol = Math.Round(c.Volume - b.Volume, 4);
                double dArea = Math.Round(c.Area - b.Area, 4);
                double dLen = Math.Round(c.Length - b.Length, 4);
                long dCount = c.Count - b.Count;

                string status;
                if (b.Count == 0 && c.Count > 0) { status = "added"; added++; }
                else if (b.Count > 0 && c.Count == 0) { status = "removed"; removed++; }
                else if (Math.Abs(dVol) > 1e-4 || Math.Abs(dArea) > 1e-4 || Math.Abs(dLen) > 1e-4 || dCount != 0)
                {
                    status = "changed";
                    changed++;
                }
                else
                {
                    status = "unchanged";
                    unchanged++;
                }

                double? pctVol = Math.Abs(b.Volume) > 1e-9 ? Math.Round((dVol / b.Volume) * 100.0, 2) : (double?)null;

                var row = new JObject
                {
                    ["partida"] = c.Partida,
                    ["status"] = status,
                    ["baseline_volume_m3"] = Math.Round(b.Volume, 4),
                    ["current_volume_m3"] = Math.Round(c.Volume, 4),
                    ["delta_volume_m3"] = dVol,
                    ["delta_volume_pct"] = pctVol.HasValue ? (JToken)pctVol.Value : JValue.CreateNull(),
                    ["baseline_area_m2"] = Math.Round(b.Area, 4),
                    ["current_area_m2"] = Math.Round(c.Area, 4),
                    ["delta_area_m2"] = dArea,
                    ["baseline_length_m"] = Math.Round(b.Length, 4),
                    ["current_length_m"] = Math.Round(c.Length, 4),
                    ["delta_length_m"] = dLen,
                    ["baseline_count"] = b.Count,
                    ["current_count"] = c.Count,
                    ["delta_count"] = dCount
                };
                if (!string.IsNullOrEmpty(groupBy))
                    row[groupBy] = c.GroupValue;
                items.Add(row);
            }

            var ordered = items
                .OrderByDescending(r => Math.Abs(r.Value<double>("delta_volume_m3")))
                .ThenByDescending(r => Math.Abs(r.Value<double>("delta_area_m2")))
                .ThenBy(r => r.Value<string>("partida"), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var summary = new JObject
            {
                ["total_partidas"] = items.Count,
                ["added"] = added,
                ["removed"] = removed,
                ["changed"] = changed,
                ["unchanged"] = unchanged,
                ["total_delta_volume_m3"] = Math.Round(items.Sum(r => r.Value<double>("delta_volume_m3")), 4),
                ["total_delta_area_m2"] = Math.Round(items.Sum(r => r.Value<double>("delta_area_m2")), 4),
                ["total_delta_length_m"] = Math.Round(items.Sum(r => r.Value<double>("delta_length_m")), 4)
            };

            var res = new JObject
            {
                ["baseline_run_id"] = baselineId,
                ["current_run_id"] = currentId,
                ["written"] = false,
                ["summary"] = summary,
                ["items_shown"] = Math.Min(maxRows, ordered.Count),
                ["items_truncated"] = ordered.Count > maxRows,
                ["items"] = new JArray(ordered.Take(maxRows))
            };

            if (exportReport)
            {
                string dir = Path.Combine(ScioPaths.DataRoot(), "salidas");
                Directory.CreateDirectory(dir);
                string csvPath = Path.Combine(dir, $"progress-takeoff-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
                WriteDeltaCsv(csvPath, ordered, groupBy);
                byte[] bytes = File.ReadAllBytes(csvPath);
                res["written"] = true;
                res["archivo_csv"] = csvPath;
                res["bytes"] = bytes.Length;
                res["sha256"] = ComputeSha256(bytes);
            }

            return res;
        }

        private static Dictionary<string, BucketAgg> LoadAggregated(string runId, string groupBy)
        {
            string safe = runId.Replace("'", "''");
            string extraCol = string.IsNullOrEmpty(groupBy) ? "'' AS grp" : $"{groupBy} AS grp";
            string groupClause = string.IsNullOrEmpty(groupBy) ? "partida" : $"partida, {groupBy}";
            string sql = $"SELECT COALESCE(partida, '(sin partida)') AS partida, {extraCol}, " +
                         $"COUNT(*) AS cnt, SUM(volumen_m3) AS vol, SUM(area_m2) AS area, SUM(longitud_m) AS len " +
                         $"FROM metrados WHERE run_id = '{safe}' GROUP BY {groupClause}";

            JObject qr = SqliteIngestor.QueryReadOnly(sql, 10000);
            var map = new Dictionary<string, BucketAgg>(StringComparer.OrdinalIgnoreCase);
            if (qr.Value<bool?>("ok") != true || !(qr["rows"] is JArray rows)) return map;

            foreach (JObject r in rows.OfType<JObject>())
            {
                string partida = r.Value<string>("partida") ?? "(sin partida)";
                string grp = r.Value<string>("grp") ?? string.Empty;
                string key = string.IsNullOrEmpty(groupBy) ? partida : (partida + "\u001f" + grp);
                map[key] = new BucketAgg
                {
                    Partida = partida,
                    GroupValue = grp,
                    Count = r.Value<long?>("cnt") ?? 0,
                    Volume = r.Value<double?>("vol") ?? 0.0,
                    Area = r.Value<double?>("area") ?? 0.0,
                    Length = r.Value<double?>("len") ?? 0.0
                };
            }
            return map;
        }

        private static void WriteDeltaCsv(string path, List<JObject> items, string groupBy)
        {
            using (var sw = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                sw.WriteLine(string.IsNullOrEmpty(groupBy)
                    ? "partida,status,baseline_volume_m3,current_volume_m3,delta_volume_m3,baseline_area_m2,current_area_m2,delta_area_m2,baseline_length_m,current_length_m,delta_length_m,baseline_count,current_count,delta_count"
                    : $"partida,{groupBy},status,baseline_volume_m3,current_volume_m3,delta_volume_m3,baseline_area_m2,current_area_m2,delta_area_m2,baseline_length_m,current_length_m,delta_length_m,baseline_count,current_count,delta_count");
                foreach (JObject r in items)
                {
                    string p = Esc(r.Value<string>("partida"));
                    string grpCell = string.IsNullOrEmpty(groupBy) ? "" : "," + Esc(r.Value<string>(groupBy));
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}",
                        p, grpCell, r.Value<string>("status"),
                        r.Value<double>("baseline_volume_m3"), r.Value<double>("current_volume_m3"), r.Value<double>("delta_volume_m3"),
                        r.Value<double>("baseline_area_m2"), r.Value<double>("current_area_m2"), r.Value<double>("delta_area_m2"),
                        r.Value<double>("baseline_length_m"), r.Value<double>("current_length_m"), r.Value<double>("delta_length_m"),
                        r.Value<long>("baseline_count"), r.Value<long>("current_count"), r.Value<long>("delta_count")));
                }
            }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }

        private static string ComputeSha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class BucketAgg
        {
            public string Partida;
            public string GroupValue;
            public long Count;
            public double Volume;
            public double Area;
            public double Length;
        }
    }
}

