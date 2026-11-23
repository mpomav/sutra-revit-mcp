using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Modeling
{
    [ScioTool("scio_create_family", "modeling", "Load a .rfa family into the active project, or duplicate an existing ElementType with updated type parameters.", ReadOnly = false)]
    public sealed class CreateFamilyCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: load_rfa or duplicate_type.", Required = true, EnumValues = new[] { "load_rfa", "duplicate_type" })]
        public string Action { get; set; } = "duplicate_type";

        [ScioParam("rfa_path", "Absolute path to .rfa file for load_rfa.")]
        public string RfaPath { get; set; } = string.Empty;

        [ScioParam("source_type_id", "Existing ElementType ID to duplicate for duplicate_type.")]
        public long? SourceTypeId { get; set; }

        [ScioParam("new_type_name", "New type name for duplicate_type.")]
        public string NewTypeName { get; set; } = string.Empty;

        [ScioParam("parameters", "Optional map of type parameter names -> values to apply after duplication.")]
        public JObject? Parameters { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CreateFamilyCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "duplicate_type").Trim().ToLowerInvariant();
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            if (action == "load_rfa")
            {
                string path = request.Arguments.Value<string>("rfa_path") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return ToolResponse.Refusal(request.RequestId, "file_not_found", $"Family file not found: '{path}'.");

                return TransactionPipeline.RunVerified(
                    doc,
                    request.RequestId,
                    $"Load Family {Path.GetFileName(path)}",
                    dryRun,
                    d =>
                    {
                        bool loaded = d.LoadFamily(path, out Family? fam);
                        return new JObject
                        {
                            ["loaded"] = loaded,
                            ["rfa_path"] = path,
                            ["family_id"] = fam == null ? -1 : IdCompat.ToInt64(fam.Id),
                            ["family_name"] = fam?.Name ?? string.Empty
                        };
                    },
                    (d, staged) => staged,
                    $"Loaded family '{Path.GetFileName(path)}'.");
            }

            long srcId = request.Arguments.Value<long?>("source_type_id") ?? 0;
            string newName = request.Arguments.Value<string>("new_type_name") ?? string.Empty;
            var srcType = RevitElementKit.ResolveElement(doc, srcId) as ElementType;
            if (srcType == null)
                return ToolResponse.Refusal(request.RequestId, "type_not_found", $"Source ElementType {srcId} not found.");
            if (string.IsNullOrWhiteSpace(newName))
                return ToolResponse.Refusal(request.RequestId, "missing_name", "Provide 'new_type_name'.");

            var paramMap = request.Arguments["parameters"] as JObject;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Duplicate Type {newName}",
                dryRun,
                d =>
                {
                    ElementType dup = srcType.Duplicate(newName);
                    var applied = new JArray();
                    if (paramMap != null)
                    {
                        foreach (var prop in paramMap.Properties())
                        {
                            var p = RevitElementKit.FindParameter(dup, prop.Name);
                            if (p != null && RevitElementKit.TrySetParameter(p, prop.Value, out _))
                                applied.Add(prop.Name);
                        }
                    }
                    return new JObject
                    {
                        ["source_type_id"] = srcId,
                        ["new_type_id"] = IdCompat.ToInt64(dup.Id),
                        ["new_type_name"] = dup.Name,
                        ["family"] = dup.FamilyName ?? string.Empty,
                        ["applied_parameters"] = applied
                    };
                },
                (d, staged) =>
                {
                    staged["verified"] = RevitElementKit.ResolveElement(d, staged["new_type_id"]) != null;
                    return staged;
                },
                $"Duplicated type '{srcType.Name}' as '{newName}'.");
        }
    }

    [ScioTool("scio_family_apply", "modeling", "Inspect or modify types and parameters inside an open Revit Family Document (.rfa) using FamilyManager.", ReadOnly = false)]
    public sealed class FamilyApplyCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: inspect (default), add_type, or set_param.", EnumValues = new[] { "inspect", "add_type", "set_param" })]
        public string Action { get; set; } = "inspect";

        [ScioParam("type_name", "Family type name for add_type or set_param.")]
        public string TypeName { get; set; } = string.Empty;

        [ScioParam("parameter", "Family parameter name for set_param.")]
        public string ParameterName { get; set; } = string.Empty;

        [ScioParam("value", "Parameter value for set_param (numbers in mm if unit='mm').")]
        public JToken? Value { get; set; }

        [ScioParam("unit", "Optional unit ('mm', 'm', 'deg') for double parameters.")]
        public string Unit { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(FamilyApplyCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            if (!doc.IsFamilyDocument)
                return ToolResponse.Refusal(request.RequestId, "not_family_doc", $"Document '{doc.Title}' is not a Family Document (.rfa).");

            var fm = doc.FamilyManager;
            string action = (request.Arguments.Value<string>("action") ?? "inspect").Trim().ToLowerInvariant();

            if (action == "inspect")
            {
                var types = new JArray();
                foreach (FamilyType ft in fm.Types)
                    if (!string.IsNullOrWhiteSpace(ft.Name)) types.Add(ft.Name);

                var parms = new JArray();
                foreach (FamilyParameter fp in fm.Parameters)
                {
                    parms.Add(new JObject
                    {
                        ["name"] = fp.Definition.Name,
                        ["is_instance"] = fp.IsInstance,
                        ["storage_type"] = fp.StorageType.ToString(),
                        ["formula"] = fp.Formula ?? string.Empty
                    });
                }

                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["family"] = doc.Title,
                    ["current_type"] = fm.CurrentType?.Name ?? string.Empty,
                    ["types"] = types,
                    ["parameters"] = parms
                }, $"Family '{doc.Title}': {types.Count} type(s), {parms.Count} parameter(s).");
            }

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Family Apply ({action})",
                dryRun,
                d =>
                {
                    string typeName = request.Arguments.Value<string>("type_name") ?? string.Empty;
                    if (action == "add_type")
                    {
                        if (string.IsNullOrWhiteSpace(typeName)) throw new ArgumentException("Provide 'type_name'.");
                        var created = fm.NewType(typeName);
                        fm.CurrentType = created;
                        return new JObject { ["action"] = action, ["type_name"] = created.Name };
                    }

                    string pName = request.Arguments.Value<string>("parameter") ?? string.Empty;
                    var fp = fm.get_Parameter(pName);
                    if (fp == null) throw new InvalidOperationException($"FamilyParameter '{pName}' not found.");

                    if (!string.IsNullOrWhiteSpace(typeName))
                    {
                        foreach (FamilyType ft in fm.Types)
                        {
                            if (string.Equals(ft.Name, typeName, StringComparison.OrdinalIgnoreCase))
                            {
                                fm.CurrentType = ft;
                                break;
                            }
                        }
                    }

                    var valTok = request.Arguments["value"];
                    string unit = (request.Arguments.Value<string>("unit") ?? string.Empty).Trim().ToLowerInvariant();
                    switch (fp.StorageType)
                    {
                        case StorageType.String:
                            fm.Set(fp, valTok?.ToString() ?? string.Empty);
                            break;
                        case StorageType.Integer:
                            fm.Set(fp, valTok?.Value<int>() ?? 0);
                            break;
                        case StorageType.Double:
                            double dv = valTok?.Value<double>() ?? 0.0;
                            if (unit == "mm") dv = UnitKit.MmToFeet(dv);
                            else if (unit == "m") dv = UnitKit.MmToFeet(dv * 1000.0);
                            else if (unit == "deg") dv = UnitKit.DegToRad(dv);
                            fm.Set(fp, dv);
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported FamilyParameter storage type: {fp.StorageType}.");
                    }

                    return new JObject
                    {
                        ["action"] = action,
                        ["type"] = fm.CurrentType?.Name ?? string.Empty,
                        ["parameter"] = pName,
                        ["value"] = valTok
                    };
                },
                (d, staged) => staged,
                $"Executed family '{action}'.");
        }
    }

    [ScioTool("scio_set_keynote", "modeling", "Batch-assign Keynote or Assembly Code on elements or ElementTypes with dry-run and post-commit verification.", ReadOnly = false)]
    public sealed class SetKeynoteCommand : IRevitCommand
    {
        [ScioParam("assignments", "Array of { element_id, keynote?, assembly_code? }.", Required = true)]
        public JArray Assignments { get; set; } = new JArray();

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SetKeynoteCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var assignments = request.Arguments["assignments"] as JArray;
            if (assignments == null || assignments.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_assignments", "Provide non-empty 'assignments' array.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Set Keynote / Assembly Code",
                dryRun,
                d =>
                {
                    int updated = 0;
                    var log = new JArray();
                    foreach (var row in assignments.OfType<JObject>())
                    {
                        var el = RevitElementKit.ResolveElement(d, row["element_id"]);
                        if (el == null) continue;
                        Element target = el is ElementType ? el : (d.GetElement(el.GetTypeId()) ?? el);

                        string? keynote = row.Value<string>("keynote");
                        string? asmCode = row.Value<string>("assembly_code");
                        bool touched = false;

                        if (keynote != null)
                        {
                            var kp = target.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
                            if (kp != null && !kp.IsReadOnly) { kp.Set(keynote); touched = true; }
                        }
                        if (asmCode != null)
                        {
#if REVIT2026_OR_GREATER
                            var ap = RevitElementKit.FindParameter(target, "Assembly Code")
                                     ?? RevitElementKit.FindParameter(target, "Código de montaje");
#else
                            var ap = target.get_Parameter(BuiltInParameter.UNIFORMAT_CODE)
                                     ?? RevitElementKit.FindParameter(target, "Assembly Code");
#endif
                            if (ap != null && !ap.IsReadOnly) { ap.Set(asmCode); touched = true; }
                        }
                        if (touched)
                        {
                            updated++;
                            log.Add(new JObject
                            {
                                ["target_id"] = IdCompat.ToInt64(target.Id),
                                ["name"] = target.Name,
                                ["keynote"] = keynote,
                                ["assembly_code"] = asmCode
                            });
                        }
                    }
                    return new JObject { ["updated_count"] = updated, ["updated"] = log };
                },
                (d, staged) => staged,
                $"Updated keynote/classification on {assignments.Count} entry(ies).");
        }
    }

    [ScioTool("scio_catalog_lookup", "modeling", "Search loaded ElementTypes and FamilySymbols in the active document by category, family, or keyword.", ReadOnly = true)]
    public sealed class CatalogLookupCommand : IRevitCommand
    {
        [ScioParam("category", "Optional category name filter (e.g. Walls, Doors, Structural Framing).")]
        public string CategoryName { get; set; } = string.Empty;

        [ScioParam("query", "Optional keyword substring matched against family name and type name.")]
        public string Query { get; set; } = string.Empty;

        [ScioParam("limit", "Maximum types to return (default 150).")]
        public int Limit { get; set; } = 150;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CatalogLookupCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string? catArg = request.Arguments.Value<string>("category");
            string query = request.Arguments.Value<string>("query") ?? string.Empty;
            int limit = Math.Max(1, Math.Min(1000, request.Arguments.Value<int?>("limit") ?? 150));

            var collector = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (!string.IsNullOrWhiteSpace(catArg))
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, catArg);
                if (cat == null)
                    return ToolResponse.Refusal(request.RequestId, "unknown_category", $"Category '{catArg}' not found.");
                collector = collector.OfCategoryId(cat.Id);
            }

            var items = new JArray();
            foreach (var et in collector.OfType<ElementType>())
            {
                string fam = et.FamilyName ?? string.Empty;
                string name = et.Name ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(query) &&
                    fam.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                items.Add(new JObject
                {
                    ["type_id"] = IdCompat.ToInt64(et.Id),
                    ["category"] = et.Category?.Name ?? string.Empty,
                    ["family"] = fam,
                    ["type"] = name,
                    ["is_family_symbol"] = et is FamilySymbol,
                    ["is_active"] = (et as FamilySymbol)?.IsActive ?? true
                });
                if (items.Count >= limit) break;
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["count"] = items.Count,
                ["types"] = items
            }, $"Found {items.Count} matching catalog type(s).");
        }
    }
}
