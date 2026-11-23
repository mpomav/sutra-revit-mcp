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
    [ScioTool("scio_create_views", "documentation", "Create Revit views (floor_plan, ceiling_plan, 3d, drafting, section) with dry-run preview.", ReadOnly = false)]
    public sealed class CreateViewsCommand : IRevitCommand
    {
        [ScioParam("kind", "View kind to create.", Required = true, EnumValues = new[] { "floor_plan", "ceiling_plan", "3d", "drafting", "section" })]
        public string Kind { get; set; } = "floor_plan";

        [ScioParam("name", "Optional name for the new view.")]
        public string Name { get; set; } = string.Empty;

        [ScioParam("level", "Level name for floor_plan or ceiling_plan.")]
        public string LevelName { get; set; } = string.Empty;

        [ScioParam("origin_mm", "Section origin [x,y,z] in mm for section.")]
        public List<double>? OriginMm { get; set; }

        [ScioParam("width_mm", "Section width in mm (default 10000).")]
        public double WidthMm { get; set; } = 10000;

        [ScioParam("height_mm", "Section height in mm (default 4000).")]
        public double HeightMm { get; set; } = 4000;

        [ScioParam("depth_mm", "Section far clip depth in mm (default 5000).")]
        public double DepthMm { get; set; } = 5000;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateViewsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string kind = (request.Arguments.Value<string>("kind") ?? "floor_plan").Trim().ToLowerInvariant();
            string? viewName = request.Arguments.Value<string>("name");
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Create View ({kind})",
                dryRun,
                d =>
                {
                    View created;
                    switch (kind)
                    {
                        case "floor_plan":
                        case "ceiling_plan":
                        {
                            var vft = new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                                .FirstOrDefault(t => t.ViewFamily == (kind == "floor_plan" ? ViewFamily.FloorPlan : ViewFamily.CeilingPlan))
                                ?? throw new InvalidOperationException($"No ViewFamilyType for {kind}.");
                            string? lvlName = request.Arguments.Value<string>("level");
                            var lvl = new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>()
                                .OrderBy(l => l.Elevation)
                                .FirstOrDefault(l => string.IsNullOrWhiteSpace(lvlName) || string.Equals(l.Name, lvlName, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException("Target level not found.");
                            created = ViewPlan.Create(d, vft.Id, lvl.Id);
                            break;
                        }
                        case "3d":
                        {
                            var vft = new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                                ?? throw new InvalidOperationException("No 3D ViewFamilyType found.");
                            created = View3D.CreateIsometric(d, vft.Id);
                            break;
                        }
                        case "drafting":
                        {
                            var vft = new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting)
                                ?? throw new InvalidOperationException("No Drafting ViewFamilyType found.");
                            created = ViewDrafting.Create(d, vft.Id);
                            break;
                        }
                        case "section":
                        {
                            var vft = new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Section)
                                ?? throw new InvalidOperationException("No Section ViewFamilyType found.");
                            var oArr = request.Arguments["origin_mm"] as JArray;
                            var origin = oArr != null && oArr.Count >= 3
                                ? new XYZ(UnitKit.MmToFeet(oArr[0].Value<double>()), UnitKit.MmToFeet(oArr[1].Value<double>()), UnitKit.MmToFeet(oArr[2].Value<double>()))
                                : XYZ.Zero;
                            double hw = UnitKit.MmToFeet((request.Arguments.Value<double?>("width_mm") ?? 10000.0) / 2.0);
                            double hh = UnitKit.MmToFeet((request.Arguments.Value<double?>("height_mm") ?? 4000.0) / 2.0);
                            double depth = UnitKit.MmToFeet(request.Arguments.Value<double?>("depth_mm") ?? 5000.0);
                            var t = Transform.Identity;
                            t.Origin = origin;
                            t.BasisX = XYZ.BasisX;
                            t.BasisY = XYZ.BasisZ;
                            t.BasisZ = -XYZ.BasisY;
                            var box = new BoundingBoxXYZ { Transform = t, Min = new XYZ(-hw, -hh, 0), Max = new XYZ(hw, hh, depth) };
                            created = ViewSection.CreateSection(d, vft.Id, box);
                            break;
                        }
                        default:
                            throw new ArgumentException($"Unsupported view kind '{kind}'.");
                    }

                    if (!string.IsNullOrWhiteSpace(viewName)) created.Name = viewName;
                    return new JObject
                    {
                        ["id"] = IdCompat.ToInt64(created.Id),
                        ["name"] = created.Name,
                        ["view_type"] = created.ViewType.ToString()
                    };
                },
                (d, staged) =>
                {
                    staged["verified"] = RevitElementKit.ResolveElement(d, staged["id"]) != null;
                    return staged;
                },
                $"Created {kind} view.");
        }
    }

    [ScioTool("scio_manage_views", "documentation", "Duplicate, rename, set scale, or set detail level on one or more views with dry-run support.", ReadOnly = false)]
    public sealed class ManageViewsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: duplicate, rename, set_scale, or set_detail.", Required = true, EnumValues = new[] { "duplicate", "rename", "set_scale", "set_detail" })]
        public string Action { get; set; } = "set_scale";

        [ScioParam("view_id", "Target View ElementId.", Required = true)]
        public long ViewId { get; set; }

        [ScioParam("new_name", "New view name for rename or duplicate.")]
        public string NewName { get; set; } = string.Empty;

        [ScioParam("scale", "View scale integer (e.g. 50, 100) for set_scale.")]
        public int? Scale { get; set; }

        [ScioParam("detail_level", "Detail level for set_detail.", EnumValues = new[] { "coarse", "medium", "fine" })]
        public string DetailLevel { get; set; } = "medium";

        [ScioParam("duplicate_mode", "For duplicate: duplicate, with_detailing, or as_dependent.", EnumValues = new[] { "duplicate", "with_detailing", "as_dependent" })]
        public string DuplicateMode { get; set; } = "with_detailing";

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageViewsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var view = RevitElementKit.ResolveElement(doc, request.Arguments["view_id"]) as View;
            if (view == null)
                return ToolResponse.Refusal(request.RequestId, "view_not_found", "Target view not found.");

            string action = (request.Arguments.Value<string>("action") ?? "set_scale").Trim().ToLowerInvariant();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Manage View ({action})",
                dryRun,
                d =>
                {
                    switch (action)
                    {
                        case "duplicate":
                        {
                            string mode = (request.Arguments.Value<string>("duplicate_mode") ?? "with_detailing").ToLowerInvariant();
                            ViewDuplicateOption opt = mode == "as_dependent" ? ViewDuplicateOption.AsDependent
                                : (mode == "duplicate" ? ViewDuplicateOption.Duplicate : ViewDuplicateOption.WithDetailing);
                            ElementId newId = view.Duplicate(opt);
                            var dup = (View)d.GetElement(newId);
                            string? nm = request.Arguments.Value<string>("new_name");
                            if (!string.IsNullOrWhiteSpace(nm)) dup.Name = nm;
                            return new JObject { ["new_view_id"] = IdCompat.ToInt64(dup.Id), ["name"] = dup.Name };
                        }
                        case "rename":
                        {
                            string nm = request.Arguments.Value<string>("new_name") ?? throw new ArgumentException("new_name required.");
                            view.Name = nm;
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["name"] = view.Name };
                        }
                        case "set_scale":
                        {
                            int sc = request.Arguments.Value<int?>("scale") ?? throw new ArgumentException("scale required.");
                            view.Scale = sc;
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["scale"] = view.Scale };
                        }
                        case "set_detail":
                        {
                            string dl = (request.Arguments.Value<string>("detail_level") ?? "medium").ToLowerInvariant();
                            view.DetailLevel = dl == "coarse" ? ViewDetailLevel.Coarse : (dl == "fine" ? ViewDetailLevel.Fine : ViewDetailLevel.Medium);
                            return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["detail_level"] = view.DetailLevel.ToString() };
                        }
                        default:
                            throw new ArgumentException($"Unsupported action '{action}'.");
                    }
                },
                (d, staged) => staged,
                $"Executed '{action}' on view '{view.Name}'.");
        }
    }

    [ScioTool("scio_view_filters", "documentation", "List ParameterFilterElements, or add/remove/toggle a filter on a target view.", ReadOnly = false)]
    public sealed class ViewFiltersCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default), apply, or remove.", EnumValues = new[] { "list", "apply", "remove" })]
        public string Action { get; set; } = "list";

        [ScioParam("view_id", "Target View ElementId for apply/remove.")]
        public long? ViewId { get; set; }

        [ScioParam("filter_id", "ParameterFilterElement ID for apply/remove.")]
        public long? FilterId { get; set; }

        [ScioParam("visible", "Visibility flag for apply (default true).")]
        public bool Visible { get; set; } = true;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ViewFiltersCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var filters = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                    .Select(f => new JObject { ["id"] = IdCompat.ToInt64(f.Id), ["name"] = f.Name });
                var arr = new JArray(filters);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["filters"] = arr }, $"Found {arr.Count} view filter(s).");
            }

            var view = RevitElementKit.ResolveElement(doc, request.Arguments["view_id"]) as View;
            var filter = RevitElementKit.ResolveElement(doc, request.Arguments["filter_id"]) as ParameterFilterElement;
            if (view == null || filter == null)
                return ToolResponse.Refusal(request.RequestId, "not_found", "Valid 'view_id' and 'filter_id' are required.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            bool visible = request.Arguments.Value<bool?>("visible") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"View Filter ({action})",
                dryRun,
                d =>
                {
                    if (action == "remove")
                    {
                        if (view.IsFilterApplied(filter.Id)) view.RemoveFilter(filter.Id);
                    }
                    else
                    {
                        if (!view.IsFilterApplied(filter.Id)) view.AddFilter(filter.Id);
                        view.SetFilterVisibility(filter.Id, visible);
                    }
                    return new JObject { ["view_id"] = IdCompat.ToInt64(view.Id), ["filter_id"] = IdCompat.ToInt64(filter.Id), ["action"] = action };
                },
                (d, staged) => staged,
                $"Filter '{filter.Name}' {action} on '{view.Name}'.");
        }
    }

    [ScioTool("scio_view_templates", "documentation", "List view templates or assign a view template to one or more views.", ReadOnly = false)]
    public sealed class ViewTemplatesCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or assign.", EnumValues = new[] { "list", "assign" })]
        public string Action { get; set; } = "list";

        [ScioParam("template_name", "Template name to assign (or 'None' to clear).")]
        public string TemplateName { get; set; } = string.Empty;

        [ScioParam("view_ids", "Target view IDs for assign.")]
        public List<long>? ViewIds { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ViewTemplatesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).OrderBy(v => v.Name).ToList();
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var arr = new JArray(templates.Select(t => new JObject
                {
                    ["id"] = IdCompat.ToInt64(t.Id),
                    ["name"] = t.Name,
                    ["view_type"] = t.ViewType.ToString()
                }));
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["templates"] = arr }, $"Found {arr.Count} view template(s).");
            }

            string tplName = request.Arguments.Value<string>("template_name") ?? string.Empty;
            bool clear = string.Equals(tplName, "None", StringComparison.OrdinalIgnoreCase);
            View? tpl = clear ? null : templates.FirstOrDefault(t => string.Equals(t.Name, tplName, StringComparison.OrdinalIgnoreCase));
            if (!clear && tpl == null)
                return ToolResponse.Refusal(request.RequestId, "template_not_found", $"View template '{tplName}' not found.");

            var views = RevitElementKit.ResolveElements(doc, request.Arguments["view_ids"] as JArray).OfType<View>().ToList();
            if (views.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_views", "No valid target views found.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Assign View Template",
                dryRun,
                d =>
                {
                    int updated = 0;
                    foreach (var v in views)
                    {
                        v.ViewTemplateId = clear ? ElementId.InvalidElementId : tpl!.Id;
                        updated++;
                    }
                    return new JObject { ["template"] = clear ? "None" : tpl!.Name, ["updated_views"] = updated };
                },
                (d, staged) => staged,
                $"Assigned template '{(clear ? "None" : tpl!.Name)}' to {views.Count} view(s).");
        }
    }

    [ScioTool("scio_manage_sheets", "documentation", "Create sheets with titleblock, rename/renumber sheets, or place a view onto a sheet at mm coordinates.", ReadOnly = false)]
    public sealed class ManageSheetsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: create, place_view, or update.", Required = true, EnumValues = new[] { "create", "place_view", "update" })]
        public string Action { get; set; } = "create";

        [ScioParam("sheet_number", "Sheet number (e.g. A-101).")]
        public string SheetNumber { get; set; } = string.Empty;

        [ScioParam("sheet_name", "Sheet title.")]
        public string SheetName { get; set; } = string.Empty;

        [ScioParam("titleblock_name", "Optional TitleBlock Family/Type substring for create.")]
        public string TitleblockName { get; set; } = string.Empty;

        [ScioParam("sheet_id", "Target Sheet ID for place_view or update.")]
        public long? SheetId { get; set; }

        [ScioParam("view_id", "View ID to place on sheet for place_view.")]
        public long? ViewId { get; set; }

        [ScioParam("point_mm", "Placement point [x, y] in mm on sheet (default [300, 200]).")]
        public List<double>? PointMm { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageSheetsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "create").Trim().ToLowerInvariant();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Sheets ({action})",
                dryRun,
                d =>
                {
                    if (action == "create")
                    {
                        string tbFilter = request.Arguments.Value<string>("titleblock_name") ?? string.Empty;
                        var tb = new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                            .FirstOrDefault(s => string.IsNullOrWhiteSpace(tbFilter) ||
                                                 s.Name.IndexOf(tbFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 s.FamilyName.IndexOf(tbFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                        var sheet = ViewSheet.Create(d, tb?.Id ?? ElementId.InvalidElementId);
                        string? num = request.Arguments.Value<string>("sheet_number");
                        string? nm = request.Arguments.Value<string>("sheet_name");
                        if (!string.IsNullOrWhiteSpace(num)) sheet.SheetNumber = num;
                        if (!string.IsNullOrWhiteSpace(nm)) sheet.Name = nm;
                        return new JObject { ["sheet_id"] = IdCompat.ToInt64(sheet.Id), ["number"] = sheet.SheetNumber, ["name"] = sheet.Name };
                    }

                    var targetSheet = RevitElementKit.ResolveElement(d, request.Arguments["sheet_id"]) as ViewSheet
                        ?? throw new InvalidOperationException("Target sheet_id not found.");

                    if (action == "update")
                    {
                        string? num = request.Arguments.Value<string>("sheet_number");
                        string? nm = request.Arguments.Value<string>("sheet_name");
                        if (!string.IsNullOrWhiteSpace(num)) targetSheet.SheetNumber = num;
                        if (!string.IsNullOrWhiteSpace(nm)) targetSheet.Name = nm;
                        return new JObject { ["sheet_id"] = IdCompat.ToInt64(targetSheet.Id), ["number"] = targetSheet.SheetNumber, ["name"] = targetSheet.Name };
                    }

                    var view = RevitElementKit.ResolveElement(d, request.Arguments["view_id"]) as View
                        ?? throw new InvalidOperationException("Target view_id not found.");
                    if (!Viewport.CanAddViewToSheet(d, targetSheet.Id, view.Id))
                        throw new InvalidOperationException($"View '{view.Name}' cannot be added to sheet '{targetSheet.SheetNumber}' (already placed or unsupported).");

                    var ptArr = request.Arguments["point_mm"] as JArray;
                    double xMm = ptArr != null && ptArr.Count >= 2 ? ptArr[0].Value<double>() : 300.0;
                    double yMm = ptArr != null && ptArr.Count >= 2 ? ptArr[1].Value<double>() : 200.0;
                    var vp = Viewport.Create(d, targetSheet.Id, view.Id, new XYZ(UnitKit.MmToFeet(xMm), UnitKit.MmToFeet(yMm), 0));
                    return new JObject
                    {
                        ["viewport_id"] = IdCompat.ToInt64(vp.Id),
                        ["sheet_id"] = IdCompat.ToInt64(targetSheet.Id),
                        ["view_id"] = IdCompat.ToInt64(view.Id)
                    };
                },
                (d, staged) => staged,
                $"Sheet operation '{action}' completed.");
        }
    }

    [ScioTool("scio_sheet_layout", "documentation", "Inspect viewport boxes on a sheet or move a viewport's box center in mm.", ReadOnly = false)]
    public sealed class SheetLayoutCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: inspect (default) or move_viewport.", EnumValues = new[] { "inspect", "move_viewport" })]
        public string Action { get; set; } = "inspect";

        [ScioParam("sheet_id", "Target ViewSheet ID for inspect.")]
        public long? SheetId { get; set; }

        [ScioParam("viewport_id", "Target Viewport ID for move_viewport.")]
        public long? ViewportId { get; set; }

        [ScioParam("center_mm", "New viewport center [x, y] in mm for move_viewport.")]
        public List<double>? CenterMm { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SheetLayoutCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "inspect").Trim().ToLowerInvariant();

            if (action == "inspect")
            {
                var sheet = RevitElementKit.ResolveElement(doc, request.Arguments["sheet_id"]) as ViewSheet;
                if (sheet == null) return ToolResponse.Refusal(request.RequestId, "sheet_not_found", "Provide a valid 'sheet_id'.");

                var vps = new JArray();
                foreach (var vpId in sheet.GetAllViewports())
                {
                    if (doc.GetElement(vpId) is Viewport vp)
                    {
                        var c = vp.GetBoxCenter();
                        var o = vp.GetBoxOutline();
                        vps.Add(new JObject
                        {
                            ["viewport_id"] = IdCompat.ToInt64(vp.Id),
                            ["view_id"] = IdCompat.ToInt64(vp.ViewId),
                            ["view_name"] = doc.GetElement(vp.ViewId)?.Name ?? string.Empty,
                            ["center_mm"] = new JArray(Math.Round(UnitKit.FeetToMm(c.X), 1), Math.Round(UnitKit.FeetToMm(c.Y), 1)),
                            ["width_mm"] = Math.Round(UnitKit.FeetToMm(o.MaximumPoint.X - o.MinimumPoint.X), 1),
                            ["height_mm"] = Math.Round(UnitKit.FeetToMm(o.MaximumPoint.Y - o.MinimumPoint.Y), 1)
                        });
                    }
                }
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["sheet_number"] = sheet.SheetNumber,
                    ["sheet_name"] = sheet.Name,
                    ["viewport_count"] = vps.Count,
                    ["viewports"] = vps
                }, $"Sheet '{sheet.SheetNumber}' has {vps.Count} viewport(s).");
            }

            var targetVp = RevitElementKit.ResolveElement(doc, request.Arguments["viewport_id"]) as Viewport;
            var ptArr = request.Arguments["center_mm"] as JArray;
            if (targetVp == null || ptArr == null || ptArr.Count < 2)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide valid 'viewport_id' and 'center_mm' [x,y].");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Move Viewport",
                dryRun,
                d =>
                {
                    targetVp.SetBoxCenter(new XYZ(UnitKit.MmToFeet(ptArr[0].Value<double>()), UnitKit.MmToFeet(ptArr[1].Value<double>()), 0));
                    return new JObject { ["viewport_id"] = IdCompat.ToInt64(targetVp.Id), ["center_mm"] = ptArr };
                },
                (d, staged) => staged,
                "Moved viewport center.");
        }
    }

    [ScioTool("scio_export_pdf_dwg", "documentation", "Export selected Revit views or sheets to PDF or DWG files.", ReadOnly = true, OpenWorld = true)]
    public sealed class ExportPdfDwgCommand : IRevitCommand
    {
        [ScioParam("format", "Export format: pdf or dwg.", Required = true, EnumValues = new[] { "pdf", "dwg" })]
        public string Format { get; set; } = "pdf";

        [ScioParam("view_ids", "View or Sheet ElementIds to export.", Required = true)]
        public List<long> ViewIds { get; set; } = new List<long>();

        [ScioParam("output_folder", "Target folder path on disk.", Required = true)]
        public string OutputFolder { get; set; } = string.Empty;

        [ScioParam("file_prefix", "Optional filename prefix (default 'SCIO_Export').")]
        public string FilePrefix { get; set; } = "SCIO_Export";

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ExportPdfDwgCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string format = (request.Arguments.Value<string>("format") ?? "pdf").Trim().ToLowerInvariant();
            string outDir = request.Arguments.Value<string>("output_folder") ?? string.Empty;
            string prefix = request.Arguments.Value<string>("file_prefix") ?? "SCIO_Export";
            var ids = RevitElementKit.ResolveElements(doc, request.Arguments["view_ids"] as JArray).Select(e => e.Id).ToList();

            if (ids.Count == 0 || string.IsNullOrWhiteSpace(outDir))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide non-empty 'view_ids' and 'output_folder'.");

            Directory.CreateDirectory(outDir);
            bool exported;
            if (format == "dwg")
            {
                exported = doc.Export(outDir, prefix, ids, new DWGExportOptions());
            }
            else
            {
                var pdfOpts = new PDFExportOptions
                {
                    FileName = prefix,
                    Combine = ids.Count > 1
                };
                exported = doc.Export(outDir, ids, pdfOpts);
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["format"] = format,
                ["output_folder"] = outDir,
                ["exported_count"] = ids.Count,
                ["exported"] = exported
            }, $"Exported {ids.Count} view(s)/sheet(s) as {format.ToUpperInvariant()} to '{outDir}'.");
        }
    }

    [ScioTool("scio_planimetry_pack", "documentation", "Audit sheet index completeness: empty sheets, unplaced views, and scale consistency across the drawing set.", ReadOnly = true)]
    public sealed class PlanimetryPackCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PlanimetryPackCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().OrderBy(s => s.SheetNumber).ToList();
            var placedViewIds = new HashSet<long>();
            var emptySheets = new JArray();
            var index = new JArray();

            foreach (var s in sheets)
            {
                var vps = s.GetAllViewports();
                if (vps.Count == 0) emptySheets.Add(new JObject { ["id"] = IdCompat.ToInt64(s.Id), ["number"] = s.SheetNumber, ["name"] = s.Name });
                foreach (var vpId in vps)
                {
                    if (doc.GetElement(vpId) is Viewport vp) placedViewIds.Add(IdCompat.ToInt64(vp.ViewId));
                }
                index.Add(new JObject
                {
                    ["id"] = IdCompat.ToInt64(s.Id),
                    ["number"] = s.SheetNumber,
                    ["name"] = s.Name,
                    ["viewport_count"] = vps.Count
                });
            }

            var unplaced = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet && v.ViewType != ViewType.ProjectBrowser &&
                            v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Schedule &&
                            !placedViewIds.Contains(IdCompat.ToInt64(v.Id)))
                .Take(200)
                .Select(v => new JObject { ["id"] = IdCompat.ToInt64(v.Id), ["name"] = v.Name, ["view_type"] = v.ViewType.ToString() });

            var unplacedArr = new JArray(unplaced);
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["sheet_count"] = sheets.Count,
                ["empty_sheet_count"] = emptySheets.Count,
                ["unplaced_view_count"] = unplacedArr.Count,
                ["empty_sheets"] = emptySheets,
                ["unplaced_views"] = unplacedArr,
                ["sheet_index"] = index
            }, $"Planimetry audit: {sheets.Count} sheets ({emptySheets.Count} empty), {unplacedArr.Count} unplaced views.");
        }
    }
}
