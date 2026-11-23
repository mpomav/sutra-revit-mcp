using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Structure
{
    [ScioTool("scio_query_structure", "structure", "Summarize structural elements (columns, framing, foundations, structural walls/floors) with concrete volume (m3) and rebar weight (kg) ratio.", ReadOnly = true)]
    public sealed class QueryStructureCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(QueryStructureCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            BuiltInCategory[] cats =
            {
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralFoundation,
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Floors
            };

            var breakdown = new JArray();
            double totalConcreteM3 = 0.0;
            int totalElements = 0;

            foreach (var bic in cats)
            {
                int count = 0;
                double volM3 = 0.0;
                foreach (var el in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (bic == BuiltInCategory.OST_Walls || bic == BuiltInCategory.OST_Floors)
                    {
                        var sp = el.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)
                                 ?? el.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                        if (sp != null && sp.AsInteger() == 0) continue;
                    }
                    count++;
                    var vp = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                    if (vp != null && vp.HasValue) volM3 += UnitKit.CuFeetToCuM(vp.AsDouble());
                }
                totalElements += count;
                totalConcreteM3 += volM3;
                breakdown.Add(new JObject
                {
                    ["category"] = bic.ToString().Substring(4),
                    ["count"] = count,
                    ["volume_m3"] = Math.Round(volM3, 3)
                });
            }

            double rebarKg = ComputeTotalRebarKg(doc, out int rebarSets);
            double ratioKgPerM3 = totalConcreteM3 > 1e-4 ? Math.Round(rebarKg / totalConcreteM3, 2) : 0.0;

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["document"] = doc.Title,
                ["structural_elements"] = totalElements,
                ["concrete_volume_m3"] = Math.Round(totalConcreteM3, 3),
                ["rebar_sets"] = rebarSets,
                ["rebar_weight_kg"] = Math.Round(rebarKg, 2),
                ["steel_ratio_kg_per_m3"] = ratioKgPerM3,
                ["breakdown"] = breakdown
            }, $"Structure: {totalElements} elements, {Math.Round(totalConcreteM3, 2)} m3 concrete, {Math.Round(rebarKg, 1)} kg rebar ({ratioKgPerM3} kg/m3).");
        }

        internal static double ComputeTotalRebarKg(Document doc, out int setCount)
        {
            setCount = 0;
            double totalKg = 0.0;
            foreach (var r in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>())
            {
                setCount++;
                totalKg += EstimateRebarKg(doc, r);
            }
            return totalKg;
        }

        internal static double EstimateRebarKg(Document doc, Rebar r)
        {
            double totalLenFeet = r.TotalLength;
            double lenM = UnitKit.FeetToMm(totalLenFeet) / 1000.0;
            var barType = doc.GetElement(r.GetTypeId()) as RebarBarType;
            double diamM = barType != null ? UnitKit.FeetToMm(barType.BarNominalDiameter) / 1000.0 : 0.0127;
            double areaM2 = Math.PI * diamM * diamM / 4.0;
            return areaM2 * lenM * 7850.0;
        }
    }

    [ScioTool("scio_rebar_inspect", "structure", "Inspect Rebar sets in the model or hosted by a specific element (bar type, diameter mm, quantity, total length m, weight kg).", ReadOnly = true)]
    public sealed class RebarInspectCommand : IRevitCommand
    {
        [ScioParam("host_id", "Optional host concrete ElementId to filter rebar sets.")]
        public long? HostId { get; set; }

        [ScioParam("limit", "Maximum rebar sets to return (default 200).")]
        public int Limit { get; set; } = 200;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RebarInspectCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            long? hostId = request.Arguments.Value<long?>("host_id");
            int limit = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("limit") ?? 200));

            var items = new JArray();
            double totalKg = 0.0;
            int totalSets = 0;

            foreach (var r in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>())
            {
                long rHost = IdCompat.ToInt64(r.GetHostId());
                if (hostId.HasValue && rHost != hostId.Value) continue;
                totalSets++;

                double kg = QueryStructureCommand.EstimateRebarKg(doc, r);
                totalKg += kg;
                if (items.Count < limit)
                {
                    var bt = doc.GetElement(r.GetTypeId()) as RebarBarType;
                    items.Add(new JObject
                    {
                        ["id"] = IdCompat.ToInt64(r.Id),
                        ["host_id"] = rHost,
                        ["bar_type"] = bt?.Name ?? string.Empty,
                        ["diameter_mm"] = bt == null ? 0 : Math.Round(UnitKit.FeetToMm(bt.BarNominalDiameter), 1),
                        ["quantity"] = r.Quantity,
                        ["total_length_m"] = Math.Round(UnitKit.FeetToMm(r.TotalLength) / 1000.0, 3),
                        ["weight_kg"] = Math.Round(kg, 2)
                    });
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["total_sets"] = totalSets,
                ["total_weight_kg"] = Math.Round(totalKg, 2),
                ["rebar"] = items
            }, $"Inspected {totalSets} rebar set(s), total {Math.Round(totalKg, 1)} kg.");
        }
    }

    [ScioTool("scio_rebar_create", "structure", "Create a straight or polyline Rebar set inside a concrete host element from 3D mm points.", ReadOnly = false)]
    public sealed class RebarCreateCommand : IRevitCommand
    {
        [ScioParam("host_id", "Concrete host ElementId (column, beam, wall, floor, foundation).", Required = true)]
        public long HostId { get; set; }

        [ScioParam("points_mm", "Ordered array of [x, y, z] mm points defining the rebar centerline.", Required = true)]
        public JArray PointsMm { get; set; } = new JArray();

        [ScioParam("normal_vec", "Normal vector [nx, ny, nz] for rebar distribution plane (default [0,0,1]).")]
        public List<double>? NormalVec { get; set; }

        [ScioParam("bar_type", "Optional RebarBarType name substring (e.g. '16', '5/8').")]
        public string BarType { get; set; } = string.Empty;

        [ScioParam("count", "Number of bars in fixed-number layout (default 1).")]
        public int Count { get; set; } = 1;

        [ScioParam("spacing_mm", "Spacing between bars in mm when count > 1 (default 150).")]
        public double SpacingMm { get; set; } = 150;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RebarCreateCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var host = RevitElementKit.ResolveElement(doc, request.Arguments["host_id"]);
            var ptsArr = request.Arguments["points_mm"] as JArray;
            if (host == null || ptsArr == null || ptsArr.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Valid 'host_id' and >=2 'points_mm' required.");

            string btFilter = request.Arguments.Value<string>("bar_type") ?? string.Empty;
            var barType = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .FirstOrDefault(b => string.IsNullOrWhiteSpace(btFilter) || b.Name.IndexOf(btFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            if (barType == null)
                return ToolResponse.Refusal(request.RequestId, "no_bar_type", "No matching RebarBarType loaded in document.");

            var nArr = request.Arguments["normal_vec"] as JArray;
            var normal = nArr != null && nArr.Count >= 3
                ? new XYZ(nArr[0].Value<double>(), nArr[1].Value<double>(), nArr[2].Value<double>()).Normalize()
                : XYZ.BasisZ;

            int count = Math.Max(1, request.Arguments.Value<int?>("count") ?? 1);
            double spacingMm = Math.Max(25.0, request.Arguments.Value<double?>("spacing_mm") ?? 150.0);
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Rebar",
                dryRun,
                d =>
                {
                    var pts = ptsArr.Select(t => new XYZ(
                        UnitKit.MmToFeet(t[0]!.Value<double>()),
                        UnitKit.MmToFeet(t[1]!.Value<double>()),
                        UnitKit.MmToFeet(t[2]!.Value<double>()))).ToList();
                    var curves = new List<Curve>();
                    for (int i = 0; i < pts.Count - 1; i++)
                        if (pts[i].DistanceTo(pts[i + 1]) > 1e-4) curves.Add(Line.CreateBound(pts[i], pts[i + 1]));

#if REVIT2027
                    var rebar = Rebar.CreateFromCurves(d, RebarStyle.Standard, barType, host, normal, curves, new BarTerminationsData(d), true, true);
#else
                    var rebar = Rebar.CreateFromCurves(d, RebarStyle.Standard, barType, null, null, host, normal, curves, RebarHookOrientation.Right, RebarHookOrientation.Right, true, true);
#endif
                    if (count > 1)
                        rebar.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(count, UnitKit.MmToFeet(spacingMm), true, true, true);

                    return new JObject
                    {
                        ["rebar_id"] = IdCompat.ToInt64(rebar.Id),
                        ["host_id"] = IdCompat.ToInt64(host.Id),
                        ["bar_type"] = barType.Name,
                        ["quantity"] = rebar.Quantity,
                        ["weight_kg"] = Math.Round(QueryStructureCommand.EstimateRebarKg(d, rebar), 2)
                    };
                },
                (d, staged) => staged,
                $"Created rebar '{barType.Name}' in host {IdCompat.ToInt64(host.Id)}.");
        }
    }

    [ScioTool("scio_rebar_cover", "structure", "List available RebarCoverTypes or assign a cover type to concrete host elements.", ReadOnly = false)]
    public sealed class RebarCoverCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or assign.", EnumValues = new[] { "list", "assign" })]
        public string Action { get; set; } = "list";

        [ScioParam("cover_name", "RebarCoverType name substring for assign.")]
        public string CoverName { get; set; } = string.Empty;

        [ScioParam("element_ids", "Concrete host ElementIds for assign.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RebarCoverCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var covers = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>().OrderBy(c => c.CoverDistance).ToList();
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var arr = new JArray(covers.Select(c => new JObject
                {
                    ["id"] = IdCompat.ToInt64(c.Id),
                    ["name"] = c.Name,
                    ["cover_mm"] = Math.Round(UnitKit.FeetToMm(c.CoverDistance), 1)
                }));
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["covers"] = arr }, $"Found {arr.Count} rebar cover type(s).");
            }

            string cName = request.Arguments.Value<string>("cover_name") ?? string.Empty;
            var targetCover = covers.FirstOrDefault(c => c.Name.IndexOf(cName, StringComparison.OrdinalIgnoreCase) >= 0);
            var hosts = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (targetCover == null || hosts.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Valid 'cover_name' and 'element_ids' required.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Assign Rebar Cover",
                dryRun,
                d =>
                {
                    BuiltInParameter[] coverParams =
                    {
                        BuiltInParameter.CLEAR_COVER_TOP,
                        BuiltInParameter.CLEAR_COVER_BOTTOM,
                        BuiltInParameter.CLEAR_COVER_OTHER,
                        BuiltInParameter.CLEAR_COVER_EXTERIOR,
                        BuiltInParameter.CLEAR_COVER_INTERIOR
                    };
                    int updated = 0;
                    foreach (var h in hosts)
                    {
                        bool touched = false;
                        foreach (var bip in coverParams)
                        {
                            var p = h.get_Parameter(bip);
                            if (p != null && !p.IsReadOnly) { p.Set(targetCover.Id); touched = true; }
                        }
                        if (touched) updated++;
                    }
                    return new JObject { ["cover"] = targetCover.Name, ["updated_hosts"] = updated };
                },
                (d, staged) => staged,
                $"Assigned cover '{targetCover.Name}' to {hosts.Count} element(s).");
        }
    }

    [ScioTool("scio_structural_connections", "structure", "List structural connection handlers and loaded structural connection types in the document.", ReadOnly = true)]
    public sealed class StructuralConnectionsCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(StructuralConnectionsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var types = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandlerType)).Cast<StructuralConnectionHandlerType>()
                .Select(t => new JObject { ["id"] = IdCompat.ToInt64(t.Id), ["name"] = t.Name });
            var instances = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandler)).Cast<StructuralConnectionHandler>()
                .Take(200)
                .Select(i => new JObject
                {
                    ["id"] = IdCompat.ToInt64(i.Id),
                    ["name"] = i.Name,
                    ["connected_elements"] = new JArray(i.GetConnectedElementIds().Select(IdCompat.ToInt64))
                });
            var tArr = new JArray(types);
            var iArr = new JArray(instances);
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["type_count"] = tArr.Count,
                ["instance_count"] = iArr.Count,
                ["types"] = tArr,
                ["instances"] = iArr
            }, $"Structural connections: {iArr.Count} instance(s), {tArr.Count} loaded type(s).");
        }
    }

    [ScioTool("scio_analytical_audit", "structure", "Compare physical structural element counts against AnalyticalMember / AnalyticalPanel counts in the model.", ReadOnly = true)]
    public sealed class AnalyticalAuditCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(AnalyticalAuditCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            int physCols = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount();
            int physBeams = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().GetElementCount();
            int analyticalMembers = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember)).GetElementCount();
            int analyticalPanels = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalPanel)).GetElementCount();

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["physical_columns"] = physCols,
                ["physical_beams"] = physBeams,
                ["analytical_members"] = analyticalMembers,
                ["analytical_panels"] = analyticalPanels,
                ["member_delta"] = (physCols + physBeams) - analyticalMembers
            }, $"Analytical audit: {physCols + physBeams} physical frame elements vs {analyticalMembers} analytical members, {analyticalPanels} panels.");
        }
    }

    [ScioTool("scio_structural_pdf_schedule", "structure", "Parse structural reinforcement specifications (e.g. '4Ø5/8\" + 2Ø1/2\"', 'Ø3/8\"@15cm', '30x60') from text or a text/CSV schedule file.", ReadOnly = true)]
    public sealed class StructuralScheduleParserCommand : IRevitCommand
    {
        [ScioParam("text", "Raw schedule text to parse (optional if file_path is supplied).")]
        public string Text { get; set; } = string.Empty;

        [ScioParam("file_path", "Optional path to a .txt/.csv schedule extract on disk.")]
        public string FilePath { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(StructuralScheduleParserCommand));

        private static readonly Regex LongBarRx = new Regex(@"(?<qty>\d+)\s*[ØøΦ#]\s*(?<diam>\d+/\d+|\d+(?:\.\d+)?)\s*(?:""|mm)?", RegexOptions.Compiled);
        private static readonly Regex StirrupRx = new Regex(@"[ØøΦ#]\s*(?<diam>\d+/\d+|\d+)\s*(?:""|mm)?\s*@\s*(?<sp>\d+(?:\.\d+)?)\s*(?<unit>cm|mm)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SectionRx = new Regex(@"(?<b>\d{2,4})\s*[xX×]\s*(?<h>\d{2,4})", RegexOptions.Compiled);

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string raw = request.Arguments.Value<string>("text") ?? string.Empty;
            string? path = request.Arguments.Value<string>("file_path");
            if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                raw = File.ReadAllText(path);

            if (string.IsNullOrWhiteSpace(raw))
                return ToolResponse.Refusal(request.RequestId, "empty_input", "Provide 'text' or an existing 'file_path'.");

            var bars = new JArray();
            foreach (Match m in LongBarRx.Matches(raw))
                bars.Add(new JObject { ["qty"] = int.Parse(m.Groups["qty"].Value), ["diameter"] = m.Groups["diam"].Value, ["raw"] = m.Value });

            var stirrups = new JArray();
            foreach (Match m in StirrupRx.Matches(raw))
                stirrups.Add(new JObject { ["diameter"] = m.Groups["diam"].Value, ["spacing"] = m.Groups["sp"].Value, ["unit"] = m.Groups["unit"].Value, ["raw"] = m.Value });

            var sections = new JArray();
            foreach (Match m in SectionRx.Matches(raw))
                sections.Add(new JObject { ["b"] = int.Parse(m.Groups["b"].Value), ["h"] = int.Parse(m.Groups["h"].Value), ["raw"] = m.Value });

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["longitudinal_bars"] = bars,
                ["stirrups"] = stirrups,
                ["sections"] = sections
            }, $"Parsed {bars.Count} bar group(s), {stirrups.Count} stirrup callout(s), {sections.Count} section(s).");
        }
    }
}
