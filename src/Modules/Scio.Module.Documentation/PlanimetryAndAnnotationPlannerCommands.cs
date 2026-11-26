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

    [ScioTool("scio_plan_views", "documentation", "Read-only deliverable view planner: room_views, unplaced_views, and deliverable_set readiness.", ReadOnly = true)]
    public sealed class PlanViewsCommand : IRevitCommand
    {
        [ScioParam("operation", "Planning mode.", Required = true, EnumValues = new[] { "room_views", "unplaced_views", "deliverable_set" })]
        public string Operation { get; set; } = "deliverable_set";

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PlanViewsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "deliverable_set").Trim().ToLowerInvariant();

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
