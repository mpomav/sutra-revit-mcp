using System;
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
    /// AutoCAD / Civil 3D Scan-to-BIM point-cloud (.rcp/.rcs) command.
    /// Operations:
    ///   * inspect  (read-only): lists PointCloudEx entities and PointCloudDefEx definitions.
    ///   * attach   (write, dry_run=true default): attaches an .rcp/.rcs to ModelSpace and verifies Handle.
    ///   * crop_box (write, dry_run=true default): applies/clears a rectangular XY crop boundary on a PointCloudEx.
    ///   * stylize  (write, dry_run=true default): sets display stylization (rgb, elevation, intensity, normal, classification, object_color).
    /// </summary>
    internal static class CadPointCloudCommand
    {
        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active AutoCAD / Civil 3D drawing is open.");

            string op = ((string)args["operation"] ?? "inspect").Trim().ToLowerInvariant();
            if (op == "inspect")
                return Inspect(doc);

            if (op != "attach" && op != "crop_box" && op != "stylize")
                throw new ArgumentException("Unsupported operation '" + op + "'. Known: inspect, attach, crop_box, stylize.");

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

            string fingerprint = CadConfirmationStore.ComputeFingerprint("cad_point_cloud", doc.Name, args);
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

        // ---------------------------------------------------------------------
        // inspect (read-only)
        // ---------------------------------------------------------------------
        private static JObject Inspect(Document doc)
        {
            Database db = doc.Database;
            var clouds = new JArray();

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId oid in ms)
                {
                    if (oid.IsErased) continue;
                    Entity ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (!(ent is PointCloudEx pc)) continue;

                    string sourcePath = null;
                    bool? isLoaded = null;
                    long? totalPoints = null;
                    try
                    {
                        if (!pc.PointCloudDefExId.IsNull && !pc.PointCloudDefExId.IsErased)
                        {
                            var def = tr.GetObject(pc.PointCloudDefExId, OpenMode.ForRead) as PointCloudDefEx;
                            if (def != null)
                            {
                                sourcePath = def.SourceFileName;
                                isLoaded = TryReadBoolMember(def, "isLoaded", "IsLoaded", "Loaded");
                                totalPoints = TryReadTotalPoints(def);
                            }
                        }
                    }
                    catch { }

                    JObject bboxJson = null;
                    try
                    {
                        Extents3d ext = pc.GeometricExtents;
                        bboxJson = new JObject
                        {
                            ["min"] = Pt(ext.MinPoint),
                            ["max"] = Pt(ext.MaxPoint)
                        };
                    }
                    catch { }

                    clouds.Add(new JObject
                    {
                        ["handle"] = pc.Handle.ToString(),
                        ["layer"] = pc.Layer,
                        ["source_path"] = sourcePath,
                        ["is_loaded"] = isLoaded,
                        ["total_points"] = totalPoints,
                        ["location"] = Pt(pc.Location),
                        ["scale"] = Math.Round(pc.Scale, 6),
                        ["rotation_deg"] = Math.Round(pc.Rotation * 180.0 / Math.PI, 4),
                        ["locked"] = TryReadBoolMember(pc, "isLocked", "IsLocked", "Locked"),
                        ["show_cropped"] = pc.ShowCropped,
                        ["stylization"] = pc.Stylization.ToString(),
                        ["color_scheme"] = pc.CurrentColorScheme,
                        ["cropping_count"] = pc.getCroppingCount(),
                        ["bounding_box"] = bboxJson
                    });
                }

                tr.Commit();
            }

            return new JObject
            {
                ["operation"] = "inspect",
                ["document"] = Path.GetFileName(doc.Name),
                ["count"] = clouds.Count,
                ["point_clouds"] = clouds
            };
        }

        // ---------------------------------------------------------------------
        // Preview (dry_run=true)
        // ---------------------------------------------------------------------
        private static JObject Preview(Document doc, string op, JObject args)
        {
            if (op == "attach")
            {
                string path = ValidateCloudPath((string)args["path"]);
                Point3d loc = ReadPoint3d(args["location"] as JArray, Point3d.Origin);
                double scale = (double?)args["scale"] ?? 1.0;
                double rotDeg = (double?)args["rotation_deg"] ?? 0.0;
                if (scale <= 0) throw new ArgumentException("scale must be > 0.");

                return new JObject
                {
                    ["operation"] = op,
                    ["path"] = path,
                    ["location"] = Pt(loc),
                    ["scale"] = scale,
                    ["rotation_deg"] = rotDeg,
                    ["layer"] = ((string)args["layer"] ?? "").Trim(),
                    ["would_attach"] = true
                };
            }

            string handle = ((string)args["handle"] ?? "").Trim();
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException(op + " requires 'handle' identifying a PointCloudEx entity.");

            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!TryResolvePointCloud(db, tr, handle, OpenMode.ForRead, out PointCloudEx pc))
                    throw new ArgumentException("Handle '" + handle + "' does not resolve to an active PointCloudEx entity.");

                if (op == "crop_box")
                {
                    bool clear = (bool?)args["clear"] ?? false;
                    if (!clear)
                    {
                        var minArr = args["min"] as JArray;
                        var maxArr = args["max"] as JArray;
                        if (minArr == null || minArr.Count < 2 || maxArr == null || maxArr.Count < 2)
                            throw new ArgumentException("crop_box requires min [x,y] and max [x,y] (or clear=true).");
                    }
                    tr.Commit();
                    return new JObject
                    {
                        ["operation"] = op,
                        ["handle"] = handle,
                        ["clear"] = clear,
                        ["invert"] = (bool?)args["invert"] ?? false,
                        ["current_cropping_count"] = pc.getCroppingCount()
                    };
                }

                if (op == "stylize")
                {
                    PointCloudStylizationType target = ParseStylization((string)args["stylization"]);
                    tr.Commit();
                    return new JObject
                    {
                        ["operation"] = op,
                        ["handle"] = handle,
                        ["current_stylization"] = pc.Stylization.ToString(),
                        ["target_stylization"] = target.ToString(),
                        ["color_scheme"] = (string)args["color_scheme"]
                    };
                }
            }

            throw new ArgumentException("Unsupported operation '" + op + "'.");
        }

        // ---------------------------------------------------------------------
        // CommitAndVerify (dry_run=false)
        // ---------------------------------------------------------------------
        private static JObject CommitAndVerify(Document doc, string op, JObject args)
        {
            Database db = doc.Database;

            if (op == "attach")
            {
                string path = ValidateCloudPath((string)args["path"]);
                Point3d loc = ReadPoint3d(args["location"] as JArray, Point3d.Origin);
                double scale = (double?)args["scale"] ?? 1.0;
                double rotDeg = (double?)args["rotation_deg"] ?? 0.0;
                if (scale <= 0) throw new ArgumentException("scale must be > 0.");
                string layer = ((string)args["layer"] ?? "").Trim();

                string createdHandle;
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId pcId = AttachPointCloudEntity(db, tr, path, loc, scale, rotDeg * Math.PI / 180.0, layer);
                    var pc = (PointCloudEx)tr.GetObject(pcId, OpenMode.ForRead);
                    createdHandle = pc.Handle.ToString();
                    tr.Commit();
                }

                // Post-commit re-read verification
                bool verified = false;
                bool? loaded = null;
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (TryResolvePointCloud(db, tr, createdHandle, OpenMode.ForRead, out PointCloudEx reread))
                    {
                        verified = true;
                        var def = tr.GetObject(reread.PointCloudDefExId, OpenMode.ForRead) as PointCloudDefEx;
                        loaded = def == null ? (bool?)null : TryReadBoolMember(def, "isLoaded", "IsLoaded", "Loaded");
                    }
                    tr.Commit();
                }

                return new JObject
                {
                    ["operation"] = op,
                    ["handle"] = createdHandle,
                    ["path"] = path,
                    ["is_loaded"] = loaded,
                    ["verified"] = verified,
                    ["verification_state"] = verified ? "verified_applied" : "partial"
                };
            }

            string handle = ((string)args["handle"] ?? "").Trim();

            if (op == "crop_box")
            {
                bool clear = (bool?)args["clear"] ?? false;
                bool invert = (bool?)args["invert"] ?? false;

                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (!TryResolvePointCloud(db, tr, handle, OpenMode.ForWrite, out PointCloudEx pc))
                        throw new ArgumentException("Handle '" + handle + "' does not resolve to an active PointCloudEx entity.");

                    if (clear)
                    {
                        pc.clearCropping();
                    }
                    else
                    {
                        var minArr = args["min"] as JArray;
                        var maxArr = args["max"] as JArray;
                        double x1 = minArr[0].Value<double>(), y1 = minArr[1].Value<double>();
                        double x2 = maxArr[0].Value<double>(), y2 = maxArr[1].Value<double>();
                        double z = minArr.Count >= 3 ? minArr[2].Value<double>() : 0.0;
                        if (x2 <= x1 || y2 <= y1)
                            throw new ArgumentException("crop_box requires max.x > min.x and max.y > min.y.");

                        if ((bool?)args["replace_existing"] ?? true)
                            pc.clearCropping();

                        var pts = new Point3dCollection
                        {
                            new Point3d(x1, y1, z),
                            new Point3d(x2, y2, z)
                        };
                        PointCloudCrop crop = CreateRectangularCrop(pts, invert);
                        pc.addCroppingBoundary(crop);
                        pc.ShowCropped = true;
                    }
                    tr.Commit();
                }

                int cropCountAfter = 0;
                bool showCroppedAfter = false;
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (TryResolvePointCloud(db, tr, handle, OpenMode.ForRead, out PointCloudEx reread))
                    {
                        cropCountAfter = reread.getCroppingCount();
                        showCroppedAfter = reread.ShowCropped;
                    }
                    tr.Commit();
                }

                bool ok = clear ? cropCountAfter == 0 : cropCountAfter > 0;
                return new JObject
                {
                    ["operation"] = op,
                    ["handle"] = handle,
                    ["clear"] = clear,
                    ["cropping_count"] = cropCountAfter,
                    ["show_cropped"] = showCroppedAfter,
                    ["verified"] = ok,
                    ["verification_state"] = ok ? "verified_applied" : "partial"
                };
            }

            if (op == "stylize")
            {
                PointCloudStylizationType target = ParseStylization((string)args["stylization"]);
                string scheme = ((string)args["color_scheme"] ?? "").Trim();

                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (!TryResolvePointCloud(db, tr, handle, OpenMode.ForWrite, out PointCloudEx pc))
                        throw new ArgumentException("Handle '" + handle + "' does not resolve to an active PointCloudEx entity.");

                    pc.Stylization = target;
                    if (!string.IsNullOrEmpty(scheme))
                        pc.CurrentColorScheme = scheme;
                    tr.Commit();
                }

                string actualStyle = null;
                string actualScheme = null;
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (TryResolvePointCloud(db, tr, handle, OpenMode.ForRead, out PointCloudEx reread))
                    {
                        actualStyle = reread.Stylization.ToString();
                        actualScheme = reread.CurrentColorScheme;
                    }
                    tr.Commit();
                }

                bool ok = string.Equals(actualStyle, target.ToString(), StringComparison.OrdinalIgnoreCase);
                return new JObject
                {
                    ["operation"] = op,
                    ["handle"] = handle,
                    ["stylization"] = actualStyle,
                    ["color_scheme"] = actualScheme,
                    ["verified"] = ok,
                    ["verification_state"] = ok ? "verified_applied" : "partial"
                };
            }

            throw new ArgumentException("Unsupported operation '" + op + "'.");
        }

        // ---------------------------------------------------------------------
        // HELPERS
        // ---------------------------------------------------------------------

        private static ObjectId AttachPointCloudEntity(
            Database db, Transaction tr, string path, Point3d loc, double scale, double rotRad, string layer)
        {
            // PointCloudDefEx dictionary lives in the Named Objects Dictionary under "ACAD_POINTCLOUD_EX_DICT".
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
            const string dictKey = "ACAD_POINTCLOUD_EX_DICT";
            ObjectId dictId;
            if (nod.Contains(dictKey))
            {
                dictId = nod.GetAt(dictKey);
            }
            else
            {
                var createdDict = new DBDictionary();
                dictId = nod.SetAt(dictKey, createdDict);
                tr.AddNewlyCreatedDBObject(createdDict, true);
            }

            var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForWrite);
            string defName = MakeUniqueDefName(dict, Path.GetFileNameWithoutExtension(path));

            var def = new PointCloudDefEx { SourceFileName = path };
            ObjectId defId = dict.SetAt(defName, def);
            tr.AddNewlyCreatedDBObject(def, true);
            TryLoadDef(def);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var pc = new PointCloudEx
            {
                PointCloudDefExId = defId,
                Location = loc,
                Scale = scale,
                Rotation = rotRad
            };
            if (!string.IsNullOrEmpty(layer))
                pc.Layer = layer;

            ObjectId pcId = ms.AppendEntity(pc);
            tr.AddNewlyCreatedDBObject(pc, true);
            return pcId;
        }

        private static string ValidateCloudPath(string rawPath)
        {
            string path = (rawPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new ArgumentException("path is required and must be an absolute path to an .rcp or .rcs file.");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".rcp" && ext != ".rcs")
                throw new ArgumentException("AutoCAD point cloud attach supports .rcp or .rcs files (got '" + ext + "').");
            if (!File.Exists(path))
                throw new ArgumentException("Point cloud file does not exist: '" + path + "'.");
            return path;
        }

        private static bool TryResolvePointCloud(Database db, Transaction tr, string hexHandle, OpenMode mode, out PointCloudEx pc)
        {
            pc = null;
            if (string.IsNullOrWhiteSpace(hexHandle)) return false;
            if (!long.TryParse(hexHandle.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long val))
                return false;
            if (!db.TryGetObjectId(new Handle(val), out ObjectId oid) || oid.IsNull || oid.IsErased)
                return false;
            pc = tr.GetObject(oid, mode) as PointCloudEx;
            return pc != null;
        }

        private static PointCloudStylizationType ParseStylization(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant();
            string enumName;
            switch (s)
            {
                case "rgb":
                case "true_color":
                case "truecolor":
                    enumName = "TrueColor"; break;
                case "object_color":
                case "objectcolor":
                    enumName = "ObjectColor"; break;
                case "normal":
                case "normals":
                    enumName = "Normal"; break;
                case "intensity":
                    enumName = "Intensity"; break;
                case "elevation":
                    enumName = "Elevation"; break;
                case "classification":
                    enumName = "Classification"; break;
                default:
                    throw new ArgumentException(
                        "Unsupported stylization '" + raw + "'. Known: rgb, elevation, intensity, normal, classification, object_color.");
            }
            if (Enum.TryParse(enumName, true, out PointCloudStylizationType parsed))
                return parsed;
            // Fallback: match by substring across AutoCAD version enum member names (e.g. ColorRamp / ElevationRamp).
            foreach (string name in Enum.GetNames(typeof(PointCloudStylizationType)))
            {
                if (name.IndexOf(enumName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return (PointCloudStylizationType)Enum.Parse(typeof(PointCloudStylizationType), name);
            }
            throw new ArgumentException(
                "Stylization '" + raw + "' is not supported by this AutoCAD version. Available: " +
                string.Join(", ", Enum.GetNames(typeof(PointCloudStylizationType))));
        }

        private static long? TryReadTotalPoints(PointCloudDefEx def)
        {
            try
            {
                PropertyInfo p = typeof(PointCloudDefEx).GetProperty("TotalPointsCount")
                    ?? typeof(PointCloudDefEx).GetProperty("PointCount");
                if (p != null) return Convert.ToInt64(p.GetValue(def));
            }
            catch { }
            return null;
        }

        private static bool? TryReadBoolMember(object target, params string[] names)
        {
            if (target == null) return null;
            Type t = target.GetType();
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo p = t.GetProperty(name);
                    if (p != null) return Convert.ToBoolean(p.GetValue(target));
                    MethodInfo m = t.GetMethod(name, Type.EmptyTypes);
                    if (m != null) return Convert.ToBoolean(m.Invoke(target, null));
                }
                catch { }
            }
            return null;
        }

        private static void TryLoadDef(PointCloudDefEx def)
        {
            if (def == null) return;
            Type t = def.GetType();
            foreach (string name in new[] { "Load", "load", "LoadPointCloud" })
            {
                try
                {
                    MethodInfo mBool = t.GetMethod(name, new[] { typeof(bool) });
                    if (mBool != null) { mBool.Invoke(def, new object[] { true }); return; }
                    MethodInfo mVoid = t.GetMethod(name, Type.EmptyTypes);
                    if (mVoid != null) { mVoid.Invoke(def, null); return; }
                }
                catch { }
            }
        }

        private static PointCloudCrop CreateRectangularCrop(Point3dCollection pts, bool invert)
        {
            // AutoCAD PointCloudCrop.Create overloads vary slightly across 2023-2026;
            // resolve cleanly via Create(pts, normal, type) or Create(pts, normal, type, keepInside, invert).
            Type t = typeof(PointCloudCrop);
            MethodInfo m3 = t.GetMethod("Create", new[] { typeof(Point3dCollection), typeof(Vector3d), typeof(PointCloudCropType) });
            PointCloudCrop crop = null;
            if (m3 != null)
            {
                crop = (PointCloudCrop)m3.Invoke(null, new object[] { pts, Vector3d.ZAxis, PointCloudCropType.Rectangular });
            }
            else
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Create") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 5 && ps[0].ParameterType == typeof(Point3dCollection))
                    {
                        crop = (PointCloudCrop)m.Invoke(null, new object[] { pts, Vector3d.ZAxis, PointCloudCropType.Rectangular, !invert, invert });
                        break;
                    }
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(Point3dCollection))
                    {
                        crop = (PointCloudCrop)m.Invoke(null, new object[] { pts, Vector3d.ZAxis });
                        break;
                    }
                }
            }
            if (crop == null)
                throw new InvalidOperationException("PointCloudCrop.Create overload not found in this AutoCAD version.");
            try
            {
                PropertyInfo pInv = t.GetProperty("Invert") ?? t.GetProperty("Inverted");
                if (pInv != null && pInv.CanWrite) pInv.SetValue(crop, invert);
            }
            catch { }
            return crop;
        }

        private static string MakeUniqueDefName(DBDictionary dict, string baseName)
        {
            string clean = string.IsNullOrWhiteSpace(baseName) ? "SCIO_CLOUD" : baseName.Trim();
            if (!dict.Contains(clean)) return clean;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = clean + "_" + i.ToString(CultureInfo.InvariantCulture);
                if (!dict.Contains(candidate)) return candidate;
            }
            return clean + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static Point3d ReadPoint3d(JArray arr, Point3d fallback)
        {
            if (arr == null || arr.Count < 2) return fallback;
            double x = arr[0].Value<double>();
            double y = arr[1].Value<double>();
            double z = arr.Count >= 3 ? arr[2].Value<double>() : 0.0;
            return new Point3d(x, y, z);
        }

        private static JArray Pt(Point3d p)
            => new JArray(Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4));
    }
}
