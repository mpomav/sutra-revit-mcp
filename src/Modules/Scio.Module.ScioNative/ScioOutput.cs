// -----------------------------------------------------------------------------
// SCIO Revit MCP - In-process execution output standard.
//
// ScioOutput guarantees that responses returned to the LLM are compact (<500 tokens),
// while full detailed row-by-row data is streamed directly to CSV and interactive HTML
// in %USERPROFILE%\.scio\salidas\.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class ScioOutput
    {
        public string Resumen { get; set; } = string.Empty;
        public JObject Cifras { get; set; } = new JObject();
        public JArray Muestra { get; set; } = new JArray();
        public string Archivo { get; set; } = string.Empty;
        public string ArchivoCsv { get; set; } = string.Empty;
        public string ArchivoHtml { get; set; } = string.Empty;
        public JArray Advertencias { get; set; } = new JArray();

        public JObject ToJson()
        {
            return new JObject
            {
                ["resumen"] = Resumen ?? string.Empty,
                ["cifras"] = Cifras ?? new JObject(),
                ["muestra"] = Muestra ?? new JArray(),
                ["archivo"] = !string.IsNullOrEmpty(ArchivoHtml) ? ArchivoHtml : (Archivo ?? string.Empty),
                ["archivo_csv"] = ArchivoCsv ?? string.Empty,
                ["archivo_html"] = ArchivoHtml ?? string.Empty,
                ["advertencias"] = Advertencias ?? new JArray()
            };
        }

        public CommandResult ToCommandResult()
        {
            return CommandResult.Ok(ToJson());
        }

        public const int MaxHtmlTableRows = 1000;

        public static (string csvPath, string htmlPath) WriteStructuredReport(
            string prefix,
            string reportTitle,
            string summary,
            Dictionary<string, string> metadata,
            JObject kpis,
            IEnumerable<string> headers,
            IEnumerable<IEnumerable<string>> rows,
            JArray warnings = null,
            bool generateHtml = true)
        {
            string dir = ScioPaths.SalidasDir();
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"No se pudo crear el directorio de salidas SCIO en '{dir}': {ex.Message}", ex);
                }
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string csvFilename = $"{prefix}-{timestamp}.csv";
            string htmlFilename = $"{prefix}-{timestamp}.html";
            string csvPath = Path.Combine(dir, csvFilename);
            string htmlPath = Path.Combine(dir, htmlFilename);

            var rowList = rows as List<List<string>> ?? rows.Select(r => r.ToList()).ToList();
            var headerList = headers.ToList();

            // 1. Write CSV with structured header comments
            using (var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine($"# SCIO BIM HUB - {reportTitle.ToUpperInvariant()}");
                writer.WriteLine($"# Fecha: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                if (metadata != null)
                {
                    foreach (var kv in metadata) writer.WriteLine($"# {kv.Key}: {kv.Value}");
                }
                writer.WriteLine($"# Registros: {rowList.Count}");
                writer.WriteLine(string.Join(",", headerList.Select(EscapeCsv)));
                foreach (var row in rowList)
                {
                    writer.WriteLine(string.Join(",", row.Select(EscapeCsv)));
                }
            }

            if (!generateHtml)
            {
                return (csvPath, null);
            }

            // 2. Write standalone interactive HTML Report (capped at MaxHtmlTableRows to keep DOM fast)
            string html = GenerateHtmlReport(reportTitle, summary, metadata, kpis, headerList, rowList, warnings, csvFilename);
            File.WriteAllText(htmlPath, html, new UTF8Encoding(true));

            return (csvPath, htmlPath);
        }

        public static string WriteCsv(string prefix, IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
        {
            string dir = ScioPaths.SalidasDir();
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"No se pudo crear el directorio de salidas SCIO en '{dir}': {ex.Message}", ex);
                }
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string filename = $"{prefix}-{timestamp}.csv";
            string path = Path.Combine(dir, filename);

            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(string.Join(",", headers.Select(EscapeCsv)));
                foreach (var row in rows)
                {
                    writer.WriteLine(string.Join(",", row.Select(EscapeCsv)));
                }
            }
            return path;
        }

        private static string EscapeCsv(string field)
        {
            if (field == null) return "\"\"";
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }
            return field;
        }

        private static string GenerateHtmlReport(
            string title,
            string summary,
            Dictionary<string, string> metadata,
            JObject kpis,
            List<string> headers,
            List<List<string>> rows,
            JArray warnings,
            string csvFilename)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"es\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"utf-8\">");
            sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.AppendLine($"  <title>SCIO BIM - {WebUtility.HtmlEncode(title)}</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine(Scio.Abstractions.ScioHtmlTheme.ReportCss());
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            string actionsHtml =
                $"<a href=\"{WebUtility.HtmlEncode(csvFilename)}\" class=\"btn\" download>{Scio.Abstractions.ScioHtmlTheme.SvgDownload} Descargar CSV</a>" +
                $"<button onclick=\"window.print()\" class=\"btn btn-secondary\">{Scio.Abstractions.ScioHtmlTheme.SvgPrint} Imprimir</button>";

            sb.AppendLine(Scio.Abstractions.ScioHtmlTheme.RenderHeader(
                "SCIO BIM Hub",
                title,
                $"Generado el {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                actionsHtml));

            sb.AppendLine("  <div class=\"container\">");

            // Warnings if any
            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine("    <div class=\"warn-box\">");
                sb.AppendLine($"      <span>{Scio.Abstractions.ScioHtmlTheme.SvgAlert}</span>");
                sb.AppendLine("      <div>");
                sb.AppendLine($"        <strong>Advertencias registradas ({warnings.Count}):</strong><br>");
                foreach (var w in warnings) sb.AppendLine($"        &bull; {WebUtility.HtmlEncode(w.ToString())}<br>");
                sb.AppendLine("      </div>");
                sb.AppendLine("    </div>");
            }

            // Summary Card & Metadata
            sb.AppendLine("    <div class=\"summary-card\">");
            sb.AppendLine("      <div style=\"font-size: 15px; font-weight: 700; color: var(--scio-accent-dark); margin-bottom: 6px;\">Resumen Ejecutivo</div>");
            sb.AppendLine($"      <div style=\"font-size: 13.5px;\">{WebUtility.HtmlEncode(summary)}</div>");
            if (metadata != null && metadata.Count > 0)
            {
                sb.AppendLine("      <div class=\"meta-grid\">");
                foreach (var m in metadata)
                {
                    sb.AppendLine($"        <div class=\"meta-item\"><strong>{WebUtility.HtmlEncode(m.Key)}</strong>{WebUtility.HtmlEncode(m.Value)}</div>");
                }
                sb.AppendLine("      </div>");
            }
            sb.AppendLine("    </div>");

            // KPIs Grid
            if (kpis != null && kpis.Count > 0)
            {
                sb.AppendLine("    <div class=\"kpi-grid\">");
                foreach (var prop in kpis.Properties())
                {
                    string label = prop.Name.Replace('_', ' ');
                    sb.AppendLine("      <div class=\"kpi-card\">");
                    sb.AppendLine($"        <div class=\"kpi-title\">{WebUtility.HtmlEncode(label)}</div>");
                    sb.AppendLine($"        <div class=\"kpi-val\">{WebUtility.HtmlEncode(prop.Value.ToString())}</div>");
                    sb.AppendLine("      </div>");
                }
                sb.AppendLine("    </div>");
            }

            int renderedCount = Math.Min(rows.Count, MaxHtmlTableRows);
            bool truncated = rows.Count > MaxHtmlTableRows;

            // Data Table
            sb.AppendLine("    <div class=\"table-container\">");
            sb.AppendLine("      <div class=\"table-toolbar\">");
            sb.AppendLine(truncated
                ? $"        <span style=\"font-size: 13px; font-weight: 600; color: var(--scio-muted);\">Mostrando {renderedCount:N0} de {rows.Count:N0} filas (detalle completo en CSV adjunto)</span>"
                : $"        <span style=\"font-size: 13px; font-weight: 600; color: var(--scio-muted);\">Total filas: {rows.Count:N0}</span>");
            sb.AppendLine("        <input type=\"text\" id=\"filterInput\" class=\"search-input\" placeholder=\"Filtrar por categoría, familia, partida...\" onkeyup=\"filterTable()\">");
            sb.AppendLine("      </div>");
            sb.AppendLine("      <div class=\"table-scroll\">");
            sb.AppendLine("        <table id=\"reportTable\">");
            sb.AppendLine("          <thead>");
            sb.AppendLine("            <tr>");
            foreach (var h in headers) sb.AppendLine($"              <th>{WebUtility.HtmlEncode(h)}</th>");
            sb.AppendLine("            </tr>");
            sb.AppendLine("          </thead>");
            sb.AppendLine("          <tbody>");
            for (int rIdx = 0; rIdx < renderedCount; rIdx++)
            {
                var row = rows[rIdx];
                sb.AppendLine("            <tr>");
                foreach (var cell in row) sb.AppendLine($"              <td>{WebUtility.HtmlEncode(cell ?? "")}</td>");
                sb.AppendLine("            </tr>");
            }
            sb.AppendLine("          </tbody>");
            sb.AppendLine("        </table>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");

            sb.AppendLine("  </div>"); // end container
            sb.AppendLine(Scio.Abstractions.ScioHtmlTheme.RenderFooter("SCIO Revit MCP · SCIO BIM Hub", csvFilename));

            // Filter script
            sb.AppendLine("  <script>");
            sb.AppendLine("    function filterTable() {");
            sb.AppendLine("      var input = document.getElementById('filterInput');");
            sb.AppendLine("      var filter = input.value.toLowerCase();");
            sb.AppendLine("      var rows = document.querySelectorAll('#reportTable tbody tr');");
            sb.AppendLine("      for (var i = 0; i < rows.Length; i++) {");
            sb.AppendLine("        var text = rows[i].innerText.toLowerCase();");
            sb.AppendLine("        rows[i].style.display = text.indexOf(filter) > -1 ? '' : 'none';");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            sb.AppendLine("  </script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }
    }
}
