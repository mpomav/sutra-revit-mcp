using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Newtonsoft.Json.Linq;

namespace Scio.Cad.Commands
{
    internal static class CadContextCommand
    {
        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            bool includeLayers = (bool?)args["include_layers"] ?? false;
            bool includeXrefs = (bool?)args["include_xrefs"] ?? true;
            int maxItems = Math.Max(1, Math.Min(500, (int?)args["max_items"] ?? 50));

            Database db = doc.Database;
            Editor ed = doc.Editor;

            var layersArr = new JArray();
            var xrefsArr = new JArray();
            int layerCount = 0;
            int blockCount = 0;
            int xrefCount = 0;
            string currentLayer = "";
            string activeSpace = db.TileMode ? "Model" : "Paper";
            string activeLayout = LayoutManager.Current?.CurrentLayout ?? "";

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    layerCount++;
                    if (lid == db.Clayer) currentLayer = ltr.Name;
                    if (includeLayers && layersArr.Count < maxItems)
                    {
                        layersArr.Add(new JObject
                        {
                            ["name"] = ltr.Name,
                            ["color_aci"] = ltr.Color?.ColorIndex ?? 7,
                            ["is_off"] = ltr.IsOff,
                            ["is_frozen"] = ltr.IsFrozen,
                            ["is_locked"] = ltr.IsLocked
                        });
                    }
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId bid in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                    if (btr.IsLayout) continue;
                    blockCount++;
                    if (btr.IsFromExternalReference)
                    {
                        xrefCount++;
                        if (includeXrefs && xrefsArr.Count < maxItems)
                        {
                            xrefsArr.Add(new JObject
                            {
                                ["name"] = btr.Name,
                                ["path"] = btr.PathName,
                                ["status"] = btr.XrefStatus.ToString(),
                                ["is_overlay"] = btr.IsFromOverlayReference
                            });
                        }
                    }
                }

                tr.Commit();
            }

            int selectionCount = 0;
            var selectionTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                PromptSelectionResult sel = ed.SelectImplied();
                if (sel.Status == PromptStatus.OK && sel.Value != null)
                {
                    selectionCount = sel.Value.Count;
                    using (doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        int scanned = 0;
                        foreach (SelectedObject so in sel.Value)
                        {
                            if (so == null || scanned++ >= 200) break;
                            DBObject obj = tr.GetObject(so.ObjectId, OpenMode.ForRead);
                            string typeName = obj.GetRXClass().DxfName;
                            selectionTypes[typeName] = selectionTypes.TryGetValue(typeName, out int c) ? c + 1 : 1;
                        }
                        tr.Commit();
                    }
                }
            }
            catch { }

            var selBreakdown = new JObject();
            foreach (var kv in selectionTypes) selBreakdown[kv.Key] = kv.Value;

            bool c3dLoaded = Civil3DInspectCommand.IsCivil3DLoaded();
            string coordSystem = Civil3DInspectCommand.TryReadCoordinateSystem();

            var result = new JObject
            {
                ["host"] = c3dLoaded ? "civil3d" : "autocad",
                ["civil3d_loaded"] = c3dLoaded,
                ["document_title"] = Path.GetFileName(doc.Name),
                ["document_path"] = doc.Name,
                ["is_read_only"] = doc.IsReadOnly,
                ["units"] = db.Insunits.ToString(),
                ["coordinate_system"] = coordSystem,
                ["active_space"] = activeSpace,
                ["active_layout"] = activeLayout,
                ["current_layer"] = currentLayer,
                ["layer_count"] = layerCount,
                ["block_count"] = blockCount,
                ["xref_count"] = xrefCount,
                ["selection_count"] = selectionCount,
                ["selection_types"] = selBreakdown
            };

            if (includeLayers) result["layers"] = layersArr;
            if (includeXrefs) result["xrefs"] = xrefsArr;
            if (c3dLoaded) result["civil3d_summary"] = Civil3DInspectCommand.BuildSummary();

            return result;
        }
    }
}
