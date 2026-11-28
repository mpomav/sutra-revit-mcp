#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Host-Resident Local SQLite Query Tool (scio_query_local)
//
// Queries %USERPROFILE%\.scio\scio_local.db locally without touching Revit or
// re-reading model elements over the wire. Supports run listing, schema
// inspection, safe whitelisted aggregation, read-only SQL SELECT, ID extraction
// (saving filtered @run:<id> sidecars for Revit handoff), and run diffs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    internal static class QueryLocalTool
    {
        private static readonly HashSet<string> AllowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "runs", "censo_elements", "metrados", "auditoria_parametros",
            "clashes", "audit_findings", "schedule_rows", "query_rows", "naming_results"
        };

        private static readonly Dictionary<string, HashSet<string>> AllowedColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["censo_elements"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_id", "documento", "origen", "element_id", "categoria", "familia", "tipo", "nivel" },
            ["metrados"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_id", "documento", "origen", "element_id", "partida", "categoria", "familia", "tipo", "nivel", "volumen_m3", "area_m2", "longitud_m" },
            ["auditoria_parametros"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_id", "documento", "origen", "element_id", "categoria", "familia", "tipo", "nivel", "parametro", "estado" },
            ["clashes"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_id", "model_a", "element_a_id", "category_a", "model_b", "element_b_id", "category_b", "volume_m3" },
            ["audit_findings"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_id", "finding_id", "check_name", "severity", "count" },
            ["query_rows"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_id", "element_id", "category", "family", "type", "level", "source_model" },
            ["naming_results"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_id", "scope", "element_id", "status" }
        };

        public static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();
            string op = (args.Value<string>("operation") ?? "list_runs").Trim().ToLowerInvariant();

            switch (op)
            {
                case "list_runs":
                    return SqliteIngestor.ListRuns(
                        args.Value<string>("command"),
                        args.Value<string>("model_name"),
                        args.Value<int?>("limit") ?? 20);

                case "schema":
                    return SqliteIngestor.GetSchema();

                case "sql_select":
                {
                    string sql = args.Value<string>("sql");
                    int maxRows = args.Value<int?>("max_rows") ?? 50;
                    JObject res = SqliteIngestor.QueryReadOnly(sql, maxRows);
                    if (res.Value<bool?>("ok") == false)
                        throw new ToolRefusalException(res.Value<string>("error") ?? "SQL query refused.");
                    return res;
                }

                case "aggregate":
                    return HandleAggregate(args);

                case "get_ids":
                    return HandleGetIds(args);

                case "diff_runs":
                    return HandleDiffRuns(args);

                default:
                    throw new ToolRefusalException(
                        "operation must be list_runs, schema, aggregate, sql_select, get_ids or diff_runs.");
            }
        }

        private static JObject HandleAggregate(JObject args)
        {
            string table = (args.Value<string>("table") ?? string.Empty).Trim().ToLowerInvariant();
            if (!AllowedTables.Contains(table) || !AllowedColumns.TryGetValue(table, out var validCols))
                throw new ToolRefusalException("table must be one of: censo_elements, metrados, auditoria_parametros, clashes, audit_findings, query_rows, naming_results.");

            var groupBy = new List<string>();
            if (args["group_by"] is JArray gbArr)
            {
                foreach (JToken t in gbArr)
                {
                    string col = (t?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
                    if (!validCols.Contains(col))
                        throw new ToolRefusalException($"Column '{col}' is not valid for table '{table}'.");
                    groupBy.Add(col);
                }
            }

            var whereParts = new List<string>();
            string runId = args.Value<string>("run_id");
            if (!string.IsNullOrWhiteSpace(runId))
            {
                whereParts.Add("run_id = '" + Escape(RunRefResolver.SanitizeId(runId)) + "'");
            }
            if (args["where"] is JObject whereObj)
            {
                foreach (var prop in whereObj.Properties())
                {
                    string col = prop.Name.Trim().ToLowerInvariant();
                    if (!validCols.Contains(col))
                        throw new ToolRefusalException($"Filter column '{col}' is not valid for table '{table}'.");
                    whereParts.Add(col + " = '" + Escape(prop.Value?.ToString() ?? string.Empty) + "'");
                }
            }

            var selectExprs = new List<string>();
            selectExprs.AddRange(groupBy);
            selectExprs.Add("COUNT(*) AS row_count");
            if (table == "metrados")
            {
                selectExprs.Add("ROUND(SUM(volumen_m3), 4) AS volumen_m3");
                selectExprs.Add("ROUND(SUM(area_m2), 4) AS area_m2");
                selectExprs.Add("ROUND(SUM(longitud_m), 4) AS longitud_m");
            }
            else if (table == "clashes")
            {
                selectExprs.Add("ROUND(SUM(volume_m3), 6) AS volume_m3");
            }
            else if (table == "audit_findings")
            {
                selectExprs.Add("SUM(count) AS total_findings");
            }

            int maxRows = Math.Max(1, Math.Min(500, args.Value<int?>("max_rows") ?? 100));
            string whereSql = whereParts.Count > 0 ? " WHERE " + string.Join(" AND ", whereParts) : string.Empty;
            string groupSql = groupBy.Count > 0 ? " GROUP BY " + string.Join(", ", groupBy) : string.Empty;
            string orderSql = " ORDER BY row_count DESC";
            string sql = $"SELECT {string.Join(", ", selectExprs)} FROM {table}{whereSql}{groupSql}{orderSql} LIMIT {maxRows}";

            JObject res = SqliteIngestor.QueryReadOnly(sql, maxRows);
            if (res.Value<bool?>("ok") == false)
                throw new ToolRefusalException(res.Value<string>("error") ?? "Aggregation failed.");
            res["table"] = table;
            return res;
        }

        private static JObject HandleGetIds(JObject args)
        {
            string runId = RunRefResolver.SanitizeId(args.Value<string>("run_id"));
            if (runId.Length == 0)
                throw new ToolRefusalException("get_ids requires 'run_id'. Use list_runs first to find available run_ids.");

            JObject runLookup = SqliteIngestor.QueryReadOnly(
                $"SELECT command, model_name FROM runs WHERE run_id = '{Escape(runId)}' LIMIT 1", 1);
            var rArr = runLookup["rows"] as JArray;
            if (rArr == null || rArr.Count == 0)
                throw new ToolRefusalException($"Run '{runId}' was not found in scio_local.db.");

            string cmd = rArr[0].Value<string>("command");
            string modelName = rArr[0].Value<string>("model_name");
            int limit = Math.Max(1, Math.Min(2000, args.Value<int?>("max_rows") ?? 200));

            var whereExtra = new List<string>();
            if (args["where"] is JObject whereObj)
            {
                foreach (var prop in whereObj.Properties())
                {
                    string col = prop.Name.Trim().ToLowerInvariant();
                    if (!System.Text.RegularExpressions.Regex.IsMatch(col, "^[a-z_][a-z0-9_]*$"))
                        throw new ToolRefusalException($"Invalid filter column '{col}'.");
                    whereExtra.Add(col + " = '" + Escape(prop.Value?.ToString() ?? string.Empty) + "'");
                }
            }
            string whereClause = $"run_id = '{Escape(runId)}'" + (whereExtra.Count > 0 ? " AND " + string.Join(" AND ", whereExtra) : "");

            string sql;
            switch (cmd)
            {
                case "scio_censo":
                    sql = $"SELECT DISTINCT element_id FROM censo_elements WHERE {whereClause} AND element_id > 0 LIMIT {limit}";
                    break;
                case "scio_metrado_partidas":
                case "scio_quantities":
                    sql = $"SELECT DISTINCT element_id FROM metrados WHERE {whereClause} AND element_id > 0 LIMIT {limit}";
                    break;
                case "scio_auditoria_parametros":
                    sql = $"SELECT DISTINCT element_id FROM auditoria_parametros WHERE {whereClause} AND element_id > 0 LIMIT {limit}";
                    break;
                case "scio_naming_check":
                    sql = $"SELECT DISTINCT element_id FROM naming_results WHERE {whereClause} AND element_id > 0 LIMIT {limit}";
                    break;
                case "scio_clash":
                    sql = $"SELECT DISTINCT element_a_id AS element_id FROM clashes WHERE {whereClause} AND element_a_id > 0 LIMIT {limit}";
                    break;
                default:
                    sql = $"SELECT DISTINCT element_id FROM query_rows WHERE {whereClause} AND element_id > 0 LIMIT {limit}";
                    break;
            }

            JObject qr = SqliteIngestor.QueryReadOnly(sql, limit);
            if (qr.Value<bool?>("ok") == false)
                throw new ToolRefusalException(qr.Value<string>("error") ?? "Failed querying element_ids.");

            var ids = new List<long>();
            if (qr["rows"] is JArray rows)
            {
                foreach (JObject row in rows.OfType<JObject>())
                {
                    long v = row.Value<long?>("element_id") ?? 0;
                    if (v > 0) ids.Add(v);
                }
            }

            bool saveAsRun = args.Value<bool?>("save_as_run") ?? (ids.Count > 50);
            string savedRunRef = null;
            if (saveAsRun && ids.Count > 0)
            {
                string filteredId = "flt-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                RunRefResolver.WriteIdsSidecar(filteredId, "scio_query_local", modelName, ids);
                savedRunRef = "@run:" + filteredId;
            }

            var outObj = new JObject
            {
                ["source_run_id"] = runId,
                ["source_command"] = cmd,
                ["count"] = ids.Count,
                ["truncated"] = qr.Value<bool?>("truncated") ?? false,
                ["element_ids"] = new JArray(ids.Take(200)),
                ["element_ids_in_response_truncated"] = ids.Count > 200
            };
            if (savedRunRef != null)
            {
                outObj["run_ref"] = savedRunRef;
                outObj["note"] = $"Pass '{savedRunRef}' as element_ids/ids to any Revit command instead of listing IDs.";
            }
            return outObj;
        }

        private static JObject HandleDiffRuns(JObject args)
        {
            string baseId = RunRefResolver.SanitizeId(args.Value<string>("baseline_run_id"));
            string currId = RunRefResolver.SanitizeId(args.Value<string>("current_run_id"));
            if (baseId.Length == 0 || currId.Length == 0)
                throw new ToolRefusalException("diff_runs requires both 'baseline_run_id' and 'current_run_id'.");

            var baseRows = SqliteIngestor.LoadRowsAsObjects(baseId, 10000);
            var currRows = SqliteIngestor.LoadRowsAsObjects(currId, 10000);

            var baseIds = new HashSet<long>(baseRows.Select(r => r.Value<long?>("element_id") ?? 0).Where(id => id > 0));
            var currIds = new HashSet<long>(currRows.Select(r => r.Value<long?>("element_id") ?? 0).Where(id => id > 0));

            var added = currIds.Where(id => !baseIds.Contains(id)).OrderBy(x => x).ToList();
            var removed = baseIds.Where(id => !currIds.Contains(id)).OrderBy(x => x).ToList();

            return new JObject
            {
                ["baseline_run_id"] = baseId,
                ["current_run_id"] = currId,
                ["baseline_rows"] = baseRows.Count,
                ["current_rows"] = currRows.Count,
                ["baseline_unique_ids"] = baseIds.Count,
                ["current_unique_ids"] = currIds.Count,
                ["added_ids_count"] = added.Count,
                ["removed_ids_count"] = removed.Count,
                ["added_ids_sample"] = new JArray(added.Take(50)),
                ["removed_ids_sample"] = new JArray(removed.Take(50))
            };
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("\0", string.Empty).Replace("'", "''");
    }
}

