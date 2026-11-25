using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CoreRead
{
    [ScioTool("scio_template_kit", "core", "Read companion template-kit.json conventions plus live model catalogs (types, levels, view templates, parameters) so agents choose real template types.", ReadOnly = true)]
    public sealed class TemplateKitCommand : IRevitCommand
    {
        [ScioParam("kit_path", "Optional path to a template-kit.json file.")]
        public string? KitPath { get; set; }

        [ScioParam("max_types_per_category", "Maximum types returned per category (5-200, default 40).")]
        public int MaxTypesPerCategory { get; set; } = 40;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(TemplateKitCommand));

        private static readonly (string Key, BuiltInCategory Cat)[] CatalogCategories =
        {
            ("walls", BuiltInCategory.OST_Walls),
            ("floors", BuiltInCategory.OST_Floors),
            ("roofs", BuiltInCategory.OST_Roofs),
            ("ceilings", BuiltInCategory.OST_Ceilings),
            ("structural_columns", BuiltInCategory.OST_StructuralColumns),
            ("structural_framing", BuiltInCategory.OST_StructuralFraming),
            ("doors", BuiltInCategory.OST_Doors),
            ("windows", BuiltInCategory.OST_Windows),
            ("plumbing_fixtures", BuiltInCategory.OST_PlumbingFixtures),
            ("pipes", BuiltInCategory.OST_PipeCurves),
            ("ducts", BuiltInCategory.OST_DuctCurves)
        };

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            Document doc = context.RequireActiveDocument(request.TargetDocument);
            string? explicitKit = request.Arguments.Value<string>("kit_path");
            int maxTypes = Math.Max(5, Math.Min(200, request.Arguments.Value<int?>("max_types_per_category") ?? 40));

            string? kitPath = ResolveKitPath(doc, explicitKit);
            JObject? kitJson = null;
            if (!string.IsNullOrEmpty(kitPath) && File.Exists(kitPath))
            {
                try { kitJson = JObject.Parse(File.ReadAllText(kitPath!)); } catch { }
            }

            var catalog = new JObject();
            int totalTypes = 0;
            foreach (var (key, bic) in CatalogCategories)
            {
                var types = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsElementType()
                    .ToElements()
                    .OrderBy(e => (e as ElementType)?.FamilyName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var arr = new JArray();
                foreach (Element et in types.Take(maxTypes))
                {
                    var item = new JObject
                    {
                        ["type_id"] = IdCompat.ToInt64(et.Id),
                        ["family"] = (et as ElementType)?.FamilyName ?? string.Empty,
                        ["type"] = et.Name ?? string.Empty
                    };
                    if (et is WallType wt)
                        item["width_mm"] = Math.Round(wt.Width * 304.8, 1);
                    else if (et is FloorType ft && ft.GetCompoundStructure() is CompoundStructure cs)
                        item["thickness_mm"] = Math.Round(cs.GetWidth() * 304.8, 1);
                    arr.Add(item);
                }

                totalTypes += types.Count;
                catalog[key] = new JObject { ["total"] = types.Count, ["truncated"] = types.Count > maxTypes, ["types"] = arr };
            }

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                .Select(l => new JObject
                {
                    ["level_id"] = IdCompat.ToInt64(l.Id),
                    ["name"] = l.Name,
                    ["elevation_mm"] = Math.Round(l.Elevation * 304.8, 1)
                });

            var viewTemplates = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Take(100)
                .Select(v => new JObject
                {
                    ["view_id"] = IdCompat.ToInt64(v.Id),
                    ["name"] = v.Name,
                    ["view_type"] = v.ViewType.ToString()
                });

            var data = new JObject
            {
                ["document"] = doc.Title,
                ["template_kit_file"] = kitPath,
                ["template_kit_loaded"] = kitJson != null,
                ["template_kit"] = kitJson ?? (JToken)JValue.CreateNull(),
                ["total_catalog_types"] = totalTypes,
                ["levels"] = new JArray(levels),
                ["view_templates"] = new JArray(viewTemplates),
                ["catalog"] = catalog
            };
            return ToolResponse.Success(request.RequestId, data, $"Template kit loaded: {totalTypes} catalog types across {CatalogCategories.Length} categories.");
        }

        private static string? ResolveKitPath(Document doc, string? explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
            if (!string.IsNullOrWhiteSpace(doc.PathName))
            {
                string? dir = Path.GetDirectoryName(doc.PathName);
                if (!string.IsNullOrEmpty(dir))
                {
                    string sidecar = Path.Combine(dir, Path.GetFileNameWithoutExtension(doc.PathName) + ".template-kit.json");
                    if (File.Exists(sidecar)) return sidecar;
                    string folderKit = Path.Combine(dir, "template-kit.json");
                    if (File.Exists(folderKit)) return folderKit;
                }
            }
            string userKit = Path.Combine(ScioRuntimePaths.DataRoot, "kits", "template-kit.json");
            return File.Exists(userKit) ? userKit : null;
        }
    }

    [ScioTool("scio_measure_geometry", "core", "Read-only geometry inspection: 'distance' (3D/XY/Z clear distance in mm), 'relations' (host, inserts, joins, intersections), or 'family_internals' (types & formulas).", ReadOnly = true)]
    public sealed class MeasureGeometryCommand : IRevitCommand
    {
        [ScioParam("operation", "One of: distance, relations, family_internals.", Required = true)]
        public string Operation { get; set; } = "distance";

        [ScioParam("first_id", "First ElementId (for operation=distance).")]
        public long? FirstId { get; set; }

        [ScioParam("second_id", "Second ElementId (for operation=distance).")]
        public long? SecondId { get; set; }

        [ScioParam("element_id", "Target ElementId (for relations or family_internals).")]
        public long? ElementIdArg { get; set; }

        [ScioParam("family_name", "Loadable Family name (alternative to element_id for family_internals).")]
        public string? FamilyName { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MeasureGeometryCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            Document doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "distance").Trim().ToLowerInvariant();
            return op switch
            {
                "distance" => MeasureDistance(doc, request),
                "relations" => QueryRelations(doc, request),
                "family_internals" => QueryFamilyInternals(doc, request),
                _ => ToolResponse.Refusal(request.RequestId, "invalid_operation", $"Unsupported operation '{op}'. Expected: distance, relations, family_internals.")
            };
        }

        private static ToolResponse MeasureDistance(Document doc, ToolRequest request)
        {
            long? idA = request.Arguments.Value<long?>("first_id");
            long? idB = request.Arguments.Value<long?>("second_id");
            if (idA == null || idB == null)
                return ToolResponse.Refusal(request.RequestId, "missing_ids", "operation='distance' requires 'first_id' and 'second_id'.");

            Element? a = doc.GetElement(IdCompat.FromInt64(idA.Value));
            Element? b = doc.GetElement(IdCompat.FromInt64(idB.Value));
            if (a == null || b == null)
                return ToolResponse.Refusal(request.RequestId, "not_found", "One or both elements were not found.");

            BoundingBoxXYZ? bbA = a.get_BoundingBox(null);
            BoundingBoxXYZ? bbB = b.get_BoundingBox(null);
            if (bbA == null || bbB == null)
                return ToolResponse.Refusal(request.RequestId, "no_bbox", "One or both elements have no 3D bounding box.");

            double dx = AxisGap(bbA.Min.X, bbA.Max.X, bbB.Min.X, bbB.Max.X);
            double dy = AxisGap(bbA.Min.Y, bbA.Max.Y, bbB.Min.Y, bbB.Max.Y);
            double dz = AxisGap(bbA.Min.Z, bbA.Max.Z, bbB.Min.Z, bbB.Max.Z);
            XYZ cDelta = ((bbB.Min + bbB.Max) * 0.5) - ((bbA.Min + bbA.Max) * 0.5);

            bool joined = false;
            try { joined = JoinGeometryUtils.AreElementsJoined(doc, a, b); } catch { }

            var data = new JObject
            {
                ["operation"] = "distance",
                ["first_id"] = idA.Value,
                ["second_id"] = idB.Value,
                ["clear_distance_mm"] = Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz) * 304.8, 2),
                ["horizontal_clear_mm"] = Math.Round(Math.Sqrt(dx * dx + dy * dy) * 304.8, 2),
                ["vertical_clear_mm"] = Math.Round(dz * 304.8, 2),
                ["center_distance_mm"] = Math.Round(cDelta.GetLength() * 304.8, 2),
                ["bounding_boxes_overlap"] = dx == 0.0 && dy == 0.0 && dz == 0.0,
                ["joined_geometry"] = joined
            };
            return ToolResponse.Success(request.RequestId, data, $"Clear distance between {idA} and {idB}: {data["clear_distance_mm"]} mm.");
        }

        private static ToolResponse QueryRelations(Document doc, ToolRequest request)
        {
            long? id = request.Arguments.Value<long?>("element_id");
            if (id == null) return ToolResponse.Refusal(request.RequestId, "missing_id", "operation='relations' requires 'element_id'.");

            Element? e = doc.GetElement(IdCompat.FromInt64(id.Value));
            if (e == null) return ToolResponse.Refusal(request.RequestId, "not_found", $"Element {id} not found.");

            long? hostId = (e is FamilyInstance fi && fi.Host != null) ? IdCompat.ToInt64(fi.Host.Id) : (long?)null;
            var inserts = new JArray();
            if (e is HostObject ho)
            {
                try { foreach (ElementId ins in ho.FindInserts(true, true, true, true)) inserts.Add(IdCompat.ToInt64(ins)); } catch { }
            }

            var joined = new JArray();
            try { foreach (ElementId j in JoinGeometryUtils.GetJoinedElements(doc, e)) joined.Add(IdCompat.ToInt64(j)); } catch { }

            var peers = new JArray();
            BoundingBoxXYZ? bb = e.get_BoundingBox(null);
            if (bb != null)
            {
                var filter = new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max));
                foreach (Element p in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().Where(x => x.Id != e.Id).Take(50))
                {
                    peers.Add(new JObject { ["element_id"] = IdCompat.ToInt64(p.Id), ["category"] = p.Category?.Name ?? string.Empty, ["name"] = p.Name ?? string.Empty });
                }
            }

            var data = new JObject
            {
                ["operation"] = "relations",
                ["element_id"] = id.Value,
                ["category"] = e.Category?.Name ?? string.Empty,
                ["host_id"] = hostId.HasValue ? (JToken)hostId.Value : JValue.CreateNull(),
                ["insert_ids"] = inserts,
                ["joined_ids"] = joined,
                ["intersecting_sample"] = peers
            };
            return ToolResponse.Success(request.RequestId, data, $"Relations for {id}: {inserts.Count} inserts, {joined.Count} joined, {peers.Count} intersecting.");
        }

        private static ToolResponse QueryFamilyInternals(Document doc, ToolRequest request)
        {
            long? id = request.Arguments.Value<long?>("element_id");
            string? famName = request.Arguments.Value<string>("family_name");

            Family? fam = null;
            if (id.HasValue)
            {
                Element? e = doc.GetElement(IdCompat.FromInt64(id.Value));
                if (e is FamilyInstance fi) fam = fi.Symbol?.Family;
                else if (e is FamilySymbol fs) fam = fs.Family;
                else if (e is Family f) fam = f;
            }
            else if (!string.IsNullOrWhiteSpace(famName))
            {
                fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                    .FirstOrDefault(f => string.Equals(f.Name, famName, StringComparison.OrdinalIgnoreCase));
            }

            if (fam == null || !fam.IsEditable)
                return ToolResponse.Refusal(request.RequestId, "family_not_editable", "Provide a valid editable loadable family 'element_id' or 'family_name'.");

            Document? famDoc = null;
            try
            {
                famDoc = doc.EditFamily(fam);
                FamilyManager fm = famDoc.FamilyManager;
                var types = new JArray();
                foreach (FamilyType ft in fm.Types) if (!string.IsNullOrWhiteSpace(ft.Name)) types.Add(ft.Name);

                var parms = new JArray();
                foreach (FamilyParameter fp in fm.Parameters)
                {
                    parms.Add(new JObject
                    {
                        ["name"] = fp.Definition?.Name ?? string.Empty,
                        ["is_instance"] = fp.IsInstance,
                        ["is_reporting"] = fp.IsReporting,
                        ["is_shared"] = fp.IsShared,
                        ["formula"] = string.IsNullOrWhiteSpace(fp.Formula) ? (JToken)JValue.CreateNull() : fp.Formula
                    });
                }

                var data = new JObject
                {
                    ["operation"] = "family_internals",
                    ["family_id"] = IdCompat.ToInt64(fam.Id),
                    ["family_name"] = fam.Name,
                    ["type_count"] = types.Count,
                    ["types"] = types,
                    ["parameter_count"] = parms.Count,
                    ["parameters"] = parms
                };
                return ToolResponse.Success(request.RequestId, data, $"Family '{fam.Name}': {types.Count} types, {parms.Count} parameters.");
            }
            finally
            {
                if (famDoc != null) { try { famDoc.Close(false); } catch { } }
            }
        }

        private static double AxisGap(double minA, double maxA, double minB, double maxB)
        {
            if (maxA < minB) return minB - maxA;
            if (maxB < minA) return minA - maxB;
            return 0.0;
        }
    }
}
