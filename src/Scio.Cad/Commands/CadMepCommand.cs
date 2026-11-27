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
    /// Parametric MEP automatic drafting tool (draw_pipe_run, draw_duct_run,
    /// draw_electrical_layout, draw_mep_legend) with dry_run=true default,
    /// confirmation_token, atomic Transaction commit, and post-commit Handle re-read.
    /// Organisation-neutral: system layers, diameters, slopes, wire counts, and
    /// normative citations arrive as call-time arguments.
    /// </summary>
    internal static class CadMepCommand
    {
        public static JObject Execute(JObject args)
        {
            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            bool dryRun = (bool?)args["dry_run"] ?? true;
            Document doc = CadDraftingKit.RequireActiveDocument(args, dryRun, out string activeTitle);
            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_mep", doc.Name, args);

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
                case "draw_pipe_run":
                {
                    var pts = args["points"] as JArray ?? new JArray();
                    if (pts.Count < 2)
                        throw new ArgumentException("draw_pipe_run requires 'points' with at least 2 vertices.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["system"] = ((string)args["system"] ?? "cold_water").Trim(),
                        ["vertex_count"] = pts.Count,
                        ["segment_count"] = pts.Count - 1,
                        ["diameter_label"] = ((string)args["diameter_label"] ?? "").Trim(),
                        ["slope_percent"] = (double?)args["slope_percent"] ?? 0.0
                    };
                }
                case "draw_duct_run":
                {
                    var pts = args["points"] as JArray ?? new JArray();
                    if (pts.Count < 2)
                        throw new ArgumentException("draw_duct_run requires 'points' with at least 2 vertices.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["system"] = ((string)args["system"] ?? "supply").Trim(),
                        ["vertex_count"] = pts.Count,
                        ["width"] = (double?)args["width"] ?? 0.40,
                        ["height"] = (double?)args["height"] ?? 0.25
                    };
                }
                case "draw_electrical_layout":
                {
                    int circuits = (args["circuits"] as JArray)?.Count ?? 0;
                    int fixtures = (args["fixtures"] as JArray)?.Count ?? 0;
                    int trays = (args["trays"] as JArray)?.Count ?? 0;
                    if (circuits + fixtures + trays == 0)
                        throw new ArgumentException("draw_electrical_layout requires at least one of 'circuits', 'fixtures', or 'trays'.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["circuits_count"] = circuits,
                        ["fixtures_count"] = fixtures,
                        ["trays_count"] = trays
                    };
                }
                case "draw_mep_legend":
                {
                    var items = args["items"] as JArray ?? new JArray();
                    if (items.Count == 0)
                        throw new ArgumentException("draw_mep_legend requires a non-empty 'items' array.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["legend_items"] = items.Count
                    };
                }
                default:
                    throw new ArgumentException("Unsupported cad_mep operation '" + op + "'.");
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
                        case "draw_pipe_run":
                            summary = DrawPipeRun(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_duct_run":
                            summary = DrawDuctRun(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_electrical_layout":
                            summary = DrawElectricalLayout(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_mep_legend":
                            summary = DrawMepLegend(db, tr, ms, args, createdHandles);
                            break;
                        default:
                            throw new ArgumentException("Unsupported cad_mep operation '" + op + "'.");
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

        private static JObject DrawPipeRun(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            var ptsArr = args["points"] as JArray ?? new JArray();
            string system = ((string)args["system"] ?? "cold_water").Trim().ToLowerInvariant();
            string diamLabel = ((string)args["diameter_label"] ?? "").Trim();
            double slopePct = (double?)args["slope_percent"] ?? 0.0;
            double? startElev = (double?)args["start_elevation"];
            double textH = (double?)args["text_height"] ?? 0.14;
            bool flowArrows = (bool?)args["flow_arrows"] ?? true;
            bool drawFittings = (bool?)args["draw_fittings"] ?? true;
            double lineWidth = (double?)args["line_width"] ?? 0.0;

            ResolvePipeSystemDefaults(system, out string defLayer, out short defColor, out string defLinetype);
            string pipeLayer = ((string)args["layer"] ?? defLayer).Trim();
            string noteLayer = ((string)args["note_layer"] ?? "P-ANNO-NOTE").Trim();
            short colorAci = (short?)((int?)args["color_aci"] ?? defColor) ?? defColor;
            string linetype = ((string)args["linetype"] ?? defLinetype).Trim();

            CadDraftingKit.EnsureLayer(db, tr, pipeLayer, colorAci, linetype, 0.30, "MEP Pipe System: " + system);
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.15, "MEP Pipe Annotations");

            var pts = new List<Point3d>(ptsArr.Count);
            for (int i = 0; i < ptsArr.Count; i++)
                pts.Add(CadDraftingKit.ReadPoint3d(ptsArr[i], "points[" + i + "]"));

            // Main pipe polyline
            var pl = new Polyline();
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, lineWidth, lineWidth);
            handles.Add(CadDraftingKit.Append(ms, tr, db, pl, pipeLayer));

            // Compute cumulative length & invert elevations along slope
            double totalLength = 0.0;
            double curElev = startElev ?? 0.0;
            var nodeElevs = new JArray();
            if (startElev.HasValue) nodeElevs.Add(Math.Round(curElev, 4));

            for (int i = 0; i < pts.Count - 1; i++)
            {
                Point3d p0 = pts[i];
                Point3d p1 = pts[i + 1];
                Vector3d seg = p1 - p0;
                double len = seg.Length;
                if (len < 1e-6) continue;
                totalLength += len;

                if (startElev.HasValue)
                {
                    curElev -= len * (slopePct / 100.0);
                    nodeElevs.Add(Math.Round(curElev, 4));
                }

                Vector3d unit = seg / len;
                Vector3d normal = new Vector3d(-unit.Y, unit.X, 0.0);
                Point3d mid = p0 + seg * 0.5;
                double rotDeg = Math.Atan2(unit.Y, unit.X) * 180.0 / Math.PI;
                if (rotDeg > 90.0) rotDeg -= 180.0;
                if (rotDeg < -90.0) rotDeg += 180.0;

                // Directional flow arrow at 35% of segment
                if (flowArrows && len >= textH * 2.5)
                {
                    Point3d tip = p0 + seg * 0.35;
                    double aLen = Math.Max(textH * 1.1, 0.14);
                    double aHalfW = aLen * 0.42;
                    Point3d wing1 = tip - unit * aLen + normal * aHalfW;
                    Point3d wing2 = tip - unit * aLen - normal * aHalfW;
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, tip, wing1, pipeLayer));
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, tip, wing2, pipeLayer));
                }

                // Segment annotation (diameter + slope)
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(diamLabel)) parts.Add(diamLabel);
                if (Math.Abs(slopePct) > 1e-6)
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "S={0:0.##}%", slopePct));
                if (parts.Count > 0 && len >= textH * 2.0)
                {
                    Point3d lblPt = mid + normal * (textH * 1.25);
                    handles.Add(CadDraftingKit.AddMText(
                        ms, tr, db, lblPt, string.Join(" ", parts),
                        textH, 0, noteLayer, AttachmentPoint.BottomCenter, rotDeg, true));
                }
            }

            // Fitting markers at intermediate elbows & end points
            if (drawFittings)
            {
                double fitR = Math.Max(textH * 0.35, 0.045);
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(pts[i], Vector3d.ZAxis, fitR), pipeLayer));
                }
            }

            var res = new JObject
            {
                ["system"] = system,
                ["layer"] = pipeLayer,
                ["segment_count"] = pts.Count - 1,
                ["total_length"] = Math.Round(totalLength, 4)
            };
            if (startElev.HasValue) res["node_elevations"] = nodeElevs;
            return res;
        }

        private static JObject DrawDuctRun(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            var ptsArr = args["points"] as JArray ?? new JArray();
            string system = ((string)args["system"] ?? "supply").Trim().ToLowerInvariant();
            double width = (double?)args["width"] ?? 0.40;
            double height = (double?)args["height"] ?? 0.25;
            if (width <= 0) throw new ArgumentException("draw_duct_run requires positive 'width'.");
            string sizeLabel = ((string)args["size_label"] ?? "").Trim();
            if (string.IsNullOrEmpty(sizeLabel))
                sizeLabel = string.Format(CultureInfo.InvariantCulture, "{0:0}x{1:0}mm", width * 1000.0, height * 1000.0);

            double textH = (double?)args["text_height"] ?? Math.Max(width * 0.32, 0.12);
            bool includeCenterline = (bool?)args["include_centerline"] ?? true;
            bool flowArrows = (bool?)args["flow_arrows"] ?? true;

            ResolveDuctSystemDefaults(system, out string defLayer, out short defColor);
            string ductLayer = ((string)args["layer"] ?? defLayer).Trim();
            string centerLayer = ((string)args["center_layer"] ?? "M-HVAC-CNTR").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "M-ANNO-NOTE").Trim();

            CadDraftingKit.EnsureLayer(db, tr, ductLayer, (short?)((int?)args["color_aci"] ?? defColor) ?? defColor, "CONTINUOUS", 0.30, "HVAC Ductwork: " + system);
            CadDraftingKit.EnsureLayer(db, tr, centerLayer, 8, "CENTER", 0.13, "HVAC Duct Centerline");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.15, "HVAC Annotations");

            var pts = new List<Point3d>(ptsArr.Count);
            for (int i = 0; i < ptsArr.Count; i++)
                pts.Add(CadDraftingKit.ReadPoint3d(ptsArr[i], "points[" + i + "]"));

            double halfW = width * 0.5;
            double totalLength = 0.0;

            for (int i = 0; i < pts.Count - 1; i++)
            {
                Point3d p0 = pts[i];
                Point3d p1 = pts[i + 1];
                Vector3d seg = p1 - p0;
                double len = seg.Length;
                if (len < 1e-6) continue;
                totalLength += len;

                Vector3d unit = seg / len;
                Vector3d normal = new Vector3d(-unit.Y, unit.X, 0.0);

                // Double-line duct rectangle per segment
                Point3d c0 = p0 + normal * halfW;
                Point3d c1 = p1 + normal * halfW;
                Point3d c2 = p1 - normal * halfW;
                Point3d c3 = p0 - normal * halfW;

                var ductPl = new Polyline();
                ductPl.AddVertexAt(0, new Point2d(c0.X, c0.Y), 0, 0, 0);
                ductPl.AddVertexAt(1, new Point2d(c1.X, c1.Y), 0, 0, 0);
                ductPl.AddVertexAt(2, new Point2d(c2.X, c2.Y), 0, 0, 0);
                ductPl.AddVertexAt(3, new Point2d(c3.X, c3.Y), 0, 0, 0);
                ductPl.Closed = true;
                handles.Add(CadDraftingKit.Append(ms, tr, db, ductPl, ductLayer));

                if (includeCenterline)
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, p0, p1, centerLayer));

                double rotDeg = Math.Atan2(unit.Y, unit.X) * 180.0 / Math.PI;
                if (rotDeg > 90.0) rotDeg -= 180.0;
                if (rotDeg < -90.0) rotDeg += 180.0;

                if (flowArrows && len >= width * 1.2)
                {
                    Point3d tip = p0 + seg * 0.25;
                    double aLen = Math.Max(width * 0.45, 0.15);
                    double aHalfW = aLen * 0.4;
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, tip, tip - unit * aLen + normal * aHalfW, ductLayer));
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, tip, tip - unit * aLen - normal * aHalfW, ductLayer));
                }

                Point3d mid = p0 + seg * 0.5;
                handles.Add(CadDraftingKit.AddMText(
                    ms, tr, db, mid + normal * (halfW + textH * 0.9),
                    sizeLabel, textH, 0, noteLayer, AttachmentPoint.BottomCenter, rotDeg, true));
            }

            return new JObject
            {
                ["system"] = system,
                ["layer"] = ductLayer,
                ["size_label"] = sizeLabel,
                ["segment_count"] = pts.Count - 1,
                ["total_length"] = Math.Round(totalLength, 4)
            };
        }

        private static JObject DrawElectricalLayout(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            string circuitLayer = ((string)args["circuit_layer"] ?? "E-POWR-CIRC").Trim();
            string fixtureLayer = ((string)args["fixture_layer"] ?? "E-LITE-FIXT").Trim();
            string trayLayer = ((string)args["tray_layer"] ?? "E-POWR-TRAY").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "E-ANNO-NOTE").Trim();
            double textH = (double?)args["text_height"] ?? 0.14;

            CadDraftingKit.EnsureLayer(db, tr, circuitLayer, 1, "CONTINUOUS", 0.20, "Electrical Circuits & Conduits");
            CadDraftingKit.EnsureLayer(db, tr, fixtureLayer, 2, "CONTINUOUS", 0.25, "Electrical Fixtures & Panels");
            CadDraftingKit.EnsureLayer(db, tr, trayLayer, 6, "CONTINUOUS", 0.25, "Cable Trays");
            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.15, "Electrical Annotations");

            int circuitsDrawn = 0, fixturesDrawn = 0, traysDrawn = 0;

            // 1. Circuits (conduit arcs/polylines + conductor tick marks + circuit tag)
            if (args["circuits"] is JArray circuits)
            {
                foreach (JObject c in circuits)
                {
                    var ptsArr = c["points"] as JArray;
                    if (ptsArr == null || ptsArr.Count < 2) continue;
                    double bulge = (double?)c["bulge"] ?? 0.18;
                    string tag = ((string)c["tag"] ?? "").Trim();
                    string wireSpec = ((string)c["wire_spec"] ?? "").Trim();
                    int wires = (int?)c["wires"] ?? 0;
                    string cLayer = ((string)c["layer"] ?? circuitLayer).Trim();

                    var pl = new Polyline();
                    for (int i = 0; i < ptsArr.Count; i++)
                    {
                        Point3d p = CadDraftingKit.ReadPoint3d(ptsArr[i], "points[" + i + "]");
                        double b = (i < ptsArr.Count - 1) ? bulge : 0.0;
                        pl.AddVertexAt(i, new Point2d(p.X, p.Y), b, 0, 0);
                    }
                    handles.Add(CadDraftingKit.Append(ms, tr, db, pl, cLayer));

                    // Conductor ticks & circuit label on first segment midpoint
                    Point3d p0 = CadDraftingKit.ReadPoint3d(ptsArr[0], "points[0]");
                    Point3d p1 = CadDraftingKit.ReadPoint3d(ptsArr[1], "points[1]");
                    Vector3d seg = p1 - p0;
                    if (seg.Length > 1e-4)
                    {
                        Vector3d unit = seg / seg.Length;
                        Vector3d normal = new Vector3d(-unit.Y, unit.X, 0.0);
                        // Approximate arc midpoint offset from chord
                        double sagitta = bulge * seg.Length * 0.5;
                        Point3d mid = p0 + seg * 0.5 - normal * sagitta;

                        if (wires > 0)
                        {
                            double tickH = Math.Max(textH * 0.7, 0.08);
                            double step = tickH * 0.55;
                            double startOff = -((wires - 1) * step) * 0.5;
                            for (int w = 0; w < Math.Min(wires, 8); w++)
                            {
                                Point3d tc = mid + unit * (startOff + w * step);
                                handles.Add(CadDraftingKit.AddLine(ms, tr, db,
                                    tc - normal * (tickH * 0.5) - unit * (tickH * 0.2),
                                    tc + normal * (tickH * 0.5) + unit * (tickH * 0.2),
                                    cLayer));
                            }
                        }

                        string lbl = string.IsNullOrEmpty(wireSpec) ? tag : (string.IsNullOrEmpty(tag) ? wireSpec : tag + " (" + wireSpec + ")");
                        if (!string.IsNullOrEmpty(lbl))
                        {
                            handles.Add(CadDraftingKit.AddMText(
                                ms, tr, db, mid + normal * (textH * 1.1),
                                lbl, textH, 0, noteLayer, AttachmentPoint.BottomCenter, 0, true));
                        }
                    }
                    circuitsDrawn++;
                }
            }

            // 2. Fixtures (panel, luminaire, outlet, switch, detector, junction_box)
            if (args["fixtures"] is JArray fixtures)
            {
                foreach (JObject f in fixtures)
                {
                    string kind = ((string)f["kind"] ?? "luminaire").Trim().ToLowerInvariant();
                    Point3d pos = CadDraftingKit.ReadPoint3d(f["position"], "position");
                    double size = (double?)f["size"] ?? 0.30;
                    string tag = ((string)f["tag"] ?? "").Trim();
                    string fLayer = ((string)f["layer"] ?? fixtureLayer).Trim();

                    if (kind == "panel")
                    {
                        double w = (double?)f["width"] ?? Math.Max(size * 2.0, 0.60);
                        double h = (double?)f["height"] ?? Math.Max(size * 0.8, 0.24);
                        handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(pos.X - w * 0.5, pos.Y - h * 0.5), w, h, fLayer));
                        // Half-filled panelboard convention
                        var halfPoly = new List<Point2d>
                        {
                            new Point2d(pos.X - w * 0.5, pos.Y - h * 0.5),
                            new Point2d(pos.X + w * 0.5, pos.Y - h * 0.5),
                            new Point2d(pos.X + w * 0.5, pos.Y + h * 0.5)
                        };
                        string hh = CadDraftingKit.AddPolygonHatch(ms, tr, db, halfPoly, "SOLID", 1.0, 0.0, fLayer);
                        if (!string.IsNullOrEmpty(hh)) handles.Add(hh);
                    }
                    else if (kind == "luminaire")
                    {
                        double r = size * 0.5;
                        handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(pos, Vector3d.ZAxis, r), fLayer));
                        handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos + new Vector3d(-r, 0, 0), pos + new Vector3d(r, 0, 0), fLayer));
                        handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos + new Vector3d(0, -r, 0), pos + new Vector3d(0, r, 0), fLayer));
                    }
                    else if (kind == "outlet")
                    {
                        double r = size * 0.4;
                        handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(pos, Vector3d.ZAxis, r), fLayer));
                        handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos + new Vector3d(-r * 0.35, -r * 1.3, 0), pos + new Vector3d(-r * 0.35, r * 1.3, 0), fLayer));
                        handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos + new Vector3d(r * 0.35, -r * 1.3, 0), pos + new Vector3d(r * 0.35, r * 1.3, 0), fLayer));
                    }
                    else if (kind == "switch")
                    {
                        double r = size * 0.35;
                        handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(pos, Vector3d.ZAxis, r), fLayer));
                        handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos, pos + new Vector3d(r * 1.6, r * 1.6, 0), fLayer));
                    }
                    else if (kind == "detector")
                    {
                        double r = size * 0.45;
                        handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(pos, Vector3d.ZAxis, r), fLayer));
                        handles.Add(CadDraftingKit.AddMText(ms, tr, db, pos, "S", r * 1.1, 0, fLayer, AttachmentPoint.MiddleCenter));
                    }
                    else
                    {
                        // junction_box or generic
                        handles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(pos.X - size * 0.5, pos.Y - size * 0.5), size, size, fLayer));
                    }

                    if (!string.IsNullOrEmpty(tag))
                    {
                        handles.Add(CadDraftingKit.AddMText(
                            ms, tr, db, pos + new Vector3d(size * 0.75, size * 0.55, 0),
                            tag, textH, 0, noteLayer, AttachmentPoint.BottomLeft));
                    }
                    fixturesDrawn++;
                }
            }

            // 3. Cable trays (double line + transverse rungs)
            if (args["trays"] is JArray trays)
            {
                foreach (JObject t in trays)
                {
                    var ptsArr = t["points"] as JArray;
                    if (ptsArr == null || ptsArr.Count < 2) continue;
                    double w = (double?)t["width"] ?? 0.30;
                    double rungSpacing = (double?)t["rung_spacing"] ?? Math.Max(w * 1.2, 0.30);
                    string tLayer = ((string)t["layer"] ?? trayLayer).Trim();
                    double halfW = w * 0.5;

                    for (int i = 0; i < ptsArr.Count - 1; i++)
                    {
                        Point3d p0 = CadDraftingKit.ReadPoint3d(ptsArr[i], "points[" + i + "]");
                        Point3d p1 = CadDraftingKit.ReadPoint3d(ptsArr[i + 1], "points[" + (i + 1) + "]");
                        Vector3d seg = p1 - p0;
                        double len = seg.Length;
                        if (len < 1e-6) continue;
                        Vector3d unit = seg / len;
                        Vector3d normal = new Vector3d(-unit.Y, unit.X, 0.0);

                        handles.Add(CadDraftingKit.AddLine(ms, tr, db, p0 + normal * halfW, p1 + normal * halfW, tLayer));
                        handles.Add(CadDraftingKit.AddLine(ms, tr, db, p0 - normal * halfW, p1 - normal * halfW, tLayer));

                        int rungs = Math.Min(200, (int)Math.Floor(len / rungSpacing));
                        for (int r = 1; r <= rungs; r++)
                        {
                            Point3d rp = p0 + unit * (r * rungSpacing);
                            handles.Add(CadDraftingKit.AddLine(ms, tr, db, rp - normal * halfW, rp + normal * halfW, tLayer));
                        }
                    }
                    traysDrawn++;
                }
            }

            return new JObject
            {
                ["circuits_drawn"] = circuitsDrawn,
                ["fixtures_drawn"] = fixturesDrawn,
                ["trays_drawn"] = traysDrawn
            };
        }

        private static JObject DrawMepLegend(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            double textH = (double?)args["text_height"] ?? 0.14;
            string title = ((string)args["title"] ?? "LEYENDA MEP / INSTALACIONES").Trim();
            string noteLayer = ((string)args["note_layer"] ?? "M-ANNO-NOTE").Trim();
            var items = args["items"] as JArray ?? new JArray();

            CadDraftingKit.EnsureLayer(db, tr, noteLayer, 7, "CONTINUOUS", 0.18, "MEP Legend");
            handles.Add(CadDraftingKit.AddMText(ms, tr, db, origin, title, textH * 1.25, 0, noteLayer, AttachmentPoint.TopLeft));

            double sampleLen = textH * 5.0;
            double rowStep = textH * 2.4;
            double curY = origin.Y - textH * 2.4;
            int count = 0;

            foreach (JObject item in items)
            {
                string label = ((string)item["label"] ?? "SISTEMA").Trim();
                string spec = ((string)item["spec"] ?? "").Trim();
                string citation = ((string)item["citation"] ?? "").Trim();
                string layer = ((string)item["layer"] ?? noteLayer).Trim();
                short? colorAci = (short?)(int?)item["color_aci"];
                string linetype = ((string)item["linetype"] ?? "CONTINUOUS").Trim();

                CadDraftingKit.EnsureLayer(db, tr, layer, colorAci, linetype, 0.25);
                handles.Add(CadDraftingKit.AddLine(ms, tr, db,
                    new Point3d(origin.X, curY, origin.Z),
                    new Point3d(origin.X + sampleLen, curY, origin.Z),
                    layer, colorAci));

                string fullText = label;
                if (!string.IsNullOrEmpty(spec)) fullText += " — " + spec;
                if (!string.IsNullOrEmpty(citation)) fullText += " [" + citation + "]";

                handles.Add(CadDraftingKit.AddMText(
                    ms, tr, db,
                    new Point3d(origin.X + sampleLen + textH * 1.2, curY, origin.Z),
                    fullText, textH, 0, noteLayer, AttachmentPoint.MiddleLeft));

                curY -= rowStep;
                count++;
            }

            return new JObject
            {
                ["legend_items_drawn"] = count
            };
        }

        private static void ResolvePipeSystemDefaults(string system, out string layer, out short colorAci, out string linetype)
        {
            switch (system)
            {
                case "hot_water":
                    layer = "P-DOMW-HOT"; colorAci = 1; linetype = "DASHED"; return;
                case "sanitary":
                    layer = "P-SANR-PIPE"; colorAci = 3; linetype = "CONTINUOUS"; return;
                case "storm":
                    layer = "P-STRM-PIPE"; colorAci = 4; linetype = "DASHED"; return;
                case "fire":
                    layer = "FP-SPRN-PIPE"; colorAci = 1; linetype = "CONTINUOUS"; return;
                case "gas":
                    layer = "P-GAS-PIPE"; colorAci = 2; linetype = "DASHDOT"; return;
                default:
                    layer = "P-DOMW-COLD"; colorAci = 5; linetype = "CONTINUOUS"; return;
            }
        }

        private static void ResolveDuctSystemDefaults(string system, out string layer, out short colorAci)
        {
            switch (system)
            {
                case "return":
                    layer = "M-HVAC-RETN"; colorAci = 6; return;
                case "exhaust":
                    layer = "M-HVAC-EXHS"; colorAci = 3; return;
                case "fresh_air":
                    layer = "M-HVAC-OA"; colorAci = 5; return;
                default:
                    layer = "M-HVAC-SUPP"; colorAci = 4; return;
            }
        }
    }
}
