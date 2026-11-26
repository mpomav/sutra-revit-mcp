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
    }
}
