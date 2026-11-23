using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Mep
{
    internal static class MepKit
    {
        public static XYZ ParseMm(JToken? tok)
        {
            if (tok is JArray a && a.Count >= 3)
                return new XYZ(UnitKit.MmToFeet(a[0].Value<double>()), UnitKit.MmToFeet(a[1].Value<double>()), UnitKit.MmToFeet(a[2].Value<double>()));
            throw new ArgumentException("3D point [x,y,z] in mm is required.");
        }

        public static Level ResolveLevel(Document doc, string? name)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            if (levels.Count == 0) throw new InvalidOperationException("No levels in document.");
            if (string.IsNullOrWhiteSpace(name)) return levels[0];
            return levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Level '{name}' not found.");
        }
    }

    [ScioTool("scio_mep_create_pipe", "mep", "Create a Pipe between two 3D mm points on a level with specified diameter (mm), system type, and pipe type.", ReadOnly = false)]
    public sealed class MepCreatePipeCommand : IRevitCommand
    {
        [ScioParam("start_mm", "Start point [x,y,z] in mm.", Required = true)]
        public List<double> StartMm { get; set; } = new List<double>();

        [ScioParam("end_mm", "End point [x,y,z] in mm.", Required = true)]
        public List<double> EndMm { get; set; } = new List<double>();

        [ScioParam("diameter_mm", "Nominal pipe diameter in mm (default 50).")]
        public double DiameterMm { get; set; } = 50;

        [ScioParam("level", "Optional level name.")]
        public string LevelName { get; set; } = string.Empty;

        [ScioParam("system_type", "Optional PipingSystemType name.")]
        public string SystemType { get; set; } = string.Empty;

        [ScioParam("pipe_type", "Optional PipeType name.")]
        public string PipeTypeName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MepCreatePipeCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var p0 = MepKit.ParseMm(request.Arguments["start_mm"]);
            var p1 = MepKit.ParseMm(request.Arguments["end_mm"]);
            var lvl = MepKit.ResolveLevel(doc, request.Arguments.Value<string>("level"));
            string? sysName = request.Arguments.Value<string>("system_type");
            string? ptName = request.Arguments.Value<string>("pipe_type");

            var sysType = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>()
                .FirstOrDefault(s => string.IsNullOrWhiteSpace(sysName) || string.Equals(s.Name, sysName, StringComparison.OrdinalIgnoreCase));
            var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>()
                .FirstOrDefault(t => string.IsNullOrWhiteSpace(ptName) || string.Equals(t.Name, ptName, StringComparison.OrdinalIgnoreCase));
            if (sysType == null || pipeType == null)
                return ToolResponse.Refusal(request.RequestId, "missing_mep_type", "Document lacks PipingSystemType or PipeType.");

            double diamMm = request.Arguments.Value<double?>("diameter_mm") ?? 50.0;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Pipe",
                dryRun,
                d =>
                {
                    var pipe = Pipe.Create(d, sysType.Id, pipeType.Id, lvl.Id, p0, p1);
                    var dp = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    if (dp != null && !dp.IsReadOnly) dp.Set(UnitKit.MmToFeet(diamMm));
                    return new JObject
                    {
                        ["id"] = IdCompat.ToInt64(pipe.Id),
                        ["system_type"] = sysType.Name,
                        ["pipe_type"] = pipeType.Name,
                        ["diameter_mm"] = diamMm,
                        ["length_mm"] = Math.Round(UnitKit.FeetToMm(p0.DistanceTo(p1)), 1)
                    };
                },
                (d, staged) => staged,
                $"Created pipe Ø{diamMm}mm.");
        }
    }

    [ScioTool("scio_mep_create_duct", "mep", "Create a Duct between two 3D mm points on a level with specified width/height or diameter (mm).", ReadOnly = false)]
    public sealed class MepCreateDuctCommand : IRevitCommand
    {
        [ScioParam("start_mm", "Start point [x,y,z] in mm.", Required = true)]
        public List<double> StartMm { get; set; } = new List<double>();

        [ScioParam("end_mm", "End point [x,y,z] in mm.", Required = true)]
        public List<double> EndMm { get; set; } = new List<double>();

        [ScioParam("width_mm", "Rectangular duct width in mm (default 400).")]
        public double WidthMm { get; set; } = 400;

        [ScioParam("height_mm", "Rectangular duct height in mm (default 250).")]
        public double HeightMm { get; set; } = 250;

        [ScioParam("level", "Optional level name.")]
        public string LevelName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MepCreateDuctCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var p0 = MepKit.ParseMm(request.Arguments["start_mm"]);
            var p1 = MepKit.ParseMm(request.Arguments["end_mm"]);
            var lvl = MepKit.ResolveLevel(doc, request.Arguments.Value<string>("level"));

            var sysType = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().FirstOrDefault();
            var ductType = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().FirstOrDefault();
            if (sysType == null || ductType == null)
                return ToolResponse.Refusal(request.RequestId, "missing_mep_type", "Document lacks MechanicalSystemType or DuctType.");

            double wMm = request.Arguments.Value<double?>("width_mm") ?? 400.0;
            double hMm = request.Arguments.Value<double?>("height_mm") ?? 250.0;
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                "Create Duct",
                dryRun,
                d =>
                {
                    var duct = Duct.Create(d, sysType.Id, ductType.Id, lvl.Id, p0, p1);
                    var wp = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                    var hp = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                    if (wp != null && !wp.IsReadOnly) wp.Set(UnitKit.MmToFeet(wMm));
                    if (hp != null && !hp.IsReadOnly) hp.Set(UnitKit.MmToFeet(hMm));
                    return new JObject
                    {
                        ["id"] = IdCompat.ToInt64(duct.Id),
                        ["duct_type"] = ductType.Name,
                        ["length_mm"] = Math.Round(UnitKit.FeetToMm(p0.DistanceTo(p1)), 1)
                    };
                },
                (d, staged) => staged,
                "Created duct.");
        }
    }

    [ScioTool("scio_mep_create_conduit_tray", "mep", "Create a CableTray or Conduit run between two 3D mm points on a level.", ReadOnly = false)]
    public sealed class MepCreateConduitTrayCommand : IRevitCommand
    {
        [ScioParam("kind", "Element kind: cable_tray or conduit.", Required = true, EnumValues = new[] { "cable_tray", "conduit" })]
        public string Kind { get; set; } = "cable_tray";

        [ScioParam("start_mm", "Start point [x,y,z] in mm.", Required = true)]
        public List<double> StartMm { get; set; } = new List<double>();

        [ScioParam("end_mm", "End point [x,y,z] in mm.", Required = true)]
        public List<double> EndMm { get; set; } = new List<double>();

        [ScioParam("level", "Optional level name.")]
        public string LevelName { get; set; } = string.Empty;

        [ScioParam("dry_run", "Preview without committing (default true).")]
        public bool DryRun { get; set; } = true;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MepCreateConduitTrayCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string kind = (request.Arguments.Value<string>("kind") ?? "cable_tray").Trim().ToLowerInvariant();
            var p0 = MepKit.ParseMm(request.Arguments["start_mm"]);
            var p1 = MepKit.ParseMm(request.Arguments["end_mm"]);
            var lvl = MepKit.ResolveLevel(doc, request.Arguments.Value<string>("level"));
            bool dryRun = request.DryRun ?? request.Arguments.Value<bool?>("dry_run") ?? true;

            return TransactionPipeline.RunVerified(
                doc,
                request.RequestId,
                $"Create {kind}",
                dryRun,
                d =>
                {
                    if (kind == "conduit")
                    {
                        var ct = new FilteredElementCollector(d).OfClass(typeof(ConduitType)).Cast<ConduitType>().FirstOrDefault()
                            ?? throw new InvalidOperationException("No ConduitType loaded.");
                        var c = Conduit.Create(d, ct.Id, p0, p1, lvl.Id);
                        return new JObject { ["id"] = IdCompat.ToInt64(c.Id), ["kind"] = kind, ["type"] = ct.Name };
                    }
                    var tt = new FilteredElementCollector(d).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().FirstOrDefault()
                        ?? throw new InvalidOperationException("No CableTrayType loaded.");
                    var tray = CableTray.Create(d, tt.Id, p0, p1, lvl.Id);
                    return new JObject { ["id"] = IdCompat.ToInt64(tray.Id), ["kind"] = kind, ["type"] = tt.Name };
                },
                (d, staged) => staged,
                $"Created {kind}.");
        }
    }

    [ScioTool("scio_mep_systems", "mep", "List MEP systems (Piping, Mechanical/HVAC, Electrical) with element counts and classifications.", ReadOnly = true)]
    public sealed class MepSystemsCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MepSystemsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var arr = new JArray();

            foreach (var sys in new FilteredElementCollector(doc).OfClass(typeof(MEPSystem)).Cast<MEPSystem>().OrderBy(s => s.Name))
            {
                arr.Add(new JObject
                {
                    ["id"] = IdCompat.ToInt64(sys.Id),
                    ["name"] = sys.Name,
                    ["kind"] = sys.GetType().Name,
                    ["element_count"] = sys.Elements?.Size ?? 0
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject { ["count"] = arr.Count, ["systems"] = arr }, $"Found {arr.Count} MEP system(s).");
        }
    }

    [ScioTool("scio_mep_connectors", "mep", "Audit open (unconnected) MEP connectors across pipes, ducts, cable trays, and conduits.", ReadOnly = true)]
    public sealed class MepConnectorsCommand : IRevitCommand
    {
        [ScioParam("limit", "Maximum open connectors to list (default 200).")]
        public int Limit { get; set; } = 200;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MepConnectorsCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            int limit = Math.Max(1, Math.Min(2000, request.Arguments.Value<int?>("limit") ?? 200));
            var open = new JArray();
            int totalOpen = 0;

            foreach (var curve in new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Cast<MEPCurve>())
            {
                var cm = curve.ConnectorManager;
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.IsConnected) continue;
                    totalOpen++;
                    if (open.Count < limit)
                    {
                        open.Add(new JObject
                        {
                            ["element_id"] = IdCompat.ToInt64(curve.Id),
                            ["category"] = curve.Category?.Name ?? string.Empty,
                            ["domain"] = c.Domain.ToString(),
                            ["origin_mm"] = new JArray(
                                Math.Round(UnitKit.FeetToMm(c.Origin.X), 1),
                                Math.Round(UnitKit.FeetToMm(c.Origin.Y), 1),
                                Math.Round(UnitKit.FeetToMm(c.Origin.Z), 1))
                        });
                    }
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["total_open_connectors"] = totalOpen,
                ["returned"] = open.Count,
                ["open_connectors"] = open
            }, $"Found {totalOpen} open MEP connector(s).");
        }
    }

    [ScioTool("scio_mep_slope_check", "mep", "Audit pipe slopes (%) against target minimum/maximum slope bounds.", ReadOnly = true)]
    public sealed class MepSlopeCheckCommand : IRevitCommand
    {
        [ScioParam("min_slope_pct", "Minimum expected slope in percent (default 1.0).")]
        public double MinSlopePct { get; set; } = 1.0;

        [ScioParam("system_filter", "Optional system name substring filter (e.g. Sanitary).")]
        public string SystemFilter { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MepSlopeCheckCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            double minPct = request.Arguments.Value<double?>("min_slope_pct") ?? 1.0;
            string sysFilter = request.Arguments.Value<string>("system_filter") ?? string.Empty;

            int checkedCount = 0;
            var flagged = new JArray();

            foreach (var pipe in new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>())
            {
                string sysName = pipe.MEPSystem?.Name ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sysFilter) && sysName.IndexOf(sysFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!(pipe.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;

                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                double dx = p1.X - p0.X, dy = p1.Y - p0.Y, dz = Math.Abs(p1.Z - p0.Z);
                double horiz = Math.Sqrt(dx * dx + dy * dy);
                if (horiz < 1e-4) continue; // vertical riser

                checkedCount++;
                double slopePct = (dz / horiz) * 100.0;
                if (slopePct < minPct && flagged.Count < 200)
                {
                    flagged.Add(new JObject
                    {
                        ["id"] = IdCompat.ToInt64(pipe.Id),
                        ["system"] = sysName,
                        ["slope_pct"] = Math.Round(slopePct, 2),
                        ["min_required_pct"] = minPct
                    });
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["checked_horizontal_pipes"] = checkedCount,
                ["flagged_count"] = flagged.Count,
                ["flagged"] = flagged
            }, $"Slope check: {flagged.Count} of {checkedCount} pipe(s) below {minPct}%.");
        }
    }

    [ScioTool("scio_mep_electrical", "mep", "Audit electrical circuits (panel, circuit number, apparent load, unassigned circuits).", ReadOnly = true)]
    public sealed class MepElectricalCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(MepElectricalCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>().ToList();
            var arr = new JArray();
            int unassigned = 0;

            foreach (var c in circuits.Take(300))
            {
                string panel = c.PanelName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(panel)) unassigned++;
                arr.Add(new JObject
                {
                    ["id"] = IdCompat.ToInt64(c.Id),
                    ["circuit_number"] = c.CircuitNumber ?? string.Empty,
                    ["panel"] = panel,
                    ["load_name"] = c.LoadName ?? string.Empty,
                    ["apparent_load_va"] = Math.Round(c.ApparentLoad, 1),
                    ["element_count"] = c.Elements?.Size ?? 0
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["circuit_count"] = circuits.Count,
                ["unassigned_count"] = unassigned,
                ["circuits"] = arr
            }, $"Electrical audit: {circuits.Count} circuit(s) ({unassigned} unassigned).");
        }
    }
}
