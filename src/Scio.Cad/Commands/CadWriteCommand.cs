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

namespace Scio.Cad.Commands
{
    /// <summary>
    /// Transactional AutoCAD / Civil 3D write command implementing Sutra's exact
    /// rehearse (dry_run=true) -> confirmation_token -> commit -> post-commit Handle re-read contract.
    /// </summary>
    internal static class CadWriteCommand
    {
        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            string activeTitle = Path.GetFileName(doc.Name);
            string targetDoc = ((string)args["target_document"] ?? "").Trim();
            bool dryRun = (bool?)args["dry_run"] ?? true;

            if (!dryRun && !string.IsNullOrEmpty(targetDoc) &&
                !string.Equals(targetDoc, activeTitle, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetDoc, doc.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Active drawing is '" + activeTitle + "', not requested target_document '" + targetDoc + "'. Nothing was written.");
            }

            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_write", doc.Name, args);

            if (dryRun)
            {
                string token = CadConfirmationStore.Issue(fingerprint);
                JObject preview = Preview(doc, op, args);
                preview["dry_run"] = true;
                preview["verification_state"] = "rehearsed";
                preview["active_document"] = activeTitle;
                preview["confirmation_token"] = token;
                return preview;
            }

            string tokenError = CadConfirmationStore.Consume((string)args["confirmation_token"], fingerprint);
            if (tokenError != null)
                throw new InvalidOperationException(tokenError);

            JObject committed = CommitAndVerify(doc, op, args);
            committed["dry_run"] = false;
            committed["active_document"] = activeTitle;
            return committed;
        }

        private static JObject Preview(Document doc, string op, JObject args)
        {
            if (op == "ensure_layers")
            {
                var layers = args["layers"] as JArray ?? new JArray();
                if (layers.Count == 0) throw new ArgumentException("ensure_layers requires a non-empty 'layers' array.");
                return new JObject
                {
                    ["operation"] = op,
                    ["would_process_layers"] = layers.Count
                };
            }
            if (op == "create_entities")
            {
                var entities = args["entities"] as JArray ?? new JArray();
                if (entities.Count == 0) throw new ArgumentException("create_entities requires a non-empty 'entities' array.");
                return new JObject
                {
                    ["operation"] = op,
                    ["would_create_entities"] = entities.Count
                };
            }
            if (op == "delete_entities")
            {
                var handles = args["handles"] as JArray ?? new JArray();
                if (handles.Count == 0) throw new ArgumentException("delete_entities requires a non-empty 'handles' array.");
                int existing = 0;
                Database db = doc.Database;
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (JToken hTok in handles)
                    {
                        if (TryResolveHandle(db, (string)hTok, out ObjectId oid) && !oid.IsErased) existing++;
                    }
                    tr.Commit();
                }
                return new JObject
                {
                    ["operation"] = op,
                    ["requested_handles"] = handles.Count,
                    ["would_delete_entities"] = existing
                };
            }
            throw new ArgumentException("Unsupported operation '" + op + "'.");
        }

        private static JObject CommitAndVerify(Document doc, string op, JObject args)
        {
            Database db = doc.Database;

            if (op == "ensure_layers")
            {
                var layers = args["layers"] as JArray ?? new JArray();
                var layerNames = new List<string>();
                int created = 0, updated = 0;

                using (doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        foreach (JObject spec in layers)
                        {
                            string name = ((string)spec["name"] ?? "").Trim();
                            if (string.IsNullOrEmpty(name)) continue;
                            layerNames.Add(name);

                            LayerTableRecord ltr;
                            if (lt.Has(name))
                            {
                                ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                                updated++;
                            }
                            else
                            {
                                lt.UpgradeOpen();
                                ltr = new LayerTableRecord { Name = name };
                                lt.Add(ltr);
                                tr.AddNewlyCreatedDBObject(ltr, true);
                                created++;
                            }

                            if (spec["color_aci"] != null)
                                ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)(int)spec["color_aci"]);
                            if (spec["is_off"] != null) ltr.IsOff = (bool)spec["is_off"];
                            if (spec["is_frozen"] != null) ltr.IsFrozen = (bool)spec["is_frozen"];
                            if (spec["is_locked"] != null) ltr.IsLocked = (bool)spec["is_locked"];
                        }
                        tr.Commit();
                    }

                    // Post-commit re-read proof
                    int verifiedCount = 0;
                    using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                    {
                        var lt = (LayerTable)verifyTr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        foreach (string name in layerNames)
                            if (lt.Has(name)) verifiedCount++;
                        verifyTr.Commit();
                    }

