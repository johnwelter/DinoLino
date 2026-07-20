using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Shared 2D polyline and polygon helpers used by the outline tools.
    /// </summary>
    public static class PolylineGeometry
    {
        /// <summary>
        /// Squared distance threshold for treating two vertices as the same point.
        /// </summary>
        public const double ClosureEpsilonSquared = 1.0;

        // =====================
        // Closure helpers
        // =====================

        /// <summary>
        /// Returns true when the last point duplicates the first within closure tolerance.
        /// </summary>
        public static bool HasClosureDuplicate(IList<Point> pts)
        {
            if (pts == null || pts.Count < 2) return false;
            Point f = pts[0], l = pts[pts.Count - 1];
            double dx = f.X - l.X, dy = f.Y - l.Y;
            return dx * dx + dy * dy < ClosureEpsilonSquared;
        }

        /// <summary>
        /// Removes a trailing closure point in place, if present.
        /// </summary>
        public static bool StripClosureDuplicate(List<Point> pts)
        {
            if (!HasClosureDuplicate(pts)) return false;
            pts.RemoveAt(pts.Count - 1);
            return true;
        }

        // =====================
        // Rasterization
        // =====================

        /// <summary>
        /// Rasterizes a closed polygon into a boolean mask using even-odd fill.
        /// </summary>
        public static bool[] RasterizePolygon(List<Point> pts, int w, int h)
        {
            var mask = new bool[w * h];
            int n = pts.Count;
            if (n < 3) return mask;

            var xs = new List<double>(8);
            for (int y = 0; y < h; y++)
            {
                double scanY = y + 0.5;
                xs.Clear();

                // Collect edge crossings for this scanline.
                for (int i = 0; i < n; i++)
                {
                    Point a = pts[i], b = pts[(i + 1) % n];
                    double ay = a.Y, by = b.Y;
                    if ((ay <= scanY && by > scanY) || (by <= scanY && ay > scanY))
                    {
                        double t = (scanY - ay) / (by - ay);
                        xs.Add(a.X + t * (b.X - a.X));
                    }
                }

                if (xs.Count < 2) continue;
                xs.Sort();

                // Fill each inside span between crossing pairs.
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int xStart = (int)Math.Ceiling(xs[k] - 0.5);
                    int xEnd = (int)Math.Floor(xs[k + 1] - 0.5);
                    if (xStart < 0) xStart = 0;
                    if (xEnd > w - 1) xEnd = w - 1;

                    int row = y * w;
                    for (int x = xStart; x <= xEnd; x++) mask[row + x] = true;
                }
            }

            return mask;
        }

        // =====================
        // Intersection tests
        // =====================

        /// <summary>
        /// Returns true when any non-adjacent segments of a closed polyline cross.
        /// </summary>
        public static bool HasSelfIntersection(IList<Point> pts)
        {
            int n = pts.Count;
            if (n < 4) return false;

            for (int i = 0; i < n; i++)
            {
                Point a1 = pts[i], a2 = pts[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i) continue;
                    if ((i + 1) % n == j || (j + 1) % n == i) continue;

                    Point b1 = pts[j], b2 = pts[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true when two line segments cross.
        /// </summary>
        public static bool SegmentsIntersect(Point p1, Point p2, Point p3, Point p4)
        {
            double d1 = Cross(p3, p4, p1);
            double d2 = Cross(p3, p4, p2);
            double d3 = Cross(p1, p2, p3);
            double d4 = Cross(p1, p2, p4);

            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        /// <summary>
        /// Computes the intersection point for two crossing segments.
        /// </summary>
        public static bool TryGetSegmentIntersection(Point p1, Point p2, Point p3, Point p4, out Point hit)
        {
            hit = default;

            double d1x = p2.X - p1.X, d1y = p2.Y - p1.Y;
            double d2x = p4.X - p3.X, d2y = p4.Y - p3.Y;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-9) return false;

            double t = ((p3.X - p1.X) * d2y - (p3.Y - p1.Y) * d2x) / denom;
            double u = ((p3.X - p1.X) * d1y - (p3.Y - p1.Y) * d1x) / denom;

            if (t < 0.0 || t > 1.0 || u < 0.0 || u > 1.0) return false;

            hit = new Point(p1.X + t * d1x, p1.Y + t * d1y);
            return true;
        }

        /// <summary>
        /// Cross product used by the segment orientation test.
        /// </summary>
        public static double Cross(Point a, Point b, Point c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        // =====================
        // Arc utilities
        // =====================

        /// <summary>
        /// Returns the index of the point closest to the target.
        /// </summary>
        public static int NearestIndex(List<Point> pts, Point target)
        {
            int best = -1;
            double bestD = double.MaxValue;

            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i].X - target.X, dy = pts[i].Y - target.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }

            return best;
        }

        /// <summary>
        /// Extracts one arc from a cyclic point list, including both endpoints.
        /// </summary>
        public static List<Point> ExtractArc(List<Point> pts, int i, int j, bool forward)
        {
            int n = pts.Count;
            var arc = new List<Point> { pts[i] };
            int idx = i, guard = n + 1;

            while (idx != j && guard-- > 0)
            {
                idx = forward ? (idx + 1) % n : (idx - 1 + n) % n;
                arc.Add(pts[idx]);
            }

            return arc;
        }

        // =====================
        // Uniform resampling
        // =====================

        /// <summary>
        /// Resamples a closed polyline to roughly uniform spacing along its perimeter.
        /// </summary>
        public static List<Point> ResampleClosedUniformSpacing(List<Point> points, double targetSpacing)
        {
            if (points == null || points.Count < 3) return points;

            // Work on the distinct vertices only; preserve the closure point separately.
            int n = points.Count;
            bool hasClosure = HasClosureDuplicate(points);
            int open = hasClosure ? n - 1 : n;

            var lengths = new double[open];
            lengths[0] = 0;

            // Build cumulative arc length across the open vertex chain.
            for (int i = 1; i < open; i++)
            {
                double dx = points[i].X - points[i - 1].X;
                double dy = points[i].Y - points[i - 1].Y;
                lengths[i] = lengths[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }

            double dxLast = points[0].X - points[open - 1].X;
            double dyLast = points[0].Y - points[open - 1].Y;
            double totalLength = lengths[open - 1] + Math.Sqrt(dxLast * dxLast + dyLast * dyLast);

            if (totalLength < 1e-6) return points;

            int count = Math.Max(3, (int)Math.Round(totalLength / targetSpacing));
            double step = totalLength / count;

            var result = new List<Point>(count + 1);
            int seg = 0;

            for (int k = 0; k < count; k++)
            {
                double target = k * step;

                // Advance to the segment containing the current target arc length.
                while (seg < open - 1 && lengths[seg + 1] < target) seg++;

                double segStart = lengths[seg];
                double segEnd = seg < open - 1
                    ? lengths[seg + 1]
                    : lengths[open - 1] + Math.Sqrt(dxLast * dxLast + dyLast * dyLast);

                double t = segEnd > segStart ? (target - segStart) / (segEnd - segStart) : 0;

                Point a = points[seg];
                Point b = seg < open - 1 ? points[seg + 1] : points[0];
                result.Add(new Point(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
            }

            if (hasClosure)
                result.Add(result[0]);

            return result;
        }
    }
}