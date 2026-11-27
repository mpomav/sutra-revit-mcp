using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using Scio.Revit.Core;

namespace Scio.Cad.Commands
{
    /// <summary>
    /// Generates and exports Civil 3D engineering datasets (CSV -> SQLite scio_query_local + LandXML 1.2)
    /// cleanly in-process into %USERPROFILE%\.scio\salidas\ with zero modal dialogs.
    /// </summary>
    internal static class Civil3DExportCommand
    {
        public static JObject Execute(JObject args)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active Civil 3D drawing is open.");

            object cdoc = GetCivilDocument();
            if (cdoc == null)
                throw new InvalidOperationException("Civil 3D (AeccDbMgd) is not active in this session.");

            string op = ((string)args["operation"] ?? "").Trim().ToLowerInvariant();
            string nameFilter = ((string)args["name_filter"] ?? "").Trim();
            int maxRows = Math.Max(1, Math.Min(100000, (int?)args["max_rows"] ?? 20000));

            string salidasDir = Path.Combine(ScioPaths.DataRoot(), "salidas");
            Directory.CreateDirectory(salidasDir);
            string safeDoc = Sanitize(Path.GetFileNameWithoutExtension(doc.Name));
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            switch (op)
            {
                case "cogo_csv":
                    return ExportCogoCsv(doc, cdoc, nameFilter, maxRows, Path.Combine(salidasDir, "c3d_cogo_" + safeDoc + "_" + stamp + ".csv"));
                case "surfaces_csv":
                    return ExportSurfacesCsv(doc, cdoc, nameFilter, Path.Combine(salidasDir, "c3d_surfaces_" + safeDoc + "_" + stamp + ".csv"));
                case "alignments_csv":
                    double interval = Math.Max(0.5, (double?)args["station_interval"] ?? 20.0);
                    return ExportAlignmentsCsv(doc, cdoc, nameFilter, interval, maxRows, Path.Combine(salidasDir, "c3d_alignments_" + safeDoc + "_" + stamp + ".csv"));
                case "pipe_network_csv":
                    return ExportPipeNetworkCsv(doc, cdoc, nameFilter, maxRows, Path.Combine(salidasDir, "c3d_pipes_" + safeDoc + "_" + stamp + ".csv"));
                case "surface_grid_csv":
                    string sname = ((string)args["surface_name"] ?? nameFilter).Trim();
                    double spacing = Math.Max(0.5, (double?)args["grid_spacing"] ?? 10.0);
                    return ExportSurfaceGridCsv(doc, cdoc, sname, spacing, maxRows, Path.Combine(salidasDir, "c3d_grid_" + safeDoc + "_" + stamp + ".csv"));
                case "landxml":
                    return ExportLandXml(doc, cdoc, args, Path.Combine(salidasDir, "c3d_export_" + safeDoc + "_" + stamp + ".xml"));
                default:
                    throw new ArgumentException("Unsupported Civil 3D export operation '" + op + "'.");
            }
        }

