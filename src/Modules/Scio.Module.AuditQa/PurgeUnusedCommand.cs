using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.AuditQa
{
    [ScioTool("scio_purge_unused", "audit", "List or purge unused elements/types in the model (dry_run=true by default; uses native GetUnusedElements on Revit 2024+).", ReadOnly = false, Destructive = true)]
    public sealed class PurgeUnusedCommand : IRevitCommand
    {
        [ScioParam("limit", "Maximum unused elements to delete in one run (default 200).")]
        public int Limit { get; set; } = 200;

        [ScioParam("dry_run", "Preview unused candidates without deleting (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PurgeUnusedCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            int limit = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("limit") ?? 200));
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            var unusedIds = FindUnusedIds(doc);
            var byCat = unusedIds
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .GroupBy(e => e!.Category?.Name ?? e.GetType().Name)
                .OrderByDescending(g => g.Count())
                .Take(25)
                .Select(g => new JObject { ["category"] = g.Key, ["count"] = g.Count() })
                .ToList();

            if (dryRun)
            {
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["dry_run"] = true,
                    ["unused_count"] = unusedIds.Count,
                    ["would_delete"] = Math.Min(limit, unusedIds.Count),
                    ["by_category"] = new JArray(byCat)
                }, $"Purge preview: {unusedIds.Count} unused candidate(s) found (would delete up to {Math.Min(limit, unusedIds.Count)}).");
            }

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Purge Unused Elements",
                false,
                d =>
                {
                    var batch = unusedIds.Take(limit).ToList();
                    int deleted = 0;
                    foreach (var id in batch)
                    {
                        try
                        {
                            var removed = d.Delete(id);
                            if (removed != null && removed.Count > 0) deleted++;
                        }
                        catch { }
                    }
                    return new JObject { ["attempted"] = batch.Count, ["deleted"] = deleted, ["remaining_estimate"] = Math.Max(0, unusedIds.Count - deleted) };
                },
                (d, staged) => staged,
                $"Purged unused elements (batch limit {limit}).");
        }

        private static List<ElementId> FindUnusedIds(Document doc)
        {
#if REVIT2024_OR_GREATER
            return doc.GetUnusedElements(new HashSet<ElementId>()).ToList();
#else
            var usedTypeIds = new HashSet<long>(
                new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .Select(e => IdCompat.ToInt64(e.GetTypeId())));
            return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(fs => !usedTypeIds.Contains(IdCompat.ToInt64(fs.Id)))
                .Select(fs => fs.Id)
                .ToList();
#endif
        }
    }
}
