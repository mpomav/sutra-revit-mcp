using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CoreRead
{
    [ScioTool("scio_list_elements", "core", "List model elements filtered by category, level, family, type, active view, or parameter value.", ReadOnly = true)]
    public sealed class ListElementsCommand : IRevitCommand
    {
        [ScioParam("category", "Category name or BuiltInCategory (e.g. Walls, OST_Doors, Structural Columns).")]
        public string CategoryName { get; set; } = string.Empty;

        [ScioParam("level", "Optional level name filter (case-insensitive).")]
        public string LevelName { get; set; } = string.Empty;

        [ScioParam("family", "Optional family name substring filter.")]
        public string FamilyFilter { get; set; } = string.Empty;

        [ScioParam("type", "Optional type name substring filter.")]
        public string TypeFilter { get; set; } = string.Empty;

        [ScioParam("param_name", "Optional parameter name to filter by.")]
        public string ParamName { get; set; } = string.Empty;

        [ScioParam("param_value", "Optional expected value substring when param_name is set.")]
        public string ParamValue { get; set; } = string.Empty;

        [ScioParam("active_view_only", "If true, restrict collector to elements visible in the active view.")]
        public bool ActiveViewOnly { get; set; }

        [ScioParam("include_params", "If true, include non-empty instance parameters in output.")]
        public bool IncludeParams { get; set; }

        [ScioParam("include_bbox", "If true, include bounding box in mm.")]
        public bool IncludeBbox { get; set; }

        [ScioParam("limit", "Maximum number of elements to return (default 200, max 2000).")]
        public int Limit { get; set; } = 200;

        [ScioParam("offset", "Number of matched elements to skip for pagination.")]
        public int Offset { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ListElementsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string? catArg = request.Arguments.Value<string>("category");
            string? levelArg = request.Arguments.Value<string>("level");
            string? familyArg = request.Arguments.Value<string>("family");
            string? typeArg = request.Arguments.Value<string>("type");
            string? paramName = request.Arguments.Value<string>("param_name");
            string? paramVal = request.Arguments.Value<string>("param_value");
            bool activeViewOnly = request.Arguments.Value<bool?>("active_view_only") ?? false;
            bool includeParams = request.Arguments.Value<bool?>("include_params") ?? false;
            bool includeBbox = request.Arguments.Value<bool?>("include_bbox") ?? false;
            int limit = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("limit") ?? 200));
            int offset = Math.Max(0, request.Arguments.Value<int?>("offset") ?? 0);

            FilteredElementCollector collector = activeViewOnly && context.ActiveUiDocument?.ActiveView != null
                ? new FilteredElementCollector(doc, context.ActiveUiDocument.ActiveView.Id)
                : new FilteredElementCollector(doc);

            collector = collector.WhereElementIsNotElementType();

            if (!string.IsNullOrWhiteSpace(catArg))
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, catArg);
                if (cat == null)
                    return ToolResponse.Refusal(request.RequestId, "unknown_category", $"Category '{catArg}' was not found in '{doc.Title}'.");
                collector = collector.OfCategoryId(cat.Id);
            }

            ElementId? targetLevelId = null;
            if (!string.IsNullOrWhiteSpace(levelArg))
            {
                var lvl = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelArg, StringComparison.OrdinalIgnoreCase));
                if (lvl == null)
                    return ToolResponse.Refusal(request.RequestId, "unknown_level", $"Level '{levelArg}' not found.");
                targetLevelId = lvl.Id;
            }

            int totalMatched = 0;
            var items = new JArray();

            foreach (Element el in collector)
            {
                if (el.Category == null) continue;
                if (targetLevelId != null && el.LevelId != targetLevelId) continue;

                if (!string.IsNullOrWhiteSpace(familyArg) || !string.IsNullOrWhiteSpace(typeArg))
                {
                    var et = doc.GetElement(el.GetTypeId()) as ElementType;
                    if (!string.IsNullOrWhiteSpace(familyArg) &&
                        (et?.FamilyName ?? string.Empty).IndexOf(familyArg, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (!string.IsNullOrWhiteSpace(typeArg) &&
                        (et?.Name ?? el.Name ?? string.Empty).IndexOf(typeArg, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                if (!string.IsNullOrWhiteSpace(paramName))
                {
                    var p = RevitElementKit.FindParameter(el, paramName!, includeType: true);
                    if (p == null) continue;
                    if (!string.IsNullOrWhiteSpace(paramVal))
                    {
                        string display = RevitElementKit.ReadParameterDisplay(p);
                        if (display.IndexOf(paramVal, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                }

                totalMatched++;
                if (totalMatched <= offset) continue;
                if (items.Count >= limit) continue;

                items.Add(RevitElementKit.SummarizeElement(el, includeParams, includeBbox));
            }

            var data = new JObject
            {
                ["total_matched"] = totalMatched,
                ["returned"] = items.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["elements"] = items
            };
            return ToolResponse.Success(request.RequestId, data, $"Listed {items.Count} of {totalMatched} matching element(s).");
        }
    }

    [ScioTool("scio_query_model", "core", "Query structured model entities: levels, grids, views, sheets, rooms, families, types, links, or warnings.", ReadOnly = true)]
    public sealed class QueryModelCommand : IRevitCommand
    {
        [ScioParam("entity", "Entity kind to query.", Required = true, EnumValues = new[] { "levels", "grids", "views", "sheets", "rooms", "families", "types", "links", "warnings" })]
        public string Entity { get; set; } = "levels";

        [ScioParam("filter", "Optional case-insensitive name/category substring filter.")]
        public string Filter { get; set; } = string.Empty;

        [ScioParam("limit", "Maximum rows to return (default 300).")]
        public int Limit { get; set; } = 300;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(QueryModelCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string entity = (request.Arguments.Value<string>("entity") ?? "levels").Trim().ToLowerInvariant();
            string filter = request.Arguments.Value<string>("filter") ?? string.Empty;
            int limit = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("limit") ?? 300));

            var rows = new JArray();

            switch (entity)
            {
                case "levels":
                    foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.Elevation))
                    {
                        if (!Matches(l.Name, filter)) continue;
                        rows.Add(new JObject { ["id"] = IdCompat.ToInt64(l.Id), ["name"] = l.Name, ["elevation_mm"] = Math.Round(UnitKit.FeetToMm(l.Elevation), 1) });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "grids":
                    foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(x => x.Name))
                    {
                        if (!Matches(g.Name, filter)) continue;
                        var curve = g.Curve;
                        var p0 = curve.GetEndPoint(0);
                        var p1 = curve.GetEndPoint(1);
                        rows.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(g.Id),
                            ["name"] = g.Name,
                            ["start_mm"] = new JArray(Math.Round(UnitKit.FeetToMm(p0.X), 1), Math.Round(UnitKit.FeetToMm(p0.Y), 1)),
                            ["end_mm"] = new JArray(Math.Round(UnitKit.FeetToMm(p1.X), 1), Math.Round(UnitKit.FeetToMm(p1.Y), 1))
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "views":
                    foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).OrderBy(v => v.Name))
                    {
                        if (!Matches(v.Name, filter) && !Matches(v.ViewType.ToString(), filter)) continue;
                        rows.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(v.Id),
                            ["name"] = v.Name,
                            ["view_type"] = v.ViewType.ToString(),
                            ["scale"] = v.Scale,
                            ["level"] = v.GenLevel?.Name ?? string.Empty
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "sheets":
                    foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().OrderBy(s => s.SheetNumber))
                    {
                        if (!Matches(s.SheetNumber, filter) && !Matches(s.Name, filter)) continue;
                        rows.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(s.Id),
                            ["number"] = s.SheetNumber,
                            ["name"] = s.Name,
                            ["viewport_count"] = s.GetAllViewports().Count
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "rooms":
                    foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
                    {
                        if (!Matches(r.Name, filter) && !Matches(r.Number, filter)) continue;
                        rows.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(r.Id),
                            ["number"] = r.Number ?? string.Empty,
                            ["name"] = r.Name ?? string.Empty,
                            ["level"] = r.Level?.Name ?? string.Empty,
                            ["area_m2"] = Math.Round(UnitKit.SqFeetToSqM(r.Area), 2),
                            ["placed"] = r.Location != null
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "families":
                    foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().OrderBy(f => f.Name))
                    {
                        string catName = f.FamilyCategory?.Name ?? string.Empty;
                        if (!Matches(f.Name, filter) && !Matches(catName, filter)) continue;
                        rows.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(f.Id),
                            ["name"] = f.Name,
                            ["category"] = catName,
                            ["is_in_place"] = f.IsInPlace,
                            ["symbol_count"] = f.GetFamilySymbolIds().Count
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "types":
                    foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>())
                    {
                        string catName = t.Category?.Name ?? string.Empty;
                        if (!Matches(t.Name, filter) && !Matches(t.FamilyName, filter) && !Matches(catName, filter)) continue;
                        rows.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(t.Id),
                            ["family"] = t.FamilyName ?? string.Empty,
                            ["name"] = t.Name ?? string.Empty,
                            ["category"] = catName
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "links":
                    foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                    {
                        if (!Matches(link.Name, filter)) continue;
                        var linkDoc = link.GetLinkDocument();
                        rows.Add(new JObject
                        {
                            ["id"] = IdCompat.ToInt64(link.Id),
                            ["name"] = link.Name,
                            ["loaded"] = linkDoc != null,
                            ["link_title"] = linkDoc?.Title ?? string.Empty,
                            ["link_path"] = linkDoc?.PathName ?? string.Empty
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                case "warnings":
                    var grouped = doc.GetWarnings()
                        .GroupBy(w => w.GetDescriptionText())
                        .OrderByDescending(g => g.Count());
                    foreach (var g in grouped)
                    {
                        if (!Matches(g.Key, filter)) continue;
                        rows.Add(new JObject
                        {
                            ["description"] = g.Key,
                            ["count"] = g.Count(),
                            ["sample_element_ids"] = new JArray(g.First().GetFailingElements().Take(10).Select(IdCompat.ToInt64))
                        });
                        if (rows.Count >= limit) break;
                    }
                    break;

                default:
                    return ToolResponse.Refusal(request.RequestId, "invalid_entity", $"Unsupported entity '{entity}'.");
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["entity"] = entity,
                ["count"] = rows.Count,
                ["items"] = rows
            }, $"Returned {rows.Count} {entity} item(s).");
        }

        private static bool Matches(string? text, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            return (text ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    [ScioTool("scio_model_scan", "core", "Scan the active Revit model for category counts, levels, links, warnings, and optionally persist a snapshot JSON.", ReadOnly = true)]
    public sealed class ModelScanCommand : IRevitCommand
    {
        [ScioParam("save_snapshot_path", "Optional absolute JSON path where the scan snapshot (including element signatures) will be saved for later scio_model_diff.")]
        public string SaveSnapshotPath { get; set; } = string.Empty;

        [ScioParam("include_element_signatures", "If true (or when save_snapshot_path is set), capture per-element id/unique_id/category/type/level signatures.")]
        public bool IncludeSignatures { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ModelScanCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string? snapshotPath = request.Arguments.Value<string>("save_snapshot_path");
            bool captureSigs = (request.Arguments.Value<bool?>("include_element_signatures") ?? false) || !string.IsNullOrWhiteSpace(snapshotPath);

            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var signatures = captureSigs ? new JArray() : null;
            int totalInstances = 0;

            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                totalInstances++;
                string cat = el.Category.Name;
                categoryCounts[cat] = categoryCounts.TryGetValue(cat, out int c) ? c + 1 : 1;

                if (signatures != null)
                {
                    var sigObj = new JObject
                    {
                        ["id"] = IdCompat.ToInt64(el.Id),
                        ["uid"] = el.UniqueId,
                        ["cat"] = cat,
                        ["type_id"] = IdCompat.ToInt64(el.GetTypeId()),
                        ["level_id"] = IdCompat.ToInt64(el.LevelId),
                        ["name"] = el.Name ?? string.Empty
                    };
                    var bb = el.get_BoundingBox(null);
                    if (bb != null)
                    {
                        sigObj["bbox_mm"] = new JArray(
                            Math.Round(UnitKit.FeetToMm(bb.Min.X), 1),
                            Math.Round(UnitKit.FeetToMm(bb.Min.Y), 1),
                            Math.Round(UnitKit.FeetToMm(bb.Min.Z), 1),
                            Math.Round(UnitKit.FeetToMm(bb.Max.X), 1),
                            Math.Round(UnitKit.FeetToMm(bb.Max.Y), 1),
                            Math.Round(UnitKit.FeetToMm(bb.Max.Z), 1));
                    }
                    signatures.Add(sigObj);
                }
            }

            var catsArray = new JArray(categoryCounts
                .OrderByDescending(k => k.Value)
                .Select(k => new JObject { ["category"] = k.Key, ["count"] = k.Value }));

            var data = new JObject
            {
                ["document"] = doc.Title,
                ["path"] = doc.PathName,
                ["scanned_utc"] = DateTime.UtcNow.ToString("o"),
                ["total_model_elements"] = totalInstances,
                ["warning_count"] = doc.GetWarnings().Count,
                ["view_count"] = new FilteredElementCollector(doc).OfClass(typeof(View)).GetElementCount(),
                ["sheet_count"] = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount(),
                ["link_count"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount(),
                ["categories"] = catsArray
            };

            if (!string.IsNullOrWhiteSpace(snapshotPath))
            {
                var snapObj = (JObject)data.DeepClone();
                snapObj["elements"] = signatures;
                string? dir = Path.GetDirectoryName(snapshotPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(snapshotPath!, snapObj.ToString(Formatting.Indented));
                data["snapshot_saved"] = snapshotPath;
            }

            return ToolResponse.Success(request.RequestId, data, $"Scanned '{doc.Title}': {totalInstances} model elements across {categoryCounts.Count} categories.");
        }
    }

    [ScioTool("scio_model_diff", "core", "Compare the active Revit model against a previously saved scio_model_scan JSON snapshot.", ReadOnly = true)]
    public sealed class ModelDiffCommand : IRevitCommand
    {
        [ScioParam("baseline_snapshot_path", "Absolute path to the baseline JSON snapshot produced by scio_model_scan.", Required = true)]
        public string BaselinePath { get; set; } = string.Empty;

        [ScioParam("limit", "Maximum items per diff bucket (added, removed, modified). Default 200.")]
        public int Limit { get; set; } = 200;

        [ScioParam("geometry_tolerance_mm", "Bounding-box delta threshold in mm to flag geometry_changed (default 1.0).")]
        public double GeometryToleranceMm { get; set; } = 1.0;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ModelDiffCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string path = request.Arguments.Value<string>("baseline_snapshot_path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ToolResponse.Refusal(request.RequestId, "snapshot_not_found", $"Baseline snapshot not found: '{path}'.");

            int limit = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("limit") ?? 200));
            double geomTolMm = Math.Max(0.0, request.Arguments.Value<double?>("geometry_tolerance_mm") ?? 1.0);
            var baseline = JObject.Parse(File.ReadAllText(path));
            var baseElements = baseline["elements"] as JArray;
            if (baseElements == null)
                return ToolResponse.Refusal(request.RequestId, "missing_signatures", "Baseline snapshot does not contain 'elements' signatures.");

            var baseMap = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var item in baseElements.OfType<JObject>())
            {
                string uid = item.Value<string>("uid") ?? string.Empty;
                if (!string.IsNullOrEmpty(uid)) baseMap[uid] = item;
            }

            var added = new JArray();
            var modified = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int addedTotal = 0, modifiedTotal = 0;

            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                string uid = el.UniqueId;
                seen.Add(uid);

                long typeId = IdCompat.ToInt64(el.GetTypeId());
                long levelId = IdCompat.ToInt64(el.LevelId);

                if (!baseMap.TryGetValue(uid, out var prev))
                {
                    addedTotal++;
                    if (added.Count < limit)
                        added.Add(new JObject { ["id"] = IdCompat.ToInt64(el.Id), ["uid"] = uid, ["category"] = el.Category.Name, ["name"] = el.Name ?? string.Empty });
                }
                else
                {
                    long prevType = prev.Value<long>("type_id");
                    long prevLevel = prev.Value<long>("level_id");
                    string prevName = prev.Value<string>("name") ?? string.Empty;
                    bool typeChanged = prevType != typeId;
                    bool levelChanged = prevLevel != levelId;
                    bool nameChanged = !string.Equals(prevName, el.Name ?? string.Empty, StringComparison.Ordinal);

                    bool geomChanged = false;
                    JArray? currBbox = null;
                    if (prev["bbox_mm"] is JArray prevBbox && prevBbox.Count >= 6)
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb != null)
                        {
                            currBbox = new JArray(
                                Math.Round(UnitKit.FeetToMm(bb.Min.X), 1),
                                Math.Round(UnitKit.FeetToMm(bb.Min.Y), 1),
                                Math.Round(UnitKit.FeetToMm(bb.Min.Z), 1),
                                Math.Round(UnitKit.FeetToMm(bb.Max.X), 1),
                                Math.Round(UnitKit.FeetToMm(bb.Max.Y), 1),
                                Math.Round(UnitKit.FeetToMm(bb.Max.Z), 1));
                            for (int bi = 0; bi < 6; bi++)
                            {
                                if (Math.Abs(currBbox[bi].Value<double>() - prevBbox[bi].Value<double>()) > geomTolMm)
                                {
                                    geomChanged = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (typeChanged || levelChanged || nameChanged || geomChanged)
                    {
                        modifiedTotal++;
                        if (modified.Count < limit)
                        {
                            var modObj = new JObject
                            {
                                ["id"] = IdCompat.ToInt64(el.Id),
                                ["uid"] = uid,
                                ["category"] = el.Category.Name,
                                ["type_changed"] = typeChanged,
                                ["level_changed"] = levelChanged,
                                ["name_changed"] = nameChanged,
                                ["geometry_changed"] = geomChanged
                            };
                            if (geomChanged && currBbox != null)
                            {
                                modObj["bbox_before_mm"] = prev["bbox_mm"];
                                modObj["bbox_after_mm"] = currBbox;
                            }
                            modified.Add(modObj);
                        }
                    }
                }
            }

            var removed = new JArray();
            int removedTotal = 0;
            foreach (var kvp in baseMap)
            {
                if (seen.Contains(kvp.Key)) continue;
                removedTotal++;
                if (removed.Count < limit)
                {
                    removed.Add(new JObject
                    {
                        ["uid"] = kvp.Key,
                        ["prev_id"] = kvp.Value["id"],
                        ["category"] = kvp.Value["cat"],
                        ["name"] = kvp.Value["name"]
                    });
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["baseline_document"] = baseline.Value<string>("document"),
                ["current_document"] = doc.Title,
                ["added_count"] = addedTotal,
                ["removed_count"] = removedTotal,
                ["modified_count"] = modifiedTotal,
                ["added"] = added,
                ["removed"] = removed,
                ["modified"] = modified
            }, $"Diff vs baseline: +{addedTotal} added, -{removedTotal} removed, ~{modifiedTotal} modified.");
        }
    }

    [ScioTool("scio_navigate", "core", "Activate a Revit view by ID/name and/or zoom the UI to specific element IDs.", ReadOnly = false)]
    public sealed class NavigateCommand : IRevitCommand
    {
        [ScioParam("view_id", "Optional View ElementId to activate.")]
        public long? ViewId { get; set; }

        [ScioParam("view_name", "Optional View name to activate.")]
        public string ViewName { get; set; } = string.Empty;

        [ScioParam("element_ids", "Optional element IDs to zoom and select in the active view.")]
        public List<long>? ElementIds { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(NavigateCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var uiDoc = context.ActiveUiDocument!;

            long? viewId = request.Arguments.Value<long?>("view_id");
            string? viewName = request.Arguments.Value<string>("view_name");
            View? targetView = null;

            if (viewId.HasValue && viewId.Value > 0)
            {
                targetView = doc.GetElement(IdCompat.FromInt64(viewId.Value)) as View;
            }
            else if (!string.IsNullOrWhiteSpace(viewName))
            {
                targetView = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
            }

            if (targetView != null)
                uiDoc.ActiveView = targetView;

            var idsArr = request.Arguments["element_ids"] as JArray;
            int shownCount = 0;
            if (idsArr != null && idsArr.Count > 0)
            {
                var ids = RevitElementKit.ResolveElements(doc, idsArr).Select(e => e.Id).ToList();
                if (ids.Count > 0)
                {
                    uiDoc.Selection.SetElementIds(ids);
                    uiDoc.ShowElements(ids);
                    shownCount = ids.Count;
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["active_view"] = uiDoc.ActiveView?.Name ?? string.Empty,
                ["active_view_id"] = uiDoc.ActiveView == null ? -1 : IdCompat.ToInt64(uiDoc.ActiveView.Id),
                ["shown_elements"] = shownCount
            }, $"Navigated to view '{uiDoc.ActiveView?.Name}' ({shownCount} elements focused).");
        }
    }

    [ScioTool("scio_capture_view", "core", "Export a PNG image of the active view or a named/ID view and return it inline to the AI chat for visual QA and reading DWG/sheet text.", ReadOnly = true)]
    public sealed class CaptureViewCommand : IRevitCommand
    {
        [ScioParam("view_id", "Optional View ElementId to capture (defaults to active view).")]
        public long? ViewId { get; set; }

        [ScioParam("output_path", "Optional absolute PNG file path (defaults to temp folder).")]
        public string OutputPath { get; set; } = string.Empty;

        [ScioParam("pixel_size", "Image width/height in pixels (default 1600, range 400..4096).")]
        public int PixelSize { get; set; } = 1600;

        [ScioParam("inline_image", "Optional: when true, also returns the captured PNG inline to the AI chat as an MCP image content block for visual QA (default false to save tokens).")]
        public bool InlineImage { get; set; } = false;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CaptureViewCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            long? viewId = request.Arguments.Value<long?>("view_id");
            View? view = viewId.HasValue
                ? doc.GetElement(IdCompat.FromInt64(viewId.Value)) as View
                : context.ActiveUiDocument?.ActiveView;

            if (view == null)
                return ToolResponse.Refusal(request.RequestId, "view_not_found", "No valid view available to capture.");

            int pixels = Math.Max(400, Math.Min(4096, request.Arguments.Value<int?>("pixel_size") ?? 1600));
            string outPath = request.Arguments.Value<string>("output_path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outPath))
            {
                string dir = Path.Combine(Path.GetTempPath(), "ScioRevitCaptures");
                Directory.CreateDirectory(dir);
                outPath = Path.Combine(dir, $"view_{IdCompat.ToInt64(view.Id)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
            }
            else
            {
                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            string baseNoExt = Path.Combine(Path.GetDirectoryName(outPath) ?? Path.GetTempPath(), Path.GetFileNameWithoutExtension(outPath));
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = baseNoExt,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixels
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opts);

            string? written = Directory.GetFiles(Path.GetDirectoryName(baseNoExt) ?? Path.GetTempPath(), Path.GetFileName(baseNoExt) + "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            bool inlineImage = request.Arguments.Value<bool?>("inline_image") ?? false;
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["view_id"] = IdCompat.ToInt64(view.Id),
                ["view_name"] = view.Name,
                ["image_path"] = written ?? outPath,
                ["pixel_size"] = pixels,
                ["inline_image"] = inlineImage
            }, $"Captured view '{view.Name}' to '{written ?? outPath}'.");
        }
    }

    [ScioTool("scio_selection_exchange", "core", "Read the user's current Revit selection or set a new selection by element IDs.", ReadOnly = false)]
    public sealed class SelectionExchangeCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: get (default), set, or clear.", EnumValues = new[] { "get", "set", "clear" })]
        public string Action { get; set; } = "get";

        [ScioParam("element_ids", "Element IDs to select when action=set.")]
        public List<long>? ElementIds { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SelectionExchangeCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var uiDoc = context.ActiveUiDocument!;
            string action = (request.Arguments.Value<string>("action") ?? "get").Trim().ToLowerInvariant();

            if (action == "clear")
            {
                uiDoc.Selection.SetElementIds(new List<ElementId>());
            }
            else if (action == "set")
            {
                var ids = RevitElementKit.ResolveElements(doc, request.Arguments["element_ids"] as JArray)
                    .Select(e => e.Id)
                    .ToList();
                uiDoc.Selection.SetElementIds(ids);
            }

            var current = uiDoc.Selection.GetElementIds();
            var items = new JArray();
            foreach (var id in current)
            {
                var el = doc.GetElement(id);
                if (el != null) items.Add(RevitElementKit.SummarizeElement(el));
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["action"] = action,
                ["count"] = items.Count,
                ["elements"] = items
            }, $"Selection has {items.Count} element(s).");
        }
    }

    [ScioTool("scio_query_classification", "core", "Audit classification parameter coverage (Assembly Code, Keynote, OmniClass, or custom parameter) across model elements.", ReadOnly = true)]
    public sealed class QueryClassificationCommand : IRevitCommand
    {
        [ScioParam("parameter_name", "Classification parameter name (default 'Assembly Code').")]
        public string ParameterName { get; set; } = "Assembly Code";

        [ScioParam("category", "Optional category filter.")]
        public string CategoryName { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(QueryClassificationCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string paramName = request.Arguments.Value<string>("parameter_name") ?? "Assembly Code";
            string? catArg = request.Arguments.Value<string>("category");

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (!string.IsNullOrWhiteSpace(catArg))
            {
                var cat = RevitElementKit.ResolveDocumentCategory(doc, catArg);
                if (cat == null)
                    return ToolResponse.Refusal(request.RequestId, "unknown_category", $"Category '{catArg}' not found.");
                collector = collector.OfCategoryId(cat.Id);
            }

            int total = 0, classified = 0;
            var codeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (Element el in collector)
            {
                if (el.Category == null || el.Category.CategoryType != CategoryType.Model) continue;
                total++;
                var p = RevitElementKit.FindParameter(el, paramName, includeType: true);
                string val = RevitElementKit.ReadParameterDisplay(p).Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    classified++;
                    codeCounts[val] = codeCounts.TryGetValue(val, out int c) ? c + 1 : 1;
                }
            }

            double pct = total == 0 ? 0.0 : Math.Round(100.0 * classified / total, 1);
            var codes = new JArray(codeCounts.OrderByDescending(k => k.Value).Take(100)
                .Select(k => new JObject { ["code"] = k.Key, ["count"] = k.Value }));

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["parameter"] = paramName,
                ["total_elements"] = total,
                ["classified_elements"] = classified,
                ["unclassified_elements"] = total - classified,
                ["coverage_pct"] = pct,
                ["codes"] = codes
            }, $"Classification '{paramName}': {classified}/{total} ({pct}%) classified.");
        }
    }
}
