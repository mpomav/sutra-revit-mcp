using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// Parametric construction-detail drafting tool (wall_section, layer_buildup,
    /// spec_legend) designed to consume structured criteria &amp; citations from design
    /// standards (e.g., Gemini Notebook / File Search) and generate hatched, dimensioned,
    /// annotated AutoCAD details in a single verified transaction.
    /// </summary>
    internal static class CadDetailCommand
    {
        public static JObject Execute(JObject args)
        {
            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            bool dryRun = (bool?)args["dry_run"] ?? true;
            Document doc = CadDraftingKit.RequireActiveDocument(args, dryRun, out string activeTitle);
            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_detail", doc.Name, args);

            if (dryRun)
            {
                string token = CadConfirmationStore.Issue(fingerprint);
                JObject preview = Preview(op, args);
                preview["dry_run"] = true;
                preview["verification_state"] = "rehearsed";
                preview["active_document"] = activeTitle;
                preview["confirmation_token"] = token;
                if (args["criteria_ref"] != null) preview["criteria_ref"] = args["criteria_ref"].DeepClone();
                return preview;
            }

            string tokenError = CadConfirmationStore.Consume((string)args["confirmation_token"], fingerprint);
            if (tokenError != null)
                throw new InvalidOperationException(tokenError);

            JObject committed = CommitAndVerify(doc, op, args);
            committed["dry_run"] = false;
            committed["active_document"] = activeTitle;
            if (args["criteria_ref"] != null) committed["criteria_ref"] = args["criteria_ref"].DeepClone();
            return committed;
        }

        private static JObject Preview(string op, JObject args)
        {
            switch (op)
            {
                case "wall_section":
                case "layer_buildup":
                {
                    var layers = args["layers_stack"] as JArray ?? new JArray();
                    if (layers.Count == 0)
                        throw new ArgumentException(op + " requires a non-empty 'layers_stack' array.");
                    double totalThickness = 0.0;
                    foreach (JObject l in layers) totalThickness += (double?)l["thickness"] ?? 0.0;
                    return new JObject
                    {
                        ["operation"] = op,
                        ["layer_count"] = layers.Count,
                        ["total_thickness"] = Math.Round(totalThickness, 4),
                        ["citations_count"] = (args["citations"] as JArray)?.Count ?? 0
                    };
                }
                case "spec_legend":
                {
                    var items = args["items"] as JArray ?? new JArray();
                    if (items.Count == 0)
                        throw new ArgumentException("spec_legend requires a non-empty 'items' array.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["legend_items"] = items.Count
                    };
                }
                default:
                    throw new ArgumentException("Unsupported cad_detail operation '" + op + "'.");
            }
        }

        private static JObject CommitAndVerify(Document doc, string op, JObject args)
        {
            Database db = doc.Database;
            var createdHandles = new List<string>();
            JObject summary;

            using (doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    switch (op)
                    {
                        case "wall_section":
                            summary = DrawWallSection(db, tr, ms, args, createdHandles);
                            break;
                        case "layer_buildup":
                            summary = DrawLayerBuildup(db, tr, ms, args, createdHandles);
                            break;
                        case "spec_legend":
                            summary = DrawSpecLegend(db, tr, ms, args, createdHandles);
                            break;
                        default:
                            throw new ArgumentException("Unsupported cad_detail operation '" + op + "'.");
                    }

                    tr.Commit();
                }

                JArray verified = CadDraftingKit.VerifyHandles(db, createdHandles);
                summary["operation"] = op;
                summary["created_count"] = createdHandles.Count;
                summary["verified_count"] = verified.Count;
                summary["handles"] = verified;
                summary["verification_state"] = verified.Count == createdHandles.Count ? "verified" : "partial";
                return summary;
            }
        }

        /// <summary>
        /// Draws a vertical construction detail (e.g., multi-layer wall section) with each
        /// material stratum arranged left-to-right, hatched, dimensioned at bottom, and
        /// called out cleanly to the right with optional standard/book citations.
        /// </summary>
        private static JObject DrawWallSection(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double sectionHeight = (double?)args["section_height"] ?? 1.20;
            double textH = (double?)args["text_height"] ?? Math.Max(sectionHeight * 0.035, 0.035);
            string title = ((string)args["title"] ?? "DETALLE CONSTRUCTIVO DE MURO").Trim();
            string scaleLabel = ((string)args["scale_label"] ?? "ESC. 1:10").Trim();
            var stack = args["layers_stack"] as JArray ?? new JArray();

            string cutLayer = ((string)args["cut_layer"] ?? "A-DETL-CUT").Trim();
            string hatchLayer = ((string)args["hatch_layer"] ?? "A-DETL-PATT").Trim();
            string dimLayer = ((string)args["dim_layer"] ?? "A-ANNO-DIMS").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "A-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, cutLayer, 7, "CONTINUOUS", 0.35, "Detail Cut Lines");
            CadDraftingKit.EnsureLayer(db, tr, hatchLayer, 8, "CONTINUOUS", 0.09, "Detail Hatch");
            CadDraftingKit.EnsureLayer(db, tr, dimLayer, 2, "CONTINUOUS", 0.15, "Detail Dimensions");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "Detail Callouts & Notes");
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-DETL", textH * 0.85, textH * 0.6, 1.0, 3);

            double x = origin.X;
            var boundariesX = new List<double> { x };
            var layerCentersX = new List<double>();
            var layerLabels = new List<string>();

            foreach (JObject l in stack)
            {
                double thick = (double?)l["thickness"] ?? 0.02;
                if (thick <= 0) continue;
                string name = ((string)l["name"] ?? "CAPA").Trim();
                string spec = ((string)l["spec"] ?? "").Trim();
                string pat = ((string)l["hatch_pattern"] ?? "ANSI31").Trim();
                double patScale = (double?)l["hatch_scale"] ?? Math.Max(thick * 0.4, 0.01);
                double patAngle = (double?)l["hatch_angle_deg"] ?? 0.0;
                short? colorAci = (short?)(int?)l["color_aci"];

                // Stratum outline
                handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(x, origin.Y), thick, sectionHeight, cutLayer));

                // Stratum hatch
                var poly = new List<Point2d>
                {
                    new Point2d(x, origin.Y),
                    new Point2d(x + thick, origin.Y),
                    new Point2d(x + thick, origin.Y + sectionHeight),
                    new Point2d(x, origin.Y + sectionHeight)
                };
                string hh = CadDraftingKit.AddPolygonHatch(ms, tr, db, poly, pat, patScale, patAngle, hatchLayer, colorAci);
                if (!string.IsNullOrEmpty(hh)) handles.Add(hh);

                layerCentersX.Add(x + thick * 0.5);
                string calloutText = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} (e={1:0.###}){2}",
                    name,
                    thick,
                    string.IsNullOrEmpty(spec) ? "" : " - " + spec);
                layerLabels.Add(calloutText);

                x += thick;
                boundariesX.Add(x);
            }

            double totalThickness = x - origin.X;

            // Break lines at top and bottom of section
            double breakOverhang = Math.Max(totalThickness * 0.2, 0.08);
            handles.Add(CadDraftingKit.AddLine(ms, tr, db,
                new Point3d(origin.X - breakOverhang, origin.Y, origin.Z),
                new Point3d(x + breakOverhang, origin.Y, origin.Z), cutLayer));
            handles.Add(CadDraftingKit.AddLine(ms, tr, db,
                new Point3d(origin.X - breakOverhang, origin.Y + sectionHeight, origin.Z),
                new Point3d(x + breakOverhang, origin.Y + sectionHeight, origin.Z), cutLayer));

            // Ordered non-overlapping callouts to the right
            double calloutX = x + Math.Max(totalThickness * 0.8, 0.25);
            double shelfLen = Math.Max(textH * 3.0, 0.12);
            int n = layerCentersX.Count;
            for (int i = 0; i < n; i++)
            {
                double frac = n == 1 ? 0.5 : (0.15 + 0.70 * ((double)(n - 1 - i) / (n - 1)));
                double arrowY = origin.Y + sectionHeight * frac;
                Point3d arrowPt = new Point3d(layerCentersX[i], arrowY, origin.Z);
                Point3d landingPt = new Point3d(calloutX, arrowY, origin.Z);
                handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, arrowPt, landingPt, layerLabels[i], textH, shelfLen, noteLayer));
            }

            // Bottom partial & total dimensions
            double dimY1 = origin.Y - Math.Max(textH * 3.0, 0.15);
            for (int i = 0; i < boundariesX.Count - 1; i++)
            {
                handles.Add(CadDraftingKit.AddLinearDim(
                    ms, tr, db,
                    new Point3d(boundariesX[i], origin.Y, origin.Z),
                    new Point3d(boundariesX[i + 1], origin.Y, origin.Z),
                    new Point3d((boundariesX[i] + boundariesX[i + 1]) * 0.5, dimY1, origin.Z),
                    0.0, dimLayer, dimStyleId));
            }
            if (boundariesX.Count > 2)
            {
                handles.Add(CadDraftingKit.AddLinearDim(
                    ms, tr, db,
                    new Point3d(origin.X, origin.Y, origin.Z),
                    new Point3d(x, origin.Y, origin.Z),
                    new Point3d((origin.X + x) * 0.5, dimY1 - textH * 2.5, origin.Z),
                    0.0, dimLayer, dimStyleId));
            }

            // Title + scale + optional normative citations block
            Point3d titlePt = new Point3d(origin.X, dimY1 - textH * 5.5, origin.Z);
            handles.Add(CadDraftingKit.AddMText(
                ms, tr, db, titlePt,
                title + "\\P" + scaleLabel,
                textH * 1.25, 0.0, noteLayer, AttachmentPoint.TopLeft));

            int citCount = AppendCitationsNote(ms, tr, db, args["citations"] as JArray,
                new Point3d(calloutX, origin.Y - textH * 2.0, origin.Z), textH * 0.85, noteLayer, handles);

            return new JObject
            {
                ["layers_drawn"] = n,
                ["total_thickness"] = Math.Round(totalThickness, 4),
                ["citations_stamped"] = citCount
            };
        }

        /// <summary>
        /// Draws a horizontal construction assembly (floor slab, roof buildup, pavement
        /// structure) with strata stacked bottom-to-top, hatched, dimensioned on the left,
        /// called out cleanly to the right, and stamped with normative citations.
        /// </summary>
        private static JObject DrawLayerBuildup(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double sectionWidth = (double?)args["section_width"] ?? 1.20;
            double textH = (double?)args["text_height"] ?? Math.Max(sectionWidth * 0.035, 0.035);
            string title = ((string)args["title"] ?? "DETALLE DE PAQUETE CONSTRUCTIVO").Trim();
            string scaleLabel = ((string)args["scale_label"] ?? "ESC. 1:10").Trim();
            var stack = args["layers_stack"] as JArray ?? new JArray();

            string cutLayer = ((string)args["cut_layer"] ?? "A-DETL-CUT").Trim();
            string hatchLayer = ((string)args["hatch_layer"] ?? "A-DETL-PATT").Trim();
            string dimLayer = ((string)args["dim_layer"] ?? "A-ANNO-DIMS").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "A-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, cutLayer, 7, "CONTINUOUS", 0.35, "Detail Cut Lines");
            CadDraftingKit.EnsureLayer(db, tr, hatchLayer, 8, "CONTINUOUS", 0.09, "Detail Hatch");
            CadDraftingKit.EnsureLayer(db, tr, dimLayer, 2, "CONTINUOUS", 0.15, "Detail Dimensions");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "Detail Callouts & Notes");
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-DETL", textH * 0.85, textH * 0.6, 1.0, 3);

            double y = origin.Y;
            var boundariesY = new List<double> { y };
            var layerMidY = new List<double>();
            var layerLabels = new List<string>();

            foreach (JObject l in stack)
            {
                double thick = (double?)l["thickness"] ?? 0.05;
                if (thick <= 0) continue;
                string name = ((string)l["name"] ?? "ESTRATO").Trim();
                string spec = ((string)l["spec"] ?? "").Trim();
                string pat = ((string)l["hatch_pattern"] ?? "AR-CONC").Trim();
                double patScale = (double?)l["hatch_scale"] ?? Math.Max(thick * 0.4, 0.01);
                double patAngle = (double?)l["hatch_angle_deg"] ?? 0.0;
                short? colorAci = (short?)(int?)l["color_aci"];

                handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, y), sectionWidth, thick, cutLayer));

                var poly = new List<Point2d>
                {
                    new Point2d(origin.X, y),
                    new Point2d(origin.X + sectionWidth, y),
                    new Point2d(origin.X + sectionWidth, y + thick),
                    new Point2d(origin.X, y + thick)
                };
                string hh = CadDraftingKit.AddPolygonHatch(ms, tr, db, poly, pat, patScale, patAngle, hatchLayer, colorAci);
                if (!string.IsNullOrEmpty(hh)) handles.Add(hh);

                layerMidY.Add(y + thick * 0.5);
                layerLabels.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} (e={1:0.###}){2}",
                    name,
                    thick,
                    string.IsNullOrEmpty(spec) ? "" : " - " + spec));

                y += thick;
                boundariesY.Add(y);
            }

            double totalThickness = y - origin.Y;

            // Callouts to the right, spaced cleanly to prevent text collisions
            double calloutX = origin.X + sectionWidth + Math.Max(sectionWidth * 0.25, 0.25);
            double shelfLen = Math.Max(textH * 3.0, 0.12);
            double minStep = textH * 2.2;
            double prevLandingY = double.NegativeInfinity;

            for (int i = 0; i < layerMidY.Count; i++)
            {
                double targetY = layerMidY[i];
                if (targetY < prevLandingY + minStep) targetY = prevLandingY + minStep;
                prevLandingY = targetY;

                Point3d arrowPt = new Point3d(origin.X + sectionWidth * 0.85, layerMidY[i], origin.Z);
                Point3d landingPt = new Point3d(calloutX, targetY, origin.Z);
                handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, arrowPt, landingPt, layerLabels[i], textH, shelfLen, noteLayer));
            }

            // Left partial & total vertical dimensions
            double dimX1 = origin.X - Math.Max(textH * 3.5, 0.18);
            for (int i = 0; i < boundariesY.Count - 1; i++)
            {
                handles.Add(CadDraftingKit.AddLinearDim(
                    ms, tr, db,
                    new Point3d(origin.X, boundariesY[i], origin.Z),
                    new Point3d(origin.X, boundariesY[i + 1], origin.Z),
                    new Point3d(dimX1, (boundariesY[i] + boundariesY[i + 1]) * 0.5, origin.Z),
                    90.0, dimLayer, dimStyleId));
            }
            if (boundariesY.Count > 2)
            {
                handles.Add(CadDraftingKit.AddLinearDim(
                    ms, tr, db,
                    new Point3d(origin.X, origin.Y, origin.Z),
                    new Point3d(origin.X, y, origin.Z),
                    new Point3d(dimX1 - textH * 3.0, (origin.Y + y) * 0.5, origin.Z),
                    90.0, dimLayer, dimStyleId));
            }

            Point3d titlePt = new Point3d(origin.X, origin.Y - textH * 3.0, origin.Z);
            handles.Add(CadDraftingKit.AddMText(
                ms, tr, db, titlePt,
                title + "\\P" + scaleLabel,
                textH * 1.25, 0.0, noteLayer, AttachmentPoint.TopLeft));

            int citCount = AppendCitationsNote(ms, tr, db, args["citations"] as JArray,
                new Point3d(calloutX, origin.Y - textH * 3.0, origin.Z), textH * 0.85, noteLayer, handles);

            return new JObject
            {
                ["layers_drawn"] = layerMidY.Count,
                ["total_thickness"] = Math.Round(totalThickness, 4),
                ["citations_stamped"] = citCount
            };
        }

        /// <summary>
        /// Draws a visual material/specification legend with hatch swatch boxes,
        /// descriptions, and normative/book citations.
        /// </summary>
        private static JObject DrawSpecLegend(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double textH = (double?)args["text_height"] ?? 0.14;
            string title = ((string)args["title"] ?? "LEYENDA DE MATERIALES Y ESPECIFICACIONES").Trim();
            var items = args["items"] as JArray ?? new JArray();

            string cutLayer = ((string)args["cut_layer"] ?? "A-DETL-CUT").Trim();
            string hatchLayer = ((string)args["hatch_layer"] ?? "A-DETL-PATT").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "A-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, cutLayer, 7, "CONTINUOUS", 0.25, "Legend Borders");
            CadDraftingKit.EnsureLayer(db, tr, hatchLayer, 8, "CONTINUOUS", 0.09, "Legend Hatch Swatches");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "Legend Text");

            handles.Add(CadDraftingKit.AddMText(ms, tr, db, origin, title, textH * 1.25, 0.0, noteLayer, AttachmentPoint.TopLeft));

            double swatchW = textH * 4.5;
            double swatchH = textH * 2.4;
            double rowStep = swatchH + textH * 1.1;
            double curY = origin.Y - textH * 2.4;

            int count = 0;
            foreach (JObject item in items)
            {
                string name = ((string)item["name"] ?? "MATERIAL").Trim();
                string desc = ((string)item["description"] ?? "").Trim();
                string citation = ((string)item["citation"] ?? "").Trim();
                string pat = ((string)item["hatch_pattern"] ?? "ANSI31").Trim();
                double patScale = (double?)item["hatch_scale"] ?? Math.Max(swatchH * 0.3, 0.05);
                short? colorAci = (short?)(int?)item["color_aci"];

                double boxY = curY - swatchH;
                handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, boxY), swatchW, swatchH, cutLayer));

                var poly = new List<Point2d>
                {
                    new Point2d(origin.X, boxY),
                    new Point2d(origin.X + swatchW, boxY),
                    new Point2d(origin.X + swatchW, boxY + swatchH),
                    new Point2d(origin.X, boxY + swatchH)
                };
                string hh = CadDraftingKit.AddPolygonHatch(ms, tr, db, poly, pat, patScale, 0.0, hatchLayer, colorAci);
                if (!string.IsNullOrEmpty(hh)) handles.Add(hh);

                string label = name;
                if (!string.IsNullOrEmpty(desc)) label += " — " + desc;
                if (!string.IsNullOrEmpty(citation)) label += " [" + citation + "]";

                Point3d txtPt = new Point3d(origin.X + swatchW + textH * 1.2, boxY + swatchH * 0.5, origin.Z);
                handles.Add(CadDraftingKit.AddMText(ms, tr, db, txtPt, label, textH, 0.0, noteLayer, AttachmentPoint.MiddleLeft));

                curY -= rowStep;
                count++;
            }

            return new JObject
            {
                ["legend_items_drawn"] = count
            };
        }

        private static int AppendCitationsNote(
            BlockTableRecord ms,
            Transaction tr,
            Database db,
            JArray citations,
            Point3d position,
            double textH,
            string layer,
            List<string> handles)
        {
            if (citations == null || citations.Count == 0) return 0;
            var lines = new List<string> { "REFERENCIAS NORMATIVAS:" };
            int count = 0;
            foreach (JToken tok in citations)
            {
                string line;
                if (tok is JObject obj)
                {
                    string src = ((string)obj["source"] ?? (string)obj["book"] ?? "").Trim();
                    string sec = ((string)obj["section"] ?? "").Trim();
                    string page = ((string)obj["page"] ?? "").Trim();
                    string note = ((string)obj["note"] ?? "").Trim();
                    line = "- " + src;
                    if (!string.IsNullOrEmpty(sec)) line += ", " + sec;
                    if (!string.IsNullOrEmpty(page)) line += " (p. " + page + ")";
                    if (!string.IsNullOrEmpty(note)) line += ": " + note;
                }
                else
                {
                    line = "- " + (tok?.ToString() ?? "").Trim();
                }
                if (line.Length > 2) { lines.Add(line); count++; }
            }

            if (count > 0)
            {
                handles.Add(CadDraftingKit.AddMText(
                    ms, tr, db, position,
                    string.Join("\\P", lines),
                    textH, 0.0, layer, AttachmentPoint.TopLeft));
            }
            return count;
        }
    }
}
