using System;
using System.Collections.Generic;
using System.Linq;

namespace Scio.Abstractions
{
    public sealed class ImageVectorizeOptions
    {
        public double? MmPerPixel { get; set; }
        public double[]? CalibrationPoint1Px { get; set; }
        public double[]? CalibrationPoint2Px { get; set; }
        public double? KnownDistanceMm { get; set; }
        public double OriginXMm { get; set; }
        public double OriginYMm { get; set; }
        public string ThresholdMode { get; set; } = "otsu";
        public int ManualThreshold { get; set; } = 128;
        public bool DarkForeground { get; set; } = true;
        public double MinSegmentMm { get; set; } = 150.0;
        public double SimplifyToleranceMm { get; set; } = 25.0;
        public double MinWallThicknessMm { get; set; } = 80.0;
        public double MaxWallThicknessMm { get; set; } = 450.0;
        public int MaxSegments { get; set; } = 300;
    }

    public sealed class VectorSegmentMm
    {
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public double LengthMm { get; set; }
    }

    public sealed class WallCandidateMm
    {
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public double LengthMm { get; set; }
        public double ThicknessMm { get; set; }
        public double Confidence { get; set; }
    }

    public sealed class ImageVectorizeResult
    {
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        public double MmPerPixel { get; set; }
        public int ThresholdUsed { get; set; }
        public double ForegroundRatio { get; set; }
        public List<VectorSegmentMm> Segments { get; } = new List<VectorSegmentMm>();
        public List<WallCandidateMm> WallCandidates { get; } = new List<WallCandidateMm>();
    }

    /// <summary>
    /// Clean-room raster-to-vector pipeline: Otsu thresholding, Zhang-Suen thinning,
    /// 8-connected skeleton tracing, Douglas-Peucker simplification, and parallel-face wall pairing.
    /// </summary>
    public static class ImageVectorizeKit
    {
        public static double ResolveMmPerPixel(ImageVectorizeOptions opt)
        {
            if (opt == null) throw new ArgumentNullException(nameof(opt));
            if (opt.MmPerPixel.HasValue && opt.MmPerPixel.Value > 0)
                return opt.MmPerPixel.Value;

            if (opt.CalibrationPoint1Px != null && opt.CalibrationPoint1Px.Length >= 2 &&
                opt.CalibrationPoint2Px != null && opt.CalibrationPoint2Px.Length >= 2 &&
                opt.KnownDistanceMm.HasValue && opt.KnownDistanceMm.Value > 0)
            {
                double dx = opt.CalibrationPoint2Px[0] - opt.CalibrationPoint1Px[0];
                double dy = opt.CalibrationPoint2Px[1] - opt.CalibrationPoint1Px[1];
                double pxDist = Math.Sqrt(dx * dx + dy * dy);
                if (pxDist > 1e-6)
                    return opt.KnownDistanceMm.Value / pxDist;
            }

            throw new InvalidOperationException("Explicit scale calibration is required: provide 'mm_per_pixel' > 0 or two calibration points with 'known_distance_mm' > 0.");
        }

        public static int ComputeOtsuThreshold(byte[] gray)
        {
            if (gray == null || gray.Length == 0) return 128;

            var hist = new int[256];
            for (int i = 0; i < gray.Length; i++) hist[gray[i]]++;

            int total = gray.Length;
            double sum = 0;
            for (int t = 0; t < 256; t++) sum += t * hist[t];

            double sumB = 0;
            int wB = 0;
            double maxVar = -1.0;
            int bestT = 128;

            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;

                sumB += t * hist[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double diff = mB - mF;
                double varBetween = (double)wB * wF * diff * diff;
                if (varBetween > maxVar)
                {
                    maxVar = varBetween;
                    bestT = t;
                }
            }

            return bestT;
        }

        public static bool[] Binarize(byte[] gray, int threshold, bool darkForeground = true)
        {
            var mask = new bool[gray.Length];
            for (int i = 0; i < gray.Length; i++)
                mask[i] = darkForeground ? (gray[i] <= threshold) : (gray[i] >= threshold);
            return mask;
        }

        public static bool[] ThinZhangSuen(bool[] input, int width, int height, int maxIterations = 80)
        {
            var img = (bool[])input.Clone();
            var toRemove = new List<int>();

            for (int iter = 0; iter < maxIterations; iter++)
            {
                bool changed = false;

                for (int step = 0; step < 2; step++)
                {
                    toRemove.Clear();
                    for (int y = 1; y < height - 1; y++)
                    {
                        int row = y * width;
                        for (int x = 1; x < width - 1; x++)
                        {
                            int idx = row + x;
                            if (!img[idx]) continue;

                            bool p2 = img[idx - width];
                            bool p3 = img[idx - width + 1];
                            bool p4 = img[idx + 1];
                            bool p5 = img[idx + width + 1];
                            bool p6 = img[idx + width];
                            bool p7 = img[idx + width - 1];
                            bool p8 = img[idx - 1];
                            bool p9 = img[idx - width - 1];

                            int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) +
                                    (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                            if (b < 2 || b > 6) continue;

                            int a = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0) +
                                    (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                            if (a != 1) continue;

                            if (step == 0)
                            {
                                if (p2 && p4 && p6) continue;
                                if (p4 && p6 && p8) continue;
                            }
                            else
                            {
                                if (p2 && p4 && p8) continue;
                                if (p2 && p6 && p8) continue;
                            }

                            toRemove.Add(idx);
                        }
                    }

                    if (toRemove.Count > 0)
                    {
                        changed = true;
                        for (int i = 0; i < toRemove.Count; i++)
                            img[toRemove[i]] = false;
                    }
                }

                if (!changed) break;
            }

            return img;
        }

