// -----------------------------------------------------------------------------
// SCIO Revit MCP - Batch View/Sheet Capture & Contact Sheet (scio_visual_qa)
//
// Exports multiple views or sheets locally to PNG + an offline HTML contact
// sheet in %USERPROFILE%\.scio\salidas\visual_qa-<ts>\ with zero base64 tokens
// sent back through MCP (attach_image=false). Gated via ExternalSideEffectOnRequest.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class VisualQaCommand : ICommand
    {
        public string Name => "scio_visual_qa";
        public string Description =>
            "Batch capture Revit views/sheets to local PNG files plus a self-contained HTML contact sheet without " +
            "sending base64 pixels over MCP (dry_run default true).";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Scio.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult wrongDoc = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDoc != null) return wrongDoc;

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            bool onlySheets = request["only_sheets"] == null || request.Value<bool>("only_sheets");
            string viewTypeFilter = request.Value<string>("view_type");
            int maxViews = Math.Max(1, Math.Min(25, request.Value<int?>("max_views") ?? 10));
            int pixelSize = Math.Max(256, Math.Min(4096, request.Value<int?>("pixel_size") ?? 1400));

            List<View> targets = SelectViews(app, doc, request, onlySheets, viewTypeFilter, maxViews, out string selErr);
            if (selErr != null) return CommandResult.Fail(selErr);

            if (dryRun)
            {
                return CommandResult.Ok(new JObject
                {
                    ["dry_run"] = true,
                    ["written"] = false,
                    ["document"] = SafeTitle(doc),
                    ["planned_count"] = targets.Count,
                    ["captured_count"] = 0,
                    ["attach_image"] = false,
                    ["views"] = new JArray(targets.Select(v => new JObject
                    {
                        ["view_id"] = Rid.Value(v.Id),
                        ["name"] = SafeName(v),
                        ["view_type"] = v.ViewType.ToString()
                    }))
                });
            }

            if (!Scio.Revit.Core.Settings.AllowsExternalSideEffect(out string deny))
                return CommandResult.Fail(deny + " Run with dry_run=true to preview.");

            if (targets.Count == 0)
            {
                return CommandResult.Ok(new JObject
                {
                    ["dry_run"] = false,
                    ["written"] = false,
                    ["document"] = SafeTitle(doc),
                    ["captured_count"] = 0,
                    ["attach_image"] = false,
                    ["views"] = new JArray()
                });
            }

            string dir = Path.Combine(ScioPaths.DataRoot(), "salidas", "visual_qa-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);

            string docTitle = SafeTitle(doc) ?? "Model";
            var capturedRows = new JArray();
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>SCIO Visual QA</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(ScioHtmlTheme.ReportCss());
            sb.AppendLine("</style></head><body>");
            sb.AppendLine(ScioHtmlTheme.RenderHeader(
                "SCIO VISUAL QA",
                $"Inspección visual — {docTitle}",
                $"Generado: {DateTime.Now:dd/MM/yyyy HH:mm:ss} · {targets.Count} vista(s)",
                $"<button onclick=\"window.print()\" class=\"btn btn-secondary\">{ScioHtmlTheme.SvgPrint} Imprimir</button>"));
            sb.AppendLine("<div class=\"container\"><div class=\"gallery-grid\">");

            int okCount = 0;
            foreach (View v in targets)
            {
                long vid = Rid.Value(v.Id);
                string stem = Path.Combine(dir, $"v-{vid}");
                string produced = null;
                string err = null;
                try
                {
                    var opts = new ImageExportOptions
                    {
                        ExportRange = ExportRange.SetOfViews,
                        FilePath = stem,
                        HLRandWFViewsFileType = ImageFileType.PNG,
                        ShadowViewsFileType = ImageFileType.PNG,
                        ZoomType = ZoomFitType.FitToPage,
                        FitDirection = FitDirectionType.Horizontal,
                        ImageResolution = ImageResolution.DPI_150
                    };
                    try { opts.PixelSize = pixelSize; } catch { }
                    opts.SetViewsAndSheets(new List<ElementId> { v.Id });
                    doc.ExportImage(opts);

                    string prefix = $"v-{vid}";
                    produced = Directory.GetFiles(dir, prefix + "*.png").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                }
                catch (Exception ex) { err = ex.Message; }

                if (!string.IsNullOrEmpty(produced) && File.Exists(produced))
                {
                    okCount++;
                    string relName = Path.GetFileName(produced);
                    string fileSha = ComputeSha256(File.ReadAllBytes(produced));
                    capturedRows.Add(new JObject
                    {
                        ["view_id"] = vid,
                        ["name"] = SafeName(v),
                        ["view_type"] = v.ViewType.ToString(),
                        ["file_path"] = produced,
                        ["sha256"] = fileSha
                    });
                    sb.AppendLine($"<div class=\"gallery-card\"><h3>{WebUtility.HtmlEncode(SafeName(v))} <span class=\"badge badge-neutral\">#{vid}</span></h3>" +
                                  $"<div class=\"section-meta\">{WebUtility.HtmlEncode(v.ViewType.ToString())}</div>" +
                                  $"<img src=\"{WebUtility.HtmlEncode(relName)}\" alt=\"{WebUtility.HtmlEncode(SafeName(v))}\"/></div>");
                }
                else
                {
                    capturedRows.Add(new JObject
                    {
                        ["view_id"] = vid,
                        ["name"] = SafeName(v),
                        ["error"] = err ?? "ExportImage produced no PNG file."
                    });
                }
            }

            sb.AppendLine("</div></div>");
            sb.AppendLine(ScioHtmlTheme.RenderFooter("SCIO Revit MCP · scio_visual_qa", docTitle));
            sb.AppendLine("</body></html>");
            string indexPath = Path.Combine(dir, "index.html");
            File.WriteAllText(indexPath, sb.ToString(), new UTF8Encoding(false));
            byte[] htmlBytes = File.ReadAllBytes(indexPath);

            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = false,
                ["written"] = okCount > 0 || File.Exists(indexPath),
                ["document"] = SafeTitle(doc),
                ["output_dir"] = dir,
                ["index_html"] = indexPath,
                ["sha256"] = ComputeSha256(htmlBytes),
                ["captured_count"] = okCount,
                ["requested_count"] = targets.Count,
                ["attach_image"] = false,
                ["views"] = capturedRows
            });
        }

        private static List<View> SelectViews(UIApplication app, Document doc, JObject request, bool onlySheets, string viewTypeFilter, int maxViews, out string error)
        {
            error = null;
            var list = new List<View>();

            if (request["view_ids"] != null && request["view_ids"].Type != JTokenType.Null)
            {
                SelectionResolution sel = SelectionResolver.Resolve(app, request["view_ids"]);
                if (!sel.Ok) { error = sel.Error; return list; }
                foreach (long id in sel.Ids)
                {
                    if (!Rid.CanRepresent(id)) continue;
                    View v = doc.GetElement(Rid.Make(id)) as View;
                    if (v != null && IsRasterExportable(v))
                    {
                        list.Add(v);
                        if (list.Count >= maxViews) break;
                    }
                }
                return list;
            }

            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (!IsRasterExportable(v)) continue;
                if (!string.IsNullOrWhiteSpace(viewTypeFilter))
                {
                    if (!string.Equals(v.ViewType.ToString(), viewTypeFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (onlySheets && !(v is ViewSheet))
                {
                    continue;
                }
                list.Add(v);
                if (list.Count >= maxViews) break;
            }
            return list;
        }

        private static bool IsRasterExportable(View v)
        {
            if (v == null) return false;
            try { if (v.IsTemplate) return false; } catch { return false; }
            return v.ViewType != ViewType.Schedule &&
                   v.ViewType != ViewType.ColumnSchedule &&
                   v.ViewType != ViewType.PanelSchedule &&
                   v.ViewType != ViewType.Internal &&
                   v.ViewType != ViewType.ProjectBrowser &&
                   v.ViewType != ViewType.SystemBrowser;
        }

        private static string ComputeSha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static string SafeName(View v) { try { return v?.Name ?? string.Empty; } catch { return string.Empty; } }
        private static string SafeTitle(Document d) { try { return d?.Title; } catch { return null; } }
    }
}
