using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CadIfc
{
    [ScioTool("scio_scan_to_bim", "cad_ifc",
        "Read-only Scan-to-BIM extraction on linked .rcp/.rcs point clouds: inspect_clouds, detect_levels (Z-histogram -> levels), slice_plan (horizontal Z-band -> 2D wall centerlines), and fit_primitive (3D box -> plane, column, or cylinder with USIBD LOA).",
        ReadOnly = true)]
    public sealed class ScanToBimCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation to run: inspect_clouds (default), detect_levels, slice_plan, or fit_primitive.",
            EnumValues = new[] { "inspect_clouds", "detect_levels", "slice_plan", "fit_primitive" })]
        public string Operation { get; set; } = "inspect_clouds";

        [ScioParam("link_instance_id", "PointCloudInstance ElementId (optional when only one cloud is linked).")]
        public long? LinkInstanceId { get; set; }

        [ScioParam("z_mm", "slice_plan: horizontal cut elevation in mm.")]
        public double? ZMm { get; set; }

        [ScioParam("band_mm", "slice_plan: half-thickness of the Z band in mm (default 60).")]
        public double? BandMm { get; set; }

        [ScioParam("xy_min_mm", "slice_plan: optional 2D min crop [x, y] in mm.")]
        public double[]? XyMinMm { get; set; }

        [ScioParam("xy_max_mm", "slice_plan: optional 2D max crop [x, y] in mm.")]
        public double[]? XyMaxMm { get; set; }

        [ScioParam("box_min_mm", "detect_levels (optional) or fit_primitive (required): 3D box min [x, y, z] in mm.")]
        public double[]? BoxMinMm { get; set; }

        [ScioParam("box_max_mm", "detect_levels (optional) or fit_primitive (required): 3D box max [x, y, z] in mm.")]
        public double[]? BoxMaxMm { get; set; }

        [ScioParam("primitive_kind", "fit_primitive: plane (default), column, or cylinder.",
            EnumValues = new[] { "plane", "column", "cylinder" })]
        public string PrimitiveKind { get; set; } = "plane";

        [ScioParam("tolerance_mm", "fit_primitive: inlier tolerance in mm (default 15).")]
        public double? ToleranceMm { get; set; }

        [ScioParam("spacing_mm", "Sampling spacing in mm.")]
        public double? SpacingMm { get; set; }

        [ScioParam("max_points", "Maximum sampled points (500..150000).")]
        public int? MaxPoints { get; set; }

        [ScioParam("bin_size_mm", "detect_levels: Z-histogram bin size in mm (default 20).")]
        public double? BinSizeMm { get; set; }

        [ScioParam("min_separation_mm", "detect_levels: minimum vertical separation between levels in mm (default 1800).")]
        public double? MinSeparationMm { get; set; }

        [ScioParam("min_share", "detect_levels: minimum point share per peak (default 0.015).")]
        public double? MinShare { get; set; }

        [ScioParam("max_levels", "detect_levels: maximum detected levels (default 30).")]
        public int? MaxLevels { get; set; }

        [ScioParam("voxel_mm", "slice_plan: 2D voxel grid size in mm (default 25).")]
        public double? VoxelMm { get; set; }

        [ScioParam("min_line_length_mm", "slice_plan: minimum line segment length in mm (default 400).")]
        public double? MinLineLengthMm { get; set; }

        [ScioParam("line_tolerance_mm", "slice_plan: RANSAC/line fit tolerance in mm (default 25).")]
        public double? LineToleranceMm { get; set; }

        [ScioParam("min_points_per_line", "slice_plan: minimum points per line segment (default 12).")]
        public int? MinPointsPerLine { get; set; }

        [ScioParam("min_wall_thickness_mm", "slice_plan: minimum double-face wall thickness in mm (default 60).")]
        public double? MinWallThicknessMm { get; set; }

        [ScioParam("max_wall_thickness_mm", "slice_plan: maximum double-face wall thickness in mm (default 500).")]
        public double? MaxWallThicknessMm { get; set; }

        [ScioParam("max_lines", "slice_plan: maximum extracted line segments (default 60).")]
        public int? MaxLines { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ScanToBimCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var args = request.Arguments ?? new JObject();
            string op = (args.Value<string>("operation") ?? "inspect_clouds").Trim().ToLowerInvariant();

            switch (op)
            {
                case "inspect_clouds": return InspectClouds(doc, request.RequestId);
                case "detect_levels": return DetectLevels(doc, request.RequestId, args);
                case "slice_plan": return SlicePlan(doc, request.RequestId, args);
                case "fit_primitive": return FitPrimitive(doc, request.RequestId, args);
                default:
                    return ToolResponse.Refusal(request.RequestId, "invalid_operation",
                        $"operation '{op}' is not supported by scio_scan_to_bim. Known: inspect_clouds, detect_levels, slice_plan, fit_primitive.");
            }
        }

        private static ToolResponse InspectClouds(Document doc, string requestId)
        {
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();

            var rows = new JArray();
            foreach (PointCloudInstance pc in instances)
            {
                var pcType = doc.GetElement(pc.GetTypeId()) as PointCloudType;
                string? path = null;
                try
                {
                    ModelPath? mp = pcType?.GetPath();
                    if (mp != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                }
                catch { path = null; }

                BoundingBoxXYZ? bb = null;
                try { bb = pc.get_BoundingBox(null); } catch { bb = null; }

                Transform tr = Transform.Identity;
                try { tr = pc.GetTotalTransform(); } catch { tr = Transform.Identity; }

                var scans = new JArray();
                try
                {
                    IList<string>? scanNames = pc.GetScans();
                    if (scanNames != null)
                        foreach (string s in scanNames.Take(200)) scans.Add(s);
                }
                catch { }

                var regions = new JArray();
                try
                {
                    IList<string>? regionNames = pc.GetRegions();
                    if (regionNames != null)
                        foreach (string r in regionNames.Take(200)) regions.Add(r);
                }
                catch { }

                rows.Add(new JObject
                {
                    ["link_instance_id"] = IdCompat.ToInt64(pc.Id),
                    ["link_type_id"] = IdCompat.ToInt64(pc.GetTypeId()),
                    ["name"] = pc.Name,
                    ["path"] = path,
                    ["engine"] = Safe(() => pcType?.EngineIdentifier),
                    ["found_status"] = Safe(() => pcType?.FoundStatus.ToString()),
                    ["pinned"] = Safe(() => pc.Pinned),
                    ["origin_mm"] = PtMm(tr.Origin),
                    ["is_identity_transform"] = tr.IsIdentity,
                    ["bounding_box_mm"] = bb == null ? null : new JObject
                    {
                        ["min"] = PtMm(bb.Min),
                        ["max"] = PtMm(bb.Max)
                    },
                    ["scans_count"] = scans.Count,
                    ["scans"] = scans,
                    ["regions_count"] = regions.Count,
                    ["regions"] = regions
                });
            }

            var result = new JObject
            {
                ["operation"] = "inspect_clouds",
                ["document"] = doc.Title,
                ["count"] = rows.Count,
                ["point_clouds"] = rows
            };
            return ToolResponse.Success(requestId, result, $"Inspected {rows.Count} linked point cloud(s).");
        }

        private static ToolResponse DetectLevels(Document doc, string requestId, JObject args)
        {
            if (!TryResolveCloud(doc, args, out PointCloudInstance? pc, out string? err) || pc == null)
                return ToolResponse.Refusal(requestId, "cloud_unavailable", err ?? "Point cloud not found.");

            BoundingBoxXYZ? bb = null;
            try { bb = pc.get_BoundingBox(null); } catch { bb = null; }
            if (bb == null)
                return ToolResponse.Failure(requestId, "no_bounding_box", $"Point cloud {IdCompat.ToInt64(pc.Id)} has no readable bounding box.");

            XYZ minFt = ReadPointMmOrDefault(args["box_min_mm"] as JArray, bb.Min);
            XYZ maxFt = ReadPointMmOrDefault(args["box_max_mm"] as JArray, bb.Max);
            if (!ValidBox(minFt, maxFt, out string? boxErr))
                return ToolResponse.Refusal(requestId, "invalid_box", boxErr!);

            double spacingMm = Clamp(args.Value<double?>("spacing_mm") ?? 80.0, 10.0, 1000.0);
            int maxPoints = Math.Min(ScanToBimRules.MaxLevelPoints, Math.Max(1000, args.Value<int?>("max_points") ?? 80000));
            if (!SampleBoxPoints(pc, minFt, maxFt, spacingMm, maxPoints, out List<XYZ> pts, out string frame, out string? sampleErr))
                return ToolResponse.Failure(requestId, "sampling_failed", sampleErr!);

            var zMm = new List<double>(pts.Count);
            foreach (XYZ p in pts) zMm.Add(p.Z * 304.8);

            double binSizeMm = args.Value<double?>("bin_size_mm") ?? 20.0;
            double minSepMm = args.Value<double?>("min_separation_mm") ?? 1800.0;
            double minShare = args.Value<double?>("min_share") ?? 0.015;
            int maxLevels = args.Value<int?>("max_levels") ?? 30;

            JObject result = ScanToBimRules.DetectLevelsFromZ(zMm, binSizeMm, minSepMm, minShare, maxLevels);
            result["operation"] = "detect_levels";
            result["point_cloud_instance_id"] = IdCompat.ToInt64(pc.Id);
            result["point_frame"] = frame;
            result["spacing_mm"] = spacingMm;
            result["box_min_mm"] = PtMm(minFt);
            result["box_max_mm"] = PtMm(maxFt);
            return ToolResponse.Success(requestId, result, $"Detected {result.Value<int>("levels_detected")} level(s) from {pts.Count} sampled points.");
        }

        private static ToolResponse SlicePlan(Document doc, string requestId, JObject args)
        {
            if (!TryResolveCloud(doc, args, out PointCloudInstance? pc, out string? err) || pc == null)
                return ToolResponse.Refusal(requestId, "cloud_unavailable", err ?? "Point cloud not found.");

            double? zMmOpt = args.Value<double?>("z_mm");
            if (!zMmOpt.HasValue)
                return ToolResponse.Refusal(requestId, "missing_argument", "slice_plan requires z_mm (elevation of the horizontal cut in millimeters).");

            double zMm = zMmOpt.Value;
            double bandMm = args.Value<double?>("band_mm") ?? 60.0;
            if (bandMm <= 0 || bandMm > 2000)
                return ToolResponse.Refusal(requestId, "invalid_argument", "band_mm must be > 0 and <= 2000.");

            BoundingBoxXYZ? bb = null;
            try { bb = pc.get_BoundingBox(null); } catch { bb = null; }
            if (bb == null)
                return ToolResponse.Failure(requestId, "no_bounding_box", $"Point cloud {IdCompat.ToInt64(pc.Id)} has no readable bounding box.");

            double xMin = bb.Min.X, yMin = bb.Min.Y, xMax = bb.Max.X, yMax = bb.Max.Y;
            if (args["xy_min_mm"] is JArray xyMin && xyMin.Count >= 2)
            {
                xMin = xyMin[0].Value<double>() / 304.8;
                yMin = xyMin[1].Value<double>() / 304.8;
            }
            if (args["xy_max_mm"] is JArray xyMax && xyMax.Count >= 2)
            {
                xMax = xyMax[0].Value<double>() / 304.8;
                yMax = xyMax[1].Value<double>() / 304.8;
            }

            XYZ minFt = new XYZ(xMin, yMin, (zMm - bandMm) / 304.8);
            XYZ maxFt = new XYZ(xMax, yMax, (zMm + bandMm) / 304.8);
            if (!ValidBox(minFt, maxFt, out string? boxErr))
                return ToolResponse.Refusal(requestId, "invalid_box", boxErr!);

            double spacingMm = Clamp(args.Value<double?>("spacing_mm") ?? 25.0, 5.0, 500.0);
            int maxPoints = Math.Min(ScanToBimRules.MaxSlicePoints, Math.Max(1000, args.Value<int?>("max_points") ?? 80000));
            if (!SampleBoxPoints(pc, minFt, maxFt, spacingMm, maxPoints, out List<XYZ> pts, out string frame, out string? sampleErr))
                return ToolResponse.Failure(requestId, "sampling_failed", sampleErr!);

            var xyMm = new List<double[]>(pts.Count);
            foreach (XYZ p in pts) xyMm.Add(new[] { p.X * 304.8, p.Y * 304.8 });

            double voxelMm = args.Value<double?>("voxel_mm") ?? 25.0;
            double minLenMm = args.Value<double?>("min_line_length_mm") ?? 400.0;
            double lineTolMm = args.Value<double?>("line_tolerance_mm") ?? 25.0;
            int minPts = args.Value<int?>("min_points_per_line") ?? 12;
            int maxLines = args.Value<int?>("max_lines") ?? 60;
            double minWallMm = args.Value<double?>("min_wall_thickness_mm") ?? 60.0;
            double maxWallMm = args.Value<double?>("max_wall_thickness_mm") ?? 500.0;

            JObject result = ScanToBimRules.ExtractSliceWalls(
                xyMm, voxelMm, minLenMm, lineTolMm, minPts, maxLines, minWallMm, maxWallMm);
            result["operation"] = "slice_plan";
            result["point_cloud_instance_id"] = IdCompat.ToInt64(pc.Id);
            result["z_mm"] = zMm;
            result["band_mm"] = bandMm;
            result["point_frame"] = frame;
            result["spacing_mm"] = spacingMm;
            return ToolResponse.Success(requestId, result, $"Extracted slice at Z={zMm:F0} mm from {pts.Count} points.");
        }

        private static ToolResponse FitPrimitive(Document doc, string requestId, JObject args)
        {
            if (!TryResolveCloud(doc, args, out PointCloudInstance? pc, out string? err) || pc == null)
                return ToolResponse.Refusal(requestId, "cloud_unavailable", err ?? "Point cloud not found.");

            var minArr = args["box_min_mm"] as JArray;
            var maxArr = args["box_max_mm"] as JArray;
            if (minArr == null || minArr.Count < 3 || maxArr == null || maxArr.Count < 3)
                return ToolResponse.Refusal(requestId, "missing_argument", "fit_primitive requires box_min_mm [x,y,z] and box_max_mm [x,y,z] in millimeters.");

            XYZ minFt = new XYZ(minArr[0].Value<double>() / 304.8, minArr[1].Value<double>() / 304.8, minArr[2].Value<double>() / 304.8);
            XYZ maxFt = new XYZ(maxArr[0].Value<double>() / 304.8, maxArr[1].Value<double>() / 304.8, maxArr[2].Value<double>() / 304.8);
            if (!ValidBox(minFt, maxFt, out string? boxErr))
                return ToolResponse.Refusal(requestId, "invalid_box", boxErr!);

            string kind = (args.Value<string>("primitive_kind") ?? "plane").Trim().ToLowerInvariant();
            double tolMm = args.Value<double?>("tolerance_mm") ?? 15.0;
            double spacingMm = Clamp(args.Value<double?>("spacing_mm") ?? 15.0, 2.0, 200.0);
            int maxPoints = Math.Min(ScanToBimRules.MaxPrimitivePoints, Math.Max(500, args.Value<int?>("max_points") ?? 25000));

            if (!SampleBoxPoints(pc, minFt, maxFt, spacingMm, maxPoints, out List<XYZ> pts, out string frame, out string? sampleErr))
                return ToolResponse.Failure(requestId, "sampling_failed", sampleErr!);

            var xyzMm = new List<double[]>(pts.Count);
            foreach (XYZ p in pts) xyzMm.Add(new[] { p.X * 304.8, p.Y * 304.8, p.Z * 304.8 });

            JObject result;
            try { result = ScanToBimRules.FitPrimitive(xyzMm, kind, tolMm); }
            catch (ArgumentException ex) { return ToolResponse.Refusal(requestId, "invalid_argument", ex.Message); }

            result["operation"] = "fit_primitive";
            result["point_cloud_instance_id"] = IdCompat.ToInt64(pc.Id);
            result["point_frame"] = frame;
            result["spacing_mm"] = spacingMm;
            result["box_min_mm"] = PtMm(minFt);
            result["box_max_mm"] = PtMm(maxFt);
            return ToolResponse.Success(requestId, result, $"Fitted {kind} primitive ({result.Value<string>("loa") ?? "n/a"}).");
        }

        private static bool TryResolveCloud(Document doc, JObject args, out PointCloudInstance? pc, out string? error)
        {
            pc = null;
            error = null;
            long? pcIdOpt = args.Value<long?>("link_instance_id");
            var all = new FilteredElementCollector(doc).OfClass(typeof(PointCloudInstance)).Cast<PointCloudInstance>().ToList();

            if (!pcIdOpt.HasValue)
            {
                if (all.Count == 1)
                {
                    pc = all[0];
                }
                else if (all.Count == 0)
                {
                    error = "No PointCloudInstance (.rcp/.rcs) is linked in the active document.";
                    return false;
                }
                else
                {
                    error = $"Multiple PointCloudInstances exist ({string.Join(", ", all.Select(x => IdCompat.ToInt64(x.Id)))}). Specify link_instance_id explicitly.";
                    return false;
                }
            }
            else
            {
                long id = pcIdOpt.Value;
                pc = doc.GetElement(IdCompat.FromInt64(id)) as PointCloudInstance;
                if (pc == null)
                {
                    string avail = all.Count == 0 ? "(none)" : string.Join(", ", all.Select(x => IdCompat.ToInt64(x.Id)));
                    error = $"link_instance_id {id} is not a PointCloudInstance. Available: {avail}.";
                    return false;
                }
            }

            PointCloudInstance resolved = pc;
            string? found = Safe(() => (doc.GetElement(resolved.GetTypeId()) as PointCloudType)?.FoundStatus.ToString());
            if (found == "NotFound")
            {
                error = $"cloud_not_found: point cloud {IdCompat.ToInt64(resolved.Id)} has FoundStatus=NotFound. Reload or repath the .rcp/.rcs first.";
                return false;
            }
            return true;
        }

        private static bool SampleBoxPoints(
            PointCloudInstance pc, XYZ minFt, XYZ maxFt, double spacingMm, int maxPoints,
            out List<XYZ> points, out string frame, out string? error)
        {
            points = new List<XYZ>();
            frame = "identity";
            error = null;

            var planes = new List<Plane>
            {
                Plane.CreateByNormalAndOrigin(XYZ.BasisX, minFt),
                Plane.CreateByNormalAndOrigin(XYZ.BasisX.Negate(), maxFt),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY, minFt),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY.Negate(), maxFt),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, minFt),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ.Negate(), maxFt)
            };

            List<XYZ> raw;
            try
            {
                PointCloudFilter filter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
                PointCollection col = pc.GetPoints(filter, spacingMm / 304.8, maxPoints);
                raw = new List<XYZ>();
                foreach (CloudPoint cp in col) raw.Add(new XYZ(cp.X, cp.Y, cp.Z));
            }
            catch (Exception ex)
            {
                error = $"Failed to sample points from point cloud {IdCompat.ToInt64(pc.Id)}: {ex.Message}";
                return false;
            }

            Transform tr = Transform.Identity;
            try { tr = pc.GetTotalTransform(); } catch { tr = Transform.Identity; }

            if (tr.IsIdentity || raw.Count == 0)
            {
                points = raw;
                frame = "identity";
                return true;
            }

            List<XYZ> moved = raw.Select(p => tr.OfPoint(p)).ToList();
            int inRaw = raw.Count(p => InsideBox(p, minFt, maxFt));
            int inMoved = moved.Count(p => InsideBox(p, minFt, maxFt));
            if (inMoved > inRaw)
            {
                points = moved;
                frame = "instance_transform";
            }
            else
            {
                points = raw;
                frame = "raw";
            }
            return true;
        }

        private static bool InsideBox(XYZ p, XYZ minFt, XYZ maxFt)
        {
            const double eps = 1e-3;
            return p.X >= minFt.X - eps && p.X <= maxFt.X + eps &&
                   p.Y >= minFt.Y - eps && p.Y <= maxFt.Y + eps &&
                   p.Z >= minFt.Z - eps && p.Z <= maxFt.Z + eps;
        }

        private static XYZ ReadPointMmOrDefault(JArray? arr, XYZ fallback)
        {
            if (arr == null || arr.Count < 3) return fallback;
            return new XYZ(arr[0].Value<double>() / 304.8, arr[1].Value<double>() / 304.8, arr[2].Value<double>() / 304.8);
        }

        private static bool ValidBox(XYZ minFt, XYZ maxFt, out string? err)
        {
            err = null;
            if (maxFt.X <= minFt.X || maxFt.Y <= minFt.Y || maxFt.Z <= minFt.Z)
            {
                err = "Invalid 3D sampling box: box_max_mm must be strictly greater than box_min_mm on X, Y and Z.";
                return false;
            }
            return true;
        }

        private static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);

        private static JArray PtMm(XYZ p)
            => new JArray(Math.Round(p.X * 304.8, 1), Math.Round(p.Y * 304.8, 1), Math.Round(p.Z * 304.8, 1));

        private static T? Safe<T>(Func<T> fn) { try { return fn(); } catch { return default; } }
    }
}
