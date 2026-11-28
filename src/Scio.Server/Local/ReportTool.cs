#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Host-Resident Local Report Generator (scio_report)
//
// Builds self-contained HTML or Markdown executive reports from local SQLite
// runs in scio_local.db without calling Revit. Writing to disk respects
// Settings.AllowsExternalSideEffect and LocalPath.Problem.
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
    internal static class ReportTool
    {
        public static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();

            string format = (args.Value<string>("format") ?? "html").Trim().ToLowerInvariant();
            if (format != "html" && format != "markdown" && format != "md")
                throw new ToolRefusalException("format must be 'html' or 'markdown'.");
            bool isHtml = format == "html";

            bool saveToDisk = args["save_to_disk"] == null || args.Value<bool>("save_to_disk");
            string outputPath = args.Value<string>("output_path");
            if (saveToDisk && !ScioRuntimePaths.AllowsExternalSideEffect(PermissionProfile.SafeWrite, out string deny))
                throw new ToolRefusalException(deny + " Pass save_to_disk=false to generate the report inline.");

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                string badPath = ScioRuntimePaths.ValidateLocalOutputPath(outputPath, "output_path");
                if (badPath != null) throw new ToolRefusalException(badPath);
            }

            string title = args.Value<string>("title");
            if (string.IsNullOrWhiteSpace(title)) title = "Reporte Ejecutivo BIM — Sutra";
            int maxRows = Math.Max(1, Math.Min(500, args.Value<int?>("max_rows_per_section") ?? 50));

            var runIds = ResolveRunIds(args);
            var sections = new List<ReportSection>();
            foreach (string id in runIds)
            {
                ct.ThrowIfCancellationRequested();
                ReportSection sec = LoadSection(id, maxRows);
                if (sec != null) sections.Add(sec);
            }

            if (sections.Count == 0)
            {
                return new JObject
                {
                    ["written"] = false,
                    ["sha256"] = JValue.CreateNull(),
                    ["runs_included"] = new JArray(),
                    ["sections_count"] = 0,
                    ["note"] = "No runs found in scio_local.db. Run censo, metrado, auditoria or naming check first."
                };
            }

            string content = isHtml
                ? RenderHtml(title, sections)
                : RenderMarkdown(title, sections);

            if (!saveToDisk)
            {
                return new JObject
                {
                    ["written"] = false,
                    ["sha256"] = ComputeStringSha256(content),
                    ["format"] = isHtml ? "html" : "markdown",
                    ["title"] = title,
                    ["runs_included"] = new JArray(sections.Select(s => s.RunId)),
                    ["sections_count"] = sections.Count,
                    ["content"] = content.Length > 12000 ? content.Substring(0, 12000) : content,
                    ["content_truncated"] = content.Length > 12000
                };
            }

            string finalPath = outputPath;
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                string dir = Path.Combine(ScioPaths.DataRoot(), "salidas");
                Directory.CreateDirectory(dir);
                finalPath = Path.Combine(dir, $"report-{DateTime.UtcNow:yyyyMMddHHmmss}.{(isHtml ? "html" : "md")}");
            }
            else
            {
                string parent = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            }

            string tmpPath = finalPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmpPath, content, new UTF8Encoding(false));
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);

            byte[] writtenBytes = File.ReadAllBytes(finalPath);
            string sha = ComputeSha256(writtenBytes);

            return new JObject
            {
                ["written"] = true,
                ["file_path"] = finalPath,
                ["format"] = isHtml ? "html" : "markdown",
                ["title"] = title,
                ["bytes"] = writtenBytes.Length,
                ["sha256"] = sha,
                ["runs_included"] = new JArray(sections.Select(s => s.RunId)),
                ["sections_count"] = sections.Count
            };
        }

        private static List<string> ResolveRunIds(JObject args)
        {
            var ids = new List<string>();
            if (args["run_ids"] is JArray arr && arr.Count > 0)
            {
                foreach (JToken t in arr)
                {
                    string s = RunRefResolver.SanitizeId(t?.ToString());
                    if (!string.IsNullOrEmpty(s) && !ids.Contains(s)) ids.Add(s);
                }
                return ids;
            }

            string modelFilter = args.Value<string>("model_name");
            JObject listRes = SqliteIngestor.ListRuns(null, modelFilter, 6);
            if (listRes.Value<bool?>("ok") == true && listRes["rows"] is JArray rows)
            {
                foreach (JObject r in rows.OfType<JObject>())
                {
                    string id = r.Value<string>("run_id");
                    if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
                }
            }
            return ids;
        }

        private static ReportSection LoadSection(string runId, int maxRows)
        {
            string safe = RunRefResolver.SanitizeId(runId);
            if (safe.Length == 0) return null;

            JObject metaRes = SqliteIngestor.QueryReadOnly(
                $"SELECT run_id, command, timestamp_utc, model_name, total_rows, summary_json FROM runs WHERE run_id = '{safe.Replace("'", "''")}' LIMIT 1", 1);
            var metaArr = metaRes["rows"] as JArray;
            if (metaArr == null || metaArr.Count == 0) return null;

            JObject meta = metaArr[0] as JObject;
            var rows = SqliteIngestor.LoadRowsAsObjects(safe, maxRows);
            JObject summaryObj = null;
            string sumJson = meta?.Value<string>("summary_json");
            if (!string.IsNullOrWhiteSpace(sumJson))
            {
                try { summaryObj = JObject.Parse(sumJson); } catch { }
            }

            return new ReportSection
            {
                RunId = meta.Value<string>("run_id") ?? safe,
                Command = meta.Value<string>("command") ?? string.Empty,
                TimestampUtc = meta.Value<string>("timestamp_utc") ?? string.Empty,
                ModelName = meta.Value<string>("model_name") ?? string.Empty,
                TotalRows = meta.Value<long?>("total_rows") ?? rows.Count,
                Summary = summaryObj,
                Rows = rows
            };
        }

        private static string RenderMarkdown(string title, List<ReportSection> sections)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + title);
            sb.AppendLine();
            sb.AppendLine($"Generado UTC: `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}` | Corridas: `{sections.Count}`");
            sb.AppendLine();

            foreach (var sec in sections)
            {
                sb.AppendLine($"## {sec.Command} (`{sec.RunId}`)");
                sb.AppendLine($"- Modelo: **{sec.ModelName}** | Fecha UTC: `{sec.TimestampUtc}` | Total filas: **{sec.TotalRows}**");
                sb.AppendLine();

                if (sec.Rows.Count > 0)
                {
                    var cols = CollectColumns(sec.Rows);
                    sb.AppendLine("| " + string.Join(" | ", cols) + " |");
                    sb.AppendLine("| " + string.Join(" | ", cols.Select(_ => "---")) + " |");
                    foreach (JObject row in sec.Rows)
                    {
                        var cells = cols.Select(c => (row[c]?.ToString() ?? string.Empty).Replace("|", "/").Replace("\r", " ").Replace("\n", " "));
                        sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string RenderHtml(string title, List<ReportSection> sections)
        {
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.AppendLine("<title>" + WebUtility.HtmlEncode(title) + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(ScioHtmlTheme.ReportCss());
            sb.AppendLine("</style></head><body>");
            sb.AppendLine(ScioHtmlTheme.RenderHeader(
                "SUTRA · REPORTE LOCAL",
                title,
                "Generado: " + stamp,
                "<button type=\"button\" id=\"scioThemeToggle\" class=\"btn btn-secondary\" onclick=\"toggleScioTheme()\">Tema Claro / Oscuro</button>" +
                $"<button type=\"button\" onclick=\"window.print()\" class=\"btn btn-secondary\">{ScioHtmlTheme.SvgPrint} Imprimir</button>"));
            sb.AppendLine("<div class=\"container\">");
            sb.AppendLine("<div class=\"kpi-grid\">");
            sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-title\">Corridas incluidas</div><div class=\"kpi-val accent\">{sections.Count}</div></div>");
            sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-title\">Total registros</div><div class=\"kpi-val\">{sections.Sum(s => s.TotalRows)}</div></div>");
            sb.AppendLine("</div>");

            int tblIdx = 0;
            foreach (var sec in sections)
            {
                sb.AppendLine($"<h2>{WebUtility.HtmlEncode(sec.Command)} ({WebUtility.HtmlEncode(sec.RunId)})</h2>");
                sb.AppendLine($"<p class=\"section-meta\">Modelo: <strong>{WebUtility.HtmlEncode(sec.ModelName)}</strong> &middot; Total filas: <strong>{sec.TotalRows}</strong> &middot; Muestra: {sec.Rows.Count}</p>");
                if (sec.Rows.Count > 0)
                {
                    tblIdx++;
                    string tblId = "scio-tbl-" + tblIdx;
                    var cols = CollectColumns(sec.Rows);
                    sb.AppendLine("<div class=\"table-container\">");
                    sb.AppendLine($"<div class=\"table-toolbar\"><input class=\"search-input\" type=\"search\" placeholder=\"Filtrar filas...\" oninput=\"filterScioTable('{tblId}', this.value)\" /><button type=\"button\" class=\"btn btn-secondary\" onclick=\"copyScioCsv('{tblId}', this)\">{ScioHtmlTheme.SvgDownload} Copiar CSV</button></div>");
                    sb.AppendLine($"<div class=\"table-scroll\"><table id=\"{tblId}\"><thead><tr>" +
                        string.Join("", cols.Select((c, ci) => $"<th style=\"cursor:pointer\" onclick=\"sortScioTable('{tblId}', {ci})\">{WebUtility.HtmlEncode(c)} &#8597;</th>")) +
                        "</tr></thead><tbody>");
                    foreach (JObject row in sec.Rows)
                    {
                        sb.Append("<tr>");
                        foreach (string c in cols)
                            sb.Append("<td>" + WebUtility.HtmlEncode(row[c]?.ToString() ?? string.Empty) + "</td>");
                        sb.AppendLine("</tr>");
                    }
                    sb.AppendLine("</tbody></table></div></div>");
                }
            }
            sb.AppendLine("</div>");
            sb.AppendLine(ScioHtmlTheme.RenderFooter("Sutra — Revit MCP · scio_report", stamp));
            sb.AppendLine(@"<script>
(function(){
  window.toggleScioTheme = function(){
    var root = document.documentElement;
    var cur = root.getAttribute('data-theme');
    if(!cur){ cur = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'; }
    root.setAttribute('data-theme', cur === 'dark' ? 'light' : 'dark');
  };
  window.filterScioTable = function(id, q){
    var tbl = document.getElementById(id); if(!tbl) return;
    var needle = (q || '').toLowerCase();
    var rows = tbl.tBodies[0].rows;
    for(var i=0;i<rows.length;i++){
      rows[i].style.display = rows[i].textContent.toLowerCase().indexOf(needle) >= 0 ? '' : 'none';
    }
  };
  window.sortScioTable = function(id, col){
    var tbl = document.getElementById(id); if(!tbl) return;
    var tb = tbl.tBodies[0];
    var dir = tbl.getAttribute('data-sort-col') == String(col) && tbl.getAttribute('data-sort-dir') === 'asc' ? 'desc' : 'asc';
    tbl.setAttribute('data-sort-col', String(col)); tbl.setAttribute('data-sort-dir', dir);
    var rows = Array.prototype.slice.call(tb.rows);
    rows.sort(function(a,b){
      var av = (a.cells[col] ? a.cells[col].textContent : '').trim();
      var bv = (b.cells[col] ? b.cells[col].textContent : '').trim();
      var an = parseFloat(av.replace(/,/g,'')), bn = parseFloat(bv.replace(/,/g,''));
      var cmp = (!isNaN(an) && !isNaN(bn)) ? (an - bn) : av.localeCompare(bv);
      return dir === 'asc' ? cmp : -cmp;
    });
    rows.forEach(function(r){ tb.appendChild(r); });
  };
  window.copyScioCsv = function(id, btn){
    var tbl = document.getElementById(id); if(!tbl) return;
    var lines = [];
    Array.prototype.forEach.call(tbl.rows, function(r){
      if(r.style.display === 'none') return;
      var cells = Array.prototype.map.call(r.cells, function(c){
        return '""' + (c.textContent || '').replace(/""/g, '""""').trim() + '""';
      });
      lines.push(cells.join(','));
    });
    if(navigator.clipboard){ navigator.clipboard.writeText(lines.join('\n')); }
    if(btn){ var orig = btn.textContent; btn.textContent = 'Copiado'; setTimeout(function(){ btn.textContent = orig; }, 1400); }
  };
})();
</script>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static List<string> CollectColumns(List<JObject> rows)
        {
            var cols = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject r in rows)
                foreach (var p in r.Properties())
                    if (seen.Add(p.Name)) cols.Add(p.Name);
            return cols;
        }

        private static string ComputeSha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static string ComputeStringSha256(string s)
            => ComputeSha256(new UTF8Encoding(false).GetBytes(s ?? string.Empty));

        private sealed class ReportSection
        {
            public string RunId;
            public string Command;
            public string TimestampUtc;
            public string ModelName;
            public long TotalRows;
            public JObject Summary;
            public List<JObject> Rows;
        }
    }
}

