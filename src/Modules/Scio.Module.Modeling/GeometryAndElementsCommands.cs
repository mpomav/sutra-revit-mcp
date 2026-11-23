using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Modeling
{
    internal static class ModelingGeometryKit
    {
        public static XYZ ParsePointMm(JToken? tok)
        {
            if (tok is JArray arr && arr.Count >= 2)
            {
                double x = arr[0].Value<double>();
                double y = arr[1].Value<double>();
                double z = arr.Count >= 3 ? arr[2].Value<double>() : 0.0;
                return new XYZ(UnitKit.MmToFeet(x), UnitKit.MmToFeet(y), UnitKit.MmToFeet(z));
            }
            if (tok is JObject obj)
            {
                double x = obj.Value<double?>("x") ?? 0.0;
                double y = obj.Value<double?>("y") ?? 0.0;
                double z = obj.Value<double?>("z") ?? 0.0;
                return new XYZ(UnitKit.MmToFeet(x), UnitKit.MmToFeet(y), UnitKit.MmToFeet(z));
            }
            throw new ArgumentException("Point must be [x,y] or [x,y,z] in mm.");
        }

        public static Level ResolveLevel(Document doc, string? levelName, long? levelId = null)
        {
            if (levelId.HasValue && levelId.Value > 0 && doc.GetElement(IdCompat.FromInt64(levelId.Value)) is Level byId)
                return byId;

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            if (levels.Count == 0)
                throw new InvalidOperationException("Document has no levels.");

            if (!string.IsNullOrWhiteSpace(levelName))
            {
                var match = levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new InvalidOperationException($"Level '{levelName}' not found.");
                return match;
            }
            return levels[0];
        }

        public static CurveLoop BuildClosedLoopMm(JArray points, double zFeet = 0.0)
        {
            if (points == null || points.Count < 3)
                throw new ArgumentException("At least 3 boundary points are required.");

            var pts = points.Select(p =>
            {
                var xyz = ParsePointMm(p);
                return new XYZ(xyz.X, xyz.Y, zFeet);
            }).ToList();

            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(b) < 1e-4) continue;
                loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }
    }

    [ScioTool("scio_create_elements", "modeling", "Create Revit elements (wall, floor, level, grid, family_instance, room) in mm coordinates with dry-run preview and post-commit verification.", ReadOnly = false)]
    public sealed class CreateElementsCommand : IRevitCommand
    {
        [ScioParam("items", "Array of creation specs: { kind: 'wall'|'floor'|'level'|'grid'|'family_instance'|'room', ... }.", Required = true)]
        public JArray Items { get; set; } = new JArray();

        [ScioParam("dry_run", "If true (default), previews creation and rolls back.")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateElementsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var items = request.Arguments["items"] as JArray;
            if (items == null || items.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_items", "Provide a non-empty 'items' array.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Create {items.Count} Element(s)",
                dryRun,
                d =>
                {
                    var created = new JArray();
                    var errors = new JArray();

                    foreach (var spec in items.OfType<JObject>())
                    {
                        string kind = (spec.Value<string>("kind") ?? string.Empty).Trim().ToLowerInvariant();
                        try
                        {
                            Element el = CreateOne(d, kind, spec);
                            created.Add(new JObject
                            {
                                ["kind"] = kind,
                                ["id"] = IdCompat.ToInt64(el.Id),
                                ["unique_id"] = el.UniqueId,
                                ["name"] = el.Name ?? string.Empty
                            });
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new JObject { ["kind"] = kind, ["error"] = ex.Message });
                        }
                    }

                    return new JObject
                    {
                        ["created_count"] = created.Count,
                        ["error_count"] = errors.Count,
                        ["created"] = created,
                        ["errors"] = errors
                    };
                },
                (d, staged) =>
                {
                    int verified = 0;
                    if (staged["created"] is JArray arr)
                        verified = arr.Count(t => RevitElementKit.ResolveElement(d, t["id"]) != null);
                    staged["verified_count"] = verified;
                    return staged;
                },
                $"Created elements from {items.Count} spec(s).");
        }

        private static Element CreateOne(Document doc, string kind, JObject spec)
        {
            switch (kind)
            {
                case "level":
                {
                    double elevMm = spec.Value<double>("elevation_mm");
                    var lvl = Level.Create(doc, UnitKit.MmToFeet(elevMm));
                    string? name = spec.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(name)) lvl.Name = name;
                    return lvl;
                }
                case "grid":
                {
                    var p0 = ModelingGeometryKit.ParsePointMm(spec["start_mm"]);
                    var p1 = ModelingGeometryKit.ParsePointMm(spec["end_mm"]);
                    var g = Grid.Create(doc, Line.CreateBound(new XYZ(p0.X, p0.Y, 0), new XYZ(p1.X, p1.Y, 0)));
                    string? name = spec.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(name)) g.Name = name;
                    return g;
                }
                case "wall":
                {
                    var lvl = ModelingGeometryKit.ResolveLevel(doc, spec.Value<string>("level"), spec.Value<long?>("level_id"));
                    var p0 = ModelingGeometryKit.ParsePointMm(spec["start_mm"]);
                    var p1 = ModelingGeometryKit.ParsePointMm(spec["end_mm"]);
                    double heightMm = spec.Value<double?>("height_mm") ?? 3000.0;
                    double offsetMm = spec.Value<double?>("offset_mm") ?? 0.0;
                    bool structural = spec.Value<bool?>("structural") ?? false;
                    var line = Line.CreateBound(new XYZ(p0.X, p0.Y, lvl.Elevation), new XYZ(p1.X, p1.Y, lvl.Elevation));

                    WallType? wt = ResolveType<WallType>(doc, spec.Value<string>("type"), spec.Value<long?>("type_id"));
                    if (wt != null)
                        return Wall.Create(doc, line, wt.Id, lvl.Id, UnitKit.MmToFeet(heightMm), UnitKit.MmToFeet(offsetMm), false, structural);
                    return Wall.Create(doc, line, lvl.Id, structural);
                }
                case "floor":
                {
                    var lvl = ModelingGeometryKit.ResolveLevel(doc, spec.Value<string>("level"), spec.Value<long?>("level_id"));
                    var pts = spec["boundary_mm"] as JArray ?? throw new ArgumentException("floor requires 'boundary_mm' array of [x,y] points.");
                    var loop = ModelingGeometryKit.BuildClosedLoopMm(pts, lvl.Elevation);
                    FloorType ft = ResolveType<FloorType>(doc, spec.Value<string>("type"), spec.Value<long?>("type_id"))
                        ?? new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First();
                    return Floor.Create(doc, new List<CurveLoop> { loop }, ft.Id, lvl.Id);
                }
                case "family_instance":
                {
                    var lvl = ModelingGeometryKit.ResolveLevel(doc, spec.Value<string>("level"), spec.Value<long?>("level_id"));
                    var pt = ModelingGeometryKit.ParsePointMm(spec["location_mm"]);
                    FamilySymbol sym = ResolveFamilySymbol(doc, spec.Value<string>("family"), spec.Value<string>("type"), spec.Value<long?>("type_id"))
                        ?? throw new InvalidOperationException("Matching FamilySymbol not found.");
                    if (!sym.IsActive) sym.Activate();

                    Element? host = RevitElementKit.ResolveElement(doc, spec["host_id"]);
                    var st = Enum.TryParse<StructuralType>(spec.Value<string>("structural_type") ?? "NonStructural", true, out var parsedSt)
                        ? parsedSt : StructuralType.NonStructural;

                    if (host != null)
                        return doc.Create.NewFamilyInstance(pt, sym, host, lvl, st);
                    return doc.Create.NewFamilyInstance(pt, sym, lvl, st);
                }
                case "room":
                {
                    var lvl = ModelingGeometryKit.ResolveLevel(doc, spec.Value<string>("level"), spec.Value<long?>("level_id"));
                    var pt = ModelingGeometryKit.ParsePointMm(spec["location_mm"]);
                    Room r = doc.Create.NewRoom(lvl, new UV(pt.X, pt.Y));
                    string? name = spec.Value<string>("name");
                    string? num = spec.Value<string>("number");
                    if (!string.IsNullOrWhiteSpace(name)) r.Name = name;
                    if (!string.IsNullOrWhiteSpace(num)) r.Number = num;
                    return r;
                }
                default:
                    throw new ArgumentException($"Unsupported creation kind '{kind}'.");
            }
        }

        private static T? ResolveType<T>(Document doc, string? typeName, long? typeId) where T : ElementType
        {
            if (typeId.HasValue && typeId.Value > 0 && doc.GetElement(IdCompat.FromInt64(typeId.Value)) is T byId)
                return byId;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                return new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                    .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            }
            return null;
        }

        private static FamilySymbol? ResolveFamilySymbol(Document doc, string? familyName, string? typeName, long? typeId)
        {
            if (typeId.HasValue && typeId.Value > 0 && doc.GetElement(IdCompat.FromInt64(typeId.Value)) is FamilySymbol byId)
                return byId;
            var syms = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            return syms.FirstOrDefault(s =>
                (string.IsNullOrWhiteSpace(familyName) || string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(typeName) || string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [ScioTool("scio_transform_elements", "modeling", "Move, rotate, copy, mirror, pin/unpin, or change type of elements in mm/degrees with dry-run support.", ReadOnly = false)]
    public sealed class TransformElementsCommand : IRevitCommand
    {
        [ScioParam("action", "Transform operation.", Required = true, EnumValues = new[] { "move", "copy", "rotate", "pin", "unpin", "change_type" })]
        public string Action { get; set; } = "move";

        [ScioParam("element_ids", "Target element IDs.", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        [ScioParam("translation_mm", "Translation vector [dx, dy, dz] in mm for move/copy.")]
        public List<double>? TranslationMm { get; set; }

        [ScioParam("origin_mm", "Rotation axis origin [x, y, z] in mm for rotate.")]
        public List<double>? OriginMm { get; set; }

        [ScioParam("angle_deg", "Rotation angle in degrees around Z axis for rotate.")]
        public double? AngleDeg { get; set; }

        [ScioParam("new_type_id", "Target ElementType ID for change_type.")]
        public long? NewTypeId { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(TransformElementsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "move").Trim().ToLowerInvariant();
            var elements = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elements.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "No matching elements found.");

            var ids = elements.Select(e => e.Id).ToList();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Transform ({action})",
                dryRun,
                d =>
                {
                    var result = new JObject { ["action"] = action, ["element_count"] = ids.Count };
                    switch (action)
                    {
                        case "move":
                        {
                            var vec = ModelingGeometryKit.ParsePointMm(request.Arguments["translation_mm"]);
                            ElementTransformUtils.MoveElements(d, ids, vec);
                            break;
                        }
                        case "copy":
                        {
                            var vec = ModelingGeometryKit.ParsePointMm(request.Arguments["translation_mm"]);
                            var copied = ElementTransformUtils.CopyElements(d, ids, vec);
                            result["copied_ids"] = new JArray(copied.Select(IdCompat.ToInt64));
                            break;
                        }
                        case "rotate":
                        {
                            var origin = request.Arguments["origin_mm"] != null
                                ? ModelingGeometryKit.ParsePointMm(request.Arguments["origin_mm"])
                                : XYZ.Zero;
                            double rad = UnitKit.DegToRad(request.Arguments.Value<double?>("angle_deg") ?? 0.0);
                            var axis = Line.CreateBound(origin, origin + XYZ.BasisZ);
                            ElementTransformUtils.RotateElements(d, ids, axis, rad);
                            break;
                        }
                        case "pin":
                        case "unpin":
                        {
                            bool pinVal = action == "pin";
                            foreach (var el in elements) el.Pinned = pinVal;
                            break;
                        }
                        case "change_type":
                        {
                            long tid = request.Arguments.Value<long?>("new_type_id") ?? 0;
                            if (tid <= 0) throw new ArgumentException("new_type_id is required for change_type.");
                            var targetTypeId = IdCompat.FromInt64(tid);
                            foreach (var el in elements) el.ChangeTypeId(targetTypeId);
                            break;
                        }
                        default:
                            throw new ArgumentException($"Unsupported action '{action}'.");
                    }
                    return result;
                },
                (d, staged) => staged,
                $"Applied '{action}' to {ids.Count} element(s).");
        }
    }

    [ScioTool("scio_manage_curtain", "modeling", "Inspect curtain wall grid/panels/mullions or add a curtain grid line at a 3D point (mm).", ReadOnly = false)]
    public sealed class ManageCurtainCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: inspect (default) or add_grid_line.", EnumValues = new[] { "inspect", "add_grid_line" })]
        public string Action { get; set; } = "inspect";

        [ScioParam("wall_id", "Curtain Wall ElementId.", Required = true)]
        public long WallId { get; set; }

        [ScioParam("horizontal", "For add_grid_line: true for horizontal U-grid, false for vertical V-grid.")]
        public bool Horizontal { get; set; }

        [ScioParam("point_mm", "3D point [x,y,z] in mm through which the new curtain grid line passes.")]
        public List<double>? PointMm { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageCurtainCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var wall = RevitElementKit.ResolveElement(doc, request.Arguments["wall_id"]) as Wall;
            if (wall == null || wall.CurtainGrid == null)
                return ToolResponse.Refusal(request.RequestId, "not_curtain_wall", "Target element is not a Curtain Wall.");

            string action = (request.Arguments.Value<string>("action") ?? "inspect").Trim().ToLowerInvariant();
            if (action == "inspect")
            {
                var cg = wall.CurtainGrid;
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["wall_id"] = IdCompat.ToInt64(wall.Id),
                    ["u_grid_lines"] = cg.GetUGridLineIds().Count,
                    ["v_grid_lines"] = cg.GetVGridLineIds().Count,
                    ["panel_count"] = cg.GetPanelIds().Count,
                    ["mullion_count"] = cg.GetMullionIds().Count,
                    ["panel_ids"] = new JArray(cg.GetPanelIds().Take(100).Select(IdCompat.ToInt64))
                }, $"Curtain wall has {cg.GetPanelIds().Count} panels and {cg.GetMullionIds().Count} mullions.");
            }

            bool horiz = request.Arguments.Value<bool?>("horizontal") ?? false;
            var pt = ModelingGeometryKit.ParsePointMm(request.Arguments["point_mm"]);
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Add Curtain Grid Line",
                dryRun,
                d =>
                {
                    var line = wall.CurtainGrid.AddGridLine(horiz, pt, false);
                    return new JObject
                    {
                        ["wall_id"] = IdCompat.ToInt64(wall.Id),
                        ["grid_line_id"] = line == null ? -1 : IdCompat.ToInt64(line.Id),
                        ["horizontal"] = horiz
                    };
                },
                (d, staged) => staged,
                "Added curtain grid line.");
        }
    }

    [ScioTool("scio_slab_shape", "modeling", "Inspect, add elevation vertices, or reset a Floor/Roof SlabShapeEditor in mm.", ReadOnly = false)]
    public sealed class SlabShapeCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: inspect (default), add_points, or reset.", EnumValues = new[] { "inspect", "add_points", "reset" })]
        public string Action { get; set; } = "inspect";

        [ScioParam("element_id", "Floor or FootPrintRoof ElementId.", Required = true)]
        public long ElementId { get; set; }

        [ScioParam("points_mm", "Array of [x, y, z_offset_mm] points for add_points.")]
        public JArray? PointsMm { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SlabShapeCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var el = RevitElementKit.ResolveElement(doc, request.Arguments["element_id"]);
#if REVIT2024_OR_GREATER
            SlabShapeEditor? editor = (el as Floor)?.GetSlabShapeEditor() ?? (el as RoofBase)?.GetSlabShapeEditor();
#else
            SlabShapeEditor? editor = (el as Floor)?.SlabShapeEditor ?? (el as RoofBase)?.SlabShapeEditor;
#endif
            if (editor == null)
                return ToolResponse.Refusal(request.RequestId, "no_slab_editor", "Target element does not support SlabShapeEditor.");

            string action = (request.Arguments.Value<string>("action") ?? "inspect").Trim().ToLowerInvariant();
            if (action == "inspect")
            {
                var verts = new JArray();
                foreach (SlabShapeVertex v in editor.SlabShapeVertices)
                {
                    verts.Add(new JArray(
                        Math.Round(UnitKit.FeetToMm(v.Position.X), 1),
                        Math.Round(UnitKit.FeetToMm(v.Position.Y), 1),
                        Math.Round(UnitKit.FeetToMm(v.Position.Z), 1)));
                }
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["element_id"] = IdCompat.ToInt64(el!.Id),
                    ["is_enabled"] = editor.IsEnabled,
                    ["vertex_count"] = verts.Count,
                    ["vertices_mm"] = verts
                }, $"SlabShapeEditor enabled={editor.IsEnabled}, {verts.Count} vertices.");
            }

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Slab Shape ({action})",
                dryRun,
                d =>
                {
                    if (action == "reset")
                    {
                        editor.ResetSlabShape();
                        return new JObject { ["element_id"] = IdCompat.ToInt64(el!.Id), ["reset"] = true };
                    }
                    editor.Enable();
                    var pts = request.Arguments["points_mm"] as JArray ?? new JArray();
                    int added = 0;
                    foreach (var tok in pts)
                    {
                        var p = ModelingGeometryKit.ParsePointMm(tok);
#if REVIT2026_OR_GREATER
                        if (editor.AddPoint(p) != null) added++;
#else
                        if (editor.DrawPoint(p) != null) added++;
#endif
                    }
                    return new JObject
                    {
                        ["element_id"] = IdCompat.ToInt64(el!.Id),
                        ["added_points"] = added,
                        ["total_vertices"] = editor.SlabShapeVertices.Size
                    };
                },
                (d, staged) => staged,
                $"Slab shape '{action}' processed.");
        }
    }

    [ScioTool("scio_framing", "modeling", "Create structural beams or braces between 3D mm points on a level with dry-run support.", ReadOnly = false)]
    public sealed class FramingCommand : IRevitCommand
    {
        [ScioParam("beams", "Array of specs: { start_mm: [x,y,z], end_mm: [x,y,z], level?, family?, type?, type_id?, kind?: 'beam'|'brace' }.", Required = true)]
        public JArray Beams { get; set; } = new JArray();

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(FramingCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var beams = request.Arguments["beams"] as JArray;
            if (beams == null || beams.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_beams", "Provide non-empty 'beams' array.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Create {beams.Count} Framing Member(s)",
                dryRun,
                d =>
                {
                    var created = new JArray();
                    var errors = new JArray();
                    var symbols = new FilteredElementCollector(d)
                        .OfCategory(BuiltInCategory.OST_StructuralFraming)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .ToList();

                    foreach (var spec in beams.OfType<JObject>())
                    {
                        try
                        {
                            var p0 = ModelingGeometryKit.ParsePointMm(spec["start_mm"]);
                            var p1 = ModelingGeometryKit.ParsePointMm(spec["end_mm"]);
                            var lvl = ModelingGeometryKit.ResolveLevel(d, spec.Value<string>("level"), spec.Value<long?>("level_id"));
                            long? tid = spec.Value<long?>("type_id");
                            string? fam = spec.Value<string>("family");
                            string? typ = spec.Value<string>("type");

                            FamilySymbol? sym = tid.HasValue
                                ? d.GetElement(IdCompat.FromInt64(tid.Value)) as FamilySymbol
                                : symbols.FirstOrDefault(s =>
                                    (string.IsNullOrWhiteSpace(fam) || string.Equals(s.FamilyName, fam, StringComparison.OrdinalIgnoreCase)) &&
                                    (string.IsNullOrWhiteSpace(typ) || string.Equals(s.Name, typ, StringComparison.OrdinalIgnoreCase)))
                                  ?? symbols.FirstOrDefault();

                            if (sym == null) throw new InvalidOperationException("No Structural Framing FamilySymbol loaded.");
                            if (!sym.IsActive) sym.Activate();

                            var st = string.Equals(spec.Value<string>("kind"), "brace", StringComparison.OrdinalIgnoreCase)
                                ? StructuralType.Brace : StructuralType.Beam;

                            var inst = d.Create.NewFamilyInstance(Line.CreateBound(p0, p1), sym, lvl, st);
                            created.Add(new JObject
                            {
                                ["id"] = IdCompat.ToInt64(inst.Id),
                                ["type"] = $"{sym.FamilyName} : {sym.Name}",
                                ["length_mm"] = Math.Round(UnitKit.FeetToMm(p0.DistanceTo(p1)), 1)
                            });
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new JObject { ["error"] = ex.Message });
                        }
                    }

                    return new JObject { ["created_count"] = created.Count, ["error_count"] = errors.Count, ["created"] = created, ["errors"] = errors };
                },
                (d, staged) => staged,
                $"Processed {beams.Count} framing member(s).");
        }
    }

    [ScioTool("scio_create_railing", "modeling", "Create a Railing along a 2D/3D polyline in mm on a specified level.", ReadOnly = false)]
    public sealed class CreateRailingCommand : IRevitCommand
    {
        [ScioParam("points_mm", "Ordered array of [x, y] or [x, y, z] points in mm defining the railing path.", Required = true)]
        public JArray PointsMm { get; set; } = new JArray();

        [ScioParam("level", "Level name (defaults to lowest level).")]
        public string LevelName { get; set; } = string.Empty;

        [ScioParam("type", "Optional RailingType name.")]
        public string TypeName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateRailingCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var pts = request.Arguments["points_mm"] as JArray;
            if (pts == null || pts.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "missing_points", "Provide at least 2 points in 'points_mm'.");

            var lvl = ModelingGeometryKit.ResolveLevel(doc, request.Arguments.Value<string>("level"));
            string? typeName = request.Arguments.Value<string>("type");
            var rType = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).Cast<RailingType>()
                .FirstOrDefault(t => string.IsNullOrWhiteSpace(typeName) || string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (rType == null)
                return ToolResponse.Refusal(request.RequestId, "no_railing_type", "No RailingType found in document.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Railing",
                dryRun,
                d =>
                {
                    var loop = new CurveLoop();
                    var xyzs = pts.Select(ModelingGeometryKit.ParsePointMm).Select(p => new XYZ(p.X, p.Y, lvl.Elevation)).ToList();
                    for (int i = 0; i < xyzs.Count - 1; i++)
                    {
                        if (xyzs[i].DistanceTo(xyzs[i + 1]) > 1e-4)
                            loop.Append(Line.CreateBound(xyzs[i], xyzs[i + 1]));
                    }
                    var railing = Railing.Create(d, loop, rType.Id, lvl.Id);
                    return new JObject
                    {
                        ["id"] = IdCompat.ToInt64(railing.Id),
                        ["type"] = rType.Name,
                        ["level"] = lvl.Name
                    };
                },
                (d, staged) =>
                {
                    staged["verified"] = RevitElementKit.ResolveElement(d, staged["id"]) != null;
                    return staged;
                },
                $"Created railing '{rType.Name}' on '{lvl.Name}'.");
        }
    }

    [ScioTool("scio_manage_assemblies_parts", "modeling", "List or create Assemblies, or convert eligible elements into Revit Parts.", ReadOnly = false)]
    public sealed class ManageAssembliesPartsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list_assemblies (default), create_assembly, or create_parts.", EnumValues = new[] { "list_assemblies", "create_assembly", "create_parts" })]
        public string Action { get; set; } = "list_assemblies";

        [ScioParam("element_ids", "Element IDs for create_assembly or create_parts.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("assembly_name", "Optional name for the created assembly type.")]
        public string AssemblyName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageAssembliesPartsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list_assemblies").Trim().ToLowerInvariant();

            if (action == "list_assemblies")
            {
                var items = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>()
                    .Select(a => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(a.Id),
                        ["name"] = a.AssemblyTypeName,
                        ["member_count"] = a.GetMemberIds().Count
                    });
                var arr = new JArray(items);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["assemblies"] = arr }, $"Found {arr.Count} assembly instance(s).");
            }

            var elements = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elements.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "Provide valid 'element_ids'.");

            var ids = elements.Select(e => e.Id).ToList();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Assemblies/Parts ({action})",
                dryRun,
                d =>
                {
                    if (action == "create_parts")
                    {
                        if (!PartUtils.AreElementsValidForCreateParts(d, ids))
                            throw new InvalidOperationException("One or more elements are not valid for CreateParts.");
                        PartUtils.CreateParts(d, ids);
                        return new JObject { ["action"] = action, ["source_count"] = ids.Count };
                    }

                    ElementId namingCat = elements[0].Category?.Id ?? ElementId.InvalidElementId;
                    var asm = AssemblyInstance.Create(d, ids, namingCat);
                    string? asmName = request.Arguments.Value<string>("assembly_name");
                    if (!string.IsNullOrWhiteSpace(asmName))
                    {
                        d.Regenerate();
                        asm.AssemblyTypeName = asmName;
                    }
                    return new JObject
                    {
                        ["action"] = action,
                        ["assembly_id"] = IdCompat.ToInt64(asm.Id),
                        ["name"] = asm.AssemblyTypeName,
                        ["member_count"] = ids.Count
                    };
                },
                (d, staged) => staged,
                $"Executed '{action}' on {ids.Count} element(s).");
        }
    }
}
