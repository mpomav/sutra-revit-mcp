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

namespace Scio.Module.AuditQa
{
    [ScioTool(
        "scio_material_takeoff",
        "audit",
        "Compute per-material metric takeoff (area m2, volume m3, element count) from compound layers and optional paint faces, with optional organisation-neutral unit_rates costing and CSV/JSON export.",
        ReadOnly = true)]
    public sealed class MaterialTakeoffCommand : IRevitCommand
    {
        [ScioParam("categories", "Optional list of category names (e.g. ['Walls', 'Floors', 'Structural Framing']). Omit for all model categories.")]
        public List<string>? Categories { get; set; }

        [ScioParam("level_name", "Optional Level name filter.")]
        public string? LevelName { get; set; }

        [ScioParam("view_id", "Optional View ElementId to restrict takeoff to elements visible in that view.")]
        public long? ViewId { get; set; }

        [ScioParam("include_paint", "Include painted face materials via GetMaterialIds(true) (default true).")]
        public bool IncludePaint { get; set; } = true;

        [ScioParam("unit_rates", "Optional organisation-neutral rate map: { '<material_name_or_keynote>': { 'rate': 120.5, 'basis': 'area_m2'|'volume_m3', 'currency': 'PEN' } }.")]
        public JObject? UnitRates { get; set; }

        [ScioParam("save_json_path", "Optional absolute path to save takeoff JSON.")]
        public string? SaveJsonPath { get; set; }

