using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.AuditQa
{
    [ScioTool("scio_audit_model", "audit", "Run a comprehensive BIM health audit (warnings, orphan groups, unplaced rooms, unloaded links, CAD imports) with 0-100 health score.", ReadOnly = true)]
    public sealed class AuditModelCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(AuditModelCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            int warnings = doc.GetWarnings().Count;

            int unplacedRooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                .Count(r => r.Location == null || r.Area <= 0.0);

            var placedGroupTypeIds = new HashSet<long>(
                new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().Select(g => IdCompat.ToInt64(g.GroupType.Id)));
            int orphanGroupTypes = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                .Count(gt => !placedGroupTypeIds.Contains(IdCompat.ToInt64(gt.Id)));

            int cadImports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Count(i => !i.IsLinked);
            int unloadedLinks = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                .Count(lt => lt.GetLinkedFileStatus() != LinkedFileStatus.Loaded);
            int inPlaceFamilies = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Count(f => f.IsInPlace);

            int score = 100;
            score -= Math.Min(30, warnings / 10);
            score -= Math.Min(20, cadImports * 4);
            score -= Math.Min(15, unplacedRooms * 2);
            score -= Math.Min(15, orphanGroupTypes * 2);
            score -= Math.Min(10, unloadedLinks * 5);
            score -= Math.Min(10, inPlaceFamilies);
            score = Math.Max(0, score);

            var data = new JObject
            {
                ["document"] = doc.Title,
                ["health_score"] = score,
                ["warnings"] = warnings,
                ["unplaced_or_unbounded_rooms"] = unplacedRooms,
                ["orphan_group_types"] = orphanGroupTypes,
                ["cad_imports_not_linked"] = cadImports,
                ["unloaded_rvt_links"] = unloadedLinks,
                ["in_place_families"] = inPlaceFamilies
            };
            return ToolResponse.Success(request.RequestId, data, $"Model audit '{doc.Title}': score {score}/100 ({warnings} warnings, {cadImports} CAD imports).");
        }
    }

    [ScioTool("scio_clash_detect", "audit", "Detect 3D geometric intersections between two model categories using Revit's ElementIntersectsElementFilter.", ReadOnly = true)]
    public sealed class ClashDetectCommand : IRevitCommand
    {
        [ScioParam("category_a", "First category name (e.g. Structural Framing).", Required = true)]
        public string CategoryA { get; set; } = string.Empty;

        [ScioParam("category_b", "Second category name (e.g. Pipes, Ducts).", Required = true)]
        public string CategoryB { get; set; } = string.Empty;

        [ScioParam("max_clashes", "Maximum clash pairs to report (default 200).")]
        public int MaxClashes { get; set; } = 200;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ClashDetectCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var catA = RevitElementKit.ResolveDocumentCategory(doc, request.Arguments.Value<string>("category_a"));
            var catB = RevitElementKit.ResolveDocumentCategory(doc, request.Arguments.Value<string>("category_b"));
            if (catA == null || catB == null)
                return ToolResponse.Refusal(request.RequestId, "unknown_category", "Both 'category_a' and 'category_b' must be valid document categories.");

            int limit = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("max_clashes") ?? 200));
            var elemsA = new FilteredElementCollector(doc).OfCategoryId(catA.Id).WhereElementIsNotElementType().ToList();
            bool sameCat = catA.Id == catB.Id;

            var clashes = new JArray();
            var seenPairs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var a in elemsA)
            {
                if (clashes.Count >= limit) break;
                try
                {
                    var hits = new FilteredElementCollector(doc)
                        .OfCategoryId(catB.Id)
                        .WhereElementIsNotElementType()
                        .WherePasses(new ElementIntersectsElementFilter(a))
                        .ToElements();

                    long idA = IdCompat.ToInt64(a.Id);
                    foreach (var b in hits)
                    {
                        long idB = IdCompat.ToInt64(b.Id);
                        if (idA == idB) continue;
                        string key = sameCat ? (idA < idB ? $"{idA}:{idB}" : $"{idB}:{idA}") : $"{idA}:{idB}";
                        if (!seenPairs.Add(key)) continue;

                        var bbA = a.get_BoundingBox(null);
                        var bbB = b.get_BoundingBox(null);
                        JArray? centerMm = null;
                        if (bbA != null && bbB != null)
                        {
                            double cx = (Math.Max(bbA.Min.X, bbB.Min.X) + Math.Min(bbA.Max.X, bbB.Max.X)) * 0.5;
                            double cy = (Math.Max(bbA.Min.Y, bbB.Min.Y) + Math.Min(bbA.Max.Y, bbB.Max.Y)) * 0.5;
                            double cz = (Math.Max(bbA.Min.Z, bbB.Min.Z) + Math.Min(bbA.Max.Z, bbB.Max.Z)) * 0.5;
                            centerMm = new JArray(Math.Round(UnitKit.FeetToMm(cx), 1), Math.Round(UnitKit.FeetToMm(cy), 1), Math.Round(UnitKit.FeetToMm(cz), 1));
                        }

                        clashes.Add(new JObject
                        {
                            ["element_a"] = idA,
                            ["name_a"] = a.Name,
                            ["element_b"] = idB,
                            ["name_b"] = b.Name,
                            ["point_mm"] = centerMm
                        });
                        if (clashes.Count >= limit) break;
                    }
                }
                catch { /* skip non-solid elements */ }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["category_a"] = catA.Name,
                ["category_b"] = catB.Name,
                ["clash_count"] = clashes.Count,
                ["clashes"] = clashes
            }, $"Found {clashes.Count} clash(es) between '{catA.Name}' and '{catB.Name}'.");
        }
    }

    [ScioTool("scio_check_clearances", "audit", "Check minimum vertical headroom clearance (in mm) of elements in a category above their associated level.", ReadOnly = true)]
    public sealed class CheckClearancesCommand : IRevitCommand
    {
        [ScioParam("category", "Category to check (e.g. Ducts, Pipes, Structural Framing).", Required = true)]
        public string CategoryName { get; set; } = string.Empty;

        [ScioParam("min_headroom_mm", "Minimum required clear height above level in mm (default 2400).")]
        public double MinHeadroomMm { get; set; } = 2400;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CheckClearancesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var cat = RevitElementKit.ResolveDocumentCategory(doc, request.Arguments.Value<string>("category"));
            if (cat == null) return ToolResponse.Refusal(request.RequestId, "unknown_category", "Valid 'category' required.");

            double minMm = request.Arguments.Value<double?>("min_headroom_mm") ?? 2400.0;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            var violations = new JArray();
            int checkedCount = 0;

            foreach (var el in new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType())
            {
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                checkedCount++;
                var refLevel = (el.LevelId != ElementId.InvalidElementId ? doc.GetElement(el.LevelId) as Level : null)
                    ?? levels.LastOrDefault(l => l.Elevation <= bb.Min.Z)
                    ?? levels.FirstOrDefault();
                if (refLevel == null) continue;

                double clearMm = UnitKit.FeetToMm(bb.Min.Z - refLevel.Elevation);
                if (clearMm > 0 && clearMm < minMm && violations.Count < 200)
                {
                    violations.Add(new JObject
                    {
                        ["id"] = IdCompat.ToInt64(el.Id),
                        ["name"] = el.Name,
                        ["level"] = refLevel.Name,
                        ["clearance_mm"] = Math.Round(clearMm, 1),
                        ["deficit_mm"] = Math.Round(minMm - clearMm, 1)
                    });
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["category"] = cat.Name,
                ["min_headroom_mm"] = minMm,
                ["checked_count"] = checkedCount,
                ["violation_count"] = violations.Count,
                ["violations"] = violations
            }, $"Clearance check on '{cat.Name}': {violations.Count} violation(s) below {minMm} mm.");
        }
    }

    [ScioTool("scio_quantity_takeoff", "audit", "Compute metric quantity takeoff (count, volume m3, area m2, length m) grouped by category, family, type, and level.", ReadOnly = true)]
    public sealed class QuantityTakeoffCommand : IRevitCommand
    {
        [ScioParam("category", "Optional category filter; omit to compute across all model categories.")]
        public string CategoryName { get; set; } = string.Empty;

        [ScioParam("save_json_path", "Optional absolute path to persist the takeoff JSON.")]
        public string SaveJsonPath { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(QuantityTakeoffCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string? catArg = request.Arguments.Value<string>("category");
            var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (!string.IsNullOrWhiteSpace(catArg))
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, catArg);
                if (cat == null) return ToolResponse.Refusal(request.RequestId, "unknown_category", $"Category '{catArg}' not found.");
                col = col.OfCategoryId(cat.Id);
            }

            var buckets = new Dictionary<string, (string Cat, string Family, string Type, string Level, int Count, double VolM3, double AreaM2, double LenM)>(StringComparer.OrdinalIgnoreCase);

            foreach (Element el in col)
            {
                if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                var et = doc.GetElement(el.GetTypeId()) as ElementType;
                string catName = el.Category.Name;
                string fam = et?.FamilyName ?? string.Empty;
                string typ = et?.Name ?? el.Name ?? string.Empty;
                string lvl = (el.LevelId != ElementId.InvalidElementId && doc.GetElement(el.LevelId) is Level l) ? l.Name : "(Unassigned)";

                double vol = ReadDouble(el, BuiltInParameter.HOST_VOLUME_COMPUTED, UnitKit.CuFeetToCuM);
                double area = ReadDouble(el, BuiltInParameter.HOST_AREA_COMPUTED, UnitKit.SqFeetToSqM);
                double len = ReadDouble(el, BuiltInParameter.CURVE_ELEM_LENGTH, f => UnitKit.FeetToMm(f) / 1000.0);

                string key = $"{catName}|{fam}|{typ}|{lvl}";
                if (buckets.TryGetValue(key, out var cur))
                    buckets[key] = (cur.Cat, cur.Family, cur.Type, cur.Level, cur.Count + 1, cur.VolM3 + vol, cur.AreaM2 + area, cur.LenM + len);
                else
                    buckets[key] = (catName, fam, typ, lvl, 1, vol, area, len);
            }

            var rows = new JArray(buckets.Values.OrderBy(b => b.Cat).ThenBy(b => b.Type).Select(b => new JObject
            {
                ["category"] = b.Cat,
                ["family"] = b.Family,
                ["type"] = b.Type,
                ["level"] = b.Level,
                ["count"] = b.Count,
                ["volume_m3"] = Math.Round(b.VolM3, 3),
                ["area_m2"] = Math.Round(b.AreaM2, 3),
                ["length_m"] = Math.Round(b.LenM, 3)
            }));

            var data = new JObject
            {
                ["document"] = doc.Title,
                ["group_count"] = rows.Count,
                ["total_elements"] = buckets.Values.Sum(b => b.Count),
                ["total_volume_m3"] = Math.Round(buckets.Values.Sum(b => b.VolM3), 3),
                ["total_area_m2"] = Math.Round(buckets.Values.Sum(b => b.AreaM2), 3),
                ["rows"] = rows
            };

            string? savePath = request.Arguments.Value<string>("save_json_path");
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                string? dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(savePath!, data.ToString(Formatting.Indented));
                data["saved_path"] = savePath;
            }

            return ToolResponse.Success(request.RequestId, data, $"Quantity takeoff: {rows.Count} groups, {data["total_elements"]} elements.");
        }

        private static double ReadDouble(Element el, BuiltInParameter bip, Func<double, double> convert)
        {
            var p = el.get_Parameter(bip);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                return convert(p.AsDouble());
            return 0.0;
        }
    }

    [ScioTool("scio_audit_warnings", "audit", "Group and rank all Revit model warnings with affected element IDs.", ReadOnly = true)]
    public sealed class AuditWarningsCommand : IRevitCommand
    {
        [ScioParam("limit", "Maximum warning groups to return (default 50).")]
        public int Limit { get; set; } = 50;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(AuditWarningsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            int limit = Math.Max(1, Math.Min(500, request.Arguments.Value<int?>("limit") ?? 50));
            var all = doc.GetWarnings();
            var grouped = all.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(limit)
                .Select(g => new JObject
                {
                    ["description"] = g.Key,
                    ["count"] = g.Count(),
                    ["severity"] = g.First().GetSeverity().ToString(),
                    ["sample_ids"] = new JArray(g.SelectMany(w => w.GetFailingElements()).Distinct().Take(20).Select(IdCompat.ToInt64))
                });
            var arr = new JArray(grouped);
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["total_warnings"] = all.Count,
                ["unique_types"] = arr.Count,
                ["groups"] = arr
            }, $"Model has {all.Count} warnings across {arr.Count} distinct group(s).");
        }
    }

    [ScioTool("scio_audit_parameters", "audit", "Check required parameter completeness across model elements of a category (or entire model).", ReadOnly = true)]
    public sealed class AuditParametersCommand : IRevitCommand
    {
        [ScioParam("required_parameters", "List of parameter names that must be non-empty.", Required = true)]
        public List<string> RequiredParameters { get; set; } = new List<string>();

        [ScioParam("category", "Optional category filter.")]
        public string CategoryName { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(AuditParametersCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var reqParams = (request.Arguments["required_parameters"] as JArray)?.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            if (reqParams.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_params", "Provide non-empty 'required_parameters'.");

            var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            string? catArg = request.Arguments.Value<string>("category");
            if (!string.IsNullOrWhiteSpace(catArg))
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, catArg);
                if (cat == null) return ToolResponse.Refusal(request.RequestId, "unknown_category", $"Category '{catArg}' not found.");
                col = col.OfCategoryId(cat.Id);
            }

            int total = 0;
            var filledCounts = reqParams.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);
            var sampleMissing = new JArray();

            foreach (Element el in col)
            {
                if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                total++;
                var missingForEl = new List<string>();
                foreach (string pName in reqParams)
                {
                    var p = RevitElementKit.FindParameter(el, pName, includeType: true);
                    if (!string.IsNullOrWhiteSpace(RevitElementKit.ReadParameterDisplay(p)))
                        filledCounts[pName]++;
                    else
                        missingForEl.Add(pName);
                }
                if (missingForEl.Count > 0 && sampleMissing.Count < 100)
                {
                    sampleMissing.Add(new JObject
                    {
                        ["id"] = IdCompat.ToInt64(el.Id),
                        ["category"] = el.Category.Name,
                        ["missing"] = new JArray(missingForEl)
                    });
                }
            }

            var summary = new JArray(reqParams.Select(p => new JObject
            {
                ["parameter"] = p,
                ["filled"] = filledCounts[p],
                ["missing"] = total - filledCounts[p],
                ["coverage_pct"] = total == 0 ? 0.0 : Math.Round(100.0 * filledCounts[p] / total, 1)
            }));

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["total_elements"] = total,
                ["parameters"] = summary,
                ["sample_missing"] = sampleMissing
            }, $"Audited {reqParams.Count} parameter(s) across {total} element(s).");
        }
    }

    [ScioTool("scio_manage_links", "audit", "List all Revit/CAD links with load status, or reload/unload a RevitLinkType.", ReadOnly = false)]
    public sealed class ManageLinksCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default), reload, or unload.", EnumValues = new[] { "list", "reload", "unload" })]
        public string Action { get; set; } = "list";

        [ScioParam("link_type_id", "RevitLinkType ElementId for reload or unload.")]
        public long? LinkTypeId { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ManageLinksCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();

            if (action == "list")
            {
                var rvtLinks = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .Select(lt => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(lt.Id),
                        ["name"] = lt.Name,
                        ["kind"] = "rvt",
                        ["status"] = lt.GetLinkedFileStatus().ToString()
                    });
                var cadLinks = new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>()
                    .Select(ct => new JObject
                    {
                        ["id"] = IdCompat.ToInt64(ct.Id),
                        ["name"] = ct.Name,
                        ["kind"] = "cad"
                    });
                var arr = new JArray(rvtLinks.Concat(cadLinks));
                return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["links"] = arr }, $"Found {arr.Count} link(s).");
            }

            var target = RevitElementKit.ResolveElement(doc, request.Arguments["link_type_id"]) as RevitLinkType;
            if (target == null) return ToolResponse.Refusal(request.RequestId, "link_not_found", "Valid 'link_type_id' required.");

            if (action == "unload")
            {
                target.Unload(null);
                return ToolResponse.Success(request.RequestId, new JObject { ["id"] = IdCompat.ToInt64(target.Id), ["status"] = target.GetLinkedFileStatus().ToString() }, $"Unloaded link '{target.Name}'.");
            }

            var res = target.Reload();
            return ToolResponse.Success(request.RequestId, new JObject { ["id"] = IdCompat.ToInt64(target.Id), ["load_result"] = res.LoadResult.ToString() }, $"Reloaded link '{target.Name}'.");
        }
    }

    [ScioTool("scio_coordination_issues", "audit", "Export structured BCF-ready coordination issues JSON from element IDs with 3D centroids and active view metadata.", ReadOnly = true, OpenWorld = true)]
    public sealed class CoordinationIssuesCommand : IRevitCommand
    {
        [ScioParam("title", "Issue title.", Required = true)]
        public string Title { get; set; } = string.Empty;

        [ScioParam("description", "Issue description / action requested.")]
        public string Description { get; set; } = string.Empty;

        [ScioParam("element_ids", "Related element IDs.", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        [ScioParam("output_path", "Optional JSON output path (defaults to ~/.scio/salidas).")]
        public string OutputPath { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CoordinationIssuesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var elems = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray);
            string title = request.Arguments.Value<string>("title") ?? "Coordination Issue";
            string desc = request.Arguments.Value<string>("description") ?? string.Empty;

            var issue = new JObject
            {
                ["issue_guid"] = Guid.NewGuid().ToString(),
                ["created_utc"] = DateTime.UtcNow.ToString("o"),
                ["document"] = doc.Title,
                ["title"] = title,
                ["description"] = desc,
                ["active_view"] = context.ActiveUiDocument?.ActiveView?.Name ?? string.Empty,
                ["elements"] = new JArray(elems.Select(e => RevitElementKit.SummarizeElement(e, includeParams: false, includeBbox: true)))
            };

            string outPath = request.Arguments.Value<string>("output_path") ?? Path.Combine(ScioPaths.SalidasDir(), $"issue_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, issue.ToString(Formatting.Indented));
            issue["saved_path"] = outPath;

            return ToolResponse.Success(request.RequestId, issue, $"Saved coordination issue '{title}' to '{outPath}'.");
        }
    }
}
