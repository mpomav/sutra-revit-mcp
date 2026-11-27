using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Core
{
    /// <summary>
    /// Reusable, clean-room AutoCAD drafting primitives, style helpers, and transactional
    /// guardrails shared by cad_draft, cad_arch_plan, cad_detail, and future discipline tools.
    /// </summary>
    internal static class CadDraftingKit
    {
        public static Document RequireActiveDocument(JObject args, bool dryRun, out string activeTitle)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            activeTitle = Path.GetFileName(doc.Name);
            string targetDoc = ((string)args["target_document"] ?? "").Trim();
            if (!dryRun && !string.IsNullOrEmpty(targetDoc) &&
                !string.Equals(targetDoc, activeTitle, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetDoc, doc.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Active drawing is '" + activeTitle + "', not requested target_document '" + targetDoc + "'. Nothing was written.");
            }
            return doc;
        }

        public static Point3d ReadPoint3d(JToken tok, string field)
        {
            var arr = tok as JArray;
            if (arr == null || arr.Count < 2)
                throw new ArgumentException("Field '" + field + "' must be a [x, y] or [x, y, z] coordinate array.");
            double x = (double)arr[0];
            double y = (double)arr[1];
            double z = arr.Count >= 3 ? (double)arr[2] : 0.0;
            return new Point3d(x, y, z);
        }

        public static Point3d ReadPoint3dOrDefault(JToken tok, Point3d fallback)
        {
            var arr = tok as JArray;
            if (arr == null || arr.Count < 2) return fallback;
            double x = (double)arr[0];
            double y = (double)arr[1];
            double z = arr.Count >= 3 ? (double)arr[2] : 0.0;
            return new Point3d(x, y, z);
        }

        public static bool TryResolveHandle(Database db, string hexHandle, out ObjectId oid)
        {
            oid = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(hexHandle)) return false;
            if (!long.TryParse(hexHandle.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long val))
                return false;
            return db.TryGetObjectId(new Handle(val), out oid);
        }

        public static ObjectId EnsureLinetype(Database db, Transaction tr, string linetypeName)
        {
            if (string.IsNullOrWhiteSpace(linetypeName)) return ObjectId.Null;
            string name = linetypeName.Trim();
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (ltt.Has(name)) return ltt[name];

            foreach (string file in new[] { "acadiso.lin", "acad.lin" })
            {
                try
                {
                    db.LoadLineTypeFile(name, file);
                    if (ltt.Has(name)) return ltt[name];
                }
                catch
                {
                    // Ignore missing .lin or unknown pattern and try next
                }
            }
            return ObjectId.Null;
        }

        public static void EnsureLayer(
            Database db,
            Transaction tr,
            string name,
            short? colorAci = null,
            string linetype = null,
            double? lineweightMm = null,
            string description = null,
            bool? isPlottable = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string clean = name.Trim();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord ltr;

            if (lt.Has(clean))
            {
                ltr = (LayerTableRecord)tr.GetObject(lt[clean], OpenMode.ForWrite);
            }
            else
            {
                lt.UpgradeOpen();
                ltr = new LayerTableRecord { Name = clean };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }

            if (colorAci.HasValue && colorAci.Value >= 1 && colorAci.Value <= 255)
                ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, colorAci.Value);

            if (!string.IsNullOrWhiteSpace(linetype))
            {
                ObjectId ltId = EnsureLinetype(db, tr, linetype);
                if (!ltId.IsNull) ltr.LinetypeObjectId = ltId;
            }

            if (lineweightMm.HasValue)
                ltr.LineWeight = MapLineWeight(lineweightMm.Value);

            if (!string.IsNullOrWhiteSpace(description))
                ltr.Description = description.Trim();

            if (isPlottable.HasValue)
                ltr.IsPlottable = isPlottable.Value;
        }

        public static LineWeight MapLineWeight(double mm)
        {
            int hundredths = (int)Math.Round(mm * 100.0);
            if (hundredths <= 5) return LineWeight.LineWeight005;
            if (hundredths <= 9) return LineWeight.LineWeight009;
            if (hundredths <= 13) return LineWeight.LineWeight013;
            if (hundredths <= 15) return LineWeight.LineWeight015;
            if (hundredths <= 18) return LineWeight.LineWeight018;
            if (hundredths <= 20) return LineWeight.LineWeight020;
            if (hundredths <= 25) return LineWeight.LineWeight025;
            if (hundredths <= 30) return LineWeight.LineWeight030;
            if (hundredths <= 35) return LineWeight.LineWeight035;
            if (hundredths <= 40) return LineWeight.LineWeight040;
            if (hundredths <= 50) return LineWeight.LineWeight050;
            if (hundredths <= 53) return LineWeight.LineWeight053;
            if (hundredths <= 60) return LineWeight.LineWeight060;
            if (hundredths <= 70) return LineWeight.LineWeight070;
            return LineWeight.LineWeight080;
        }

        public static ObjectId EnsureTextStyle(Database db, Transaction tr, string name, string fontName = "Arial", double textHeight = 0.0)
        {
            if (string.IsNullOrWhiteSpace(name)) return db.Textstyle;
            string clean = name.Trim();
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            TextStyleTableRecord tsr;
            if (tst.Has(clean))
            {
                tsr = (TextStyleTableRecord)tr.GetObject(tst[clean], OpenMode.ForWrite);
            }
            else
            {
                tst.UpgradeOpen();
                tsr = new TextStyleTableRecord { Name = clean };
                tst.Add(tsr);
                tr.AddNewlyCreatedDBObject(tsr, true);
            }

            if (!string.IsNullOrWhiteSpace(fontName))
            {
                if (fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
                    tsr.FileName = fontName;
                else
                    tsr.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(fontName, false, false, 0, 0);
            }

            if (textHeight >= 0) tsr.TextSize = textHeight;
            return tsr.ObjectId;
        }

        public static ObjectId EnsureDimStyle(
            Database db,
            Transaction tr,
            string name,
            double textHeight = 0.18,
            double arrowSize = 0.15,
            double overallScale = 1.0,
            int precision = 2,
            double extOffset = 0.08,
            double extExtend = 0.08)
        {
            if (string.IsNullOrWhiteSpace(name)) return db.Dimstyle;
            string clean = name.Trim();
            var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            DimStyleTableRecord dsr;
            if (dst.Has(clean))
            {
                dsr = (DimStyleTableRecord)tr.GetObject(dst[clean], OpenMode.ForWrite);
            }
            else
            {
                dst.UpgradeOpen();
                dsr = new DimStyleTableRecord { Name = clean };
                dst.Add(dsr);
                tr.AddNewlyCreatedDBObject(dsr, true);
            }

            if (textHeight > 0) dsr.Dimtxt = textHeight;
            if (arrowSize > 0) dsr.Dimasz = arrowSize;
            if (overallScale > 0) dsr.Dimscale = overallScale;
            if (precision >= 0 && precision <= 8) dsr.Dimdec = precision;
            if (extOffset >= 0) dsr.Dimexo = extOffset;
            if (extExtend >= 0) dsr.Dimexe = extExtend;
            dsr.Dimtad = 1; // Text above dimension line
            dsr.Dimtih = false;
            dsr.Dimtoh = false;
            return dsr.ObjectId;
        }

        public static string Append(BlockTableRecord ms, Transaction tr, Database db, Entity ent, string layer = null, short? colorAci = null)
        {
            if (!string.IsNullOrWhiteSpace(layer))
            {
                EnsureLayer(db, tr, layer);
                ent.Layer = layer.Trim();
            }
            if (colorAci.HasValue && colorAci.Value >= 1 && colorAci.Value <= 255)
            {
                ent.Color = Color.FromColorIndex(ColorMethod.ByAci, colorAci.Value);
            }
            ms.AppendEntity(ent);
            tr.AddNewlyCreatedDBObject(ent, true);
            return ent.Handle.ToString();
        }

        public static string AddLine(BlockTableRecord ms, Transaction tr, Database db, Point3d p0, Point3d p1, string layer, short? colorAci = null)
        {
            var ln = new Line(p0, p1);
            return Append(ms, tr, db, ln, layer, colorAci);
        }

        public static string AddRectangle(BlockTableRecord ms, Transaction tr, Database db, Point2d minPt, double width, double height, string layer, double constantWidth = 0.0)
        {
            var pl = new Polyline();
            pl.AddVertexAt(0, minPt, 0, constantWidth, constantWidth);
            pl.AddVertexAt(1, new Point2d(minPt.X + width, minPt.Y), 0, constantWidth, constantWidth);
            pl.AddVertexAt(2, new Point2d(minPt.X + width, minPt.Y + height), 0, constantWidth, constantWidth);
            pl.AddVertexAt(3, new Point2d(minPt.X, minPt.Y + height), 0, constantWidth, constantWidth);
            pl.Closed = true;
            return Append(ms, tr, db, pl, layer);
        }

        public static string AddPolygonHatch(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            IList<Point2d> polygon,
            string patternName,
            double patternScale,
            double patternAngleDeg,
            string layer,
            short? colorAci = null)
        {
            if (polygon == null || polygon.Count < 3) return null;

            var hatch = new Hatch();
            string handle = Append(ms, tr, db, hatch, layer, colorAci);

            string pat = string.IsNullOrWhiteSpace(patternName) ? "SOLID" : patternName.Trim().ToUpperInvariant();
            double scale = patternScale > 0 ? patternScale : 1.0;
            double angleRad = patternAngleDeg * Math.PI / 180.0;

            try
            {
                hatch.PatternScale = scale;
                hatch.PatternAngle = angleRad;
                hatch.SetHatchPattern(HatchPatternType.PreDefined, pat);
            }
            catch
            {
                // Fallback to SOLID if a custom pattern is not installed in acadiso.pat
                hatch.PatternScale = 1.0;
                hatch.PatternAngle = 0.0;
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            }

            var pts = new Point2dCollection();
            var bulges = new DoubleCollection();
            for (int i = 0; i < polygon.Count; i++)
            {
                pts.Add(polygon[i]);
                bulges.Add(0.0);
            }
            // Close loop explicitly for Hatch.AppendLoop(Point2dCollection)
            if (!polygon[0].IsEqualTo(polygon[polygon.Count - 1]))
            {
                pts.Add(polygon[0]);
                bulges.Add(0.0);
            }

            hatch.Associative = false;
            hatch.AppendLoop(HatchLoopTypes.Default, pts, bulges);
            hatch.EvaluateHatch(true);
            return handle;
        }

        public static string AddMText(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            Point3d location,
            string contents,
            double height,
            double width,
            string layer,
            AttachmentPoint attachment = AttachmentPoint.TopLeft,
            double rotationDeg = 0.0,
            bool backgroundMask = false)
        {
            var mt = new MText
            {
                Location = location,
                Contents = contents ?? "",
                TextHeight = height > 0 ? height : 0.2,
                Width = width >= 0 ? width : 0.0,
                Attachment = attachment,
                Rotation = rotationDeg * Math.PI / 180.0
            };
            if (backgroundMask)
            {
                mt.BackgroundFill = true;
                mt.UseBackgroundColor = true;
                mt.BackgroundScaleFactor = 1.15;
            }
            return Append(ms, tr, db, mt, layer);
        }

        public static string AddLinearDim(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            Point3d p1,
            Point3d p2,
            Point3d dimLinePt,
            double rotationDeg,
            string layer,
            ObjectId dimStyleId,
            string overrideText = null)
        {
            var dim = new RotatedDimension(
                rotationDeg * Math.PI / 180.0,
                p1,
                p2,
                dimLinePt,
                overrideText ?? "",
                dimStyleId.IsNull ? db.Dimstyle : dimStyleId);
            return Append(ms, tr, db, dim, layer);
        }

        public static string AddAlignedDim(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            Point3d p1,
            Point3d p2,
            Point3d dimLinePt,
            string layer,
            ObjectId dimStyleId,
            string overrideText = null)
        {
            var dim = new AlignedDimension(
                p1,
                p2,
                dimLinePt,
                overrideText ?? "",
                dimStyleId.IsNull ? db.Dimstyle : dimStyleId);
            return Append(ms, tr, db, dim, layer);
        }

        public static List<string> AddCallout(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            Point3d arrowPoint,
            Point3d landingPoint,
            string text,
            double textHeight,
            double landingLength,
            string layer)
        {
            var handles = new List<string>();
            bool goesRight = landingPoint.X >= arrowPoint.X;
            double dir = goesRight ? 1.0 : -1.0;
            Point3d elbowEnd = new Point3d(landingPoint.X + dir * landingLength, landingPoint.Y, landingPoint.Z);

            // Leader polyline (arrow -> landing -> horizontal shelf)
            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(arrowPoint.X, arrowPoint.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(landingPoint.X, landingPoint.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(elbowEnd.X, elbowEnd.Y), 0, 0, 0);
            handles.Add(Append(ms, tr, db, pl, layer));

            // Small filled dot at arrow tip (universal architectural/detail tick)
            double dotR = Math.Max(textHeight * 0.22, 0.015);
            var dotCircle = new Circle(arrowPoint, Vector3d.ZAxis, dotR);
            handles.Add(Append(ms, tr, db, dotCircle, layer));

            Point3d textPos = new Point3d(
                elbowEnd.X + dir * (textHeight * 0.4),
                elbowEnd.Y,
                elbowEnd.Z);
            AttachmentPoint att = goesRight ? AttachmentPoint.MiddleLeft : AttachmentPoint.MiddleRight;
            handles.Add(AddMText(ms, tr, db, textPos, text, textHeight, 0.0, layer, att));
            return handles;
        }

        public static string AddTable(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            Point3d position,
            string title,
            IList<string> headers,
            IList<IList<string>> rows,
            IList<double> colWidths,
            double textHeight,
            string layer)
        {
            int numCols = headers != null && headers.Count > 0 ? headers.Count : 1;
            int dataRows = rows != null ? rows.Count : 0;
            bool hasTitle = !string.IsNullOrWhiteSpace(title);
            int totalRows = (hasTitle ? 1 : 0) + 1 + dataRows;

            var tbl = new Table
            {
                TableStyle = db.Tablestyle,
                Position = position
            };
            tbl.SetSize(totalRows, numCols);

            double rowH = Math.Max(textHeight * 2.2, 0.35);
            for (int r = 0; r < totalRows; r++)
                tbl.Rows[r].Height = r == 0 && hasTitle ? rowH * 1.25 : rowH;

            for (int c = 0; c < numCols; c++)
            {
                double w = (colWidths != null && c < colWidths.Count && colWidths[c] > 0)
                    ? colWidths[c]
                    : Math.Max(textHeight * 14.0, 2.5);
                tbl.Columns[c].Width = w;
            }

            int rowIdx = 0;
            if (hasTitle)
            {
                tbl.Cells[0, 0].TextString = title;
                tbl.Cells[0, 0].TextHeight = textHeight * 1.15;
                tbl.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
                rowIdx = 1;
            }

            if (headers != null)
            {
                for (int c = 0; c < numCols; c++)
                {
                    tbl.Cells[rowIdx, c].TextString = headers[c] ?? "";
                    tbl.Cells[rowIdx, c].TextHeight = textHeight;
                    tbl.Cells[rowIdx, c].Alignment = CellAlignment.MiddleCenter;
                }
                rowIdx++;
            }

            if (rows != null)
            {
                foreach (var rVals in rows)
                {
                    for (int c = 0; c < numCols; c++)
                    {
                        string val = (rVals != null && c < rVals.Count) ? (rVals[c] ?? "") : "";
                        tbl.Cells[rowIdx, c].TextString = val;
                        tbl.Cells[rowIdx, c].TextHeight = textHeight * 0.92;
                        tbl.Cells[rowIdx, c].Alignment = CellAlignment.MiddleLeft;
                    }
                    rowIdx++;
                }
            }

            tbl.GenerateLayout();
            return Append(ms, tr, db, tbl, layer);
        }

        public static JArray VerifyHandles(Database db, IList<string> createdHandles)
        {
            var verified = new JArray();
            using (Transaction verifyTr = db.TransactionManager.StartTransaction())
            {
                foreach (string h in createdHandles)
                {
                    if (TryResolveHandle(db, h, out ObjectId oid) && !oid.IsErased)
                        verified.Add(h);
                }
                verifyTr.Commit();
            }
            return verified;
        }
    }
}
