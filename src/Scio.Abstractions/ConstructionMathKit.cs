using System;
using System.Collections.Generic;

namespace Scio.Abstractions
{
    public sealed class Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }
        public Point2D() { }
        public Point2D(double x, double y) { X = x; Y = y; }
    }

    /// <summary>
    /// Pure geometry helpers for Ola 3 construction modeling & structural grid planning.
    /// </summary>
    public static class ConstructionMathKit
    {
        /// <summary>
        /// Computes 2D intersection of two line segments (p1-p2 and p3-p4). Returns null if parallel or disjoint.
        /// </summary>
        public static Point2D? IntersectSegments2D(Point2D p1, Point2D p2, Point2D p3, Point2D p4, double eps = 1e-6)
        {
            double x1 = p1.X, y1 = p1.Y, x2 = p2.X, y2 = p2.Y;
            double x3 = p3.X, y3 = p3.Y, x4 = p4.X, y4 = p4.Y;

            double denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (Math.Abs(denom) < eps) return null;

            double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denom;
            double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / denom;

            if (t < -eps || t > 1.0 + eps || u < -eps || u > 1.0 + eps) return null;
            return new Point2D(x1 + t * (x2 - x1), y1 + t * (y2 - y1));
        }

        /// <summary>
        /// If a segment (p1->p2) is within maxAngleDeg of horizontal (0/180) or vertical (90/270),
        /// returns the snapped endpoint p2' keeping p1 fixed and preserving length; otherwise null.
        /// </summary>
        public static Point2D? SnapEndpointOrthogonal(Point2D p1, Point2D p2, double maxAngleDeg = 3.0)
        {
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return null;

            double angleDeg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
            double[] cardinals = { -180.0, -90.0, 0.0, 90.0, 180.0 };
            double bestCardinal = 0.0;
            double bestDelta = double.MaxValue;
            foreach (double c in cardinals)
            {
                double d = Math.Abs(angleDeg - c);
                if (d < bestDelta) { bestDelta = d; bestCardinal = c; }
            }

            if (bestDelta < 1e-4 || bestDelta > maxAngleDeg) return null;
            double rad = bestCardinal * (Math.PI / 180.0);
            return new Point2D(p1.X + len * Math.Cos(rad), p1.Y + len * Math.Sin(rad));
        }

        /// <summary>
        /// Given compound slab layer thicknesses (top-to-bottom), returns the top elevation offset
        /// of each layer relative to the parent slab's top face (<= 0).
        /// </summary>
        public static List<double> ComputeSlabLayerTopOffsets(IReadOnlyList<double> layerThicknesses)
        {
            var offsets = new List<double>();
            double acc = 0.0;
            if (layerThicknesses == null) return offsets;
            foreach (double th in layerThicknesses)
            {
                offsets.Add(-acc);
                acc += Math.Max(0.0, th);
            }
            return offsets;
        }
    }
}
