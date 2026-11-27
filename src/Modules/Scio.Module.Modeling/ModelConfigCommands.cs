using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Modeling
{
    [ScioTool("scio_manage_groups", "modeling", "List model/detail groups, create a group from element IDs, or ungroup existing group instances.", ReadOnly = false)]
    public sealed class ManageGroupsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default), create, or ungroup.", EnumValues = new[] { "list", "create", "ungroup" })]
        public string Action { get; set; } = "list";

        [ScioParam("element_ids", "Element IDs to group (for create) or Group IDs to ungroup (for ungroup).")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("group_name", "Optional name for newly created GroupType.")]
        public string GroupName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageGroupsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>()
                    .Select(g => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(g.Id),
                        ["name"] = g.Name,
                        ["type_id"] = IdCompat.ToInt64(g.GroupType.Id),
                        ["member_count"] = g.GetMemberIds().Count
                    });
                var arr = new JArray(groups);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["groups"] = arr }, $"Found {arr.Count} group instance(s).");
            }

            var elems = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elems.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "Provide valid 'element_ids'.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Groups ({action})",
                dryRun,
                d =>
                {
                    if (action == "create")
                    {
                        var grp = d.Create.NewGroup(elems.Select(e => e.Id).ToList());
                        string? name = request.Arguments.Value<string>("group_name");
                        if (!string.IsNullOrWhiteSpace(name)) grp.GroupType.Name = name;
                        return new JObject { ["group_id"] = IdCompat.ToInt64(grp.Id), ["name"] = grp.GroupType.Name, ["member_count"] = elems.Count };
                    }
                    int ungrouped = 0;
                    foreach (var g in elems.OfType<Group>())
                    {
                        g.UngroupMembers();
                        ungrouped++;
                    }
                    return new JObject { ["ungrouped_count"] = ungrouped };
                },
                (d, staged) => staged,
                $"Group operation '{action}' completed.");
        }
    }

    [ScioTool("scio_manage_worksets", "modeling", "List user worksets, create a new workset, or assign elements to a workset in a workshared model.", ReadOnly = false)]
    public sealed class ManageWorksetsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default), create, or assign.", EnumValues = new[] { "list", "create", "assign" })]
        public string Action { get; set; } = "list";

        [ScioParam("workset_name", "Workset name for create or assign.")]
        public string WorksetName { get; set; } = string.Empty;

        [ScioParam("element_ids", "Element IDs to assign when action=assign.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageWorksetsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            if (!doc.IsWorkshared)
                return ToolResponse.Refusal(request.RequestId, "not_workshared", $"Document '{doc.Title}' is not workshared.");

            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();
            var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToList();

            if (action == "list")
            {
                var arr = new JArray(worksets.Select(w => new JObject
                {
                    ["id"] = w.Id.IntegerValue,
                    ["name"] = w.Name,
                    ["is_open"] = w.IsOpen,
                    ["is_editable"] = w.IsEditable,
                    ["owner"] = w.Owner ?? string.Empty
                }));
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["worksets"] = arr }, $"Found {arr.Count} user workset(s).");
            }

            string wsName = request.Arguments.Value<string>("workset_name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(wsName))
                return ToolResponse.Refusal(request.RequestId, "missing_name", "Provide 'workset_name'.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Worksets ({action})",
                dryRun,
                d =>
                {
                    if (action == "create")
                    {
                        var ws = Workset.Create(d, wsName);
                        return new JObject { ["id"] = ws.Id.IntegerValue, ["name"] = ws.Name };
                    }
                    var targetWs = worksets.FirstOrDefault(w => string.Equals(w.Name, wsName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Workset '{wsName}' not found.");
                    var elems = RevitElementKit.ResolveElements(d, request.Arguments["element_ids"] as JArray);
                    int assigned = 0;
                    foreach (var el in elems)
                    {
                        var p = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (p != null && !p.IsReadOnly) { p.Set(targetWs.Id.IntegerValue); assigned++; }
                    }
                    return new JObject { ["workset"] = targetWs.Name, ["assigned_count"] = assigned };
                },
                (d, staged) => staged,
                $"Workset operation '{action}' completed.");
        }
    }

    [ScioTool("scio_manage_phases", "modeling", "List project phases or assign Phase Created / Phase Demolished to elements with dry-run support.", ReadOnly = false)]
    public sealed class ManagePhasesCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or assign.", EnumValues = new[] { "list", "assign" })]
        public string Action { get; set; } = "list";

        [ScioParam("element_ids", "Element IDs for action=assign.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("phase_created", "Optional Phase Created name.")]
        public string PhaseCreated { get; set; } = string.Empty;

        [ScioParam("phase_demolished", "Optional Phase Demolished name (or 'None').")]
        public string PhaseDemolished { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManagePhasesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var phases = doc.Phases.Cast<Phase>().ToList();
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var arr = new JArray(phases.Select((p, idx) => new JObject
                {
                    ["sequence"] = idx + 1,
                    ["id"] = IdCompat.ToInt64(p.Id),
                    ["name"] = p.Name
                }));
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["phases"] = arr }, $"Found {arr.Count} phase(s).");
            }

            var elems = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elems.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "Provide valid 'element_ids'.");

            string? createdName = request.Arguments.Value<string>("phase_created");
            string? demoName = request.Arguments.Value<string>("phase_demolished");
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Assign Phases",
                dryRun,
                d =>
                {
                    Phase? pCreated = string.IsNullOrWhiteSpace(createdName) ? null : phases.FirstOrDefault(p => string.Equals(p.Name, createdName, StringComparison.OrdinalIgnoreCase));
                    Phase? pDemo = string.IsNullOrWhiteSpace(demoName) || string.Equals(demoName, "None", StringComparison.OrdinalIgnoreCase)
                        ? null : phases.FirstOrDefault(p => string.Equals(p.Name, demoName, StringComparison.OrdinalIgnoreCase));

                    int updated = 0;
                    foreach (var el in elems)
                    {
                        bool touched = false;
                        if (pCreated != null)
                        {
                            var pc = el.get_Parameter(BuiltInParameter.PHASE_CREATED);
                            if (pc != null && !pc.IsReadOnly) { pc.Set(pCreated.Id); touched = true; }
                        }
                        if (!string.IsNullOrWhiteSpace(demoName))
                        {
                            var pd = el.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                            if (pd != null && !pd.IsReadOnly) { pd.Set(pDemo?.Id ?? ElementId.InvalidElementId); touched = true; }
                        }
                        if (touched) updated++;
                    }
                    return new JObject { ["updated_count"] = updated };
                },
                (d, staged) => staged,
                $"Updated phase assignment on {elems.Count} element(s).");
        }
    }

    [ScioTool("scio_manage_revisions", "modeling", "List project revisions or create a new Revision with description, date, and issued_by.", ReadOnly = false)]
    public sealed class ManageRevisionsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or create.", EnumValues = new[] { "list", "create" })]
        public string Action { get; set; } = "list";

        [ScioParam("description", "Revision description for action=create.")]
        public string Description { get; set; } = string.Empty;

        [ScioParam("date", "Revision date string (e.g. 2026-11-22).")]
        public string RevisionDate { get; set; } = string.Empty;

        [ScioParam("issued_by", "Issuer name/role.")]
        public string IssuedBy { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageRevisionsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var revs = new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().OrderBy(r => r.SequenceNumber);
                var arr = new JArray(revs.Select(r => new JObject
                {
                    ["id"] = IdCompat.ToInt64(r.Id),
                    ["sequence"] = r.SequenceNumber,
                    ["number"] = r.RevisionNumber ?? string.Empty,
                    ["date"] = r.RevisionDate ?? string.Empty,
                    ["description"] = r.Description ?? string.Empty,
                    ["issued"] = r.Issued,
                    ["issued_by"] = r.IssuedBy ?? string.Empty
                }));
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["revisions"] = arr }, $"Found {arr.Count} revision(s).");
            }

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Revision",
                dryRun,
                d =>
                {
                    var rev = Revision.Create(d);
                    string? desc = request.Arguments.Value<string>("description");
                    string? date = request.Arguments.Value<string>("date");
                    string? by = request.Arguments.Value<string>("issued_by");
                    if (!string.IsNullOrWhiteSpace(desc)) rev.Description = desc;
                    if (!string.IsNullOrWhiteSpace(date)) rev.RevisionDate = date;
                    if (!string.IsNullOrWhiteSpace(by)) rev.IssuedBy = by;
                    return new JObject
                    {
                        ["id"] = IdCompat.ToInt64(rev.Id),
                        ["sequence"] = rev.SequenceNumber,
                        ["description"] = rev.Description
                    };
                },
                (d, staged) => staged,
                "Created new project revision.");
        }
    }

    [ScioTool("scio_manage_materials", "modeling", "List, create, update (rename/description/cost/keynote/mark/color), or assign Revit Materials with dry-run verification.", ReadOnly = false)]
    public sealed class ManageMaterialsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default), create, update, or assign.", EnumValues = new[] { "list", "create", "update", "assign" })]
        public string Action { get; set; } = "list";

        [ScioParam("filter", "Optional substring filter for action=list.")]
        public string Filter { get; set; } = string.Empty;

        [ScioParam("material_id", "Target Material ElementId for action=update or action=assign.")]
        public long? MaterialId { get; set; }

        [ScioParam("name", "Material name (new name for create, or lookup name for update/assign).")]
        public string Name { get; set; } = string.Empty;

        [ScioParam("new_name", "Optional new name when renaming a material in action=update.")]
        public string? NewName { get; set; }

        [ScioParam("description", "Optional Description parameter value for create/update.")]
        public string? Description { get; set; }

        [ScioParam("cost", "Optional Cost parameter value for create/update.")]
        public double? Cost { get; set; }

        [ScioParam("keynote", "Optional Keynote parameter value for create/update.")]
        public string? Keynote { get; set; }

        [ScioParam("mark", "Optional Mark parameter value for create/update.")]
        public string? Mark { get; set; }

        [ScioParam("comments", "Optional Comments parameter value for create/update.")]
        public string? Comments { get; set; }

        [ScioParam("rgb", "Optional [r, g, b] color values (0..255) for create/update.")]
        public List<int>? Rgb { get; set; }

        [ScioParam("transparency", "Optional transparency (0..100) for create/update.")]
        public int? Transparency { get; set; }

        [ScioParam("items", "Optional batch list of material update objects [{ material_id?, name?, new_name?, description?, cost?, keynote?, mark?, comments?, rgb?, transparency? }] for action=update.")]
        public JArray? Items { get; set; }

        [ScioParam("element_ids", "Target ElementIds for action=assign.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageMaterialsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var args = request.Arguments ?? new JObject();
            string action = (args.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                string filter = args.Value<string>("filter") ?? string.Empty;
                var mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .Where(m => string.IsNullOrWhiteSpace(filter) || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(m => m.Name)
                    .Take(250)
                    .Select(m => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(m.Id),
                        ["name"] = m.Name,
                        ["class"] = m.MaterialClass ?? string.Empty,
                        ["description"] = m.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? string.Empty,
                        ["keynote"] = m.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString() ?? string.Empty,
                        ["cost"] = m.get_Parameter(BuiltInParameter.ALL_MODEL_COST)?.AsDouble(),
                        ["rgb"] = m.Color.IsValid ? new JArray(m.Color.Red, m.Color.Green, m.Color.Blue) : null,
                        ["transparency"] = m.Transparency
                    });
                var arr = new JArray(mats);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["materials"] = arr }, $"Listed {arr.Count} material(s).");
            }

            bool dryRun = request.DryRun ?? args.Value<bool?>("dry_run") ?? true;

            if (action == "create")
            {
                string matName = args.Value<string>("name") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(matName))
                    return ToolResponse.Refusal(request.RequestId, "missing_name", "Provide 'name' for new material.");

                return TransactionPipeline.RunVerified(
                    doc,
                    request.RequestId,
                    $"Create Material {matName}",
                    dryRun,
                    d =>
                    {
                        ElementId id = Material.Create(d, matName);
                        var mat = (Material)d.GetElement(id);
                        ApplyMaterialFields(mat, args);
                        return new JObject { ["id"] = IdCompat.ToInt64(id), ["name"] = mat.Name };
                    },
                    (d, staged) =>
                    {
                        staged["verified"] = RevitElementKit.ResolveElement(d, staged["id"]) is Material;
                        return staged;
                    },
                    $"Created material '{matName}'.");
            }

            if (action == "update")
            {
                var batch = args["items"] as JArray;
                var specs = batch != null && batch.Count > 0
                    ? batch.OfType<JObject>().ToList()
                    : new List<JObject> { args };

                return TransactionPipeline.RunVerified(
                    doc,
                    request.RequestId,
                    "Update Materials",
                    dryRun,
                    d =>
                    {
                        var updated = new JArray();
                        var missing = new JArray();
                        foreach (var spec in specs)
                        {
                            var mat = ResolveMaterial(d, spec["material_id"], spec.Value<string>("name"));
                            if (mat == null)
                            {
                                missing.Add(spec.Value<string>("name") ?? spec["material_id"]?.ToString() ?? "unknown");
                                continue;
                            }
                            string? newName = spec.Value<string>("new_name")?.Trim();
                            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(mat.Name, newName, StringComparison.Ordinal))
                                mat.Name = newName!;
                            ApplyMaterialFields(mat, spec);
                            updated.Add(new JObject
                            {
                                ["id"] = IdCompat.ToInt64(mat.Id),
                                ["name"] = mat.Name,
                                ["description"] = mat.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? string.Empty,
                                ["keynote"] = mat.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString() ?? string.Empty,
                                ["cost"] = mat.get_Parameter(BuiltInParameter.ALL_MODEL_COST)?.AsDouble()
                            });
                        }
                        if (updated.Count == 0)
                            throw new InvalidOperationException("No target materials were found to update.");
                        return new JObject { ["updated_count"] = updated.Count, ["updated"] = updated, ["missing"] = missing };
                    },
                    (d, staged) =>
                    {
                        staged["verified"] = (staged["updated_count"]?.Value<int>() ?? 0) > 0;
                        return staged;
                    },
                    $"Updated {specs.Count} material specification(s).");
            }

            if (action == "assign")
            {
                var mat = ResolveMaterial(doc, args["material_id"], args.Value<string>("name"));
                if (mat == null)
                    return ToolResponse.Refusal(request.RequestId, "material_not_found", "Provide a valid 'material_id' or 'name'.");

                var elems = RevitElementKit.ResolveElements(doc, args["element_ids"] as JArray);
                if (elems.Count == 0)
                    return ToolResponse.Refusal(request.RequestId, "no_elements", "Provide valid 'element_ids' for action=assign.");

                return TransactionPipeline.RunVerified(
                    doc,
                    request.RequestId,
                    $"Assign Material {mat.Name}",
                    dryRun,
                    d =>
                    {
                        int assigned = 0;
                        var skipped = new JArray();
                        foreach (var el in elems)
                        {
                            var p = el.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)
                                ?? el.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId)
                            {
                                p.Set(mat.Id);
                                assigned++;
                            }
                            else
                            {
                                skipped.Add(IdCompat.ToInt64(el.Id));
                            }
                        }
                        return new JObject
                        {
                            ["material_id"] = IdCompat.ToInt64(mat.Id),
                            ["material_name"] = mat.Name,
                            ["assigned_count"] = assigned,
                            ["skipped_ids"] = skipped
                        };
                    },
                    (d, staged) =>
                    {
                        staged["verified"] = (staged["assigned_count"]?.Value<int>() ?? 0) > 0;
                        return staged;
                    },
                    $"Assigned material '{mat.Name}' to target elements.");
            }

            return ToolResponse.Refusal(request.RequestId, "invalid_action", $"Unsupported action '{action}'.");
        }

        private static Material? ResolveMaterial(Document doc, JToken? idToken, string? name)
        {
            if (idToken != null && idToken.Type != JTokenType.Null && RevitElementKit.ResolveElement(doc, idToken) is Material byId)
                return byId;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .FirstOrDefault(m => string.Equals(m.Name, name!.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            return null;
        }

        private static void ApplyMaterialFields(Material mat, JObject spec)
        {
            if (spec["rgb"] is JArray rgb && rgb.Count >= 3)
                mat.Color = new Color((byte)rgb[0].Value<int>(), (byte)rgb[1].Value<int>(), (byte)rgb[2].Value<int>());
            int? transp = spec.Value<int?>("transparency");
            if (transp.HasValue) mat.Transparency = Math.Max(0, Math.Min(100, transp.Value));

            SetStringParam(mat, BuiltInParameter.ALL_MODEL_DESCRIPTION, spec.Value<string>("description"));
            SetStringParam(mat, BuiltInParameter.KEYNOTE_PARAM, spec.Value<string>("keynote"));
            SetStringParam(mat, BuiltInParameter.ALL_MODEL_MARK, spec.Value<string>("mark"));
            SetStringParam(mat, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, spec.Value<string>("comments"));

            double? cost = spec.Value<double?>("cost");
            if (cost.HasValue)
            {
                var cp = mat.get_Parameter(BuiltInParameter.ALL_MODEL_COST);
                if (cp != null && !cp.IsReadOnly) cp.Set(cost.Value);
            }
        }

        private static void SetStringParam(Element el, BuiltInParameter bip, string? value)
        {
            if (value == null) return;
            var p = el.get_Parameter(bip);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                p.Set(value);
        }
    }

    [ScioTool("scio_manage_styles", "modeling", "List line styles and fill patterns, or create a new line subcategory style with weight and RGB color.", ReadOnly = false)]
    public sealed class ManageStylesCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list_lines (default), list_patterns, or create_line_style.", EnumValues = new[] { "list_lines", "list_patterns", "create_line_style" })]
        public string Action { get; set; } = "list_lines";

        [ScioParam("name", "Line style name for create_line_style.")]
        public string Name { get; set; } = string.Empty;

        [ScioParam("weight", "Line weight (1..16) for create_line_style.")]
        public int Weight { get; set; } = 1;

        [ScioParam("rgb", "Optional [r, g, b] color for create_line_style.")]
        public List<int>? Rgb { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageStylesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list_lines").Trim().ToLowerInvariant();

            if (action == "list_patterns")
            {
                var pats = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .Select(p => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(p.Id),
                        ["name"] = p.Name,
                        ["target"] = p.GetFillPattern().Target.ToString()
                    });
                var arr = new JArray(pats);
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["patterns"] = arr }, $"Found {arr.Count} fill pattern(s).");
            }

            var linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            if (action == "list_lines")
            {
                var arr = new JArray();
                if (linesCat != null)
                {
                    foreach (Category sub in linesCat.SubCategories)
                    {
                        arr.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(sub.Id),
                            ["name"] = sub.Name,
                            ["weight"] = sub.GetLineWeight(GraphicsStyleType.Projection),
                            ["rgb"] = sub.LineColor.IsValid ? new JArray(sub.LineColor.Red, sub.LineColor.Green, sub.LineColor.Blue) : null
                        });
                    }
                }
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["line_styles"] = arr }, $"Found {arr.Count} line style(s).");
            }

            string styleName = request.Arguments.Value<string>("name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(styleName) || linesCat == null)
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide 'name' for new line style.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Create Line Style {styleName}",
                dryRun,
                d =>
                {
                    Category sub = d.Settings.Categories.NewSubcategory(linesCat, styleName);
                    int weight = Math.Max(1, Math.Min(16, request.Arguments.Value<int?>("weight") ?? 1));
                    sub.SetLineWeight(weight, GraphicsStyleType.Projection);
                    if (request.Arguments["rgb"] is JArray rgb && rgb.Count >= 3)
                        sub.LineColor = new Color((byte)rgb[0].Value<int>(), (byte)rgb[1].Value<int>(), (byte)rgb[2].Value<int>());
                    return new JObject { ["id"] = IdCompat.ToInt64(sub.Id), ["name"] = sub.Name, ["weight"] = weight };
                },
                (d, staged) => staged,
                $"Created line style '{styleName}'.");
        }
    }

    [ScioTool("scio_manage_units", "modeling", "Inspect project display units (length, area, volume, angle) or set length display unit (mm, cm, m, ft).", ReadOnly = false)]
    public sealed class ManageUnitsCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: get (default) or set_length.", EnumValues = new[] { "get", "set_length" })]
        public string Action { get; set; } = "get";

        [ScioParam("unit", "Target length unit for set_length.", EnumValues = new[] { "mm", "cm", "m", "ft" })]
        public string Unit { get; set; } = "mm";

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageUnitsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "get").Trim().ToLowerInvariant();
            var units = doc.GetUnits();

            if (action == "get")
            {
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["length"] = units.GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId,
                    ["area"] = units.GetFormatOptions(SpecTypeId.Area).GetUnitTypeId().TypeId,
                    ["volume"] = units.GetFormatOptions(SpecTypeId.Volume).GetUnitTypeId().TypeId,
                    ["angle"] = units.GetFormatOptions(SpecTypeId.Angle).GetUnitTypeId().TypeId
                }, "Read project display units.");
            }

            string targetUnit = (request.Arguments.Value<string>("unit") ?? "mm").Trim().ToLowerInvariant();
            ForgeTypeId unitId = targetUnit switch
            {
                "mm" => UnitTypeId.Millimeters,
                "cm" => UnitTypeId.Centimeters,
                "m" => UnitTypeId.Meters,
                "ft" => UnitTypeId.Feet,
                _ => throw new ArgumentException($"Unsupported unit '{targetUnit}'.")
            };

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Set Length Unit ({targetUnit})",
                dryRun,
                d =>
                {
                    var u = d.GetUnits();
                    var fo = u.GetFormatOptions(SpecTypeId.Length);
                    fo.SetUnitTypeId(unitId);
                    u.SetFormatOptions(SpecTypeId.Length, fo);
                    d.SetUnits(u);
                    return new JObject { ["length"] = unitId.TypeId };
                },
                (d, staged) =>
                {
                    staged["verified_length"] = d.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId;
                    return staged;
                },
                $"Set length display unit to '{targetUnit}'.");
        }
    }

    [ScioTool("scio_manage_system_types", "modeling", "List or duplicate Revit system types (wall, floor, roof, ceiling, pipe, duct) with optional compound thickness summary.", ReadOnly = false)]
    public sealed class ManageSystemTypesCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or duplicate.", EnumValues = new[] { "list", "duplicate" })]
        public string Action { get; set; } = "list";

        [ScioParam("family_kind", "System family kind.", EnumValues = new[] { "wall", "floor", "roof", "ceiling", "pipe", "duct" })]
        public string FamilyKind { get; set; } = "wall";

        [ScioParam("source_type_id", "Source system type ID for duplicate.")]
        public long? SourceTypeId { get; set; }

        [ScioParam("new_name", "New type name for duplicate.")]
        public string NewName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageSystemTypesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();
            string kind = (request.Arguments.Value<string>("family_kind") ?? "wall").Trim().ToLowerInvariant();

            if (action == "list")
            {
                Type targetType = kind switch
                {
                    "wall" => typeof(WallType),
                    "floor" => typeof(FloorType),
                    "roof" => typeof(RoofType),
                    "ceiling" => typeof(CeilingType),
                    "pipe" => typeof(PipeType),
                    "duct" => typeof(DuctType),
                    _ => typeof(WallType)
                };

                var arr = new JArray();
                foreach (var et in new FilteredElementCollector(doc).OfClass(targetType).Cast<ElementType>().OrderBy(t => t.Name))
                {
                    var obj = new JObject
                    {
                        ["id"] = IdCompat.ToInt64(et.Id),
                        ["family"] = et.FamilyName ?? string.Empty,
                        ["name"] = et.Name
                    };
                    if (et is HostObjAttributes hoa)
                    {
                        var cs = hoa.GetCompoundStructure();
                        if (cs != null)
                        {
                            obj["width_mm"] = Math.Round(UnitKit.FeetToMm(cs.GetWidth()), 1);
                            obj["layer_count"] = cs.LayerCount;
                        }
                    }
                    arr.Add(obj);
                }
                return ToolResponse.Success(request.RequestId, new JObject { ["kind"] = kind, ["count"] = arr.Count, ["types"] = arr }, $"Listed {arr.Count} {kind} system type(s).");
            }

            long srcId = request.Arguments.Value<long?>("source_type_id") ?? 0;
            string newName = request.Arguments.Value<string>("new_name") ?? string.Empty;
            var src = RevitElementKit.ResolveElement(doc, srcId) as ElementType;
            if (src == null || string.IsNullOrWhiteSpace(newName))
                return ToolResponse.Refusal(request.RequestId, "invalid_request", "Provide valid 'source_type_id' and 'new_name'.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Duplicate System Type {newName}",
                dryRun,
                d =>
                {
                    var dup = src.Duplicate(newName);
                    return new JObject { ["id"] = IdCompat.ToInt64(dup.Id), ["name"] = dup.Name, ["family"] = dup.FamilyName ?? string.Empty };
                },
                (d, staged) =>
                {
                    staged["verified"] = RevitElementKit.ResolveElement(d, staged["id"]) != null;
                    return staged;
                },
                $"Duplicated system type '{src.Name}' as '{newName}'.");
        }
    }
}
