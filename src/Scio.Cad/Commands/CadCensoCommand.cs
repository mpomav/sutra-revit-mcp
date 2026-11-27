using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using Scio.Revit.Core;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// In-process AutoCAD / Civil 3D entity census.
    /// Streams full CSV to %USERPROFILE%\.scio\salidas\ so Scio.Server's SqliteIngestor
    /// automatically indexes it into scio_local.db for scio_query_local.
    /// </summary>
    internal static class CadCensoCommand
    {
        private sealed class GroupRow
        {
            public string Space;
            public string EntityType;
            public string Layer;
            public string BlockName;
            public int Count;
            public double TotalLength;
            public double TotalArea;
        }

        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            string spaceMode = ((string)args["space"] ?? "model").Trim().ToLowerInvariant();
            string layerFilter = ((string)args["layer_filter"] ?? "").Trim();
            int maxGroups = Math.Max(1, Math.Min(500, (int?)args["max_groups"] ?? 50));

            Database db = doc.Database;
            var groups = new Dictionary<string, GroupRow>(StringComparer.Ordinal);
            var rawRows = new List<string>();
            rawRows.Add("handle,space,entity_type,layer,block_name,length,area,color_index");

            int totalEntities = 0;

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var spacesToScan = new List<KeyValuePair<string, ObjectId>>();

                if (spaceMode == "model" || spaceMode == "all")
                    spacesToScan.Add(new KeyValuePair<string, ObjectId>("Model", bt[BlockTableRecord.ModelSpace]));

                if (spaceMode == "paper" || spaceMode == "all")
                {
                    foreach (ObjectId bid in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                        if (btr.IsLayout && bid != bt[BlockTableRecord.ModelSpace])
                            spacesToScan.Add(new KeyValuePair<string, ObjectId>(btr.Name, bid));
                    }
                }

                foreach (var sp in spacesToScan)
                {
                    var btr = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                    foreach (ObjectId eid in btr)
                    {
                        Entity ent = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;

                        string layer = ent.Layer ?? "";
                        if (!string.IsNullOrEmpty(layerFilter) &&
                            layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        string typeName = ent.GetRXClass().DxfName;
                        string blockName = "";
                        if (ent is BlockReference br)
                        {
                            try
                            {
                                ObjectId dynId = br.DynamicBlockTableRecord.IsValid ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                                var def = (BlockTableRecord)tr.GetObject(dynId, OpenMode.ForRead);
                                blockName = def.Name ?? "";
                            }
                            catch { blockName = br.Name ?? ""; }
                        }

                        double length = TryGetLength(ent);
                        double area = TryGetArea(ent);
                        totalEntities++;

                        string key = sp.Key + "|" + typeName + "|" + layer + "|" + blockName;
                        if (!groups.TryGetValue(key, out GroupRow g))
                        {
                            g = new GroupRow
                            {
                                Space = sp.Key,
                                EntityType = typeName,
                                Layer = layer,
                                BlockName = blockName
                            };
                            groups[key] = g;
                        }
                        g.Count++;
                        g.TotalLength += length;
                        g.TotalArea += area;

                        rawRows.Add(string.Join(",",
                            Csv(ent.Handle.ToString()),
                            Csv(sp.Key),
                            Csv(typeName),
                            Csv(layer),
                            Csv(blockName),
                            length.ToString("0.####", CultureInfo.InvariantCulture),
                            area.ToString("0.####", CultureInfo.InvariantCulture),
                            ent.ColorIndex.ToString(CultureInfo.InvariantCulture)));
                    }
                }

                tr.Commit();
            }

            string salidasDir = Path.Combine(ScioPaths.DataRoot(), "salidas");
            Directory.CreateDirectory(salidasDir);
            string safeDoc = SanitizeFileName(Path.GetFileNameWithoutExtension(doc.Name));
            string csvPath = Path.Combine(salidasDir, "cad_censo_" + safeDoc + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            File.WriteAllLines(csvPath, rawRows, new UTF8Encoding(false));

            var sorted = groups.Values.OrderByDescending(x => x.Count).ToList();
            var sample = new JArray();
            foreach (GroupRow g in sorted.Take(maxGroups))
            {
                sample.Add(new JObject
                {
                    ["space"] = g.Space,
                    ["entity_type"] = g.EntityType,
                    ["layer"] = g.Layer,
                    ["block_name"] = g.BlockName,
                    ["count"] = g.Count,
                    ["total_length"] = Math.Round(g.TotalLength, 4),
                    ["total_area"] = Math.Round(g.TotalArea, 4)
                });
            }

            return new JObject
            {
                ["document"] = Path.GetFileName(doc.Name),
                ["units"] = db.Insunits.ToString(),
                ["space"] = spaceMode,
                ["total_entities"] = totalEntities,
                ["total_groups"] = sorted.Count,
                ["csv_path"] = csvPath,
                ["top_groups"] = sample
            };
        }

        private static double TryGetLength(Entity ent)
        {
            try
            {
                if (ent is Curve c)
                    return c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);
            }
            catch { }
            return 0.0;
        }

        private static double TryGetArea(Entity ent)
        {
            try
            {
                if (ent is Curve c && c.Closed) return c.Area;
                if (ent is Hatch h) return h.Area;
                if (ent is Region r) return r.Area;
            }
            catch { }
            return 0.0;
        }

        private static string Csv(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "drawing";
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
