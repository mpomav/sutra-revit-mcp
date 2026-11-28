using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CoreRead
{
    [ScioTool("scio_manage_parameters", "core", "Inspect instance and type parameters of specific elements, or filter by parameter names.", ReadOnly = true)]
    public sealed class ManageParametersCommand : IRevitCommand
    {
        [ScioParam("element_ids", "Element IDs to inspect.", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        [ScioParam("parameter_names", "Optional list of parameter names to read; omit to return all parameters.")]
        public List<string>? ParameterNames { get; set; }

        [ScioParam("include_type", "If true, also include type parameters.")]
        public bool IncludeType { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageParametersCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var elements = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elements.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "None of the supplied element_ids were found.");

            var wanted = (request.Arguments["parameter_names"] as JArray)?
                .Select(t => t.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            bool includeType = request.Arguments.Value<bool?>("include_type") ?? true;

            var results = new JArray();
            foreach (var el in elements)
            {
                var pList = new JArray();
                if (wanted != null && wanted.Count > 0)
                {
                    foreach (string name in wanted)
                    {
                        var p = RevitElementKit.FindParameter(el, name, includeType);
                        pList.Add(new JObject
                        {
                            ["name"] = name,
                            ["found"] = p != null,
                            ["read_only"] = p?.IsReadOnly ?? true,
                            ["storage_type"] = p?.StorageType.ToString() ?? "None",
                            ["value"] = p == null ? JValue.CreateNull() : RevitElementKit.ReadParameterToken(p),
                            ["display"] = RevitElementKit.ReadParameterDisplay(p)
                        });
                    }
                }
                else
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Parameter p in el.Parameters)
                    {
                        if (p?.Definition == null || !seen.Add(p.Definition.Name)) continue;
                        pList.Add(SerializeParam(p, "instance"));
                    }
                    if (includeType && doc.GetElement(el.GetTypeId()) is Element typeEl)
                    {
                        foreach (Parameter p in typeEl.Parameters)
                        {
                            if (p?.Definition == null || !seen.Add("type:" + p.Definition.Name)) continue;
                            pList.Add(SerializeParam(p, "type"));
                        }
                    }
                }

                results.Add(new JObject
                {
                    ["id"] = IdCompat.ToInt64(el.Id),
                    ["name"] = el.Name ?? string.Empty,
                    ["category"] = el.Category?.Name ?? string.Empty,
                    ["parameters"] = pList
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["count"] = results.Count,
                ["elements"] = results
            }, $"Read parameters for {results.Count} element(s).");
        }

        private static JObject SerializeParam(Parameter p, string scope) => new JObject
        {
            ["name"] = p.Definition.Name,
            ["scope"] = scope,
            ["read_only"] = p.IsReadOnly,
            ["storage_type"] = p.StorageType.ToString(),
            ["value"] = RevitElementKit.ReadParameterToken(p),
            ["display"] = RevitElementKit.ReadParameterDisplay(p)
        };
    }

    [ScioTool("scio_write_params_verified", "core", "Write parameters on one or more elements with dry-run preview (default true) and post-commit re-read verification.", ReadOnly = false)]
    public sealed class WriteParamsVerifiedCommand : IRevitCommand
    {
        [ScioParam("writes", "Array of write objects: { element_id, parameter, value, unit?, include_type? }.", Required = true)]
        public JArray Writes { get; set; } = new JArray();

        [ScioParam("dry_run", "If true (default), previews changes and rolls back the transaction without mutating the model.")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(WriteParamsVerifiedCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var writes = request.Arguments["writes"] as JArray;
            if (writes == null || writes.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_writes", "Provide a non-empty 'writes' array.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Write Parameters",
                dryRun,
                d =>
                {
                    var applied = new JArray();
                    var errors = new JArray();
                    foreach (var item in writes.OfType<JObject>())
                    {
                        var el = RevitElementKit.ResolveElement(d, item["element_id"]);
                        string paramName = item.Value<string>("parameter") ?? string.Empty;
                        bool includeType = item.Value<bool?>("include_type") ?? false;
                        string? unit = item.Value<string>("unit");

                        if (el == null)
                        {
                            errors.Add(new JObject { ["element_id"] = item["element_id"], ["error"] = "Element not found." });
                            continue;
                        }

                        var p = RevitElementKit.FindParameter(el, paramName, includeType);
                        string before = RevitElementKit.ReadParameterDisplay(p);
                        if (!RevitElementKit.TrySetParameter(p!, item["value"], out string err, unit))
                        {
                            var errObj = new JObject { ["element_id"] = IdCompat.ToInt64(el.Id), ["parameter"] = paramName, ["error"] = err };
                            if (el is Floor && err.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                (paramName.IndexOf("Height Offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 paramName.IndexOf("Desfase de altura", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                errObj["hint"] = "Sloped floors have a read-only Height Offset From Level; use scio_transform_elements (action:'move', delta_mm:[0,0,dz]) instead.";
                            }
                            errors.Add(errObj);
                            continue;
                        }

                        var rowObj = new JObject
                        {
                            ["element_id"] = IdCompat.ToInt64(el.Id),
                            ["parameter"] = paramName,
                            ["include_type"] = includeType,
                            ["before"] = before,
                            ["after"] = RevitElementKit.ReadParameterDisplay(p)
                        };
                        if (string.IsNullOrWhiteSpace(unit) && p != null && p.StorageType == StorageType.Double && item["value"]?.Type != JTokenType.String)
                        {
                            try
                            {
                                if (p.Definition.GetDataType() == SpecTypeId.Length)
                                    rowObj["unit_warning"] = "No 'unit' specified for Length parameter; raw number was interpreted as Revit internal feet. Pass unit:'mm' or 'm' to avoid 0.3048x scaling errors.";
                            }
                            catch { }
                        }
                        applied.Add(rowObj);
                    }
                    return new JObject
                    {
                        ["updated_count"] = applied.Count,
                        ["error_count"] = errors.Count,
                        ["applied"] = applied,
                        ["errors"] = errors
                    };
                },
                (d, staged) =>
                {
                    int verified = 0;
                    if (staged["applied"] is JArray arr)
                    {
                        foreach (var row in arr.OfType<JObject>())
                        {
                            var el = RevitElementKit.ResolveElement(d, row["element_id"]);
                            var p = el == null ? null : RevitElementKit.FindParameter(el, row.Value<string>("parameter") ?? string.Empty, row.Value<bool?>("include_type") ?? false);
                            string reread = RevitElementKit.ReadParameterDisplay(p);
                            row["verified_value"] = reread;
                            if (string.Equals(reread, row.Value<string>("after"), StringComparison.Ordinal))
                                verified++;
                        }
                    }
                    staged["verified_count"] = verified;
                    return staged;
                },
                $"Processed {writes.Count} parameter write(s).");
        }
    }

    [ScioTool("scio_bind_shared_param", "core", "Bind a shared parameter definition from a Revit shared parameters file to one or more categories.", ReadOnly = false)]
    public sealed class BindSharedParamCommand : IRevitCommand
    {
        [ScioParam("shared_param_file", "Absolute path to the shared parameters .txt file (optional if already configured in Revit).")]
        public string SharedParamFile { get; set; } = string.Empty;

        [ScioParam("group_name", "Shared parameter group name.", Required = true)]
        public string GroupName { get; set; } = string.Empty;

        [ScioParam("param_name", "Shared parameter definition name.", Required = true)]
        public string ParamName { get; set; } = string.Empty;

        [ScioParam("categories", "List of target category names.", Required = true)]
        public List<string> Categories { get; set; } = new List<string>();

        [ScioParam("is_type", "If true, create a TypeBinding; otherwise InstanceBinding (default).")]
        public bool IsType { get; set; }

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(BindSharedParamCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var app = context.UiApplication.Application;

            string? spFile = request.Arguments.Value<string>("shared_param_file");
            if (!string.IsNullOrWhiteSpace(spFile))
            {
                if (!File.Exists(spFile))
                    return ToolResponse.Refusal(request.RequestId, "file_not_found", $"Shared parameter file not found: '{spFile}'.");
                app.SharedParametersFilename = spFile;
            }

            var defFile = app.OpenSharedParameterFile();
            if (defFile == null)
                return ToolResponse.Refusal(request.RequestId, "no_shared_param_file", "No shared parameters file is configured in Revit.");

            string groupName = request.Arguments.Value<string>("group_name") ?? string.Empty;
            string paramName = request.Arguments.Value<string>("param_name") ?? string.Empty;
            var group = defFile.Groups.get_Item(groupName);
            var def = group?.Definitions.get_Item(paramName);
            if (def == null)
                return ToolResponse.Refusal(request.RequestId, "definition_not_found", $"Shared parameter '{groupName}/{paramName}' not found.");

            var catNames = (request.Arguments["categories"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string>();
            var catSet = app.Create.NewCategorySet();
            var resolvedCats = new JArray();
            foreach (string cn in catNames)
            {
                var c = RevitElementKit.ResolveDocumentCategory(doc, cn);
                if (c != null && c.AllowsBoundParameters)
                {
                    catSet.Insert(c);
                    resolvedCats.Add(c.Name);
                }
            }
            if (catSet.Size == 0)
                return ToolResponse.Refusal(request.RequestId, "no_valid_categories", "None of the requested categories allow bound parameters.");

            bool isType = request.Arguments.Value<bool?>("is_type") ?? false;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Bind Shared Parameter {paramName}",
                dryRun,
                d =>
                {
                    ElementBinding binding = isType
                        ? (ElementBinding)app.Create.NewTypeBinding(catSet)
                        : app.Create.NewInstanceBinding(catSet);

#if REVIT2024_OR_GREATER
                    bool ok = d.ParameterBindings.Insert(def, binding, GroupTypeId.Data)
                              || d.ParameterBindings.ReInsert(def, binding, GroupTypeId.Data);
#else
                    bool ok = d.ParameterBindings.Insert(def, binding, BuiltInParameterGroup.PG_DATA)
                              || d.ParameterBindings.ReInsert(def, binding, BuiltInParameterGroup.PG_DATA);
#endif
                    return new JObject
                    {
                        ["parameter"] = paramName,
                        ["group"] = groupName,
                        ["is_type"] = isType,
                        ["categories"] = resolvedCats,
                        ["bound"] = ok
                    };
                },
                (d, staged) =>
                {
                    staged["verified_in_bindings"] = d.ParameterBindings.Contains(def);
                    return staged;
                },
                $"Bound shared parameter '{paramName}' to {resolvedCats.Count} category(ies).");
        }
    }

    [ScioTool("scio_delete_verified", "core", "Delete elements by ID with dry-run cascade preview (default true) and post-commit deletion verification.", ReadOnly = false, Destructive = true)]
    public sealed class DeleteVerifiedCommand : IRevitCommand
    {
        [ScioParam("element_ids", "Element IDs to delete.", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        [ScioParam("dry_run", "If true (default), previews all dependent elements that would be deleted and rolls back.")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(DeleteVerifiedCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var elements = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            if (elements.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "No matching elements found to delete.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            var targetIds = elements.Select(e => e.Id).ToList();

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Delete Elements",
                dryRun,
                d =>
                {
                    var requestedSet = new HashSet<long>(targetIds.Select(IdCompat.ToInt64));
                    var deleted = d.Delete(targetIds);
                    var dependentIds = deleted
                        .Select(IdCompat.ToInt64)
                        .Where(id => !requestedSet.Contains(id))
                        .ToList();
                    var payload = new JObject
                    {
                        ["requested_count"] = targetIds.Count,
                        ["total_deleted_including_dependents"] = deleted.Count,
                        ["deleted_dependents_count"] = dependentIds.Count,
                        ["deleted_ids"] = new JArray(deleted.Take(200).Select(IdCompat.ToInt64))
                    };
                    if (dependentIds.Count > 0)
                        payload["deleted_dependent_ids"] = new JArray(dependentIds.Take(100));
                    return payload;
                },
                (d, staged) =>
                {
                    int stillPresent = targetIds.Count(id => d.GetElement(id) != null);
                    staged["remaining_requested_count"] = stillPresent;
                    staged["all_deleted"] = stillPresent == 0;
                    return staged;
                },
                $"Deleted {targetIds.Count} requested element(s).");
        }
    }

    [ScioTool("scio_execute_plan", "core", "Execute a structured multi-step parameter/delete plan (inline or loaded by plan_id from LocalPlanStore) with dry-run support.", ReadOnly = false)]
    public sealed class ExecutePlanCommand : IRevitCommand
    {
        [ScioParam("plan_id", "Optional plan ID stored in LocalPlanStore.")]
        public string PlanId { get; set; } = string.Empty;

        [ScioParam("steps", "Optional inline array of plan steps ({ op: 'set_param'|'delete', ... }).")]
        public JArray? Steps { get; set; }

        [ScioParam("dry_run", "If true (default), previews all plan steps and rolls back.")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ExecutePlanCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            JArray? steps = request.Arguments["steps"] as JArray;

            string? planId = request.Arguments.Value<string>("plan_id");
            if ((steps == null || steps.Count == 0) && !string.IsNullOrWhiteSpace(planId))
            {
                if (!LocalPlanStore.TryLoad(planId!, doc.Title, out var loaded, out string? err) || loaded == null)
                    return ToolResponse.Refusal(request.RequestId, "plan_not_found", err ?? $"Plan '{planId}' not found.");
                steps = loaded["steps"] as JArray ?? loaded["operations"] as JArray;
            }

            if (steps == null || steps.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "empty_plan", "Provide non-empty 'steps' or a valid 'plan_id'.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Execute Plan ({steps.Count} steps)",
                dryRun,
                d =>
                {
                    int okSteps = 0, failedSteps = 0;
                    var log = new JArray();
                    foreach (var step in steps.OfType<JObject>())
                    {
                        string op = (step.Value<string>("op") ?? string.Empty).Trim().ToLowerInvariant();
                        if (op == "set_param")
                        {
                            var el = RevitElementKit.ResolveElement(d, step["element_id"]);
                            string pName = step.Value<string>("parameter") ?? string.Empty;
                            var p = el == null ? null : RevitElementKit.FindParameter(el, pName, step.Value<bool?>("include_type") ?? false);
                            if (p != null && RevitElementKit.TrySetParameter(p, step["value"], out _, step.Value<string>("unit")))
                            {
                                okSteps++;
                                log.Add(new JObject { ["op"] = op, ["element_id"] = step["element_id"], ["ok"] = true });
                            }
                            else
                            {
                                failedSteps++;
                                log.Add(new JObject { ["op"] = op, ["element_id"] = step["element_id"], ["ok"] = false });
                            }
                        }
                        else if (op == "delete")
                        {
                            var el = RevitElementKit.ResolveElement(d, step["element_id"]);
                            if (el != null)
                            {
                                var del = d.Delete(el.Id);
                                okSteps++;
                                log.Add(new JObject { ["op"] = op, ["element_id"] = step["element_id"], ["deleted"] = del.Count, ["ok"] = true });
                            }
                            else
                            {
                                failedSteps++;
                                log.Add(new JObject { ["op"] = op, ["element_id"] = step["element_id"], ["ok"] = false });
                            }
                        }
                        else
                        {
                            failedSteps++;
                            log.Add(new JObject { ["op"] = op, ["ok"] = false, ["error"] = "Unsupported plan op." });
                        }
                    }

                    return new JObject
                    {
                        ["plan_id"] = planId ?? string.Empty,
                        ["total_steps"] = steps.Count,
                        ["succeeded"] = okSteps,
                        ["failed"] = failedSteps,
                        ["log"] = log
                    };
                },
                (d, staged) => staged,
                $"Executed plan with {steps.Count} step(s).");
        }
    }

    [ScioTool("scio_copy_between_documents", "core", "Copy elements (types, views, or instances) from another open Revit document into the active document.", ReadOnly = false)]
    public sealed class CopyBetweenDocumentsCommand : IRevitCommand
    {
        [ScioParam("source_document_title", "Title of the open source Revit document.", Required = true)]
        public string SourceDocumentTitle { get; set; } = string.Empty;

        [ScioParam("element_ids", "Element IDs in the source document to copy.", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        [ScioParam("dry_run", "If true (default), previews the copy and rolls back.")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CopyBetweenDocumentsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var targetDoc = context.RequireActiveDocument(request.TargetDocument);
            string sourceTitle = request.Arguments.Value<string>("source_document_title") ?? string.Empty;
            Document? sourceDoc = null;
            foreach (Document d in context.UiApplication.Application.Documents)
            {
                if (string.Equals(d.Title, sourceTitle, StringComparison.OrdinalIgnoreCase))
                {
                    sourceDoc = d;
                    break;
                }
            }
            if (sourceDoc == null)
                return ToolResponse.Refusal(request.RequestId, "source_doc_not_found", $"Open source document '{sourceTitle}' not found.");

            var srcIds = RevitElementKit.ResolveElements(sourceDoc, request.Arguments["element_ids"] as JArray)
                .Select(e => e.Id)
                .ToList();
            if (srcIds.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_source_elements", "No valid source elements found.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                targetDoc,
                request.RequestId,
                $"Copy from {sourceDoc.Title}",
                dryRun,
                d =>
                {
                    var copied = ElementTransformUtils.CopyElements(
                        sourceDoc,
                        srcIds,
                        d,
                        Transform.Identity,
                        new CopyPasteOptions());
                    return new JObject
                    {
                        ["source_document"] = sourceDoc.Title,
                        ["requested_count"] = srcIds.Count,
                        ["copied_count"] = copied.Count,
                        ["copied_ids"] = new JArray(copied.Select(IdCompat.ToInt64))
                    };
                },
                (d, staged) =>
                {
                    int verified = 0;
                    if (staged["copied_ids"] is JArray arr)
                        verified = arr.Count(tok => RevitElementKit.ResolveElement(d, tok) != null);
                    staged["verified_copied_count"] = verified;
                    return staged;
                },
                $"Copied {srcIds.Count} element(s) from '{sourceDoc.Title}' to '{targetDoc.Title}'.");
        }
    }
}
