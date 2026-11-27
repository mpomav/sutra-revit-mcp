using System;
using System.Collections;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// Late-bound Civil 3D inspector (AeccDbMgd.dll).
    /// Keeps Scio.Cad.dll loadable in BOTH plain AutoCAD and Civil 3D without hard-linking C3D assemblies.
    /// </summary>
    internal static class Civil3DInspectCommand
    {
        private static Assembly _aeccAssembly;
        private static bool _probed;

        public static bool IsCivil3DLoaded()
        {
            return GetCivilDocument() != null;
        }

        public static string TryReadCoordinateSystem()
        {
            try
            {
                object cdoc = GetCivilDocument();
                if (cdoc == null) return null;
                object settings = GetProp(cdoc, "Settings");
                object dwgSettings = GetProp(settings, "DrawingSettings");
                object unitZone = GetProp(dwgSettings, "UnitZoneSettings");
                string code = GetProp(unitZone, "CoordinateSystemCode") as string;
                return string.IsNullOrWhiteSpace(code) ? null : code;
            }
            catch { return null; }
        }

        public static JObject BuildSummary()
        {
            object cdoc = GetCivilDocument();
            if (cdoc == null) return new JObject { ["civil3d_loaded"] = false };

            return new JObject
            {
                ["civil3d_loaded"] = true,
                ["coordinate_system"] = TryReadCoordinateSystem(),
                ["surfaces_count"] = CountObjectIds(InvokeMethod(cdoc, "GetSurfaceIds")),
                ["alignments_count"] = CountObjectIds(InvokeMethod(cdoc, "GetAlignmentIds")),
                ["cogo_points_count"] = CountCollection(GetProp(cdoc, "CogoPoints")),
                ["pipe_networks_count"] = CountObjectIds(InvokeMethod(cdoc, "GetPipeNetworkIds")),
                ["corridors_count"] = CountCollection(GetProp(cdoc, "CorridorCollection"))
            };
        }

        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active drawing is open.");

            object cdoc = GetCivilDocument();
            if (cdoc == null)
            {
                return new JObject
                {
                    ["civil3d_loaded"] = false,
                    ["note"] = "Civil 3D (AeccDbMgd) is not active in this session. Plain AutoCAD commands (scio_cad_context, scio_cad_censo, scio_cad_write) remain available."
                };
            }

            string op = ((string)args["operation"] ?? "summary").Trim().ToLowerInvariant();
            string nameFilter = ((string)args["name_filter"] ?? "").Trim();
            int limit = Math.Max(1, Math.Min(500, (int?)args["limit"] ?? 100));

            if (op == "summary") return BuildSummary();

            Database db = doc.Database;
            var items = new JArray();

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (op == "surfaces")
                {
                    ObjectIdCollection ids = InvokeMethod(cdoc, "GetSurfaceIds") as ObjectIdCollection;
                    if (ids != null)
                    {
                        foreach (ObjectId id in ids)
                        {
                            if (items.Count >= limit) break;
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            string name = GetProp(obj, "Name") as string ?? "";
                            if (!MatchesFilter(name, nameFilter)) continue;

                            var item = new JObject
                            {
                                ["handle"] = obj.Handle.ToString(),
                                ["name"] = name,
                                ["type"] = obj.GetType().Name,
                                ["style"] = GetProp(obj, "StyleName") as string
                            };

                            object genProps = InvokeMethod(obj, "GetGeneralProperties");
                            if (genProps != null)
                            {
                                item["min_elevation"] = ToNumber(GetProp(genProps, "MinimumElevation"));
                                item["max_elevation"] = ToNumber(GetProp(genProps, "MaximumElevation"));
                                item["mean_elevation"] = ToNumber(GetProp(genProps, "MeanElevation"));
                                item["area_2d"] = ToNumber(GetProp(genProps, "Area2D"));
                                item["area_3d"] = ToNumber(GetProp(genProps, "Area3D"));
                                item["point_count"] = ToLong(GetProp(genProps, "NumberOfPoints"));
                            }

                            object tinProps = InvokeMethod(obj, "GetTinProperties");
                            if (tinProps != null)
                            {
                                item["triangle_count"] = ToLong(GetProp(tinProps, "NumberOfTriangles"));
                            }

                            items.Add(item);
                        }
                    }
                }
                else if (op == "alignments")
                {
                    ObjectIdCollection ids = InvokeMethod(cdoc, "GetAlignmentIds") as ObjectIdCollection;
                    if (ids != null)
                    {
                        foreach (ObjectId id in ids)
                        {
                            if (items.Count >= limit) break;
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            string name = GetProp(obj, "Name") as string ?? "";
                            if (!MatchesFilter(name, nameFilter)) continue;

                            items.Add(new JObject
                            {
                                ["handle"] = obj.Handle.ToString(),
                                ["name"] = name,
                                ["alignment_type"] = GetProp(obj, "AlignmentType")?.ToString(),
                                ["style"] = GetProp(obj, "StyleName") as string,
                                ["length"] = ToNumber(GetProp(obj, "Length")),
                                ["starting_station"] = ToNumber(GetProp(obj, "StartingStation")),
                                ["ending_station"] = ToNumber(GetProp(obj, "EndingStation"))
                            });
                        }
                    }
                }
                else if (op == "cogo_points")
                {
                    IEnumerable cogoIds = GetProp(cdoc, "CogoPoints") as IEnumerable;
                    if (cogoIds != null)
                    {
                        foreach (object raw in cogoIds)
                        {
                            if (items.Count >= limit) break;
                            if (!(raw is ObjectId id)) continue;
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            string desc = GetProp(obj, "RawDescription") as string ?? "";
                            string pName = GetProp(obj, "PointName") as string ?? "";
                            if (!string.IsNullOrEmpty(nameFilter) &&
                                !MatchesFilter(pName, nameFilter) && !MatchesFilter(desc, nameFilter))
                                continue;

                            items.Add(new JObject
                            {
                                ["handle"] = obj.Handle.ToString(),
                                ["point_number"] = ToLong(GetProp(obj, "PointNumber")),
                                ["name"] = pName,
                                ["raw_description"] = desc,
                                ["easting"] = ToNumber(GetProp(obj, "Easting")),
                                ["northing"] = ToNumber(GetProp(obj, "Northing")),
                                ["elevation"] = ToNumber(GetProp(obj, "Elevation"))
                            });
                        }
                    }
                }
                else if (op == "pipe_networks")
                {
                    ObjectIdCollection ids = InvokeMethod(cdoc, "GetPipeNetworkIds") as ObjectIdCollection;
                    if (ids != null)
                    {
                        foreach (ObjectId id in ids)
                        {
                            if (items.Count >= limit) break;
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            string name = GetProp(obj, "Name") as string ?? "";
                            if (!MatchesFilter(name, nameFilter)) continue;

                            items.Add(new JObject
                            {
                                ["handle"] = obj.Handle.ToString(),
                                ["name"] = name,
                                ["parts_list"] = GetProp(obj, "PartsListName") as string,
                                ["pipe_count"] = CountObjectIds(InvokeMethod(obj, "GetPipeIds")),
                                ["structure_count"] = CountObjectIds(InvokeMethod(obj, "GetStructureIds"))
                            });
                        }
                    }
                }
                else if (op == "corridors")
                {
                    IEnumerable corrIds = GetProp(cdoc, "CorridorCollection") as IEnumerable;
                    if (corrIds != null)
                    {
                        foreach (object raw in corrIds)
                        {
                            if (items.Count >= limit) break;
                            if (!(raw is ObjectId id)) continue;
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            string name = GetProp(obj, "Name") as string ?? "";
                            if (!MatchesFilter(name, nameFilter)) continue;

                            items.Add(new JObject
                            {
                                ["handle"] = obj.Handle.ToString(),
                                ["name"] = name,
                                ["baseline_count"] = CountCollection(GetProp(obj, "Baselines")),
                                ["is_out_of_date"] = GetProp(obj, "IsOutOfDate") as bool? ?? false
                            });
                        }
                    }
                }
                else
                {
                    throw new ArgumentException("Unsupported Civil 3D inspect operation '" + op + "'.");
                }

                tr.Commit();
            }

            return new JObject
            {
                ["civil3d_loaded"] = true,
                ["operation"] = op,
                ["count"] = items.Count,
                ["items"] = items
            };
        }

        private static object GetCivilDocument()
        {
            try
            {
                if (!_probed)
                {
                    _probed = true;
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(asm.GetName().Name, "AeccDbMgd", StringComparison.OrdinalIgnoreCase))
                        {
                            _aeccAssembly = asm;
                            break;
                        }
                    }
                }
                if (_aeccAssembly == null) return null;

                Type appType = _aeccAssembly.GetType("Autodesk.Civil.ApplicationServices.CivilApplication", false);
                if (appType == null) return null;
                PropertyInfo prop = appType.GetProperty("ActiveDocument", BindingFlags.Public | BindingFlags.Static);
                return prop?.GetValue(null);
            }
            catch { return null; }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj); }
            catch { return null; }
        }

        private static object InvokeMethod(object obj, string name)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(obj, null); }
            catch { return null; }
        }

        private static int CountObjectIds(object collection)
            => collection is ObjectIdCollection ids ? ids.Count : 0;

        private static int CountCollection(object collection)
        {
            if (collection == null) return 0;
            object countProp = GetProp(collection, "Count");
            if (countProp is int i) return i;
            if (collection is IEnumerable en)
            {
                int n = 0;
                foreach (object _ in en) n++;
                return n;
            }
            return 0;
        }

        private static bool MatchesFilter(string value, string filter)
            => string.IsNullOrEmpty(filter) || (value != null && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        private static JToken ToNumber(object val)
        {
            if (val == null) return JValue.CreateNull();
            try { return Math.Round(Convert.ToDouble(val), 4); } catch { return JValue.CreateNull(); }
        }

        private static JToken ToLong(object val)
        {
            if (val == null) return JValue.CreateNull();
            try { return Convert.ToInt64(val); } catch { return JValue.CreateNull(); }
        }
    }
}
