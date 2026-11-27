using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Documentation
{
    [ScioTool("scio_planimetry_ops", "documentation", "Planimetry finishing operations: move_viewport, resolve_overlaps, replace_text, stagger_grid_bubbles, sync_sheet_references.", ReadOnly = false)]
    public sealed class PlanimetryOpsCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation to execute.", Required = true, EnumValues = new[]
        {
            "move_viewport", "resolve_overlaps", "replace_text", "stagger_grid_bubbles", "sync_sheet_references"
        })]
        public string Operation { get; set; } = "resolve_overlaps";

        [ScioParam("sheet_id", "Target ViewSheet ElementId for viewport operations.")]
        public long? SheetId { get; set; }

        [ScioParam("viewport_id", "Viewport ElementId for move_viewport.")]
        public long? ViewportId { get; set; }

        [ScioParam("target_mm", "Target center [x, y] in mm on sheet for move_viewport.")]
        public List<double>? TargetMm { get; set; }

        [ScioParam("gap_mm", "Minimum gap between viewports in mm for resolve_overlaps (default 15).")]
        public double GapMm { get; set; } = 15.0;

        [ScioParam("view_id", "Target View ElementId for replace_text or stagger_grid_bubbles.")]
        public long? ViewId { get; set; }

        [ScioParam("find", "Substring to search for in replace_text.")]
        public string? Find { get; set; }

        [ScioParam("replace", "Replacement text for replace_text.")]
        public string? Replace { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PlanimetryOpsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "resolve_overlaps").Trim().ToLowerInvariant();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Planimetry Ops ({op})",
                dryRun,
                d =>
                {
                    switch (op)
                    {
                        case "move_viewport":
                        {
                            var vp = RevitElementKit.ResolveElement(d, request.Arguments["viewport_id"]) as Viewport
                                ?? throw new InvalidOperationException("Valid 'viewport_id' required.");
                            var tArr = request.Arguments["target_mm"] as JArray;
                            if (tArr == null || tArr.Count < 2) throw new InvalidOperationException("'target_mm' [x,y] required.");
                            var center = new XYZ(UnitKit.MmToFeet(tArr[0].Value<double>()), UnitKit.MmToFeet(tArr[1].Value<double>()), 0);
                            vp.SetBoxCenter(center);
                            return new JObject { ["viewport_id"] = IdCompat.ToInt64(vp.Id), ["moved_to_mm"] = tArr };
                        }
                        case "resolve_overlaps":
                        {
                            var sheet = RevitElementKit.ResolveElement(d, request.Arguments["sheet_id"]) as ViewSheet
                                ?? (d.ActiveView as ViewSheet)
                                ?? throw new InvalidOperationException("Valid 'sheet_id' or active ViewSheet required.");
                            double gapMm = Math.Max(2.0, request.Arguments.Value<double?>("gap_mm") ?? 15.0);
                            var vps = sheet.GetAllViewports().Select(id => d.GetElement(id) as Viewport).Where(v => v != null).Cast<Viewport>().ToList();
                            var rects = new List<Rect2D>();
                            var vpById = new Dictionary<string, Viewport>(StringComparer.Ordinal);
                            foreach (var vp in vps)
                            {
                                var box = vp.GetBoxOutline();
                                string key = IdCompat.ToInt64(vp.Id).ToString();
                                vpById[key] = vp;
                                rects.Add(new Rect2D
                                {
                                    Id = key,
                                    MinX = UnitKit.FeetToMm(box.MinimumPoint.X),
                                    MinY = UnitKit.FeetToMm(box.MinimumPoint.Y),
                                    MaxX = UnitKit.FeetToMm(box.MaximumPoint.X),
                                    MaxY = UnitKit.FeetToMm(box.MaximumPoint.Y)
                                });
                            }

                            var moves = PlanimetryMathKit.ResolveOverlaps(rects, gapMm);
                            var movedArr = new JArray();
                            foreach (var mv in moves)
                            {
                                var vp = vpById[mv.Id];
                                var cur = vp.GetBoxCenter();
                                vp.SetBoxCenter(new XYZ(cur.X + UnitKit.MmToFeet(mv.DeltaX), cur.Y + UnitKit.MmToFeet(mv.DeltaY), cur.Z));
                                movedArr.Add(new JObject { ["viewport_id"] = long.Parse(mv.Id), ["dx_mm"] = mv.DeltaX, ["dy_mm"] = mv.DeltaY });
                            }
                            return new JObject { ["sheet_number"] = sheet.SheetNumber, ["moved_count"] = movedArr.Count, ["moves"] = movedArr };
                        }
                        case "replace_text":
                        {
                            string find = request.Arguments.Value<string>("find") ?? string.Empty;
                            string repl = request.Arguments.Value<string>("replace") ?? string.Empty;
                            if (string.IsNullOrEmpty(find)) throw new InvalidOperationException("'find' text is required.");
                            var view = ViewResolveKit.ResolveView(d, request.Arguments["view_id"]);
                            int updated = 0;
                            foreach (var tn in new FilteredElementCollector(d, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>())
                            {
                                string txt = tn.Text ?? string.Empty;
                                if (txt.IndexOf(find, StringComparison.Ordinal) >= 0)
                                {
                                    tn.Text = txt.Replace(find, repl);
                                    updated++;
                                }
                            }
                            return new JObject { ["view_name"] = view.Name, ["updated_notes"] = updated };
                        }
                        case "stagger_grid_bubbles":
                        {
                            var view = ViewResolveKit.ResolveView(d, request.Arguments["view_id"]);
                            var grids = new FilteredElementCollector(d, view.Id).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name).ToList();
                            int toggled = 0;
                            for (int i = 0; i < grids.Count; i++)
                            {
                                bool showStart = (i % 2) == 0;
                                if (showStart) { grids[i].ShowBubbleInView(DatumEnds.End0, view); grids[i].HideBubbleInView(DatumEnds.End1, view); }
                                else { grids[i].HideBubbleInView(DatumEnds.End0, view); grids[i].ShowBubbleInView(DatumEnds.End1, view); }
                                toggled++;
                            }
                            return new JObject { ["view_name"] = view.Name, ["grids_staggered"] = toggled };
                        }
                        case "sync_sheet_references":
                        {
                            var sheets = new FilteredElementCollector(d).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
                            var report = new JArray();
                            foreach (var sh in sheets)
                            {
                                report.Add(new JObject
                                {
                                    ["sheet_number"] = sh.SheetNumber,
                                    ["sheet_name"] = sh.Name,
                                    ["viewport_count"] = sh.GetAllViewports().Count
                                });
                            }
                            return new JObject { ["sheet_count"] = report.Count, ["sheets"] = report };
                        }
                        default:
                            throw new ArgumentException($"Unsupported operation '{op}'.");
                    }
                },
                (d, staged) => staged,
                $"Planimetry operation '{op}' completed.");
        }
    }

    [ScioTool("scio_plan_annotations", "documentation", "Read-only annotation planner: detects untagged elements, grid chains, level chains, and wall openings to dimension before writing.", ReadOnly = true)]
    public sealed class PlanAnnotationsCommand : IRevitCommand
    {
        [ScioParam("operation", "Planning mode.", Required = true, EnumValues = new[]
        {
            "auto_tags", "auto_dimension_grids", "auto_dimension_levels", "auto_dimension_openings"
        })]
        public string Operation { get; set; } = "auto_tags";

        [ScioParam("view_id", "Optional View ElementId (defaults to active view).")]
        public long? ViewId { get; set; }

        [ScioParam("category", "Optional category name for auto_tags (e.g. 'Doors', 'Windows', 'Walls', 'Rooms').")]
        public string? Category { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PlanAnnotationsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = ViewResolveKit.ResolveView(doc, request.Arguments["view_id"]);
            string op = (request.Arguments.Value<string>("operation") ?? "auto_tags").Trim().ToLowerInvariant();

            switch (op)
            {
                case "auto_tags":
                {
                    string? catFilter = request.Arguments.Value<string>("category")?.Trim();
                    var taggedIds = new HashSet<long>();
                    foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                    {
                        foreach (var lid in tag.GetTaggedLocalElementIds())
                            taggedIds.Add(IdCompat.ToInt64(lid));
                    }

                    var candidates = new FilteredElementCollector(doc, view.Id)
                        .WhereElementIsNotElementType()
                        .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                        .Where(e => string.IsNullOrWhiteSpace(catFilter) || string.Equals(e.Category!.Name, catFilter, StringComparison.OrdinalIgnoreCase))
                        .Where(e => !taggedIds.Contains(IdCompat.ToInt64(e.Id)))
                        .Take(300)
                        .Select(e => new JObject
                        {
                            ["id"] = IdCompat.ToInt64(e.Id),
                            ["category"] = e.Category?.Name ?? string.Empty,
                            ["name"] = e.Name
                        });
                    var arr = new JArray(candidates);
                    return ToolResponse.Success(request.RequestId, new JObject
                    {
                        ["operation"] = op,
                        ["view_name"] = view.Name,
                        ["untagged_count"] = arr.Count,
                        ["elements"] = arr
                    }, $"Planned {arr.Count} untagged candidate(s) in '{view.Name}'.");
                }
                case "auto_dimension_grids":
                {
                    var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                        .Select(g => new JObject { ["id"] = IdCompat.ToInt64(g.Id), ["name"] = g.Name })
                        .ToList();
                    return ToolResponse.Success(request.RequestId, new JObject
                    {
                        ["operation"] = op,
                        ["view_name"] = view.Name,
                        ["grid_count"] = grids.Count,
                        ["grids"] = new JArray(grids)
                    }, $"Found {grids.Count} grid(s) in '{view.Name}' for chain dimensioning.");
                }
                case "auto_dimension_levels":
                {
                    var levels = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .Select(l => new JObject
                        {
                            ["id"] = IdCompat.ToInt64(l.Id),
                            ["name"] = l.Name,
                            ["elevation_mm"] = Math.Round(UnitKit.FeetToMm(l.Elevation), 1)
                        })
                        .ToList();
                    return ToolResponse.Success(request.RequestId, new JObject
                    {
                        ["operation"] = op,
                        ["view_name"] = view.Name,
                        ["level_count"] = levels.Count,
                        ["levels"] = new JArray(levels)
                    }, $"Found {levels.Count} level(s) in '{view.Name}'.");
                }
                case "auto_dimension_openings":
                {
                    var openings = new FilteredElementCollector(doc, view.Id)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .Where(fi => fi.Host is Wall && (fi.Category?.Id.Equals(new ElementId(BuiltInCategory.OST_Doors)) == true ||
                                                         fi.Category?.Id.Equals(new ElementId(BuiltInCategory.OST_Windows)) == true))
                        .Take(200)
                        .Select(fi => new JObject
                        {
                            ["id"] = IdCompat.ToInt64(fi.Id),
                            ["category"] = fi.Category?.Name ?? string.Empty,
                            ["host_wall_id"] = IdCompat.ToInt64(fi.Host.Id)
                        })
                        .ToList();
                    return ToolResponse.Success(request.RequestId, new JObject
                    {
                        ["operation"] = op,
                        ["view_name"] = view.Name,
                        ["opening_count"] = openings.Count,
                        ["openings"] = new JArray(openings)
                    }, $"Found {openings.Count} door/window opening(s) in '{view.Name}'.");
                }
                default:
                    return ToolResponse.Refusal(request.RequestId, "invalid_operation", $"Unsupported operation '{op}'.");
            }
        }
    }

    [ScioTool("scio_plan_views", "documentation", "Read-only deliverable view planner: room_views, element_views (isometric_by_host, beam_sections, grid_elevations_sheets), unplaced_views, and deliverable_set readiness.", ReadOnly = true)]
    public sealed class PlanViewsCommand : IRevitCommand
    {
        [ScioParam("operation", "Planning mode.", Required = true, EnumValues = new[] { "room_views", "element_views", "unplaced_views", "deliverable_set" })]
        public string Operation { get; set; } = "deliverable_set";

        [ScioParam("preset", "Preset for operation=element_views.", EnumValues = new[] { "isometric_by_host", "beam_sections", "grid_elevations_sheets" })]
        public string Preset { get; set; } = "isometric_by_host";

        [ScioParam("element_ids", "Optional target ElementIds for operation=element_views (auto-selects Floors / Structural Framing / Grids when omitted).")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("level_name", "Optional Level name filter for operation=element_views.")]
        public string? LevelName { get; set; }

        [ScioParam("padding_mm", "Section box / crop padding margin in mm for operation=element_views (default 150).")]
        public double PaddingMm { get; set; } = 150.0;

        [ScioParam("transverse_count", "Number of transverse sections per beam for preset=beam_sections (1..5, default 1).")]
        public int TransverseCount { get; set; } = 1;

        [ScioParam("name_pattern", "Optional organisation-neutral view name pattern supporting {preset}, {level}, {tag}, {grid}, {index}.")]
        public string? NamePattern { get; set; }

        [ScioParam("sheet_number_prefix", "Optional caller-supplied sheet number prefix for preset=grid_elevations_sheets (default 'S').")]
        public string SheetNumberPrefix { get; set; } = "S";

        [ScioParam("limit", "Maximum planned items to return for operation=element_views (1..500, default 100).")]
        public int Limit { get; set; } = 100;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PlanViewsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "deliverable_set").Trim().ToLowerInvariant();

            if (op == "element_views")
                return PlanElementViews(doc, request.RequestId, request.Arguments ?? new JObject());

            if (op == "room_views")
            {
                var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
                    .Where(r => r.Area > 1e-4)
                    .Select(r => new JObject
                    {
                        ["room_id"] = IdCompat.ToInt64(r.Id),
                        ["number"] = r.Number,
                        ["name"] = r.Name,
                        ["level"] = r.Level?.Name ?? string.Empty,
                        ["area_m2"] = Math.Round(UnitKit.SqFeetToSqM(r.Area), 2)
                    }).ToList();
                return ToolResponse.Success(request.RequestId, new JObject { ["room_count"] = rooms.Count, ["rooms"] = new JArray(rooms) }, $"Planned room views for {rooms.Count} placed room(s).");
            }

            var placedViewIds = new HashSet<long>();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            foreach (var sh in sheets)
            {
                foreach (var vid in sh.GetAllPlacedViews())
                    placedViewIds.Add(IdCompat.ToInt64(vid));
            }

            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet && v.ViewType != ViewType.Internal && v.ViewType != ViewType.ProjectBrowser)
                .ToList();

            var unplaced = allViews.Where(v => !placedViewIds.Contains(IdCompat.ToInt64(v.Id)))
                .Select(v => new JObject { ["id"] = IdCompat.ToInt64(v.Id), ["name"] = v.Name, ["view_type"] = v.ViewType.ToString() })
                .ToList();

            if (op == "unplaced_views")
            {
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["unplaced_count"] = unplaced.Count,
                    ["views"] = new JArray(unplaced)
                }, $"Found {unplaced.Count} unplaced view(s).");
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["sheet_count"] = sheets.Count,
                ["total_views"] = allViews.Count,
                ["placed_views"] = placedViewIds.Count,
                ["unplaced_views"] = unplaced.Count
            }, $"Deliverable set: {sheets.Count} sheet(s), {placedViewIds.Count} placed view(s), {unplaced.Count} unplaced.");
        }

        private static ToolResponse PlanElementViews(Document doc, string requestId, JObject args)
        {
            string preset = (args.Value<string>("preset") ?? "isometric_by_host").Trim().ToLowerInvariant();
            string? levelFilter = args.Value<string>("level_name")?.Trim();
            double paddingMm = Math.Max(0.0, args.Value<double?>("padding_mm") ?? 150.0);
            int transverseCount = Math.Max(1, Math.Min(5, args.Value<int?>("transverse_count") ?? 1));
            string namePattern = args.Value<string>("name_pattern") ?? string.Empty;
            string sheetPrefix = string.IsNullOrWhiteSpace(args.Value<string>("sheet_number_prefix"))
                ? "S"
                : args.Value<string>("sheet_number_prefix")!.Trim();
            int limit = Math.Max(1, Math.Min(500, args.Value<int?>("limit") ?? 100));

            var explicitElements = args["element_ids"] is JArray idArr && idArr.Count > 0
                ? RevitElementKit.ResolveElements(doc, idArr)
                : null;

            switch (preset)
            {
                case "isometric_by_host":
                {
                    var targets = (explicitElements ?? new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Floors)
                            .WhereElementIsNotElementType()
                            .ToList())
                        .Where(e => MatchesLevel(doc, e, levelFilter))
                        .Take(limit)
                        .ToList();

                    var planned = new JArray();
                    int idx = 1;
                    foreach (var el in targets)
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb == null) continue;
                        var padded = PlanimetryMathKit.ComputePaddedBox(new[]
                        {
                            new Box3DMm
                            {
                                MinX = UnitKit.FeetToMm(bb.Min.X),
                                MinY = UnitKit.FeetToMm(bb.Min.Y),
                                MinZ = UnitKit.FeetToMm(bb.Min.Z),
                                MaxX = UnitKit.FeetToMm(bb.Max.X),
                                MaxY = UnitKit.FeetToMm(bb.Max.Y),
                                MaxZ = UnitKit.FeetToMm(bb.Max.Z)
                            }
                        }, paddingMm)!;

                        string lvl = ResolveElementLevelName(doc, el);
                        string tag = ReadMarkOrId(el);
                        string viewName = PlanimetryMathKit.FormatRevitSafeName(
                            string.IsNullOrWhiteSpace(namePattern) ? "ISO_{level}_{tag}" : namePattern,
                            "ISO", lvl, tag, string.Empty, idx++);

                        planned.Add(new JObject
                        {
                            ["view_name"] = viewName,
                            ["view_type"] = "3d",
                            ["element_id"] = IdCompat.ToInt64(el.Id),
                            ["category"] = el.Category?.Name ?? string.Empty,
                            ["level"] = lvl,
                            ["section_box_mm"] = new JObject
                            {
                                ["min_mm"] = new JArray(padded.MinX, padded.MinY, padded.MinZ),
                                ["max_mm"] = new JArray(padded.MaxX, padded.MaxY, padded.MaxZ)
                            },
                            ["next_calls"] = new JArray
                            {
                                new JObject { ["tool"] = "scio_create_views", ["args"] = new JObject { ["view_type"] = "3d", ["name"] = viewName } },
                                new JObject { ["tool"] = "scio_view_crop", ["args"] = new JObject { ["operation"] = "set_section_box_from_elements", ["element_ids"] = new JArray(IdCompat.ToInt64(el.Id)), ["padding_mm"] = paddingMm } }
                            }
                        });
                    }

                    return ToolResponse.Success(requestId, new JObject
                    {
                        ["preset"] = preset,
                        ["padding_mm"] = paddingMm,
                        ["planned_count"] = planned.Count,
                        ["views"] = planned
                    }, $"Planned {planned.Count} 3D isometric view(s) with section box.");
                }

                case "beam_sections":
                {
                    var beams = (explicitElements ?? new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_StructuralFraming)
                            .WhereElementIsNotElementType()
                            .ToList())
                        .Where(e => MatchesLevel(doc, e, levelFilter))
                        .Take(limit)
                        .ToList();

                    var planned = new JArray();
                    int idx = 1;
                    foreach (var beam in beams)
                    {
                        var bb = beam.get_BoundingBox(null);
                        if (bb == null) continue;

                        double[] startMm;
                        double[] endMm;
                        if (beam.Location is LocationCurve lc && lc.Curve != null)
                        {
                            var p0 = lc.Curve.GetEndPoint(0);
                            var p1 = lc.Curve.GetEndPoint(1);
                            startMm = new[] { UnitKit.FeetToMm(p0.X), UnitKit.FeetToMm(p0.Y), UnitKit.FeetToMm(p0.Z) };
                            endMm = new[] { UnitKit.FeetToMm(p1.X), UnitKit.FeetToMm(p1.Y), UnitKit.FeetToMm(p1.Z) };
                        }
                        else
                        {
                            double midZ = UnitKit.FeetToMm((bb.Min.Z + bb.Max.Z) * 0.5);
                            double spanX = UnitKit.FeetToMm(bb.Max.X - bb.Min.X);
                            double spanY = UnitKit.FeetToMm(bb.Max.Y - bb.Min.Y);
                            if (spanX >= spanY)
                            {
                                double midY = UnitKit.FeetToMm((bb.Min.Y + bb.Max.Y) * 0.5);
                                startMm = new[] { UnitKit.FeetToMm(bb.Min.X), midY, midZ };
                                endMm = new[] { UnitKit.FeetToMm(bb.Max.X), midY, midZ };
                            }
                            else
                            {
                                double midX = UnitKit.FeetToMm((bb.Min.X + bb.Max.X) * 0.5);
                                startMm = new[] { midX, UnitKit.FeetToMm(bb.Min.Y), midZ };
                                endMm = new[] { midX, UnitKit.FeetToMm(bb.Max.Y), midZ };
                            }
                        }

                        double crossW = Math.Min(UnitKit.FeetToMm(bb.Max.X - bb.Min.X), UnitKit.FeetToMm(bb.Max.Y - bb.Min.Y));
                        double crossH = UnitKit.FeetToMm(bb.Max.Z - bb.Min.Z);
                        var frames = PlanimetryMathKit.ComputeBeamSectionFrames(startMm, endMm, crossW, crossH, paddingMm, transverseCount);
                        if (frames.Count == 0) continue;

                        string lvl = ResolveElementLevelName(doc, beam);
                        string tag = ReadMarkOrId(beam);
                        var frameArr = new JArray();
                        int fIdx = 1;
                        foreach (var f in frames)
                        {
                            string vName = PlanimetryMathKit.FormatRevitSafeName(
                                string.IsNullOrWhiteSpace(namePattern) ? "SEC_{level}_{tag}_{preset}_{index}" : namePattern,
                                f.Kind == "longitudinal" ? "LONG" : "TRANS", lvl, tag, string.Empty, fIdx++);
                            frameArr.Add(new JObject
                            {
                                ["view_name"] = vName,
                                ["kind"] = f.Kind,
                                ["fraction"] = f.Fraction,
                                ["origin_mm"] = new JArray(f.OriginMm),
                                ["axis_dir_xy"] = new JArray(f.AxisDirXy),
                                ["view_dir_xy"] = new JArray(f.ViewDirXy),
                                ["width_mm"] = f.WidthMm,
                                ["height_mm"] = f.HeightMm,
                                ["depth_mm"] = f.DepthMm
                            });
                        }

                        planned.Add(new JObject
                        {
                            ["element_id"] = IdCompat.ToInt64(beam.Id),
                            ["tag"] = tag,
                            ["level"] = lvl,
                            ["section_count"] = frameArr.Count,
                            ["sections"] = frameArr,
                            ["index"] = idx++
                        });
                    }

                    return ToolResponse.Success(requestId, new JObject
                    {
                        ["preset"] = preset,
                        ["padding_mm"] = paddingMm,
                        ["transverse_count"] = transverseCount,
                        ["planned_count"] = planned.Count,
                        ["beams"] = planned
                    }, $"Planned longitudinal and transverse sections for {planned.Count} beam(s).");
                }

                case "grid_elevations_sheets":
                {
                    var grids = new FilteredElementCollector(doc)
                        .OfClass(typeof(Grid))
                        .Cast<Grid>()
                        .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(limit)
                        .ToList();

                    var beams = (explicitElements ?? new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_StructuralFraming)
                            .WhereElementIsNotElementType()
                            .ToList())
                        .Where(e => MatchesLevel(doc, e, levelFilter))
                        .ToList();

                    var sheetPlans = new JArray();
                    int sheetIdx = 1;
                    double tolFt = UnitKit.MmToFeet(Math.Max(250.0, paddingMm));

                    foreach (var g in grids)
                    {
                        if (g.Curve == null) continue;
                        var matchedIds = new List<long>();
                        var boxes = new List<Box3DMm>();

                        foreach (var b in beams)
                        {
                            var bb = b.get_BoundingBox(null);
                            if (bb == null) continue;
                            var mid = (bb.Min + bb.Max) * 0.5;
                            var proj = g.Curve.Project(new XYZ(mid.X, mid.Y, g.Curve.GetEndPoint(0).Z));
                            if (proj != null && proj.Distance <= tolFt)
                            {
                                matchedIds.Add(IdCompat.ToInt64(b.Id));
                                boxes.Add(new Box3DMm
                                {
                                    MinX = UnitKit.FeetToMm(bb.Min.X),
                                    MinY = UnitKit.FeetToMm(bb.Min.Y),
                                    MinZ = UnitKit.FeetToMm(bb.Min.Z),
                                    MaxX = UnitKit.FeetToMm(bb.Max.X),
                                    MaxY = UnitKit.FeetToMm(bb.Max.Y),
                                    MaxZ = UnitKit.FeetToMm(bb.Max.Z)
                                });
                            }
                        }

                        if (matchedIds.Count == 0) continue;
                        var padded = PlanimetryMathKit.ComputePaddedBox(boxes, paddingMm)!;
                        string lvlLabel = string.IsNullOrWhiteSpace(levelFilter) ? "ALL" : levelFilter!;
                        string sheetNumber = $"{sheetPrefix}-{sheetIdx:D2}";
                        string sheetName = PlanimetryMathKit.FormatRevitSafeName(
                            string.IsNullOrWhiteSpace(namePattern) ? "GRID_{grid}_{level}" : namePattern,
                            "GRID", lvlLabel, g.Name, g.Name, sheetIdx);
                        string isoName = PlanimetryMathKit.FormatRevitSafeName("ISO_GRID_{grid}_{level}", "ISO", lvlLabel, g.Name, g.Name, sheetIdx);
                        sheetIdx++;

                        sheetPlans.Add(new JObject
                        {
                            ["grid_id"] = IdCompat.ToInt64(g.Id),
                            ["grid_name"] = g.Name,
                            ["sheet_number"] = sheetNumber,
                            ["sheet_name"] = sheetName,
                            ["isometric_view_name"] = isoName,
                            ["matched_element_count"] = matchedIds.Count,
                            ["element_ids"] = new JArray(matchedIds),
                            ["section_box_mm"] = new JObject
                            {
                                ["min_mm"] = new JArray(padded.MinX, padded.MinY, padded.MinZ),
                                ["max_mm"] = new JArray(padded.MaxX, padded.MaxY, padded.MaxZ)
                            },
                            ["next_calls"] = new JArray
                            {
                                new JObject { ["tool"] = "scio_create_views", ["args"] = new JObject { ["view_type"] = "3d", ["name"] = isoName } },
                                new JObject { ["tool"] = "scio_view_crop", ["args"] = new JObject { ["operation"] = "set_section_box_from_elements", ["element_ids"] = new JArray(matchedIds), ["padding_mm"] = paddingMm } },
                                new JObject { ["tool"] = "scio_manage_sheets", ["args"] = new JObject { ["action"] = "create", ["sheet_number"] = sheetNumber, ["sheet_name"] = sheetName } }
                            }
                        });
                    }

                    return ToolResponse.Success(requestId, new JObject
                    {
                        ["preset"] = preset,
                        ["padding_mm"] = paddingMm,
                        ["planned_count"] = sheetPlans.Count,
                        ["sheets"] = sheetPlans
                    }, $"Planned {sheetPlans.Count} grid-grouped sheet set(s).");
                }

                default:
                    return ToolResponse.Refusal(requestId, "invalid_preset",
                        $"Unsupported preset '{preset}'. Supported: isometric_by_host, beam_sections, grid_elevations_sheets.");
            }
        }

        private static bool MatchesLevel(Document doc, Element el, string? levelFilter)
        {
            if (string.IsNullOrWhiteSpace(levelFilter)) return true;
            string lvl = ResolveElementLevelName(doc, el);
            return string.Equals(lvl, levelFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveElementLevelName(Document doc, Element el)
        {
            if (el.LevelId != ElementId.InvalidElementId && doc.GetElement(el.LevelId) is Level l)
                return l.Name;
            var refLvl = el.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
            if (refLvl != null && refLvl != ElementId.InvalidElementId && doc.GetElement(refLvl) is Level rl)
                return rl.Name;
            return "Unassigned";
        }

        private static string ReadMarkOrId(Element el)
        {
            string? mark = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            if (!string.IsNullOrWhiteSpace(mark)) return mark!.Trim();
            return IdCompat.ToInt64(el.Id).ToString();
        }
    }

    [ScioTool("scio_query_dimensions", "documentation", "Inspect existing dimensions in a view: values (mm), segments, overrides, and referenced element IDs.", ReadOnly = true)]
    public sealed class QueryDimensionsCommand : IRevitCommand
    {
        [ScioParam("view_id", "Optional View ElementId (defaults to active view).")]
        public long? ViewId { get; set; }

        [ScioParam("limit", "Maximum dimensions to return (default 200).")]
        public int Limit { get; set; } = 200;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(QueryDimensionsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = ViewResolveKit.ResolveView(doc, request.Arguments["view_id"]);
            int limit = Math.Max(1, Math.Min(1000, request.Arguments.Value<int?>("limit") ?? 200));

            var arr = new JArray();
            foreach (var dim in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>().Take(limit))
            {
                var refIds = new JArray();
                if (dim.References != null)
                {
                    foreach (Reference r in dim.References)
                    {
                        if (r != null && r.ElementId != ElementId.InvalidElementId)
                            refIds.Add(IdCompat.ToInt64(r.ElementId));
                    }
                }

                arr.Add(new JObject
                {
                    ["id"] = IdCompat.ToInt64(dim.Id),
                    ["type_name"] = dim.DimensionType?.Name ?? string.Empty,
                    ["value_mm"] = dim.Value.HasValue ? Math.Round(UnitKit.FeetToMm(dim.Value.Value), 1) : (double?)null,
                    ["segment_count"] = dim.NumberOfSegments,
                    ["value_override"] = dim.ValueOverride ?? string.Empty,
                    ["above"] = dim.Above ?? string.Empty,
                    ["below"] = dim.Below ?? string.Empty,
                    ["prefix"] = dim.Prefix ?? string.Empty,
                    ["suffix"] = dim.Suffix ?? string.Empty,
                    ["referenced_element_ids"] = refIds
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["view_name"] = view.Name,
                ["count"] = arr.Count,
                ["dimensions"] = arr
            }, $"Inspected {arr.Count} dimension(s) in '{view.Name}'.");
        }
    }

    [ScioTool("scio_edit_dimensions", "documentation", "Edit text fields (prefix, suffix, above, below, value_override) on existing dimensions with dry-run preview.", ReadOnly = false)]
    public sealed class EditDimensionsCommand : IRevitCommand
    {
        [ScioParam("dimension_ids", "Dimension ElementIds to edit.", Required = true)]
        public List<long> DimensionIds { get; set; } = new List<long>();

        [ScioParam("prefix", "Optional prefix string.")]
        public string? Prefix { get; set; }

        [ScioParam("suffix", "Optional suffix string.")]
        public string? Suffix { get; set; }

        [ScioParam("above", "Optional above text.")]
        public string? Above { get; set; }

        [ScioParam("below", "Optional below text.")]
        public string? Below { get; set; }

        [ScioParam("value_override", "Optional override text (empty string clears override).")]
        public string? ValueOverride { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(EditDimensionsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var dims = RevitElementKit.ResolveElements(doc, request.Arguments["dimension_ids"] as JArray).OfType<Dimension>().ToList();
            if (dims.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_dimensions", "No valid Dimension elements resolved from 'dimension_ids'.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Edit Dimensions",
                dryRun,
                d =>
                {
                    int updated = 0;
                    foreach (var dim in dims)
                    {
                        if (request.Arguments["prefix"] != null) dim.Prefix = request.Arguments.Value<string>("prefix") ?? string.Empty;
                        if (request.Arguments["suffix"] != null) dim.Suffix = request.Arguments.Value<string>("suffix") ?? string.Empty;
                        if (request.Arguments["above"] != null) dim.Above = request.Arguments.Value<string>("above") ?? string.Empty;
                        if (request.Arguments["below"] != null) dim.Below = request.Arguments.Value<string>("below") ?? string.Empty;
                        if (request.Arguments["value_override"] != null) dim.ValueOverride = request.Arguments.Value<string>("value_override") ?? string.Empty;
                        updated++;
                    }
                    return new JObject { ["updated_count"] = updated };
                },
                (d, staged) => staged,
                $"Updated {dims.Count} dimension(s).");
        }
    }

    [ScioTool("scio_query_detail_2d", "documentation", "Inventory 2D drafting resources (line styles, filled region types, text note types) or 2D elements in a view.", ReadOnly = true)]
    public sealed class QueryDetail2DCommand : IRevitCommand
    {
        [ScioParam("mode", "Inventory mode: resources or elements (default resources).", EnumValues = new[] { "resources", "elements" })]
        public string Mode { get; set; } = "resources";

        [ScioParam("view_id", "Optional View ElementId for mode='elements' (defaults to active view).")]
        public long? ViewId { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(QueryDetail2DCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string mode = (request.Arguments.Value<string>("mode") ?? "resources").Trim().ToLowerInvariant();

            if (mode == "resources")
            {
                var regions = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                    .Select(t => t.Name).OrderBy(n => n).ToList();
                var textTypes = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                    .Select(t => t.Name).OrderBy(n => n).ToList();
                var lineCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                var lineStyles = lineCat?.SubCategories.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList() ?? new List<string>();

                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["mode"] = "resources",
                    ["filled_region_types"] = new JArray(regions),
                    ["text_note_types"] = new JArray(textTypes),
                    ["line_styles"] = new JArray(lineStyles)
                }, $"2D resources: {regions.Count} filled regions, {textTypes.Count} text types, {lineStyles.Count} line styles.");
            }

            var view = ViewResolveKit.ResolveView(doc, request.Arguments["view_id"]);
            int curves = new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement)).Count();
            int filled = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).Count();
            int notes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Count();
            int detailComps = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_DetailComponents).WhereElementIsNotElementType().Count();

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["mode"] = "elements",
                ["view_name"] = view.Name,
                ["detail_curves"] = curves,
                ["filled_regions"] = filled,
                ["text_notes"] = notes,
                ["detail_components"] = detailComps
            }, $"2D elements in '{view.Name}': {curves} curves, {filled} regions, {notes} notes, {detailComps} detail components.");
        }
    }
}
