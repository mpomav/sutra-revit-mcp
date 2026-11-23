// -----------------------------------------------------------------------------
// SCIO Revit MCP - Model & Session Context Command
//
// Returns live situational awareness: active view, selected elements,
// detected disciplines, loaded RVT links, and project levels.
// Gives the AI assistant immediate context ("trabaja sobre lo que estoy viendo").
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class ContextCommand : ICommand
    {
        public string Name => "scio_context";

        public string Description =>
            "Obtiene el contexto situacional inmediato de Revit: vista activa, elementos seleccionados " +
            "por el usuario, disciplinas detectadas, vinculos RVT y niveles del proyecto. " +
            "Permite al asistente responder a 'trabaja sobre lo que estoy viendo o seleccionando'.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            UIDocument uidoc = app?.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null)
            {
                return CommandResult.Fail("No hay ningun documento activo en Revit.");
            }

            bool includeElements = true;
            bool includeLevels = true;
            bool includeLinks = true;
            int maxSelectionIds = 50;

            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try
                {
                    var req = Scio.Revit.Core.JsonText.ParseObject(paramsJson);
                    if (req["include_elements"] != null) includeElements = req.Value<bool>("include_elements");
                    if (req["include_levels"] != null) includeLevels = req.Value<bool>("include_levels");
                    if (req["include_links"] != null) includeLinks = req.Value<bool>("include_links");
                    if (req["max_selection_ids"] != null)
                        maxSelectionIds = Math.Max(1, Math.Min(200, req.Value<int>("max_selection_ids")));
                }
                catch (JsonException ex)
                {
                    return CommandResult.Fail("Parametros invalidos: " + ex.Message);
                }
            }

            // 1. Active document
            var activeDocInfo = new
            {
                title = doc.Title,
                path = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                is_workshared = doc.IsWorkshared,
                is_family_document = doc.IsFamilyDocument,
                has_unsaved_changes = doc.IsModified
            };

            // 2. Active view
            View activeView = uidoc?.ActiveView;
            object activeViewInfo = null;
            if (activeView != null)
            {
                activeViewInfo = new
                {
                    id = Rid.Value(activeView.Id),
                    name = activeView.Name,
                    view_type = activeView.ViewType.ToString(),
                    discipline = activeView.Discipline.ToString(),
                    scale = activeView.Scale,
                    is_template = activeView.IsTemplate
                };
            }

            // 3. Selection (bounded to avoid token bloat on huge selections)
            var selIds = uidoc?.Selection?.GetElementIds();
            int selCount = selIds?.Count ?? 0;
            var idList = selIds != null
                ? selIds.Take(maxSelectionIds).Select(id => Rid.Value(id)).ToList()
                : new List<long>();
            var elementsSample = new List<object>();
            var selByCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (includeElements && selIds != null && selCount > 0)
            {
                int i = 0;
                foreach (ElementId id in selIds)
                {
                    Element e = doc.GetElement(id);
                    if (e == null) continue;

                    string catName = e.Category?.Name ?? "(Sin categoría)";
                    selByCategory[catName] = selByCategory.TryGetValue(catName, out int cCnt) ? cCnt + 1 : 1;

                    if (i < maxSelectionIds)
                    {
                        string famName = "(Sistema)";
                        string typeName = "(Estándar)";

                        ElementId typeId = e.GetTypeId();
                        if (typeId != null && typeId != ElementId.InvalidElementId)
                        {
                            var elemType = doc.GetElement(typeId) as ElementType;
                            if (elemType != null)
                            {
                                if (!string.IsNullOrWhiteSpace(elemType.FamilyName)) famName = elemType.FamilyName;
                                if (!string.IsNullOrWhiteSpace(elemType.Name)) typeName = elemType.Name;
                            }
                        }

                        elementsSample.Add(new
                        {
                            id = Rid.Value(e.Id),
                            category = catName,
                            family = famName,
                            type = typeName,
                            name = e.Name
                        });
                    }
                    i++;
                }
            }

            var selectionInfo = new
            {
                count = selCount,
                element_ids = idList,
                element_ids_truncated = selCount > maxSelectionIds,
                elements = elementsSample,
                by_category = selByCategory.OrderByDescending(kv => kv.Value).Take(20)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                truncated = selCount > maxSelectionIds
            };

            // 4. Disciplines detected by actual placed element counts (not doc.Settings.Categories)
            var disciplines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var disciplineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                int archCount = CountElementsInCategories(doc,
                    BuiltInCategory.OST_Walls, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows,
                    BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings);
                int structCount = CountElementsInCategories(doc,
                    BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Rebar);
                int mepCount = CountElementsInCategories(doc,
                    BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves,
                    BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
                    BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_PlumbingFixtures,
                    BuiltInCategory.OST_MechanicalEquipment);

                disciplineCounts["Arquitectura"] = archCount;
                disciplineCounts["Estructuras"] = structCount;
                disciplineCounts["MEP"] = mepCount;

                if (archCount > 0) disciplines.Add("Arquitectura");
                if (structCount > 0) disciplines.Add("Estructuras");
                if (mepCount > 0) disciplines.Add("MEP");
            }
            catch { }

            // 5. Links
            var linksList = new List<object>();
            if (includeLinks)
            {
                try
                {
                    var linkCollector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>();
                    foreach (var link in linkCollector)
                    {
                        Document linkedDoc = null;
                        try { linkedDoc = link.GetLinkDocument(); } catch { }
                        linksList.Add(new
                        {
                            name = link.Name,
                            is_loaded = linkedDoc != null,
                            document_title = linkedDoc?.Title
                        });
                    }
                }
                catch { }
            }

            // 6. Levels
            var levelsList = new List<object>();
            if (includeLevels)
            {
                try
                {
                    var levelCollector = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation);
                    foreach (var lvl in levelCollector)
                    {
                        levelsList.Add(new
                        {
                            id = Rid.Value(lvl.Id),
                            name = lvl.Name,
                            elevation_m = Math.Round(lvl.Elevation * 0.3048, 3)
                        });
                    }
                }
                catch { }
            }

            var result = new JObject
            {
                ["active_document"] = JObject.FromObject(activeDocInfo),
                ["active_view"] = activeViewInfo != null ? JObject.FromObject(activeViewInfo) : null,
                ["selection"] = JObject.FromObject(selectionInfo),
                ["disciplines_detected"] = new JArray(disciplines.OrderBy(d => d)),
                ["discipline_counts"] = JObject.FromObject(disciplineCounts),
                ["links"] = JArray.FromObject(linksList),
                ["levels"] = JArray.FromObject(levelsList)
            };

            return CommandResult.Ok(result);
        }

        private static int CountElementsInCategories(Document doc, params BuiltInCategory[] categories)
        {
            if (doc == null || categories == null || categories.Length == 0) return 0;
            try
            {
                var filter = new ElementMulticategoryFilter(categories);
                return new FilteredElementCollector(doc)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
            }
            catch
            {
                return 0;
            }
        }
    }
}

