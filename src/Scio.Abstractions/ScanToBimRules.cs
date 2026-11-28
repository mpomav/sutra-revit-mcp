// -----------------------------------------------------------------------------
// SCIO Revit MCP â€” Scan-to-BIM core geometry & rules engine (Revit-free).
//
// Pure arithmetic for point-cloud (.rcp/.rcs) level detection, 2D slice wall
// extraction, 3D primitive fitting (plane / column / cylinder), and USIBD LOA
// classification. Carries no `using Autodesk.*` so every algorithm is directly
// unit-tested in Scio.Core.Tests without Revit open.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public static class ScanToBimRules
    {
        public const int MaxSlicePoints = 150000;
        public const int MaxLevelPoints = 150000;
        public const int MaxPrimitivePoints = 50000;

        /// <summary>
        /// USIBD Level of Accuracy (LOA) grade from p95 absolute deviation in mm.
        /// LOA50: 0â€“1 mm, LOA40: 1â€“5 mm, LOA30: 5â€“15 mm, LOA20: 15â€“50 mm, LOA10: >50 mm.
        /// </summary>
        public static string ClassifyLoa(double p95AbsMm)
        {
            if (double.IsNaN(p95AbsMm) || double.IsInfinity(p95AbsMm)) return "undetermined";
            double v = Math.Abs(p95AbsMm);
            if (v <= 1.0) return "LOA50";
            if (v <= 5.0) return "LOA40";
            if (v <= 15.0) return "LOA30";
            if (v <= 50.0) return "LOA20";
            return "LOA10";
        }

        // ---------------------------------------------------------------------
        // 1. Z-HISTOGRAM LEVEL DETECTION
        // ---------------------------------------------------------------------

        /// <summary>
        /// Detects dominant horizontal elevation planes (floors / slabs / ceilings)
        /// from sampled Z coordinates in millimeters.
        /// </summary>
        public static JObject DetectLevelsFromZ(
            IList<double> zMm,
            double binSizeMm = 20.0,
            double minSeparationMm = 1800.0,
            double minShare = 0.015,
            int maxLevels = 30)
        {
            if (binSizeMm <= 0) binSizeMm = 20.0;
            if (minSeparationMm < 100) minSeparationMm = 100.0;
            if (minShare <= 0) minShare = 0.01;
            if (maxLevels < 1) maxLevels = 1;

            int total = zMm?.Count ?? 0;
            if (total < 20)
            {
                return new JObject
                {
                    ["points_analyzed"] = total,
                    ["levels_detected"] = 0,
                    ["levels"] = new JArray(),
                    ["verdict"] = "insufficient_points",
                    ["note"] = "At least 20 sampled points are required to detect elevation peaks."
                };
            }

            double minZ = double.MaxValue, maxZ = double.MinValue;
            for (int i = 0; i < total; i++)
            {
                double z = zMm![i];
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            int binCount = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / binSizeMm) + 1);
            if (binCount > 50000)
            {
                binSizeMm = (maxZ - minZ) / 49999.0;
                binCount = 50000;
            }

            var counts = new int[binCount];
            var sums = new double[binCount];
            for (int i = 0; i < total; i++)
            {
                int b = Math.Min(binCount - 1, Math.Max(0, (int)Math.Floor((zMm![i] - minZ) / binSizeMm)));
                counts[b]++;
                sums[b] += zMm![i];
            }

            // 3-bin triangular smoothing to avoid split peaks at bin edges.
            var smooth = new double[binCount];
            for (int b = 0; b < binCount; b++)
            {
                double left = b > 0 ? counts[b - 1] : 0;
                double right = b + 1 < binCount ? counts[b + 1] : 0;
                smooth[b] = 0.25 * left + 0.5 * counts[b] + 0.25 * right;
            }

            double minPeakWeight = Math.Max(10.0, total * minShare);
            var candidates = new List<(int bin, double weight)>();
            for (int b = 0; b < binCount; b++)
            {
                if (smooth[b] < minPeakWeight) continue;
                bool geLeft = b == 0 || smooth[b] >= smooth[b - 1];
                bool geRight = b == binCount - 1 || smooth[b] >= smooth[b + 1];
                if (geLeft && geRight) candidates.Add((b, smooth[b]));
            }

            candidates.Sort((a, b) => b.weight.CompareTo(a.weight));

            int sepBins = Math.Max(1, (int)Math.Round(minSeparationMm / binSizeMm));
            int bandBins = Math.Max(1, (int)Math.Ceiling(60.0 / binSizeMm));
            var selectedBins = new List<int>();
            foreach (var c in candidates)
            {
                if (selectedBins.Count >= maxLevels) break;
                if (selectedBins.Any(sb => Math.Abs(sb - c.bin) < sepBins)) continue;
                selectedBins.Add(c.bin);
            }

            selectedBins.Sort();

            var levels = new JArray();
            var createItems = new JArray();
            int idx = 1;
            foreach (int peakBin in selectedBins)
            {
                int lo = Math.Max(0, peakBin - bandBins);
                int hi = Math.Min(binCount - 1, peakBin + bandBins);
                int bandCount = 0;
                double bandSum = 0;
                for (int b = lo; b <= hi; b++)
                {
                    bandCount += counts[b];
                    bandSum += sums[b];
                }
                if (bandCount == 0) continue;
                double meanZ = bandSum / bandCount;

                // Compute spread around meanZ from points inside the band.
                double loZ = minZ + lo * binSizeMm;
                double hiZ = minZ + (hi + 1) * binSizeMm;
                var devs = new List<double>();
                for (int i = 0; i < total; i++)
                {
                    double z = zMm![i];
                    if (z >= loZ && z <= hiZ) devs.Add(Math.Abs(z - meanZ));
                }
                devs.Sort();
                double p95 = devs.Count > 0 ? PercentileSorted(devs, 0.95) : 0;
                double elevRounded = Math.Round(meanZ, 1);
                string suggestedName = "SCAN_L" + idx.ToString(CultureInfo.InvariantCulture);

                levels.Add(new JObject
                {
                    ["index"] = idx,
                    ["suggested_name"] = suggestedName,
                    ["elevation_mm"] = elevRounded,
                    ["point_count"] = bandCount,
                    ["point_share"] = Math.Round(bandCount / (double)total, 4),
                    ["p95_spread_mm"] = Math.Round(p95, 1),
                    ["loa"] = ClassifyLoa(p95)
                });
                createItems.Add(new JObject
                {
                    ["kind"] = "level",
                    ["name"] = suggestedName,
                    ["elevation_mm"] = elevRounded
                });
                idx++;
            }

            return new JObject
            {
                ["points_analyzed"] = total,
                ["z_min_mm"] = Math.Round(minZ, 1),
                ["z_max_mm"] = Math.Round(maxZ, 1),
                ["bin_size_mm"] = Math.Round(binSizeMm, 1),
                ["min_separation_mm"] = Math.Round(minSeparationMm, 1),
                ["levels_detected"] = levels.Count,
                ["levels"] = levels,
                ["verdict"] = levels.Count > 0 ? "levels_detected" : "no_clear_peaks",
                ["next_arguments"] = levels.Count > 0 ? new JObject
                {
                    ["tool"] = "scio_create_elements",
                    ["arguments"] = new JObject
                    {
                        ["elements"] = createItems,
                        ["dry_run"] = true
                    }
                } : null
            };
        }

        // ---------------------------------------------------------------------
        // 2. HORIZONTAL SLICE -> 2D SEGMENTS & WALL CENTERLINES
        // ---------------------------------------------------------------------

        private sealed class Segment2D
        {
            public int Id;
            public double X1, Y1, X2, Y2;
            public double Ux, Uy;     // unit direction
            public double Nx, Ny;     // unit normal (-Uy, Ux)
            public double LengthMm;
            public double AngleDeg;
            public int PointCount;
            public double P95ResidualMm;
            public bool Paired;
        }

        /// <summary>
        /// Projects 2D slice points (mm) onto line segments via voxel downsampling +
        /// deterministic RANSAC + gap splitting, then pairs opposing parallel faces into
        /// wall centerlines with estimated thickness.
        /// </summary>
        public static JObject ExtractSliceWalls(
            IList<double[]> xyMm,
            double voxelMm = 25.0,
            double minLineLengthMm = 400.0,
            double lineToleranceMm = 25.0,
            int minPointsPerLine = 12,
            int maxLines = 60,
            double minWallThicknessMm = 60.0,
            double maxWallThicknessMm = 500.0)
        {
            if (voxelMm <= 0) voxelMm = 25.0;
            if (minLineLengthMm <= 0) minLineLengthMm = 400.0;
            if (lineToleranceMm <= 0) lineToleranceMm = 25.0;
            if (minPointsPerLine < 4) minPointsPerLine = 4;
            if (maxLines < 1) maxLines = 1;

            int rawCount = xyMm?.Count ?? 0;
            if (rawCount < minPointsPerLine)
            {
                return new JObject
                {
                    ["raw_points"] = rawCount,
                    ["downsampled_points"] = rawCount,
                    ["segments"] = new JArray(),
                    ["wall_candidates"] = new JArray(),
                    ["verdict"] = "insufficient_points"
                };
            }

            // Voxel downsample in XY to normalize density and keep runtime predictable.
            var voxels = new Dictionary<(int, int), (double sx, double sy, int n)>();
            for (int i = 0; i < rawCount; i++)
            {
                double[] p = xyMm![i];
                if (p == null || p.Length < 2) continue;
                int ix = (int)Math.Floor(p[0] / voxelMm);
                int iy = (int)Math.Floor(p[1] / voxelMm);
                var key = (ix, iy);
                if (voxels.TryGetValue(key, out var acc))
                    voxels[key] = (acc.sx + p[0], acc.sy + p[1], acc.n + 1);
                else
                    voxels[key] = (p[0], p[1], 1);
            }

            var pts = new List<(double x, double y)>(voxels.Count);
            foreach (var kv in voxels.Values)
                pts.Add((kv.sx / kv.n, kv.sy / kv.n));

            // Keep deterministic order independent of dictionary hash.
            pts.Sort((a, b) =>
            {
                int cmp = a.x.CompareTo(b.x);
                return cmp != 0 ? cmp : a.y.CompareTo(b.y);
            });

            var remaining = new List<int>(pts.Count);
            for (int i = 0; i < pts.Count; i++) remaining.Add(i);

            var rng = new Random(42);
            var segments = new List<Segment2D>();
            double gapThresholdMm = Math.Max(250.0, 6.0 * voxelMm);

            for (int iter = 0; iter < maxLines && remaining.Count >= minPointsPerLine; iter++)
            {
                int trials = Math.Min(250, Math.Max(60, remaining.Count / 2));
                int bestCount = 0;
                double bestAx = 0, bestAy = 0, bestNx = 0, bestNy = 0;

                for (int t = 0; t < trials; t++)
                {
                    int i1 = remaining[rng.Next(remaining.Count)];
                    int i2 = remaining[rng.Next(remaining.Count)];
                    if (i1 == i2) continue;
                    double dx = pts[i2].x - pts[i1].x;
                    double dy = pts[i2].y - pts[i1].y;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 100.0) continue;
                    double nx = -dy / len, ny = dx / len;
                    double ax = pts[i1].x, ay = pts[i1].y;

                    int inliers = 0;
                    for (int k = 0; k < remaining.Count; k++)
                    {
                        var p = pts[remaining[k]];
                        double d = Math.Abs((p.x - ax) * nx + (p.y - ay) * ny);
                        if (d <= lineToleranceMm) inliers++;
                    }
                    if (inliers > bestCount)
                    {
                        bestCount = inliers;
                        bestAx = ax; bestAy = ay; bestNx = nx; bestNy = ny;
                    }
                }

                if (bestCount < minPointsPerLine) break;

                // Collect inliers of winning line and refine via 2D PCA.
                var lineInliers = new List<int>(bestCount);
                for (int k = 0; k < remaining.Count; k++)
                {
                    int idx = remaining[k];
                    var p = pts[idx];
                    if (Math.Abs((p.x - bestAx) * bestNx + (p.y - bestAy) * bestNy) <= lineToleranceMm)
                        lineInliers.Add(idx);
                }

                FitLine2D(pts, lineInliers, out double cx, out double cy, out double ux, out double uy);
                double nxRef = -uy, nyRef = ux;

                // Project along line direction and split into contiguous spans (gaps = doorways/openings).
                var projected = new List<(double t, int idx, double dist)>(lineInliers.Count);
                foreach (int idx in lineInliers)
                {
                    var p = pts[idx];
                    double t = (p.x - cx) * ux + (p.y - cy) * uy;
                    double dist = Math.Abs((p.x - cx) * nxRef + (p.y - cy) * nyRef);
                    projected.Add((t, idx, dist));
                }
                projected.Sort((a, b) => a.t.CompareTo(b.t));

                var consumed = new HashSet<int>();
                int spanStart = 0;
                for (int i = 1; i <= projected.Count; i++)
                {
                    bool isBreak = i == projected.Count || (projected[i].t - projected[i - 1].t) > gapThresholdMm;
                    if (!isBreak) continue;

                    int spanCount = i - spanStart;
                    double t0 = projected[spanStart].t;
                    double t1 = projected[i - 1].t;
                    double spanLen = t1 - t0;
                    if (spanCount >= minPointsPerLine && spanLen >= minLineLengthMm)
                    {
                        var res = new List<double>(spanCount);
                        for (int j = spanStart; j < i; j++)
                        {
                            res.Add(projected[j].dist);
                            consumed.Add(projected[j].idx);
                        }
                        res.Sort();
                        double x1 = cx + ux * t0, y1 = cy + uy * t0;
                        double x2 = cx + ux * t1, y2 = cy + uy * t1;
                        double angle = Math.Atan2(uy, ux) * 180.0 / Math.PI;
                        if (angle < 0) angle += 180.0;
                        if (angle >= 180.0) angle -= 180.0;

                        segments.Add(new Segment2D
                        {
                            Id = segments.Count + 1,
                            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                            Ux = ux, Uy = uy, Nx = nxRef, Ny = nyRef,
                            LengthMm = spanLen,
                            AngleDeg = angle,
                            PointCount = spanCount,
                            P95ResidualMm = PercentileSorted(res, 0.95)
                        });
                    }
                    spanStart = i;
                }

                if (consumed.Count == 0)
                {
                    // Remove raw inliers so we do not spin forever on short noisy clusters.
                    foreach (int idx in lineInliers) consumed.Add(idx);
                }

                remaining = remaining.Where(idx => !consumed.Contains(idx)).ToList();
            }

            // Pair parallel opposing segments into double-face walls.
            var wallCandidates = new JArray();
            const double maxAngleDiffDeg = 5.0;
            const double minOverlapMm = 300.0;

            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Paired) continue;
                Segment2D a = segments[i];
                int bestPartner = -1;
                double bestThickness = double.MaxValue;
                double bestOv0 = 0, bestOv1 = 0;

                for (int j = i + 1; j < segments.Count; j++)
                {
                    if (segments[j].Paired) continue;
                    Segment2D b = segments[j];
                    double dAng = Math.Abs(a.AngleDeg - b.AngleDeg);
                    if (dAng > 90.0) dAng = 180.0 - dAng;
                    if (dAng > maxAngleDiffDeg) continue;

                    // Perpendicular distance between segment midpoints.
                    double bmx = 0.5 * (b.X1 + b.X2), bmy = 0.5 * (b.Y1 + b.Y2);
                    double amx = 0.5 * (a.X1 + a.X2), amy = 0.5 * (a.Y1 + a.Y2);
                    double perp = Math.Abs((bmx - amx) * a.Nx + (bmy - amy) * a.Ny);
                    if (perp < minWallThicknessMm || perp > maxWallThicknessMm) continue;

                    // Longitudinal overlap along A's direction.
                    double a0 = 0, a1 = a.LengthMm;
                    double bt1 = (b.X1 - a.X1) * a.Ux + (b.Y1 - a.Y1) * a.Uy;
                    double bt2 = (b.X2 - a.X1) * a.Ux + (b.Y2 - a.Y1) * a.Uy;
                    double b0 = Math.Min(bt1, bt2), b1 = Math.Max(bt1, bt2);
                    double ov0 = Math.Max(a0, b0), ov1 = Math.Min(a1, b1);
                    double overlap = ov1 - ov0;
                    if (overlap < minOverlapMm) continue;

                    if (perp < bestThickness)
                    {
                        bestThickness = perp;
                        bestPartner = j;
                        bestOv0 = ov0;
                        bestOv1 = ov1;
                    }
                }

                if (bestPartner >= 0)
                {
                    Segment2D b = segments[bestPartner];
                    a.Paired = true;
                    b.Paired = true;
                    double bmx = 0.5 * (b.X1 + b.X2), bmy = 0.5 * (b.Y1 + b.Y2);
                    double signedPerp = (bmx - a.X1) * a.Nx + (bmy - a.Y1) * a.Ny;
                    double shift = 0.5 * signedPerp;

                    double cx1 = a.X1 + a.Ux * bestOv0 + a.Nx * shift;
                    double cy1 = a.Y1 + a.Uy * bestOv0 + a.Ny * shift;
                    double cx2 = a.X1 + a.Ux * bestOv1 + a.Nx * shift;
                    double cy2 = a.Y1 + a.Uy * bestOv1 + a.Ny * shift;
                    double p95 = Math.Max(a.P95ResidualMm, b.P95ResidualMm);

                    wallCandidates.Add(new JObject
                    {
                        ["confidence"] = "double_face",
                        ["start_mm"] = new JArray(Math.Round(cx1, 1), Math.Round(cy1, 1)),
                        ["end_mm"] = new JArray(Math.Round(cx2, 1), Math.Round(cy2, 1)),
                        ["length_mm"] = Math.Round(bestOv1 - bestOv0, 1),
                        ["estimated_thickness_mm"] = Math.Round(bestThickness, 1),
                        ["angle_deg"] = Math.Round(a.AngleDeg, 2),
                        ["p95_residual_mm"] = Math.Round(p95, 1),
                        ["loa"] = ClassifyLoa(p95),
                        ["segment_ids"] = new JArray(a.Id, b.Id)
                    });
                }
            }

            // Include unpaired segments as single_face wall candidates so nothing is silently dropped.
            int singleFaceCount = 0;
            foreach (Segment2D s in segments)
            {
                if (s.Paired) continue;
                singleFaceCount++;
                wallCandidates.Add(new JObject
                {
                    ["confidence"] = "single_face",
                    ["start_mm"] = new JArray(Math.Round(s.X1, 1), Math.Round(s.Y1, 1)),
                    ["end_mm"] = new JArray(Math.Round(s.X2, 1), Math.Round(s.Y2, 1)),
                    ["length_mm"] = Math.Round(s.LengthMm, 1),
                    ["estimated_thickness_mm"] = JValue.CreateNull(),
                    ["angle_deg"] = Math.Round(s.AngleDeg, 2),
                    ["p95_residual_mm"] = Math.Round(s.P95ResidualMm, 1),
                    ["loa"] = ClassifyLoa(s.P95ResidualMm),
                    ["segment_ids"] = new JArray(s.Id)
                });
            }

            var segJson = new JArray();
            foreach (Segment2D s in segments)
            {
                segJson.Add(new JObject
                {
                    ["id"] = s.Id,
                    ["start_mm"] = new JArray(Math.Round(s.X1, 1), Math.Round(s.Y1, 1)),
                    ["end_mm"] = new JArray(Math.Round(s.X2, 1), Math.Round(s.Y2, 1)),
                    ["length_mm"] = Math.Round(s.LengthMm, 1),
                    ["angle_deg"] = Math.Round(s.AngleDeg, 2),
                    ["points"] = s.PointCount,
                    ["p95_residual_mm"] = Math.Round(s.P95ResidualMm, 1),
                    ["paired"] = s.Paired
                });
            }

            return new JObject
            {
                ["raw_points"] = rawCount,
                ["downsampled_points"] = pts.Count,
                ["voxel_mm"] = Math.Round(voxelMm, 1),
                ["segments_found"] = segments.Count,
                ["double_face_walls"] = wallCandidates.Count - singleFaceCount,
                ["single_face_walls"] = singleFaceCount,
                ["wall_candidates"] = wallCandidates,
                ["segments"] = segJson,
                ["verdict"] = segments.Count > 0 ? "segments_extracted" : "no_segments_found"
            };
        }

        // ---------------------------------------------------------------------
        // 3. 3D PRIMITIVE FITTING (PLANE / COLUMN / CYLINDER)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Fits a geometric primitive ("plane", "column", or "cylinder") to 3D points in mm.
        /// </summary>
        public static JObject FitPrimitive(IList<double[]> xyzMm, string kind, double toleranceMm = 15.0)
        {
            string k = (kind ?? "plane").Trim().ToLowerInvariant();
            if (k != "plane" && k != "column" && k != "cylinder")
                throw new ArgumentException("primitive_kind must be 'plane', 'column', or 'cylinder'.");
            if (toleranceMm <= 0) toleranceMm = 15.0;

            int n = xyzMm?.Count ?? 0;
            if (n < 15)
            {
                return new JObject
                {
                    ["primitive_kind"] = k,
                    ["points"] = n,
                    ["verdict"] = "insufficient_points",
                    ["note"] = "At least 15 points are required inside the region to fit a primitive."
                };
            }

            if (k == "plane") return FitPlane(xyzMm!, toleranceMm);
            if (k == "column") return FitColumn(xyzMm!, toleranceMm);
            return FitCylinder(xyzMm!, toleranceMm);
        }

        private static JObject FitPlane(IList<double[]> pts, double toleranceMm)
        {
            int n = pts.Count;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += pts[i][0]; cy += pts[i][1]; cz += pts[i][2]; }
            cx /= n; cy /= n; cz /= n;

            ComputePca3D(pts, cx, cy, cz, out double[] evals, out double[][] evecs);
            // Smallest eigenvalue -> normal; largest two -> in-plane axes.
            double[] normal = evecs[0];
            if (normal[2] < 0 || (Math.Abs(normal[2]) < 1e-6 && normal[0] < 0))
                normal = new[] { -normal[0], -normal[1], -normal[2] };

            double[] axisU = evecs[2];
            double[] axisV = evecs[1];

            var absDist = new List<double>(n);
            double uMin = double.MaxValue, uMax = double.MinValue;
            double vMin = double.MaxValue, vMax = double.MinValue;
            int inliers = 0;

            for (int i = 0; i < n; i++)
            {
                double dx = pts[i][0] - cx, dy = pts[i][1] - cy, dz = pts[i][2] - cz;
                double d = Math.Abs(dx * normal[0] + dy * normal[1] + dz * normal[2]);
                absDist.Add(d);
                if (d <= toleranceMm) inliers++;
                double u = dx * axisU[0] + dy * axisU[1] + dz * axisU[2];
                double v = dx * axisV[0] + dy * axisV[1] + dz * axisV[2];
                if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                if (v < vMin) vMin = v; if (v > vMax) vMax = v;
            }
            absDist.Sort();
            double p95 = PercentileSorted(absDist, 0.95);
            double nz = Math.Min(1.0, Math.Abs(normal[2]));
            double tiltFromHoriz = Math.Acos(nz) * 180.0 / Math.PI;
            double tiltFromVert = Math.Abs(90.0 - tiltFromHoriz);

            return new JObject
            {
                ["primitive_kind"] = "plane",
                ["points"] = n,
                ["centroid_mm"] = new JArray(Math.Round(cx, 1), Math.Round(cy, 1), Math.Round(cz, 1)),
                ["normal"] = new JArray(Math.Round(normal[0], 5), Math.Round(normal[1], 5), Math.Round(normal[2], 5)),
                ["extents_mm"] = new JArray(Math.Round(uMax - uMin, 1), Math.Round(vMax - vMin, 1)),
                ["tilt_from_horizontal_deg"] = Math.Round(tiltFromHoriz, 2),
                ["tilt_from_vertical_deg"] = Math.Round(tiltFromVert, 2),
                ["mean_abs_residual_mm"] = Math.Round(absDist.Average(), 2),
                ["p95_residual_mm"] = Math.Round(p95, 2),
                ["inlier_share"] = Math.Round(inliers / (double)n, 4),
                ["tolerance_mm"] = toleranceMm,
                ["loa"] = ClassifyLoa(p95),
                ["verdict"] = p95 <= toleranceMm ? "fitted_ok" : "high_residual"
            };
        }

        private static JObject FitColumn(IList<double[]> pts, double toleranceMm)
        {
            int n = pts.Count;
            double cx = 0, cy = 0, zMin = double.MaxValue, zMax = double.MinValue;
            var xy = new List<(double x, double y)>(n);
            var idxs = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                double x = pts[i][0], y = pts[i][1], z = pts[i][2];
                cx += x; cy += y;
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
                xy.Add((x, y));
                idxs.Add(i);
            }
            cx /= n; cy /= n;

            // Oriented rectangle via 2D PCA.
            FitLine2D(xy, idxs, out _, out _, out double ux, out double uy);
            double vx = -uy, vy = ux;
            var us = new List<double>(n);
            var vs = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double dx = xy[i].x - cx, dy = xy[i].y - cy;
                us.Add(dx * ux + dy * uy);
                vs.Add(dx * vx + dy * vy);
            }
            us.Sort(); vs.Sort();
            double uLo = PercentileSorted(us, 0.02), uHi = PercentileSorted(us, 0.98);
            double vLo = PercentileSorted(vs, 0.02), vHi = PercentileSorted(vs, 0.98);
            double width = uHi - uLo;
            double depth = vHi - vLo;
            double uMid = 0.5 * (uLo + uHi), vMid = 0.5 * (vLo + vHi);
            double rcx = cx + uMid * ux + vMid * vx;
            double rcy = cy + uMid * uy + vMid * vy;

            // Residual to nearest edge of the fitted rectangle.
            double halfW = 0.5 * width, halfD = 0.5 * depth;
            var rectErr = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double dx = xy[i].x - rcx, dy = xy[i].y - rcy;
                double lu = Math.Abs(dx * ux + dy * uy);
                double lv = Math.Abs(dx * vx + dy * vy);
                double du = Math.Abs(lu - halfW);
                double dv = Math.Abs(lv - halfD);
                rectErr.Add(Math.Min(du, dv));
            }
            rectErr.Sort();
            double rectP95 = PercentileSorted(rectErr, 0.95);

            // Compare against circular fit in XY.
            FitCircle2D(xy, out double ccx, out double ccy, out double radius);
            var circErr = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double r = Math.Sqrt((xy[i].x - ccx) * (xy[i].x - ccx) + (xy[i].y - ccy) * (xy[i].y - ccy));
                circErr.Add(Math.Abs(r - radius));
            }
            circErr.Sort();
            double circP95 = PercentileSorted(circErr, 0.95);

            bool isCircular = radius > 25.0 && circP95 < rectP95 * 0.85;
            double bestP95 = isCircular ? circP95 : rectP95;
            double angle = Math.Atan2(uy, ux) * 180.0 / Math.PI;
            if (angle < 0) angle += 180.0;

            return new JObject
            {
                ["primitive_kind"] = "column",
                ["points"] = n,
                ["shape_hint"] = isCircular ? "circular" : "rectangular",
                ["center_xy_mm"] = isCircular
                    ? new JArray(Math.Round(ccx, 1), Math.Round(ccy, 1))
                    : new JArray(Math.Round(rcx, 1), Math.Round(rcy, 1)),
                ["z_min_mm"] = Math.Round(zMin, 1),
                ["z_max_mm"] = Math.Round(zMax, 1),
                ["height_mm"] = Math.Round(zMax - zMin, 1),
                ["width_mm"] = Math.Round(width, 1),
                ["depth_mm"] = Math.Round(depth, 1),
                ["angle_deg"] = Math.Round(angle, 2),
                ["diameter_mm"] = Math.Round(2.0 * radius, 1),
                ["rect_p95_mm"] = Math.Round(rectP95, 2),
                ["circular_p95_mm"] = Math.Round(circP95, 2),
                ["p95_residual_mm"] = Math.Round(bestP95, 2),
                ["loa"] = ClassifyLoa(bestP95),
                ["verdict"] = bestP95 <= toleranceMm ? "fitted_ok" : "high_residual"
            };
        }

        private static JObject FitCylinder(IList<double[]> pts, double toleranceMm)
        {
            int n = pts.Count;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += pts[i][0]; cy += pts[i][1]; cz += pts[i][2]; }
            cx /= n; cy /= n; cz /= n;

            ComputePca3D(pts, cx, cy, cz, out _, out double[][] evecs);
            // Largest eigenvector is the cylinder axis (pipe/duct centerline direction).
            double[] axis = evecs[2];
            double[] uAxis = evecs[1];
            double[] vAxis = evecs[0];

            var proj2d = new List<(double x, double y)>(n);
            double tMin = double.MaxValue, tMax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double dx = pts[i][0] - cx, dy = pts[i][1] - cy, dz = pts[i][2] - cz;
                double t = dx * axis[0] + dy * axis[1] + dz * axis[2];
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
                double pu = dx * uAxis[0] + dy * uAxis[1] + dz * uAxis[2];
                double pv = dx * vAxis[0] + dy * vAxis[1] + dz * vAxis[2];
                proj2d.Add((pu, pv));
            }

            FitCircle2D(proj2d, out double cu, out double cv, out double radius);
            double axX = cx + cu * uAxis[0] + cv * vAxis[0];
            double axY = cy + cu * uAxis[1] + cv * vAxis[1];
            double axZ = cz + cu * uAxis[2] + cv * vAxis[2];

            double[] start = { axX + tMin * axis[0], axY + tMin * axis[1], axZ + tMin * axis[2] };
            double[] end = { axX + tMax * axis[0], axY + tMax * axis[1], axZ + tMax * axis[2] };

            var radialErr = new List<double>(n);
            int inliers = 0;
            for (int i = 0; i < n; i++)
            {
                double du = proj2d[i].x - cu, dv = proj2d[i].y - cv;
                double err = Math.Abs(Math.Sqrt(du * du + dv * dv) - radius);
                radialErr.Add(err);
                if (err <= toleranceMm) inliers++;
            }
            radialErr.Sort();
            double p95 = PercentileSorted(radialErr, 0.95);
            double horizLen = Math.Sqrt((end[0] - start[0]) * (end[0] - start[0]) + (end[1] - start[1]) * (end[1] - start[1]));
            double slopePct = horizLen > 1.0 ? Math.Abs(end[2] - start[2]) / horizLen * 100.0 : 0.0;

            return new JObject
            {
                ["primitive_kind"] = "cylinder",
                ["points"] = n,
                ["start_mm"] = new JArray(Math.Round(start[0], 1), Math.Round(start[1], 1), Math.Round(start[2], 1)),
                ["end_mm"] = new JArray(Math.Round(end[0], 1), Math.Round(end[1], 1), Math.Round(end[2], 1)),
                ["axis"] = new JArray(Math.Round(axis[0], 5), Math.Round(axis[1], 5), Math.Round(axis[2], 5)),
                ["length_mm"] = Math.Round(tMax - tMin, 1),
                ["outer_diameter_mm"] = Math.Round(2.0 * radius, 1),
                ["radius_mm"] = Math.Round(radius, 1),
                ["slope_percent"] = Math.Round(slopePct, 3),
                ["p95_residual_mm"] = Math.Round(p95, 2),
                ["inlier_share"] = Math.Round(inliers / (double)n, 4),
                ["tolerance_mm"] = toleranceMm,
                ["loa"] = ClassifyLoa(p95),
                ["verdict"] = p95 <= toleranceMm && radius > 5.0 ? "fitted_ok" : "high_residual"
            };
        }

        // ---------------------------------------------------------------------
        // MATH HELPERS (2D PCA, 3D Jacobi PCA, Kasa Circle Fit, Percentiles)
        // ---------------------------------------------------------------------

        private static void FitLine2D(
            List<(double x, double y)> pts,
            List<int> indices,
            out double cx, out double cy, out double ux, out double uy)
        {
            cx = 0; cy = 0;
            int n = indices.Count;
            for (int i = 0; i < n; i++) { var p = pts[indices[i]]; cx += p.x; cy += p.y; }
            cx /= n; cy /= n;

            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                var p = pts[indices[i]];
                double dx = p.x - cx, dy = p.y - cy;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
            double theta = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
            ux = Math.Cos(theta);
            uy = Math.Sin(theta);
        }

        /// <summary>
        /// Kasa algebraic circle fit in 2D: solves [2x, 2y, 1] * [cx, cy, c] = x^2 + y^2.
        /// </summary>
        private static void FitCircle2D(List<(double x, double y)> pts, out double cx, out double cy, out double r)
        {
            int n = pts.Count;
            double mx = 0, my = 0;
            for (int i = 0; i < n; i++) { mx += pts[i].x; my += pts[i].y; }
            mx /= n; my /= n;

            double suu = 0, suv = 0, svv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            for (int i = 0; i < n; i++)
            {
                double u = pts[i].x - mx, v = pts[i].y - my;
                double uu = u * u, vv = v * v;
                suu += uu; suv += u * v; svv += vv;
                suuu += uu * u; svvv += vv * v;
                suvv += u * vv; svuu += v * uu;
            }

            double det = suu * svv - suv * suv;
            if (Math.Abs(det) < 1e-9)
            {
                cx = mx; cy = my; r = 0;
                return;
            }
            double rhs1 = 0.5 * (suuu + suvv);
            double rhs2 = 0.5 * (svvv + svuu);
            double uc = (rhs1 * svv - rhs2 * suv) / det;
            double vc = (suu * rhs2 - suv * rhs1) / det;
            cx = mx + uc;
            cy = my + vc;
            double r2 = uc * uc + vc * vc + (suu + svv) / n;
            r = r2 > 0 ? Math.Sqrt(r2) : 0;
        }

        /// <summary>
        /// 3x3 symmetric covariance eigen-decomposition via Jacobi rotations.
        /// Returns eigenvalues and unit eigenvectors sorted ascending (0=smallest, 2=largest).
        /// </summary>
        private static void ComputePca3D(
            IList<double[]> pts, double cx, double cy, double cz,
            out double[] evals, out double[][] evecs)
        {
            double[,] a = new double[3, 3];
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                double x = pts[i][0] - cx, y = pts[i][1] - cy, z = pts[i][2] - cz;
                a[0, 0] += x * x; a[0, 1] += x * y; a[0, 2] += x * z;
                a[1, 1] += y * y; a[1, 2] += y * z; a[2, 2] += z * z;
            }
            a[1, 0] = a[0, 1]; a[2, 0] = a[0, 2]; a[2, 1] = a[1, 2];

            double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 25; sweep++)
            {
                int p = 0, q = 1;
                double maxOff = Math.Abs(a[0, 1]);
                if (Math.Abs(a[0, 2]) > maxOff) { p = 0; q = 2; maxOff = Math.Abs(a[0, 2]); }
                if (Math.Abs(a[1, 2]) > maxOff) { p = 1; q = 2; maxOff = Math.Abs(a[1, 2]); }
                if (maxOff < 1e-12) break;

                double app = a[p, p], aqq = a[q, q], apq = a[p, q];
                double phi = 0.5 * Math.Atan2(2.0 * apq, aqq - app);
                double c = Math.Cos(phi), s = Math.Sin(phi);

                for (int k = 0; k < 3; k++)
                {
                    if (k == p || k == q) continue;
                    double akp = a[k, p], akq = a[k, q];
                    a[k, p] = a[p, k] = c * akp - s * akq;
                    a[k, q] = a[q, k] = s * akp + c * akq;
                }
                a[p, p] = c * c * app - 2.0 * s * c * apq + s * s * aqq;
                a[q, q] = s * s * app + 2.0 * s * c * apq + c * c * aqq;
                a[p, q] = a[q, p] = 0;

                for (int k = 0; k < 3; k++)
                {
                    double vkp = v[k, p], vkq = v[k, q];
                    v[k, p] = c * vkp - s * vkq;
                    v[k, q] = s * vkp + c * vkq;
                }
            }

            var pairs = new[]
            {
                (val: a[0, 0], vec: new[] { v[0, 0], v[1, 0], v[2, 0] }),
                (val: a[1, 1], vec: new[] { v[0, 1], v[1, 1], v[2, 1] }),
                (val: a[2, 2], vec: new[] { v[0, 2], v[1, 2], v[2, 2] })
            };
            Array.Sort(pairs, (x, y) => x.val.CompareTo(y.val));
            evals = new[] { pairs[0].val, pairs[1].val, pairs[2].val };
            evecs = new[] { pairs[0].vec, pairs[1].vec, pairs[2].vec };
        }

        private static double PercentileSorted(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            double idx = p * (sorted.Count - 1);
            int lo = (int)Math.Floor(idx);
            int hi = Math.Min(sorted.Count - 1, lo + 1);
            double frac = idx - lo;
            return sorted[lo] * (1.0 - frac) + sorted[hi] * frac;
        }
    }
}
