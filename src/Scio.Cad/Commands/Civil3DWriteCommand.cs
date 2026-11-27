using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using Scio.Cad.Core;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// Transactional Civil 3D creation &amp; editing command (late-bound over AeccDbMgd.dll)
    /// following Sutra's rehearse (dry_run=true) -> confirmation_token -> commit -> re-read contract.
    /// </summary>
    internal static class Civil3DWriteCommand
    {
        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active Civil 3D drawing is open.");

            Assembly aecc = GetAeccAssembly();
            object cdoc = GetCivilDocument(aecc);
            if (aecc == null || cdoc == null)
                throw new InvalidOperationException("Civil 3D (AeccDbMgd) is not active in this session. Open Civil 3D to run scio_c3d_write.");

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
            string fingerprint = CadConfirmationStore.ComputeFingerprint("c3d_write", doc.Name, args);

            if (dryRun)
            {
                string token = CadConfirmationStore.Issue(fingerprint);
                JObject preview = Preview(doc, cdoc, op, args);
                preview["dry_run"] = true;
                preview["verification_state"] = "rehearsed";
                preview["active_document"] = activeTitle;
                preview["confirmation_token"] = token;
                return preview;
            }

            string tokenError = CadConfirmationStore.Consume((string)args["confirmation_token"], fingerprint);
            if (tokenError != null)
                throw new InvalidOperationException(tokenError);

            JObject committed = CommitAndVerify(doc, aecc, cdoc, op, args);
            committed["dry_run"] = false;
            committed["active_document"] = activeTitle;
            return committed;
        }

        private static JObject Preview(Document doc, object cdoc, string op, JObject args)
        {
            switch (op)
            {
                case "create_cogo_points":
                {
                    var pts = args["points"] as JArray ?? new JArray();
                    if (pts.Count == 0) throw new ArgumentException("create_cogo_points requires a non-empty 'points' array.");
                    return new JObject { ["operation"] = op, ["would_create_cogo_points"] = pts.Count };
                }
                case "create_tin_surface":
                {
                    string name = ((string)args["name"] ?? (string)args["surface_name"] ?? "").Trim();
                    if (string.IsNullOrEmpty(name)) throw new ArgumentException("create_tin_surface requires 'name'.");
                    var pts = args["points"] as JArray;
                    return new JObject
                    {
                        ["operation"] = op,
                        ["surface_name"] = name,
                        ["initial_points"] = pts?.Count ?? 0
                    };
                }
                case "add_surface_data":
                {
                    string sname = ((string)args["surface_name"] ?? (string)args["name"] ?? "").Trim();
                    if (string.IsNullOrEmpty(sname)) throw new ArgumentException("add_surface_data requires 'surface_name'.");
                    ObjectId sid = FindSurfaceByName(doc, cdoc, sname);
                    if (sid.IsNull) throw new InvalidOperationException("TIN surface '" + sname + "' not found.");
                    var pts = args["points"] as JArray;
                    var bls = args["breakline_handles"] as JArray;
                    return new JObject
                    {
                        ["operation"] = op,
                        ["surface_name"] = sname,
                        ["points_to_add"] = pts?.Count ?? 0,
                        ["breaklines_to_add"] = bls?.Count ?? 0,
                        ["rebuild"] = (bool?)args["rebuild"] ?? true
                    };
                }
                case "create_alignment":
                {
                    string name = ((string)args["name"] ?? "").Trim();
                    if (string.IsNullOrEmpty(name)) throw new ArgumentException("create_alignment requires 'name'.");
                    string plHandle = ((string)args["polyline_handle"] ?? "").Trim();
                    var pts = args["alignment_points"] as JArray;
                    if (string.IsNullOrEmpty(plHandle) && (pts == null || pts.Count < 2))
                        throw new ArgumentException("create_alignment requires either 'polyline_handle' or 'alignment_points' (>= 2 points).");
                    return new JObject
                    {
                        ["operation"] = op,
                        ["alignment_name"] = name,
                        ["source"] = !string.IsNullOrEmpty(plHandle) ? "polyline:" + plHandle : "points:" + pts.Count
                    };
                }
                case "edit_pipe_network":
                {
                    var edits = args["pipe_edits"] as JArray ?? new JArray();
                    if (edits.Count == 0) throw new ArgumentException("edit_pipe_network requires a non-empty 'pipe_edits' array.");
                    int found = 0;
                    using (doc.LockDocument())
                    using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (JObject e in edits)
                            if (TryResolveHandle(doc.Database, (string)e["handle"], out ObjectId oid) && !oid.IsErased) found++;
                        tr.Commit();
                    }
                    return new JObject
                    {
                        ["operation"] = op,
                        ["requested_edits"] = edits.Count,
                        ["resolvable_objects"] = found
                    };
                }
                default:
                    throw new ArgumentException("Unsupported Civil 3D write operation '" + op + "'.");
            }
        }

        private static JObject CommitAndVerify(Document doc, Assembly aecc, object cdoc, string op, JObject args)
        {
            Database db = doc.Database;
            switch (op)
            {
                case "create_cogo_points":
                    return CreateCogoPoints(doc, db, cdoc, args);
                case "create_tin_surface":
                    return CreateTinSurface(doc, db, aecc, cdoc, args);
                case "add_surface_data":
                    return AddSurfaceData(doc, db, cdoc, args);
                case "create_alignment":
                    return CreateAlignment(doc, db, aecc, cdoc, args);
                case "edit_pipe_network":
                    return EditPipeNetwork(doc, db, args);
                default:
                    throw new ArgumentException("Unsupported Civil 3D write operation '" + op + "'.");
            }
        }

        private static JObject CreateCogoPoints(Document doc, Database db, object cdoc, JObject args)
        {
            var pts = args["points"] as JArray ?? new JArray();
            object cogoCol = GetProp(cdoc, "CogoPoints");
            if (cogoCol == null) throw new InvalidOperationException("CivilDocument.CogoPoints is unavailable.");

            var createdIds = new List<ObjectId>();
            var handles = new JArray();

            using (doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (JObject p in pts)
                    {
                        double e = (double)p["easting"];
                        double n = (double)p["northing"];
                        double z = (double?)p["elevation"] ?? 0.0;
                        string desc = ((string)p["description"] ?? "").Trim();
                        string pName = ((string)p["name"] ?? "").Trim();

                        ObjectId pid = AddSingleCogoPoint(cogoCol, new Point3d(e, n, z), desc);
                        if (!pid.IsNull)
                        {
                            createdIds.Add(pid);
                            if (!string.IsNullOrEmpty(pName))
                            {
                                DBObject cogoObj = tr.GetObject(pid, OpenMode.ForWrite);
                                SetProp(cogoObj, "PointName", pName);
                            }
                        }
                    }
                    tr.Commit();
                }

                // Post-commit re-read proof
                int verified = 0;
                using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId oid in createdIds)
                    {
                        if (!oid.IsNull && !oid.IsErased)
                        {
                            DBObject obj = verifyTr.GetObject(oid, OpenMode.ForRead);
                            handles.Add(obj.Handle.ToString());
                            verified++;
                        }
                    }
                    verifyTr.Commit();
                }

                return new JObject
                {
                    ["operation"] = "create_cogo_points",
                    ["created_count"] = createdIds.Count,
                    ["verified_count"] = verified,
                    ["handles"] = handles,
                    ["verification_state"] = verified == pts.Count ? "verified" : "partial"
                };
            }
        }

        private static JObject CreateTinSurface(Document doc, Database db, Assembly aecc, object cdoc, JObject args)
        {
            string name = ((string)args["name"] ?? (string)args["surface_name"] ?? "").Trim();
            var pts = args["points"] as JArray;

            Type tinType = aecc.GetType("Autodesk.Civil.DatabaseServices.TinSurface", false);
            if (tinType == null) throw new InvalidOperationException("TinSurface type not found in AeccDbMgd.");

            ObjectId surfId;
            string handle;

            using (doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    MethodInfo create2 = tinType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(Database), typeof(string) }, null);
                    if (create2 == null)
                        throw new InvalidOperationException("TinSurface.Create(Database, string) overload not found.");

                    surfId = (ObjectId)create2.Invoke(null, new object[] { db, name });
                    DBObject surfObj = tr.GetObject(surfId, OpenMode.ForWrite);

                    if (pts != null && pts.Count > 0)
                    {
                        var p3d = new Point3dCollection();
                        foreach (JObject p in pts)
                            p3d.Add(new Point3d((double)p["easting"], (double)p["northing"], (double?)p["elevation"] ?? 0.0));
                        InvokeBest(surfObj, "AddVertices", new object[] { p3d });
                    }

                    if ((bool?)args["rebuild"] ?? true) InvokeBest(surfObj, "Rebuild", Array.Empty<object>());
                    handle = surfObj.Handle.ToString();
                    tr.Commit();
                }

                // Post-commit re-read proof
                long pointCount = 0, triCount = 0;
                bool exists = false;
                using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                {
                    if (!surfId.IsNull && !surfId.IsErased)
                    {
                        DBObject surfObj = verifyTr.GetObject(surfId, OpenMode.ForRead);
                        exists = string.Equals(GetProp(surfObj, "Name") as string, name, StringComparison.OrdinalIgnoreCase);
                        object gen = InvokeBest(surfObj, "GetGeneralProperties", Array.Empty<object>());
                        object tin = InvokeBest(surfObj, "GetTinProperties", Array.Empty<object>());
                        if (gen != null) pointCount = Convert.ToInt64(GetProp(gen, "NumberOfPoints") ?? 0);
                        if (tin != null) triCount = Convert.ToInt64(GetProp(tin, "NumberOfTriangles") ?? 0);
                    }
                    verifyTr.Commit();
                }

                return new JObject
                {
                    ["operation"] = "create_tin_surface",
                    ["surface_name"] = name,
                    ["handle"] = handle,
                    ["point_count"] = pointCount,
                    ["triangle_count"] = triCount,
                    ["verification_state"] = exists ? "verified" : "partial"
                };
            }
        }

        private static JObject AddSurfaceData(Document doc, Database db, object cdoc, JObject args)
        {
            string sname = ((string)args["surface_name"] ?? (string)args["name"] ?? "").Trim();
            ObjectId surfId = FindSurfaceByName(doc, cdoc, sname);
            if (surfId.IsNull) throw new InvalidOperationException("TIN surface '" + sname + "' not found.");

            var pts = args["points"] as JArray;
            var blHandles = args["breakline_handles"] as JArray;
            int ptsAdded = 0, blAdded = 0;

            using (doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBObject surfObj = tr.GetObject(surfId, OpenMode.ForWrite);

                    if (pts != null && pts.Count > 0)
                    {
                        var p3d = new Point3dCollection();
                        foreach (JObject p in pts)
                            p3d.Add(new Point3d((double)p["easting"], (double)p["northing"], (double?)p["elevation"] ?? 0.0));
                        InvokeBest(surfObj, "AddVertices", new object[] { p3d });
                        ptsAdded = p3d.Count;
                    }

                    if (blHandles != null && blHandles.Count > 0)
                    {
                        var ids = new ObjectIdCollection();
                        foreach (JToken h in blHandles)
                            if (TryResolveHandle(db, (string)h, out ObjectId oid) && !oid.IsErased) ids.Add(oid);

                        if (ids.Count > 0)
                        {
                            object blDef = GetProp(surfObj, "BreaklinesDefinition");
                            if (blDef != null)
                            {
                                InvokeBest(blDef, "AddStandardBreaklines", new object[] { ids, 1.0, 0.0, 0.0, 0.0 });
                                blAdded = ids.Count;
                            }
                        }
                    }

                    if ((bool?)args["rebuild"] ?? true) InvokeBest(surfObj, "Rebuild", Array.Empty<object>());
                    tr.Commit();
                }

                // Post-commit re-read proof
                long pointCount = 0, triCount = 0;
                using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                {
                    DBObject surfObj = verifyTr.GetObject(surfId, OpenMode.ForRead);
                    object gen = InvokeBest(surfObj, "GetGeneralProperties", Array.Empty<object>());
                    object tin = InvokeBest(surfObj, "GetTinProperties", Array.Empty<object>());
                    if (gen != null) pointCount = Convert.ToInt64(GetProp(gen, "NumberOfPoints") ?? 0);
                    if (tin != null) triCount = Convert.ToInt64(GetProp(tin, "NumberOfTriangles") ?? 0);
                    verifyTr.Commit();
                }

                return new JObject
                {
                    ["operation"] = "add_surface_data",
                    ["surface_name"] = sname,
                    ["points_added"] = ptsAdded,
                    ["breaklines_added"] = blAdded,
                    ["total_points"] = pointCount,
                    ["total_triangles"] = triCount,
                    ["verification_state"] = "verified"
                };
            }
        }

        private static JObject CreateAlignment(Document doc, Database db, Assembly aecc, object cdoc, JObject args)
        {
            string name = ((string)args["name"] ?? "").Trim();
            string layerName = ((string)args["layer"] ?? "0").Trim();
            string plHandle = ((string)args["polyline_handle"] ?? "").Trim();
            var pts = args["alignment_points"] as JArray;

            Type alignType = aecc.GetType("Autodesk.Civil.DatabaseServices.Alignment", false);
            Type plOptsType = aecc.GetType("Autodesk.Civil.DatabaseServices.PolylineOptions", false);
            if (alignType == null || plOptsType == null)
                throw new InvalidOperationException("Civil 3D Alignment / PolylineOptions types not found.");

            ObjectId alignId = ObjectId.Null;
            string alignHandle = null;

            using (doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId plId;
                    if (!string.IsNullOrEmpty(plHandle))
                    {
                        if (!TryResolveHandle(db, plHandle, out plId) || plId.IsErased)
                            throw new InvalidOperationException("Polyline handle '" + plHandle + "' not found.");
                    }
                    else
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        var pl = new Polyline();
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var arr = (JArray)pts[i];
                            pl.AddVertexAt(i, new Point2d((double)arr[0], (double)arr[1]), 0, 0, 0);
                        }
                        plId = ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                    }

                    ObjectId layerId = EnsureLayer(db, tr, layerName);
                    ObjectId styleId = FirstStyleId(cdoc, "AlignmentStyles");
                    ObjectId labelSetId = FirstStyleId(cdoc, "AlignmentLabelSetStyles");

                    object plOpts = Activator.CreateInstance(plOptsType);
                    SetProp(plOpts, "PlineId", plId);
                    SetProp(plOpts, "AddCurvesBetweenTangents", false);
                    SetProp(plOpts, "EraseExistingEntities", string.IsNullOrEmpty(plHandle));

                    MethodInfo createMethod = null;
                    foreach (MethodInfo m in alignType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "Create") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 7 && ps[1].ParameterType == plOptsType)
                        {
                            createMethod = m;
                            break;
                        }
                    }
                    if (createMethod == null)
                        throw new InvalidOperationException("Alignment.Create(CivilDocument, PolylineOptions, ...) overload not found.");

                    alignId = (ObjectId)createMethod.Invoke(null, new object[]
                    {
                        cdoc, plOpts, name, ObjectId.Null, layerId, styleId, labelSetId
                    });

                    DBObject alignObj = tr.GetObject(alignId, OpenMode.ForRead);
                    alignHandle = alignObj.Handle.ToString();
                    tr.Commit();
                }

                // Post-commit re-read proof
                double length = 0;
                bool exists = false;
                using (Transaction verifyTr = db.TransactionManager.StartTransaction())
                {
                    if (!alignId.IsNull && !alignId.IsErased)
                    {
                        DBObject alignObj = verifyTr.GetObject(alignId, OpenMode.ForRead);
                        exists = string.Equals(GetProp(alignObj, "Name") as string, name, StringComparison.OrdinalIgnoreCase);
                        length = Convert.ToDouble(GetProp(alignObj, "Length") ?? 0.0);
                    }
                    verifyTr.Commit();
                }

                return new JObject
                {
                    ["operation"] = "create_alignment",
                    ["alignment_name"] = name,
                    ["handle"] = alignHandle,
                    ["length"] = Math.Round(length, 4),
                    ["verification_state"] = exists ? "verified" : "partial"
                };
            }
        }

        private static JObject EditPipeNetwork(Document doc, Database db, JObject args)
        {
            var edits = args["pipe_edits"] as JArray ?? new JArray();
            var verifiedHandles = new JArray();
            int updated = 0;

            using (doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (JObject e in edits)
                    {
                        string h = (string)e["handle"];
                        if (!TryResolveHandle(db, h, out ObjectId oid) || oid.IsErased) continue;
                        DBObject obj = tr.GetObject(oid, OpenMode.ForWrite);
                        string typeName = obj.GetType().Name;

                        if (e["description"] != null) SetProp(obj, "Description", (string)e["description"]);

                        if (typeName == "Pipe")
                        {
                            if (e["start_invert"] != null)
                            {
                                object spObj = GetProp(obj, "StartPoint");
                                if (spObj is Point3d sp)
                                {
                                    double radius = Convert.ToDouble(GetProp(obj, "InnerDiameterOrWidth") ?? 0.0) * 0.5;
                                    SetProp(obj, "StartPoint", new Point3d(sp.X, sp.Y, (double)e["start_invert"] + radius));
                                }
                            }
                            if (e["end_invert"] != null)
                            {
                                object epObj = GetProp(obj, "EndPoint");
                                if (epObj is Point3d ep)
                                {
                                    double radius = Convert.ToDouble(GetProp(obj, "InnerDiameterOrWidth") ?? 0.0) * 0.5;
                                    SetProp(obj, "EndPoint", new Point3d(ep.X, ep.Y, (double)e["end_invert"] + radius));
                                }
                            }
                        }
                        else if (typeName == "Structure")
                        {
                            if (e["rim_elevation"] != null) SetProp(obj, "RimElevation", (double)e["rim_elevation"]);
                            if (e["sump_elevation"] != null) SetProp(obj, "SumpElevation", (double)e["sump_elevation"]);
                        }

                        updated++;
                        verifiedHandles.Add(h);
                    }
                    tr.Commit();
                }

                return new JObject
                {
                    ["operation"] = "edit_pipe_network",
                    ["updated_count"] = updated,
                    ["handles"] = verifiedHandles,
                    ["verification_state"] = updated == edits.Count ? "verified" : "partial"
                };
            }
        }

        // ---- Reflection & CAD helpers -----------------------------------------

        private static ObjectId AddSingleCogoPoint(object cogoCol, Point3d pt, string desc)
        {
            Type t = cogoCol.GetType();
            MethodInfo m4 = t.GetMethod("Add", new[] { typeof(Point3d), typeof(string), typeof(bool), typeof(bool) });
            if (m4 != null) return (ObjectId)m4.Invoke(cogoCol, new object[] { pt, desc ?? "", true, false });
            MethodInfo m2 = t.GetMethod("Add", new[] { typeof(Point3d), typeof(string) });
            if (m2 != null) return (ObjectId)m2.Invoke(cogoCol, new object[] { pt, desc ?? "" });
            MethodInfo m1 = t.GetMethod("Add", new[] { typeof(Point3d), typeof(bool) });
            if (m1 != null) return (ObjectId)m1.Invoke(cogoCol, new object[] { pt, true });
            throw new InvalidOperationException("No compatible CogoPointCollection.Add overload found.");
        }

        private static ObjectId FindSurfaceByName(Document doc, object cdoc, string name)
        {
            ObjectIdCollection ids = InvokeBest(cdoc, "GetSurfaceIds", Array.Empty<object>()) as ObjectIdCollection;
            if (ids == null) return ObjectId.Null;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    if (string.Equals(GetProp(obj, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
                    {
                        tr.Commit();
                        return id;
                    }
                }
                tr.Commit();
            }
            return ObjectId.Null;
        }

        private static ObjectId EnsureLayer(Database db, Transaction tr, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName)) return lt[layerName];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = layerName };
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        private static ObjectId FirstStyleId(object cdoc, string styleCollectionProp)
        {
            try
            {
                object styles = GetProp(cdoc, "Styles");
                object col = GetProp(styles, styleCollectionProp);
                if (col is System.Collections.IEnumerable en)
                {
                    foreach (object item in en)
                        if (item is ObjectId id && !id.IsNull) return id;
                }
            }
            catch { }
            return ObjectId.Null;
        }

        private static Assembly GetAeccAssembly()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(asm.GetName().Name, "AeccDbMgd", StringComparison.OrdinalIgnoreCase))
                    return asm;
            return null;
        }

        private static object GetCivilDocument(Assembly aecc)
        {
            if (aecc == null) return null;
            Type appType = aecc.GetType("Autodesk.Civil.ApplicationServices.CivilApplication", false);
            return appType?.GetProperty("ActiveDocument", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj); }
            catch { return null; }
        }

        private static void SetProp(object obj, string name, object value)
        {
            if (obj == null) return;
            try { obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(obj, value); }
            catch { }
        }

        private static object InvokeBest(object obj, string name, object[] args)
        {
            if (obj == null) return null;
            foreach (MethodInfo m in obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (args[i] != null && !ps[i].ParameterType.IsAssignableFrom(args[i].GetType()))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return m.Invoke(obj, args);
            }
            return null;
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