        [ScioParam("save_csv_path", "Optional absolute path to save UTF-8 BOM CSV takeoff.")]
        public string? SaveCsvPath { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MaterialTakeoffCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var args = request.Arguments ?? new JObject();

            var catFilter = new HashSet<string>(
                (args["categories"] as JArray)?.Select(t => t.ToString().Trim()).Where(s => !string.IsNullOrEmpty(s))
                ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            string? levelFilter = args.Value<string>("level_name")?.Trim();
            bool includePaint = args.Value<bool?>("include_paint") ?? true;
            var unitRates = args["unit_rates"] as JObject;

            FilteredElementCollector col;
            if (args["view_id"] != null && args["view_id"]!.Type != JTokenType.Null)
            {
                var v = RevitElementKit.ResolveElement(doc, args["view_id"]) as View;
                if (v == null) return ToolResponse.Refusal(request.RequestId, "view_not_found", "Provide a valid 'view_id'.");
                col = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType();
            }
            else
            {
                col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            }

            var buckets = new Dictionary<string, MaterialBucket>(StringComparer.OrdinalIgnoreCase);
            int scannedElements = 0;

            foreach (Element el in col)
            {
                if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                string catName = el.Category.Name;
                if (catFilter.Count > 0 && !catFilter.Contains(catName)) continue;

                string lvl = ResolveLevelName(doc, el);
                if (!string.IsNullOrWhiteSpace(levelFilter) && !string.Equals(lvl, levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                scannedElements++;
                AccumulateMaterials(doc, el, catName, lvl, isPaint: false, buckets);
                if (includePaint)
                    AccumulateMaterials(doc, el, catName, lvl, isPaint: true, buckets);
            }

            var rows = new JArray();
            double totalAreaM2 = 0.0;
            double totalVolumeM3 = 0.0;
            double totalCost = 0.0;
            bool hasCost = false;

            foreach (var b in buckets.Values.OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Category).ThenBy(x => x.Level))
            {
                double areaM2 = Math.Round(b.AreaM2, 3);
                double volM3 = Math.Round(b.VolumeM3, 3);
                totalAreaM2 += areaM2;
                totalVolumeM3 += volM3;

                var row = new JObject
                {
                    ["material_id"] = b.MaterialId,
                    ["material_name"] = b.MaterialName,
                    ["material_class"] = b.MaterialClass,
                    ["keynote"] = b.Keynote,
                    ["description"] = b.Description,
                    ["category"] = b.Category,
                    ["level"] = b.Level,
                    ["is_paint"] = b.IsPaint,
                    ["element_count"] = b.ElementIds.Count,
                    ["area_m2"] = areaM2,
                    ["volume_m3"] = volM3
                };

                if (unitRates != null && TryResolveRate(unitRates, b.MaterialName, b.Keynote, out double rate, out string basis, out string currency))
                {
                    double qty = string.Equals(basis, "volume_m3", StringComparison.OrdinalIgnoreCase) ? volM3 : areaM2;
                    double cost = Math.Round(qty * rate, 2);
                    row["unit_rate"] = rate;
                    row["rate_basis"] = basis;
                    row["currency"] = currency;
                    row["estimated_cost"] = cost;
                    totalCost += cost;
                    hasCost = true;
                }

                rows.Add(row);
            }

            var data = new JObject
            {
                ["document"] = doc.Title,
                ["scanned_elements"] = scannedElements,
                ["group_count"] = rows.Count,
                ["total_area_m2"] = Math.Round(totalAreaM2, 3),
                ["total_volume_m3"] = Math.Round(totalVolumeM3, 3),
                ["rows"] = rows
            };
            if (hasCost) data["total_estimated_cost"] = Math.Round(totalCost, 2);

            string? saveJson = args.Value<string>("save_json_path");
            if (!string.IsNullOrWhiteSpace(saveJson))
            {
                string? dir = Path.GetDirectoryName(saveJson);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(saveJson!, data.ToString(Formatting.Indented), new UTF8Encoding(false));
                data["saved_json_path"] = saveJson;
            }

            string? saveCsv = args.Value<string>("save_csv_path");
            if (!string.IsNullOrWhiteSpace(saveCsv))
            {
                string? dir = Path.GetDirectoryName(saveCsv);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine("material_id,material_name,material_class,keynote,category,level,is_paint,element_count,area_m2,volume_m3,estimated_cost");
                foreach (var r in rows.OfType<JObject>())
                {
                    sb.AppendLine(string.Join(",",
                        r["material_id"],
                        CsvEscape(r.Value<string>("material_name")),
                        CsvEscape(r.Value<string>("material_class")),
                        CsvEscape(r.Value<string>("keynote")),
                        CsvEscape(r.Value<string>("category")),
                        CsvEscape(r.Value<string>("level")),
                        r.Value<bool>("is_paint") ? "true" : "false",
                        r["element_count"],
                        r.Value<double>("area_m2").ToString(CultureInfo.InvariantCulture),
                        r.Value<double>("volume_m3").ToString(CultureInfo.InvariantCulture),
                        r["estimated_cost"] != null ? r.Value<double>("estimated_cost").ToString(CultureInfo.InvariantCulture) : string.Empty));
                }
                File.WriteAllText(saveCsv!, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                data["saved_csv_path"] = saveCsv;
            }

            return ToolResponse.Success(
                request.RequestId,
                data,
                $"Material takeoff: {rows.Count} material group(s) from {scannedElements} element(s), {Math.Round(totalAreaM2, 2)} m2, {Math.Round(totalVolumeM3, 2)} m3.");
        }

        private static void AccumulateMaterials(
            Document doc,
            Element el,
            string catName,
            string lvl,
            bool isPaint,
            Dictionary<string, MaterialBucket> buckets)
        {
            ICollection<ElementId>? matIds;
            try { matIds = el.GetMaterialIds(isPaint); }
            catch { return; }
            if (matIds == null || matIds.Count == 0) return;

            long elId = IdCompat.ToInt64(el.Id);
            foreach (var matId in matIds)
            {
                if (matId == ElementId.InvalidElementId) continue;
                var mat = doc.GetElement(matId) as Material;
                string matName = mat?.Name ?? $"Material_{IdCompat.ToInt64(matId)}";
                string matClass = mat?.MaterialClass ?? string.Empty;
                string keynote = mat?.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString() ?? string.Empty;
                string desc = mat?.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? string.Empty;

                double areaM2 = 0.0;
                double volM3 = 0.0;
                try { areaM2 = UnitKit.SqFeetToSqM(el.GetMaterialArea(matId, isPaint)); } catch { }
                if (!isPaint)
                {
                    try { volM3 = UnitKit.CuFeetToCuM(el.GetMaterialVolume(matId)); } catch { }
                }

                if (areaM2 <= 1e-6 && volM3 <= 1e-6) continue;

                long mId = IdCompat.ToInt64(matId);
                string key = $"{mId}|{catName}|{lvl}|{isPaint}";
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new MaterialBucket
                    {
                        MaterialId = mId,
                        MaterialName = matName,
                        MaterialClass = matClass,
                        Keynote = keynote,
                        Description = desc,
                        Category = catName,
                        Level = lvl,
                        IsPaint = isPaint
                    };
                    buckets[key] = bucket;
                }

                bucket.AreaM2 += areaM2;
                bucket.VolumeM3 += volM3;
                bucket.ElementIds.Add(elId);
            }
        }

        private static bool TryResolveRate(JObject unitRates, string matName, string keynote, out double rate, out string basis, out string currency)
        {
            rate = 0.0;
            basis = "area_m2";
            currency = string.Empty;

            JToken? entry = null;
            if (!string.IsNullOrWhiteSpace(keynote))
                entry = unitRates.Properties().FirstOrDefault(p => string.Equals(p.Name, keynote, StringComparison.OrdinalIgnoreCase))?.Value;
            if (entry == null)
                entry = unitRates.Properties().FirstOrDefault(p => string.Equals(p.Name, matName, StringComparison.OrdinalIgnoreCase))?.Value;
            if (entry == null) return false;

            if (entry.Type == JTokenType.Float || entry.Type == JTokenType.Integer)
            {
                rate = entry.Value<double>();
                return true;
            }
            if (entry is JObject obj && obj["rate"] != null)
            {
                rate = obj.Value<double>("rate");
                basis = obj.Value<string>("basis") ?? "area_m2";
                currency = obj.Value<string>("currency") ?? string.Empty;
                return true;
            }
            return false;
        }

        private static string ResolveLevelName(Document doc, Element el)
        {
            if (el.LevelId != ElementId.InvalidElementId && doc.GetElement(el.LevelId) is Level l)
                return l.Name;
            var refLvl = el.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
            if (refLvl != null && refLvl != ElementId.InvalidElementId && doc.GetElement(refLvl) is Level rl)
                return rl.Name;
            return "(Unassigned)";
        }

        private static string CsvEscape(string? val)
        {
            string s = val ?? string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private sealed class MaterialBucket
        {
            public long MaterialId { get; set; }
            public string MaterialName { get; set; } = string.Empty;
            public string MaterialClass { get; set; } = string.Empty;
            public string Keynote { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string Level { get; set; } = string.Empty;
            public bool IsPaint { get; set; }
            public double AreaM2 { get; set; }
            public double VolumeM3 { get; set; }
            public HashSet<long> ElementIds { get; } = new HashSet<long>();
        }
    }
}