        private static JObject ExportCogoCsv(Document doc, object cdoc, string filter, int maxRows, string csvPath)
        {
            var rows = new List<string> { "point_number,name,easting,northing,elevation,raw_description,full_description,handle" };
            int exported = 0;

            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (GetProp(cdoc, "CogoPoints") is IEnumerable cogoIds)
                {
                    foreach (object raw in cogoIds)
                    {
                        if (exported >= maxRows) break;
                        if (!(raw is ObjectId id)) continue;
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        string pName = GetProp(obj, "PointName") as string ?? "";
                        string rawDesc = GetProp(obj, "RawDescription") as string ?? "";
                        string fullDesc = GetProp(obj, "FullDescription") as string ?? "";
                        if (!string.IsNullOrEmpty(filter) && !Matches(pName, filter) && !Matches(rawDesc, filter)) continue;

                        rows.Add(string.Join(",",
                            FmtLong(GetProp(obj, "PointNumber")),
                            Csv(pName),
                            FmtNum(GetProp(obj, "Easting")),
                            FmtNum(GetProp(obj, "Northing")),
                            FmtNum(GetProp(obj, "Elevation")),
                            Csv(rawDesc),
                            Csv(fullDesc),
                            Csv(obj.Handle.ToString())));
                        exported++;
                    }
                }
                tr.Commit();
            }

            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));
            return new JObject { ["operation"] = "cogo_csv", ["rows_exported"] = exported, ["csv_path"] = csvPath };
        }

        private static JObject ExportSurfacesCsv(Document doc, object cdoc, string filter, string csvPath)
        {
            var rows = new List<string> { "name,type,style,min_elevation,max_elevation,mean_elevation,area_2d,area_3d,point_count,triangle_count,handle" };
            int exported = 0;

            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (Invoke0(cdoc, "GetSurfaceIds") is ObjectIdCollection ids)
                {
                    foreach (ObjectId id in ids)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        string name = GetProp(obj, "Name") as string ?? "";
                        if (!Matches(name, filter)) continue;

                        object gen = Invoke0(obj, "GetGeneralProperties");
                        object tin = Invoke0(obj, "GetTinProperties");

                        rows.Add(string.Join(",",
                            Csv(name),
                            Csv(obj.GetType().Name),
                            Csv(GetProp(obj, "StyleName") as string ?? ""),
                            FmtNum(GetProp(gen, "MinimumElevation")),
                            FmtNum(GetProp(gen, "MaximumElevation")),
                            FmtNum(GetProp(gen, "MeanElevation")),
                            FmtNum(GetProp(gen, "Area2D")),
                            FmtNum(GetProp(gen, "Area3D")),
                            FmtLong(GetProp(gen, "NumberOfPoints")),
                            FmtLong(GetProp(tin, "NumberOfTriangles")),
                            Csv(obj.Handle.ToString())));
                        exported++;
                    }
                }
                tr.Commit();
            }

            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));
            return new JObject { ["operation"] = "surfaces_csv", ["rows_exported"] = exported, ["csv_path"] = csvPath };
        }

        private static JObject ExportAlignmentsCsv(Document doc, object cdoc, string filter, double interval, int maxRows, string csvPath)
        {
            var rows = new List<string> { "alignment_name,station,easting,northing,handle" };
            int exported = 0;
            int alignments = 0;

            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (Invoke0(cdoc, "GetAlignmentIds") is ObjectIdCollection ids)
                {
                    foreach (ObjectId id in ids)
                    {
                        if (exported >= maxRows) break;
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        string name = GetProp(obj, "Name") as string ?? "";
                        if (!Matches(name, filter)) continue;

                        double startSta = Convert.ToDouble(GetProp(obj, "StartingStation") ?? 0.0);
                        double endSta = Convert.ToDouble(GetProp(obj, "EndingStation") ?? 0.0);
                        if (endSta <= startSta) continue;

                        alignments++;
                        MethodInfo ptLoc = FindPointLocationMethod(obj.GetType());
                        if (ptLoc == null) continue;

                        for (double sta = startSta; sta <= endSta + 1e-6 && exported < maxRows; sta += interval)
                        {
                            double s = Math.Min(sta, endSta);
                            object[] callArgs = new object[] { s, 0.0, 0.0, 0.0 };
                            try
                            {
                                ptLoc.Invoke(obj, callArgs);
                                double e = Convert.ToDouble(callArgs[2]);
                                double n = Convert.ToDouble(callArgs[3]);
                                rows.Add(string.Join(",",
                                    Csv(name),
                                    s.ToString("0.###", CultureInfo.InvariantCulture),
                                    e.ToString("0.####", CultureInfo.InvariantCulture),
                                    n.ToString("0.####", CultureInfo.InvariantCulture),
                                    Csv(obj.Handle.ToString())));
                                exported++;
                            }
                            catch { }
                            if (Math.Abs(s - endSta) < 1e-6) break;
                        }
                    }
                }
                tr.Commit();
            }

            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));
            return new JObject
            {
                ["operation"] = "alignments_csv",
                ["alignments_processed"] = alignments,
                ["station_interval"] = interval,
                ["rows_exported"] = exported,
                ["csv_path"] = csvPath
            };
        }

        private static JObject ExportPipeNetworkCsv(Document doc, object cdoc, string filter, int maxRows, string csvPath)
        {
            var rows = new List<string>
            {
                "network,kind,name,part_size,description,start_invert,end_invert,rim_elevation,sump_elevation,length_3d,slope_pct,inner_diameter,start_x,start_y,end_x,end_y,handle"
            };
            int exported = 0;

            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (Invoke0(cdoc, "GetPipeNetworkIds") is ObjectIdCollection netIds)
                {
                    foreach (ObjectId netId in netIds)
                    {
                        if (exported >= maxRows) break;
                        DBObject net = tr.GetObject(netId, OpenMode.ForRead);
                        string netName = GetProp(net, "Name") as string ?? "";
                        if (!Matches(netName, filter)) continue;

                        if (Invoke0(net, "GetPipeIds") is ObjectIdCollection pipeIds)
                        {
                            foreach (ObjectId pid in pipeIds)
                            {
                                if (exported >= maxRows) break;
                                DBObject pipe = tr.GetObject(pid, OpenMode.ForRead);
                                double innerDia = Convert.ToDouble(GetProp(pipe, "InnerDiameterOrWidth") ?? 0.0);
                                Point3d sp = GetProp(pipe, "StartPoint") is Point3d s ? s : Point3d.Origin;
                                Point3d ep = GetProp(pipe, "EndPoint") is Point3d e ? e : Point3d.Origin;
                                double slope = Convert.ToDouble(GetProp(pipe, "Slope") ?? 0.0) * 100.0;

                                rows.Add(string.Join(",",
                                    Csv(netName), "Pipe",
                                    Csv(GetProp(pipe, "Name") as string ?? ""),
                                    Csv(GetProp(pipe, "PartSizeName") as string ?? ""),
                                    Csv(GetProp(pipe, "Description") as string ?? ""),
                                    (sp.Z - innerDia * 0.5).ToString("0.####", CultureInfo.InvariantCulture),
                                    (ep.Z - innerDia * 0.5).ToString("0.####", CultureInfo.InvariantCulture),
                                    "", "",
                                    FmtNum(GetProp(pipe, "Length3DCenterToCenter")),
                                    slope.ToString("0.####", CultureInfo.InvariantCulture),
                                    innerDia.ToString("0.####", CultureInfo.InvariantCulture),
                                    sp.X.ToString("0.####", CultureInfo.InvariantCulture),
                                    sp.Y.ToString("0.####", CultureInfo.InvariantCulture),
                                    ep.X.ToString("0.####", CultureInfo.InvariantCulture),
                                    ep.Y.ToString("0.####", CultureInfo.InvariantCulture),
                                    Csv(pipe.Handle.ToString())));
                                exported++;
                            }
                        }

                        if (Invoke0(net, "GetStructureIds") is ObjectIdCollection stIds)
                        {
                            foreach (ObjectId sid in stIds)
                            {
                                if (exported >= maxRows) break;
                                DBObject st = tr.GetObject(sid, OpenMode.ForRead);
                                Point3d loc = GetProp(st, "Location") is Point3d p ? p : Point3d.Origin;

                                rows.Add(string.Join(",",
                                    Csv(netName), "Structure",
                                    Csv(GetProp(st, "Name") as string ?? ""),
                                    Csv(GetProp(st, "PartSizeName") as string ?? ""),
                                    Csv(GetProp(st, "Description") as string ?? ""),
                                    "", "",
                                    FmtNum(GetProp(st, "RimElevation")),
                                    FmtNum(GetProp(st, "SumpElevation")),
                                    "", "",
                                    FmtNum(GetProp(st, "InnerDiameterOrWidth")),
                                    loc.X.ToString("0.####", CultureInfo.InvariantCulture),
                                    loc.Y.ToString("0.####", CultureInfo.InvariantCulture),
                                    "", "",
                                    Csv(st.Handle.ToString())));
                                exported++;
                            }
                        }
                    }
                }
                tr.Commit();
            }

            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));
            return new JObject { ["operation"] = "pipe_network_csv", ["rows_exported"] = exported, ["csv_path"] = csvPath };
        }

        private static JObject ExportSurfaceGridCsv(Document doc, object cdoc, string surfaceName, double spacing, int maxRows, string csvPath)
        {
            if (string.IsNullOrEmpty(surfaceName))
                throw new ArgumentException("surface_grid_csv requires 'surface_name'.");

            var rows = new List<string> { "surface_name,easting,northing,elevation" };
            int sampled = 0;

            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                DBObject target = null;
                if (Invoke0(cdoc, "GetSurfaceIds") is ObjectIdCollection ids)
                {
                    foreach (ObjectId id in ids)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (string.Equals(GetProp(obj, "Name") as string, surfaceName, StringComparison.OrdinalIgnoreCase))
                        {
                            target = obj;
                            break;
                        }
                    }
                }
                if (target == null) throw new InvalidOperationException("Surface '" + surfaceName + "' not found.");

                Entity ent = target as Entity;
                Extents3d ext = ent.GeometricExtents;
                MethodInfo findElev = target.GetType().GetMethod("FindElevationAtXY", new[] { typeof(double), typeof(double) });
                if (findElev == null) throw new InvalidOperationException("FindElevationAtXY not available on this surface.");

                for (double x = ext.MinPoint.X; x <= ext.MaxPoint.X && sampled < maxRows; x += spacing)
                {
                    for (double y = ext.MinPoint.Y; y <= ext.MaxPoint.Y && sampled < maxRows; y += spacing)
                    {
                        try
                        {
                            double z = Convert.ToDouble(findElev.Invoke(target, new object[] { x, y }));
                            rows.Add(string.Join(",",
                                Csv(surfaceName),
                                x.ToString("0.###", CultureInfo.InvariantCulture),
                                y.ToString("0.###", CultureInfo.InvariantCulture),
                                z.ToString("0.####", CultureInfo.InvariantCulture)));
                            sampled++;
                        }
                        catch { /* point outside TIN hull */ }
                    }
                }
                tr.Commit();
            }

            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));
            return new JObject
            {
                ["operation"] = "surface_grid_csv",
                ["surface_name"] = surfaceName,
                ["grid_spacing"] = spacing,
                ["rows_exported"] = sampled,
                ["csv_path"] = csvPath
            };
        }

        private static JObject ExportLandXml(Document doc, object cdoc, JObject args, string xmlPath)
        {
            bool incSurfaces = (bool?)args["include_surfaces"] ?? true;
            bool incAlignments = (bool?)args["include_alignments"] ?? true;
            bool incCogo = (bool?)args["include_cogo_points"] ?? true;
            bool incPipes = (bool?)args["include_pipe_networks"] ?? true;
            string filter = ((string)args["name_filter"] ?? "").Trim();

            XNamespace ns = "http://www.landxml.org/schema/LandXML-1.2";
            var root = new XElement(ns + "LandXML",
                new XAttribute("version", "1.2"),
                new XAttribute("date", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                new XAttribute("time", DateTime.UtcNow.ToString("HH:mm:ss")),
                new XElement(ns + "Units",
                    new XElement(ns + "Metric",
                        new XAttribute("areaUnit", "squareMeter"),
                        new XAttribute("linearUnit", "meter"),
                        new XAttribute("volumeUnit", "cubicMeter"),
                        new XAttribute("temperatureUnit", "celsius"),
                        new XAttribute("pressureUnit", "milliBars"))),
                new XElement(ns + "Application",
                    new XAttribute("name", "Sutra Civil 3D MCP"),
                    new XAttribute("manufacturer", "SCIO DC"),
                    new XAttribute("version", "1.0.0")));

            int cogoCount = 0, surfCount = 0, alignCount = 0, netCount = 0;

            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (incCogo && GetProp(cdoc, "CogoPoints") is IEnumerable cogoIds)
                {
                    var cgPoints = new XElement(ns + "CgPoints");
                    foreach (object raw in cogoIds)
                    {
                        if (!(raw is ObjectId id)) continue;
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        string desc = GetProp(obj, "RawDescription") as string ?? "";
                        string pName = GetProp(obj, "PointName") as string ?? "";
                        if (!string.IsNullOrEmpty(filter) && !Matches(pName, filter) && !Matches(desc, filter)) continue;

                        double n = Convert.ToDouble(GetProp(obj, "Northing") ?? 0.0);
                        double e = Convert.ToDouble(GetProp(obj, "Easting") ?? 0.0);
                        double z = Convert.ToDouble(GetProp(obj, "Elevation") ?? 0.0);
                        string num = FmtLong(GetProp(obj, "PointNumber"));

                        cgPoints.Add(new XElement(ns + "CgPoint",
                            new XAttribute("name", string.IsNullOrEmpty(pName) ? num : pName),
                            new XAttribute("desc", desc),
                            string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####}", n, e, z)));
                        cogoCount++;
                    }
                    if (cogoCount > 0) root.Add(cgPoints);
                }

                if (incSurfaces && Invoke0(cdoc, "GetSurfaceIds") is ObjectIdCollection surfIds)
                {
                    var surfacesEl = new XElement(ns + "Surfaces");
                    foreach (ObjectId id in surfIds)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        string name = GetProp(obj, "Name") as string ?? "";
                        if (!Matches(name, filter)) continue;

                        var pntsEl = new XElement(ns + "Pnts");
                        var facesEl = new XElement(ns + "Faces");
                        var vertIndex = new Dictionary<object, int>();
                        int idx = 1;

                        if (GetProp(obj, "Vertices") is IEnumerable verts)
                        {
                            foreach (object v in verts)
                            {
                                if (GetProp(v, "Location") is Point3d loc)
                                {
                                    vertIndex[v] = idx;
                                    pntsEl.Add(new XElement(ns + "P",
                                        new XAttribute("id", idx),
                                        string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####} {2:0.####}", loc.Y, loc.X, loc.Z)));
                                    idx++;
                                }
                            }
                        }

                        if (GetProp(obj, "Triangles") is IEnumerable tris)
                        {
                            foreach (object tri in tris)
                            {
                                object v1 = GetProp(tri, "Vertex1");
                                object v2 = GetProp(tri, "Vertex2");
                                object v3 = GetProp(tri, "Vertex3");
                                if (v1 != null && v2 != null && v3 != null &&
                                    vertIndex.TryGetValue(v1, out int i1) &&
                                    vertIndex.TryGetValue(v2, out int i2) &&
                                    vertIndex.TryGetValue(v3, out int i3))
                                {
                                    facesEl.Add(new XElement(ns + "F", i1 + " " + i2 + " " + i3));
                                }
                            }
                        }

                        surfacesEl.Add(new XElement(ns + "Surface",
                            new XAttribute("name", name),
                            new XElement(ns + "Definition",
                                new XAttribute("surfType", "TIN"),
                                pntsEl, facesEl)));
                        surfCount++;
                    }
                    if (surfCount > 0) root.Add(surfacesEl);
                }

                if (incAlignments && Invoke0(cdoc, "GetAlignmentIds") is ObjectIdCollection alignIds)
                {
                    var alignsEl = new XElement(ns + "Alignments");
                    foreach (ObjectId id in alignIds)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        string name = GetProp(obj, "Name") as string ?? "";
                        if (!Matches(name, filter)) continue;

                        double startSta = Convert.ToDouble(GetProp(obj, "StartingStation") ?? 0.0);
                        double endSta = Convert.ToDouble(GetProp(obj, "EndingStation") ?? 0.0);
                        double length = Convert.ToDouble(GetProp(obj, "Length") ?? 0.0);

                        var alEl = new XElement(ns + "Alignment",
                            new XAttribute("name", name),
                            new XAttribute("staStart", startSta.ToString("0.###", CultureInfo.InvariantCulture)),
                            new XAttribute("length", length.ToString("0.###", CultureInfo.InvariantCulture)));

                        MethodInfo ptLoc = FindPointLocationMethod(obj.GetType());
                        if (ptLoc != null && endSta > startSta)
                        {
                            var coordGeom = new XElement(ns + "CoordGeom");
                            double step = Math.Max(5.0, length / 200.0);
                            double prevN = 0, prevE = 0;
                            bool hasPrev = false;
                            for (double sta = startSta; sta <= endSta + 1e-6; sta += step)
                            {
                                double s = Math.Min(sta, endSta);
                                object[] callArgs = new object[] { s, 0.0, 0.0, 0.0 };
                                try
                                {
                                    ptLoc.Invoke(obj, callArgs);
                                    double e = Convert.ToDouble(callArgs[2]);
                                    double n = Convert.ToDouble(callArgs[3]);
                                    if (hasPrev)
                                    {
                                        coordGeom.Add(new XElement(ns + "Line",
                                            new XElement(ns + "Start", string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####}", prevN, prevE)),
                                            new XElement(ns + "End", string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####}", n, e))));
                                    }
                                    prevN = n; prevE = e; hasPrev = true;
                                }
                                catch { }
                                if (Math.Abs(s - endSta) < 1e-6) break;
                            }
                            alEl.Add(coordGeom);
                        }

                        alignsEl.Add(alEl);
                        alignCount++;
                    }
                    if (alignCount > 0) root.Add(alignsEl);
                }

                if (incPipes && Invoke0(cdoc, "GetPipeNetworkIds") is ObjectIdCollection netIds)
                {
                    var netsEl = new XElement(ns + "PipeNetworks");
                    foreach (ObjectId netId in netIds)
                    {
                        DBObject net = tr.GetObject(netId, OpenMode.ForRead);
                        string netName = GetProp(net, "Name") as string ?? "";
                        if (!Matches(netName, filter)) continue;

                        var structsEl = new XElement(ns + "Structs");
                        var pipesEl = new XElement(ns + "Pipes");

                        if (Invoke0(net, "GetStructureIds") is ObjectIdCollection stIds)
                        {
                            foreach (ObjectId sid in stIds)
                            {
                                DBObject st = tr.GetObject(sid, OpenMode.ForRead);
                                Point3d loc = GetProp(st, "Location") is Point3d p ? p : Point3d.Origin;
                                structsEl.Add(new XElement(ns + "Struct",
                                    new XAttribute("name", GetProp(st, "Name") as string ?? st.Handle.ToString()),
                                    new XAttribute("elevRim", FmtNum(GetProp(st, "RimElevation"))),
                                    new XAttribute("elevSump", FmtNum(GetProp(st, "SumpElevation"))),
                                    new XElement(ns + "Center", string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####}", loc.Y, loc.X))));
                            }
                        }

                        if (Invoke0(net, "GetPipeIds") is ObjectIdCollection pipeIds)
                        {
                            foreach (ObjectId pid in pipeIds)
                            {
                                DBObject pipe = tr.GetObject(pid, OpenMode.ForRead);
                                double dia = Convert.ToDouble(GetProp(pipe, "InnerDiameterOrWidth") ?? 0.0);
                                pipesEl.Add(new XElement(ns + "Pipe",
                                    new XAttribute("name", GetProp(pipe, "Name") as string ?? pipe.Handle.ToString()),
                                    new XAttribute("length", FmtNum(GetProp(pipe, "Length3DCenterToCenter"))),
                                    new XAttribute("slope", FmtNum(GetProp(pipe, "Slope"))),
                                    new XElement(ns + "CircPipe",
                                        new XAttribute("diameter", dia.ToString("0.####", CultureInfo.InvariantCulture)))));
                            }
                        }

                        netsEl.Add(new XElement(ns + "PipeNetwork",
                            new XAttribute("name", netName),
                            structsEl, pipesEl));
                        netCount++;
                    }
                    if (netCount > 0) root.Add(netsEl);
                }

                tr.Commit();
            }

            var xdoc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            xdoc.Save(xmlPath);

            return new JObject
            {
                ["operation"] = "landxml",
                ["xml_path"] = xmlPath,
                ["cogo_points_exported"] = cogoCount,
                ["surfaces_exported"] = surfCount,
                ["alignments_exported"] = alignCount,
                ["pipe_networks_exported"] = netCount
            };
        }

        // ---- Reflection & formatting helpers ----------------------------------

        private static object GetCivilDocument()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(asm.GetName().Name, "AeccDbMgd", StringComparison.OrdinalIgnoreCase)) continue;
                Type appType = asm.GetType("Autodesk.Civil.ApplicationServices.CivilApplication", false);
                return appType?.GetProperty("ActiveDocument", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
            return null;
        }

        private static MethodInfo FindPointLocationMethod(Type alignType)
        {
            foreach (MethodInfo m in alignType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "PointLocation" && m.GetParameters().Length == 4) return m;
            }
            return null;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj); }
            catch { return null; }
        }

        private static object Invoke0(object obj, string name)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(obj, null); }
            catch { return null; }
        }

        private static bool Matches(string val, string filter)
            => string.IsNullOrEmpty(filter) || (val != null && val.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string FmtNum(object v)
        {
            if (v == null) return "";
            try { return Convert.ToDouble(v).ToString("0.####", CultureInfo.InvariantCulture); }
            catch { return ""; }
        }

        private static string FmtLong(object v)
        {
            if (v == null) return "";
            try { return Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture); }
            catch { return ""; }
        }

        private static string Csv(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "drawing";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
