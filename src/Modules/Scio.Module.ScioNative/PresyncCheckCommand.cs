using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class PresyncCheckCommand : ICommand
    {
        public string Name => "scio_presync_check";

        public string Description =>
            "Verificacion previa a sincronizacion (pre-sync check): analiza asignacion de worksets, " +
            "conteo y severidad de advertencias activas en Revit, elementos de referencia (rejillas/niveles) " +
            "no bloqueados (unpinned) y elementos sin codigo de partida/clasificacion antes de sincronizar.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            int maxWarnings = request.Value<int?>("max_warnings") ?? 20;
            int maxElements = Math.Max(50, Math.Min(10000, request.Value<int?>("max_elements") ?? 2000));
            bool checkWorksets = request["check_worksets"] == null || request.Value<bool>("check_worksets");
            bool checkUnpinned = request["check_unpinned"] == null || request.Value<bool>("check_unpinned");
            bool checkUnclassified = request["check_unclassified"] == null || request.Value<bool>("check_unclassified");

            var blockers = new List<string>();
            var advisories = new List<string>();

            // 1. Workshared status & user worksets inspection
            bool isWorkshared = doc.IsWorkshared;
            string centralPath = null;
            int totalUserWorksets = 0;
            int openWorksets = 0;
            int closedWorksets = 0;
            var closedWorksetNames = new List<string>();
            if (isWorkshared)
            {
                try
                {
                    ModelPath mp = doc.GetWorksharingCentralModelPath();
                    centralPath = mp != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : "Local";
                }
                catch { centralPath = "Unavailable"; }

                if (checkWorksets)
                {
                    try
                    {
                        var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
                        foreach (Workset ws in worksets)
                        {
                            totalUserWorksets++;
                            if (ws.IsOpen) openWorksets++;
                            else
                            {
                                closedWorksets++;
                                if (closedWorksetNames.Count < 10) closedWorksetNames.Add(ws.Name);
                            }
                        }
                        if (closedWorksets > 0)
                        {
                            advisories.Add($"Hay {closedWorksets} workset(s) cerrado(s); elementos en esos subproyectos quedan fuera de revision local.");
                        }
                    }
                    catch { }
                }
            }

            // 2. Warnings
            IList<FailureMessage> warningsList = null;
            try { warningsList = doc.GetWarnings(); } catch { warningsList = new List<FailureMessage>(); }
            int totalWarnings = warningsList.Count;

            var criticalSamples = new List<string>();
            int criticalCount = 0;
            foreach (FailureMessage w in warningsList)
            {
                string desc = w.GetDescriptionText() ?? "";
                bool isCrit = desc.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              desc.IndexOf("overlap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              desc.IndexOf("superpuest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              desc.IndexOf("duplicad", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isCrit)
                {
                    criticalCount++;
                    if (criticalSamples.Count < 5) criticalSamples.Add(desc);
                }
            }

            if (totalWarnings > maxWarnings)
            {
                advisories.Add($"El modelo tiene {totalWarnings} advertencias activas (limite sugerido: {maxWarnings}).");
            }
            if (criticalCount > 0)
            {
                blockers.Add($"Se detectaron {criticalCount} advertencias criticas de superposicion o duplicidad.");
            }

            // 3. Unpinned Grids & Levels (Rejillas y Niveles)
            int unpinnedLevels = 0;
            int unpinnedGrids = 0;
            var unpinnedSample = new List<string>();

            if (checkUnpinned)
            {
                var levels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Levels).WhereElementIsNotElementType().ToElements();
                foreach (Element lvl in levels)
                {
                    if (!lvl.Pinned)
                    {
                        unpinnedLevels++;
                        if (unpinnedSample.Count < 5) unpinnedSample.Add($"Nivel '{lvl.Name}' (ID {Rid.Value(lvl.Id)}) no esta bloqueado (pinned).");
                    }
                }

                var grids = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Grids).WhereElementIsNotElementType().ToElements();
                foreach (Element grd in grids)
                {
                    if (!grd.Pinned)
                    {
                        unpinnedGrids++;
                        if (unpinnedSample.Count < 5) unpinnedSample.Add($"Rejilla/Eje '{grd.Name}' (ID {Rid.Value(grd.Id)}) no esta bloqueado (pinned).");
                    }
                }

                if (unpinnedLevels > 0 || unpinnedGrids > 0)
                {
                    blockers.Add($"Hay {unpinnedLevels} niveles y {unpinnedGrids} rejillas sin bloquear (unpinned).");
                }
            }

            // 4. Elementos sin partida / clasificacion (with explicit coverage & type fallback)
            int unclassifiedCount = 0;
            int totalCandidates = 0;
            int sampledCount = 0;
            bool coverageComplete = true;
            var unclassifiedSample = new List<long>();
            string paramName = null;
            if (checkUnclassified)
            {
                paramName = request.Value<string>("classification_parameter") ?? ProjectClassificationLookup.FindClassificationParameter(doc.PathName, out _);
                if (!string.IsNullOrEmpty(paramName))
                {
                    var catFilter = new ElementMulticategoryFilter(new[] {
                        BuiltInCategory.OST_Walls,
                        BuiltInCategory.OST_Floors,
                        BuiltInCategory.OST_StructuralColumns,
                        BuiltInCategory.OST_StructuralFraming
                    });
                    var baseCollector = new FilteredElementCollector(doc)
                        .WherePasses(catFilter)
                        .WhereElementIsNotElementType();
                    totalCandidates = baseCollector.GetElementCount();

                    foreach (Element e in baseCollector)
                    {
                        if (sampledCount >= maxElements)
                        {
                            coverageComplete = false;
                            break;
                        }
                        sampledCount++;
                        ElementType elemType = null;
                        ElementId tid = e.GetTypeId();
                        if (tid != null && tid != ElementId.InvalidElementId) elemType = doc.GetElement(tid) as ElementType;

                        Parameter p = e.LookupParameter(paramName) ?? elemType?.LookupParameter(paramName);
                        string val = p != null && p.HasValue ? (p.AsString() ?? p.AsValueString()) : null;
                        if (string.IsNullOrWhiteSpace(val))
                        {
                            unclassifiedCount++;
                            if (unclassifiedSample.Count < 10) unclassifiedSample.Add(Rid.Value(e.Id));
                        }
                    }

                    if (unclassifiedCount > 0)
                    {
                        advisories.Add($"{unclassifiedCount} de {sampledCount} elementos revisados carecen del parametro de clasificacion '{paramName}'.");
                    }
                    if (!coverageComplete)
                    {
                        advisories.Add($"Revision de clasificacion limitada a {sampledCount} de {totalCandidates} elementos (aumente max_elements si requiere barrido completo).");
                    }
                }
            }

            var issues = blockers.Concat(advisories).ToList();
            bool readyToSync = blockers.Count == 0 && advisories.Count == 0;
            string syncStatus = blockers.Count > 0
                ? "blocked"
                : (advisories.Count > 0 ? "advisories_only" : "ready");

            var result = new JObject
            {
                ["ready_to_sync"] = readyToSync,
                ["sync_status"] = syncStatus,
                ["verdict"] = blockers.Count > 0 ? "blocked" : (advisories.Count > 0 ? "issues_detected" : "ready_to_sync"),
                ["is_workshared"] = isWorkshared,
                ["central_path"] = centralPath ?? "N/A",
                ["worksets"] = new JObject
                {
                    ["checked"] = isWorkshared && checkWorksets,
                    ["total_user_worksets"] = totalUserWorksets,
                    ["open_count"] = openWorksets,
                    ["closed_count"] = closedWorksets,
                    ["closed_sample"] = new JArray(closedWorksetNames)
                },
                ["warnings"] = new JObject
                {
                    ["total"] = totalWarnings,
                    ["critical_count"] = criticalCount,
                    ["threshold"] = maxWarnings,
                    ["critical_sample"] = new JArray(criticalSamples)
                },
                ["unpinned_datum"] = new JObject
                {
                    ["unpinned_levels"] = unpinnedLevels,
                    ["unpinned_grids"] = unpinnedGrids,
                    ["sample"] = new JArray(unpinnedSample)
                },
                ["unclassified"] = new JObject
                {
                    ["parameter"] = paramName ?? "(no configurado)",
                    ["total_candidates"] = totalCandidates,
                    ["sampled_count"] = sampledCount,
                    ["coverage_complete"] = coverageComplete,
                    ["count_in_sample"] = unclassifiedCount,
                    ["sample_element_ids"] = new JArray(unclassifiedSample)
                },
                ["blockers"] = new JArray(blockers),
                ["advisories"] = new JArray(advisories),
                ["issues"] = new JArray(issues),
                ["recommendation"] = blockers.Count > 0
                    ? "Sincronizacion NO recomendada: existen bloqueadores criticos (advertencias criticas o ejes/niveles sin bloquear)."
                    : (advisories.Count > 0
                        ? "Sincronizacion posible con precaucion: revise las observaciones pendientes antes de sincronizar con el central."
                        : "El modelo esta en condiciones optimas para sincronizar con el archivo central.")
            };

            return CommandResult.Ok(result);
        }
    }
}

