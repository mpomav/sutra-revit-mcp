using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CadIfc
{
    internal static class CadGeometryKit
    {
        public static List<Line> ExtractLayerLines(Document doc, ImportInstance cad, string layerName)
        {
            var lines = new List<Line>();
            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            var geom = cad.get_Geometry(opt);
            if (geom == null) return lines;
            CollectLines(doc, geom, Transform.Identity, layerName, lines, null);
            return lines;
        }

        public static Dictionary<string, int> CountLayerLines(Document doc, ImportInstance cad)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            var geom = cad.get_Geometry(opt);
            if (geom == null) return counts;
            CollectLines(doc, geom, Transform.Identity, null, null, counts);
            return counts;
        }

        private static void CollectLines(
            Document doc,
            GeometryElement geom,
            Transform xform,
            string? layerName,
            List<Line>? acc,
            Dictionary<string, int>? counts)
        {
            foreach (GeometryObject obj in geom)
            {
                if (obj is GeometryInstance gi)
                {
                    CollectLines(doc, gi.GetSymbolGeometry(), xform.Multiply(gi.Transform), layerName, acc, counts);
                    continue;
                }

                string? objLayer = null;
                if (obj.GraphicsStyleId != ElementId.InvalidElementId && doc.GetElement(obj.GraphicsStyleId) is GraphicsStyle gs)
                    objLayer = gs.GraphicsStyleCategory?.Name;

                if (!string.IsNullOrWhiteSpace(layerName) &&
                    !string.Equals(objLayer, layerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string layerKey = string.IsNullOrWhiteSpace(objLayer) ? "(Unnamed)" : objLayer!;
                double minCurveFt = Math.Max(1e-3, doc.Application.ShortCurveTolerance * 1.05);

                if (obj is Line ln)
                {
                    var p0 = xform.OfPoint(ln.GetEndPoint(0));
                    var p1 = xform.OfPoint(ln.GetEndPoint(1));
                    if (p0.DistanceTo(p1) >= minCurveFt)
                    {
                        try
                        {
                            acc?.Add(Line.CreateBound(p0, p1));
                            if (counts != null) counts[layerKey] = counts.TryGetValue(layerKey, out int c) ? c + 1 : 1;
                        }
                        catch { }
                    }
                }
                else if (obj is PolyLine pl)
                {
                    var pts = pl.GetCoordinates();
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        var p0 = xform.OfPoint(pts[i]);
                        var p1 = xform.OfPoint(pts[i + 1]);
                        if (p0.DistanceTo(p1) >= minCurveFt)
                        {
                            try
                            {
                                acc?.Add(Line.CreateBound(p0, p1));
                                if (counts != null) counts[layerKey] = counts.TryGetValue(layerKey, out int c) ? c + 1 : 1;
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        public readonly struct PairedWallSegment
        {
            public PairedWallSegment(XYZ p0, XYZ p1, double thicknessMm)
            {
                P0 = p0;
                P1 = p1;
                ThicknessMm = thicknessMm;
            }
            public XYZ P0 { get; }
            public XYZ P1 { get; }
            public double ThicknessMm { get; }
        }

        public static List<PairedWallSegment> PairParallelWalls(
            List<Line> lines,
            double minThicknessFt,
            double maxThicknessFt,
            double minOverlapRatio = 0.5,
            double angleTolDeg = 2.0)
        {
            var result = new List<PairedWallSegment>();
            if (lines.Count < 2) return result;

            double cosTol = Math.Cos(angleTolDeg * Math.PI / 180.0);
            int n = lines.Count;
            var a0 = new XYZ[n];
            var a1 = new XYZ[n];
            var dir = new XYZ[n];
            var len = new double[n];

            for (int i = 0; i < n; i++)
            {
                var p0 = lines[i].GetEndPoint(0);
                var p1 = lines[i].GetEndPoint(1);
                a0[i] = new XYZ(p0.X, p0.Y, 0);
                a1[i] = new XYZ(p1.X, p1.Y, 0);
                double l = a0[i].DistanceTo(a1[i]);
                len[i] = l;
                dir[i] = l > 1e-6 ? (a1[i] - a0[i]) / l : XYZ.BasisX;
            }

            // Collect candidate pairs and match greedily by smallest perpendicular distance then largest overlap.
            var candidates = new List<(int I, int J, double Dist, double Overlap, XYZ C0, XYZ C1)>();
            for (int i = 0; i < n; i++)
            {
                if (len[i] < 1e-3) continue;
                var di = dir[i];
                var ni = new XYZ(-di.Y, di.X, 0);
                double sA0 = a0[i].DotProduct(di);
                double sA1 = a1[i].DotProduct(di);
                if (sA0 > sA1) { double t = sA0; sA0 = sA1; sA1 = t; }
                double tA = a0[i].DotProduct(ni);

                for (int j = i + 1; j < n; j++)
                {
                    if (len[j] < 1e-3) continue;
                    double dot = Math.Abs(di.DotProduct(dir[j]));
                    if (dot < cosTol) continue;

                    double tB = 0.5 * (a0[j].DotProduct(ni) + a1[j].DotProduct(ni));
                    double dist = Math.Abs(tB - tA);
                    if (dist < minThicknessFt || dist > maxThicknessFt) continue;

                    double sB0 = a0[j].DotProduct(di);
                    double sB1 = a1[j].DotProduct(di);
                    if (sB0 > sB1) { double t = sB0; sB0 = sB1; sB1 = t; }

                    double ov0 = Math.Max(sA0, sB0);
                    double ov1 = Math.Min(sA1, sB1);
                    double ov = ov1 - ov0;
                    double minLen = Math.Min(len[i], len[j]);
                    if (ov <= dist || ov < minOverlapRatio * minLen) continue;

                    double tMid = 0.5 * (tA + tB);
                    var c0 = di * ov0 + ni * tMid;
                    var c1 = di * ov1 + ni * tMid;
                    candidates.Add((i, j, dist, ov, c0, c1));
                }
            }

            candidates.Sort((x, y) =>
            {
                int cmp = x.Dist.CompareTo(y.Dist);
                return cmp != 0 ? cmp : y.Overlap.CompareTo(x.Overlap);
            });

            var used = new bool[n];
            foreach (var c in candidates)
            {
                if (used[c.I] || used[c.J]) continue;
                used[c.I] = true;
                used[c.J] = true;
                result.Add(new PairedWallSegment(c.C0, c.C1, Math.Round(UnitKit.FeetToMm(c.Dist), 1)));
            }

            return result;
        }
    }

    [ScioTool("scio_cad_inspect", "cad_ifc", "Inspect linked or imported CAD instances (DWG/DXF): list layers, line counts per layer, bounding boxes in mm, and optional compact [x1,y1,x2,y2] mm geometry for a layer.", ReadOnly = true)]
    public sealed class CadInspectCommand : IRevitCommand
    {
        [ScioParam("import_id", "Optional ImportInstance ElementId to inspect in detail.")]
        public long? ImportId { get; set; }

        [ScioParam("include_counts", "Include per-layer line/polyline segment counts (default true when import_id is specified).")]
        public bool? IncludeCounts { get; set; }

        [ScioParam("include_geometry", "When true, return compact integer-mm [x1,y1,x2,y2] segments for 'layer_name' (default false).")]
        public bool IncludeGeometry { get; set; } = false;

        [ScioParam("layer_name", "CAD layer name to extract geometry from when include_geometry=true.")]
        public string LayerName { get; set; } = string.Empty;

        [ScioParam("max_segments", "Maximum geometry segments to return when include_geometry=true (default 1000, max 5000).")]
        public int MaxSegments { get; set; } = 1000;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CadInspectCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            long? idArg = request.Arguments.Value<long?>("import_id");
            bool includeCounts = request.Arguments.Value<bool?>("include_counts") ?? idArg.HasValue;
            bool includeGeometry = request.Arguments.Value<bool?>("include_geometry") ?? false;
            string layerFilter = request.Arguments.Value<string>("layer_name") ?? string.Empty;
            int maxSegments = Math.Max(1, Math.Min(5000, request.Arguments.Value<int?>("max_segments") ?? 1000));

            var instances = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => !idArg.HasValue || IdCompat.ToInt64(i.Id) == idArg.Value)
                .ToList();

            var arr = new JArray();
            foreach (var inst in instances)
            {
                var layers = new JArray();
                if (inst.Category != null)
                {
                    foreach (Category sub in inst.Category.SubCategories)
                        layers.Add(sub.Name);
                }

                var item = new JObject
                {
                    ["id"] = IdCompat.ToInt64(inst.Id),
                    ["name"] = inst.Category?.Name ?? inst.Name,
                    ["is_linked"] = inst.IsLinked,
                    ["layer_count"] = layers.Count,
                    ["layers"] = layers,
                    ["bbox_mm"] = RevitElementKit.SerializeBoundingBoxMm(inst.get_BoundingBox(null))
                };

                if (includeCounts)
                {
                    var counts = CadGeometryKit.CountLayerLines(doc, inst);
                    var countsObj = new JObject();
                    foreach (var kv in counts.OrderByDescending(k => k.Value))
                        countsObj[kv.Key] = kv.Value;
                    item["layer_counts"] = countsObj;
                }

                if (includeGeometry)
                {
                    var segs = CadGeometryKit.ExtractLayerLines(doc, inst, layerFilter);
                    var segArr = new JArray();
                    foreach (var ln in segs.Take(maxSegments))
                    {
                        var p0 = ln.GetEndPoint(0);
                        var p1 = ln.GetEndPoint(1);
                        segArr.Add(new JArray(
                            (int)Math.Round(UnitKit.FeetToMm(p0.X)),
                            (int)Math.Round(UnitKit.FeetToMm(p0.Y)),
                            (int)Math.Round(UnitKit.FeetToMm(p1.X)),
                            (int)Math.Round(UnitKit.FeetToMm(p1.Y))));
                    }
                    item["geometry_layer"] = string.IsNullOrWhiteSpace(layerFilter) ? "(all)" : layerFilter;
                    item["total_segments"] = segs.Count;
                    item["returned_segments"] = segArr.Count;
                    item["segments_mm"] = segArr;
                }

                arr.Add(item);
            }

            return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["cad_instances"] = arr }, $"Inspected {arr.Count} CAD instance(s).");
        }
    }

    [ScioTool("scio_cad_link", "cad_ifc", "Link a DWG/DXF file into the active Revit view with specified import unit.", ReadOnly = false)]
    public sealed class CadLinkCommand : IRevitCommand
    {
        [ScioParam("dwg_path", "Absolute path to the .dwg/.dxf file.", Required = true)]
        public string DwgPath { get; set; } = string.Empty;

        [ScioParam("unit", "CAD file unit: mm, cm, m (default), or default.", EnumValues = new[] { "mm", "cm", "m", "default" })]
        public string Unit { get; set; } = "m";

        [ScioParam("this_view_only", "Link only in active view (default true).")]
        public bool ThisViewOnly { get; set; } = true;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CadLinkCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = context.ActiveUiDocument?.ActiveView;
            string path = request.Arguments.Value<string>("dwg_path") ?? string.Empty;
            if (view == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", $"Active view and existing 'dwg_path' required ('{path}').");

            string unitStr = (request.Arguments.Value<string>("unit") ?? "m").Trim().ToLowerInvariant();
            bool thisViewOnly = request.Arguments.Value<bool?>("this_view_only") ?? true;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Link CAD {Path.GetFileName(path)}",
                dryRun,
                d =>
                {
                    var opts = new DWGImportOptions
                    {
                        ThisViewOnly = thisViewOnly,
                        Placement = ImportPlacement.Origin,
                        Unit = unitStr switch
                        {
                            "mm" => ImportUnit.Millimeter,
                            "cm" => ImportUnit.Centimeter,
                            "m" => ImportUnit.Meter,
                            _ => ImportUnit.Default
                        }
                    };
                    bool ok = d.Link(path, opts, view, out ElementId linkId);
                    return new JObject { ["linked"] = ok, ["id"] = IdCompat.ToInt64(linkId), ["path"] = path };
                },
                (d, staged) => staged,
                $"Linked CAD '{Path.GetFileName(path)}'.");
        }
    }

    [ScioTool("scio_cad_to_walls", "cad_ifc", "Convert lines/polylines from a specified CAD ImportInstance layer into Revit Walls (centerline or double_line parallel-face pairing) with Basic wall default, optional type_id/thickness_map, and dry-run preview.", ReadOnly = false)]
    public sealed class CadToWallsCommand : IRevitCommand
    {
        [ScioParam("import_id", "ImportInstance ElementId.", Required = true)]
        public long ImportId { get; set; }

        [ScioParam("layer_name", "CAD layer name containing wall lines or double-line faces.", Required = true)]
        public string LayerName { get; set; } = string.Empty;

        [ScioParam("mode", "Conversion mode: 'centerline' (default, 1 wall per line) or 'double_line' (pairs parallel wall faces into centerline + thickness).", EnumValues = new[] { "centerline", "double_line" })]
        public string Mode { get; set; } = "centerline";

        [ScioParam("level", "Target level name.")]
        public string LevelName { get; set; } = string.Empty;

        [ScioParam("type_id", "Optional default WallType ElementId (otherwise first Basic WallType is used, never Curtain Wall).")]
        public long? TypeId { get; set; }

        [ScioParam("wall_type", "Optional default WallType name (case-insensitive).")]
        public string WallTypeName { get; set; } = string.Empty;

        [ScioParam("height_mm", "Wall height in mm (default 3000).")]
        public double HeightMm { get; set; } = 3000;

        [ScioParam("min_length_mm", "Ignore segments shorter than this mm threshold (default 150).")]
        public double MinLengthMm { get; set; } = 150;

        [ScioParam("min_thickness_mm", "double_line: minimum parallel-face distance in mm (default 80).")]
        public double MinThicknessMm { get; set; } = 80;

        [ScioParam("max_thickness_mm", "double_line: maximum parallel-face distance in mm (default 460).")]
        public double MaxThicknessMm { get; set; } = 460;

        [ScioParam("thickness_tolerance_mm", "double_line: matching tolerance in mm for thickness_map entries (default 20).")]
        public double ThicknessToleranceMm { get; set; } = 20;

        [ScioParam("thickness_map", "double_line: optional JSON object mapping CAD thickness in mm (e.g. '130', '240') to a WallType ElementId or WallType name.")]
        public JObject? ThicknessMap { get; set; }

        [ScioParam("skip_unmatched_thickness", "double_line: when true and thickness_map is provided, skip walls whose thickness is not in thickness_map instead of falling back to default type (default false).")]
        public bool SkipUnmatchedThickness { get; set; } = false;

        [ScioParam("max_walls", "Maximum walls to create in one call (default 2000, max 5000). Truncation is explicitly reported.")]
        public int MaxWalls { get; set; } = 2000;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CadToWallsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var cad = RevitElementKit.ResolveElement(doc, request.Arguments["import_id"]) as ImportInstance;
            string layer = request.Arguments.Value<string>("layer_name") ?? string.Empty;
            if (cad == null || string.IsNullOrWhiteSpace(layer))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Valid 'import_id' and 'layer_name' required.");

            string mode = (request.Arguments.Value<string>("mode") ?? "centerline").Trim().ToLowerInvariant();
            string? lvlName = request.Arguments.Value<string>("level");
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                .FirstOrDefault(l => string.IsNullOrWhiteSpace(lvlName) || string.Equals(l.Name, lvlName, StringComparison.OrdinalIgnoreCase));
            if (lvl == null) return ToolResponse.Refusal(request.RequestId, "no_level", "Target level not found.");

            var allWallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
            WallType? defaultWallType = null;
            long? reqTypeId = request.Arguments.Value<long?>("type_id");
            string? reqTypeName = request.Arguments.Value<string>("wall_type");
            if (reqTypeId.HasValue && reqTypeId.Value > 0)
                defaultWallType = doc.GetElement(IdCompat.FromInt64(reqTypeId.Value)) as WallType;
            if (defaultWallType == null && !string.IsNullOrWhiteSpace(reqTypeName))
                defaultWallType = allWallTypes.FirstOrDefault(wt => string.Equals(wt.Name, reqTypeName.Trim(), StringComparison.OrdinalIgnoreCase));
            defaultWallType ??= allWallTypes.FirstOrDefault(wt => wt.Kind == WallKind.Basic) ?? allWallTypes.FirstOrDefault();
            if (defaultWallType == null)
                return ToolResponse.Refusal(request.RequestId, "no_wall_type", "No WallType found in document.");

            double minLenFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("min_length_mm") ?? 150.0);
            double heightFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("height_mm") ?? 3000.0);
            var lines = CadGeometryKit.ExtractLayerLines(doc, cad, layer).Where(l => l.Length >= minLenFt).ToList();
            if (lines.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_lines", $"No qualifying lines found on CAD layer '{layer}'.");

            // Parse optional thickness_map for double_line mode
            var thicknessRules = new List<(double TargetMm, WallType Type)>();
            if (request.Arguments["thickness_map"] is JObject tmap)
            {
                foreach (var prop in tmap.Properties())
                {
                    if (!double.TryParse(prop.Name, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double targetMm))
                        continue;
                    WallType? mapped = null;
                    if (prop.Value.Type == JTokenType.Integer)
                        mapped = doc.GetElement(IdCompat.FromInt64(prop.Value.Value<long>())) as WallType;
                    else
                        mapped = allWallTypes.FirstOrDefault(wt => string.Equals(wt.Name, prop.Value?.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (mapped != null) thicknessRules.Add((targetMm, mapped));
                }
            }

            double minThickFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("min_thickness_mm") ?? 80.0);
            double maxThickFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("max_thickness_mm") ?? 460.0);
            double thickTolMm = Math.Max(1.0, request.Arguments.Value<double?>("thickness_tolerance_mm") ?? 20.0);
            bool skipUnmatched = request.Arguments.Value<bool?>("skip_unmatched_thickness") ?? false;
            int maxWalls = Math.Max(1, Math.Min(5000, request.Arguments.Value<int?>("max_walls") ?? 2000));
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"CAD to Walls ({layer}, {mode})",
                dryRun,
                d =>
                {
                    double shortTol = Math.Max(minLenFt, d.Application.ShortCurveTolerance * 1.05);
                    var ids = new JArray();
                    int skippedShort = 0;
                    int skippedFailed = 0;
                    int skippedUnmatched = 0;
                    var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var unmatchedByThick = new Dictionary<int, int>();

                    if (mode == "double_line")
                    {
                        var paired = CadGeometryKit.PairParallelWalls(lines, minThickFt, maxThickFt);
                        int skippedByLimit = Math.Max(0, paired.Count - maxWalls);
                        foreach (var seg in paired.Take(maxWalls))
                        {
                            var p0 = new XYZ(seg.P0.X, seg.P0.Y, lvl.Elevation);
                            var p1 = new XYZ(seg.P1.X, seg.P1.Y, lvl.Elevation);
                            if (p0.DistanceTo(p1) < shortTol) { skippedShort++; continue; }

                            WallType? chosen = null;
                            if (thicknessRules.Count > 0)
                            {
                                var best = thicknessRules
                                    .Select(r => (Rule: r, Diff: Math.Abs(r.TargetMm - seg.ThicknessMm)))
                                    .Where(x => x.Diff <= thickTolMm)
                                    .OrderBy(x => x.Diff)
                                    .FirstOrDefault();
                                chosen = best.Rule.Type;
                                if (chosen == null)
                                {
                                    int bucketMm = (int)(Math.Round(seg.ThicknessMm / 10.0) * 10);
                                    unmatchedByThick[bucketMm] = unmatchedByThick.TryGetValue(bucketMm, out int uc) ? uc + 1 : 1;
                                    if (skipUnmatched) { skippedUnmatched++; continue; }
                                }
                            }
                            chosen ??= defaultWallType;

                            try
                            {
                                var w = Wall.Create(d, Line.CreateBound(p0, p1), chosen.Id, lvl.Id, heightFt, 0.0, false, false);
                                byType[chosen.Name] = byType.TryGetValue(chosen.Name, out int tc) ? tc + 1 : 1;
                                if (!dryRun) ids.Add(IdCompat.ToInt64(w.Id));
                                else ids.Add(0);
                            }
                            catch { skippedFailed++; }
                        }

                        var payload = new JObject
                        {
                            ["layer"] = layer,
                            ["mode"] = mode,
                            ["default_wall_type"] = defaultWallType.Name,
                            ["source_lines"] = lines.Count,
                            ["paired_walls"] = paired.Count,
                            ["created_walls"] = ids.Count,
                            ["max_walls"] = maxWalls,
                            ["truncated"] = skippedByLimit > 0,
                            ["skipped_by_limit"] = skippedByLimit,
                            ["by_type"] = JObject.FromObject(byType),
                            ["skipped_short_segments"] = skippedShort,
                            ["skipped_unmatched_thickness"] = skippedUnmatched,
                            ["skipped_failed_segments"] = skippedFailed
                        };
                        if (skippedByLimit > 0)
                            payload["warning"] = $"Truncated by max_walls={maxWalls}: {skippedByLimit} paired wall(s) were not processed. Increase max_walls (up to 5000) to process all.";
                        if (unmatchedByThick.Count > 0)
                            payload["unmatched_thickness_mm_counts"] = JObject.FromObject(unmatchedByThick.ToDictionary(k => k.Key.ToString(), v => v.Value));
                        if (!dryRun) payload["wall_ids"] = ids;
                        return payload;
                    }
                    else
                    {
                        int skippedByLimit = Math.Max(0, lines.Count - maxWalls);
                        foreach (var ln in lines.Take(maxWalls))
                        {
                            var p0 = new XYZ(ln.GetEndPoint(0).X, ln.GetEndPoint(0).Y, lvl.Elevation);
                            var p1 = new XYZ(ln.GetEndPoint(1).X, ln.GetEndPoint(1).Y, lvl.Elevation);
                            if (p0.DistanceTo(p1) < shortTol) { skippedShort++; continue; }
                            try
                            {
                                var w = Wall.Create(d, Line.CreateBound(p0, p1), defaultWallType.Id, lvl.Id, heightFt, 0.0, false, false);
                                if (!dryRun) ids.Add(IdCompat.ToInt64(w.Id));
                                else ids.Add(0);
                            }
                            catch { skippedFailed++; }
                        }

                        var payload = new JObject
                        {
                            ["layer"] = layer,
                            ["mode"] = mode,
                            ["default_wall_type"] = defaultWallType.Name,
                            ["source_lines"] = lines.Count,
                            ["created_walls"] = ids.Count,
                            ["max_walls"] = maxWalls,
                            ["truncated"] = skippedByLimit > 0,
                            ["skipped_by_limit"] = skippedByLimit,
                            ["skipped_short_segments"] = skippedShort,
                            ["skipped_failed_segments"] = skippedFailed
                        };
                        if (skippedByLimit > 0)
                            payload["warning"] = $"Truncated by max_walls={maxWalls}: {skippedByLimit} line(s) were not processed. Increase max_walls (up to 5000) to process all.";
                        if (!dryRun) payload["wall_ids"] = ids;
                        return payload;
                    }
                },
                (d, staged) => staged,
                $"Converted CAD layer '{layer}' ({mode}) into walls.");
        }
    }

    [ScioTool("scio_cad_to_grids", "cad_ifc", "Convert lines from a specified CAD ImportInstance layer into Revit Grids with dry-run preview.", ReadOnly = false)]
    public sealed class CadToGridsCommand : IRevitCommand
    {
        [ScioParam("import_id", "ImportInstance ElementId.", Required = true)]
        public long ImportId { get; set; }

        [ScioParam("layer_name", "CAD layer name containing grid lines.", Required = true)]
        public string LayerName { get; set; } = string.Empty;

        [ScioParam("max_grids", "Maximum grids to create in one call (default 500, max 2000). Truncation is explicitly reported.")]
        public int MaxGrids { get; set; } = 500;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CadToGridsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var cad = RevitElementKit.ResolveElement(doc, request.Arguments["import_id"]) as ImportInstance;
            string layer = request.Arguments.Value<string>("layer_name") ?? string.Empty;
            if (cad == null || string.IsNullOrWhiteSpace(layer))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Valid 'import_id' and 'layer_name' required.");

            var lines = CadGeometryKit.ExtractLayerLines(doc, cad, layer).Where(l => l.Length >= 1.0).ToList();
            if (lines.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_lines", $"No grid lines found on CAD layer '{layer}'.");

            int maxGrids = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("max_grids") ?? 500));
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"CAD to Grids ({layer})",
                dryRun,
                d =>
                {
                    double shortTol = Math.Max(1.0, d.Application.ShortCurveTolerance * 1.05);
                    var ids = new JArray();
                    int skippedShort = 0;
                    int skippedFailed = 0;
                    int skippedByLimit = Math.Max(0, lines.Count - maxGrids);
                    foreach (var ln in lines.Take(maxGrids))
                    {
                        var p0 = new XYZ(ln.GetEndPoint(0).X, ln.GetEndPoint(0).Y, 0);
                        var p1 = new XYZ(ln.GetEndPoint(1).X, ln.GetEndPoint(1).Y, 0);
                        if (p0.DistanceTo(p1) < shortTol)
                        {
                            skippedShort++;
                            continue;
                        }
                        try
                        {
                            var g = Grid.Create(d, Line.CreateBound(p0, p1));
                            if (!dryRun) ids.Add(IdCompat.ToInt64(g.Id));
                            else ids.Add(0);
                        }
                        catch
                        {
                            skippedFailed++;
                        }
                    }
                    var payload = new JObject
                    {
                        ["layer"] = layer,
                        ["source_lines"] = lines.Count,
                        ["created_grids"] = ids.Count,
                        ["max_grids"] = maxGrids,
                        ["truncated"] = skippedByLimit > 0,
                        ["skipped_by_limit"] = skippedByLimit,
                        ["skipped_short_segments"] = skippedShort,
                        ["skipped_failed_segments"] = skippedFailed
                    };
                    if (skippedByLimit > 0)
                        payload["warning"] = $"Truncated by max_grids={maxGrids}: {skippedByLimit} line(s) were not processed.";
                    if (!dryRun) payload["grid_ids"] = ids;
                    return payload;
                },
                (d, staged) => staged,
                $"Converted CAD layer '{layer}' into grids.");
        }
    }

    [ScioTool("scio_deliver_ifc", "cad_ifc", "Export the active Revit model to IFC (IFC2x3 or IFC4) with room/IfcSpace and Revit property-set (Comments/Mark) options, and verify file size, SHA-256, and IfcSpace/IfcPropertySet counts.", ReadOnly = false, OpenWorld = true)]
    public sealed class DeliverIfcCommand : IRevitCommand
    {
        [ScioParam("output_folder", "Target folder for the .ifc file.", Required = true)]
        public string OutputFolder { get; set; } = string.Empty;

        [ScioParam("file_name", "Output .ifc filename (e.g. PRJ-SCIO-ZZ-ZZ-M3-S-0001.ifc).", Required = true)]
        public string FileName { get; set; } = string.Empty;

        [ScioParam("ifc_version", "IFC schema version: IFC2x3 or IFC4 (default).", EnumValues = new[] { "IFC2x3", "IFC4" })]
        public string IfcVersion { get; set; } = "IFC4";

        [ScioParam("export_rooms", "Export Rooms as IfcSpace entities (default true). Sets SpaceBoundaryLevel >= 1 and ExportRoomsInView=true.")]
        public bool ExportRooms { get; set; } = true;

        [ScioParam("space_boundary_level", "IFC Space Boundary level: 0 (none), 1 (1st level, default when export_rooms=true), or 2 (2nd level).")]
        public int? SpaceBoundaryLevel { get; set; }

        [ScioParam("export_revit_property_sets", "Enable 'Export Revit property sets' (ExportInternalRevitPropertySets=true, default true) so Revit parameters such as Comments and Mark are written to the IFC.")]
        public bool ExportRevitPropertySets { get; set; } = true;

        [ScioParam("export_ifc_common_property_sets", "Export standard IFC common Pset_* property sets (default true).")]
        public bool ExportIfcCommonPropertySets { get; set; } = true;

        [ScioParam("export_base_quantities", "Export IFC base quantities (default true).")]
        public bool ExportBaseQuantities { get; set; } = true;

        [ScioParam("view_id", "Optional View ElementId to restrict export to elements visible in that view.")]
        public long? ViewId { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(DeliverIfcCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var args = request.Arguments ?? new JObject();
            string folder = args.Value<string>("output_folder") ?? string.Empty;
            string name = args.Value<string>("file_name") ?? string.Empty;
            string ver = (args.Value<string>("ifc_version") ?? "IFC4").Trim();
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide 'output_folder' and 'file_name'.");

            bool exportRooms = args.Value<bool?>("export_rooms") ?? true;
            int sbLevel = args.Value<int?>("space_boundary_level") ?? (exportRooms ? 1 : 0);
            sbLevel = Math.Max(0, Math.Min(2, sbLevel));
            if (exportRooms && sbLevel == 0) sbLevel = 1;

            bool exportRevitPsets = args.Value<bool?>("export_revit_property_sets") ?? true;
            bool exportCommonPsets = args.Value<bool?>("export_ifc_common_property_sets") ?? true;
            bool exportBaseQty = args.Value<bool?>("export_base_quantities") ?? true;

            if (!name.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase)) name += ".ifc";
            Directory.CreateDirectory(folder);

            var opts = new IFCExportOptions
            {
                FileVersion = string.Equals(ver, "IFC2x3", StringComparison.OrdinalIgnoreCase) ? IFCVersion.IFC2x3CV2 : IFCVersion.IFC4RV,
                ExportBaseQuantities = exportBaseQty,
                SpaceBoundaryLevel = sbLevel
            };

            opts.AddOption("ExportRoomsInView", exportRooms ? "true" : "false");
            opts.AddOption("ExportInternalRevitPropertySets", exportRevitPsets ? "true" : "false");
            opts.AddOption("ExportIFCCommonPropertySets", exportCommonPsets ? "true" : "false");
            opts.AddOption("ExportSchedulesAsPsets", "false");

            if (args["view_id"] != null && args["view_id"]!.Type != JTokenType.Null)
            {
                var filterView = RevitElementKit.ResolveElement(doc, args["view_id"]) as View;
                if (filterView == null)
                    return ToolResponse.Refusal(request.RequestId, "view_not_found", "Provide a valid 'view_id'.");
                opts.FilterViewId = filterView.Id;
                opts.AddOption("VisibleElementsOfCurrentView", "true");
            }

            bool ok;
            using (var tx = new Transaction(doc, "SCIO: Export IFC"))
            {
                TransactionPipeline.AttachWarningCapture(tx);
                tx.Start();
                ok = doc.Export(folder, name, opts);
                tx.RollBack();
            }

            string fullPath = Path.Combine(folder, name);
            if (!ok || !File.Exists(fullPath))
                return ToolResponse.Failure(request.RequestId, "ifc_export_failed", $"Revit IFC export did not produce '{fullPath}'.");

            string sha256;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(fullPath))
                sha256 = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();

            int ifcSpaceCount = 0;
            int ifcPsetCount = 0;
            foreach (string line in File.ReadLines(fullPath))
            {
                if (line.IndexOf("= IFCSPACE(", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("=IFCSPACE(", StringComparison.OrdinalIgnoreCase) >= 0)
                    ifcSpaceCount++;
                else if (line.IndexOf("= IFCPROPERTYSET(", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         line.IndexOf("=IFCPROPERTYSET(", StringComparison.OrdinalIgnoreCase) >= 0)
                    ifcPsetCount++;
            }

            long sizeBytes = new FileInfo(fullPath).Length;
            var warnings = new JArray();
            if (exportRooms && ifcSpaceCount == 0)
                warnings.Add("export_rooms=true was requested, but 0 IfcSpace entities were found in the exported IFC (verify that placed Rooms exist and are visible/bounded).");

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["path"] = fullPath,
                ["ifc_version"] = ver,
                ["size_bytes"] = sizeBytes,
                ["sha256"] = sha256,
                ["export_rooms"] = exportRooms,
                ["space_boundary_level"] = sbLevel,
                ["export_revit_property_sets"] = exportRevitPsets,
                ["export_ifc_common_property_sets"] = exportCommonPsets,
                ["ifc_space_count"] = ifcSpaceCount,
                ["ifc_property_set_count"] = ifcPsetCount,
                ["warnings"] = warnings
            }, $"Delivered IFC '{name}' ({sizeBytes / 1024} KB, {ifcSpaceCount} IfcSpace, {ifcPsetCount} IfcPropertySet).");
        }
    }

    [ScioTool("scio_ifc_audit", "cad_ifc", "Audit an .ifc STEP file on disk: schema header, spatial hierarchy, and entity counts.", ReadOnly = true)]
    public sealed class IfcAuditCommand : IRevitCommand
    {
        [ScioParam("ifc_path", "Absolute path to the .ifc file.", Required = true)]
        public string IfcPath { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(IfcAuditCommand));

        private static readonly Regex EntityRx = new Regex(@"^#\d+\s*=\s*(IFC[A-Z0-9_]+)\s*\(", RegexOptions.Compiled);

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string path = request.Arguments.Value<string>("ifc_path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ToolResponse.Refusal(request.RequestId, "file_not_found", $"IFC file not found: '{path}'.");

            string schema = "Unknown";
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int totalEntities = 0;

            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("FILE_SCHEMA", StringComparison.Ordinal))
                    schema = line.Trim();
                var m = EntityRx.Match(line);
                if (m.Success)
                {
                    totalEntities++;
                    string ent = m.Groups[1].Value;
                    counts[ent] = counts.TryGetValue(ent, out int c) ? c + 1 : 1;
                }
            }

            var top = new JArray(counts.OrderByDescending(k => k.Value).Take(40)
                .Select(k => new JObject { ["entity"] = k.Key, ["count"] = k.Value }));

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["path"] = path,
                ["size_bytes"] = new FileInfo(path).Length,
                ["schema"] = schema,
                ["total_entities"] = totalEntities,
                ["has_project"] = counts.ContainsKey("IFCPROJECT"),
                ["has_site"] = counts.ContainsKey("IFCSITE"),
                ["has_building"] = counts.ContainsKey("IFCBUILDING"),
                ["top_entities"] = top
            }, $"Audited IFC '{Path.GetFileName(path)}': {totalEntities} entities ({schema}).");
        }
    }

    [ScioTool("scio_ids_check", "cad_ifc", "Validate model elements against declarative IDS-style requirements ({ category, required_parameters, regex? }).", ReadOnly = true)]
    public sealed class IdsCheckCommand : IRevitCommand
    {
        [ScioParam("specifications", "Array of IDS specs: { name, category, parameter, pattern? }.", Required = true)]
        public JArray Specifications { get; set; } = new JArray();

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(IdsCheckCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var specs = request.Arguments["specifications"] as JArray;
            if (specs == null || specs.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_specs", "Provide non-empty 'specifications'.");

            var results = new JArray();
            int totalPass = 0, totalFail = 0;

            foreach (var sp in specs.OfType<JObject>())
            {
                string specName = sp.Value<string>("name") ?? "Spec";
                var cat = RevitElementKit.ResolveDocumentCategory(doc, sp.Value<string>("category"));
                string paramName = sp.Value<string>("parameter") ?? string.Empty;
                string? pattern = sp.Value<string>("pattern");
                Regex? rx = string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase);

                int pass = 0, fail = 0;
                var failedIds = new JArray();
                if (cat != null)
                {
                    foreach (var el in new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType())
                    {
                        string val = RevitElementKit.ReadParameterDisplay(RevitElementKit.FindParameter(el, paramName, includeType: true)).Trim();
                        bool ok = !string.IsNullOrEmpty(val) && (rx == null || rx.IsMatch(val));
                        if (ok) pass++;
                        else { fail++; if (failedIds.Count < 50) failedIds.Add(IdCompat.ToInt64(el.Id)); }
                    }
                }
                totalPass += pass;
                totalFail += fail;
                results.Add(new JObject
                {
                    ["name"] = specName,
                    ["category"] = cat?.Name ?? sp.Value<string>("category"),
                    ["parameter"] = paramName,
                    ["passed"] = pass,
                    ["failed"] = fail,
                    ["compliant"] = fail == 0,
                    ["sample_failed_ids"] = failedIds
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["all_compliant"] = totalFail == 0,
                ["total_passed"] = totalPass,
                ["total_failed"] = totalFail,
                ["specifications"] = results
            }, $"IDS check: {totalPass} passed, {totalFail} failed across {results.Count} specification(s).");
        }
    }

    [ScioTool("scio_georeference", "cad_ifc", "Inspect Project Base Point, Survey Point, Shared Site ProjectPosition (N/S, E/W, Elevation, True North angle), and SiteLocation.", ReadOnly = true)]
    public sealed class GeoreferenceCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(GeoreferenceCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var site = doc.SiteLocation;
            var pos = doc.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero);
            var pbp = BasePoint.GetProjectBasePoint(doc);
            var sp = BasePoint.GetSurveyPoint(doc);

            var data = new JObject
            {
                ["place_name"] = site?.PlaceName ?? string.Empty,
                ["latitude_deg"] = site == null ? 0 : Math.Round(UnitKit.RadToDeg(site.Latitude), 6),
                ["longitude_deg"] = site == null ? 0 : Math.Round(UnitKit.RadToDeg(site.Longitude), 6),
                ["time_zone"] = site?.TimeZone ?? 0,
                ["project_position_m"] = pos == null ? null : new JObject
                {
                    ["east_west_m"] = Math.Round(UnitKit.FeetToMm(pos.EastWest) / 1000.0, 3),
                    ["north_south_m"] = Math.Round(UnitKit.FeetToMm(pos.NorthSouth) / 1000.0, 3),
                    ["elevation_m"] = Math.Round(UnitKit.FeetToMm(pos.Elevation) / 1000.0, 3),
                    ["true_north_deg"] = Math.Round(UnitKit.RadToDeg(pos.Angle), 3)
                },
                ["project_base_point_m"] = pbp == null ? null : new JArray(
                    Math.Round(UnitKit.FeetToMm(pbp.Position.X) / 1000.0, 3),
                    Math.Round(UnitKit.FeetToMm(pbp.Position.Y) / 1000.0, 3),
                    Math.Round(UnitKit.FeetToMm(pbp.Position.Z) / 1000.0, 3)),
                ["survey_point_m"] = sp == null ? null : new JArray(
                    Math.Round(UnitKit.FeetToMm(sp.Position.X) / 1000.0, 3),
                    Math.Round(UnitKit.FeetToMm(sp.Position.Y) / 1000.0, 3),
                    Math.Round(UnitKit.FeetToMm(sp.Position.Z) / 1000.0, 3))
            };
            return ToolResponse.Success(request.RequestId, data, $"Georeference read for '{doc.Title}'.");
        }
    }
}
