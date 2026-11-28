using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Modeling
{
    [ScioTool("scio_split_multilayer_walls", "modeling", "Inspect or split compound multilayer walls into independent single-layer walls per compound layer (with dry-run preview).", ReadOnly = false)]
    public sealed class SplitMultilayerWallsCommand : IRevitCommand
    {
        [ScioParam("wall_ids", "Target Wall ElementIds to split.", Required = true)]
        public List<long> WallIds { get; set; } = new List<long>();

        [ScioParam("delete_original", "Delete original compound walls after splitting (default false).")]
        public bool DeleteOriginal { get; set; } = false;

        [ScioParam("dry_run", "Preview layers without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SplitMultilayerWallsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var walls = RevitElementKit.ResolveElements(doc, request.Arguments["wall_ids"] as JArray).OfType<Wall>().ToList();
            if (walls.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_walls", "No valid Wall elements resolved from 'wall_ids'.");

            bool deleteOriginal = request.Arguments.Value<bool?>("delete_original") ?? false;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Split Multilayer Walls",
                dryRun,
                d =>
                {
                    var created = new JArray();
                    var existingTypes = new FilteredElementCollector(d).OfClass(typeof(WallType)).Cast<WallType>()
                        .Where(wt => wt.Kind == WallKind.Basic)
                        .ToDictionary(wt => wt.Name, wt => wt, StringComparer.OrdinalIgnoreCase);

                    foreach (var wall in walls)
                    {
                        var cs = wall.WallType.GetCompoundStructure();
                        if (cs == null) continue;
                        var layers = cs.GetLayers().Where(l => l.Width > 1e-4).ToList();
                        if (layers.Count < 2) continue;

                        if (wall.Location is not LocationCurve lc || lc.Curve == null) continue;
                        double totalWidth = layers.Sum(l => l.Width);
                        double cursor = -totalWidth * 0.5;
                        double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? UnitKit.MmToFeet(3000.0);
                        double baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0;
                        var baseLevelId = wall.LevelId;

                        for (int i = 0; i < layers.Count; i++)
                        {
                            var layer = layers[i];
                            double centerOffset = cursor + layer.Width * 0.5;
                            cursor += layer.Width;

                            string layerTypeName = $"{wall.WallType.Name}_L{i + 1}_{Math.Round(UnitKit.FeetToMm(layer.Width), 0)}mm";
                            if (!existingTypes.TryGetValue(layerTypeName, out var layerWallType))
                            {
                                layerWallType = wall.WallType.Duplicate(layerTypeName) as WallType
                                    ?? throw new InvalidOperationException($"Failed to duplicate WallType '{layerTypeName}'.");
                                var singleCs = CompoundStructure.CreateSingleLayerCompoundStructure(layer.Function, layer.Width, layer.MaterialId);
                                layerWallType.SetCompoundStructure(singleCs);
                                existingTypes[layerTypeName] = layerWallType;
                            }

                            var offsetCurve = Math.Abs(centerOffset) > 1e-5
                                ? lc.Curve.CreateOffset(centerOffset, XYZ.BasisZ)
                                : lc.Curve;
                            var nw = Wall.Create(d, offsetCurve, layerWallType.Id, baseLevelId, height, baseOffset, false, false);
                            created.Add(new JObject
                            {
                                ["source_wall_id"] = IdCompat.ToInt64(wall.Id),
                                ["new_wall_id"] = IdCompat.ToInt64(nw.Id),
                                ["layer_index"] = i,
                                ["wall_type"] = layerTypeName,
                                ["thickness_mm"] = Math.Round(UnitKit.FeetToMm(layer.Width), 1)
                            });
                        }

                        if (deleteOriginal) d.Delete(wall.Id);
                    }

                    return new JObject
                    {
                        ["source_walls"] = walls.Count,
                        ["created_layer_walls"] = created.Count,
                        ["deleted_originals"] = deleteOriginal,
                        ["items"] = created
                    };
                },
                (d, staged) => staged,
                $"Split {walls.Count} multilayer wall(s).");
        }
    }

    [ScioTool("scio_split_multilayer_slabs", "modeling", "Inspect or split compound multilayer floors/slabs into stacked single-layer floors (with dry-run preview).", ReadOnly = false)]
    public sealed class SplitMultilayerSlabsCommand : IRevitCommand
    {
        [ScioParam("floor_ids", "Target Floor ElementIds to split.", Required = true)]
        public List<long> FloorIds { get; set; } = new List<long>();

        [ScioParam("dry_run", "Preview layer breakdown without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SplitMultilayerSlabsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var floors = RevitElementKit.ResolveElements(doc, request.Arguments["floor_ids"] as JArray).OfType<Floor>().ToList();
            if (floors.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_floors", "No valid Floor elements resolved from 'floor_ids'.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Split Multilayer Slabs",
                dryRun,
                d =>
                {
                    var created = new JArray();
                    var skippedSingleLayer = new JArray();
                    var existingTypes = new FilteredElementCollector(d).OfClass(typeof(FloorType)).Cast<FloorType>()
                        .ToDictionary(ft => ft.Name, ft => ft, StringComparer.OrdinalIgnoreCase);

                    foreach (var fl in floors)
                    {
                        var cs = fl.FloorType.GetCompoundStructure();
                        if (cs == null)
                        {
                            skippedSingleLayer.Add(IdCompat.ToInt64(fl.Id));
                            continue;
                        }
                        var layers = cs.GetLayers().Where(l => l.Width > 1e-4).ToList();
                        if (layers.Count < 2)
                        {
                            skippedSingleLayer.Add(IdCompat.ToInt64(fl.Id));
                            continue;
                        }

                        var sketch = d.GetElement(fl.SketchId) as Sketch;
                        if (sketch == null) continue;
                        var loops = ExtractCurveLoops(sketch);
                        if (loops.Count == 0) continue;

                        double baseOff = fl.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
                        var zOffsets = ConstructionMathKit.ComputeSlabLayerTopOffsets(layers.Select(l => l.Width).ToList());

                        for (int i = 0; i < layers.Count; i++)
                        {
                            var layer = layers[i];
                            string typeName = $"{fl.FloorType.Name}_L{i + 1}_{Math.Round(UnitKit.FeetToMm(layer.Width), 0)}mm";
                            if (!existingTypes.TryGetValue(typeName, out var layerFloorType))
                            {
                                layerFloorType = fl.FloorType.Duplicate(typeName) as FloorType
                                    ?? throw new InvalidOperationException($"Failed to duplicate FloorType '{typeName}'.");
                                var singleCs = CompoundStructure.CreateSingleLayerCompoundStructure(layer.Function, layer.Width, layer.MaterialId);
                                singleCs.EndCap = EndCapCondition.NoEndCap;
                                layerFloorType.SetCompoundStructure(singleCs);
                                existingTypes[typeName] = layerFloorType;
                            }

                            var nf = Floor.Create(d, loops, layerFloorType.Id, fl.LevelId);
                            var hp = nf.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                            if (hp != null && !hp.IsReadOnly) hp.Set(baseOff + zOffsets[i]);

                            created.Add(new JObject
                            {
                                ["source_floor_id"] = IdCompat.ToInt64(fl.Id),
                                ["new_floor_id"] = IdCompat.ToInt64(nf.Id),
                                ["layer_index"] = i,
                                ["floor_type"] = typeName,
                                ["thickness_mm"] = Math.Round(UnitKit.FeetToMm(layer.Width), 1),
                                ["top_offset_mm"] = Math.Round(UnitKit.FeetToMm(baseOff + zOffsets[i]), 1)
                            });
                        }
                    }

                    var payload = new JObject
                    {
                        ["source_floors"] = floors.Count,
                        ["created_layer_floors"] = created.Count,
                        ["skipped_single_layer_count"] = skippedSingleLayer.Count,
                        ["items"] = created
                    };
                    if (skippedSingleLayer.Count > 0)
                    {
                        payload["skipped_single_layer_floor_ids"] = skippedSingleLayer;
                        payload["note"] = $"{skippedSingleLayer.Count} floor(s) had fewer than 2 compound layers and were skipped.";
                    }
                    return payload;
                },
                (d, staged) => staged,
                $"Split {floors.Count} multilayer slab(s).");
        }

        internal static List<CurveLoop> ExtractCurveLoops(Sketch sketch)
        {
            var loops = new List<CurveLoop>();
            foreach (CurveArray ca in sketch.Profile)
            {
                var cl = new CurveLoop();
                foreach (Curve c in ca) cl.Append(c);
                loops.Add(cl);
            }
            return loops;
        }
    }

    [ScioTool("scio_split_floor_loops", "modeling", "Split a multi-loop Floor element into separate Floor elements (one per closed loop) with dry-run preview.", ReadOnly = false)]
    public sealed class SplitFloorLoopsCommand : IRevitCommand
    {
        [ScioParam("floor_id", "Target Floor ElementId.", Required = true)]
        public long FloorId { get; set; }

        [ScioParam("delete_original", "Delete the original multi-loop floor (default false).")]
        public bool DeleteOriginal { get; set; } = false;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SplitFloorLoopsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var floor = RevitElementKit.ResolveElement(doc, request.Arguments["floor_id"]) as Floor;
            if (floor == null)
                return ToolResponse.Refusal(request.RequestId, "invalid_floor", "Valid 'floor_id' required.");

            var sketch = doc.GetElement(floor.SketchId) as Sketch;
            var loops = sketch != null ? SplitMultilayerSlabsCommand.ExtractCurveLoops(sketch) : new List<CurveLoop>();
            if (loops.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "single_loop", $"Floor {IdCompat.ToInt64(floor.Id)} has only {loops.Count} loop(s).");

            bool deleteOriginal = request.Arguments.Value<bool?>("delete_original") ?? false;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Split Floor Loops",
                dryRun,
                d =>
                {
                    var ids = new JArray();
                    double off = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
                    foreach (var loop in loops)
                    {
                        var nf = Floor.Create(d, new List<CurveLoop> { loop }, floor.FloorType.Id, floor.LevelId);
                        var hp = nf.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                        if (hp != null && !hp.IsReadOnly) hp.Set(off);
                        ids.Add(IdCompat.ToInt64(nf.Id));
                    }
                    if (deleteOriginal) d.Delete(floor.Id);
                    return new JObject { ["loop_count"] = loops.Count, ["created_floor_ids"] = ids, ["deleted_original"] = deleteOriginal };
                },
                (d, staged) => staged,
                $"Split floor {IdCompat.ToInt64(floor.Id)} into {loops.Count} loop floor(s).");
        }
    }

    [ScioTool("scio_copy_slab_elevations", "modeling", "Copy SlabShapeEditor vertex elevations from a source Floor to target Floor(s) by nearest XY match.", ReadOnly = false)]
    public sealed class CopySlabElevationsCommand : IRevitCommand
    {
        [ScioParam("source_floor_id", "Source Floor ElementId with slab shape vertices.", Required = true)]
        public long SourceFloorId { get; set; }

        [ScioParam("target_floor_ids", "Target Floor ElementIds to receive elevations.", Required = true)]
        public List<long> TargetFloorIds { get; set; } = new List<long>();

        [ScioParam("tolerance_mm", "Max XY matching distance in mm (default 200).")]
        public double ToleranceMm { get; set; } = 200.0;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CopySlabElevationsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var src = RevitElementKit.ResolveElement(doc, request.Arguments["source_floor_id"]) as Floor;
            var targets = RevitElementKit.ResolveElements(doc, request.Arguments["target_floor_ids"] as JArray).OfType<Floor>().ToList();
            if (src == null || targets.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Valid 'source_floor_id' and 'target_floor_ids' required.");

            var srcEditor = GetFloorShapeEditor(src);
            if (srcEditor == null || !srcEditor.IsEnabled)
                return ToolResponse.Refusal(request.RequestId, "no_slab_shape", "Source floor has no enabled SlabShapeEditor.");

            var srcVerts = srcEditor.SlabShapeVertices.Cast<SlabShapeVertex>().Select(v => v.Position).ToList();
            double tolFt = UnitKit.MmToFeet(Math.Max(1.0, request.Arguments.Value<double?>("tolerance_mm") ?? 200.0));
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Copy Slab Elevations",
                dryRun,
                d =>
                {
                    int modifiedVerts = 0;
                    foreach (var tgt in targets)
                    {
                        var ed = GetFloorShapeEditor(tgt);
                        if (ed == null) continue;
                        if (!ed.IsEnabled) ed.Enable();
                        foreach (SlabShapeVertex tv in ed.SlabShapeVertices)
                        {
                            XYZ? best = null;
                            double bestDist = tolFt;
                            foreach (var sv in srcVerts)
                            {
                                double dist = Math.Sqrt(Math.Pow(sv.X - tv.Position.X, 2) + Math.Pow(sv.Y - tv.Position.Y, 2));
                                if (dist <= bestDist) { bestDist = dist; best = sv; }
                            }
                            if (best != null)
                            {
                                ed.ModifySubElement(tv, best.Z - tv.Position.Z);
                                modifiedVerts++;
                            }
                        }
                    }
                    return new JObject { ["target_floors"] = targets.Count, ["modified_vertices"] = modifiedVerts };
                },
                (d, staged) => staged,
                $"Copied slab elevations to {targets.Count} floor(s).");
        }

        private static SlabShapeEditor? GetFloorShapeEditor(Floor floor)
        {
#if REVIT2024_OR_GREATER
            return floor.GetSlabShapeEditor();
#else
            return floor.SlabShapeEditor;
#endif
        }
    }

    [ScioTool("scio_rectangularize_walls", "modeling", "Snap nearly-orthogonal walls within max_angle_deg of 0/90 degrees to exact orthogonal alignment.", ReadOnly = false)]
    public sealed class RectangularizeWallsCommand : IRevitCommand
    {
        [ScioParam("wall_ids", "Optional Wall ElementIds (defaults to all walls in document, capped at 500).")]
        public List<long>? WallIds { get; set; }

        [ScioParam("max_angle_deg", "Maximum angular deviation in degrees to snap (default 3.0).")]
        public double MaxAngleDeg { get; set; } = 3.0;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RectangularizeWallsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var walls = request.Arguments["wall_ids"] is JArray arr && arr.Count > 0
                ? RevitElementKit.ResolveElements(doc, arr).OfType<Wall>().ToList()
                : new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Take(500).ToList();

            double maxDeg = Math.Max(0.1, Math.Min(15.0, request.Arguments.Value<double?>("max_angle_deg") ?? 3.0));
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Rectangularize Walls",
                dryRun,
                d =>
                {
                    var snapped = new JArray();
                    foreach (var w in walls)
                    {
                        if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                        var p0 = line.GetEndPoint(0);
                        var p1 = line.GetEndPoint(1);
                        var snapped2d = ConstructionMathKit.SnapEndpointOrthogonal(new Point2D(p0.X, p0.Y), new Point2D(p1.X, p1.Y), maxDeg);
                        if (snapped2d == null) continue;

                        var newEnd = new XYZ(snapped2d.X, snapped2d.Y, p1.Z);
                        if (p0.DistanceTo(newEnd) > 1e-4)
                        {
                            lc.Curve = Line.CreateBound(p0, newEnd);
                            snapped.Add(IdCompat.ToInt64(w.Id));
                        }
                    }
                    return new JObject { ["scanned_walls"] = walls.Count, ["snapped_count"] = snapped.Count, ["snapped_wall_ids"] = snapped };
                },
                (d, staged) => staged,
                $"Rectangularized walls within {maxDeg} deg.");
        }
    }

    [ScioTool("scio_wall_sweep", "modeling", "Read WallSweep/SlabEdge/Fascia/Gutter types or place a horizontal/vertical WallSweep on a wall.", ReadOnly = false)]
    public sealed class WallSweepCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation: read or place.", Required = true, EnumValues = new[] { "read", "place" })]
        public string Operation { get; set; } = "read";

        [ScioParam("wall_id", "Target Wall ElementId for place.")]
        public long? WallId { get; set; }

        [ScioParam("sweep_type_name", "Optional WallSweepType name for place.")]
        public string? SweepTypeName { get; set; }

        [ScioParam("distance_mm", "Distance from wall base in mm (default 100).")]
        public double DistanceMm { get; set; } = 100.0;

        [ScioParam("is_vertical", "Vertical sweep flag (default false = horizontal).")]
        public bool IsVertical { get; set; } = false;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(WallSweepCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "read").Trim().ToLowerInvariant();
            var sweepTypes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Cornices).WhereElementIsElementType().ToList();

            if (op == "read")
            {
                var sweeps = new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Cast<WallSweep>().Take(200)
                    .Select(s => new JObject { ["id"] = IdCompat.ToInt64(s.Id), ["name"] = s.Name }).ToList();
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["sweep_types"] = new JArray(sweepTypes.Select(t => new JObject { ["id"] = IdCompat.ToInt64(t.Id), ["name"] = t.Name })),
                    ["sweep_instances"] = new JArray(sweeps)
                }, $"Found {sweepTypes.Count} wall sweep type(s) and {sweeps.Count} instance(s).");
            }

            var wall = RevitElementKit.ResolveElement(doc, request.Arguments["wall_id"]) as Wall;
            if (wall == null)
                return ToolResponse.Refusal(request.RequestId, "invalid_wall", "Valid 'wall_id' required for place.");

            string? typeName = request.Arguments.Value<string>("sweep_type_name");
            var chosenType = sweepTypes.FirstOrDefault(t => string.IsNullOrWhiteSpace(typeName) || string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (chosenType == null)
                return ToolResponse.Refusal(request.RequestId, "no_sweep_type", "No matching WallSweep type loaded in document.");

            double distFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("distance_mm") ?? 100.0);
            bool isVert = request.Arguments.Value<bool?>("is_vertical") ?? false;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Place Wall Sweep",
                dryRun,
                d =>
                {
                    var info = new WallSweepInfo(WallSweepType.Sweep, isVert)
                    {
                        Distance = distFt,
                        DistanceMeasuredFrom = DistanceMeasuredFrom.Base,
                        WallSide = WallSide.Exterior
                    };
                    var ws = WallSweep.Create(wall, chosenType.Id, info);
                    return new JObject { ["sweep_id"] = IdCompat.ToInt64(ws.Id), ["wall_id"] = IdCompat.ToInt64(wall.Id), ["type_name"] = chosenType.Name };
                },
                (d, staged) => staged,
                $"Placed wall sweep '{chosenType.Name}' on wall {IdCompat.ToInt64(wall.Id)}.");
        }
    }

    [ScioTool("scio_room_boundaries", "modeling", "Inspect room boundary loops (mm), perimeter (m), area (m2), and bounding element IDs.", ReadOnly = true)]
    public sealed class RoomBoundariesCommand : IRevitCommand
    {
        [ScioParam("room_ids", "Optional Room ElementIds (defaults to all placed rooms, capped at 100).")]
        public List<long>? RoomIds { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RoomBoundariesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var rooms = request.Arguments["room_ids"] is JArray arr && arr.Count > 0
                ? RevitElementKit.ResolveElements(doc, arr).OfType<Room>().ToList()
                : new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>().Where(r => r.Area > 1e-4).Take(100).ToList();

            var opts = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            var result = new JArray();
            foreach (var r in rooms)
            {
                var segLists = r.GetBoundarySegments(opts);
                double perimFt = 0.0;
                var boundIds = new HashSet<long>();
                int loopCount = segLists?.Count ?? 0;
                if (segLists != null)
                {
                    foreach (var loop in segLists)
                    {
                        foreach (var seg in loop)
                        {
                            perimFt += seg.GetCurve()?.Length ?? 0.0;
                            if (seg.ElementId != ElementId.InvalidElementId)
                                boundIds.Add(IdCompat.ToInt64(seg.ElementId));
                        }
                    }
                }
                result.Add(new JObject
                {
                    ["room_id"] = IdCompat.ToInt64(r.Id),
                    ["number"] = r.Number,
                    ["name"] = r.Name,
                    ["level"] = r.Level?.Name ?? string.Empty,
                    ["area_m2"] = Math.Round(UnitKit.SqFeetToSqM(r.Area), 2),
                    ["perimeter_m"] = Math.Round(UnitKit.FeetToMm(perimFt) / 1000.0, 2),
                    ["loop_count"] = loopCount,
                    ["bounding_element_ids"] = new JArray(boundIds)
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject { ["count"] = result.Count, ["rooms"] = result }, $"Inspected boundaries for {result.Count} room(s).");
        }
    }

    [ScioTool("scio_room_finishes", "modeling", "Generate finish floors or baseboards along placed Room finish boundaries (with dry-run preview).", ReadOnly = false)]
    public sealed class RoomFinishesCommand : IRevitCommand
    {
        [ScioParam("operation", "Finish type: floors or baseboards.", Required = true, EnumValues = new[] { "floors", "baseboards" })]
        public string Operation { get; set; } = "floors";

        [ScioParam("room_ids", "Target Room ElementIds.", Required = true)]
        public List<long> RoomIds { get; set; } = new List<long>();

        [ScioParam("type_name", "Exact FloorType (for floors) or WallType (for baseboards) name.")]
        public string? TypeName { get; set; }

        [ScioParam("offset_mm", "Height above level in mm (default 0) for floors, or baseboard height in mm (default 100).")]
        public double OffsetMm { get; set; } = 0.0;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RoomFinishesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var rooms = RevitElementKit.ResolveElements(doc, request.Arguments["room_ids"] as JArray).OfType<Room>().Where(r => r.Area > 1e-4).ToList();
            if (rooms.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_rooms", "No valid placed Room elements resolved from 'room_ids'.");

            string op = (request.Arguments.Value<string>("operation") ?? "floors").Trim().ToLowerInvariant();
            string? typeName = request.Arguments.Value<string>("type_name");
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            var opts = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Room Finishes ({op})",
                dryRun,
                d =>
                {
                    var created = new JArray();
                    if (op == "floors")
                    {
                        var ft = new FilteredElementCollector(d).OfClass(typeof(FloorType)).Cast<FloorType>()
                            .FirstOrDefault(t => string.IsNullOrWhiteSpace(typeName) || string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("No matching FloorType found.");
                        double offFt = UnitKit.MmToFeet(request.Arguments.Value<double?>("offset_mm") ?? 0.0);

                        foreach (var r in rooms)
                        {
                            var segLists = r.GetBoundarySegments(opts);
                            if (segLists == null || segLists.Count == 0) continue;
                            var loops = new List<CurveLoop>();
                            foreach (var segList in segLists)
                            {
                                var cl = new CurveLoop();
                                foreach (var seg in segList)
                                {
                                    var c = seg.GetCurve();
                                    if (c != null) cl.Append(c);
                                }
                                if (cl.NumberOfCurves() >= 3) loops.Add(cl);
                            }
                            if (loops.Count == 0) continue;
                            var fl = Floor.Create(d, loops, ft.Id, r.LevelId);
                            var hp = fl.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                            if (hp != null && !hp.IsReadOnly) hp.Set(offFt);
                            created.Add(new JObject { ["room_id"] = IdCompat.ToInt64(r.Id), ["floor_id"] = IdCompat.ToInt64(fl.Id) });
                        }
                    }
                    else
                    {
                        var wt = new FilteredElementCollector(d).OfClass(typeof(WallType)).Cast<WallType>()
                            .Where(t => t.Kind == WallKind.Basic)
                            .FirstOrDefault(t => string.IsNullOrWhiteSpace(typeName) || string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("No matching Basic WallType found for baseboards.");
                        double hFt = UnitKit.MmToFeet(Math.Max(20.0, request.Arguments.Value<double?>("offset_mm") ?? 100.0));

                        foreach (var r in rooms)
                        {
                            var segLists = r.GetBoundarySegments(opts);
                            if (segLists == null) continue;
                            foreach (var segList in segLists)
                            {
                                foreach (var seg in segList)
                                {
                                    var c = seg.GetCurve();
                                    if (c == null || c.Length < 1e-3) continue;
                                    var w = Wall.Create(d, c, wt.Id, r.LevelId, hFt, 0.0, false, false);
                                    created.Add(new JObject { ["room_id"] = IdCompat.ToInt64(r.Id), ["baseboard_id"] = IdCompat.ToInt64(w.Id) });
                                }
                            }
                        }
                    }

                    return new JObject { ["operation"] = op, ["rooms_processed"] = rooms.Count, ["created_count"] = created.Count, ["items"] = created };
                },
                (d, staged) => staged,
                $"Generated room finishes ({op}) for {rooms.Count} room(s).");
        }
    }

    [ScioTool("scio_toposolid_ops", "modeling", "Inspect Toposolid elements or grade Toposolid vertices around Floor perimeters (Revit 2024+).", ReadOnly = false)]
    public sealed class ToposolidOpsCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation: read or grade_around_floors.", Required = true, EnumValues = new[] { "read", "grade_around_floors" })]
        public string Operation { get; set; } = "read";

        [ScioParam("toposolid_id", "Target Toposolid ElementId for grade_around_floors.")]
        public long? ToposolidId { get; set; }

        [ScioParam("floor_ids", "Floor ElementIds whose perimeter corners drive grading.")]
        public List<long>? FloorIds { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ToposolidOpsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
#if REVIT2024_OR_GREATER
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "read").Trim().ToLowerInvariant();
            if (op == "read")
            {
                var topos = new FilteredElementCollector(doc).OfClass(typeof(Toposolid)).Cast<Toposolid>()
                    .Select(t => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(t.Id),
                        ["name"] = t.Name,
                        ["vertex_count"] = t.GetSlabShapeEditor()?.SlabShapeVertices.Size ?? 0
                    }).ToList();
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = topos.Count, ["toposolids"] = new JArray(topos) }, $"Found {topos.Count} Toposolid element(s).");
            }

            var topo = RevitElementKit.ResolveElement(doc, request.Arguments["toposolid_id"]) as Toposolid;
            var floors = RevitElementKit.ResolveElements(doc, request.Arguments["floor_ids"] as JArray).OfType<Floor>().ToList();
            if (topo == null || floors.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Valid 'toposolid_id' and 'floor_ids' required.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Grade Toposolid Around Floors",
                dryRun,
                d =>
                {
                    var ed = topo.GetSlabShapeEditor();
                    if (!ed.IsEnabled) ed.Enable();
                    int added = 0;
                    foreach (var fl in floors)
                    {
                        var bb = fl.get_BoundingBox(null);
                        if (bb == null) continue;
                        double z = bb.Max.Z;
                        var pts = new[]
                        {
                            new XYZ(bb.Min.X, bb.Min.Y, z),
                            new XYZ(bb.Max.X, bb.Min.Y, z),
                            new XYZ(bb.Max.X, bb.Max.Y, z),
                            new XYZ(bb.Min.X, bb.Max.Y, z)
                        };
                        foreach (var pt in pts)
                        {
#if REVIT2026_OR_GREATER
                            ed.AddPoint(pt);
#else
                            ed.DrawPoint(pt);
#endif
                            added++;
                        }
                    }
                    return new JObject { ["toposolid_id"] = IdCompat.ToInt64(topo.Id), ["points_added"] = added };
                },
                (d, staged) => staged,
                $"Graded Toposolid {IdCompat.ToInt64(topo.Id)} around {floors.Count} floor(s).");
