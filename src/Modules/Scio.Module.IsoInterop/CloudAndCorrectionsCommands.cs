using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.IsoInterop
{
    [ScioTool("scio_apply_corrections", "interop", "Apply verified parameter corrections from a scio.exchange/0 correction plan (dry_run=true by default).", ReadOnly = false)]
    public sealed class ApplyCorrectionsCommand : IRevitCommand
    {
        [ScioParam("corrections", "Array of {element_id, parameter_name, value} items.", Required = true)]
        public JArray Corrections { get; set; } = new JArray();

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ApplyCorrectionsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var list = (request.Arguments["corrections"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (list.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "empty_corrections", "'corrections' array must contain at least 1 item.");

            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;
            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Apply Exchange Corrections",
                dryRun,
                d =>
                {
                    int applied = 0, skipped = 0;
                    var receipts = new JArray();
                    foreach (var item in list)
                    {
                        var el = RevitElementKit.ResolveElement(d, item["element_id"]);
                        string pName = item.Value<string>("parameter_name") ?? string.Empty;
                        var valTok = item["value"];
                        var p = el?.LookupParameter(pName);
                        if (el == null || p == null || p.IsReadOnly || valTok == null)
                        {
                            skipped++;
                            receipts.Add(new JObject { ["element_id"] = item["element_id"], ["parameter_name"] = pName, ["status"] = "skipped" });
                            continue;
                        }

                        bool ok = false;
                        switch (p.StorageType)
                        {
                            case StorageType.String: ok = p.Set(valTok.ToString()); break;
                            case StorageType.Integer: ok = p.Set(valTok.Value<int>()); break;
                            case StorageType.Double: ok = p.Set(valTok.Value<double>()); break;
                        }
                        if (ok) applied++; else skipped++;
                        receipts.Add(new JObject { ["element_id"] = IdCompat.ToInt64(el.Id), ["parameter_name"] = pName, ["status"] = ok ? "applied" : "failed" });
                    }
                    return new JObject { ["applied"] = applied, ["skipped"] = skipped, ["receipts"] = receipts };
                },
                (d, staged) => staged,
                $"Applied corrections plan ({list.Count} items).");
        }
    }

    [ScioTool("scio_cde_cloud", "interop", "Local-first CDE/ACC project & issue workspace tracker (list_projects, list_states, inspect, versions, issues_list, issue_create, issue_update).", ReadOnly = false)]
    public sealed class CdeCloudCommand : IRevitCommand
    {
        [ScioParam("operation", "Operation to run.", Required = true, EnumValues = new[] { "list_projects", "list_states", "inspect", "versions", "issues_list", "issue_create", "issue_update" })]
        public string Operation { get; set; } = "list_projects";

        [ScioParam("title", "Issue title for issue_create.")]
        public string? Title { get; set; }

        [ScioParam("issue_id", "Issue ID for issue_update.")]
        public string? IssueId { get; set; }

        [ScioParam("status", "Issue status (open, resolved, closed) for issue_update.")]
        public string? Status { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CdeCloudCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "list_projects").Trim().ToLowerInvariant();
            string storePath = Path.Combine(ScioRuntimePaths.DataRoot, "cde-cloud", "workspace.json");
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);

            var ws = File.Exists(storePath) ? JObject.Parse(File.ReadAllText(storePath, Encoding.UTF8)) : new JObject
            {
                ["projects"] = new JArray(new JObject { ["name"] = doc.Title, ["states"] = new JArray("WIP", "SHARED", "PUBLISHED", "ARCHIVED") }),
                ["issues"] = new JArray()
            };

            if (op == "issue_create")
            {
                string title = (request.Arguments.Value<string>("title") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(title))
                    return ToolResponse.Refusal(request.RequestId, "missing_title", "'title' is required for issue_create.");
                var issue = new JObject
                {
                    ["id"] = "iss-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    ["document"] = doc.Title,
                    ["title"] = title,
                    ["status"] = "open",
                    ["created_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                ((JArray)ws["issues"]!).Add(issue);
                File.WriteAllText(storePath, ws.ToString(Formatting.Indented), new UTF8Encoding(false));
                return ToolResponse.Success(request.RequestId, issue, $"Created CDE issue '{issue["id"]}'.", VerificationStatus.Verified, true);
            }

            if (op == "issue_update")
            {
                string id = (request.Arguments.Value<string>("issue_id") ?? string.Empty).Trim();
                var target = ((JArray)ws["issues"]!).OfType<JObject>().FirstOrDefault(i => string.Equals(i.Value<string>("id"), id, StringComparison.Ordinal));
                if (target == null) return ToolResponse.Refusal(request.RequestId, "issue_not_found", $"Issue '{id}' not found.");
                if (request.Arguments["status"] != null) target["status"] = request.Arguments.Value<string>("status");
                target["updated_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                File.WriteAllText(storePath, ws.ToString(Formatting.Indented), new UTF8Encoding(false));
                return ToolResponse.Success(request.RequestId, target, $"Updated CDE issue '{id}'.", VerificationStatus.Verified, true);
            }

            if (op == "issues_list")
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = ((JArray)ws["issues"]!).Count, ["issues"] = ws["issues"] }, "Listed CDE issues.");

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["operation"] = op,
                ["active_document"] = doc.Title,
                ["is_workshared"] = doc.IsWorkshared,
                ["projects"] = ws["projects"]
            }, $"CDE cloud '{op}' completed.");
        }
    }

    [ScioTool("scio_acc_upload_status", "interop", "Inspect active Revit document cloud/worksharing status (IsModelInCloud, IsWorkshared, IsModified).", ReadOnly = true)]
    public sealed class AccUploadStatusCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(AccUploadStatusCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            bool inCloud = doc.IsModelInCloud;
            string cloudGuid = string.Empty;
            string projectGuid = string.Empty;
            if (inCloud)
            {
                try
                {
                    var mp = doc.GetCloudModelPath();
                    cloudGuid = mp?.GetModelGUID().ToString() ?? string.Empty;
                    projectGuid = mp?.GetProjectGUID().ToString() ?? string.Empty;
                }
                catch { }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["document"] = doc.Title,
                ["is_model_in_cloud"] = inCloud,
                ["is_workshared"] = doc.IsWorkshared,
                ["is_modified"] = doc.IsModified,
                ["cloud_model_guid"] = cloudGuid,
                ["cloud_project_guid"] = projectGuid
            }, inCloud ? $"Cloud model active (modified={doc.IsModified})." : "Active document is a local/file-based model (not in ACC cloud).");
        }
    }
}
