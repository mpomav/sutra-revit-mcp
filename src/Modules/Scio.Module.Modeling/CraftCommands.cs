using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Modeling
{
    [ScioTool("scio_join_geometry", "modeling", "Join, unjoin, or switch join order between element pairs with dry_run=true default and post-commit verification.", ReadOnly = false)]
    public sealed class JoinGeometryCommand : IRevitCommand
    {
        [ScioParam("operation", "One of: join, unjoin, switch_order (default join).")]
        public string Operation { get; set; } = "join";

        [ScioParam("pairs", "Array of { first_id, second_id } pairs.", Required = true)]
        public JArray? Pairs { get; set; }

        [ScioParam("dry_run", "Preview without modifying the model (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(JoinGeometryCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            Document doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "join").Trim().ToLowerInvariant();
            if (op != "join" && op != "unjoin" && op != "switch_order")
                return ToolResponse.Refusal(request.RequestId, "invalid_operation", "operation must be join, unjoin, or switch_order.");

            bool dryRun = request.Arguments["dry_run"] == null || request.Arguments.Value<bool>("dry_run");
            if (request.Arguments["pairs"] is not JArray pairArr || pairArr.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_pairs", "Provide non-empty 'pairs' array of { first_id, second_id }.");

            var pairs = new List<(long A, long B, bool Joined)>();
            foreach (JToken t in pairArr)
            {
                if (t is not JObject obj) return ToolResponse.Refusal(request.RequestId, "invalid_pair", "Each pair must be an object.");
                long? a = obj.Value<long?>("first_id");
                long? b = obj.Value<long?>("second_id");
                if (a == null || b == null || a == b) return ToolResponse.Refusal(request.RequestId, "invalid_pair", "Distinct first_id and second_id required.");
                Element? ea = doc.GetElement(IdCompat.FromInt64(a.Value));
                Element? eb = doc.GetElement(IdCompat.FromInt64(b.Value));
                if (ea == null || eb == null) return ToolResponse.Refusal(request.RequestId, "not_found", $"Missing element in pair ({a}, {b}).");
                bool j = false;
                try { j = JoinGeometryUtils.AreElementsJoined(doc, ea, eb); } catch { }
                pairs.Add((a.Value, b.Value, j));
            }

            if (dryRun)
            {
                var dryData = new JObject
                {
                    ["dry_run"] = true,
                    ["operation"] = op,
                    ["pairs_total"] = pairs.Count,
                    ["pairs"] = new JArray(pairs.Select(p => new JObject { ["first_id"] = p.A, ["second_id"] = p.B, ["already_joined"] = p.Joined }))
                };
                return ToolResponse.Success(request.RequestId, dryData, $"Dry-run: {pairs.Count} pair(s) ready for {op}.", VerificationStatus.ReadOnly);
            }

            int applied = 0, verified = 0;
            using (var tx = new Transaction(doc, $"Sutra: join_geometry ({op})"))
            {
                TransactionPipeline.AttachWarningCapture(tx);
                tx.Start();
                foreach (var (a, b, _) in pairs)
                {
                    Element? ea = doc.GetElement(IdCompat.FromInt64(a));
                    Element? eb = doc.GetElement(IdCompat.FromInt64(b));
                    if (ea == null || eb == null) continue;
                    bool isJ = JoinGeometryUtils.AreElementsJoined(doc, ea, eb);
                    if (op == "join" && !isJ) { JoinGeometryUtils.JoinGeometry(doc, ea, eb); applied++; }
                    else if (op == "unjoin" && isJ) { JoinGeometryUtils.UnjoinGeometry(doc, ea, eb); applied++; }
                    else if (op == "switch_order") { if (!isJ) JoinGeometryUtils.JoinGeometry(doc, ea, eb); JoinGeometryUtils.SwitchJoinOrder(doc, ea, eb); applied++; }
                }
                doc.Regenerate();
                tx.Commit();
            }

            foreach (var (a, b, _) in pairs)
            {
                Element? ea = doc.GetElement(IdCompat.FromInt64(a));
                Element? eb = doc.GetElement(IdCompat.FromInt64(b));
                if (ea == null || eb == null) continue;
                bool after = JoinGeometryUtils.AreElementsJoined(doc, ea, eb);
                if ((op == "unjoin" && !after) || (op != "unjoin" && after)) verified++;
            }

            var res = new JObject { ["dry_run"] = false, ["operation"] = op, ["pairs_total"] = pairs.Count, ["applied"] = applied, ["verified"] = verified };
            return ToolResponse.Success(request.RequestId, res, $"Join geometry ({op}): {verified}/{pairs.Count} verified.", VerificationStatus.Verified, hostVerified: true);
        }
    }

    [ScioTool("scio_split_by_level", "modeling", "Split multi-story walls and columns at every intermediate Level they span, with dry_run=true default and post-commit verification.", ReadOnly = false, Destructive = true)]
    public sealed class SplitByLevelCommand : IRevitCommand
    {
        [ScioParam("element_ids", "Array of Wall or Column ElementIds to split.", Required = true)]
        public JArray? ElementIds { get; set; }

        [ScioParam("dry_run", "Preview segments without modifying the model (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SplitByLevelCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            Document doc = context.RequireActiveDocument(request.TargetDocument);
            bool dryRun = request.Arguments["dry_run"] == null || request.Arguments.Value<bool>("dry_run");
            if (request.Arguments["element_ids"] is not JArray idArr || idArr.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_ids", "Provide non-empty 'element_ids'.");

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            if (levels.Count < 2) return ToolResponse.Refusal(request.RequestId, "insufficient_levels", "Model requires at least 2 levels.");

            var plans = new List<(long SourceId, string Kind, List<(Level Base, Level Top)> Segs)>();
            foreach (JToken t in idArr)
            {
                long id = t.Value<long>();
                Element? e = doc.GetElement(IdCompat.FromInt64(id));
                if (e == null) continue;
                bool isWall = e is Wall w && w.Location is LocationCurve;
                bool isCol = e is FamilyInstance fi && fi.Category != null &&
                             (IdCompat.ToInt64(fi.Category.Id) == (long)BuiltInCategory.OST_StructuralColumns || IdCompat.ToInt64(fi.Category.Id) == (long)BuiltInCategory.OST_Columns) &&
                             fi.Location is LocationPoint;
                if (!isWall && !isCol) continue;

                BoundingBoxXYZ? bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                const double tol = 50.0 / 304.8;
                var spanned = levels.Where(l => l.Elevation >= bb.Min.Z - tol && l.Elevation <= bb.Max.Z + tol).ToList();
                if (spanned.Count < 3) continue;

                var segs = new List<(Level, Level)>();
                for (int i = 0; i < spanned.Count - 1; i++) segs.Add((spanned[i], spanned[i + 1]));
                plans.Add((id, isWall ? "wall" : "column", segs));
            }

            if (dryRun)
            {
                var dryData = new JObject
                {
                    ["dry_run"] = true,
                    ["elements_to_split"] = plans.Count,
                    ["total_new_segments"] = plans.Sum(p => p.Segs.Count),
                    ["plan"] = new JArray(plans.Select(p => new JObject
                    {
                        ["source_id"] = p.SourceId,
                        ["kind"] = p.Kind,
                        ["segments"] = new JArray(p.Segs.Select(s => new JObject { ["base"] = s.Base.Name, ["top"] = s.Top.Name }))
                    }))
                };
                return ToolResponse.Success(request.RequestId, dryData, $"Dry-run: {plans.Count} element(s) will split into {plans.Sum(p => p.Segs.Count)} segments.", VerificationStatus.ReadOnly);
            }

            var created = new List<long>();
            using (var tx = new Transaction(doc, "Sutra: split_by_level"))
            {
                TransactionPipeline.AttachWarningCapture(tx);
                tx.Start();
                foreach (var (srcId, _, segs) in plans)
                {
                    Element? src = doc.GetElement(IdCompat.FromInt64(srcId));
                    if (src == null) continue;
                    var segIds = new List<ElementId>();
                    if (src is Wall w && w.Location is LocationCurve lc)
                    {
                        foreach (var (bLvl, tLvl) in segs)
                        {
                            Wall nw = Wall.Create(doc, lc.Curve, w.WallType.Id, bLvl.Id, tLvl.Elevation - bLvl.Elevation, 0.0, w.Flipped, w.StructuralUsage != StructuralWallUsage.NonBearing);
                            Parameter? tp = nw.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                            if (tp != null && !tp.IsReadOnly) tp.Set(tLvl.Id);
                            segIds.Add(nw.Id);
                        }
                    }
                    else if (src is FamilyInstance col && col.Location is LocationPoint lp)
                    {
                        if (!col.Symbol.IsActive) col.Symbol.Activate();
                        foreach (var (bLvl, tLvl) in segs)
                        {
                            FamilyInstance nc = doc.Create.NewFamilyInstance(new XYZ(lp.Point.X, lp.Point.Y, bLvl.Elevation), col.Symbol, bLvl, col.StructuralType);
                            Parameter? tp = nc.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                            if (tp != null && !tp.IsReadOnly) tp.Set(tLvl.Id);
                            segIds.Add(nc.Id);
                        }
                    }
                    if (segIds.Count == segs.Count)
                    {
                        doc.Delete(src.Id);
                        created.AddRange(segIds.Select(IdCompat.ToInt64));
                    }
                }
                doc.Regenerate();
                tx.Commit();
            }

            int verified = created.Count(id => doc.GetElement(IdCompat.FromInt64(id)) != null);
            var res = new JObject { ["dry_run"] = false, ["created_segments"] = created.Count, ["verified_segments"] = verified, ["created_ids"] = new JArray(created) };
            return ToolResponse.Success(request.RequestId, res, $"Split by level: {verified}/{created.Count} segments verified.", VerificationStatus.Verified, hostVerified: true);
        }
    }

    [ScioTool("scio_wall_finish", "modeling", "Create flush architectural wall finish layers ('finish_layer') or wall sweeps/reveals ('wall_sweep') with dry_run=true default.", ReadOnly = false)]
    public sealed class WallFinishCommand : IRevitCommand
    {
        [ScioParam("operation", "One of: finish_layer, wall_sweep (default finish_layer).")]
        public string Operation { get; set; } = "finish_layer";

        [ScioParam("wall_ids", "Array of host Wall ElementIds.", Required = true)]
        public JArray? WallIds { get; set; }

        [ScioParam("side", "interior or exterior (default interior).")]
        public string Side { get; set; } = "interior";

        [ScioParam("finish_wall_type", "Exact WallType name (required for finish_layer).")]
        public string? FinishWallType { get; set; }

        [ScioParam("elevation_mm", "Base elevation in mm for wall_sweep (default 0).")]
        public double ElevationMm { get; set; }

        [ScioParam("dry_run", "Preview without modifying the model (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(WallFinishCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            Document doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "finish_layer").Trim().ToLowerInvariant();
            string side = (request.Arguments.Value<string>("side") ?? "interior").Trim().ToLowerInvariant();
            bool dryRun = request.Arguments["dry_run"] == null || request.Arguments.Value<bool>("dry_run");
            double elevMm = request.Arguments.Value<double?>("elevation_mm") ?? 0.0;

            if (request.Arguments["wall_ids"] is not JArray idArr || idArr.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_wall_ids", "Provide non-empty 'wall_ids'.");

            var walls = idArr.Select(t => doc.GetElement(IdCompat.FromInt64(t.Value<long>())) as Wall)
                .Where(w => w != null && w.Location is LocationCurve).Cast<Wall>().ToList();
            if (walls.Count == 0) return ToolResponse.Refusal(request.RequestId, "no_walls", "No valid walls found.");

            WallType? finishType = null;
            if (op == "finish_layer")
            {
                string? typeName = request.Arguments.Value<string>("finish_wall_type");
                finishType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                    .FirstOrDefault(wt => string.Equals(wt.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (finishType == null) return ToolResponse.Refusal(request.RequestId, "type_not_found", $"WallType '{typeName}' not found.");
            }

            if (dryRun)
            {
                var dryData = new JObject { ["dry_run"] = true, ["operation"] = op, ["side"] = side, ["target_walls"] = walls.Count };
                return ToolResponse.Success(request.RequestId, dryData, $"Dry-run: {op} ({side}) on {walls.Count} wall(s).", VerificationStatus.ReadOnly);
            }

            var created = new List<long>();
            using (var tx = new Transaction(doc, $"Sutra: wall_finish ({op})"))
            {
                TransactionPipeline.AttachWarningCapture(tx);
                tx.Start();
                foreach (Wall host in walls)
                {
                    if (op == "finish_layer" && finishType != null)
                    {
                        Curve curve = ((LocationCurve)host.Location).Curve;
                        double sign = side == "exterior" ? 1.0 : -1.0;
                        double offset = (host.Width * 0.5) + (finishType.Width * 0.5);
                        Curve shifted = curve.CreateTransformed(Transform.CreateTranslation(host.Orientation * (sign * offset)));
                        Parameter? hp = host.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                        double hFt = hp != null && hp.AsDouble() > 0.1 ? hp.AsDouble() : 10.0;
                        Wall fw = Wall.Create(doc, shifted, finishType.Id, host.LevelId, hFt, 0.0, false, false);
                        try { JoinGeometryUtils.JoinGeometry(doc, host, fw); } catch { }
                        created.Add(IdCompat.ToInt64(fw.Id));
                    }
                    else
                    {
                        var info = new WallSweepInfo(WallSweepType.Sweep, false)
                        {
                            Distance = elevMm / 304.8,
                            DistanceMeasuredFrom = DistanceMeasuredFrom.Base,
                            WallSide = side == "exterior" ? WallSide.Exterior : WallSide.Interior,
                            IsCutByInserts = true
                        };
                        WallSweep? sw = WallSweep.Create(host, ElementId.InvalidElementId, info);
                        if (sw != null) created.Add(IdCompat.ToInt64(sw.Id));
                    }
                }
                doc.Regenerate();
                tx.Commit();
            }

            int verified = created.Count(id => doc.GetElement(IdCompat.FromInt64(id)) != null);
            var res = new JObject { ["dry_run"] = false, ["operation"] = op, ["created_count"] = created.Count, ["verified_count"] = verified, ["created_ids"] = new JArray(created) };
            return ToolResponse.Success(request.RequestId, res, $"Wall finish ({op}): {verified}/{created.Count} verified.", VerificationStatus.Verified, hostVerified: true);
        }
    }
}
