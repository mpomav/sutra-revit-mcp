using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// General-purpose atomic AutoCAD drafting engine (setup_styles, draw_batch, list_blocks).
    /// Supports rich primitives (line, polyline with bulge/width, rectangle, arc, circle,
    /// hatch, mtext, callout, linear/aligned/radial dimensions, block inserts with attributes,
    /// and native AutoCAD tables) with dry_run=true default and post-commit handle re-read.
    /// </summary>
    internal static class CadDraftCommand
    {
        public static JObject Execute(JObject args)
        {
            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            if (op == "list_blocks")
            {
                Document readDoc = CadDraftingKit.RequireActiveDocument(args, true, out string readTitle);
                return ListBlocks(readDoc, readTitle, args);
            }

            bool dryRun = (bool?)args["dry_run"] ?? true;
            Document doc = CadDraftingKit.RequireActiveDocument(args, dryRun, out string activeTitle);
            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_draft", doc.Name, args);

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

        private static JObject ListBlocks(Document doc, string activeTitle, JObject args)
        {
            string filter = ((string)args["name_filter"] ?? "").Trim();
            int limit = Math.Max(1, Math.Min(500, (int?)args["limit"] ?? 100));
            var items = new JArray();

            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    if (items.Count >= limit) break;
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference) continue;
                    if (!string.IsNullOrEmpty(filter) &&
                        btr.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var attTags = new JArray();
                    int entityCount = 0;
                    foreach (ObjectId entId in btr)
                    {
                        entityCount++;
                        if (tr.GetObject(entId, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                            attTags.Add(ad.Tag);
                    }

                    items.Add(new JObject
                    {
                        ["name"] = btr.Name,
                        ["entity_count"] = entityCount,
                        ["attribute_tags"] = attTags
                    });
                }
                tr.Commit();
            }

            return new JObject
            {
                ["operation"] = "list_blocks",
                ["active_document"] = activeTitle,
                ["count"] = items.Count,
                ["blocks"] = items
            };
        }

        private static JObject Preview(string op, JObject args)
        {
            if (op == "setup_styles")
            {
                int layers = (args["layers"] as JArray)?.Count ?? 0;
                int textStyles = (args["text_styles"] as JArray)?.Count ?? 0;
                int dimStyles = (args["dim_styles"] as JArray)?.Count ?? 0;
                if (layers + textStyles + dimStyles == 0)
                    throw new ArgumentException("setup_styles requires at least one of 'layers', 'text_styles', or 'dim_styles'.");

                return new JObject
                {
                    ["operation"] = op,
                    ["would_process_layers"] = layers,
                    ["would_process_text_styles"] = textStyles,
                    ["would_process_dim_styles"] = dimStyles
                };
            }

            if (op == "draw_batch")
            {
                var items = args["items"] as JArray ?? new JArray();
                if (items.Count == 0)
                    throw new ArgumentException("draw_batch requires a non-empty 'items' array.");

                var kindCounts = new JObject();
                foreach (JObject item in items)
                {
                    string kind = ((string)item["kind"] ?? "unknown").Trim().ToLowerInvariant();
                    kindCounts[kind] = ((int?)kindCounts[kind] ?? 0) + 1;
                }

                return new JObject
                {
                    ["operation"] = op,
                    ["would_create_items"] = items.Count,
                    ["by_kind"] = kindCounts
                };
            }

            throw new ArgumentException("Unsupported cad_draft operation '" + op + "'.");
        }

        private static JObject CommitAndVerify(Document doc, string op, JObject args)
        {
            Database db = doc.Database;

            if (op == "setup_styles")
            {
                var layers = args["layers"] as JArray ?? new JArray();
                var textStyles = args["text_styles"] as JArray ?? new JArray();
                var dimStyles = args["dim_styles"] as JArray ?? new JArray();

                var layerNames = new List<string>();
                var textStyleNames = new List<string>();
                var dimStyleNames = new List<string>();

                using (doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (JObject l in layers)
                        {
                            string name = ((string)l["name"] ?? "").Trim();
                            if (string.IsNullOrEmpty(name)) continue;
                            CadDraftingKit.EnsureLayer(
                                db, tr, name,
                                (short?)(int?)l["color_aci"],
                                (string)l["linetype"],
                                (double?)l["lineweight_mm"],
                                (string)l["description"],
                                (bool?)l["is_plottable"]);
                            layerNames.Add(name);
                        }

                        foreach (JObject ts in textStyles)
                        {
                            string name = ((string)ts["name"] ?? "").Trim();
                            if (string.IsNullOrEmpty(name)) continue;
                            CadDraftingKit.EnsureTextStyle(
                                db, tr, name,
                                (string)ts["font"] ?? "Arial",
                                (double?)ts["height"] ?? 0.0);
                            textStyleNames.Add(name);
                        }

                        foreach (JObject ds in dimStyles)
                        {
                            string name = ((string)ds["name"] ?? "").Trim();
                            if (string.IsNullOrEmpty(name)) continue;
                            CadDraftingKit.EnsureDimStyle(
                                db, tr, name,
                                (double?)ds["text_height"] ?? 0.18,
                                (double?)ds["arrow_size"] ?? 0.15,
                                (double?)ds["overall_scale"] ?? 1.0,
                                (int?)ds["precision"] ?? 2);
                            dimStyleNames.Add(name);
                        }

                        tr.Commit();
                    }

                    int verifiedLayers = 0;
                    using (Transaction vtr = db.TransactionManager.StartTransaction())
                    {
                        var lt = (LayerTable)vtr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        foreach (string ln in layerNames)
                            if (lt.Has(ln)) verifiedLayers++;
                        vtr.Commit();
                    }

                    return new JObject
                    {
                        ["operation"] = op,
                        ["layers_processed"] = layerNames.Count,
                        ["verified_layers"] = verifiedLayers,
                        ["text_styles_processed"] = textStyleNames.Count,
                        ["dim_styles_processed"] = dimStyleNames.Count,
                        ["verification_state"] = verifiedLayers == layerNames.Count ? "verified" : "partial"
                    };
                }
            }

            if (op == "draw_batch")
            {
                var items = args["items"] as JArray ?? new JArray();
                string defaultDimStyleName = ((string)args["dim_style"] ?? "").Trim();
                var createdHandles = new List<string>();

                using (doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        ObjectId dimStyleId = string.IsNullOrEmpty(defaultDimStyleName)
                            ? db.Dimstyle
                            : CadDraftingKit.EnsureDimStyle(db, tr, defaultDimStyleName);

                        foreach (JObject item in items)
                        {
                            DrawBatchItem(db, tr, bt, ms, dimStyleId, item, createdHandles);
                        }
                        tr.Commit();
                    }

                    JArray verified = CadDraftingKit.VerifyHandles(db, createdHandles);
                    return new JObject
                    {
                        ["operation"] = op,
                        ["created_count"] = createdHandles.Count,
                        ["verified_count"] = verified.Count,
                        ["handles"] = verified,
                        ["verification_state"] = verified.Count == createdHandles.Count ? "verified" : "partial"
                    };
                }
            }

            throw new ArgumentException("Unsupported cad_draft operation '" + op + "'.");
        }

        private static void DrawBatchItem(
            Database db,
            Transaction tr,
            BlockTable bt,
            BlockTableRecord ms,
            ObjectId defaultDimStyleId,
            JObject spec,
            List<string> createdHandles)
        {
            string kind = ((string)spec["kind"] ?? "").Trim().ToLowerInvariant();
            string layer = ((string)spec["layer"] ?? "").Trim();
            short? colorAci = (short?)(int?)spec["color_aci"];

            switch (kind)
            {
                case "line":
                    createdHandles.Add(CadDraftingKit.AddLine(
                        ms, tr, db,
                        CadDraftingKit.ReadPoint3d(spec["start"], "start"),
                        CadDraftingKit.ReadPoint3d(spec["end"], "end"),
                        layer, colorAci));
                    return;

                case "rectangle":
                {
                    Point3d origin = CadDraftingKit.ReadPoint3d(spec["origin"], "origin");
                    double w = (double?)spec["width"] ?? 0;
                    double h = (double?)spec["height"] ?? 0;
                    if (w <= 0 || h <= 0) throw new ArgumentException("rectangle requires positive 'width' and 'height'.");
                    double cw = (double?)spec["constant_width"] ?? 0.0;
                    createdHandles.Add(CadDraftingKit.AddRectangle(ms, tr, db, new Point2d(origin.X, origin.Y), w, h, layer, cw));
                    return;
                }

                case "polyline":
                {
                    var pts = spec["points"] as JArray;
                    if (pts == null || pts.Count < 2)
                        throw new ArgumentException("polyline requires 'points' with at least 2 points.");
                    var bulges = spec["bulges"] as JArray;
                    double cw = (double?)spec["constant_width"] ?? 0.0;
                    var pl = new Polyline();
                    for (int i = 0; i < pts.Count; i++)
                    {
                        Point3d p = CadDraftingKit.ReadPoint3d(pts[i], "points[" + i + "]");
                        double b = (bulges != null && i < bulges.Count) ? (double)bulges[i] : 0.0;
                        pl.AddVertexAt(i, new Point2d(p.X, p.Y), b, cw, cw);
                    }
                    pl.Closed = (bool?)spec["closed"] ?? false;
                    createdHandles.Add(CadDraftingKit.Append(ms, tr, db, pl, layer, colorAci));
                    return;
                }

                case "circle":
                {
                    double r = (double?)spec["radius"] ?? 0;
                    if (r <= 0) throw new ArgumentException("circle requires positive 'radius'.");
                    var c = new Circle(CadDraftingKit.ReadPoint3d(spec["center"], "center"), Vector3d.ZAxis, r);
                    createdHandles.Add(CadDraftingKit.Append(ms, tr, db, c, layer, colorAci));
                    return;
                }

                case "arc":
                {
                    double r = (double?)spec["radius"] ?? 0;
                    if (r <= 0) throw new ArgumentException("arc requires positive 'radius'.");
                    double startDeg = (double?)spec["start_angle_deg"] ?? 0.0;
                    double endDeg = (double?)spec["end_angle_deg"] ?? 90.0;
                    var arc = new Arc(
                        CadDraftingKit.ReadPoint3d(spec["center"], "center"),
                        r,
                        startDeg * Math.PI / 180.0,
                        endDeg * Math.PI / 180.0);
                    createdHandles.Add(CadDraftingKit.Append(ms, tr, db, arc, layer, colorAci));
                    return;
                }

                case "hatch":
                {
                    var ptsArr = spec["boundary"] as JArray;
                    if (ptsArr == null || ptsArr.Count < 3)
                        throw new ArgumentException("hatch requires 'boundary' with at least 3 points.");
                    var poly = new List<Point2d>(ptsArr.Count);
                    for (int i = 0; i < ptsArr.Count; i++)
                    {
                        Point3d p = CadDraftingKit.ReadPoint3d(ptsArr[i], "boundary[" + i + "]");
                        poly.Add(new Point2d(p.X, p.Y));
                    }
                    string h = CadDraftingKit.AddPolygonHatch(
                        ms, tr, db, poly,
                        (string)spec["pattern"] ?? "SOLID",
                        (double?)spec["pattern_scale"] ?? 1.0,
                        (double?)spec["pattern_angle_deg"] ?? 0.0,
                        layer, colorAci);
                    if (!string.IsNullOrEmpty(h)) createdHandles.Add(h);
                    return;
                }

                case "mtext":
                {
                    Point3d pos = CadDraftingKit.ReadPoint3d(spec["position"], "position");
                    string text = (string)spec["text"] ?? "";
                    double h = (double?)spec["height"] ?? 0.2;
                    double w = (double?)spec["width"] ?? 0.0;
                    double rot = (double?)spec["rotation_deg"] ?? 0.0;
                    bool mask = (bool?)spec["background_mask"] ?? false;
                    AttachmentPoint att = ParseAttachment((string)spec["alignment"]);
                    createdHandles.Add(CadDraftingKit.AddMText(ms, tr, db, pos, text, h, w, layer, att, rot, mask));
                    return;
                }

                case "callout":
                {
                    Point3d arrow = CadDraftingKit.ReadPoint3d(spec["arrow"], "arrow");
                    Point3d landing = CadDraftingKit.ReadPoint3d(spec["landing"], "landing");
                    string text = (string)spec["text"] ?? "";
                    double h = (double?)spec["height"] ?? 0.18;
                    double shelf = (double?)spec["landing_length"] ?? Math.Max(h * 4.0, 0.5);
                    createdHandles.AddRange(CadDraftingKit.AddCallout(ms, tr, db, arrow, landing, text, h, shelf, layer));
                    return;
                }

                case "dim_linear":
                {
                    Point3d p1 = CadDraftingKit.ReadPoint3d(spec["start"], "start");
                    Point3d p2 = CadDraftingKit.ReadPoint3d(spec["end"], "end");
                    Point3d dimPt = CadDraftingKit.ReadPoint3d(spec["dim_line_point"], "dim_line_point");
                    double rot = (double?)spec["rotation_deg"] ?? 0.0;
                    createdHandles.Add(CadDraftingKit.AddLinearDim(ms, tr, db, p1, p2, dimPt, rot, layer, defaultDimStyleId, (string)spec["text_override"]));
                    return;
                }

                case "dim_aligned":
                {
                    Point3d p1 = CadDraftingKit.ReadPoint3d(spec["start"], "start");
                    Point3d p2 = CadDraftingKit.ReadPoint3d(spec["end"], "end");
                    Point3d dimPt = CadDraftingKit.ReadPoint3d(spec["dim_line_point"], "dim_line_point");
                    createdHandles.Add(CadDraftingKit.AddAlignedDim(ms, tr, db, p1, p2, dimPt, layer, defaultDimStyleId, (string)spec["text_override"]));
                    return;
                }

                case "dim_radial":
                {
                    Point3d center = CadDraftingKit.ReadPoint3d(spec["center"], "center");
                    Point3d chord = CadDraftingKit.ReadPoint3d(spec["chord_point"], "chord_point");
                    double leaderLen = (double?)spec["leader_length"] ?? 0.5;
                    var dim = new RadialDimension(center, chord, leaderLen, (string)spec["text_override"] ?? "", defaultDimStyleId);
                    createdHandles.Add(CadDraftingKit.Append(ms, tr, db, dim, layer, colorAci));
                    return;
                }

                case "insert_block":
                {
                    string blockName = ((string)spec["block_name"] ?? "").Trim();
                    if (string.IsNullOrEmpty(blockName) || !bt.Has(blockName))
                        throw new ArgumentException("Block '" + blockName + "' was not found in the active drawing.");
                    Point3d pos = CadDraftingKit.ReadPoint3d(spec["position"], "position");
                    double sx = (double?)spec["scale_x"] ?? (double?)spec["scale"] ?? 1.0;
                    double sy = (double?)spec["scale_y"] ?? (double?)spec["scale"] ?? 1.0;
                    double sz = (double?)spec["scale_z"] ?? (double?)spec["scale"] ?? 1.0;
                    double rotDeg = (double?)spec["rotation_deg"] ?? 0.0;

                    var br = new BlockReference(pos, bt[blockName])
                    {
                        ScaleFactors = new Scale3d(sx, sy, sz),
                        Rotation = rotDeg * Math.PI / 180.0
                    };
                    createdHandles.Add(CadDraftingKit.Append(ms, tr, db, br, layer, colorAci));

                    var attrs = spec["attributes"] as JObject;
                    var btr = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);
                    if (btr.HasAttributeDefinitions)
                    {
                        foreach (ObjectId id in btr)
                        {
                            if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                            {
                                var ar = new AttributeReference();
                                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                                ar.Position = ad.Position.TransformBy(br.BlockTransform);
                                if (attrs != null && attrs[ad.Tag] != null)
                                    ar.TextString = (string)attrs[ad.Tag];
                                br.AttributeCollection.AppendAttribute(ar);
                                tr.AddNewlyCreatedDBObject(ar, true);
                            }
                        }
                    }
                    return;
                }

                case "table":
                {
                    Point3d pos = CadDraftingKit.ReadPoint3d(spec["position"], "position");
                    string title = (string)spec["title"] ?? "";
                    var headers = ReadStringList(spec["headers"] as JArray);
                    var rows = new List<IList<string>>();
                    if (spec["rows"] is JArray rowsArr)
                    {
                        foreach (JToken rTok in rowsArr)
                            rows.Add(ReadStringList(rTok as JArray));
                    }
                    var colWidths = new List<double>();
                    if (spec["col_widths"] is JArray cwArr)
                    {
                        foreach (JToken wTok in cwArr) colWidths.Add((double)wTok);
                    }
                    double th = (double?)spec["height"] ?? 0.18;
                    createdHandles.Add(CadDraftingKit.AddTable(ms, tr, db, pos, title, headers, rows, colWidths, th, layer));
                    return;
                }

                default:
                    throw new ArgumentException("Unsupported draw_batch item kind '" + kind + "'.");
            }
        }

        private static AttachmentPoint ParseAttachment(string align)
        {
            switch (((align ?? "").Trim().ToLowerInvariant()))
            {
                case "top_center": return AttachmentPoint.TopCenter;
                case "top_right": return AttachmentPoint.TopRight;
                case "middle_left": return AttachmentPoint.MiddleLeft;
                case "middle_center": return AttachmentPoint.MiddleCenter;
                case "middle_right": return AttachmentPoint.MiddleRight;
                case "bottom_left": return AttachmentPoint.BottomLeft;
                case "bottom_center": return AttachmentPoint.BottomCenter;
                case "bottom_right": return AttachmentPoint.BottomRight;
                default: return AttachmentPoint.TopLeft;
            }
        }

        private static List<string> ReadStringList(JArray arr)
        {
            var list = new List<string>();
            if (arr == null) return list;
            foreach (JToken tok in arr) list.Add(tok?.ToString() ?? "");
            return list;
        }
    }
}
