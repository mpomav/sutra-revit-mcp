using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.IsoInterop
{
    [ScioTool("scio_budget_compare", "interop", "Compare baseline vs current budget items or export a FIEBDC-3 (.bc3) budget file from items or live model takeoff.", ReadOnly = false)]
    public sealed class BudgetCompareCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation: compare or export_bc3.", Required = true, EnumValues = new[] { "compare", "export_bc3" })]
        public string Operation { get; set; } = "compare";

        [ScioParam("baseline", "Array of baseline budget items {code, description, unit, quantity, unit_price}.")]
        public JArray? Baseline { get; set; }

        [ScioParam("current", "Array of current budget items {code, description, unit, quantity, unit_price}.")]
        public JArray? Current { get; set; }

        [ScioParam("output_path", "Destination .bc3 file path for export_bc3.")]
        public string? OutputPath { get; set; }

        [ScioParam("currency", "Currency code for BC3 export (default PEN).")]
        public string Currency { get; set; } = "PEN";

        [ScioParam("dry_run", "Preview without writing file (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(BudgetCompareCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "compare").Trim().ToLowerInvariant();

            if (op == "compare")
            {
                var bItems = ParseItems(request.Arguments["baseline"] as JArray);
                var cItems = ParseItems(request.Arguments["current"] as JArray);
                if (cItems.Count == 0) cItems = BuildLiveModelItems(doc);
                var diff = Bc3AndAuditKit.CompareBudgets(bItems, cItems);
                return ToolResponse.Success(request.RequestId, diff, $"Budget compare: delta {diff["delta_total"]} ({diff["variance_pct"]}%).");
            }

            var items = ParseItems(request.Arguments["current"] as JArray);
            if (items.Count == 0) items = BuildLiveModelItems(doc);
            string currency = request.Arguments.Value<string>("currency") ?? "PEN";
            string bc3 = Bc3AndAuditKit.BuildBc3Document("SUTRA", doc.Title ?? "Model", currency, items);
            string outPath = request.Arguments.Value<string>("output_path") ?? Path.Combine(ScioRuntimePaths.DataRoot, "exports", "budget.bc3");
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            if (!dryRun)
            {
                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, bc3, new UTF8Encoding(false));
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["operation"] = "export_bc3",
                ["dry_run"] = dryRun,
                ["item_count"] = items.Count,
                ["output_path"] = outPath,
                ["preview_lines"] = new JArray(bc3.Split('\n').Take(8).Select(l => l.TrimEnd('\r')))
            }, dryRun ? $"BC3 preview ready ({items.Count} items)." : $"Exported {items.Count} item(s) to {outPath}.",
            dryRun ? VerificationStatus.ReadOnly : VerificationStatus.Verified, hostVerified: true);
        }

        private static List<BudgetLineItem> ParseItems(JArray? arr)
        {
            var list = new List<BudgetLineItem>();
            if (arr == null) return list;
            foreach (var tok in arr.OfType<JObject>())
            {
                list.Add(new BudgetLineItem
                {
                    Code = tok.Value<string>("code") ?? string.Empty,
                    Description = tok.Value<string>("description") ?? string.Empty,
                    Unit = tok.Value<string>("unit") ?? "u",
                    Quantity = tok.Value<double?>("quantity") ?? 0.0,
                    UnitPrice = tok.Value<double?>("unit_price") ?? 0.0
                });
            }
            return list;
        }

        private static List<BudgetLineItem> BuildLiveModelItems(Document doc)
        {
            var elems = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .ToList();
            int idx = 1;
            return elems.GroupBy(e => e.Category!.Name).OrderBy(g => g.Key)
                .Select(g => new BudgetLineItem
                {
                    Code = $"CAT{idx++:000}",
                    Description = g.Key,
                    Unit = "u",
                    Quantity = g.Count(),
                    UnitPrice = 0.0
                }).ToList();
        }
    }

    [ScioTool("scio_link_schedule", "interop", "4D schedule linking: match tasks to elements by parameter, write 4D dates to elements, or color active view by status_view.", ReadOnly = false)]
    public sealed class LinkScheduleCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation: match, write, or status_view.", Required = true, EnumValues = new[] { "match", "write", "status_view" })]
        public string Operation { get; set; } = "match";

        [ScioParam("tasks", "Array of 4D tasks: {task_id, name, start_date, end_date, match_param, match_value}.")]
        public JArray? Tasks { get; set; }

        [ScioParam("as_of_date", "ISO date (yyyy-MM-dd) for status_view.")]
        public string? AsOfDate { get; set; }

        [ScioParam("target_param", "Target string parameter for write (default 'Comments').")]
        public string TargetParam { get; set; } = "Comments";

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(LinkScheduleCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "match").Trim().ToLowerInvariant();
            var tasks = (request.Arguments["tasks"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (tasks.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_tasks", "'tasks' array is required.");

            var modelElems = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .ToList();

            var matches = new List<(JObject Task, List<Element> Elements)>();
            foreach (var t in tasks)
            {
                string mParam = (t.Value<string>("match_param") ?? "Category").Trim();
                string mVal = (t.Value<string>("match_value") ?? string.Empty).Trim();
                var hit = modelElems.Where(e => MatchesParam(doc, e, mParam, mVal)).ToList();
                matches.Add((t, hit));
            }

            if (op == "match")
            {
                var arr = new JArray(matches.Select(m => new JObject
                {
                    ["task_id"] = m.Task["task_id"],
                    ["name"] = m.Task["name"],
                    ["matched_elements"] = m.Elements.Count
                }));
                return ToolResponse.Success(request.RequestId, new JObject { ["task_count"] = arr.Count, ["matches"] = arr }, $"Matched {arr.Count} 4D task(s) against model.");
            }

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            if (op == "write")
            {
                string targetParam = request.Arguments.Value<string>("target_param") ?? "Comments";
                return TransactionPipeline.RunVerified(
                    doc,
                    request.RequestId,
                    "Write 4D Schedule Links",
                    dryRun,
                    d =>
                    {
                        int written = 0;
                        foreach (var m in matches)
                        {
                            string tag = $"4D:{m.Task["task_id"]}|{m.Task["start_date"]}..{m.Task["end_date"]}";
                            foreach (var el in m.Elements)
                            {
                                var p = el.LookupParameter(targetParam);
                                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                                {
                                    p.Set(tag);
                                    written++;
                                }
                            }
                        }
                        return new JObject { ["target_param"] = targetParam, ["written_elements"] = written };
                    },
                    (d, staged) => staged,
                    "4D schedule parameters written.");
            }

            // status_view
            if (!DateTime.TryParse(request.Arguments.Value<string>("as_of_date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
                asOf = DateTime.UtcNow.Date;
            var view = doc.ActiveView ?? throw new InvalidOperationException("No active view for status_view.");

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "4D Status View",
                dryRun,
                d =>
                {
                    var solid = new FilteredElementCollector(d).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                        .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);
                    int completed = 0, inProgress = 0, notStarted = 0;

                    foreach (var m in matches)
                    {
                        DateTime.TryParse(m.Task.Value<string>("start_date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
                        DateTime.TryParse(m.Task.Value<string>("end_date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
                        string st = Bc3AndAuditKit.Evaluate4DStatus(asOf, start, end);
                        Color col = st == "completed" ? new Color(22, 163, 74)
                                  : st == "in_progress" ? new Color(217, 119, 6)
                                  : new Color(156, 163, 175);
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(col);
                        if (solid != null) { ogs.SetSurfaceForegroundPatternId(solid.Id); ogs.SetSurfaceForegroundPatternColor(col); }

                        foreach (var el in m.Elements)
                        {
                            view.SetElementOverrides(el.Id, ogs);
                            if (st == "completed") completed++;
                            else if (st == "in_progress") inProgress++;
                            else notStarted++;
                        }
                    }

                    return new JObject
                    {
                        ["as_of_date"] = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["completed"] = completed,
                        ["in_progress"] = inProgress,
                        ["not_started"] = notStarted
                    };
                },
                (d, staged) => staged,
                $"4D status view applied for {asOf:yyyy-MM-dd}.");
        }

        private static bool MatchesParam(Document doc, Element el, string paramName, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return false;
            if (string.Equals(paramName, "Category", StringComparison.OrdinalIgnoreCase))
                return string.Equals(el.Category?.Name, expected, StringComparison.OrdinalIgnoreCase);
            var p = el.LookupParameter(paramName) ?? doc.GetElement(el.GetTypeId())?.LookupParameter(paramName);
            string actual = p?.AsValueString() ?? p?.AsString() ?? string.Empty;
            return actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    [ScioTool("scio_manage_classification_table", "interop", "Configure, reload, or diagnose external classification tables (Omniclass/Uniclass/GuBIMclass) and model classification coverage.", ReadOnly = false)]
    public sealed class ManageClassificationTableCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation: diagnose, set_path, or reload.", Required = true, EnumValues = new[] { "diagnose", "set_path", "reload" })]
        public string Operation { get; set; } = "diagnose";

        [ScioParam("table_path", "Path to local CSV/TSV classification table for set_path.")]
        public string? TablePath { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageClassificationTableCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "diagnose").Trim().ToLowerInvariant();
            string cfgPath = Path.Combine(ScioRuntimePaths.DataRoot, "classification-table.json");

            if (op == "set_path")
            {
                string? path = request.Arguments.Value<string>("table_path")?.Trim();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return ToolResponse.Refusal(request.RequestId, "file_not_found", "Valid existing 'table_path' is required.");

                Directory.CreateDirectory(ScioRuntimePaths.DataRoot);
                var cfg = new JObject { ["table_path"] = path, ["updated_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
                File.WriteAllText(cfgPath, cfg.ToString(Formatting.Indented), new UTF8Encoding(false));
                int rows = File.ReadLines(path!).Count(l => !string.IsNullOrWhiteSpace(l));
                return ToolResponse.Success(request.RequestId, new JObject { ["operation"] = "set_path", ["table_path"] = path, ["rows"] = rows }, $"Configured classification table ({rows} rows).", VerificationStatus.Verified, true);
            }

            string? currentPath = File.Exists(cfgPath) ? JObject.Parse(File.ReadAllText(cfgPath)).Value<string>("table_path") : null;
            int tableRows = !string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath)
                ? File.ReadLines(currentPath!).Count(l => !string.IsNullOrWhiteSpace(l))
                : 0;

            var types = new FilteredElementCollector(doc).WhereElementIsElementType().ToList();
#if REVIT2026_OR_GREATER
            int withAssemblyCode = types.Count(t => !string.IsNullOrWhiteSpace((t.LookupParameter("Assembly Code") ?? t.LookupParameter("Código de montaje"))?.AsString()));
            int withOmniclass = types.Count(t => !string.IsNullOrWhiteSpace((t.LookupParameter("OmniClass Number") ?? t.LookupParameter("Número de OmniClass"))?.AsString()));
#else
            int withAssemblyCode = types.Count(t => !string.IsNullOrWhiteSpace(t.get_Parameter(BuiltInParameter.UNIFORMAT_CODE)?.AsString()));
            int withOmniclass = types.Count(t => !string.IsNullOrWhiteSpace(t.get_Parameter(BuiltInParameter.OMNICLASS_CODE)?.AsString()));
#endif

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["operation"] = op,
                ["configured_table_path"] = currentPath ?? string.Empty,
                ["table_exists"] = !string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath),
                ["table_rows"] = tableRows,
                ["element_types_total"] = types.Count,
                ["with_assembly_code"] = withAssemblyCode,
                ["with_omniclass_code"] = withOmniclass
            }, $"Classification diagnose: {withAssemblyCode}/{types.Count} types have Assembly Code, {withOmniclass} have OmniClass.");
        }
    }
}
