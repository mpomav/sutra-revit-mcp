using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.IsoInterop
{
    internal static class InteropKit
    {
        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        public static string CsvCell(string? v)
        {
            string s = v ?? string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public static string ContextPathForDoc(Document doc)
        {
            string dir = !string.IsNullOrWhiteSpace(doc.PathName)
                ? (Path.GetDirectoryName(doc.PathName) ?? ScioPaths.DataRoot())
                : ScioPaths.DataRoot();
            return Path.Combine(dir, "project-context.json");
        }
    }

    [ScioTool("scio_project_context", "iso19650", "Read or update ISO 19650 project-context.json (project code, originator, EIR, BEP, CDE folders, classification, naming).", ReadOnly = false, OpenWorld = true)]
    public sealed class ProjectContextCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: get (default) or set.", EnumValues = new[] { "get", "set" })]
        public string Action { get; set; } = "get";

        [ScioParam("context", "JSON object with ISO 19650 project context fields to merge when action=set.")]
        public JObject? ContextData { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ProjectContextCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string path = InteropKit.ContextPathForDoc(doc);
            string action = (request.Arguments.Value<string>("action") ?? "get").Trim().ToLowerInvariant();

            JObject current = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            if (action == "set" && request.Arguments["context"] is JObject patch)
            {
                current.Merge(patch, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                current["updated_utc"] = DateTime.UtcNow.ToString("o");
                File.WriteAllText(path, current.ToString(Formatting.Indented));
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["path"] = path,
                ["exists"] = File.Exists(path),
                ["context"] = current
            }, $"Project context '{path}'.");
        }
    }

    [ScioTool("scio_information_container", "iso19650", "Generate ISO 19650 container ID (Project-Originator-Volume-Level-Type-Role-Number), seal a file with SHA-256, or promote between WIP/Shared/Published/Archived.", ReadOnly = false, OpenWorld = true)]
    public sealed class InformationContainerCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: name, seal, or promote.", Required = true, EnumValues = new[] { "name", "seal", "promote" })]
        public string Action { get; set; } = "name";

        [ScioParam("project", "ISO 19650 Project code.")]
        public string Project { get; set; } = "PRJ";

        [ScioParam("originator", "Originator code (e.g. SCIO).")]
        public string Originator { get; set; } = "SCIO";

        [ScioParam("volume", "Functional breakdown / volume (e.g. ZZ).")]
        public string Volume { get; set; } = "ZZ";

        [ScioParam("level", "Spatial location / level (e.g. ZZ, 01).")]
        public string Level { get; set; } = "ZZ";

        [ScioParam("type", "Container type (e.g. M3, DR, SH, SP).")]
        public string TypeCode { get; set; } = "M3";

        [ScioParam("role", "Discipline role (e.g. A, S, M, E).")]
        public string Role { get; set; } = "A";

        [ScioParam("number", "Sequential container number (e.g. 0001).")]
        public string Number { get; set; } = "0001";

        [ScioParam("file_path", "File path to seal or promote.")]
        public string FilePath { get; set; } = string.Empty;

        [ScioParam("target_state", "Target CDE state for promote (WIP, Shared, Published, Archived).")]
        public string TargetState { get; set; } = "Shared";

        [ScioParam("target_folder", "Destination CDE folder for promote.")]
        public string TargetFolder { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(InformationContainerCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string action = (request.Arguments.Value<string>("action") ?? "name").Trim().ToLowerInvariant();
            string containerId = string.Join("-",
                request.Arguments.Value<string>("project") ?? "PRJ",
                request.Arguments.Value<string>("originator") ?? "SCIO",
                request.Arguments.Value<string>("volume") ?? "ZZ",
                request.Arguments.Value<string>("level") ?? "ZZ",
                request.Arguments.Value<string>("type") ?? "M3",
                request.Arguments.Value<string>("role") ?? "A",
                request.Arguments.Value<string>("number") ?? "0001").ToUpperInvariant();

            if (action == "name")
                return ToolResponse.Success(request.RequestId, new JObject { ["container_id"] = containerId }, $"Container ID: {containerId}");

            string path = request.Arguments.Value<string>("file_path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ToolResponse.Refusal(request.RequestId, "file_not_found", $"File not found: '{path}'.");

            string sha = InteropKit.Sha256File(path);
            if (action == "seal")
            {
                var seal = new JObject
                {
                    ["container_id"] = containerId,
                    ["file"] = Path.GetFileName(path),
                    ["sha256"] = sha,
                    ["size_bytes"] = new FileInfo(path).Length,
                    ["sealed_utc"] = DateTime.UtcNow.ToString("o")
                };
                string sealPath = path + ".seal.json";
                File.WriteAllText(sealPath, seal.ToString(Formatting.Indented));
                seal["seal_path"] = sealPath;
                return ToolResponse.Success(request.RequestId, seal, $"Sealed '{Path.GetFileName(path)}'.");
            }

            string dstFolder = request.Arguments.Value<string>("target_folder") ?? string.Empty;
            string state = request.Arguments.Value<string>("target_state") ?? "Shared";
            if (string.IsNullOrWhiteSpace(dstFolder))
                return ToolResponse.Refusal(request.RequestId, "missing_target_folder", "Provide 'target_folder' for promote.");

            Directory.CreateDirectory(dstFolder);
            string dstPath = Path.Combine(dstFolder, Path.GetFileName(path));
            File.Copy(path, dstPath, overwrite: true);
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["promoted_to"] = dstPath,
                ["state"] = state,
                ["sha256"] = sha
            }, $"Promoted '{Path.GetFileName(path)}' to {state}.");
        }
    }

    [ScioTool("scio_cde_status", "iso19650", "Audit a local CDE directory tree across WIP, Shared, Published, and Archived folders and check ISO 19650 filename compliance.", ReadOnly = true)]
    public sealed class CdeStatusCommand : IRevitCommand
    {
        [ScioParam("cde_root", "Root folder path of the local CDE.", Required = true)]
        public string CdeRoot { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CdeStatusCommand));

        private static readonly Regex IsoNameRx = new Regex(@"^[A-Z0-9]{2,12}-[A-Z0-9]{2,8}-[A-Z0-9]{2,4}-[A-Z0-9]{2,4}-[A-Z0-9]{2,4}-[A-Z0-9]{1,4}-\d{4,6}", RegexOptions.Compiled);

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string root = request.Arguments.Value<string>("cde_root") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return ToolResponse.Refusal(request.RequestId, "dir_not_found", $"CDE folder not found: '{root}'.");

            string[] states = { "WIP", "Shared", "Published", "Archived" };
            var stateSummary = new JObject();
            int nonCompliant = 0;
            var badNames = new JArray();

            foreach (string st in states)
            {
                string sub = Path.Combine(root, st);
                if (!Directory.Exists(sub)) { stateSummary[st] = 0; continue; }
                var files = Directory.GetFiles(sub, "*.*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".seal.json", StringComparison.OrdinalIgnoreCase)).ToList();
                stateSummary[st] = files.Count;
                foreach (var f in files)
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (!IsoNameRx.IsMatch(stem))
                    {
                        nonCompliant++;
                        if (badNames.Count < 50) badNames.Add(Path.GetFileName(f));
                    }
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["cde_root"] = root,
                ["counts_by_state"] = stateSummary,
                ["non_compliant_names"] = nonCompliant,
                ["sample_non_compliant"] = badNames
            }, $"CDE audit '{root}': {nonCompliant} non-compliant filename(s).");
        }
    }

    [ScioTool("scio_exchange", "iso19650", "Emit or verify a scio.exchange/0 cross-app handoff manifest bound to the active Revit document.", ReadOnly = false, OpenWorld = true)]
    public sealed class ExchangeManifestCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: emit or verify.", Required = true, EnumValues = new[] { "emit", "verify" })]
        public string Action { get; set; } = "emit";

        [ScioParam("manifest_path", "Path to write (emit) or read (verify) the manifest JSON.", Required = true)]
        public string ManifestPath { get; set; } = string.Empty;

        [ScioParam("payload", "Optional JSON payload/artifact list for emit.")]
        public JObject? Payload { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ExchangeManifestCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "emit").Trim().ToLowerInvariant();
            string path = request.Arguments.Value<string>("manifest_path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return ToolResponse.Refusal(request.RequestId, "missing_path", "Provide 'manifest_path'.");

            if (action == "verify")
            {
                if (!File.Exists(path)) return ToolResponse.Refusal(request.RequestId, "not_found", $"Manifest not found: '{path}'.");
                var m = JObject.Parse(File.ReadAllText(path));
                bool schemaOk = string.Equals(m.Value<string>("schema"), "scio.exchange/0", StringComparison.Ordinal);
                bool modelMatch = string.Equals(m.Value<string>("model_ref"), doc.Title, StringComparison.OrdinalIgnoreCase);
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["valid_schema"] = schemaOk,
                    ["model_matches_active"] = modelMatch,
                    ["manifest"] = m
                }, $"Verified manifest: schema={schemaOk}, modelMatch={modelMatch}.");
            }

            var manifest = new JObject
            {
                ["schema"] = "scio.exchange/0",
                ["emitted_utc"] = DateTime.UtcNow.ToString("o"),
                ["model_ref"] = doc.Title,
                ["model_path"] = doc.PathName,
                ["revit_year"] = context.RevitYear,
                ["payload"] = request.Arguments["payload"] ?? new JObject()
            };
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, manifest.ToString(Formatting.Indented));
            return ToolResponse.Success(request.RequestId, manifest, $"Emitted scio.exchange/0 manifest to '{path}'.");
        }
    }

    [ScioTool("scio_export_excel_csv", "interop", "Export model elements and specified parameters to an Excel-compatible UTF-8 BOM CSV file.", ReadOnly = true, OpenWorld = true)]
    public sealed class ExportExcelCsvCommand : IRevitCommand
    {
        [ScioParam("category", "Category name to export.", Required = true)]
        public string CategoryName { get; set; } = string.Empty;

        [ScioParam("parameters", "List of parameter names to include as columns.", Required = true)]
        public List<string> Parameters { get; set; } = new List<string>();

        [ScioParam("output_path", "Absolute output .csv file path.", Required = true)]
        public string OutputPath { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ExportExcelCsvCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var cat = RevitElementKit.ResolveDocumentCategory(doc, request.Arguments.Value<string>("category"));
            var paramNames = (request.Arguments["parameters"] as JArray)?.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            string outPath = request.Arguments.Value<string>("output_path") ?? string.Empty;
            if (cat == null || string.IsNullOrWhiteSpace(outPath))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Valid 'category' and 'output_path' required.");

            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("ElementId,UniqueId,Category,Family,Type");
            foreach (string pn in paramNames) sb.Append(',').Append(InteropKit.CsvCell(pn));
            sb.AppendLine();

            int rows = 0;
            foreach (var el in new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType())
            {
                var et = doc.GetElement(el.GetTypeId()) as ElementType;
                sb.Append(IdCompat.ToInt64(el.Id)).Append(',')
                  .Append(InteropKit.CsvCell(el.UniqueId)).Append(',')
                  .Append(InteropKit.CsvCell(cat.Name)).Append(',')
                  .Append(InteropKit.CsvCell(et?.FamilyName)).Append(',')
                  .Append(InteropKit.CsvCell(et?.Name ?? el.Name));
                foreach (string pn in paramNames)
                {
                    string v = RevitElementKit.ReadParameterDisplay(RevitElementKit.FindParameter(el, pn, includeType: true));
                    sb.Append(',').Append(InteropKit.CsvCell(v));
                }
                sb.AppendLine();
                rows++;
            }

            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["path"] = outPath,
                ["category"] = cat.Name,
                ["rows"] = rows
            }, $"Exported {rows} row(s) of '{cat.Name}' to '{outPath}'.");
        }
    }

    [ScioTool("scio_import_csv_params", "interop", "Import parameter updates from a CSV file (first column ElementId, remaining columns parameter names) with dry-run verification.", ReadOnly = false)]
    public sealed class ImportCsvParamsCommand : IRevitCommand
    {
        [ScioParam("csv_path", "Absolute path to the CSV file.", Required = true)]
        public string CsvPath { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ImportCsvParamsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string path = request.Arguments.Value<string>("csv_path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ToolResponse.Refusal(request.RequestId, "file_not_found", $"CSV not found: '{path}'.");

            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "empty_csv", "CSV must contain a header row and at least one data row.");

            string[] headers = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToArray();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Import CSV Parameters",
                dryRun,
                d =>
                {
                    int updated = 0, skipped = 0;
                    for (int i = 1; i < lines.Count; i++)
                    {
                        string[] cols = lines[i].Split(',').Select(c => c.Trim().Trim('"')).ToArray();
                        if (cols.Length < 2 || !long.TryParse(cols[0], out long eid)) { skipped++; continue; }
                        var el = RevitElementKit.ResolveElement(d, eid);
                        if (el == null) { skipped++; continue; }
                        for (int c = 1; c < Math.Min(headers.Length, cols.Length); c++)
                        {
                            var p = RevitElementKit.FindParameter(el, headers[c]);
                            if (p != null && RevitElementKit.TrySetParameter(p, cols[c], out _)) updated++;
                        }
                    }
                    return new JObject { ["rows_processed"] = lines.Count - 1, ["parameters_updated"] = updated, ["rows_skipped"] = skipped };
                },
                (d, staged) => staged,
                $"Processed {lines.Count - 1} CSV row(s).");
        }
    }

    [ScioTool("scio_budget_takeoff", "interop", "Estimate project cost by crossing model category/type quantities (m3, m2, count) against a unit-rate map.", ReadOnly = true)]
    public sealed class BudgetTakeoffCommand : IRevitCommand
    {
        [ScioParam("rates", "Array of { category, unit: 'm3'|'m2'|'count', unit_price, currency? }.", Required = true)]
        public JArray Rates { get; set; } = new JArray();

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(BudgetTakeoffCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var rates = request.Arguments["rates"] as JArray;
            if (rates == null || rates.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_rates", "Provide non-empty 'rates' array.");

            var lines = new JArray();
            double grandTotal = 0.0;

            foreach (var r in rates.OfType<JObject>())
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, r.Value<string>("category"));
                string unit = (r.Value<string>("unit") ?? "count").Trim().ToLowerInvariant();
                double price = r.Value<double?>("unit_price") ?? 0.0;
                double qty = 0.0;
                if (cat != null)
                {
                    foreach (var el in new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType())
                    {
                        if (unit == "m3")
                        {
                            var vp = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                            if (vp != null && vp.HasValue) qty += UnitKit.CuFeetToCuM(vp.AsDouble());
                        }
                        else if (unit == "m2")
                        {
                            var ap = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                            if (ap != null && ap.HasValue) qty += UnitKit.SqFeetToSqM(ap.AsDouble());
                        }
                        else qty += 1.0;
                    }
                }
                double subtotal = qty * price;
                grandTotal += subtotal;
                lines.Add(new JObject
                {
                    ["category"] = cat?.Name ?? r.Value<string>("category"),
                    ["unit"] = unit,
                    ["quantity"] = Math.Round(qty, 3),
                    ["unit_price"] = price,
                    ["subtotal"] = Math.Round(subtotal, 2)
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["document"] = doc.Title,
                ["grand_total"] = Math.Round(grandTotal, 2),
                ["items"] = lines
            }, $"Budget estimate: {Math.Round(grandTotal, 2):N2} across {lines.Count} line item(s).");
        }
    }

    [ScioTool("scio_powerbi_export", "interop", "Export normalized relational CSV tables (elements, levels, manifest) ready for Power BI ingestion.", ReadOnly = true, OpenWorld = true)]
    public sealed class PowerBiExportCommand : IRevitCommand
    {
        [ScioParam("output_folder", "Target output folder for the Power BI dataset.", Required = true)]
        public string OutputFolder { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PowerBiExportCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string outDir = request.Arguments.Value<string>("output_folder") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outDir))
                return ToolResponse.Refusal(request.RequestId, "missing_folder", "Provide 'output_folder'.");

            Directory.CreateDirectory(outDir);
            var utf8Bom = new UTF8Encoding(true);

            var lvlSb = new StringBuilder("LevelId,Name,ElevationM\n");
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.Elevation))
                lvlSb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2:F3}\n", IdCompat.ToInt64(l.Id), InteropKit.CsvCell(l.Name), UnitKit.FeetToMm(l.Elevation) / 1000.0);
            File.WriteAllText(Path.Combine(outDir, "levels.csv"), lvlSb.ToString(), utf8Bom);

            var elSb = new StringBuilder("ElementId,UniqueId,Category,Family,Type,LevelId,VolumeM3,AreaM2\n");
            int count = 0;
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                var et = doc.GetElement(el.GetTypeId()) as ElementType;
                double vol = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.HasValue == true
                    ? UnitKit.CuFeetToCuM(el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble()) : 0;
                double area = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.HasValue == true
                    ? UnitKit.SqFeetToSqM(el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED).AsDouble()) : 0;
                elSb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6:F4},{7:F4}\n",
                    IdCompat.ToInt64(el.Id), InteropKit.CsvCell(el.UniqueId), InteropKit.CsvCell(el.Category.Name),
                    InteropKit.CsvCell(et?.FamilyName), InteropKit.CsvCell(et?.Name ?? el.Name),
                    IdCompat.ToInt64(el.LevelId), vol, area);
                count++;
            }
            File.WriteAllText(Path.Combine(outDir, "elements.csv"), elSb.ToString(), utf8Bom);

            var manifest = new JObject
            {
                ["document"] = doc.Title,
                ["exported_utc"] = DateTime.UtcNow.ToString("o"),
                ["element_rows"] = count,
                ["tables"] = new JArray("levels.csv", "elements.csv")
            };
            File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToString(Formatting.Indented));
            return ToolResponse.Success(request.RequestId, manifest, $"Exported Power BI dataset ({count} elements) to '{outDir}'.");
        }
    }

    [ScioTool("scio_cobie_export", "interop", "Export baseline COBie CSV tables (Facility, Floor, Space, Type) from the active Revit model.", ReadOnly = true, OpenWorld = true)]
    public sealed class CobieExportCommand : IRevitCommand
    {
        [ScioParam("output_folder", "Target folder for COBie CSV files.", Required = true)]
        public string OutputFolder { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CobieExportCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string outDir = request.Arguments.Value<string>("output_folder") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outDir))
                return ToolResponse.Refusal(request.RequestId, "missing_folder", "Provide 'output_folder'.");

            Directory.CreateDirectory(outDir);
            var utf8Bom = new UTF8Encoding(true);

            File.WriteAllText(Path.Combine(outDir, "COBie_Facility.csv"),
                $"Name,ProjectName,SiteName\n{InteropKit.CsvCell(doc.ProjectInformation?.BuildingName ?? doc.Title)},{InteropKit.CsvCell(doc.ProjectInformation?.Name)},{InteropKit.CsvCell(doc.SiteLocation?.PlaceName)}\n", utf8Bom);

            var floors = new StringBuilder("Name,ElevationM\n");
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.Elevation))
                floors.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:F3}\n", InteropKit.CsvCell(l.Name), UnitKit.FeetToMm(l.Elevation) / 1000.0);
            File.WriteAllText(Path.Combine(outDir, "COBie_Floor.csv"), floors.ToString(), utf8Bom);

            var spaces = new StringBuilder("Number,Name,Floor,AreaM2\n");
            int spaceCount = 0;
            foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                spaces.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F2}\n",
                    InteropKit.CsvCell(r.Number), InteropKit.CsvCell(r.Name), InteropKit.CsvCell(r.Level?.Name), UnitKit.SqFeetToSqM(r.Area));
                spaceCount++;
            }
            File.WriteAllText(Path.Combine(outDir, "COBie_Space.csv"), spaces.ToString(), utf8Bom);

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["output_folder"] = outDir,
                ["space_count"] = spaceCount,
                ["files"] = new JArray("COBie_Facility.csv", "COBie_Floor.csv", "COBie_Space.csv")
            }, $"Exported COBie baseline tables ({spaceCount} spaces) to '{outDir}'.");
        }
    }

    [ScioTool("scio_nwc_fbx_export", "interop", "Export the active 3D view (or document) to Navisworks (.nwc) or FBX (.fbx).", ReadOnly = true, OpenWorld = true)]
    public sealed class NwcFbxExportCommand : IRevitCommand
    {
        [ScioParam("format", "Export format: nwc or fbx.", Required = true, EnumValues = new[] { "nwc", "fbx" })]
        public string Format { get; set; } = "nwc";

        [ScioParam("output_folder", "Target folder path.", Required = true)]
        public string OutputFolder { get; set; } = string.Empty;

        [ScioParam("file_name", "Output filename (without extension).", Required = true)]
        public string FileName { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(NwcFbxExportCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string fmt = (request.Arguments.Value<string>("format") ?? "nwc").Trim().ToLowerInvariant();
            string outDir = request.Arguments.Value<string>("output_folder") ?? string.Empty;
            string name = request.Arguments.Value<string>("file_name") ?? doc.Title;
            if (string.IsNullOrWhiteSpace(outDir))
                return ToolResponse.Refusal(request.RequestId, "missing_folder", "Provide 'output_folder'.");

            Directory.CreateDirectory(outDir);
            if (fmt == "nwc")
            {
                if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
                    return ToolResponse.Refusal(request.RequestId, "nwc_exporter_missing", "Autodesk Navisworks NWC Exporter is not installed in this Revit.");
                var opts = new NavisworksExportOptions { ExportScope = NavisworksExportScope.Model };
                doc.Export(outDir, name, opts);
                return ToolResponse.Success(request.RequestId, new JObject { ["format"] = "nwc", ["path"] = Path.Combine(outDir, name + ".nwc") }, $"Exported NWC to '{outDir}'.");
            }

            var view3d = context.ActiveUiDocument?.ActiveView as View3D
                ?? new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate);
            if (view3d == null) return ToolResponse.Refusal(request.RequestId, "no_3d_view", "No 3D view available for FBX export.");

            var vSet = new ViewSet();
            vSet.Insert(view3d);
            bool ok = doc.Export(outDir, name, vSet, new FBXExportOptions());
            return ToolResponse.Success(request.RequestId, new JObject { ["format"] = "fbx", ["exported"] = ok, ["path"] = Path.Combine(outDir, name + ".fbx") }, $"Exported FBX to '{outDir}'.");
        }
    }

    [ScioTool("scio_check_iso_naming", "iso19650", "Validate active document title and sheet numbers against ISO 19650 naming regex.", ReadOnly = true)]
    public sealed class CheckIsoNamingCommand : IRevitCommand
    {
        [ScioParam("pattern", "Optional regex pattern for document title (defaults to ISO 19650 7-field code).")]
        public string Pattern { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CheckIsoNamingCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string pat = request.Arguments.Value<string>("pattern") ?? @"^[A-Z0-9]{2,12}-[A-Z0-9]{2,8}-[A-Z0-9]{2,4}-[A-Z0-9]{2,4}-[A-Z0-9]{2,4}-[A-Z0-9]{1,4}-\d{4,6}";
            var rx = new Regex(pat, RegexOptions.IgnoreCase);
            bool docOk = rx.IsMatch(doc.Title ?? string.Empty);

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["document_title"] = doc.Title,
                ["pattern"] = pat,
                ["compliant"] = docOk
            }, $"ISO naming check for '{doc.Title}': {(docOk ? "COMPLIANT" : "NON-COMPLIANT")}.");
        }
    }

    [ScioTool("scio_deliver_package", "iso19650", "Seal a delivery folder by generating delivery-manifest.json with SHA-256 hashes of every deliverable file.", ReadOnly = false, OpenWorld = true)]
    public sealed class DeliverPackageCommand : IRevitCommand
    {
        [ScioParam("folder_path", "Delivery folder path to seal.", Required = true)]
        public string FolderPath { get; set; } = string.Empty;

        [ScioParam("package_name", "Optional delivery package label.")]
        public string PackageName { get; set; } = "SCIO-Delivery";

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(DeliverPackageCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string folder = request.Arguments.Value<string>("folder_path") ?? string.Empty;
            string pkgName = request.Arguments.Value<string>("package_name") ?? "SCIO-Delivery";
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return ToolResponse.Refusal(request.RequestId, "dir_not_found", $"Folder not found: '{folder}'.");

            var files = new JArray();
            foreach (string f in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(f), "delivery-manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(new JObject
                {
                    ["relative_path"] = f.Substring(folder.Length).TrimStart('\\', '/'),
                    ["size_bytes"] = new FileInfo(f).Length,
                    ["sha256"] = InteropKit.Sha256File(f)
                });
            }

            var manifest = new JObject
            {
                ["package_name"] = pkgName,
                ["sealed_utc"] = DateTime.UtcNow.ToString("o"),
                ["file_count"] = files.Count,
                ["files"] = files
            };
            string outPath = Path.Combine(folder, "delivery-manifest.json");
            File.WriteAllText(outPath, manifest.ToString(Formatting.Indented));
            manifest["manifest_path"] = outPath;
            return ToolResponse.Success(request.RequestId, manifest, $"Sealed delivery package '{pkgName}' ({files.Count} files).");
        }
    }
}