#else
            return ToolResponse.Refusal(request.RequestId, "revit_version_unsupported", "scio_toposolid_ops requires Revit 2024 or greater (Toposolid API).");
#endif
        }
    }

    [ScioTool("scio_regroup_by_param", "modeling", "Group elements into Revit Model Groups by parameter value, or ungroup and stamp a traceability mark.", ReadOnly = false)]
    public sealed class RegroupByParamCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation: regroup or ungroup_and_mark.", Required = true, EnumValues = new[] { "regroup", "ungroup_and_mark" })]
        public string Operation { get; set; } = "regroup";

        [ScioParam("element_ids", "ElementIds to regroup, or Group ElementIds for ungroup_and_mark.", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        [ScioParam("parameter_name", "Parameter name to group by (or target parameter for ungroup_and_mark, default 'Comments').")]
        public string ParameterName { get; set; } = "Comments";

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RegroupByParamCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var elems = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elems.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "No valid elements resolved from 'element_ids'.");

            string op = (request.Arguments.Value<string>("operation") ?? "regroup").Trim().ToLowerInvariant();
            string paramName = (request.Arguments.Value<string>("parameter_name") ?? "Comments").Trim();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Group Ops ({op})",
                dryRun,
                d =>
                {
                    if (op == "ungroup_and_mark")
                    {
                        int ungrouped = 0, marked = 0;
                        foreach (var grp in elems.OfType<Group>())
                        {
                            string gName = grp.Name;
                            var memberIds = grp.UngroupMembers();
                            ungrouped++;
                            foreach (var mid in memberIds)
                            {
                                var m = d.GetElement(mid);
                                var p = m?.LookupParameter(paramName);
                                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                                {
                                    p.Set($"ungrouped:{gName}");
                                    marked++;
                                }
                            }
                        }
                        return new JObject { ["ungrouped_groups"] = ungrouped, ["marked_members"] = marked };
                    }

                    var buckets = elems.GroupBy(e =>
                    {
                        var p = e.LookupParameter(paramName);
                        string val = p?.AsValueString() ?? p?.AsString() ?? "Unassigned";
                        return string.IsNullOrWhiteSpace(val) ? "Unassigned" : val.Trim();
                    }, StringComparer.OrdinalIgnoreCase);

                    var createdGroups = new JArray();
                    foreach (var b in buckets)
                    {
                        var ids = b.Select(e => e.Id).ToList();
                        if (ids.Count == 0) continue;
                        var grp = d.Create.NewGroup(ids);
                        createdGroups.Add(new JObject { ["group_id"] = IdCompat.ToInt64(grp.Id), ["key"] = b.Key, ["member_count"] = ids.Count });
                    }
                    return new JObject { ["created_groups"] = createdGroups.Count, ["groups"] = createdGroups };
                },
                (d, staged) => staged,
                $"Group operation '{op}' completed.");
        }
    }
}
