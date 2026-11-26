using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.Structure
{
    [ScioTool("scio_plan_structure", "structure", "Read-only structural planner: computes grid intersections for column placement and grid spans for beams.", ReadOnly = true)]
    public sealed class PlanStructureCommand : IRevitCommand
    {
        [ScioParam("operation", "Planning mode: columns_on_grid_intersections or beams_along_grids.", Required = true, EnumValues = new[] { "columns_on_grid_intersections", "beams_along_grids" })]
        public string Operation { get; set; } = "columns_on_grid_intersections";

        [ScioParam("tolerance_mm", "XY distance tolerance in mm to consider a column already present (default 250).")]
        public double ToleranceMm { get; set; } = 250.0;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(PlanStructureCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string op = (request.Arguments.Value<string>("operation") ?? "columns_on_grid_intersections").Trim().ToLowerInvariant();
            double tolMm = Math.Max(10.0, request.Arguments.Value<double?>("tolerance_mm") ?? 250.0);

            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => g.Curve is Line)
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var intersections = new List<(string GridA, string GridB, double Xmm, double Ymm)>();
            for (int i = 0; i < grids.Count; i++)
            {
                var l1 = (Line)grids[i].Curve;
                var p1 = new Point2D(UnitKit.FeetToMm(l1.GetEndPoint(0).X), UnitKit.FeetToMm(l1.GetEndPoint(0).Y));
                var p2 = new Point2D(UnitKit.FeetToMm(l1.GetEndPoint(1).X), UnitKit.FeetToMm(l1.GetEndPoint(1).Y));

                for (int j = i + 1; j < grids.Count; j++)
                {
                    var l2 = (Line)grids[j].Curve;
                    var p3 = new Point2D(UnitKit.FeetToMm(l2.GetEndPoint(0).X), UnitKit.FeetToMm(l2.GetEndPoint(0).Y));
                    var p4 = new Point2D(UnitKit.FeetToMm(l2.GetEndPoint(1).X), UnitKit.FeetToMm(l2.GetEndPoint(1).Y));

                    var hit = ConstructionMathKit.IntersectSegments2D(p1, p2, p3, p4);
                    if (hit != null)
                        intersections.Add((grids[i].Name, grids[j].Name, Math.Round(hit.X, 1), Math.Round(hit.Y, 1)));
                }
            }

            if (op == "columns_on_grid_intersections")
            {
                var colPts = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .WhereElementIsNotElementType()
                    .Select(c => (c.Location as LocationPoint)?.Point)
                    .Where(p => p != null)
                    .Select(p => (X: UnitKit.FeetToMm(p!.X), Y: UnitKit.FeetToMm(p.Y)))
                    .ToList();

                var items = new JArray();
                int missing = 0;
                foreach (var ix in intersections)
                {
                    bool hasCol = colPts.Any(cp => Math.Sqrt(Math.Pow(cp.X - ix.Xmm, 2) + Math.Pow(cp.Y - ix.Ymm, 2)) <= tolMm);
                    if (!hasCol) missing++;
                    items.Add(new JObject
                    {
                        ["grid_a"] = ix.GridA,
                        ["grid_b"] = ix.GridB,
                        ["point_mm"] = new JArray(ix.Xmm, ix.Ymm),
                        ["has_column"] = hasCol
                    });
                }

                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["operation"] = op,
                    ["grid_count"] = grids.Count,
                    ["intersection_count"] = items.Count,
                    ["missing_columns"] = missing,
                    ["intersections"] = items
                }, $"Planned {items.Count} grid intersection(s); {missing} without a structural column.");
            }

            // beams_along_grids: compute ordered spans along each grid
            var spans = new JArray();
            foreach (var g in grids)
            {
                var pts = intersections
                    .Where(ix => ix.GridA == g.Name || ix.GridB == g.Name)
                    .OrderBy(ix => ix.Xmm)
                    .ThenBy(ix => ix.Ymm)
                    .ToList();
                for (int k = 0; k < pts.Count - 1; k++)
                {
                    double lenMm = Math.Sqrt(Math.Pow(pts[k + 1].Xmm - pts[k].Xmm, 2) + Math.Pow(pts[k + 1].Ymm - pts[k].Ymm, 2));
                    if (lenMm < 100.0) continue;
                    spans.Add(new JObject
                    {
                        ["grid"] = g.Name,
                        ["start_mm"] = new JArray(pts[k].Xmm, pts[k].Ymm),
                        ["end_mm"] = new JArray(pts[k + 1].Xmm, pts[k + 1].Ymm),
                        ["span_m"] = Math.Round(lenMm / 1000.0, 3)
                    });
                }
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["operation"] = op,
                ["grid_count"] = grids.Count,
                ["span_count"] = spans.Count,
                ["spans"] = spans
            }, $"Planned {spans.Count} beam span(s) along {grids.Count} grid(s).");
        }
    }
}
