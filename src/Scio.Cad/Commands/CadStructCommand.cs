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
    /// Phase 3 — Parametric structural engineering drafting tool (draw_column_section,
    /// draw_beam_section, draw_footing_detail, draw_rebar_schedule) with dry_run=true
    /// default, confirmation_token, atomic Transaction commit, and post-commit Handle re-read.
    /// Organisation-neutral: covers, bar counts, diameters, concrete strengths, layers,
    /// and normative citations arrive as call-time arguments.
    /// </summary>
    internal static class CadStructCommand
    {
        public static JObject Execute(JObject args)
        {
            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            bool dryRun = (bool?)args["dry_run"] ?? true;
            Document doc = CadDraftingKit.RequireActiveDocument(args, dryRun, out string activeTitle);
            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_struct", doc.Name, args);

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
                case "draw_column_section":
                    return new JObject
                    {
                        ["operation"] = op,
                        ["tag"] = ((string)args["tag"] ?? "C-1").Trim(),
                        ["width"] = (double?)args["width"] ?? 0.40,
                        ["depth"] = (double?)args["depth"] ?? 0.40,
                        ["cover"] = (double?)args["cover"] ?? 0.04,
                        ["bars_x"] = (int?)args["bars_x"] ?? 3,
                        ["bars_y"] = (int?)args["bars_y"] ?? 3
                    };
                case "draw_beam_section":
                    return new JObject
                    {
                        ["operation"] = op,
                        ["tag"] = ((string)args["tag"] ?? "V-101").Trim(),
                        ["width"] = (double?)args["width"] ?? 0.30,
                        ["height"] = (double?)args["height"] ?? 0.50,
                        ["cover"] = (double?)args["cover"] ?? 0.04,
                        ["top_bars_count"] = (int?)args["top_bars_count"] ?? 3,
                        ["bottom_bars_count"] = (int?)args["bottom_bars_count"] ?? 3
                    };
                case "draw_footing_detail":
                    return new JObject
                    {
                        ["operation"] = op,
                        ["tag"] = ((string)args["tag"] ?? "Z-1").Trim(),
                        ["width_x"] = (double?)args["width_x"] ?? 1.80,
                        ["width_y"] = (double?)args["width_y"] ?? 1.80,
                        ["footing_height"] = (double?)args["footing_height"] ?? 0.50
                    };
                case "draw_rebar_schedule":
                {
                    var rows = args["rows"] as JArray ?? new JArray();
                    if (rows.Count == 0) throw new ArgumentException("draw_rebar_schedule requires a non-empty 'rows' array.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["row_count"] = rows.Count
                    };
                }
                default:
                    throw new ArgumentException("Unsupported cad_struct operation '" + op + "'.");
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
                        case "draw_column_section":
                            summary = DrawColumnSection(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_beam_section":
                            summary = DrawBeamSection(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_footing_detail":
                            summary = DrawFootingDetail(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_rebar_schedule":
                            summary = DrawRebarSchedule(db, tr, ms, args, createdHandles);
                            break;
                        default:
                            throw new ArgumentException("Unsupported cad_struct operation '" + op + "'.");
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

        private static JObject DrawColumnSection(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double w = (double?)args["width"] ?? 0.40;
            double d = (double?)args["depth"] ?? 0.40;
            double cover = (double?)args["cover"] ?? 0.04;
            int barsX = Math.Max(2, (int?)args["bars_x"] ?? 3);
            int barsY = Math.Max(2, (int?)args["bars_y"] ?? 3);
            double barDiam = ((double?)args["bar_diameter_mm"] ?? 16.0) / 1000.0;
            string tag = ((string)args["tag"] ?? "C-1").Trim();
            string barLabel = ((string)args["bar_label"] ?? ((2 * barsX + 2 * (barsY - 2)) + " Ø5/8\"")).Trim();
            string stirrupLabel = ((string)args["stirrup_label"] ?? "Est. Ø3/8\": 1@0.05, 4@0.10, Rto@0.20").Trim();
            string concreteFc = ((string)args["concrete_fc"] ?? "f'c = 210 kg/cm2").Trim();
            string scaleLabel = ((string)args["scale_label"] ?? "ESC. 1:10").Trim();
            double textH = (double?)args["text_height"] ?? Math.Max(Math.Min(w, d) * 0.08, 0.025);

            EnsureStructLayers(db, tr, args, out string concLayer, out string rebarLayer, out string stirrupLayer, out string dimLayer, out string noteLayer);
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-STR", textH * 0.85, textH * 0.6, 1.0, 2);

            // Concrete outline + subtle hatch
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, origin.Y), w, d, concLayer));
            var concPoly = new List<Point2d>
            {
                new Point2d(origin.X, origin.Y),
                new Point2d(origin.X + w, origin.Y),
                new Point2d(origin.X + w, origin.Y + d),
                new Point2d(origin.X, origin.Y + d)
            };
            string ch = CadDraftingKit.AddPolygonHatch(ms, tr, db, concPoly, "AR-CONC", Math.Max(Math.Min(w, d) * 0.06, 0.01), 0.0, concLayer, 8);
            if (!string.IsNullOrEmpty(ch)) handles.Add(ch);

            // Stirrup rectangle inset by cover + 135-degree seismic hook at top-right corner
            double sx0 = origin.X + cover, sy0 = origin.Y + cover;
            double sw = Math.Max(w - 2 * cover, 0.05), sd = Math.Max(d - 2 * cover, 0.05);
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(sx0, sy0), sw, sd, stirrupLayer, 0.004));
            double hookLen = Math.Max(Math.Min(sw, sd) * 0.16, 0.035);
            Point3d trCorner = new Point3d(sx0 + sw, sy0 + sd, origin.Z);
            handles.Add(CadDraftingKit.AddLine(ms, tr, db, trCorner, trCorner + new Vector3d(-hookLen * 0.7, -hookLen * 0.7, 0), stirrupLayer));

            // Longitudinal bars around perimeter
            double barR = Math.Max(barDiam * 0.5, 0.006);
            double bx0 = sx0 + barR * 1.4, bx1 = sx0 + sw - barR * 1.4;
            double by0 = sy0 + barR * 1.4, by1 = sy0 + sd - barR * 1.4;
            int totalBars = 0;

            for (int i = 0; i < barsX; i++)
            {
                double fx = barsX == 1 ? 0.5 : (double)i / (barsX - 1);
                double x = bx0 + fx * (bx1 - bx0);
                AddFilledRebar(ms, tr, db, new Point3d(x, by0, origin.Z), barR, rebarLayer, handles);
                AddFilledRebar(ms, tr, db, new Point3d(x, by1, origin.Z), barR, rebarLayer, handles);
                totalBars += 2;
            }
            for (int j = 1; j < barsY - 1; j++)
            {
                double fy = (double)j / (barsY - 1);
                double y = by0 + fy * (by1 - by0);
                AddFilledRebar(ms, tr, db, new Point3d(bx0, y, origin.Z), barR, rebarLayer, handles);
                AddFilledRebar(ms, tr, db, new Point3d(bx1, y, origin.Z), barR, rebarLayer, handles);
                totalBars += 2;
            }

            // Dimensions (bottom width, left depth)
            double dimOff = Math.Max(textH * 3.0, 0.10);
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                origin, new Point3d(origin.X + w, origin.Y, origin.Z),
                new Point3d(origin.X + w * 0.5, origin.Y - dimOff, origin.Z), 0.0, dimLayer, dimStyleId));
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                origin, new Point3d(origin.X, origin.Y + d, origin.Z),
                new Point3d(origin.X - dimOff, origin.Y + d * 0.5, origin.Z), 90.0, dimLayer, dimStyleId));

            // Callouts to the right
            double callX = origin.X + w + Math.Max(w * 0.45, 0.16);
            double shelf = Math.Max(textH * 2.5, 0.08);
            handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db,
                new Point3d(bx1, by1, origin.Z), new Point3d(callX, origin.Y + d * 0.85, origin.Z),
                barLabel, textH, shelf, noteLayer));
            handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db,
                new Point3d(sx0 + sw, origin.Y + d * 0.5, origin.Z), new Point3d(callX, origin.Y + d * 0.50, origin.Z),
                stirrupLabel, textH, shelf, noteLayer));
            handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db,
                new Point3d(origin.X + w, origin.Y + d * 0.2, origin.Z), new Point3d(callX, origin.Y + d * 0.15, origin.Z),
                string.Format(CultureInfo.InvariantCulture, "r={0:0.##}m · {1}", cover, concreteFc), textH, shelf, noteLayer));

            // Title + citations
            handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                new Point3d(origin.X, origin.Y - dimOff - textH * 2.5, origin.Z),
                "COLUMNA " + tag + " (" + string.Format(CultureInfo.InvariantCulture, "{0:0.00}x{1:0.00}", w, d) + ")\\P" + scaleLabel,
                textH * 1.2, 0, noteLayer, AttachmentPoint.TopLeft));

            int citCount = StampCitations(ms, tr, db, args["citations"] as JArray,
                new Point3d(callX, origin.Y - dimOff, origin.Z), textH * 0.85, noteLayer, handles);

            return new JObject
            {
                ["tag"] = tag,
                ["total_longitudinal_bars"] = totalBars,
                ["citations_stamped"] = citCount
            };
        }

        private static JObject DrawBeamSection(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double w = (double?)args["width"] ?? 0.30;
            double h = (double?)args["height"] ?? 0.50;
            double cover = (double?)args["cover"] ?? 0.04;
            int topBars = Math.Max(2, (int?)args["top_bars_count"] ?? 3);
            int botBars = Math.Max(2, (int?)args["bottom_bars_count"] ?? 3);
            double barDiam = ((double?)args["bar_diameter_mm"] ?? 16.0) / 1000.0;
            double slabThick = (double?)args["slab_thickness"] ?? 0.0;
            string tag = ((string)args["tag"] ?? "V-101").Trim();
            string topLabel = ((string)args["top_bar_label"] ?? (topBars + " Ø5/8\" (Sup.)")).Trim();
            string botLabel = ((string)args["bottom_bar_label"] ?? (botBars + " Ø5/8\" (Inf.)")).Trim();
            string stirrupLabel = ((string)args["stirrup_label"] ?? "Est. Ø3/8\": 1@0.05, 5@0.10, Rto@0.20").Trim();
            string scaleLabel = ((string)args["scale_label"] ?? "ESC. 1:10").Trim();
            double textH = (double?)args["text_height"] ?? Math.Max(Math.Min(w, h) * 0.08, 0.025);

            EnsureStructLayers(db, tr, args, out string concLayer, out string rebarLayer, out string stirrupLayer, out string dimLayer, out string noteLayer);
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-STR", textH * 0.85, textH * 0.6, 1.0, 2);

            // Beam rectangle
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, origin.Y), w, h, concLayer));
            if (slabThick > 0 && slabThick < h)
            {
                double wing = w * 0.45;
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(origin.X - wing, origin.Y + h, origin.Z), new Point3d(origin.X, origin.Y + h, origin.Z), concLayer));
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(origin.X - wing, origin.Y + h - slabThick, origin.Z), new Point3d(origin.X, origin.Y + h - slabThick, origin.Z), concLayer));
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(origin.X + w, origin.Y + h, origin.Z), new Point3d(origin.X + w + wing, origin.Y + h, origin.Z), concLayer));
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(origin.X + w, origin.Y + h - slabThick, origin.Z), new Point3d(origin.X + w + wing, origin.Y + h - slabThick, origin.Z), concLayer));
            }

            // Stirrup
            double sx0 = origin.X + cover, sy0 = origin.Y + cover;
            double sw = Math.Max(w - 2 * cover, 0.05), sh = Math.Max(h - 2 * cover, 0.05);
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(sx0, sy0), sw, sh, stirrupLayer, 0.004));

            // Top & bottom bars
            double barR = Math.Max(barDiam * 0.5, 0.006);
            double bx0 = sx0 + barR * 1.4, bx1 = sx0 + sw - barR * 1.4;
            double yTop = sy0 + sh - barR * 1.4, yBot = sy0 + barR * 1.4;

            for (int i = 0; i < topBars; i++)
            {
                double x = bx0 + ((double)i / (topBars - 1)) * (bx1 - bx0);
                AddFilledRebar(ms, tr, db, new Point3d(x, yTop, origin.Z), barR, rebarLayer, handles);
            }
            for (int i = 0; i < botBars; i++)
            {
                double x = bx0 + ((double)i / (botBars - 1)) * (bx1 - bx0);
                AddFilledRebar(ms, tr, db, new Point3d(x, yBot, origin.Z), barR, rebarLayer, handles);
            }

            // Dimensions
            double dimOff = Math.Max(textH * 3.0, 0.10);
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                origin, new Point3d(origin.X + w, origin.Y, origin.Z),
                new Point3d(origin.X + w * 0.5, origin.Y - dimOff, origin.Z), 0.0, dimLayer, dimStyleId));
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                origin, new Point3d(origin.X, origin.Y + h, origin.Z),
                new Point3d(origin.X - dimOff, origin.Y + h * 0.5, origin.Z), 90.0, dimLayer, dimStyleId));

            // Callouts
            double callX = origin.X + w + Math.Max(w * 0.65, 0.20);
            double shelf = Math.Max(textH * 2.5, 0.08);
            handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, new Point3d(bx1, yTop, origin.Z), new Point3d(callX, origin.Y + h * 0.85, origin.Z), topLabel, textH, shelf, noteLayer));
            handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, new Point3d(sx0 + sw, origin.Y + h * 0.5, origin.Z), new Point3d(callX, origin.Y + h * 0.50, origin.Z), stirrupLabel, textH, shelf, noteLayer));
            handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, new Point3d(bx1, yBot, origin.Z), new Point3d(callX, origin.Y + h * 0.15, origin.Z), botLabel, textH, shelf, noteLayer));

            handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                new Point3d(origin.X, origin.Y - dimOff - textH * 2.5, origin.Z),
                "VIGA " + tag + " (" + string.Format(CultureInfo.InvariantCulture, "{0:0.00}x{1:0.00}", w, h) + ")\\P" + scaleLabel,
                textH * 1.2, 0, noteLayer, AttachmentPoint.TopLeft));

            int citCount = StampCitations(ms, tr, db, args["citations"] as JArray,
                new Point3d(callX, origin.Y - dimOff, origin.Z), textH * 0.85, noteLayer, handles);

            return new JObject
            {
                ["tag"] = tag,
                ["top_bars"] = topBars,
                ["bottom_bars"] = botBars,
                ["citations_stamped"] = citCount
            };
        }

        private static JObject DrawFootingDetail(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double bx = (double?)args["width_x"] ?? 1.80;
            double by = (double?)args["width_y"] ?? 1.80;
            double hz = (double?)args["footing_height"] ?? 0.50;
            double colW = (double?)args["col_width"] ?? 0.40;
            double colD = (double?)args["col_depth"] ?? 0.40;
            double df = (double?)args["df_depth"] ?? 1.50;
            double subbase = (double?)args["subbase_thickness"] ?? 0.10;
            double cover = (double?)args["cover"] ?? 0.075;
            string tag = ((string)args["tag"] ?? "Z-1").Trim();
            string rebarX = ((string)args["rebar_x_label"] ?? "Ø5/8\" @ 0.15m (Dir. X)").Trim();
            string rebarY = ((string)args["rebar_y_label"] ?? "Ø5/8\" @ 0.15m (Dir. Y)").Trim();
            string scaleLabel = ((string)args["scale_label"] ?? "ESC. 1:25").Trim();
            double textH = (double?)args["text_height"] ?? Math.Max(bx * 0.045, 0.06);

            EnsureStructLayers(db, tr, args, out string concLayer, out string rebarLayer, out string stirrupLayer, out string dimLayer, out string noteLayer);
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-STR", textH * 0.85, textH * 0.6, 1.0, 2);

            // 1. PLAN VIEW at origin
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, origin.Y), bx, by, concLayer));
            double cx0 = origin.X + (bx - colW) * 0.5, cy0 = origin.Y + (by - colD) * 0.5;
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(cx0, cy0), colW, colD, concLayer));
            // Rebar mesh indicative lines in plan
            handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(origin.X + cover, origin.Y + by * 0.25, origin.Z), new Point3d(origin.X + bx - cover, origin.Y + by * 0.25, origin.Z), rebarLayer));
            handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(origin.X + bx * 0.25, origin.Y + cover, origin.Z), new Point3d(origin.X + bx * 0.25, origin.Y + by - cover, origin.Z), rebarLayer));

            double dimOff = Math.Max(textH * 3.0, 0.22);
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db, origin, new Point3d(origin.X + bx, origin.Y, origin.Z),
                new Point3d(origin.X + bx * 0.5, origin.Y - dimOff, origin.Z), 0.0, dimLayer, dimStyleId));
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db, origin, new Point3d(origin.X, origin.Y + by, origin.Z),
                new Point3d(origin.X - dimOff, origin.Y + by * 0.5, origin.Z), 90.0, dimLayer, dimStyleId));

            // 2. ELEVATION SECTION to the right of plan
            double elevX = origin.X + bx + Math.Max(bx * 0.65, 0.90);
            double elevY = origin.Y;
            // Subbase (solado)
            if (subbase > 0)
            {
                handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(elevX - 0.05, elevY), bx + 0.10, subbase, concLayer));
            }
            double footY = elevY + subbase;
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(elevX, footY), bx, hz, concLayer));

            // Column pedestal up to grade (df above footing base)
            double pedH = Math.Max(df - hz, 0.40);
            double pedX = elevX + (bx - colW) * 0.5;
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(pedX, footY + hz), colW, pedH, concLayer));

            // Grade line (N.T.N.)
            double gradeY = footY + hz + pedH * 0.85;
            handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(elevX - 0.35, gradeY, origin.Z), new Point3d(elevX + bx + 0.35, gradeY, origin.Z), noteLayer));
            handles.Add(CadDraftingKit.AddMText(ms, tr, db, new Point3d(elevX + bx + 0.40, gradeY, origin.Z), "N.T.N.", textH, 0, noteLayer, AttachmentPoint.MiddleLeft));

            // Bottom rebar mat with 90-deg hooks
            double matY = footY + cover;
            double hookH = Math.Min(hz - 2 * cover, 0.20);
            var barPl = new Polyline();
            barPl.AddVertexAt(0, new Point2d(elevX + cover, matY + hookH), 0, 0.008, 0.008);
            barPl.AddVertexAt(1, new Point2d(elevX + cover, matY), 0, 0.008, 0.008);
            barPl.AddVertexAt(2, new Point2d(elevX + bx - cover, matY), 0, 0.008, 0.008);
            barPl.AddVertexAt(3, new Point2d(elevX + bx - cover, matY + hookH), 0, 0.008, 0.008);
            handles.Add(CadDraftingKit.Append(ms, tr, db, barPl, rebarLayer));

            // Elevation dimensions (hz & total Df)
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                new Point3d(elevX, footY, origin.Z), new Point3d(elevX, footY + hz, origin.Z),
                new Point3d(elevX - dimOff, footY + hz * 0.5, origin.Z), 90.0, dimLayer, dimStyleId));
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                new Point3d(elevX, footY, origin.Z), new Point3d(elevX, gradeY, origin.Z),
                new Point3d(elevX - dimOff * 1.9, (footY + gradeY) * 0.5, origin.Z), 90.0, dimLayer, dimStyleId));

            // Callouts
            double callX = elevX + bx + 0.35;
            handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, new Point3d(elevX + bx - cover, matY, origin.Z), new Point3d(callX, footY + hz * 0.4, origin.Z), rebarX + " / " + rebarY, textH, 0.25, noteLayer));
            if (subbase > 0)
                handles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, new Point3d(elevX + bx, elevY + subbase * 0.5, origin.Z), new Point3d(callX, elevY, origin.Z), string.Format(CultureInfo.InvariantCulture, "Solado e={0:0.00}m", subbase), textH, 0.25, noteLayer));

            handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                new Point3d(origin.X, origin.Y - dimOff - textH * 2.5, origin.Z),
                "ZAPATA " + tag + " — PLANTA Y CORTE\\P" + scaleLabel,
                textH * 1.2, 0, noteLayer, AttachmentPoint.TopLeft));

            int citCount = StampCitations(ms, tr, db, args["citations"] as JArray,
                new Point3d(elevX, origin.Y - dimOff - textH * 2.5, origin.Z), textH * 0.85, noteLayer, handles);

            return new JObject
            {
                ["tag"] = tag,
                ["footing_area_m2"] = Math.Round(bx * by, 2),
                ["concrete_volume_m3"] = Math.Round(bx * by * hz, 3),
                ["citations_stamped"] = citCount
            };
        }

        private static JObject DrawRebarSchedule(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            string title = ((string)args["title"] ?? "CUADRO DE ELEMENTOS ESTRUCTURALES").Trim();
            string layer = ((string)args["note_layer"] ?? "S-ANNO-SCHD").Trim();
            double textH = (double?)args["text_height"] ?? 0.16;

            CadDraftingKit.EnsureLayer(db, tr, layer, 7, "CONTINUOUS", 0.18, "Structural Schedules");

            var headers = new List<string>();
            if (args["headers"] is JArray hArr && hArr.Count > 0)
                foreach (JToken t in hArr) headers.Add(t?.ToString() ?? "");
            else
                headers.AddRange(new[] { "ELEMENTO", "SECCION", "ACERO LONG.", "ESTRIBOS", "f'c" });

            var rows = new List<IList<string>>();
            if (args["rows"] is JArray rArr)
            {
                foreach (JToken rTok in rArr)
                {
                    var row = new List<string>();
                    if (rTok is JArray ra) foreach (JToken cell in ra) row.Add(cell?.ToString() ?? "");
                    rows.Add(row);
                }
            }

            var colWidths = new List<double>();
            if (args["col_widths"] is JArray cwArr)
                foreach (JToken wTok in cwArr) colWidths.Add((double)wTok);

            handles.Add(CadDraftingKit.AddTable(ms, tr, db, origin, title, headers, rows, colWidths, textH, layer));
            int citCount = StampCitations(ms, tr, db, args["citations"] as JArray,
                new Point3d(origin.X, origin.Y - (rows.Count + 3) * Math.Max(textH * 2.2, 0.35), origin.Z),
                textH * 0.85, layer, handles);

            return new JObject
            {
                ["rows_drawn"] = rows.Count,
                ["citations_stamped"] = citCount
            };
        }

        private static void EnsureStructLayers(
            Database db, Transaction tr, JObject args,
            out string concLayer, out string rebarLayer, out string stirrupLayer, out string dimLayer, out string noteLayer)
        {
            concLayer = ((string)args["concrete_layer"] ?? "S-CONC-OUTL").Trim();
            rebarLayer = ((string)args["rebar_layer"] ?? "S-RBAR-LONG").Trim();
            stirrupLayer = ((string)args["stirrup_layer"] ?? "S-RBAR-STIR").Trim();
            dimLayer = ((string)args["dim_layer"] ?? "S-ANNO-DIMS").Trim();
            noteLayer = ((string)args["note_layer"] ?? "S-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, concLayer, 7, "CONTINUOUS", 0.35, "Structural Concrete Outline");
            CadDraftingKit.EnsureLayer(db, tr, rebarLayer, 1, "CONTINUOUS", 0.40, "Longitudinal Rebar");
            CadDraftingKit.EnsureLayer(db, tr, stirrupLayer, 3, "CONTINUOUS", 0.25, "Stirrups & Ties");
            CadDraftingKit.EnsureLayer(db, tr, dimLayer, 2, "CONTINUOUS", 0.15, "Structural Dimensions");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "Structural Notes & Callouts");
        }

        private static void AddFilledRebar(BlockTableRecord ms, Transaction tr, Database db, Point3d center, double r, string layer, List<string> handles)
        {
            handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(center, Vector3d.ZAxis, r), layer));
            // Octagonal polygon solid hatch for fast, crisp filled rebar dot
            var oct = new List<Point2d>(8);
            for (int k = 0; k < 8; k++)
            {
                double ang = k * Math.PI / 4.0;
                oct.Add(new Point2d(center.X + r * Math.Cos(ang), center.Y + r * Math.Sin(ang)));
            }
            string hh = CadDraftingKit.AddPolygonHatch(ms, tr, db, oct, "SOLID", 1.0, 0.0, layer);
            if (!string.IsNullOrEmpty(hh)) handles.Add(hh);
        }

        private static int StampCitations(BlockTableRecord ms, Transaction tr, Database db, JArray citations, Point3d pos, double textH, string layer, List<string> handles)
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
                handles.Add(CadDraftingKit.AddMText(ms, tr, db, pos, string.Join("\\P", lines), textH, 0, layer, AttachmentPoint.TopLeft));
            return count;
        }
    }
}
