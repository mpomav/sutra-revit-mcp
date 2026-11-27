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

    public sealed class Box3DMm
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }
    }

    public sealed class BeamSectionPlanFrame
    {
        public string Kind { get; set; } = string.Empty;
        public double Fraction { get; set; }
        public double[] OriginMm { get; set; } = Array.Empty<double>();
        public double[] AxisDirXy { get; set; } = Array.Empty<double>();
        public double[] ViewDirXy { get; set; } = Array.Empty<double>();
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double DepthMm { get; set; }
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

        /// <summary>
        /// Combines one or more 3D millimeter bounding boxes and expands symmetrically by paddingMm.
        /// </summary>
        public static Box3DMm? ComputePaddedBox(IEnumerable<Box3DMm> boxes, double paddingMm)
        {
            if (boxes == null) return null;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int count = 0;

            foreach (var b in boxes)
            {
                if (b == null) continue;
                if (b.MinX < minX) minX = b.MinX;
                if (b.MinY < minY) minY = b.MinY;
                if (b.MinZ < minZ) minZ = b.MinZ;
                if (b.MaxX > maxX) maxX = b.MaxX;
                if (b.MaxY > maxY) maxY = b.MaxY;
                if (b.MaxZ > maxZ) maxZ = b.MaxZ;
                count++;
            }

            if (count == 0) return null;
            double pad = Math.Max(0.0, paddingMm);
            return new Box3DMm
            {
                MinX = Math.Round(minX - pad, 1),
                MinY = Math.Round(minY - pad, 1),
                MinZ = Math.Round(minZ - pad, 1),
                MaxX = Math.Round(maxX + pad, 1),
                MaxY = Math.Round(maxY + pad, 1),
                MaxZ = Math.Round(maxZ + pad, 1)
            };
        }

        /// <summary>
        /// Computes 1 longitudinal + N transverse section frames aligned to a beam's XY axis.
        /// </summary>
        public static List<BeamSectionPlanFrame> ComputeBeamSectionFrames(
            double[] startMm,
            double[] endMm,
            double crossWidthMm,
            double crossHeightMm,
            double paddingMm = 150.0,
            int transverseCount = 1)
        {
            var frames = new List<BeamSectionPlanFrame>();
            if (startMm == null || endMm == null || startMm.Length < 3 || endMm.Length < 3)
                return frames;

            double dx = endMm[0] - startMm[0];
            double dy = endMm[1] - startMm[1];
            double lenXy = Math.Sqrt(dx * dx + dy * dy);
            if (lenXy < 1e-3) return frames;

            double ux = dx / lenXy;
            double uy = dy / lenXy;
            // Left-hand normal in XY plane
            double nx = -uy;
            double ny = ux;

            double pad = Math.Max(0.0, paddingMm);
            double w = Math.Max(100.0, crossWidthMm);
            double h = Math.Max(100.0, crossHeightMm);
            double midZ = (startMm[2] + endMm[2]) * 0.5;

            // 1. Longitudinal section (looking perpendicular to beam axis)
            frames.Add(new BeamSectionPlanFrame
            {
                Kind = "longitudinal",
                Fraction = 0.5,
                OriginMm = new[]
                {
                    Math.Round((startMm[0] + endMm[0]) * 0.5, 1),
                    Math.Round((startMm[1] + endMm[1]) * 0.5, 1),
                    Math.Round(midZ, 1)
                },
                AxisDirXy = new[] { Math.Round(ux, 6), Math.Round(uy, 6) },
                ViewDirXy = new[] { Math.Round(nx, 6), Math.Round(ny, 6) },
                WidthMm = Math.Round(lenXy + 2.0 * pad, 1),
                HeightMm = Math.Round(h + 2.0 * pad, 1),
                DepthMm = Math.Round(w + 2.0 * pad, 1)
            });

            // 2. Transverse sections distributed evenly along beam span
            int count = Math.Max(1, Math.Min(5, transverseCount));
            for (int i = 0; i < count; i++)
            {
                double frac = (i + 1.0) / (count + 1.0);
                double ox = startMm[0] + dx * frac;
                double oy = startMm[1] + dy * frac;
                double oz = startMm[2] + (endMm[2] - startMm[2]) * frac;

                frames.Add(new BeamSectionPlanFrame
                {
                    Kind = "transverse",
                    Fraction = Math.Round(frac, 3),
                    OriginMm = new[] { Math.Round(ox, 1), Math.Round(oy, 1), Math.Round(oz, 1) },
                    AxisDirXy = new[] { Math.Round(nx, 6), Math.Round(ny, 6) },
                    ViewDirXy = new[] { Math.Round(ux, 6), Math.Round(uy, 6) },
                    WidthMm = Math.Round(w + 2.0 * pad, 1),
                    HeightMm = Math.Round(h + 2.0 * pad, 1),
                    DepthMm = Math.Round(Math.Max(300.0, pad * 2.0), 1)
                });
            }

            return frames;
        }

        /// <summary>
        /// Formats a Revit-safe view or sheet name from an organisation-neutral pattern.
        /// Strips characters forbidden by Revit view names: \ : { } [ ] | ; &lt; &gt; ? ` ~
        /// </summary>
        public static string FormatRevitSafeName(
            string pattern,
            string preset,
            string level,
            string tag,
            string grid,
            int index)
        {
            string raw = string.IsNullOrWhiteSpace(pattern)
                ? "{preset}_{level}_{tag}_{index}"
                : pattern;

            raw = raw
                .Replace("{preset}", preset ?? string.Empty)
                .Replace("{level}", level ?? string.Empty)
                .Replace("{tag}", tag ?? string.Empty)
                .Replace("{grid}", grid ?? string.Empty)
                .Replace("{index}", index.ToString("D2"));

            const string forbidden = "\\:{}[]|;<>?`~";
            var chars = raw.Select(c => forbidden.IndexOf(c) >= 0 ? '_' : c).ToArray();
            string cleaned = new string(chars).Trim();
            while (cleaned.Contains("__")) cleaned = cleaned.Replace("__", "_");
            cleaned = cleaned.Trim('_');
            return string.IsNullOrWhiteSpace(cleaned) ? $"View_{index:D2}" : cleaned;
        }
    }
}
