using System.Collections.Generic;
using Scio.Abstractions;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class Ola2DocumentationTests
    {
        [Fact]
        public void ColorForIndex_IsDeterministicAndWrapsCleanly()
        {
            var c0 = PlanimetryMathKit.ColorForIndex(0);
            var c12 = PlanimetryMathKit.ColorForIndex(12);
            Assert.Equal(c0, c12);
            Assert.Equal(3, c0.Length);
        }

        [Fact]
        public void ResolveOverlaps_SeparatesOverlappingSheetViewports()
        {
            var rects = new List<Rect2D>
            {
                new Rect2D { Id = "101", MinX = 0, MinY = 0, MaxX = 200, MaxY = 150 },
                new Rect2D { Id = "102", MinX = 100, MinY = 20, MaxX = 300, MaxY = 170 }
            };

            var overlapsBefore = PlanimetryMathKit.FindOverlaps(rects);
            Assert.Single(overlapsBefore);

            var moves = PlanimetryMathKit.ResolveOverlaps(rects, gap: 15.0);
            Assert.Single(moves);
            Assert.Equal("102", moves[0].Id);
            Assert.True(moves[0].DeltaX >= 115.0);
        }

        [Fact]
        public void ComputePaddedBox_MergesBoxesAndAppliesSymmetricPadding()
        {
            var boxes = new List<Box3DMm>
            {
                new Box3DMm { MinX = 0, MinY = 0, MinZ = 0, MaxX = 4000, MaxY = 300, MaxZ = 600 },
                new Box3DMm { MinX = 3500, MinY = -200, MinZ = 0, MaxX = 8000, MaxY = 500, MaxZ = 800 }
            };

            var padded = PlanimetryMathKit.ComputePaddedBox(boxes, paddingMm: 150.0);
            Assert.NotNull(padded);
            Assert.Equal(-150.0, padded!.MinX);
            Assert.Equal(-350.0, padded.MinY);
            Assert.Equal(-150.0, padded.MinZ);
            Assert.Equal(8150.0, padded.MaxX);
            Assert.Equal(650.0, padded.MaxY);
            Assert.Equal(950.0, padded.MaxZ);
        }

        [Fact]
        public void ComputeBeamSectionFrames_ProducesLongitudinalAndTransverseFrames()
        {
            var frames = PlanimetryMathKit.ComputeBeamSectionFrames(
                startMm: new[] { 0.0, 0.0, 3000.0 },
                endMm: new[] { 6000.0, 0.0, 3000.0 },
                crossWidthMm: 300.0,
                crossHeightMm: 600.0,
                paddingMm: 150.0,
                transverseCount: 2);

            Assert.Equal(3, frames.Count);
            Assert.Equal("longitudinal", frames[0].Kind);
            Assert.Equal(6300.0, frames[0].WidthMm);
            Assert.Equal(900.0, frames[0].HeightMm);
            Assert.Equal("transverse", frames[1].Kind);
            Assert.Equal(2000.0, frames[1].OriginMm[0]);
            Assert.Equal("transverse", frames[2].Kind);
            Assert.Equal(4000.0, frames[2].OriginMm[0]);
        }

        [Fact]
        public void FormatRevitSafeName_SanitizesForbiddenCharacters()
        {
            string name = PlanimetryMathKit.FormatRevitSafeName(
                pattern: "ISO:{level}[{tag}]/{grid}",
                preset: "ISO",
                level: "N+03.50",
                tag: "B<1>",
                grid: "A|1",
                index: 1);

            Assert.DoesNotContain(":", name);
            Assert.DoesNotContain("[", name);
            Assert.DoesNotContain("]", name);
            Assert.DoesNotContain("<", name);
            Assert.DoesNotContain(">", name);
            Assert.DoesNotContain("|", name);
            Assert.Contains("N+03.50", name);
        }
    }
}
