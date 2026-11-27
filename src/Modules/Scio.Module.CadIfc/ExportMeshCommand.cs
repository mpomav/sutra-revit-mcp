using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CadIfc
{
    [ScioTool(
        "scio_export_mesh",
        "cad_ifc",
        "Export lightweight 3D triangulated meshes for web viewers as binary glTF 2.0 (.glb) or geometry_json via Revit CustomExporter with scope filters and SHA-256 verification.",
        ReadOnly = true,
        OpenWorld = true)]
    public sealed class ExportMeshCommand : IRevitCommand
    {
        [ScioParam("format", "Output format: glb (glTF 2.0 binary, default) or geometry_json.", EnumValues = new[] { "glb", "geometry_json" })]
        public string Format { get; set; } = "glb";

        [ScioParam("output_path", "Absolute output file path (.glb or .json). Required when dry_run=false.")]
        public string OutputPath { get; set; } = string.Empty;

        [ScioParam("view_id", "Optional 3D View ElementId (defaults to active 3D view or first non-template 3D view).")]
        public long? ViewId { get; set; }

        [ScioParam("level_name", "Optional Level name filter.")]
        public string? LevelName { get; set; }

        [ScioParam("categories", "Optional list of model category names to include (e.g. ['Walls', 'Floors', 'Structural Columns']).")]
        public List<string>? Categories { get; set; }

        [ScioParam("element_ids", "Optional explicit list of ElementIds to export.")]
        public List<long>? ElementIds { get; set; }

        [ScioParam("include_parameters", "Optional parameter names to attach to each exported node's metadata.")]
        public List<string>? IncludeParameters { get; set; }

        [ScioParam("units", "Output units for geometry_json: mm (default) or m. Ignored for glb (glTF 2.0 is always meters).", EnumValues = new[] { "mm", "m" })]
        public string Units { get; set; } = "mm";

        [ScioParam("dry_run", "Preview element scope without writing files (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ExportMeshCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var args = request.Arguments ?? new JObject();

            string format = (args.Value<string>("format") ?? "glb").Trim().ToLowerInvariant();
            if (format != "glb" && format != "geometry_json")
                return ToolResponse.Refusal(request.RequestId, "invalid_format", $"Unsupported format '{format}'. Use 'glb' or 'geometry_json'.");

            bool dryRun = request.DryRun ?? args.Value<bool?>("dry_run") ?? true;
            string outputPath = (args.Value<string>("output_path") ?? string.Empty).Trim();
            if (!dryRun && string.IsNullOrWhiteSpace(outputPath))
                return ToolResponse.Refusal(request.RequestId, "missing_output_path", "Provide 'output_path' when dry_run=false.");

            var view3d = Resolve3DView(doc, args["view_id"]);
            if (view3d == null)
                return ToolResponse.Refusal(request.RequestId, "no_3d_view", "No non-template 3D view found in the active document.");

            string? levelFilter = args.Value<string>("level_name")?.Trim();
            var catFilter = new HashSet<string>(
                (args["categories"] as JArray)?.Select(t => t.ToString().Trim()).Where(s => !string.IsNullOrEmpty(s))
                ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var explicitIds = new HashSet<long>();
            if (args["element_ids"] is JArray idArr)
            {
                foreach (var t in idArr)
                {
                    if (long.TryParse(t.ToString(), out long id))
                        explicitIds.Add(id);
                }
            }

            var paramNames = (args["include_parameters"] as JArray)?
                .Select(t => t.ToString().Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            var targetElements = CollectTargetElements(doc, view3d, explicitIds, catFilter, levelFilter);

            if (dryRun)
            {
                var byCat = new JObject();
                foreach (var g in targetElements.GroupBy(e => e.Category?.Name ?? "Uncategorized").OrderBy(g => g.Key))
                    byCat[g.Key] = g.Count();

                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["dry_run"] = true,
                    ["format"] = format,
                    ["view_id"] = IdCompat.ToInt64(view3d.Id),
                    ["view_name"] = view3d.Name,
                    ["output_path"] = outputPath,
                    ["target_element_count"] = targetElements.Count,
                    ["by_category"] = byCat
                }, $"Dry-run: {targetElements.Count} element(s) in scope for {format} export from 3D view '{view3d.Name}'.");
            }

            var targetIdSet = new HashSet<long>(targetElements.Select(e => IdCompat.ToInt64(e.Id)));
            var exportContext = new ScioMeshExportContext(doc, targetIdSet, paramNames);

            using (var exporter = new CustomExporter(doc, exportContext))
            {
                exporter.IncludeGeometricObjects = false;
                exporter.ShouldStopOnError = false;
                exporter.Export(view3d);
            }

            var exportedNodes = exportContext.Nodes.Values.Where(n => n.TriangleCount > 0).ToList();
            var exportedIdSet = new HashSet<long>(exportedNodes.Select(n => n.ElementId));
            var omitted = new List<MeshOmittedItem>();
            foreach (var el in targetElements)
            {
                long id = IdCompat.ToInt64(el.Id);
                if (!exportedIdSet.Contains(id))
                {
                    omitted.Add(new MeshOmittedItem
                    {
                        ElementId = id,
                        UniqueId = el.UniqueId,
                        Category = el.Category?.Name ?? string.Empty,
                        Reason = "no_exported_geometry"
                    });
                }
            }

            if (format == "glb")
            {
                if (!outputPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                    outputPath += ".glb";
                MeshExportKit.WriteGlb(outputPath, exportedNodes, doc.Title);
            }
            else
            {
                if (!outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    outputPath += ".json";
                string units = (args.Value<string>("units") ?? "mm").Trim().ToLowerInvariant();
                MeshExportKit.WriteGeometryJson(outputPath, exportedNodes, omitted, doc.Title, units);
            }

            var verification = MeshExportKit.VerifyExportFile(outputPath, format);
            if (!verification.Verified)
                return ToolResponse.Failure(request.RequestId, "mesh_verification_failed", verification.Error ?? "Post-commit mesh verification failed.");

            int totalTriangles = exportedNodes.Sum(n => n.TriangleCount);
            var omittedSample = new JArray(omitted.Take(50).Select(o => new JObject
            {
                ["element_id"] = o.ElementId,
                ["unique_id"] = o.UniqueId,
                ["category"] = o.Category,
                ["reason"] = o.Reason
            }));

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["dry_run"] = false,
                ["verified"] = true,
                ["format"] = format,
                ["path"] = verification.Path,
                ["size_bytes"] = verification.SizeBytes,
                ["sha256"] = verification.Sha256,
                ["view_id"] = IdCompat.ToInt64(view3d.Id),
                ["view_name"] = view3d.Name,
                ["target_element_count"] = targetElements.Count,
                ["node_count"] = verification.NodeCount,
                ["triangle_count"] = totalTriangles,
                ["omitted_count"] = omitted.Count,
                ["omitted_elements"] = omittedSample
            }, $"Exported {verification.NodeCount} node(s) ({totalTriangles} triangles) to '{Path.GetFileName(verification.Path)}' ({verification.SizeBytes / 1024} KB, SHA-256 verified).");
        }

        private static View3D? Resolve3DView(Document doc, JToken? viewIdToken)
        {
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                var resolved = RevitElementKit.ResolveElement(doc, viewIdToken) as View3D;
                if (resolved != null && !resolved.IsTemplate) return resolved;
            }

            if (doc.ActiveView is View3D active3d && !active3d.IsTemplate)
                return active3d;

            return new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);
        }

        private static List<Element> CollectTargetElements(
            Document doc,
            View3D view3d,
            HashSet<long> explicitIds,
            HashSet<string> catFilter,
            string? levelFilter)
        {
            IEnumerable<Element> source;
            if (explicitIds.Count > 0)
            {
                var list = new List<Element>();
                foreach (long id in explicitIds)
                {
                    var el = RevitElementKit.ResolveElement(doc, new JValue(id));
                    if (el != null) list.Add(el);
                }
                source = list;
            }
            else
            {
                source = new FilteredElementCollector(doc, view3d.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model);
            }

            var result = new List<Element>();
            foreach (var el in source)
            {
                if (el.Category == null) continue;
                if (catFilter.Count > 0 && !catFilter.Contains(el.Category.Name)) continue;
                if (!string.IsNullOrWhiteSpace(levelFilter))
                {
                    string lvl = ResolveLevelName(doc, el);
                    if (!string.Equals(lvl, levelFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                result.Add(el);
            }
            return result;
        }

        internal static string ResolveLevelName(Document doc, Element el)
        {
            if (el.LevelId != ElementId.InvalidElementId && doc.GetElement(el.LevelId) is Level l)
                return l.Name;
            var refLvl = el.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
            if (refLvl != null && refLvl != ElementId.InvalidElementId && doc.GetElement(refLvl) is Level rl)
                return rl.Name;
            return string.Empty;
        }
    }

    internal sealed class ScioMeshExportContext : IExportContext
    {
        private readonly Document _rootDoc;
        private readonly HashSet<long> _allowedElementIds;
        private readonly List<string> _paramNames;
        private readonly Stack<Transform> _transforms = new Stack<Transform>();
        private readonly Stack<Document> _docs = new Stack<Document>();

        private MeshNodeData? _currentNode;
        private byte _curR = 180, _curG = 180, _curB = 180;
        private double _curOpacity = 1.0;

        public Dictionary<long, MeshNodeData> Nodes { get; } = new Dictionary<long, MeshNodeData>();

        public ScioMeshExportContext(Document doc, HashSet<long> allowedElementIds, List<string> paramNames)
        {
            _rootDoc = doc;
            _allowedElementIds = allowedElementIds;
            _paramNames = paramNames;
            _transforms.Push(Transform.Identity);
            _docs.Push(doc);
        }

        public bool Start() => true;
        public void Finish() { }
        public bool IsCanceled() => false;

        public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;
        public void OnViewEnd(ElementId elementId) { }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            if (_docs.Count > 1) return RenderNodeAction.Skip; // keep host document element IDs clean
            long id = IdCompat.ToInt64(elementId);
            if (!_allowedElementIds.Contains(id))
            {
                _currentNode = null;
                return RenderNodeAction.Skip;
            }

            if (!Nodes.TryGetValue(id, out var existing))
            {
                var el = _rootDoc.GetElement(elementId);
                existing = new MeshNodeData
                {
                    ElementId = id,
                    UniqueId = el?.UniqueId ?? string.Empty,
                    Name = el?.Name ?? $"Element_{id}",
                    Category = el?.Category?.Name ?? string.Empty,
                    Level = el != null ? ExportMeshCommand.ResolveLevelName(_rootDoc, el) : string.Empty
                };

                if (el != null)
                {
                    var bb = el.get_BoundingBox(null);
                    if (bb != null)
                    {
                        existing.BboxMm = new Box3DMm
                        {
                            MinX = Math.Round(UnitKit.FeetToMm(bb.Min.X), 1),
                            MinY = Math.Round(UnitKit.FeetToMm(bb.Min.Y), 1),
                            MinZ = Math.Round(UnitKit.FeetToMm(bb.Min.Z), 1),
                            MaxX = Math.Round(UnitKit.FeetToMm(bb.Max.X), 1),
                            MaxY = Math.Round(UnitKit.FeetToMm(bb.Max.Y), 1),
                            MaxZ = Math.Round(UnitKit.FeetToMm(bb.Max.Z), 1)
                        };
                    }

                    foreach (string pName in _paramNames)
                    {
                        var p = el.LookupParameter(pName);
                        if (p != null && p.HasValue)
                            existing.Parameters[pName] = p.AsValueString() ?? p.AsString() ?? string.Empty;
                    }
                }

                Nodes[id] = existing;
            }

            _currentNode = existing;
            _curR = 180; _curG = 180; _curB = 180; _curOpacity = 1.0;
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            _currentNode = null;
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            if (_transforms.Count > 1) _transforms.Pop();
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            _docs.Push(node.GetDocument() ?? _rootDoc);
            return RenderNodeAction.Skip;
        }

        public void OnLinkEnd(LinkNode node)
        {
            if (_transforms.Count > 1) _transforms.Pop();
            if (_docs.Count > 1) _docs.Pop();
        }

        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
        public void OnFaceEnd(FaceNode node) { }

        public void OnMaterial(MaterialNode node)
        {
            if (node == null) return;
            if (node.Color != null && node.Color.IsValid)
            {
                _curR = node.Color.Red;
                _curG = node.Color.Green;
                _curB = node.Color.Blue;
            }
            _curOpacity = Math.Max(0.0, Math.Min(1.0, 1.0 - node.Transparency));
        }

        public void OnPolymesh(PolymeshTopology node)
        {
            if (_currentNode == null || node == null) return;
            var pts = node.GetPoints();
            var facets = node.GetFacets();
            if (pts == null || pts.Count == 0 || facets == null || facets.Count == 0) return;

            var bucket = _currentNode.Primitives.FirstOrDefault(p =>
                p.R == _curR && p.G == _curG && p.B == _curB && Math.Abs(p.Opacity - _curOpacity) < 0.02);

            if (bucket == null)
            {
                bucket = new MeshPrimitiveBucket { R = _curR, G = _curG, B = _curB, Opacity = _curOpacity };
                _currentNode.Primitives.Add(bucket);
            }

            var xf = _transforms.Peek();
            int baseVertex = bucket.PositionsMm.Count / 3;

            for (int i = 0; i < pts.Count; i++)
            {
                var wp = xf.OfPoint(pts[i]);
                bucket.PositionsMm.Add((float)UnitKit.FeetToMm(wp.X));
                bucket.PositionsMm.Add((float)UnitKit.FeetToMm(wp.Y));
                bucket.PositionsMm.Add((float)UnitKit.FeetToMm(wp.Z));
            }

            for (int i = 0; i < facets.Count; i++)
            {
                var f = facets[i];
                bucket.Indices.Add(baseVertex + f.V1);
                bucket.Indices.Add(baseVertex + f.V2);
                bucket.Indices.Add(baseVertex + f.V3);
            }
        }

        public void OnLight(LightNode node) { }
        public void OnRPC(RPCNode node) { }
    }
}