        public static List<List<(double X, double Y)>> TraceSkeletonPolylines(bool[] skel, int width, int height)
        {
            var visited = new bool[skel.Length];
            var polylines = new List<List<(double X, double Y)>>();
            int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

            int CountNeighbors(int x, int y)
            {
                int c = 0;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + dx[k], ny = y + dy[k];
                    if (nx >= 0 && nx < width && ny >= 0 && ny < height && skel[ny * width + nx]) c++;
                }
                return c;
            }

            void TraceFrom(int sx, int sy)
            {
                var chain = new List<(double X, double Y)>();
                int cx = sx, cy = sy;
                while (true)
                {
                    int cIdx = cy * width + cx;
                    visited[cIdx] = true;
                    chain.Add((cx, cy));

                    int nextX = -1, nextY = -1;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = cx + dx[k], ny = cy + dy[k];
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        int nIdx = ny * width + nx;
                        if (skel[nIdx] && !visited[nIdx])
                        {
                            nextX = nx;
                            nextY = ny;
                            break;
                        }
                    }

                    if (nextX < 0) break;
                    cx = nextX;
                    cy = nextY;
                }

                if (chain.Count >= 2)
                    polylines.Add(chain);
            }

            // First pass: start from endpoints (degree == 1)
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    if (skel[idx] && !visited[idx] && CountNeighbors(x, y) == 1)
                        TraceFrom(x, y);
                }
            }

            // Second pass: closed loops or remaining branches
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    if (skel[idx] && !visited[idx])
                        TraceFrom(x, y);
                }
            }

            return polylines;
        }

        public static List<(double X, double Y)> SimplifyDouglasPeucker(IReadOnlyList<(double X, double Y)> pts, double tolerance)
        {
            if (pts == null || pts.Count <= 2) return pts?.ToList() ?? new List<(double, double)>();
            double tol = Math.Max(0.0, tolerance);
            var keep = new bool[pts.Count];
            keep[0] = true;
            keep[pts.Count - 1] = true;

            var stack = new Stack<(int Start, int End)>();
            stack.Push((0, pts.Count - 1));

            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                if (e - s <= 1) continue;

                double maxDist = -1.0;
                int maxIdx = -1;
                var a = pts[s];
                var b = pts[e];

                for (int i = s + 1; i < e; i++)
                {
                    double d = PointToSegmentDistance(pts[i], a, b);
                    if (d > maxDist)
                    {
                        maxDist = d;
                        maxIdx = i;
                    }
                }

                if (maxDist > tol && maxIdx > s)
                {
                    keep[maxIdx] = true;
                    stack.Push((s, maxIdx));
                    stack.Push((maxIdx, e));
                }
            }

            var simplified = new List<(double X, double Y)>();
            for (int i = 0; i < pts.Count; i++)
                if (keep[i]) simplified.Add(pts[i]);
            return simplified;
        }

        public static ImageVectorizeResult Vectorize(byte[] gray, int width, int height, ImageVectorizeOptions opt)
        {
            if (gray == null || gray.Length != width * height)
                throw new ArgumentException("Grayscale buffer length must equal width * height.");

            double mmPerPx = ResolveMmPerPixel(opt);
            int threshold = string.Equals(opt.ThresholdMode, "manual", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0, Math.Min(255, opt.ManualThreshold))
                : ComputeOtsuThreshold(gray);

            var mask = Binarize(gray, threshold, opt.DarkForeground);
            int fgCount = mask.Count(b => b);

            var skel = ThinZhangSuen(mask, width, height);
            var chains = TraceSkeletonPolylines(skel, width, height);

            double tolPx = Math.Max(0.5, opt.SimplifyToleranceMm / mmPerPx);
            var rawSegments = new List<VectorSegmentMm>();

            foreach (var ch in chains)
            {
                var simp = SimplifyDouglasPeucker(ch, tolPx);
                for (int i = 0; i < simp.Count - 1; i++)
                {
                    double x0 = opt.OriginXMm + simp[i].X * mmPerPx;
                    double y0 = opt.OriginYMm + (height - 1 - simp[i].Y) * mmPerPx;
                    double x1 = opt.OriginXMm + simp[i + 1].X * mmPerPx;
                    double y1 = opt.OriginYMm + (height - 1 - simp[i + 1].Y) * mmPerPx;
                    double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                    if (len >= opt.MinSegmentMm)
                    {
                        rawSegments.Add(new VectorSegmentMm
                        {
                            StartX = Math.Round(x0, 1),
                            StartY = Math.Round(y0, 1),
                            EndX = Math.Round(x1, 1),
                            EndY = Math.Round(y1, 1),
                            LengthMm = Math.Round(len, 1)
                        });
                    }
                }
            }

            var result = new ImageVectorizeResult
            {
                WidthPx = width,
                HeightPx = height,
                MmPerPixel = Math.Round(mmPerPx, 4),
                ThresholdUsed = threshold,
                ForegroundRatio = Math.Round((double)fgCount / Math.Max(1, gray.Length), 4)
            };

            foreach (var seg in rawSegments.OrderByDescending(s => s.LengthMm).Take(Math.Max(1, opt.MaxSegments)))
                result.Segments.Add(seg);

            // Detect parallel wall face pairs to propose candidate wall centerlines
            var used = new bool[result.Segments.Count];
            for (int i = 0; i < result.Segments.Count; i++)
            {
                if (used[i]) continue;
                var a = result.Segments[i];
                double adx = a.EndX - a.StartX, ady = a.EndY - a.StartY;
                double alen = Math.Sqrt(adx * adx + ady * ady);
                if (alen < 1e-3) continue;
                double aux = adx / alen, auy = ady / alen;

                int bestJ = -1;
                double bestThick = double.MaxValue;

                for (int j = i + 1; j < result.Segments.Count; j++)
                {
                    if (used[j]) continue;
                    var b = result.Segments[j];
                    double bdx = b.EndX - b.StartX, bdy = b.EndY - b.StartY;
                    double blen = Math.Sqrt(bdx * bdx + bdy * bdy);
                    if (blen < 1e-3) continue;
                    double bux = bdx / blen, buy = bdy / blen;

                    // Nearly parallel (within ~5 degrees)
                    double dot = Math.Abs(aux * bux + auy * buy);
                    if (dot < 0.996) continue;

                    // Perpendicular distance between lines
                    double bMidX = (b.StartX + b.EndX) * 0.5;
                    double bMidY = (b.StartY + b.EndY) * 0.5;
                    double perp = Math.Abs((bMidX - a.StartX) * (-auy) + (bMidY - a.StartY) * aux);
                    if (perp < opt.MinWallThicknessMm || perp > opt.MaxWallThicknessMm) continue;

                    // Check overlap along axis
                    double p0 = (b.StartX - a.StartX) * aux + (b.StartY - a.StartY) * auy;
                    double p1 = (b.EndX - a.StartX) * aux + (b.EndY - a.StartY) * auy;
                    double lo = Math.Max(0.0, Math.Min(p0, p1));
                    double hi = Math.Min(alen, Math.Max(p0, p1));
                    double overlap = hi - lo;
                    if (overlap < Math.Max(opt.MinSegmentMm, 0.5 * Math.Min(alen, blen))) continue;

                    if (perp < bestThick)
                    {
                        bestThick = perp;
                        bestJ = j;
                    }
                }

                if (bestJ >= 0)
                {
                    used[i] = true;
                    used[bestJ] = true;
                    var b = result.Segments[bestJ];
                    double bMidX = (b.StartX + b.EndX) * 0.5;
                    double bMidY = (b.StartY + b.EndY) * 0.5;
                    double signedOffset = (bMidX - a.StartX) * (-auy) + (bMidY - a.StartY) * aux;
                    double halfOff = signedOffset * 0.5;

                    double p0 = (b.StartX - a.StartX) * aux + (b.StartY - a.StartY) * auy;
                    double p1 = (b.EndX - a.StartX) * aux + (b.EndY - a.StartY) * auy;
                    double lo = Math.Max(0.0, Math.Min(p0, p1));
                    double hi = Math.Min(alen, Math.Max(p0, p1));

                    double csx = a.StartX + aux * lo + (-auy) * halfOff;
                    double csy = a.StartY + auy * lo + aux * halfOff;
                    double cex = a.StartX + aux * hi + (-auy) * halfOff;
                    double cey = a.StartY + auy * hi + aux * halfOff;
                    double clen = hi - lo;

                    result.WallCandidates.Add(new WallCandidateMm
                    {
                        StartX = Math.Round(csx, 1),
                        StartY = Math.Round(csy, 1),
                        EndX = Math.Round(cex, 1),
                        EndY = Math.Round(cey, 1),
                        LengthMm = Math.Round(clen, 1),
                        ThicknessMm = Math.Round(bestThick, 1),
                        Confidence = Math.Round(Math.Min(1.0, clen / Math.Max(alen, b.LengthMm)), 2)
                    });
                }
            }

            return result;
        }

        private static double PointToSegmentDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

            double t = Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            double px = a.X + t * dx;
            double py = a.Y + t * dy;
            return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
        }
    }
}
