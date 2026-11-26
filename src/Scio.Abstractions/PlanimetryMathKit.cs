using System;
using System.Collections.Generic;
using System.Linq;

namespace Scio.Abstractions
{
    public sealed class Rect2D
    {
        public string Id { get; set; } = string.Empty;
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double Width => Math.Max(0.0, MaxX - MinX);
        public double Height => Math.Max(0.0, MaxY - MinY);
        public double CenterX => (MinX + MaxX) * 0.5;
        public double CenterY => (MinY + MaxY) * 0.5;

        public bool Overlaps(Rect2D other, double margin = 0.0)
        {
            if (other == null) return false;
            return MinX < other.MaxX + margin &&
                   MaxX + margin > other.MinX &&
                   MinY < other.MaxY + margin &&
                   MaxY + margin > other.MinY;
        }
    }

    public sealed class OverlapResolutionMove
    {
        public string Id { get; set; } = string.Empty;
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
    }

    /// <summary>
    /// Pure geometry & deterministic color helpers for Ola 2 documentation/planimetry tools.
    /// </summary>
    public static class PlanimetryMathKit
    {
        private static readonly byte[][] Palette =
        {
            new byte[] { 37, 99, 235 },   // Blue
            new byte[] { 220, 38, 38 },   // Red
            new byte[] { 22, 163, 74 },   // Green
            new byte[] { 217, 119, 6 },   // Amber
            new byte[] { 147, 51, 234 },  // Purple
            new byte[] { 8, 145, 178 },   // Cyan
            new byte[] { 219, 39, 119 },  // Pink
            new byte[] { 101, 163, 13 },  // Lime
            new byte[] { 234, 88, 12 },   // Orange
            new byte[] { 79, 70, 229 },   // Indigo
            new byte[] { 13, 148, 136 },  // Teal
            new byte[] { 120, 113, 108 }  // Stone
        };

        public static byte[] ColorForIndex(int index)
        {
            int idx = Math.Abs(index) % Palette.Length;
            return new byte[] { Palette[idx][0], Palette[idx][1], Palette[idx][2] };
        }

        public static List<(string A, string B)> FindOverlaps(IReadOnlyList<Rect2D> rects, double margin = 0.0)
        {
            var hits = new List<(string, string)>();
            if (rects == null) return hits;
            for (int i = 0; i < rects.Count; i++)
            {
                for (int j = i + 1; j < rects.Count; j++)
                {
                    if (rects[i].Overlaps(rects[j], margin))
                        hits.Add((rects[i].Id, rects[j].Id));
                }
            }
            return hits;
        }

        /// <summary>
        /// Computes minimal non-overlapping shifts (preferring horizontal shift right, then vertical down)
        /// for a list of 2D sheet rectangles.
        /// </summary>
        public static List<OverlapResolutionMove> ResolveOverlaps(IReadOnlyList<Rect2D> rects, double gap = 10.0)
        {
            var moves = new List<OverlapResolutionMove>();
            if (rects == null || rects.Count < 2) return moves;

            var working = rects
                .OrderBy(r => r.MinX)
                .ThenByDescending(r => r.MaxY)
                .Select(r => new Rect2D { Id = r.Id, MinX = r.MinX, MinY = r.MinY, MaxX = r.MaxX, MaxY = r.MaxY })
                .ToList();

            var deltas = working.ToDictionary(r => r.Id, _ => (Dx: 0.0, Dy: 0.0), StringComparer.Ordinal);

            for (int i = 1; i < working.Count; i++)
            {
                var cur = working[i];
                for (int iter = 0; iter < 32; iter++)
                {
                    var blocker = working.Take(i).FirstOrDefault(p => p.Overlaps(cur, gap * 0.5));
                    if (blocker == null) break;

                    double shiftX = (blocker.MaxX + gap) - cur.MinX;
                    if (shiftX < gap) shiftX = gap;
                    cur.MinX += shiftX;
                    cur.MaxX += shiftX;
                    var prev = deltas[cur.Id];
                    deltas[cur.Id] = (prev.Dx + shiftX, prev.Dy);
                }
            }

            foreach (var kv in deltas.Where(k => Math.Abs(k.Value.Dx) > 1e-6 || Math.Abs(k.Value.Dy) > 1e-6))
            {
                moves.Add(new OverlapResolutionMove
                {
                    Id = kv.Key,
                    DeltaX = Math.Round(kv.Value.Dx, 3),
                    DeltaY = Math.Round(kv.Value.Dy, 3)
                });
            }

            return moves;
        }
    }
}
