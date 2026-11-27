using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Documentation
{
    [ScioTool("scio_manage_schedules", "documentation", "List ViewSchedules, read schedule rows as structured JSON, or export a schedule to CSV.", ReadOnly = true)]
    public sealed class ManageSchedulesCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default), read, or export_csv.", EnumValues = new[] { "list", "read", "export_csv" })]
        public string Action { get; set; } = "list";

        [ScioParam("schedule_id", "ViewSchedule ID for read or export_csv.")]
        public long? ScheduleId { get; set; }

        [ScioParam("output_path", "Target CSV path for export_csv.")]
        public string OutputPath { get; set; } = string.Empty;

        [ScioParam("max_rows", "Maximum body rows to return for read (default 200).")]
        public int MaxRows { get; set; } = 200;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageSchedulesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(s => !s.IsTitleblockRevisionSchedule)
                    .OrderBy(s => s.Name)
                    .Select(s => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(s.Id),
                        ["name"] = s.Name,
                        ["field_count"] = s.Definition.GetFieldCount()
                    });
                var arr = new JArray(schedules);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["schedules"] = arr }, $"Found {arr.Count} schedule(s).");
            }

            var sched = RevitElementKit.ResolveElement(doc, request.Arguments["schedule_id"]) as ViewSchedule;
            if (sched == null)
                return ToolResponse.Refusal(request.RequestId, "schedule_not_found", "Provide a valid 'schedule_id'.");

            if (action == "export_csv")
            {
                string outPath = request.Arguments.Value<string>("output_path") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(outPath))
                    return ToolResponse.Refusal(request.RequestId, "missing_path", "Provide 'output_path'.");
                string dir = Path.GetDirectoryName(outPath) ?? Path.GetTempPath();
                Directory.CreateDirectory(dir);
                sched.Export(dir, Path.GetFileName(outPath), new ViewScheduleExportOptions { FieldDelimiter = ",", TextQualifier = ExportTextQualifier.DoubleQuote });
                return ToolResponse.Success(request.RequestId, new JObject { ["schedule"] = sched.Name, ["csv_path"] = outPath }, $"Exported schedule '{sched.Name}' to CSV.");
            }

            int maxRows = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("max_rows") ?? 200));
            var table = sched.GetTableData().GetSectionData(SectionType.Body);
            int nRows = table.NumberOfRows;
            int nCols = table.NumberOfColumns;
            var rows = new JArray();

            for (int r = 0; r < Math.Min(nRows, maxRows); r++)
            {
                var rowArr = new JArray();
                for (int c = 0; c < nCols; c++)
                    rowArr.Add(sched.GetCellText(SectionType.Body, r, c));
                rows.Add(rowArr);
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["schedule"] = sched.Name,
                ["total_rows"] = nRows,
                ["columns"] = nCols,
                ["rows"] = rows
            }, $"Read {rows.Count} of {nRows} row(s) from '{sched.Name}'.");
        }
    }

    [ScioTool("scio_create_schedule", "documentation", "Create a new ViewSchedule (element or material_takeoff) for a category with fields and optional grouping.", ReadOnly = false)]
    public sealed class CreateScheduleCommand : IRevitCommand
    {
        [ScioParam("category", "Target category name (e.g. Walls, Doors, Structural Columns, or 'Multi-Category' for material_takeoff).", Required = true)]
        public string CategoryName { get; set; } = string.Empty;

        [ScioParam("name", "Schedule name.", Required = true)]
        public string Name { get; set; } = string.Empty;

        [ScioParam("schedule_kind", "Schedule kind: element (default) or material_takeoff.", EnumValues = new[] { "element", "material_takeoff" })]
        public string ScheduleKind { get; set; } = "element";

        [ScioParam("fields", "List of schedulable parameter field names to add.")]
        public List<string>? Fields { get; set; }

        [ScioParam("group_by", "Optional field name (from 'fields') to sort/group the schedule by.")]
        public string? GroupBy { get; set; }

        [ScioParam("itemized", "Itemize every instance (default true).")]
        public bool Itemized { get; set; } = true;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateScheduleCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string catArg = (request.Arguments.Value<string>("category") ?? string.Empty).Trim();
            string name = request.Arguments.Value<string>("name") ?? string.Empty;
            string kind = (request.Arguments.Value<string>("schedule_kind") ?? "element").Trim().ToLowerInvariant();
            bool isMultiCat = string.Equals(catArg, "Multi-Category", StringComparison.OrdinalIgnoreCase);

            ElementId catId;
            if (isMultiCat)
            {
                catId = ElementId.InvalidElementId;
            }
            else
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, catArg);
                if (cat == null)
                    return ToolResponse.Refusal(request.RequestId, "invalid_request", $"Category '{catArg}' not found.");
                catId = cat.Id;
            }

            if (string.IsNullOrWhiteSpace(name))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide a valid 'name'.");

            var wanted = (request.Arguments["fields"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string>();
            string? groupBy = request.Arguments.Value<string>("group_by")?.Trim();
            bool itemized = request.Arguments.Value<bool?>("itemized") ?? true;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Create Schedule {name}",
                dryRun,
                d =>
                {
                    var vs = kind == "material_takeoff"
                        ? ViewSchedule.CreateMaterialTakeoff(d, catId)
                        : ViewSchedule.CreateSchedule(d, catId);
                    vs.Name = name;
                    vs.Definition.IsItemized = itemized;
                    var avail = vs.Definition.GetSchedulableFields();
                    var added = new JArray();
                    ScheduleField? groupField = null;
                    foreach (string fName in wanted)
                    {
                        var sf = avail.FirstOrDefault(x => string.Equals(x.GetName(d), fName, StringComparison.OrdinalIgnoreCase));
                        if (sf != null)
                        {
                            var field = vs.Definition.AddField(sf);
                            added.Add(fName);
                            if (!string.IsNullOrWhiteSpace(groupBy) && string.Equals(fName, groupBy, StringComparison.OrdinalIgnoreCase))
                                groupField = field;
                        }
                    }

                    if (groupField != null)
                    {
                        var sgf = new ScheduleSortGroupField(groupField.FieldId) { ShowHeader = true, ShowFooter = true };
                        vs.Definition.AddSortGroupField(sgf);
                    }

                    return new JObject
                    {
                        ["id"] = IdCompat.ToInt64(vs.Id),
                        ["name"] = vs.Name,
                        ["schedule_kind"] = kind,
                        ["itemized"] = vs.Definition.IsItemized,
                        ["grouped_by"] = groupField != null ? groupBy : null,
                        ["added_fields"] = added
                    };
                },
                (d, staged) =>
                {
                    staged["verified"] = RevitElementKit.ResolveElement(d, staged["id"]) is ViewSchedule;
                    return staged;
                },
                $"Created {kind} schedule '{name}'.");
        }
    }

    [ScioTool("scio_create_dimensions", "documentation", "Create a linear dimension string across grids or parallel walls in the active view.", ReadOnly = false)]
    public sealed class CreateDimensionsCommand : IRevitCommand
    {
        [ScioParam("grid_ids", "ElementIds of parallel Grids to dimension.", Required = true)]
        public List<long> GridIds { get; set; } = new List<long>();

        [ScioParam("line_start_mm", "Dimension line start [x, y] in mm.", Required = true)]
        public List<double> LineStartMm { get; set; } = new List<double>();

        [ScioParam("line_end_mm", "Dimension line end [x, y] in mm.", Required = true)]
        public List<double> LineEndMm { get; set; } = new List<double>();

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateDimensionsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = context.ActiveUiDocument?.ActiveView;
            if (view == null) return ToolResponse.Refusal(request.RequestId, "no_view", "No active view.");

            var grids = RevitElementKit.ResolveElements(doc, request.Arguments["grid_ids"] as JArray).OfType<Grid>().ToList();
            var p0 = request.Arguments["line_start_mm"] as JArray;
            var p1 = request.Arguments["line_end_mm"] as JArray;
            if (grids.Count < 2 || p0 == null || p1 == null || p0.Count < 2 || p1.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide at least 2 grid_ids and line_start_mm/line_end_mm.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Grid Dimensions",
                dryRun,
                d =>
                {
                    var refs = new ReferenceArray();
                    foreach (var g in grids) refs.Append(new Reference(g));
                    var line = Line.CreateBound(
                        new XYZ(UnitKit.MmToFeet(p0[0].Value<double>()), UnitKit.MmToFeet(p0[1].Value<double>()), 0),
                        new XYZ(UnitKit.MmToFeet(p1[0].Value<double>()), UnitKit.MmToFeet(p1[1].Value<double>()), 0));
                    var dim = d.Create.NewDimension(view, line, refs);
                    return new JObject { ["dimension_id"] = IdCompat.ToInt64(dim.Id), ["segments"] = dim.NumberOfSegments };
                },
                (d, staged) => staged,
                $"Dimensioned {grids.Count} grid(s).");
        }
    }

    [ScioTool("scio_manage_tags", "documentation", "Audit untagged elements in the active view or place IndependentTags on specified elements.", ReadOnly = false)]
    public sealed class ManageTagsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: audit_untagged (default) or tag_elements.", EnumValues = new[] { "audit_untagged", "tag_elements" })]
        public string Action { get; set; } = "audit_untagged";

        [ScioParam("category", "Category name for audit_untagged.")]
        public string CategoryName { get; set; } = string.Empty;

        [ScioParam("element_ids", "Element IDs to tag for tag_elements.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("add_leader", "Include leader line when tagging (default false).")]
        public bool AddLeader { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageTagsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = context.ActiveUiDocument?.ActiveView;
            if (view == null) return ToolResponse.Refusal(request.RequestId, "no_view", "No active view.");

            string action = (request.Arguments.Value<string>("action") ?? "audit_untagged").Trim().ToLowerInvariant();
            if (action == "audit_untagged")
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, request.Arguments.Value<string>("category"));
                var taggedIds = new HashSet<long>();
                foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                {
                    foreach (var tid in tag.GetTaggedLocalElementIds())
                        taggedIds.Add(IdCompat.ToInt64(tid));
                }

                var col = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                if (cat != null) col = col.OfCategoryId(cat.Id);

                var untagged = new JArray();
                int total = 0;
                foreach (Element el in col)
                {
                    if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                    total++;
                    long id = IdCompat.ToInt64(el.Id);
                    if (!taggedIds.Contains(id) && untagged.Count < 200)
                        untagged.Add(new JObject { ["id"] = id, ["category"] = el.Category.Name, ["name"] = el.Name });
                }
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["view"] = view.Name,
                    ["total_visible"] = total,
                    ["tagged_count"] = taggedIds.Count,
                    ["untagged_sample"] = untagged
                }, $"View '{view.Name}': {untagged.Count} untagged element(s) sampled.");
            }

            var elems = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elems.Count == 0) return ToolResponse.Refusal(request.RequestId, "no_elements", "Provide valid 'element_ids'.");

            bool leader = request.Arguments.Value<bool?>("add_leader") ?? false;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Tag {elems.Count} Element(s)",
                dryRun,
                d =>
                {
                    var created = new JArray();
                    foreach (var el in elems)
                    {
                        var bb = el.get_BoundingBox(view) ?? el.get_BoundingBox(null);
                        if (bb == null) continue;
                        var mid = (bb.Min + bb.Max) * 0.5;
                        var tag = IndependentTag.Create(d, view.Id, new Reference(el), leader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, mid);
                        if (tag != null) created.Add(IdCompat.ToInt64(tag.Id));
                    }
                    return new JObject { ["tagged_count"] = created.Count, ["tag_ids"] = created };
                },
                (d, staged) => staged,
                $"Tagged {elems.Count} element(s) in '{view.Name}'.");
        }
    }

    [ScioTool("scio_create_text_notes", "documentation", "Create a TextNote at an [x, y] mm point in the active view.", ReadOnly = false)]
    public sealed class CreateTextNotesCommand : IRevitCommand
    {
        [ScioParam("text", "Text content.", Required = true)]
        public string Text { get; set; } = string.Empty;

        [ScioParam("point_mm", "Insertion point [x, y] in mm.", Required = true)]
        public List<double> PointMm { get; set; } = new List<double>();

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateTextNotesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = context.ActiveUiDocument?.ActiveView;
            string text = request.Arguments.Value<string>("text") ?? string.Empty;
            var ptArr = request.Arguments["point_mm"] as JArray;
            if (view == null || string.IsNullOrWhiteSpace(text) || ptArr == null || ptArr.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Active view, 'text', and 'point_mm' are required.");

            var typeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create TextNote",
                dryRun,
                d =>
                {
                    var pt = new XYZ(UnitKit.MmToFeet(ptArr[0].Value<double>()), UnitKit.MmToFeet(ptArr[1].Value<double>()), 0);
                    var note = TextNote.Create(d, view.Id, pt, text, typeId);
                    return new JObject { ["id"] = IdCompat.ToInt64(note.Id), ["view"] = view.Name };
                },
                (d, staged) => staged,
                $"Created TextNote in '{view.Name}'.");
        }
    }

    [ScioTool("scio_create_detail_lines", "documentation", "Create 2D detail lines in the active view from [start_mm, end_mm] segments.", ReadOnly = false)]
    public sealed class CreateDetailLinesCommand : IRevitCommand
    {
        [ScioParam("segments", "Array of { start_mm: [x,y], end_mm: [x,y] }.", Required = true)]
        public JArray Segments { get; set; } = new JArray();

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateDetailLinesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = context.ActiveUiDocument?.ActiveView;
            var segs = request.Arguments["segments"] as JArray;
            if (view == null || segs == null || segs.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Active view and non-empty 'segments' are required.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Detail Lines",
                dryRun,
                d =>
                {
                    var ids = new JArray();
                    foreach (var s in segs.OfType<JObject>())
                    {
                        var a = s["start_mm"] as JArray;
                        var b = s["end_mm"] as JArray;
                        if (a == null || b == null || a.Count < 2 || b.Count < 2) continue;
                        var p0 = new XYZ(UnitKit.MmToFeet(a[0].Value<double>()), UnitKit.MmToFeet(a[1].Value<double>()), 0);
                        var p1 = new XYZ(UnitKit.MmToFeet(b[0].Value<double>()), UnitKit.MmToFeet(b[1].Value<double>()), 0);
                        if (p0.DistanceTo(p1) < 1e-4) continue;
                        var dc = d.Create.NewDetailCurve(view, Line.CreateBound(p0, p1));
                        ids.Add(IdCompat.ToInt64(dc.Id));
                    }
                    return new JObject { ["created_count"] = ids.Count, ["ids"] = ids };
                },
                (d, staged) => staged,
                $"Created detail lines in '{view.Name}'.");
        }
    }

    [ScioTool("scio_create_filled_regions", "documentation", "Create a 2D FilledRegion in the active view from a closed polygon of [x, y] mm points.", ReadOnly = false)]
    public sealed class CreateFilledRegionsCommand : IRevitCommand
    {
        [ScioParam("boundary_mm", "Ordered array of [x, y] mm points forming a closed polygon.", Required = true)]
        public JArray BoundaryMm { get; set; } = new JArray();

        [ScioParam("type_name", "Optional FilledRegionType name.")]
        public string TypeName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateFilledRegionsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = context.ActiveUiDocument?.ActiveView;
            var pts = request.Arguments["boundary_mm"] as JArray;
            if (view == null || pts == null || pts.Count < 3)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Active view and >=3 'boundary_mm' points required.");

            string? tName = request.Arguments.Value<string>("type_name");
            var frt = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .FirstOrDefault(t => string.IsNullOrWhiteSpace(tName) || string.Equals(t.Name, tName, StringComparison.OrdinalIgnoreCase));
            if (frt == null) return ToolResponse.Refusal(request.RequestId, "no_type", "No FilledRegionType found.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create FilledRegion",
                dryRun,
                d =>
                {
                    var loop = new CurveLoop();
                    var xyz = pts.Select(p => new XYZ(UnitKit.MmToFeet(p[0]!.Value<double>()), UnitKit.MmToFeet(p[1]!.Value<double>()), 0)).ToList();
                    for (int i = 0; i < xyz.Count; i++)
                        loop.Append(Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]));
                    var fr = FilledRegion.Create(d, frt.Id, view.Id, new List<CurveLoop> { loop });
                    return new JObject { ["id"] = IdCompat.ToInt64(fr.Id), ["type"] = frt.Name };
                },
                (d, staged) => staged,
                $"Created FilledRegion '{frt.Name}' in '{view.Name}'.");
        }
    }

    [ScioTool("scio_manage_keynote_legends", "documentation", "List legend views and keynote schedules in the document.", ReadOnly = true)]
    public sealed class ManageKeynoteLegendsCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageKeynoteLegendsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend)
                .Select(v => new JObject { ["id"] = IdCompat.ToInt64(v.Id), ["name"] = v.Name, ["scale"] = v.Scale });
            var arr = new JArray(legends);
            return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["legends"] = arr }, $"Found {arr.Count} legend view(s).");
        }
    }

    [ScioTool("scio_manage_callouts", "documentation", "Create a rectangular callout view on a parent plan/section view using [min_mm, max_mm].", ReadOnly = false)]
    public sealed class ManageCalloutsCommand : IRevitCommand
    {
        [ScioParam("parent_view_id", "Parent View ElementId.", Required = true)]
        public long ParentViewId { get; set; }

        [ScioParam("min_mm", "Lower-left corner [x, y] in mm.", Required = true)]
        public List<double> MinMm { get; set; } = new List<double>();

        [ScioParam("max_mm", "Upper-right corner [x, y] in mm.", Required = true)]
        public List<double> MaxMm { get; set; } = new List<double>();

        [ScioParam("name", "Optional name for the new callout view.")]
        public string Name { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageCalloutsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var parent = RevitElementKit.ResolveElement(doc, request.Arguments["parent_view_id"]) as View;
            var pMin = request.Arguments["min_mm"] as JArray;
            var pMax = request.Arguments["max_mm"] as JArray;
            if (parent == null || pMin == null || pMax == null || pMin.Count < 2 || pMax.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide parent_view_id, min_mm, and max_mm.");

            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Detail || t.ViewFamily == ViewFamily.FloorPlan);
            if (vft == null) return ToolResponse.Refusal(request.RequestId, "no_vft", "No suitable ViewFamilyType found.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Callout",
                dryRun,
                d =>
                {
                    var callout = ViewSection.CreateCallout(
                        d, parent.Id, vft.Id,
                        new XYZ(UnitKit.MmToFeet(pMin[0].Value<double>()), UnitKit.MmToFeet(pMin[1].Value<double>()), 0),
                        new XYZ(UnitKit.MmToFeet(pMax[0].Value<double>()), UnitKit.MmToFeet(pMax[1].Value<double>()), 0));
                    string? nm = request.Arguments.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(nm)) callout.Name = nm;
                    return new JObject { ["callout_id"] = IdCompat.ToInt64(callout.Id), ["name"] = callout.Name };
                },
                (d, staged) => staged,
                $"Created callout on '{parent.Name}'.");
        }
    }

    [ScioTool("scio_manage_scope_boxes", "documentation", "List Scope Boxes in the model or assign a Scope Box to target views.", ReadOnly = false)]
    public sealed class ManageScopeBoxesCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or assign.", EnumValues = new[] { "list", "assign" })]
        public string Action { get; set; } = "list";

        [ScioParam("scope_box_id", "Scope Box ElementId for assign.")]
        public long? ScopeBoxId { get; set; }

        [ScioParam("view_ids", "Target View IDs for assign.")]
        public List<long>? ViewIds { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageScopeBoxesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();
            if (action == "list")
            {
                var boxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                    .Select(b => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(b.Id),
                        ["name"] = b.Name,
                        ["bbox_mm"] = RevitElementKit.SerializeBoundingBoxMm(b.get_BoundingBox(null))
                    });
                var arr = new JArray(boxes);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["scope_boxes"] = arr }, $"Found {arr.Count} scope box(es).");
            }

            var sb = RevitElementKit.ResolveElement(doc, request.Arguments["scope_box_id"]);
            var views = RevitElementKit.ResolveElements(doc, request.Arguments["view_ids"] as JArray).OfType<View>().ToList();
            if (sb == null || views.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide valid 'scope_box_id' and 'view_ids'.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Assign Scope Box",
                dryRun,
                d =>
                {
                    int updated = 0;
                    foreach (var v in views)
                    {
                        var p = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                        if (p != null && !p.IsReadOnly) { p.Set(sb.Id); updated++; }
                    }
                    return new JObject { ["scope_box"] = sb.Name, ["updated_views"] = updated };
                },
                (d, staged) => staged,
                $"Assigned scope box '{sb.Name}' to {views.Count} view(s).");
        }
    }

    [ScioTool("scio_revision_clouds", "documentation", "List RevisionClouds or create a rectangular RevisionCloud in mm on the active view/sheet.", ReadOnly = false)]
    public sealed class RevisionCloudsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or create.", EnumValues = new[] { "list", "create" })]
        public string Action { get; set; } = "list";

        [ScioParam("revision_id", "Revision ID for create (defaults to latest revision).")]
        public long? RevisionId { get; set; }

        [ScioParam("min_mm", "Lower-left corner [x, y] in mm for create.")]
        public List<double>? MinMm { get; set; }

        [ScioParam("max_mm", "Upper-right corner [x, y] in mm for create.")]
        public List<double>? MaxMm { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RevisionCloudsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();
            if (action == "list")
            {
                var clouds = new FilteredElementCollector(doc).OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>()
                    .Select(c => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(c.Id),
                        ["revision_id"] = IdCompat.ToInt64(c.RevisionId),
                        ["owner_view_id"] = IdCompat.ToInt64(c.OwnerViewId)
                    });
                var arr = new JArray(clouds);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["clouds"] = arr }, $"Found {arr.Count} revision cloud(s).");
            }

            var view = context.ActiveUiDocument?.ActiveView;
            var pMin = request.Arguments["min_mm"] as JArray;
            var pMax = request.Arguments["max_mm"] as JArray;
            if (view == null || pMin == null || pMax == null || pMin.Count < 2 || pMax.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Active view, min_mm, and max_mm required.");

            long? revIdArg = request.Arguments.Value<long?>("revision_id");
            var rev = revIdArg.HasValue
                ? doc.GetElement(IdCompat.FromInt64(revIdArg.Value)) as Revision
                : new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().OrderByDescending(r => r.SequenceNumber).FirstOrDefault();
            if (rev == null) return ToolResponse.Refusal(request.RequestId, "no_revision", "No project Revision found.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create RevisionCloud",
                dryRun,
                d =>
                {
                    double x0 = UnitKit.MmToFeet(pMin[0].Value<double>()), y0 = UnitKit.MmToFeet(pMin[1].Value<double>());
                    double x1 = UnitKit.MmToFeet(pMax[0].Value<double>()), y1 = UnitKit.MmToFeet(pMax[1].Value<double>());
                    var curves = new List<Curve>
                    {
                        Line.CreateBound(new XYZ(x0, y0, 0), new XYZ(x1, y0, 0)),
                        Line.CreateBound(new XYZ(x1, y0, 0), new XYZ(x1, y1, 0)),
                        Line.CreateBound(new XYZ(x1, y1, 0), new XYZ(x0, y1, 0)),
                        Line.CreateBound(new XYZ(x0, y1, 0), new XYZ(x0, y0, 0))
                    };
                    var cloud = RevisionCloud.Create(d, view, rev.Id, curves);
                    return new JObject { ["id"] = IdCompat.ToInt64(cloud.Id), ["revision_id"] = IdCompat.ToInt64(rev.Id) };
                },
                (d, staged) => staged,
                $"Created RevisionCloud in '{view.Name}'.");
        }
    }
}
