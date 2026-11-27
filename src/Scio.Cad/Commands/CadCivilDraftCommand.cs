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
    /// Phase 4 — Parametric Roads &amp; Civil drafting tool (draw_road_section,
    /// draw_culvert_section, draw_profile_grid) working in both plain AutoCAD and Civil 3D,
    /// with dry_run=true default, confirmation_token, atomic Transaction commit, and
    /// post-commit Handle re-read.
    /// </summary>
    internal static class CadCivilDraftCommand
    {
        public static JObject Execute(JObject args)
        {
            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            bool dryRun = (bool?)args["dry_run"] ?? true;
            Document doc = CadDraftingKit.RequireActiveDocument(args, dryRun, out string activeTitle);
            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_civil_draft", doc.Name, args);

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
                case "draw_road_section":
                {
                    int lanesL = (int?)args["lanes_left"] ?? 1;
                    int lanesR = (int?)args["lanes_right"] ?? 1;
                    double laneW = (double?)args["lane_width"] ?? 3.30;
                    double shL = (double?)args["shoulder_left"] ?? 1.20;
                    double shR = (double?)args["shoulder_right"] ?? 1.20;
                    return new JObject
                    {
                        ["operation"] = op,
                        ["total_roadway_width"] = Math.Round((lanesL + lanesR) * laneW + shL + shR, 3),
                        ["crossfall_percent"] = (double?)args["crossfall_percent"] ?? 2.0,
                        ["pavement_layers_count"] = (args["pavement_layers"] as JArray)?.Count ?? 0
                    };
                }
                case "draw_culvert_section":
                    return new JObject
                    {
                        ["operation"] = op,
                        ["kind"] = ((string)args["kind"] ?? "box_culvert").Trim(),
                        ["inner_width"] = (double?)args["inner_width"] ?? 1.50,
                        ["inner_height"] = (double?)args["inner_height"] ?? 1.20
                    };
                case "draw_profile_grid":
                {
                    double s0 = (double?)args["start_station"] ?? 0.0;
                    double s1 = (double?)args["end_station"] ?? 200.0;
                    if (s1 <= s0) throw new ArgumentException("draw_profile_grid requires 'end_station' > 'start_station'.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["length_m"] = Math.Round(s1 - s0, 2),
                        ["ground_points"] = (args["ground_points"] as JArray)?.Count ?? 0,
                        ["grade_points"] = (args["grade_points"] as JArray)?.Count ?? 0
                    };
                }
                default:
                    throw new ArgumentException("Unsupported cad_civil_draft operation '" + op + "'.");
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
                        case "draw_road_section":
                            summary = DrawRoadSection(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_culvert_section":
                            summary = DrawCulvertSection(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_profile_grid":
                            summary = DrawProfileGrid(db, tr, ms, args, createdHandles);
                            break;
                        default:
                            throw new ArgumentException("Unsupported cad_civil_draft operation '" + op + "'.");
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

        private static JObject DrawRoadSection(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d clOrigin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            int lanesL = Math.Max(0, (int?)args["lanes_left"] ?? 1);
            int lanesR = Math.Max(0, (int?)args["lanes_right"] ?? 1);
            double laneW = (double?)args["lane_width"] ?? 3.30;
            double crossfall = (double?)args["crossfall_percent"] ?? 2.0;
            double shL = Math.Max(0.0, (double?)args["shoulder_left"] ?? 1.20);
            double shR = Math.Max(0.0, (double?)args["shoulder_right"] ?? 1.20);
            double shSlope = (double?)args["shoulder_slope_percent"] ?? 4.0;
            double ditchW = Math.Max(0.0, (double?)args["ditch_width"] ?? 0.80);
            double ditchD = Math.Max(0.0, (double?)args["ditch_depth"] ?? 0.35);
            double sidewalkL = Math.Max(0.0, (double?)args["sidewalk_left"] ?? 0.0);
            double sidewalkR = Math.Max(0.0, (double?)args["sidewalk_right"] ?? 0.0);
            double curbH = (double?)args["curb_height"] ?? 0.15;
            double textH = (double?)args["text_height"] ?? 0.18;
            string title = ((string)args["title"] ?? "SECCION TRANSVERSAL TIPICA DE VIA").Trim();
            string scaleLabel = ((string)args["scale_label"] ?? "ESC. 1:50").Trim();

            string roadLayer = ((string)args["road_layer"] ?? "C-ROAD-SECT").Trim();
            string clLayer = ((string)args["cl_layer"] ?? "C-ROAD-CNTR").Trim();
            string pavLayer = ((string)args["pavement_layer"] ?? "C-ROAD-PAVM").Trim();
            string dimLayer = ((string)args["dim_layer"] ?? "C-ANNO-DIMS").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "C-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, roadLayer, 7, "CONTINUOUS", 0.35, "Road Finished Grade");
            CadDraftingKit.EnsureLayer(db, tr, clLayer, 1, "CENTER", 0.18, "Road Centerline");
            CadDraftingKit.EnsureLayer(db, tr, pavLayer, 8, "CONTINUOUS", 0.13, "Pavement Structure");
            CadDraftingKit.EnsureLayer(db, tr, dimLayer, 2, "CONTINUOUS", 0.15, "Civil Dimensions");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "Civil Notes");
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-CIV", textH * 0.85, textH * 0.6, 1.0, 2);

            // Build right-side & left-side crown profile points
            var rightPts = new List<Point2d> { new Point2d(clOrigin.X, clOrigin.Y) };
            var xBreaks = new List<double> { clOrigin.X };

            // Right lanes
            double rx = clOrigin.X, ry = clOrigin.Y;
            for (int i = 0; i < lanesR; i++)
            {
                rx += laneW;
                ry -= laneW * (crossfall / 100.0);
                rightPts.Add(new Point2d(rx, ry));
                xBreaks.Add(rx);
            }
            if (shR > 0)
            {
                rx += shR;
                ry -= shR * (shSlope / 100.0);
                rightPts.Add(new Point2d(rx, ry));
                xBreaks.Add(rx);
            }
            double rightEdgeX = rx;
            if (sidewalkR > 0)
            {
                rightPts.Add(new Point2d(rx, ry + curbH));
                rx += sidewalkR;
                ry = ry + curbH + sidewalkR * 0.02;
                rightPts.Add(new Point2d(rx, ry));
                xBreaks.Add(rx);
            }
            else if (ditchW > 0 && ditchD > 0)
            {
                rightPts.Add(new Point2d(rx + ditchW * 0.5, ry - ditchD));
                rx += ditchW;
                rightPts.Add(new Point2d(rx, ry));
                xBreaks.Add(rx);
            }

            // Left side (mirrored)
            var leftPts = new List<Point2d>();
            double lx = clOrigin.X, ly = clOrigin.Y;
            for (int i = 0; i < lanesL; i++)
            {
                lx -= laneW;
                ly -= laneW * (crossfall / 100.0);
                leftPts.Add(new Point2d(lx, ly));
                xBreaks.Add(lx);
            }
            if (shL > 0)
            {
                lx -= shL;
                ly -= shL * (shSlope / 100.0);
                leftPts.Add(new Point2d(lx, ly));
                xBreaks.Add(lx);
            }
            double leftEdgeX = lx;
            if (sidewalkL > 0)
            {
                leftPts.Add(new Point2d(lx, ly + curbH));
                lx -= sidewalkL;
                ly = ly + curbH + sidewalkL * 0.02;
                leftPts.Add(new Point2d(lx, ly));
                xBreaks.Add(lx);
            }
            else if (ditchW > 0 && ditchD > 0)
            {
                leftPts.Add(new Point2d(lx - ditchW * 0.5, ly - ditchD));
                lx -= ditchW;
                leftPts.Add(new Point2d(lx, ly));
                xBreaks.Add(lx);
            }

            // Combine left (reversed) + right into one continuous finished grade polyline
            var gradePl = new Polyline();
            int vIdx = 0;
            for (int i = leftPts.Count - 1; i >= 0; i--)
                gradePl.AddVertexAt(vIdx++, leftPts[i], 0, 0.015, 0.015);
            for (int i = 0; i < rightPts.Count; i++)
                gradePl.AddVertexAt(vIdx++, rightPts[i], 0, 0.015, 0.015);
            handles.Add(CadDraftingKit.Append(ms, tr, db, gradePl, roadLayer));

            // Pavement structure layers beneath roadway (leftEdgeX .. rightEdgeX)
            double totalPavThick = 0.0;
            if (args["pavement_layers"] is JArray pavLayers)
            {
                double curDepth = 0.0;
                foreach (JObject plSpec in pavLayers)
                {
                    double thick = (double?)plSpec["thickness"] ?? 0.05;
                    if (thick <= 0) continue;
                    string pat = ((string)plSpec["hatch_pattern"] ?? "GRAVEL").Trim();
                    double patScale = (double?)plSpec["hatch_scale"] ?? Math.Max(thick * 0.6, 0.02);

                    double yTopL = ly, yTopR = ry;
                    // Build offset ribbon directly under roadway edges
                    double topOff = curDepth;
                    double botOff = curDepth + thick;
                    var poly = new List<Point2d>
                    {
                        new Point2d(leftEdgeX, clOrigin.Y - Math.Abs(leftEdgeX - clOrigin.X) * (crossfall / 100.0) - topOff),
                        new Point2d(clOrigin.X, clOrigin.Y - topOff),
                        new Point2d(rightEdgeX, clOrigin.Y - Math.Abs(rightEdgeX - clOrigin.X) * (crossfall / 100.0) - topOff),
                        new Point2d(rightEdgeX, clOrigin.Y - Math.Abs(rightEdgeX - clOrigin.X) * (crossfall / 100.0) - botOff),
                        new Point2d(clOrigin.X, clOrigin.Y - botOff),
                        new Point2d(leftEdgeX, clOrigin.Y - Math.Abs(leftEdgeX - clOrigin.X) * (crossfall / 100.0) - botOff)
                    };
                    var bPl = new Polyline();
                    for (int k = 0; k < poly.Count; k++) bPl.AddVertexAt(k, poly[k], 0, 0, 0);
                    bPl.Closed = true;
                    handles.Add(CadDraftingKit.Append(ms, tr, db, bPl, pavLayer));

                    string hh = CadDraftingKit.AddPolygonHatch(ms, tr, db, poly, pat, patScale, 0.0, pavLayer);
                    if (!string.IsNullOrEmpty(hh)) handles.Add(hh);

                    curDepth += thick;
                }
                totalPavThick = curDepth;
            }

            // Centerline (CL)
            handles.Add(CadDraftingKit.AddLine(ms, tr, db,
                new Point3d(clOrigin.X, clOrigin.Y - totalPavThick - 0.80, clOrigin.Z),
                new Point3d(clOrigin.X, clOrigin.Y + 1.20, clOrigin.Z),
                clLayer));
            handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                new Point3d(clOrigin.X, clOrigin.Y + 1.30, clOrigin.Z),
                "EJE (CL)", textH, 0, noteLayer, AttachmentPoint.BottomCenter));

            // Slope annotations on right & left lanes
            if (lanesR > 0)
            {
                handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                    new Point3d(clOrigin.X + laneW * 0.5, clOrigin.Y + textH * 1.1, clOrigin.Z),
                    string.Format(CultureInfo.InvariantCulture, "-{0:0.##}%", crossfall),
                    textH * 0.85, 0, noteLayer, AttachmentPoint.BottomCenter));
            }
            if (lanesL > 0)
            {
                handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                    new Point3d(clOrigin.X - laneW * 0.5, clOrigin.Y + textH * 1.1, clOrigin.Z),
                    string.Format(CultureInfo.InvariantCulture, "-{0:0.##}%", crossfall),
                    textH * 0.85, 0, noteLayer, AttachmentPoint.BottomCenter));
            }

            // Horizontal dimensions across all xBreaks
            xBreaks.Sort();
            double dimY = clOrigin.Y + 0.65;
            for (int i = 0; i < xBreaks.Count - 1; i++)
            {
                if (xBreaks[i + 1] - xBreaks[i] < 0.05) continue;
                handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                    new Point3d(xBreaks[i], clOrigin.Y, clOrigin.Z),
                    new Point3d(xBreaks[i + 1], clOrigin.Y, clOrigin.Z),
                    new Point3d((xBreaks[i] + xBreaks[i + 1]) * 0.5, dimY, clOrigin.Z),
                    0.0, dimLayer, dimStyleId));
            }
            if (xBreaks.Count > 2)
            {
                handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                    new Point3d(xBreaks[0], clOrigin.Y, clOrigin.Z),
                    new Point3d(xBreaks[xBreaks.Count - 1], clOrigin.Y, clOrigin.Z),
                    new Point3d((xBreaks[0] + xBreaks[xBreaks.Count - 1]) * 0.5, dimY + textH * 2.4, clOrigin.Z),
                    0.0, dimLayer, dimStyleId));
            }

            // Title & citations
            double bottomY = clOrigin.Y - totalPavThick - 1.25;
            handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                new Point3d(xBreaks[0], bottomY, clOrigin.Z),
                title + "\\P" + scaleLabel,
                textH * 1.2, 0, noteLayer, AttachmentPoint.TopLeft));

            int citCount = StampCitations(ms, tr, db, args["citations"] as JArray,
                new Point3d(clOrigin.X, bottomY, clOrigin.Z), textH * 0.85, noteLayer, handles);

            return new JObject
            {
                ["total_section_width"] = Math.Round(xBreaks[xBreaks.Count - 1] - xBreaks[0], 3),
                ["pavement_thickness"] = Math.Round(totalPavThick, 3),
                ["citations_stamped"] = citCount
            };
        }

        private static JObject DrawCulvertSection(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            string kind = ((string)args["kind"] ?? "box_culvert").Trim().ToLowerInvariant();
            double innerW = (double?)args["inner_width"] ?? 1.50;
            double innerH = (double?)args["inner_height"] ?? 1.20;
            double wallT = (double?)args["wall_thickness"] ?? 0.25;
            double subbase = (double?)args["subbase_thickness"] ?? 0.10;
            double textH = (double?)args["text_height"] ?? 0.14;
            string title = ((string)args["title"] ?? "DETALLE DE ALCANTARILLA / DRENAJE").Trim();
            string scaleLabel = ((string)args["scale_label"] ?? "ESC. 1:25").Trim();

            string structLayer = ((string)args["struct_layer"] ?? "C-DRNG-STRC").Trim();
            string dimLayer = ((string)args["dim_layer"] ?? "C-ANNO-DIMS").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "C-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, structLayer, 7, "CONTINUOUS", 0.35, "Drainage Structure");
            CadDraftingKit.EnsureLayer(db, tr, dimLayer, 2, "CONTINUOUS", 0.15, "Drainage Dimensions");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "Drainage Notes");
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-CIV", textH * 0.85, textH * 0.6, 1.0, 2);

            double outerW, outerH;
            if (kind == "pipe_culvert")
            {
                double rIn = innerW * 0.5;
                double rOut = rIn + wallT;
                outerW = rOut * 2.0;
                outerH = rOut * 2.0;
                Point3d center = new Point3d(origin.X + rOut, origin.Y + subbase + rOut, origin.Z);
                handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(center, Vector3d.ZAxis, rOut), structLayer));
                handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(center, Vector3d.ZAxis, rIn), structLayer));
                if (subbase > 0)
                    handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X - 0.15, origin.Y), outerW + 0.30, subbase, structLayer));
            }
            else
            {
                // box_culvert (default)
                outerW = innerW + 2 * wallT;
                outerH = innerH + 2 * wallT;
                if (subbase > 0)
                    handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X - 0.10, origin.Y), outerW + 0.20, subbase, structLayer));
                handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, origin.Y + subbase), outerW, outerH, structLayer));
                handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X + wallT, origin.Y + subbase + wallT), innerW, innerH, structLayer));
            }

            double dimOff = Math.Max(textH * 3.0, 0.28);
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                new Point3d(origin.X, origin.Y + subbase, origin.Z),
                new Point3d(origin.X + outerW, origin.Y + subbase, origin.Z),
                new Point3d(origin.X + outerW * 0.5, origin.Y - dimOff, origin.Z), 0.0, dimLayer, dimStyleId));
            handles.Add(CadDraftingKit.AddLinearDim(ms, tr, db,
                new Point3d(origin.X, origin.Y + subbase, origin.Z),
                new Point3d(origin.X, origin.Y + subbase + outerH, origin.Z),
                new Point3d(origin.X - dimOff, origin.Y + subbase + outerH * 0.5, origin.Z), 90.0, dimLayer, dimStyleId));

            handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                new Point3d(origin.X, origin.Y - dimOff - textH * 2.5, origin.Z),
                title + "\\P" + scaleLabel, textH * 1.2, 0, noteLayer, AttachmentPoint.TopLeft));

            int citCount = StampCitations(ms, tr, db, args["citations"] as JArray,
                new Point3d(origin.X + outerW + 0.4, origin.Y + outerH, origin.Z), textH * 0.85, noteLayer, handles);

            return new JObject
            {
                ["kind"] = kind,
                ["outer_width"] = Math.Round(outerW, 3),
                ["outer_height"] = Math.Round(outerH, 3),
                ["citations_stamped"] = citCount
            };
        }

        private static JObject DrawProfileGrid(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double s0 = (double?)args["start_station"] ?? 0.0;
            double s1 = (double?)args["end_station"] ?? 200.0;
            double sStep = Math.Max(1.0, (double?)args["station_interval"] ?? 20.0);
            double eMin = (double?)args["min_elev"] ?? 100.0;
            double eMax = (double?)args["max_elev"] ?? 120.0;
            double eStep = Math.Max(0.5, (double?)args["elev_interval"] ?? 2.0);
            double vExag = Math.Max(1.0, (double?)args["vertical_exaggeration"] ?? 10.0);
            double textH = (double?)args["text_height"] ?? Math.Max((s1 - s0) * 0.012, 1.2);
            string title = ((string)args["title"] ?? "PERFIL LONGITUDINAL").Trim();

            string gridLayer = ((string)args["grid_layer"] ?? "C-PROF-GRID").Trim();
            string groundLayer = ((string)args["ground_layer"] ?? "C-PROF-EG").Trim();
            string gradeLayer = ((string)args["grade_layer"] ?? "C-PROF-FG").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "C-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, gridLayer, 8, "CONTINUOUS", 0.09, "Profile Grid");
            CadDraftingKit.EnsureLayer(db, tr, groundLayer, 3, "DASHED", 0.25, "Existing Ground Profile");
            CadDraftingKit.EnsureLayer(db, tr, gradeLayer, 1, "CONTINUOUS", 0.35, "Finished Grade Profile");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "Profile Annotations");

            double gridW = s1 - s0;
            double gridH = (eMax - eMin) * vExag;

            // Outer frame
            handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, origin.Y), gridW, gridH, gridLayer));

            // Vertical station gridlines + station labels
            for (double st = s0; st <= s1 + 1e-6; st += sStep)
            {
                double x = origin.X + (st - s0);
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(x, origin.Y, origin.Z), new Point3d(x, origin.Y + gridH, origin.Z), gridLayer));
                handles.Add(CadDraftingKit.AddMText(ms, tr, db, new Point3d(x, origin.Y - textH * 0.6, origin.Z), FormatStation(st), textH * 0.8, 0, noteLayer, AttachmentPoint.TopCenter));
            }

            // Horizontal elevation gridlines + labels
            for (double el = eMin; el <= eMax + 1e-6; el += eStep)
            {
                double y = origin.Y + (el - eMin) * vExag;
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, new Point3d(origin.X, y, origin.Z), new Point3d(origin.X + gridW, y, origin.Z), gridLayer));
                handles.Add(CadDraftingKit.AddMText(ms, tr, db, new Point3d(origin.X - textH * 0.6, y, origin.Z), string.Format(CultureInfo.InvariantCulture, "{0:0.00}", el), textH * 0.8, 0, noteLayer, AttachmentPoint.MiddleRight));
            }

            // Existing Ground polyline
            int egCount = DrawProfileCurve(ms, tr, db, args["ground_points"] as JArray, origin, s0, eMin, vExag, groundLayer, handles);
            // Finished Grade polyline
            int fgCount = DrawProfileCurve(ms, tr, db, args["grade_points"] as JArray, origin, s0, eMin, vExag, gradeLayer, handles);

            handles.Add(CadDraftingKit.AddMText(ms, tr, db,
                new Point3d(origin.X, origin.Y + gridH + textH * 2.0, origin.Z),
                string.Format(CultureInfo.InvariantCulture, "{0} (Exag. Vertical {1:0.#}x)", title, vExag),
                textH * 1.25, 0, noteLayer, AttachmentPoint.BottomLeft));

            int citCount = StampCitations(ms, tr, db, args["citations"] as JArray,
                new Point3d(origin.X, origin.Y - textH * 3.2, origin.Z), textH * 0.8, noteLayer, handles);

            return new JObject
            {
                ["station_span_m"] = Math.Round(gridW, 2),
                ["vertical_exaggeration"] = vExag,
                ["ground_vertices"] = egCount,
                ["grade_vertices"] = fgCount,
                ["citations_stamped"] = citCount
            };
        }

        private static int DrawProfileCurve(
            BlockTableRecord ms, Transaction tr, Database db, JArray ptsArr,
            Point3d origin, double s0, double eMin, double vExag, string layer, List<string> handles)
        {
            if (ptsArr == null || ptsArr.Count < 2) return 0;
            var pl = new Polyline();
            for (int i = 0; i < ptsArr.Count; i++)
            {
                var p = ptsArr[i] as JArray;
                if (p == null || p.Count < 2) continue;
                double st = (double)p[0];
                double el = (double)p[1];
                pl.AddVertexAt(i, new Point2d(origin.X + (st - s0), origin.Y + (el - eMin) * vExag), 0, 0, 0);
            }
            if (pl.NumberOfVertices >= 2)
                handles.Add(CadDraftingKit.Append(ms, tr, db, pl, layer));
            return pl.NumberOfVertices;
        }

        private static string FormatStation(double meters)
        {
            int km = (int)Math.Floor(meters / 1000.0);
            double rem = meters - km * 1000.0;
            return string.Format(CultureInfo.InvariantCulture, "{0}+{1:000}", km, rem);
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
