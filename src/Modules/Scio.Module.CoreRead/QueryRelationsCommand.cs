using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CoreRead
{
    [ScioTool("scio_query_relations", "core", "Build a topological relationship graph for target elements: host, hosted dependents, joined geometry, MEP connections, level, and group.", ReadOnly = true)]
    public sealed class QueryRelationsCommand : IRevitCommand
    {
        [ScioParam("element_ids", "ElementIds to inspect (max 100).", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(QueryRelationsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var elems = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray).Take(100).ToList();
            if (elems.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "no_elements", "No valid elements resolved from 'element_ids'.");

            var nodes = new JArray();
            foreach (var el in elems)
            {
                long? hostId = null;
                if (el is FamilyInstance fi && fi.Host != null)
                    hostId = IdCompat.ToInt64(fi.Host.Id);

                var joined = new JArray();
                try
                {
                    foreach (var jid in JoinGeometryUtils.GetJoinedElements(doc, el))
                        joined.Add(IdCompat.ToInt64(jid));
                }
                catch { }

                var hosted = new JArray();
                try
                {
                    var depFilter = new ElementClassFilter(typeof(FamilyInstance));
                    foreach (var did in el.GetDependentElements(depFilter))
                    {
                        if (!did.Equals(el.Id)) hosted.Add(IdCompat.ToInt64(did));
                    }
                }
                catch { }

                var connectedMep = new JArray();
                ConnectorManager? cm = null;
                if (el is MEPCurve mc) cm = mc.ConnectorManager;
                else if (el is FamilyInstance mfi) cm = mfi.MEPModel?.ConnectorManager;
                if (cm != null)
                {
                    var seen = new HashSet<long>();
                    foreach (Connector c in cm.Connectors)
                    {
                        if (!c.IsConnected) continue;
                        foreach (Connector r in c.AllRefs)
                        {
                            if (r.Owner != null && !r.Owner.Id.Equals(el.Id) && seen.Add(IdCompat.ToInt64(r.Owner.Id)))
                                connectedMep.Add(IdCompat.ToInt64(r.Owner.Id));
                        }
                    }
                }

                var lvl = el.LevelId != ElementId.InvalidElementId ? doc.GetElement(el.LevelId) as Level : null;
                nodes.Add(new JObject
                {
                    ["id"] = IdCompat.ToInt64(el.Id),
                    ["category"] = el.Category?.Name ?? string.Empty,
                    ["name"] = el.Name,
                    ["level_id"] = lvl != null ? IdCompat.ToInt64(lvl.Id) : (long?)null,
                    ["level_name"] = lvl?.Name ?? string.Empty,
                    ["group_id"] = el.GroupId != ElementId.InvalidElementId ? IdCompat.ToInt64(el.GroupId) : (long?)null,
                    ["assembly_id"] = el.AssemblyInstanceId != ElementId.InvalidElementId ? IdCompat.ToInt64(el.AssemblyInstanceId) : (long?)null,
                    ["host_id"] = hostId,
                    ["hosted_ids"] = hosted,
                    ["joined_ids"] = joined,
                    ["connected_mep_ids"] = connectedMep
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["count"] = nodes.Count,
                ["nodes"] = nodes
            }, $"Resolved topological relations for {nodes.Count} element(s).");
        }
    }
}
