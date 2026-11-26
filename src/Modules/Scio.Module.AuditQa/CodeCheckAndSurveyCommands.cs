using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.AuditQa
{
    [ScioTool("scio_code_check", "audit", "Read-only building code readiness checks: headroom, evacuation travel_distance, energy_readiness, or combined check.", ReadOnly = true)]
    public sealed class CodeCheckCommand : IRevitCommand
    {
        [ScioParam("operation", "Check mode: check (all), headroom, travel_distance, or energy_readiness.", Required = true, EnumValues = new[] { "check", "headroom", "travel_distance", "energy_readiness" })]
        public string Operation { get; set; } = "check";

        [ScioParam("min_headroom_mm", "Minimum clear headroom in mm (default 2100).")]
        public double MinHeadroomMm { get; set; } = 2100.0;

        [ScioParam("max_travel_m", "Maximum evacuation travel distance in meters (default 30.0).")]
        public double MaxTravelM { get; set; } = 30.0;

        [ScioParam("exit_keyword", "Regex pattern to identify exit doors by Mark/Type/Comments (default 'exit|salida|evac').")]
        public string ExitKeyword { get; set; } = "exit|salida|evac";

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(CodeCheckCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "check").Trim().ToLowerInvariant();
            double minHeadroomMm = Math.Max(1500.0, request.Arguments.Value<double?>("min_headroom_mm") ?? 2100.0);
            double maxTravelM = Math.Max(5.0, request.Arguments.Value<double?>("max_travel_m") ?? 30.0);
            string exitPattern = request.Arguments.Value<string>("exit_keyword") ?? "exit|salida|evac";

            if (op == "headroom")
                return ToolResponse.Success(request.RequestId, CheckHeadroom(doc, minHeadroomMm), "Headroom check completed.");
            if (op == "travel_distance")
                return ToolResponse.Success(request.RequestId, CheckTravelDistance(doc, maxTravelM, exitPattern), "Travel distance check completed.");
            if (op == "energy_readiness")
                return ToolResponse.Success(request.RequestId, CheckEnergyReadiness(doc), "Energy readiness check completed.");

            var hr = CheckHeadroom(doc, minHeadroomMm);
            var td = CheckTravelDistance(doc, maxTravelM, exitPattern);
            var en = CheckEnergyReadiness(doc);
            int totalFindings = hr.Value<int>("violation_count") + td.Value<int>("violation_count") + en.Value<int>("issue_count");
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["operation"] = "check",
                ["total_findings"] = totalFindings,
                ["headroom"] = hr,
                ["travel_distance"] = td,
                ["energy_readiness"] = en
            }, $"Code check completed: {totalFindings} total finding(s).");
        }

        private static JObject CheckHeadroom(Document doc, double minHeadroomMm)
        {
            var violations = new JArray();
            var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().ToList();
            foreach (var d in doors)
            {
                var hp = d.get_Parameter(BuiltInParameter.DOOR_HEIGHT)
                         ?? doc.GetElement(d.GetTypeId())?.get_Parameter(BuiltInParameter.DOOR_HEIGHT)
                         ?? d.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM);
                if (hp == null || !hp.HasValue) continue;
                double hMm = Math.Round(UnitKit.FeetToMm(hp.AsDouble()), 1);
                if (hMm > 10.0 && hMm < minHeadroomMm)
                {
                    violations.Add(new JObject
                    {
                        ["id"] = IdCompat.ToInt64(d.Id),
                        ["category"] = "Doors",
                        ["name"] = d.Name,
                        ["measured_mm"] = hMm,
                        ["required_min_mm"] = minHeadroomMm
                    });
                }
            }
            return new JObject { ["min_headroom_mm"] = minHeadroomMm, ["doors_checked"] = doors.Count, ["violation_count"] = violations.Count, ["violations"] = violations };
        }

        private static JObject CheckTravelDistance(Document doc, double maxTravelM, string exitPattern)
        {
            var rx = new Regex(exitPattern, RegexOptions.IgnoreCase);
            var allDoors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().ToList();
            var exitDoors = allDoors.Where(d =>
            {
                string blob = $"{d.Name} {d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()} {d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()}";
                return rx.IsMatch(blob);
            }).ToList();
            if (exitDoors.Count == 0) exitDoors = allDoors; // fallback to all doors if none explicitly tagged

            var exitPts = exitDoors.Select(d => (d.Location as LocationPoint)?.Point).Where(p => p != null).Cast<XYZ>().ToList();
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>().Where(r => r.Area > 1e-4).ToList();

            var violations = new JArray();
            foreach (var r in rooms)
            {
                var rp = (r.Location as LocationPoint)?.Point;
                if (rp == null || exitPts.Count == 0) continue;
                double nearestFt = exitPts.Min(ep => Math.Abs(ep.X - rp.X) + Math.Abs(ep.Y - rp.Y)); // Manhattan approximation
                double distM = Math.Round(UnitKit.FeetToMm(nearestFt) / 1000.0, 2);
                if (distM > maxTravelM)
                {
                    violations.Add(new JObject
                    {
                        ["room_id"] = IdCompat.ToInt64(r.Id),
                        ["room_name"] = $"{r.Number} {r.Name}".Trim(),
                        ["manhattan_distance_m"] = distM,
                        ["max_allowed_m"] = maxTravelM
                    });
                }
            }
            return new JObject { ["max_travel_m"] = maxTravelM, ["exit_doors_found"] = exitPts.Count, ["rooms_checked"] = rooms.Count, ["violation_count"] = violations.Count, ["violations"] = violations };
        }

        private static JObject CheckEnergyReadiness(Document doc)
        {
            var issues = new JArray();
            int unplacedRooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>().Count(r => r.Area <= 1e-4);
            if (unplacedRooms > 0) issues.Add(new JObject { ["check"] = "unplaced_or_unbounded_rooms", ["count"] = unplacedRooms });

            int wallTypesNoThermal = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Basic)
                .Count(wt => wt.ThermalProperties == null || wt.ThermalProperties.ThermalResistance <= 1e-6);
            if (wallTypesNoThermal > 0) issues.Add(new JObject { ["check"] = "wall_types_missing_thermal_resistance", ["count"] = wallTypesNoThermal });

            var site = doc.SiteLocation;
            if (site == null || (Math.Abs(site.Latitude) < 1e-6 && Math.Abs(site.Longitude) < 1e-6))
                issues.Add(new JObject { ["check"] = "site_location_unset", ["count"] = 1 });

            return new JObject { ["issue_count"] = issues.Count, ["issues"] = issues };
        }
    }

    [ScioTool("scio_change_survey", "audit", "Capture a lightweight local model fingerprint snapshot or diff the live model against the last snapshot.", ReadOnly = true)]
    public sealed class ChangeSurveyCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: snapshot or diff (default diff).", EnumValues = new[] { "snapshot", "diff" })]
        public string Action { get; set; } = "diff";

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(ChangeSurveyCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string action = (request.Arguments.Value<string>("action") ?? "diff").Trim().ToLowerInvariant();
            string snapDir = Path.Combine(ScioRuntimePaths.DataRoot, "snapshots");
            Directory.CreateDirectory(snapDir);
            string docKey = Regex.Replace(doc.Title ?? "untitled", @"[^A-Za-z0-9_-]+", "_");
            string snapPath = Path.Combine(snapDir, docKey + ".json");

            var current = CaptureFingerprint(doc);
            if (action == "snapshot" || !File.Exists(snapPath))
            {
                File.WriteAllText(snapPath, current.ToString(Formatting.Indented), new UTF8Encoding(false));
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["action"] = "snapshot",
                    ["snapshot_path"] = snapPath,
                    ["fingerprint"] = current
                }, $"Saved change-survey snapshot for '{doc.Title}'.");
            }

            var prev = JObject.Parse(File.ReadAllText(snapPath, Encoding.UTF8));
            var prevCats = prev["by_category"] as JObject ?? new JObject();
            var currCats = current["by_category"] as JObject ?? new JObject();
            var allKeys = prevCats.Properties().Select(p => p.Name).Union(currCats.Properties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase).OrderBy(k => k);

            var deltas = new JArray();
            foreach (string k in allKeys)
            {
                int a = prevCats.Value<int?>(k) ?? 0;
                int b = currCats.Value<int?>(k) ?? 0;
                if (a != b)
                    deltas.Add(new JObject { ["category"] = k, ["before"] = a, ["after"] = b, ["delta"] = b - a });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["action"] = "diff",
                ["baseline_utc"] = prev["captured_utc"],
                ["current_utc"] = current["captured_utc"],
                ["warnings_before"] = prev["warning_count"],
                ["warnings_after"] = current["warning_count"],
                ["category_changes"] = deltas
            }, $"Change survey diff: {deltas.Count} category count change(s) since baseline.");
        }

        private static JObject CaptureFingerprint(Document doc)
        {
            var byCat = new JObject();
            var elems = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .ToList();
            foreach (var g in elems.GroupBy(e => e.Category!.Name).OrderBy(g => g.Key))
                byCat[g.Key] = g.Count();

            return new JObject
            {
                ["document"] = doc.Title,
                ["captured_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["model_elements"] = elems.Count,
                ["warning_count"] = doc.GetWarnings().Count,
                ["by_category"] = byCat
            };
        }
    }
}