                    return new JObject
                    {
                        ["operation"] = op,
                        ["created"] = created,
                        ["updated"] = updated,
                        ["verified_layers"] = verifiedCount,
                        ["verification_state"] = verifiedCount == layerNames.Count ? "verified" : "partial"
                    };
                }
            }

            if (op == "create_entities")
            {
                var entities = args["entities"] as JArray ?? new JArray();
                var createdHandles = new List<string>();

                using (doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                        foreach (JObject spec in entities)
                        {
                            Entity ent = BuildEntity(spec);
                            string layer = ((string)spec["layer"] ?? "").Trim();
                            if (!string.IsNullOrEmpty(layer))
                            {
                                if (!lt.Has(layer))
                                {
                                    lt.UpgradeOpen();
                                    var ltr = new LayerTableRecord { Name = layer };
                                    lt.Add(ltr);
                                    tr.AddNewlyCreatedDBObject(ltr, true);
                                }
                                ent.Layer = layer;
                            }
                            ms.AppendEntity(ent);
                            tr.AddNewlyCreatedDBObject(ent, true);
                            createdHandles.Add(ent.Handle.ToString());
                        }
                        tr.Commit();
                    }

                    // Post-commit re-read proof by Handle
                    var verifiedHandles = new JArray();
                    using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                    {
                        foreach (string h in createdHandles)
                        {
                            if (TryResolveHandle(db, h, out ObjectId oid) && !oid.IsErased)
                                verifiedHandles.Add(h);
                        }
                        verifyTr.Commit();
                    }

                    return new JObject
                    {
                        ["operation"] = op,
                        ["created_count"] = createdHandles.Count,
                        ["verified_count"] = verifiedHandles.Count,
                        ["handles"] = verifiedHandles,
                        ["verification_state"] = verifiedHandles.Count == createdHandles.Count ? "verified" : "partial"
                    };
                }
            }

            if (op == "delete_entities")
            {
                var handles = args["handles"] as JArray ?? new JArray();
                var targetHandles = new List<string>();
                int deleted = 0;

                using (doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (JToken hTok in handles)
                        {
                            string h = ((string)hTok ?? "").Trim();
                            if (string.IsNullOrEmpty(h)) continue;
                            targetHandles.Add(h);
                            if (TryResolveHandle(db, h, out ObjectId oid) && !oid.IsErased)
                            {
                                DBObject obj = tr.GetObject(oid, OpenMode.ForWrite);
                                obj.Erase(true);
                                deleted++;
                            }
                        }
                        tr.Commit();
                    }

                    // Post-commit re-read proof: every deleted handle is now erased
                    int verifiedErased = 0;
                    using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                    {
                        foreach (string h in targetHandles)
                        {
                            if (!TryResolveHandle(db, h, out ObjectId oid) || oid.IsErased)
                                verifiedErased++;
                        }
                        verifyTr.Commit();
                    }

                    return new JObject
                    {
                        ["operation"] = op,
                        ["deleted_count"] = deleted,
                        ["verified_erased_count"] = verifiedErased,
                        ["verification_state"] = verifiedErased == targetHandles.Count ? "verified" : "partial"
                    };
                }
            }

            throw new ArgumentException("Unsupported operation '" + op + "'.");
        }

        private static Entity BuildEntity(JObject spec)
        {
            string kind = ((string)spec["kind"] ?? "").Trim().ToLowerInvariant();
            switch (kind)
            {
                case "line":
                    return new Line(ReadPoint3d(spec["start"], "start"), ReadPoint3d(spec["end"], "end"));

                case "polyline":
                {
                    var pts = spec["points"] as JArray;
                    if (pts == null || pts.Count < 2)
                        throw new ArgumentException("polyline requires 'points' with at least 2 points.");
                    var pl = new Polyline();
                    for (int i = 0; i < pts.Count; i++)
                    {
                        Point3d p = ReadPoint3d(pts[i], "points[" + i + "]");
                        pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                    }
                    pl.Closed = (bool?)spec["closed"] ?? false;
                    return pl;
                }

                case "circle":
                {
                    double r = (double?)spec["radius"] ?? 0;
                    if (r <= 0) throw new ArgumentException("circle requires positive 'radius'.");
                    return new Circle(ReadPoint3d(spec["center"], "center"), Vector3d.ZAxis, r);
                }

                case "text":
                {
                    string text = (string)spec["text"] ?? "";
                    double h = (double?)spec["height"] ?? 2.5;
                    return new DBText
                    {
                        Position = ReadPoint3d(spec["position"], "position"),
                        TextString = text,
                        Height = h > 0 ? h : 2.5
                    };
                }

                case "point":
                    return new DBPoint(ReadPoint3d(spec["position"], "position"));

                default:
                    throw new ArgumentException("Unsupported entity kind '" + kind + "'.");
            }
        }

        private static Point3d ReadPoint3d(JToken tok, string field)
        {
            var arr = tok as JArray;
            if (arr == null || arr.Count < 2)
                throw new ArgumentException("Field '" + field + "' must be a [x, y] or [x, y, z] coordinate array.");
            double x = (double)arr[0];
            double y = (double)arr[1];
            double z = arr.Count >= 3 ? (double)arr[2] : 0.0;
            return new Point3d(x, y, z);
        }

        private static bool TryResolveHandle(Database db, string hexHandle, out ObjectId oid)
        {
            oid = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(hexHandle)) return false;
            if (!long.TryParse(hexHandle.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long val))
                return false;
            return db.TryGetObjectId(new Handle(val), out oid);
        }
    }
}
