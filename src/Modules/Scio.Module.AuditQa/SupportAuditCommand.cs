using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.AuditQa
{
    [ScioTool("scio_audit_support", "audit", "Audit gravity and structural support: floating elements without support below, unsupported structural beam ends, and placed rooms without floor slabs.", ReadOnly = true)]
    public sealed class AuditSupportCommand : IRevitCommand
    {
        [ScioParam("tolerance_mm", "Vertical/horizontal contact tolerance in mm (5-1000, default 50).")]
        public double ToleranceMm { get; set; } = 50.0;

        [ScioParam("max_findings", "Maximum findings per bucket (10-500, default 100).")]
        public int MaxFindings { get; set; } = 100;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(AuditSupportCommand));

        private sealed class SupportBox
        {
            public long Id;
            public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        }

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            Document doc = context.RequireActiveDocument(request.TargetDocument);
            double tolMm = Math.Max(5.0, Math.Min(1000.0, request.Arguments.Value<double?>("tolerance_mm") ?? 50.0));
            double tolFt = tolMm / 304.8;
            int maxFindings = Math.Max(10, Math.Min(500, request.Arguments.Value<int?>("max_findings") ?? 100));

            var slabs = CollectBoxes(doc, BuiltInCategory.OST_Floors);
            slabs.AddRange(CollectBoxes(doc, BuiltInCategory.OST_StructuralFoundation));

            var verticals = CollectBoxes(doc, BuiltInCategory.OST_StructuralColumns);
            verticals.AddRange(CollectBoxes(doc, BuiltInCategory.OST_Columns));
            verticals.AddRange(CollectBoxes(doc, BuiltInCategory.OST_Walls));

            var beams = CollectBoxes(doc, BuiltInCategory.OST_StructuralFraming);
            var allSupports = new List<SupportBox>(slabs.Count + verticals.Count + beams.Count);
            allSupports.AddRange(slabs);
            allSupports.AddRange(verticals);
            allSupports.AddRange(beams);

            var levelZ = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).OrderBy(z => z).ToList();
            double lowestZ = levelZ.Count > 0 ? levelZ[0] : 0.0;

            var floating = new JArray();
            int floatingTotal = 0;
            var targets = new List<Element>();
            targets.AddRange(CollectInstances(doc, BuiltInCategory.OST_StructuralColumns));
            targets.AddRange(CollectInstances(doc, BuiltInCategory.OST_Walls));
            targets.AddRange(CollectInstances(doc, BuiltInCategory.OST_MechanicalEquipment));
            targets.AddRange(CollectInstances(doc, BuiltInCategory.OST_PlumbingFixtures));

            foreach (Element e in targets)
            {
                BoundingBoxXYZ? bb = e.get_BoundingBox(null);
                if (bb == null || bb.Min.Z <= lowestZ + tolFt) continue;
                long eid = IdCompat.ToInt64(e.Id);
                if (!HasSupportBelow(bb, eid, allSupports, tolFt, out double gapFt))
                {
                    floatingTotal++;
                    if (floating.Count < maxFindings)
                    {
                        floating.Add(new JObject
                        {
                            ["element_id"] = eid,
                            ["category"] = e.Category?.Name ?? string.Empty,
                            ["name"] = e.Name ?? string.Empty,
                            ["gap_mm"] = double.IsInfinity(gapFt) ? (JToken)JValue.CreateNull() : Math.Round(gapFt * 304.8, 1)
                        });
                    }
                }
            }

            var beamSupports = new List<SupportBox>(verticals.Count + beams.Count);
            beamSupports.AddRange(verticals);
            beamSupports.AddRange(beams);

            var beamEnds = new JArray();
            int beamEndTotal = 0;
            foreach (Element beam in CollectInstances(doc, BuiltInCategory.OST_StructuralFraming))
            {
                if (beam.Location is not LocationCurve lc || lc.Curve == null) continue;
                long eid = IdCompat.ToInt64(beam.Id);
                bool s0 = Touches(lc.Curve.GetEndPoint(0), eid, beamSupports, tolFt);
                bool s1 = Touches(lc.Curve.GetEndPoint(1), eid, beamSupports, tolFt);
                if (!s0 || !s1)
                {
                    beamEndTotal++;
                    if (beamEnds.Count < maxFindings)
                    {
                        var free = new JArray();
                        if (!s0) free.Add("start");
                        if (!s1) free.Add("end");
                        beamEnds.Add(new JObject { ["element_id"] = eid, ["name"] = beam.Name ?? string.Empty, ["unsupported_ends"] = free });
                    }
                }
            }

            var roomsNoSlab = new JArray();
            int roomTotal = 0;
            foreach (Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().Where(x => x.Area > 1e-4 && x.Location is LocationPoint))
            {
                XYZ pt = ((LocationPoint)r.Location).Point;
                double rz = r.Level != null ? r.Level.Elevation : pt.Z;
                bool hasSlab = slabs.Any(s => pt.X >= s.MinX - tolFt && pt.X <= s.MaxX + tolFt && pt.Y >= s.MinY - tolFt && pt.Y <= s.MaxY + tolFt && s.MaxZ >= rz - tolFt * 4 && s.MinZ <= rz + tolFt);
                if (!hasSlab)
                {
                    roomTotal++;
                    if (roomsNoSlab.Count < maxFindings)
                    {
                        roomsNoSlab.Add(new JObject { ["element_id"] = IdCompat.ToInt64(r.Id), ["number"] = r.Number ?? string.Empty, ["name"] = r.Name ?? string.Empty });
                    }
                }
            }

            int total = floatingTotal + beamEndTotal + roomTotal;
            var data = new JObject
            {
                ["document"] = doc.Title,
                ["tolerance_mm"] = tolMm,
                ["total_findings"] = total,
                ["clean"] = total == 0,
                ["counts"] = new JObject { ["floating_elements"] = floatingTotal, ["unsupported_beam_ends"] = beamEndTotal, ["rooms_without_slab"] = roomTotal },
                ["floating_elements"] = floating,
                ["unsupported_beam_ends"] = beamEnds,
                ["rooms_without_slab"] = roomsNoSlab
            };
            return ToolResponse.Success(request.RequestId, data, $"Support audit: {total} finding(s) ({floatingTotal} floating, {beamEndTotal} beam ends, {roomTotal} rooms without slab).");
        }

        private static List<Element> CollectInstances(Document doc, BuiltInCategory bic)
            => new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements().ToList();

        private static List<SupportBox> CollectBoxes(Document doc, BuiltInCategory bic)
        {
            var list = new List<SupportBox>();
            foreach (Element e in CollectInstances(doc, bic))
            {
                BoundingBoxXYZ? bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                list.Add(new SupportBox { Id = IdCompat.ToInt64(e.Id), MinX = bb.Min.X, MaxX = bb.Max.X, MinY = bb.Min.Y, MaxY = bb.Max.Y, MinZ = bb.Min.Z, MaxZ = bb.Max.Z });
            }
            return list;
        }

        private static bool HasSupportBelow(BoundingBoxXYZ bb, long selfId, List<SupportBox> supports, double tolFt, out double nearestGapFt)
        {
            nearestGapFt = double.PositiveInfinity;
            foreach (SupportBox s in supports)
            {
                if (s.Id == selfId) continue;
                if (bb.Min.X > s.MaxX + tolFt || bb.Max.X < s.MinX - tolFt || bb.Min.Y > s.MaxY + tolFt || bb.Max.Y < s.MinY - tolFt) continue;
                if (s.MaxZ >= bb.Min.Z - tolFt && s.MinZ <= bb.Min.Z + tolFt) { nearestGapFt = 0; return true; }
                if (s.MaxZ < bb.Min.Z) nearestGapFt = Math.Min(nearestGapFt, bb.Min.Z - s.MaxZ);
            }
            return false;
        }

        private static bool Touches(XYZ p, long selfId, List<SupportBox> supports, double tolFt)
        {
            double xy = Math.Max(tolFt, 150.0 / 304.8);
            return supports.Any(s => s.Id != selfId && p.X >= s.MinX - xy && p.X <= s.MaxX + xy && p.Y >= s.MinY - xy && p.Y <= s.MaxY + xy && p.Z >= s.MinZ - tolFt && p.Z <= s.MaxZ + xy);
        }
    }
}
