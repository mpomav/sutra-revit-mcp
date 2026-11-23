#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Host-Resident Multi-Model Portfolio Audit (scio_batch_audit)
//
// Aggregates the latest SQLite runs per model across censo, param audit,
// naming check, audit_model, metrados, and clashes in scio_local.db. Evaluates
// caller thresholds and optionally writes a consolidated portfolio report.
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
    internal static class BatchAuditTool
    {
        public static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();

            bool exportReport = args.Value<bool?>("export_report") == true;
            if (exportReport && !ScioRuntimePaths.AllowsExternalSideEffect(PermissionProfile.SafeWrite, out string deny))
                throw new ToolRefusalException(deny + " Omit export_report or set export_report=false.");

            var modelFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (args["models"] is JArray mArr)
            {
                foreach (JToken t in mArr)
                    if (!string.IsNullOrWhiteSpace(t?.ToString())) modelFilter.Add(t.ToString().Trim());
            }

            var thresholds = args["thresholds"] as JObject;
            int? maxClashes = thresholds?.Value<int?>("max_clashes");
            double? minParamPct = thresholds?.Value<double?>("min_param_compliance_pct");
            double? minNamingPct = thresholds?.Value<double?>("min_naming_compliance_pct");

            JObject runsRes = SqliteIngestor.QueryReadOnly(
                "SELECT run_id, command, timestamp_utc, model_name, total_rows FROM runs ORDER BY timestamp_utc DESC", 2000);
            var latestByModelAndCmd = new Dictionary<string, Dictionary<string, JObject>>(StringComparer.OrdinalIgnoreCase);

            if (runsRes.Value<bool?>("ok") == true && runsRes["rows"] is JArray runRows)
            {
                foreach (JObject r in runRows.OfType<JObject>())
                {
                    string model = (r.Value<string>("model_name") ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(model)) model = "(unnamed)";
                    if (modelFilter.Count > 0 && !modelFilter.Any(f => model.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    string cmd = r.Value<string>("command") ?? string.Empty;
                    if (!latestByModelAndCmd.TryGetValue(model, out var byCmd))
                        latestByModelAndCmd[model] = byCmd = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                    if (!byCmd.ContainsKey(cmd))
                        byCmd[cmd] = r;
                }
            }

            var scorecard = new JArray();
            int passCount = 0, failCount = 0, incompleteCount = 0;

            foreach (var kv in latestByModelAndCmd.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                string model = kv.Key;
                var cmds = kv.Value;

                double? paramPct = ComputeCompliance(cmds.TryGetValue("scio_auditoria_parametros", out JObject pa) ? pa.Value<string>("run_id") : null,
                    "auditoria_parametros", "estado", "OK");
                double? namingPct = ComputeCompliance(cmds.TryGetValue("scio_naming_check", out JObject nm) ? nm.Value<string>("run_id") : null,
                    "naming_results", "status", "OK");
                long? clashCount = cmds.TryGetValue("scio_clash", out JObject cl) ? cl.Value<long?>("total_rows") : null;
                long? elementCount = cmds.TryGetValue("scio_censo", out JObject ce) ? ce.Value<long?>("total_rows") : null;
                long? findingCount = cmds.TryGetValue("scio_audit_model", out JObject am) ? am.Value<long?>("total_rows") : null;

                var failures = new JArray();
                if (minParamPct.HasValue && paramPct.HasValue && paramPct.Value < minParamPct.Value)
                    failures.Add($"param_compliance {paramPct.Value}% < {minParamPct.Value}%");
                if (minNamingPct.HasValue && namingPct.HasValue && namingPct.Value < minNamingPct.Value)
                    failures.Add($"naming_compliance {namingPct.Value}% < {minNamingPct.Value}%");
                if (maxClashes.HasValue && clashCount.HasValue && clashCount.Value > maxClashes.Value)
                    failures.Add($"clashes {clashCount.Value} > {maxClashes.Value}");

                string status;
                if (failures.Count > 0) { status = "fail"; failCount++; }
                else if (paramPct == null && namingPct == null && clashCount == null && findingCount == null)
                { status = "incomplete"; incompleteCount++; }
                else { status = "pass"; passCount++; }

                scorecard.Add(new JObject
                {
                    ["model_name"] = model,
                    ["status"] = status,
                    ["elements"] = elementCount.HasValue ? (JToken)elementCount.Value : JValue.CreateNull(),
                    ["param_compliance_pct"] = paramPct.HasValue ? (JToken)paramPct.Value : JValue.CreateNull(),
                    ["naming_compliance_pct"] = namingPct.HasValue ? (JToken)namingPct.Value : JValue.CreateNull(),
                    ["clashes"] = clashCount.HasValue ? (JToken)clashCount.Value : JValue.CreateNull(),
                    ["audit_findings_rows"] = findingCount.HasValue ? (JToken)findingCount.Value : JValue.CreateNull(),
                    ["threshold_failures"] = failures,
                    ["latest_runs"] = new JObject(cmds.Select(x => new JProperty(x.Key, x.Value["run_id"])))
                });
            }

            var res = new JObject
            {
                ["models_audited"] = scorecard.Count,
                ["written"] = false,
                ["summary"] = new JObject
                {
                    ["pass"] = passCount,
                    ["fail"] = failCount,
                    ["incomplete"] = incompleteCount
                },
                ["scorecard"] = scorecard
            };

            if (exportReport)
            {
                string dir = Path.Combine(ScioPaths.DataRoot(), "salidas");
                Directory.CreateDirectory(dir);
                string htmlPath = Path.Combine(dir, $"batch-audit-{DateTime.UtcNow:yyyyMMddHHmmss}.html");
                File.WriteAllText(htmlPath, RenderHtml(scorecard), new UTF8Encoding(false));
                byte[] bytes = File.ReadAllBytes(htmlPath);
                res["written"] = true;
                res["file_path"] = htmlPath;
                res["bytes"] = bytes.Length;
                res["sha256"] = ComputeSha256(bytes);
            }

            return res;
        }

        private static double? ComputeCompliance(string runId, string table, string col, string okValue)
        {
            if (string.IsNullOrEmpty(runId)) return null;
            string safe = RunRefResolver.SanitizeId(runId).Replace("'", "''");
            string sql = $"SELECT COUNT(*) AS tot, SUM(CASE WHEN UPPER({col}) = '{okValue}' THEN 1 ELSE 0 END) AS ok_cnt FROM {table} WHERE run_id = '{safe}'";
            JObject qr = SqliteIngestor.QueryReadOnly(sql, 1);
            var rows = qr["rows"] as JArray;
            if (rows == null || rows.Count == 0) return null;
            long tot = rows[0].Value<long?>("tot") ?? 0;
            if (tot == 0) return null;
            long ok = rows[0].Value<long?>("ok_cnt") ?? 0;
            return Math.Round((ok * 100.0) / tot, 2);
        }

        private static string RenderHtml(JArray scorecard)
        {
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            int total = scorecard.Count;
            int pass = scorecard.OfType<JObject>().Count(r => string.Equals(r.Value<string>("status"), "pass", StringComparison.OrdinalIgnoreCase));
            int fail = scorecard.OfType<JObject>().Count(r => string.Equals(r.Value<string>("status"), "fail", StringComparison.OrdinalIgnoreCase));
            int inc = total - pass - fail;

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Batch Audit SCIO</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(ScioHtmlTheme.ReportCss());
            sb.AppendLine("</style></head><body>");
            sb.AppendLine(ScioHtmlTheme.RenderHeader(
                "SCIO BIM Â· PORTAFOLIO",
                "Portfolio Batch Audit Scorecard",
                "Consolidado multi-modelo desde scio_local.db Â· " + stamp,
                $"<button onclick=\"window.print()\" class=\"btn btn-secondary\">{ScioHtmlTheme.SvgPrint} Imprimir</button>"));
            sb.AppendLine("<div class=\"container\">");
            sb.AppendLine("<div class=\"kpi-grid\">");
            sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-title\">Modelos</div><div class=\"kpi-val accent\">{total}</div></div>");
            sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-title\">Aprobados</div><div class=\"kpi-val\" style=\"color:var(--scio-ok-fg)\">{pass}</div></div>");
            sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-title\">Observados</div><div class=\"kpi-val\" style=\"color:var(--scio-bad-fg)\">{fail}</div></div>");
            sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-title\">Incompletos</div><div class=\"kpi-val\">{inc}</div></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div class=\"table-container\"><div class=\"table-scroll\"><table><thead><tr><th>Model</th><th>Status</th><th>Elements</th><th>Param %</th><th>Naming %</th><th>Clashes</th></tr></thead><tbody>");
            foreach (JObject row in scorecard.OfType<JObject>())
            {
                string st = row.Value<string>("status") ?? "incomplete";
                string badgeClass = st == "pass" ? "badge-ok" : st == "fail" ? "badge-fail" : "badge-neutral";
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "<tr><td>{0}</td><td><span class=\"badge {1}\">{2}</span></td><td class=\"mono\">{3}</td><td class=\"mono\">{4}</td><td class=\"mono\">{5}</td><td class=\"mono\">{6}</td></tr>\n",
                    WebUtility.HtmlEncode(row.Value<string>("model_name")),
                    badgeClass,
                    WebUtility.HtmlEncode(st),
                    WebUtility.HtmlEncode(row["elements"]?.ToString() ?? "-"),
                    WebUtility.HtmlEncode(row["param_compliance_pct"]?.ToString() ?? "-"),
                    WebUtility.HtmlEncode(row["naming_compliance_pct"]?.ToString() ?? "-"),
                    WebUtility.HtmlEncode(row["clashes"]?.ToString() ?? "-"));
            }
            sb.AppendLine("</tbody></table></div></div></div>");
            sb.AppendLine(ScioHtmlTheme.RenderFooter("SCIO Revit MCP Â· scio_batch_audit", stamp));
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string ComputeSha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}

