using System;
using Scio.Abstractions;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class Ola3ModelingTests
    {
        [Fact]
        public void IntersectSegments2D_FindsPerpendicularGridCrossing()
        {
            var hit = ConstructionMathKit.IntersectSegments2D(
                new Point2D(0, 5000), new Point2D(10000, 5000),
                new Point2D(4000, 0), new Point2D(4000, 10000));

            Assert.NotNull(hit);
            Assert.Equal(4000.0, hit!.X, 3);
            Assert.Equal(5000.0, hit.Y, 3);
        }

        [Fact]
        public void SnapEndpointOrthogonal_SnapsSmallSkewAndLeavesLargeAngleAlone()
        {
            var p1 = new Point2D(0, 0);
            var skewed = new Point2D(5000, 100); // ~1.15 deg
            var snapped = ConstructionMathKit.SnapEndpointOrthogonal(p1, skewed, maxAngleDeg: 3.0);
            Assert.NotNull(snapped);
            Assert.True(Math.Abs(snapped!.Y) < 1e-3);

            var diagonal = new Point2D(5000, 5000); // 45 deg
            Assert.Null(ConstructionMathKit.SnapEndpointOrthogonal(p1, diagonal, maxAngleDeg: 3.0));
        }

        [Fact]
        public void ComputeSlabLayerTopOffsets_StacksLayersTopToBottom()
        {
            var offsets = ConstructionMathKit.ComputeSlabLayerTopOffsets(new[] { 0.05, 0.20, 0.02 });
            Assert.Equal(3, offsets.Count);
            Assert.Equal(0.0, offsets[0], 4);
            Assert.Equal(-0.05, offsets[1], 4);
            Assert.Equal(-0.25, offsets[2], 4);
        }
    }
}
