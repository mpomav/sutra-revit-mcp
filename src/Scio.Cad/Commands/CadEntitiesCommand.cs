using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// High-density AutoCAD / Civil 3D entity reader (scio_cad_entities) for TEXT, MTEXT, INSERT, and HATCH.
    /// Returns compact positional arrays in integer mm (WCS), handles dynamic block effective names,
    /// nested block traversal, outer hatch boundary tessellation (<= 100 mm chord), spatial deduplication,
    /// and offset/max_items pagination.
    /// </summary>
    internal static class CadEntitiesCommand
    {
        private static readonly Regex MTextCodeRx = new Regex(@"\\(?:[A-Za-z][^;]*;|[{}~\\])|[{}]", RegexOptions.Compiled);
        private const int MaxNestedDepth = 16;

        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            var typesArr = args["types"] as JArray;
            if (typesArr == null || typesArr.Count == 0)
                throw new ArgumentException("Field 'types' is required and must contain one or more of: TEXT, MTEXT, INSERT, HATCH.");

            var wantedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in typesArr)
            {
                string s = (t?.ToString() ?? string.Empty).Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(s)) wantedTypes.Add(s);
            }
            if (wantedTypes.Count == 0)
                throw new ArgumentException("Field 'types' must contain at least one valid type (TEXT, MTEXT, INSERT, HATCH).");

            var layersFilter = new List<string>();
            if (args["layers"] is JArray la)
            {
                foreach (var l in la)
                {
                    string s = (l?.ToString() ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(s)) layersFilter.Add(s);
                }
            }
            string layerMatch = ((string)args["layer_match"] ?? "exact").Trim().ToLowerInvariant();
            string spaceMode = ((string)args["space"] ?? "model").Trim().ToLowerInvariant();
            bool explodeNested = (bool?)args["explode_nested"] ?? true;
            bool includeAttributes = (bool?)args["include_attributes"] ?? true;
            bool includeHatchBoundary = (bool?)args["include_hatch_boundary"] ?? true;
            string textContains = ((string)args["text_contains"] ?? string.Empty).Trim();
            double dedupeTolMm = Math.Max(0.0, (double?)args["dedupe_tolerance_mm"] ?? 5.0);
            int maxItems = Math.Max(1, Math.Min(5000, (int?)args["max_items"] ?? 2000));
            int offset = Math.Max(0, (int?)args["offset"] ?? 0);

            bool hasBbox = false;
            double minXMm = 0, minYMm = 0, maxXMm = 0, maxYMm = 0;
            if (args["bbox_mm"] is JArray bb && bb.Count >= 4)
            {
                minXMm = Math.Min((double)bb[0], (double)bb[2]);
                maxXMm = Math.Max((double)bb[0], (double)bb[2]);
                minYMm = Math.Min((double)bb[1], (double)bb[3]);
                maxYMm = Math.Max((double)bb[1], (double)bb[3]);
                hasBbox = true;
            }

            Database db = doc.Database;
            double scaleToMm = ResolveScaleToMm(db.Insunits);

            var allItems = new List<(string Kind, JArray Row)>();
            var dedupeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dedupedCount = 0;

            bool LayerOk(string layer)
            {
                if (layersFilter.Count == 0) return true;
                if (layerMatch == "contains")
                    return layersFilter.Any(f => layer.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                return layersFilter.Any(f => string.Equals(layer, f, StringComparison.OrdinalIgnoreCase));
            }

            bool BboxOk(double xMm, double yMm) =>
                !hasBbox || (xMm >= minXMm && xMm <= maxXMm && yMm >= minYMm && yMm <= maxYMm);

            bool CheckDedupe(string prefix, string content, string layer, double xMm, double yMm, string extra = "")
            {
                if (dedupeTolMm <= 0.0) return true;
                long bx = (long)Math.Round(xMm / dedupeTolMm);
                long by = (long)Math.Round(yMm / dedupeTolMm);
                string key = prefix + "|" + content + "|" + layer + "|" + bx + "|" + by + "|" + extra;
                if (!dedupeKeys.Add(key))
                {
                    dedupedCount++;
                    return false;
                }
                return true;
            }

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var spacesToScan = new List<ObjectId>();

                if (spaceMode == "model" || spaceMode == "all")
                    spacesToScan.Add(bt[BlockTableRecord.ModelSpace]);

                if (spaceMode == "paper" || spaceMode == "all")
                {
                    foreach (ObjectId bid in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                        if (btr.IsLayout && bid != bt[BlockTableRecord.ModelSpace])
                            spacesToScan.Add(bid);
                    }
                }

                foreach (ObjectId spaceId in spacesToScan)
                {
                    var btr = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    Walk(tr, btr, Matrix3d.Identity, string.Empty, 0);
                }

                tr.Commit();
            }

            void Walk(Transaction tr, BlockTableRecord btr, Matrix3d xf, string parentLayer, int depth)
            {
                if (depth > MaxNestedDepth) return;
                foreach (ObjectId id in btr)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;

                    string effLayer = string.Equals(ent.Layer, "0", StringComparison.Ordinal) && !string.IsNullOrEmpty(parentLayer)
                        ? parentLayer
                        : (ent.Layer ?? string.Empty);

                    if (ent is DBText dt)
                    {
                        if (!wantedTypes.Contains("TEXT") || !LayerOk(effLayer)) continue;
                        string txt = CleanText(dt.TextString);
                        if (string.IsNullOrEmpty(txt)) continue;
                        if (!string.IsNullOrEmpty(textContains) && txt.IndexOf(textContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        Point3d rawPt = dt.Justify == AttachmentPoint.BaseLeft ? dt.Position : dt.AlignmentPoint;
                        Point3d pt = rawPt.TransformBy(xf);
                        double xMm = pt.X * scaleToMm;
                        double yMm = pt.Y * scaleToMm;
                        if (!BboxOk(xMm, yMm)) continue;
                        if (!CheckDedupe("T", txt, effLayer, xMm, yMm)) continue;

                        double scaleMag = GetUniformScale(xf);
                        double hMm = dt.Height * scaleMag * scaleToMm;
                        double rotDeg = NormalizeDeg((dt.Rotation + GetTransformRotation(xf)) * 180.0 / Math.PI);
                        allItems.Add(("T", new JArray((int)Math.Round(xMm), (int)Math.Round(yMm), (int)Math.Round(hMm), rotDeg, txt, effLayer)));
                    }
                    else if (ent is MText mt)
                    {
                        if (!wantedTypes.Contains("MTEXT") || !LayerOk(effLayer)) continue;
                        string txt = CleanText(mt.Text);
                        if (string.IsNullOrEmpty(txt)) continue;
                        if (!string.IsNullOrEmpty(textContains) && txt.IndexOf(textContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        Point3d pt = mt.Location.TransformBy(xf);
                        double xMm = pt.X * scaleToMm;
                        double yMm = pt.Y * scaleToMm;
                        if (!BboxOk(xMm, yMm)) continue;
                        if (!CheckDedupe("T", txt, effLayer, xMm, yMm)) continue;

                        double scaleMag = GetUniformScale(xf);
                        double hMm = mt.TextHeight * scaleMag * scaleToMm;
                        double rotDeg = NormalizeDeg((mt.Rotation + GetTransformRotation(xf)) * 180.0 / Math.PI);
                        allItems.Add(("T", new JArray((int)Math.Round(xMm), (int)Math.Round(yMm), (int)Math.Round(hMm), rotDeg, txt, effLayer)));
                    }
                    else if (ent is BlockReference br)
                    {
                        string blkName = ResolveEffectiveBlockName(tr, br);
                        if (wantedTypes.Contains("INSERT") && LayerOk(effLayer))
                        {
                            Point3d pt = br.Position.TransformBy(xf);
                            double xMm = pt.X * scaleToMm;
                            double yMm = pt.Y * scaleToMm;
                            if (BboxOk(xMm, yMm))
                            {
                                JObject attrs = includeAttributes ? ReadAttributes(tr, br) : null;
                                bool textOk = string.IsNullOrEmpty(textContains)
                                    || blkName.IndexOf(textContains, StringComparison.OrdinalIgnoreCase) >= 0
                                    || (attrs != null && attrs.Properties().Any(p => (p.Value?.ToString() ?? string.Empty).IndexOf(textContains, StringComparison.OrdinalIgnoreCase) >= 0));
                                if (textOk && CheckDedupe("I", blkName, effLayer, xMm, yMm))
                                {
                                    double rotDeg = NormalizeDeg((br.Rotation + GetTransformRotation(xf)) * 180.0 / Math.PI);
                                    double sx = Math.Round(br.ScaleFactors.X, 4);
                                    double sy = Math.Round(br.ScaleFactors.Y, 4);
                                    var row = new JArray((int)Math.Round(xMm), (int)Math.Round(yMm), rotDeg, sx, sy, blkName, effLayer);
                                    if (attrs != null && attrs.Count > 0) row.Add(attrs);
                                    else row.Add(new JObject());
                                    allItems.Add(("I", row));
                                }
                            }
                        }

                        if (explodeNested && br.BlockTableRecord.IsValid)
                        {
                            var childBtr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                            if (childBtr != null && !childBtr.IsLayout)
                            {
                                try
                                {
                                    Walk(tr, childBtr, xf * br.BlockTransform, effLayer, depth + 1);
                                }
                                catch { }
                            }
                        }
                    }
                    else if (ent is Hatch h)
                    {
                        if (!wantedTypes.Contains("HATCH") || !LayerOk(effLayer)) continue;
                        string pattern = h.PatternName ?? string.Empty;
                        if (!string.IsNullOrEmpty(textContains) && pattern.IndexOf(textContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        List<int[]> poly = ExtractOuterHatchPolygonMm(h, xf, scaleToMm);
                        if (poly.Count < 3) continue;

                        double cxMm = poly.Average(p => (double)p[0]);
                        double cyMm = poly.Average(p => (double)p[1]);
                        if (!BboxOk(cxMm, cyMm)) continue;

                        double areaM2 = ComputeHatchAreaM2(h, poly, xf, scaleToMm);
                        if (!CheckDedupe("H", pattern, effLayer, cxMm, cyMm, Math.Round(areaM2, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                            continue;

                        var row = new JArray(pattern, effLayer, Math.Round(areaM2, 3));
                        if (includeHatchBoundary)
                        {
                            var polyArr = new JArray();
                            foreach (var p in poly) polyArr.Add(new JArray(p[0], p[1]));
                            row.Add(polyArr);
                        }
                        allItems.Add(("H", row));
                    }
                }
            }

            int total = allItems.Count;
            var page = allItems.Skip(offset).Take(maxItems).ToList();
            int returned = page.Count;
            bool truncated = (offset + returned) < total;

            var textsArr = new JArray();
            var insertsArr = new JArray();
            var hatchesArr = new JArray();
            foreach (var item in page)
            {
                if (item.Kind == "T") textsArr.Add(item.Row);
                else if (item.Kind == "I") insertsArr.Add(item.Row);
                else if (item.Kind == "H") hatchesArr.Add(item.Row);
            }

            return new JObject
            {
                ["document"] = Path.GetFileName(doc.Name ?? string.Empty),
                ["units_source"] = db.Insunits.ToString(),
                ["scale_to_mm"] = scaleToMm,
                ["total"] = total,
                ["returned"] = returned,
                ["truncated"] = truncated,
                ["next_offset"] = truncated ? (JToken)(offset + returned) : JValue.CreateNull(),
                ["deduped"] = dedupedCount,
                ["texts"] = textsArr,
                ["inserts"] = insertsArr,
                ["hatches"] = hatchesArr
            };
        }

        private static List<int[]> ExtractOuterHatchPolygonMm(Hatch h, Matrix3d xf, double scaleToMm)
        {
            var pts = new List<int[]>();
            if (h.NumberOfLoops <= 0) return pts;

            int loopIdx = 0;
            for (int i = 0; i < h.NumberOfLoops; i++)
            {
                try
                {
                    if ((h.GetLoopAt(i).LoopType & HatchLoopTypes.External) != 0)
                    {
                        loopIdx = i;
                        break;
                    }
                }
                catch { }
            }

            HatchLoop loop;
            try { loop = h.GetLoopAt(loopIdx); }
            catch { return pts; }

            double maxChordDrawing = Math.Max(1e-4, 100.0 / scaleToMm);
            var rawPts = new List<Point2d>();

            if (loop.IsPolyline && loop.Polyline != null && loop.Polyline.Count >= 2)
            {
                var pl = loop.Polyline;
                int cnt = pl.Count;
                for (int i = 0; i < cnt; i++)
                {
                    BulgeVertex bv0 = pl[i];
                    BulgeVertex bv1 = pl[(i + 1) % cnt];
                    rawPts.Add(bv0.Vertex);
                    if (Math.Abs(bv0.Bulge) > 1e-4 && i < cnt - 1)
                    {
                        TessellateBulge(bv0.Vertex, bv1.Vertex, bv0.Bulge, maxChordDrawing, rawPts);
                    }
                }
            }
            else if (loop.Curves != null)
            {
                foreach (Curve2d c in loop.Curves)
                {
                    if (c == null) continue;
                    try
                    {
                        if (c is LineSegment2d ls)
                        {
                            rawPts.Add(ls.StartPoint);
                        }
                        else
                        {
                            double len = 0;
                            try { len = c.GetLength(c.GetParameterOf(c.StartPoint), c.GetParameterOf(c.EndPoint)); } catch { }
                            int segs = Math.Max(2, Math.Min(64, (int)Math.Ceiling(len / maxChordDrawing)));
                            Point2d[] samples = c.GetSamplePoints(segs + 1);
                            for (int k = 0; k < samples.Length - 1; k++)
                                rawPts.Add(samples[k]);
                        }
                    }
                    catch { }
                }
            }

            foreach (var p2 in rawPts)
            {
                Point3d p3 = new Point3d(p2.X, p2.Y, 0.0).TransformBy(xf);
                int xi = (int)Math.Round(p3.X * scaleToMm);
                int yi = (int)Math.Round(p3.Y * scaleToMm);
                if (pts.Count == 0 || pts[pts.Count - 1][0] != xi || pts[pts.Count - 1][1] != yi)
                    pts.Add(new[] { xi, yi });
            }
            if (pts.Count > 1 && pts[0][0] == pts[pts.Count - 1][0] && pts[0][1] == pts[pts.Count - 1][1])
                pts.RemoveAt(pts.Count - 1);

            return pts;
        }

        private static void TessellateBulge(Point2d p0, Point2d p1, double bulge, double maxChord, List<Point2d> acc)
        {
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-6) return;
            double theta = 4.0 * Math.Atan(Math.Abs(bulge));
            double sinHalf = Math.Sin(theta / 2.0);
            if (sinHalf < 1e-6) return;
            double radius = chord / (2.0 * sinHalf);
            double arcLen = radius * theta;
            int segs = Math.Max(2, Math.Min(32, (int)Math.Ceiling(arcLen / maxChord)));

            double mx = 0.5 * (p0.X + p1.X), my = 0.5 * (p0.Y + p1.Y);
            double cot = (1.0 - bulge * bulge) / (2.0 * bulge);
            double cx = mx - 0.5 * dy * cot;
            double cy = my + 0.5 * dx * cot;

            double a0 = Math.Atan2(p0.Y - cy, p0.X - cx);
            double sweep = bulge >= 0 ? theta : -theta;
            for (int k = 1; k < segs; k++)
            {
                double a = a0 + sweep * k / segs;
                acc.Add(new Point2d(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
            }
        }

        private static double ComputeHatchAreaM2(Hatch h, List<int[]> polyMm, Matrix3d xf, double scaleToMm)
        {
            try
            {
                double s = GetUniformScale(xf) * scaleToMm;
                double a = h.Area * s * s / 1_000_000.0;
                if (a > 0 && !double.IsNaN(a) && !double.IsInfinity(a)) return a;
            }
            catch { }

            // Fallback: shoelace area on mm polygon
            double sum = 0;
            for (int i = 0; i < polyMm.Count; i++)
            {
                var p0 = polyMm[i];
                var p1 = polyMm[(i + 1) % polyMm.Count];
                sum += (double)p0[0] * p1[1] - (double)p1[0] * p0[1];
            }
            return Math.Abs(sum) * 0.5 / 1_000_000.0;
        }

        private static JObject ReadAttributes(Transaction tr, BlockReference br)
        {
            var attrs = new JObject();
            if (br.AttributeCollection == null || br.AttributeCollection.Count == 0) return attrs;
            foreach (ObjectId attId in br.AttributeCollection)
            {
                var ar = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (ar == null || ar.IsErased) continue;
                string tag = (ar.Tag ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(tag)) continue;
                attrs[tag] = CleanText(ar.TextString);
            }
            return attrs;
        }

        private static string ResolveEffectiveBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                ObjectId id = br.DynamicBlockTableRecord.IsValid ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var def = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                return def.Name ?? br.Name ?? string.Empty;
            }
            catch { return br.Name ?? string.Empty; }
        }

        private static string CleanText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = raw.Replace("\\P", " | ").Replace("\r\n", " | ").Replace("\n", " | ").Replace("\r", " | ");
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

        private static double GetTransformRotation(Matrix3d xf)
        {
            Vector3d xAxis = Vector3d.XAxis.TransformBy(xf);
            return Math.Atan2(xAxis.Y, xAxis.X);
        }

        private static double GetUniformScale(Matrix3d xf)
        {
            double l = Vector3d.XAxis.TransformBy(xf).Length;
            return l > 1e-6 ? l : 1.0;
        }

        private static double NormalizeDeg(double deg)
        {
            double d = deg % 360.0;
            if (d < 0) d += 360.0;
            return Math.Round(d, 1);
        }
    }
}
