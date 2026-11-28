using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class ScanToBimRulesTests
    {
        [Theory]
        [InlineData(0.5, "LOA50")]
        [InlineData(3.2, "LOA40")]
        [InlineData(12.0, "LOA30")]
        [InlineData(35.0, "LOA20")]
        [InlineData(80.0, "LOA10")]
        public void ClassifyLoa_MapsUsibdBands(double p95Mm, string expected)
        {
            Assert.Equal(expected, ScanToBimRules.ClassifyLoa(p95Mm));
        }

        [Fact]
        public void DetectLevelsFromZ_FindsThreeFloorsAndBuildsCreateElementsPayload()
        {
            var rng = new Random(123);
            var z = new List<double>();
            double[] floors = { 0.0, 3000.0, 6200.0 };
            foreach (double f in floors)
            {
                for (int i = 0; i < 400; i++)
                    z.Add(f + (rng.NextDouble() - 0.5) * 16.0);
            }
            // Add vertical wall noise between floors
            for (int i = 0; i < 600; i++)
                z.Add(rng.NextDouble() * 6200.0);

            JObject res = ScanToBimRules.DetectLevelsFromZ(z, binSizeMm: 20, minSeparationMm: 1800, minShare: 0.05);

            Assert.Equal("levels_detected", res.Value<string>("verdict"));
            Assert.Equal(3, res.Value<int>("levels_detected"));
            var levels = (JArray)res["levels"]!;
            Assert.InRange(levels[0].Value<double>("elevation_mm"), -15, 15);
            Assert.InRange(levels[1].Value<double>("elevation_mm"), 2985, 3015);
            Assert.InRange(levels[2].Value<double>("elevation_mm"), 6185, 6215);
            Assert.Equal("scio_create_elements", res["next_arguments"]!.Value<string>("tool"));
        }

        [Fact]
        public void ExtractSliceWalls_PairsParallelFacesIntoDoubleFaceWallAndSplitsDoorway()
        {
            var pts = new List<double[]>();
            // Wall 1: X in [0..4000], two parallel faces at Y=0 and Y=200 (200 mm thick wall)
            for (double x = 0; x <= 4000; x += 25)
            {
                pts.Add(new[] { x, 0.0 });
                pts.Add(new[] { x, 200.0 });
            }
            // Wall 2 along X=5000, Y in [0..1500] and [2500..4000] (1000 mm doorway gap)
            for (double y = 0; y <= 1500; y += 25) pts.Add(new[] { 5000.0, y });
            for (double y = 2500; y <= 4000; y += 25) pts.Add(new[] { 5000.0, y });

            JObject res = ScanToBimRules.ExtractSliceWalls(pts, voxelMm: 25, minLineLengthMm: 500, lineToleranceMm: 20);

            Assert.Equal("segments_extracted", res.Value<string>("verdict"));
            Assert.Equal(1, res.Value<int>("double_face_walls"));
            Assert.True(res.Value<int>("single_face_walls") >= 2);

            var walls = (JArray)res["wall_candidates"]!;
            var doubleWall = (JObject)walls[0]!;
            Assert.Equal("double_face", doubleWall.Value<string>("confidence"));
            Assert.InRange(doubleWall.Value<double>("estimated_thickness_mm"), 190, 210);
            Assert.InRange(doubleWall.Value<double>("length_mm"), 3900, 4050);
        }

        [Fact]
        public void FitPrimitive_Plane_RecoversNormalAndTilt()
        {
            var pts = new List<double[]>();
            // Horizontal slab at Z = 3000 mm with +/- 0.5 mm noise (LOA50)
            for (int x = 0; x <= 10; x++)
                for (int y = 0; y <= 10; y++)
                    pts.Add(new[] { x * 100.0, y * 100.0, 3000.0 + ((x + y) % 2 == 0 ? 0.5 : -0.5) });

            JObject res = ScanToBimRules.FitPrimitive(pts, "plane", toleranceMm: 10);

            Assert.Equal("fitted_ok", res.Value<string>("verdict"));
            Assert.InRange(res.Value<double>("tilt_from_horizontal_deg"), 0, 0.5);
            Assert.Equal("LOA50", res.Value<string>("loa"));
        }

        [Fact]
        public void FitPrimitive_Column_DetectsRectangularDimensions()
        {
            var pts = new List<double[]>();
            // 400 x 600 mm rectangular column centered at (1000, 2000), Z in [0..3000]
            double cx = 1000, cy = 2000, hx = 200, hy = 300;
            for (double z = 0; z <= 3000; z += 500)
            {
                for (double t = -hx; t <= hx; t += 40)
                {
                    pts.Add(new[] { cx + t, cy - hy, z });
                    pts.Add(new[] { cx + t, cy + hy, z });
                }
                for (double t = -hy; t <= hy; t += 40)
                {
                    pts.Add(new[] { cx - hx, cy + t, z });
                    pts.Add(new[] { cx + hx, cy + t, z });
                }
            }

            JObject res = ScanToBimRules.FitPrimitive(pts, "column", toleranceMm: 15);

            Assert.Equal("fitted_ok", res.Value<string>("verdict"));
            Assert.Equal("rectangular", res.Value<string>("shape_hint"));
            double w = res.Value<double>("width_mm");
            double d = res.Value<double>("depth_mm");
            Assert.InRange(Math.Max(w, d), 580, 620);
            Assert.InRange(Math.Min(w, d), 380, 420);
        }

        [Fact]
        public void FitPrimitive_Cylinder_RecoversPipeDiameterAndLength()
        {
            var pts = new List<double[]>();
            // Horizontal pipe along X, length 2000 mm, diameter 200 mm (radius 100 mm), center Y=500, Z=2500
            double r = 100.0;
            for (double x = 0; x <= 2000; x += 100)
            {
                for (int k = 0; k < 24; k++)
                {
                    double ang = k * 2.0 * Math.PI / 24.0;
                    pts.Add(new[] { x, 500.0 + r * Math.Cos(ang), 2500.0 + r * Math.Sin(ang) });
                }
            }

            JObject res = ScanToBimRules.FitPrimitive(pts, "cylinder", toleranceMm: 10);

            Assert.Equal("fitted_ok", res.Value<string>("verdict"));
            Assert.InRange(res.Value<double>("outer_diameter_mm"), 195, 205);
            Assert.InRange(res.Value<double>("length_mm"), 1980, 2020);
            Assert.Equal("LOA50", res.Value<string>("loa"));
        }
    }
}
