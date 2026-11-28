using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// Reads DBText, MText (format-stripped), and BlockReference AttributeReference entities with
    /// coordinates normalized to mm (matching linked DWGs in Revit). Supports compact [x_mm, y_mm, h_mm, text, layer]
    /// output, layer_filter, text_filter, bbox_mm / bbox, include_mtext, include_attributes, and limit.
    /// </summary>
    internal static class CadTextsCommand
    {
        private static readonly Regex MTextCodeRx = new Regex(@"\\(?:[A-Za-z][^;]*;|[{}~\\])|[{}]", RegexOptions.Compiled);

        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            string spaceMode = ((string)args["space"] ?? "model").Trim().ToLowerInvariant();
            string layerFilter = ((string)args["layer_filter"] ?? string.Empty).Trim();
            string textFilter = ((string)args["text_filter"] ?? string.Empty).Trim();
            bool includeMText = (bool?)args["include_mtext"] ?? true;
            bool includeAttributes = (bool?)args["include_attributes"] ?? true;
            bool compact = (bool?)args["compact"] ?? true;
            int limit = Math.Max(1, Math.Min(5000, (int?)args["limit"] ?? 500));

            Database db = doc.Database;
            double scaleToMm = ResolveScaleToMm(db.Insunits);

            bool hasBboxMm = false;
            double minXMm = 0, minYMm = 0, maxXMm = 0, maxYMm = 0;
            if (args["bbox_mm"] is JArray bbMm && bbMm.Count >= 4)
            {
                minXMm = Math.Min((double)bbMm[0], (double)bbMm[2]);
                maxXMm = Math.Max((double)bbMm[0], (double)bbMm[2]);
                minYMm = Math.Min((double)bbMm[1], (double)bbMm[3]);
                maxYMm = Math.Max((double)bbMm[1], (double)bbMm[3]);
                hasBboxMm = true;
            }
            else if (args["bbox"] is JArray bb && bb.Count >= 4)
            {
                minXMm = Math.Min((double)bb[0], (double)bb[2]) * scaleToMm;
                maxXMm = Math.Max((double)bb[0], (double)bb[2]) * scaleToMm;
                minYMm = Math.Min((double)bb[1], (double)bb[3]) * scaleToMm;
                maxYMm = Math.Max((double)bb[1], (double)bb[3]) * scaleToMm;
                hasBboxMm = true;
            }

            var results = new JArray();
            var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int totalMatched = 0;

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

                        if (ent is DBText dt)
                        {
                            string txt = CleanText(dt.TextString);
                            Point3d pt = dt.Justify == AttachmentPoint.BaseLeft ? dt.Position : dt.AlignmentPoint;
                            TryCollect(txt, dt.Layer ?? string.Empty, "TEXT", sp.Key, pt, dt.Height, dt.Rotation, null, null);
                        }
                        else if (includeMText && ent is MText mt)
                        {
                            string txt = CleanText(mt.Text);
                            TryCollect(txt, mt.Layer ?? string.Empty, "MTEXT", sp.Key, mt.Location, mt.TextHeight, mt.Rotation, null, null);
                        }
                        else if (includeAttributes && ent is BlockReference br && br.AttributeCollection != null && br.AttributeCollection.Count > 0)
                        {
                            string blkName = ResolveBlockName(tr, br);
                            foreach (ObjectId attId in br.AttributeCollection)
                            {
                                var ar = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                if (ar == null || ar.IsErased || ar.Invisible) continue;
                                string txt = CleanText(ar.TextString);
                                string layer = string.IsNullOrEmpty(ar.Layer) ? (br.Layer ?? string.Empty) : ar.Layer;
                                TryCollect(txt, layer, "ATTRIB", sp.Key, ar.Position, ar.Height, ar.Rotation, blkName, ar.Tag);
                            }
                        }
                    }
                }

                tr.Commit();
            }

            void TryCollect(string text, string layer, string kind, string space, Point3d pt, double height, double rotRad, string block, string tag)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                if (!string.IsNullOrEmpty(layerFilter) && layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                if (!string.IsNullOrEmpty(textFilter) && text.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                double xMm = pt.X * scaleToMm;
                double yMm = pt.Y * scaleToMm;
                if (hasBboxMm && (xMm < minXMm || xMm > maxXMm || yMm < minYMm || yMm > maxYMm))
                    return;

                totalMatched++;
                byLayer[layer] = byLayer.TryGetValue(layer, out int c) ? c + 1 : 1;

                if (results.Count < limit)
                {
                    int xi = (int)Math.Round(xMm);
                    int yi = (int)Math.Round(yMm);
                    int hi = (int)Math.Round(height * scaleToMm);

                    if (compact)
                    {
                        results.Add(new JArray(xi, yi, hi, text, layer));
                    }
                    else
                    {
                        var row = new JObject
                        {
                            ["kind"] = kind,
                            ["text"] = text,
                            ["layer"] = layer,
                            ["x_mm"] = xi,
                            ["y_mm"] = yi,
                            ["height_mm"] = hi,
                            ["rot_deg"] = Math.Round(rotRad * 180.0 / Math.PI, 1)
                        };
                        if (spaceMode == "all") row["space"] = space;
                        if (!string.IsNullOrEmpty(block)) row["block"] = block;
                        if (!string.IsNullOrEmpty(tag)) row["tag"] = tag;
                        results.Add(row);
                    }
                }
            }

            var byLayerObj = new JObject();
            foreach (var kv in byLayer.OrderByDescending(k => k.Value).Take(50))
                byLayerObj[kv.Key] = kv.Value;

            return new JObject
            {
                ["drawing"] = System.IO.Path.GetFileName(doc.Name ?? string.Empty),
                ["source_units"] = db.Insunits.ToString(),
                ["scale_to_mm"] = scaleToMm,
                ["space"] = spaceMode,
                ["compact"] = compact,
                ["format"] = compact ? "[x_mm, y_mm, height_mm, text, layer]" : "object",
                ["total_matched"] = totalMatched,
                ["returned"] = results.Count,
                ["truncated"] = totalMatched > results.Count,
                ["by_layer"] = byLayerObj,
                ["texts"] = results
            };
        }

        private static string CleanText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = raw.Replace("\\P", " ").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            s = MTextCodeRx.Replace(s, string.Empty);
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }

        private static double ResolveScaleToMm(UnitsValue units)
        {
            switch (units)
            {
                case UnitsValue.Meters: return 1000.0;
                case UnitsValue.Centimeters: return 10.0;
                case UnitsValue.Decimeters: return 100.0;
                case UnitsValue.Millimeters: return 1.0;
                case UnitsValue.Feet: return 304.8;
                case UnitsValue.Inches: return 25.4;
                default: return 1.0;
            }
        }

        private static string ResolveBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                ObjectId id = br.DynamicBlockTableRecord.IsValid ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var def = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                return def.Name ?? br.Name ?? string.Empty;
            }
            catch { return br.Name ?? string.Empty; }
        }
    }
}
