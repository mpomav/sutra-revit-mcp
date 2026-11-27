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
    /// Parametric architectural floor-plan drafting tool (draw_grid, draw_walls,
    /// draw_openings, draw_room_tags) with dry_run=true default, confirmation_token,
    /// Transaction commit, and post-commit Handle re-read.
    /// Organisation-neutral: layer names, thicknesses, hatch patterns, and criteria
    /// citations arrive as call-time arguments.
    /// </summary>
    internal static class CadArchPlanCommand
    {
        public static JObject Execute(JObject args)
        {
            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            bool dryRun = (bool?)args["dry_run"] ?? true;
            Document doc = CadDraftingKit.RequireActiveDocument(args, dryRun, out string activeTitle);
            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_arch_plan", doc.Name, args);

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
                case "draw_grid":
                {
                    var xSpacings = args["x_spacings"] as JArray ?? new JArray();
                    var ySpacings = args["y_spacings"] as JArray ?? new JArray();
                    if (xSpacings.Count == 0 && ySpacings.Count == 0)
                        throw new ArgumentException("draw_grid requires 'x_spacings' and/or 'y_spacings'.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["vertical_axes"] = xSpacings.Count + 1,
                        ["horizontal_axes"] = ySpacings.Count + 1,
                        ["include_dimensions"] = (bool?)args["include_dimensions"] ?? true
                    };
                }
                case "draw_walls":
                {
                    var walls = args["walls"] as JArray ?? new JArray();
                    if (walls.Count == 0)
                        throw new ArgumentException("draw_walls requires a non-empty 'walls' array.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["wall_segments"] = walls.Count,
                        ["default_thickness"] = (double?)args["default_thickness"] ?? 0.15,
                        ["include_hatch"] = !string.IsNullOrWhiteSpace((string)args["hatch_pattern"])
                    };
                }
                case "draw_openings":
                {
                    var openings = args["openings"] as JArray ?? new JArray();
                    if (openings.Count == 0)
                        throw new ArgumentException("draw_openings requires a non-empty 'openings' array.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["opening_count"] = openings.Count
                    };
                }
                case "draw_room_tags":
                {
                    var rooms = args["rooms"] as JArray ?? new JArray();
                    if (rooms.Count == 0)
                        throw new ArgumentException("draw_room_tags requires a non-empty 'rooms' array.");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["room_count"] = rooms.Count,
                        ["include_schedule_table"] = (bool?)args["include_schedule_table"] ?? false
                    };
                }
                default:
                    throw new ArgumentException("Unsupported cad_arch_plan operation '" + op + "'.");
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
                        case "draw_grid":
                            summary = DrawGrid(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_walls":
                            summary = DrawWalls(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_openings":
                            summary = DrawOpenings(db, tr, ms, args, createdHandles);
                            break;
                        case "draw_room_tags":
                            summary = DrawRoomTags(db, tr, ms, args, createdHandles);
                            break;
                        default:
                            throw new ArgumentException("Unsupported cad_arch_plan operation '" + op + "'.");
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

        private static JObject DrawGrid(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            Point3d origin = CadDraftingKit.ReadPoint3dOrDefault(args["origin"], Point3d.Origin);
            var xSpacings = ReadDoubleList(args["x_spacings"] as JArray);
            var ySpacings = ReadDoubleList(args["y_spacings"] as JArray);
            var xLabels = ReadStringList(args["x_labels"] as JArray);
            var yLabels = ReadStringList(args["y_labels"] as JArray);

            double overhang = (double?)args["overhang"] ?? 1.5;
            double bubbleR = (double?)args["bubble_radius"] ?? 0.35;
            double textH = (double?)args["text_height"] ?? (bubbleR * 0.75);
            bool addDims = (bool?)args["include_dimensions"] ?? true;

            string gridLayer = ((string)args["grid_layer"] ?? "A-GRID").Trim();
            string bubbleLayer = ((string)args["bubble_layer"] ?? "A-GRID-IDEN").Trim();
            string dimLayer = ((string)args["dim_layer"] ?? "A-ANNO-DIMS").Trim();

            CadDraftingKit.EnsureLayer(db, tr, gridLayer, 8, "CENTER", 0.13, "Structural/Architectural Grid Lines");
            CadDraftingKit.EnsureLayer(db, tr, bubbleLayer, 7, "CONTINUOUS", 0.25, "Grid Bubbles & Labels");
            CadDraftingKit.EnsureLayer(db, tr, dimLayer, 2, "CONTINUOUS", 0.15, "Dimensions");
            ObjectId dimStyleId = CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-ARCH", textH * 0.85, textH * 0.6, 1.0, 2);

            // Compute cumulative coordinates
            var xs = new List<double> { origin.X };
            double cx = origin.X;
            foreach (double dx in xSpacings) { cx += dx; xs.Add(cx); }

            var ys = new List<double> { origin.Y };
            double cy = origin.Y;
            foreach (double dy in ySpacings) { cy += dy; ys.Add(cy); }

            double minX = xs[0], maxX = xs[xs.Count - 1];
            double minY = ys[0], maxY = ys[ys.Count - 1];

            // Vertical axes (along Y, positioned at each X)
            for (int i = 0; i < xs.Count; i++)
            {
                string label = i < xLabels.Count && !string.IsNullOrWhiteSpace(xLabels[i])
                    ? xLabels[i]
                    : (i + 1).ToString(CultureInfo.InvariantCulture);

                Point3d pBot = new Point3d(xs[i], minY - overhang, origin.Z);
                Point3d pTop = new Point3d(xs[i], maxY + overhang, origin.Z);
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, pBot, pTop, gridLayer));

                Point3d bCenter = new Point3d(xs[i], pTop.Y + bubbleR, origin.Z);
                handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(bCenter, Vector3d.ZAxis, bubbleR), bubbleLayer));
                handles.Add(CadDraftingKit.AddMText(ms, tr, db, bCenter, label, textH, 0, bubbleLayer, AttachmentPoint.MiddleCenter));
            }

            // Horizontal axes (along X, positioned at each Y)
            for (int j = 0; j < ys.Count; j++)
            {
                string label = j < yLabels.Count && !string.IsNullOrWhiteSpace(yLabels[j])
                    ? yLabels[j]
                    : IndexToLetters(j);

                Point3d pLeft = new Point3d(minX - overhang, ys[j], origin.Z);
                Point3d pRight = new Point3d(maxX + overhang, ys[j], origin.Z);
                handles.Add(CadDraftingKit.AddLine(ms, tr, db, pLeft, pRight, gridLayer));

                Point3d bCenter = new Point3d(pLeft.X - bubbleR, ys[j], origin.Z);
                handles.Add(CadDraftingKit.Append(ms, tr, db, new Circle(bCenter, Vector3d.ZAxis, bubbleR), bubbleLayer));
                handles.Add(CadDraftingKit.AddMText(ms, tr, db, bCenter, label, textH, 0, bubbleLayer, AttachmentPoint.MiddleCenter));
            }

            // Partial and total dimensions
            if (addDims && xs.Count >= 2)
            {
                double dimY1 = maxY + overhang * 0.55;
                for (int i = 0; i < xs.Count - 1; i++)
                {
                    handles.Add(CadDraftingKit.AddLinearDim(
                        ms, tr, db,
                        new Point3d(xs[i], maxY, origin.Z),
                        new Point3d(xs[i + 1], maxY, origin.Z),
                        new Point3d((xs[i] + xs[i + 1]) * 0.5, dimY1, origin.Z),
                        0.0, dimLayer, dimStyleId));
                }
                if (xs.Count > 2)
                {
                    handles.Add(CadDraftingKit.AddLinearDim(
                        ms, tr, db,
                        new Point3d(minX, maxY, origin.Z),
                        new Point3d(maxX, maxY, origin.Z),
                        new Point3d((minX + maxX) * 0.5, dimY1 + textH * 2.5, origin.Z),
                        0.0, dimLayer, dimStyleId));
                }
            }

            if (addDims && ys.Count >= 2)
            {
                double dimX1 = minX - overhang * 0.55;
                for (int j = 0; j < ys.Count - 1; j++)
                {
                    handles.Add(CadDraftingKit.AddLinearDim(
                        ms, tr, db,
                        new Point3d(minX, ys[j], origin.Z),
                        new Point3d(minX, ys[j + 1], origin.Z),
                        new Point3d(dimX1, (ys[j] + ys[j + 1]) * 0.5, origin.Z),
                        90.0, dimLayer, dimStyleId));
                }
                if (ys.Count > 2)
                {
                    handles.Add(CadDraftingKit.AddLinearDim(
                        ms, tr, db,
                        new Point3d(minX, minY, origin.Z),
                        new Point3d(minX, maxY, origin.Z),
                        new Point3d(dimX1 - textH * 2.5, (minY + maxY) * 0.5, origin.Z),
                        90.0, dimLayer, dimStyleId));
                }
            }

            return new JObject
            {
                ["vertical_axes"] = xs.Count,
                ["horizontal_axes"] = ys.Count,
                ["total_width"] = Math.Round(maxX - minX, 4),
                ["total_depth"] = Math.Round(maxY - minY, 4)
            };
        }

        private static JObject DrawWalls(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            var walls = args["walls"] as JArray ?? new JArray();
            double defaultThick = (double?)args["default_thickness"] ?? 0.15;
            string wallLayer = ((string)args["wall_layer"] ?? "A-WALL").Trim();
            string hatchLayer = ((string)args["hatch_layer"] ?? "A-WALL-PATT").Trim();
            string dimLayer = ((string)args["dim_layer"] ?? "A-ANNO-DIMS").Trim();
            string defaultHatch = ((string)args["hatch_pattern"] ?? "").Trim();
            double hatchScale = (double?)args["hatch_scale"] ?? 0.05;
            bool addDims = (bool?)args["include_dimensions"] ?? false;

            CadDraftingKit.EnsureLayer(db, tr, wallLayer, 7, "CONTINUOUS", 0.35, "Architectural Walls");
            if (!string.IsNullOrEmpty(defaultHatch))
                CadDraftingKit.EnsureLayer(db, tr, hatchLayer, 8, "CONTINUOUS", 0.09, "Wall Hatch");
            ObjectId dimStyleId = addDims ? CadDraftingKit.EnsureDimStyle(db, tr, "SUTRA-ARCH") : db.Dimstyle;

            int drawn = 0;
            double totalLength = 0.0;

            foreach (JObject w in walls)
            {
                Point3d p0 = CadDraftingKit.ReadPoint3d(w["start"], "start");
                Point3d p1 = CadDraftingKit.ReadPoint3d(w["end"], "end");
                Vector3d dir = p1 - p0;
                double len = dir.Length;
                if (len < 1e-6) continue;

                double t = (double?)w["thickness"] ?? defaultThick;
                string just = ((string)w["justification"] ?? "center").Trim().ToLowerInvariant();
                string wLayer = ((string)w["layer"] ?? wallLayer).Trim();
                string hPat = ((string)w["hatch_pattern"] ?? defaultHatch).Trim();

                Vector3d unit = dir / len;
                Vector3d leftNormal = new Vector3d(-unit.Y, unit.X, 0.0);

                double leftOffset, rightOffset;
                if (just == "left") { leftOffset = 0.0; rightOffset = t; }
                else if (just == "right") { leftOffset = t; rightOffset = 0.0; }
                else { leftOffset = t * 0.5; rightOffset = t * 0.5; }

                Point3d c0 = p0 + leftNormal * leftOffset;
                Point3d c1 = p1 + leftNormal * leftOffset;
                Point3d c2 = p1 - leftNormal * rightOffset;
                Point3d c3 = p0 - leftNormal * rightOffset;

                var pl = new Polyline();
                pl.AddVertexAt(0, new Point2d(c0.X, c0.Y), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(c1.X, c1.Y), 0, 0, 0);
                pl.AddVertexAt(2, new Point2d(c2.X, c2.Y), 0, 0, 0);
                pl.AddVertexAt(3, new Point2d(c3.X, c3.Y), 0, 0, 0);
                pl.Closed = true;
                handles.Add(CadDraftingKit.Append(ms, tr, db, pl, wLayer));

                if (!string.IsNullOrEmpty(hPat))
                {
                    var poly = new List<Point2d>
                    {
                        new Point2d(c0.X, c0.Y),
                        new Point2d(c1.X, c1.Y),
                        new Point2d(c2.X, c2.Y),
                        new Point2d(c3.X, c3.Y)
                    };
                    string hh = CadDraftingKit.AddPolygonHatch(ms, tr, db, poly, hPat, hatchScale, 0.0, hatchLayer);
                    if (!string.IsNullOrEmpty(hh)) handles.Add(hh);
                }

                if (addDims)
                {
                    Point3d dimPt = (p0 + (p1 - p0) * 0.5) + leftNormal * (leftOffset + Math.Max(t * 2.0, 0.45));
                    handles.Add(CadDraftingKit.AddAlignedDim(ms, tr, db, c0, c1, dimPt, dimLayer, dimStyleId));
                }

                drawn++;
                totalLength += len;
            }

            return new JObject
            {
                ["walls_drawn"] = drawn,
                ["total_wall_length"] = Math.Round(totalLength, 4)
            };
        }

        private static JObject DrawOpenings(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            var openings = args["openings"] as JArray ?? new JArray();
            string doorLayer = ((string)args["door_layer"] ?? "A-DOOR").Trim();
            string windowLayer = ((string)args["window_layer"] ?? "A-GLAZ").Trim();
            string tagLayer = ((string)args["tag_layer"] ?? "A-ANNO-TAGS").Trim();
            double textH = (double?)args["text_height"] ?? 0.16;

            CadDraftingKit.EnsureLayer(db, tr, doorLayer, 3, "CONTINUOUS", 0.20, "Doors");
            CadDraftingKit.EnsureLayer(db, tr, windowLayer, 4, "CONTINUOUS", 0.18, "Windows & Glazing");
            CadDraftingKit.EnsureLayer(db, tr, tagLayer, 2, "CONTINUOUS", 0.15, "Opening Tags");

            int doors = 0, windows = 0;

            foreach (JObject op in openings)
            {
                string kind = ((string)op["kind"] ?? "door").Trim().ToLowerInvariant();
                Point3d pos = CadDraftingKit.ReadPoint3d(op["position"], "position");
                double width = (double?)op["width"] ?? (kind == "door" ? 0.90 : 1.50);
                double wallThick = (double?)op["wall_thickness"] ?? 0.15;
                double rotDeg = (double?)op["rotation_deg"] ?? 0.0;
                double rotRad = rotDeg * Math.PI / 180.0;
                double swingSign = ((string)op["swing"] ?? "left").Trim().ToLowerInvariant() == "right" ? -1.0 : 1.0;
                string tag = ((string)op["tag"] ?? "").Trim();

                Vector3d along = new Vector3d(Math.Cos(rotRad), Math.Sin(rotRad), 0.0);
                Vector3d normal = new Vector3d(-along.Y, along.X, 0.0) * swingSign;
                double halfT = wallThick * 0.5;

                if (kind == "door")
                {
                    double leafThick = Math.Min(0.05, width * 0.06);
                    // Jambs
                    Point3d hinge = pos + normal * halfT;
                    Point3d latch = pos + along * width + normal * halfT;
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos - normal * halfT, hinge, doorLayer));
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos + along * width - normal * halfT, latch, doorLayer));

                    // Open leaf (90 deg perpendicular to wall)
                    Point3d leafTip = hinge + normal * width;
                    var leaf = new Polyline();
                    leaf.AddVertexAt(0, new Point2d(hinge.X, hinge.Y), 0, 0, 0);
                    leaf.AddVertexAt(1, new Point2d(leafTip.X, leafTip.Y), 0, 0, 0);
                    leaf.AddVertexAt(2, new Point2d((leafTip + along * leafThick).X, (leafTip + along * leafThick).Y), 0, 0, 0);
                    leaf.AddVertexAt(3, new Point2d((hinge + along * leafThick).X, (hinge + along * leafThick).Y), 0, 0, 0);
                    leaf.Closed = true;
                    handles.Add(CadDraftingKit.Append(ms, tr, db, leaf, doorLayer));

                    // Swing arc (90 degrees from closed position to open position)
                    double aStart = swingSign > 0 ? rotRad : rotRad - Math.PI * 0.5;
                    double aEnd = swingSign > 0 ? rotRad + Math.PI * 0.5 : rotRad;
                    var arc = new Arc(hinge, width, aStart, aEnd);
                    handles.Add(CadDraftingKit.Append(ms, tr, db, arc, doorLayer));

                    if (!string.IsNullOrEmpty(tag))
                    {
                        Point3d tagPt = hinge + along * (width * 0.5) + normal * (width * 0.45);
                        handles.Add(CadDraftingKit.AddMText(ms, tr, db, tagPt, tag, textH, 0, tagLayer, AttachmentPoint.MiddleCenter));
                    }
                    doors++;
                }
                else
                {
                    // Window: outer sill lines + central glass double line + end jambs
                    Point3d p0a = pos - normal * halfT;
                    Point3d p1a = pos + along * width - normal * halfT;
                    Point3d p0b = pos + normal * halfT;
                    Point3d p1b = pos + along * width + normal * halfT;

                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, p0a, p1a, windowLayer));
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, p0b, p1b, windowLayer));
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, p0a, p0b, windowLayer));
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, p1a, p1b, windowLayer));

                    // Glazing double line centered
                    double glassOff = Math.Min(wallThick * 0.12, 0.02);
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos - normal * glassOff, pos + along * width - normal * glassOff, windowLayer));
                    handles.Add(CadDraftingKit.AddLine(ms, tr, db, pos + normal * glassOff, pos + along * width + normal * glassOff, windowLayer));

                    if (!string.IsNullOrEmpty(tag))
                    {
                        Point3d tagPt = pos + along * (width * 0.5) + normal * (halfT + textH * 1.4);
                        handles.Add(CadDraftingKit.AddMText(ms, tr, db, tagPt, tag, textH, 0, tagLayer, AttachmentPoint.MiddleCenter));
                    }
                    windows++;
                }
            }

            return new JObject
            {
                ["doors_drawn"] = doors,
                ["windows_drawn"] = windows
            };
        }

        private static JObject DrawRoomTags(Database db, Transaction tr, BlockTableRecord ms, JObject args, List<string> handles)
        {
            var rooms = args["rooms"] as JArray ?? new JArray();
            string tagLayer = ((string)args["tag_layer"] ?? "A-ANNO-ROOM").Trim();
            double textH = (double?)args["text_height"] ?? 0.22;
            bool includeTable = (bool?)args["include_schedule_table"] ?? false;

            CadDraftingKit.EnsureLayer(db, tr, tagLayer, 7, "CONTINUOUS", 0.18, "Room Tags & Schedules");

            var tableRows = new List<IList<string>>();
            double totalArea = 0.0;

            foreach (JObject rm in rooms)
            {
                string name = ((string)rm["name"] ?? "AMBIENTE").Trim();
                string level = ((string)rm["level_text"] ?? "").Trim();
                string finish = ((string)rm["finish"] ?? "").Trim();
                double? areaVal = (double?)rm["area_m2"];

                Point3d pos;
                if (rm["polygon"] is JArray polyArr && polyArr.Count >= 3)
                {
                    var pts = new List<Point2d>(polyArr.Count);
                    for (int i = 0; i < polyArr.Count; i++)
                    {
                        Point3d p = CadDraftingKit.ReadPoint3d(polyArr[i], "polygon[" + i + "]");
                        pts.Add(new Point2d(p.X, p.Y));
                    }
                    double calcArea = ComputePolygonArea(pts, out Point2d centroid);
                    if (!areaVal.HasValue) areaVal = calcArea;
                    pos = rm["position"] != null
                        ? CadDraftingKit.ReadPoint3d(rm["position"], "position")
                        : new Point3d(centroid.X, centroid.Y, 0.0);
                }
                else
                {
                    pos = CadDraftingKit.ReadPoint3d(rm["position"], "position");
                }

                double area = areaVal ?? 0.0;
                totalArea += area;

                var lines = new List<string> { name.ToUpperInvariant() };
                if (area > 0) lines.Add(string.Format(CultureInfo.InvariantCulture, "A = {0:0.00} m2", area));
                if (!string.IsNullOrEmpty(level)) lines.Add(level);

                string mtextBody = string.Join("\\P", lines);
                handles.Add(CadDraftingKit.AddMText(ms, tr, db, pos, mtextBody, textH, 0, tagLayer, AttachmentPoint.MiddleCenter, 0, true));

                tableRows.Add(new List<string>
                {
                    name,
                    area > 0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.00}", area) : "-",
                    string.IsNullOrEmpty(level) ? "-" : level,
                    string.IsNullOrEmpty(finish) ? "-" : finish
                });
            }

            if (includeTable && tableRows.Count > 0)
            {
                Point3d tblPos = CadDraftingKit.ReadPoint3dOrDefault(args["table_position"], new Point3d(0, -3.0, 0));
                string title = ((string)args["table_title"] ?? "CUADRO DE AMBIENTES").Trim();
                handles.Add(CadDraftingKit.AddTable(
                    ms, tr, db, tblPos, title,
                    new[] { "AMBIENTE", "AREA (m2)", "NIVEL", "ACABADO" },
                    tableRows,
                    new[] { 4.5, 2.2, 2.2, 4.0 },
                    textH * 0.85,
                    tagLayer));
            }

            return new JObject
            {
                ["rooms_tagged"] = rooms.Count,
                ["total_area_m2"] = Math.Round(totalArea, 2),
                ["schedule_table_created"] = includeTable && tableRows.Count > 0
            };
        }

        private static double ComputePolygonArea(IList<Point2d> pts, out Point2d centroid)
        {
            double twiceArea = 0.0;
            double cx = 0.0, cy = 0.0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d p0 = pts[i];
                Point2d p1 = pts[(i + 1) % n];
                double cross = p0.X * p1.Y - p1.X * p0.Y;
                twiceArea += cross;
                cx += (p0.X + p1.X) * cross;
                cy += (p0.Y + p1.Y) * cross;
            }
            if (Math.Abs(twiceArea) < 1e-9)
            {
                double sx = 0, sy = 0;
                foreach (var p in pts) { sx += p.X; sy += p.Y; }
                centroid = new Point2d(sx / n, sy / n);
                return 0.0;
            }
            centroid = new Point2d(cx / (3.0 * twiceArea), cy / (3.0 * twiceArea));
            return Math.Abs(twiceArea) * 0.5;
        }

        private static string IndexToLetters(int idx)
        {
            string res = "";
            int n = idx;
            while (n >= 0)
            {
                res = (char)('A' + (n % 26)) + res;
                n = (n / 26) - 1;
            }
            return res;
        }

        private static List<double> ReadDoubleList(JArray arr)
        {
            var list = new List<double>();
            if (arr == null) return list;
            foreach (JToken t in arr) list.Add((double)t);
            return list;
        }

        private static List<string> ReadStringList(JArray arr)
        {
            var list = new List<string>();
            if (arr == null) return list;
            foreach (JToken t in arr) list.Add(t?.ToString() ?? "");
            return list;
        }
    }
}
