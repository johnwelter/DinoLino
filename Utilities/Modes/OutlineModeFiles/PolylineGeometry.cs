using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Generic 2D polyline/polygon helpers shared by the outline tools
    /// (hand-draw closure detection, erase splicing, smoothing resample) and
    /// by OutlineMode's polyline construction. These used to be private
    /// duplicates inside OutlineMode; there is now exactly one copy.
    ///
    /// NOTE: these methods pair naturally with GeometryCalculations
    /// (DouglasPeucker, ResampleClosed, Perimeter, ...). If you prefer a
    /// single geometry home, they are all static and can be pasted into
    /// GeometryCalculations verbatim — nothing here holds state.
    /// </summary>
    public static class PolylineGeometry
    {
        // Squared-distance threshold under which two vertices count as "the
        // same point" for closure detection. Matches the &lt; 1.0 tests that were
        // repeated inline throughout OutlineMode.
        public const double ClosureEpsilonSquared = 1.0;

        /// <summary>
        /// True when the last vertex duplicates the first (within closure
        /// tolerance) — the "explicit closure point" convention the outline
        /// polylines use.
        /// </summary>
        public static bool HasClosureDuplicate(IList<Point> pts)
        {
            if (pts == null || pts.Count < 2) return false;
            Point f = pts[0], l = pts[pts.Count - 1];
            double dx = f.X - l.X, dy = f.Y - l.Y;
            return dx * dx + dy * dy < ClosureEpsilonSquared;
        }

        /// <summary>
        /// Removes the trailing closure duplicate in place, if present.
        /// Returns true when a point was removed.
        /// </summary>
        public static bool StripClosureDuplicate(List<Point> pts)
        {
            if (!HasClosureDuplicate(pts)) return false;
            pts.RemoveAt(pts.Count - 1);
            return true;
        }

        /// <summary>
        /// Even-odd scanline fill of a polygon into a bool mask. Points are in
        /// local mask coordinates with the closure duplicate removed.
        /// (Moved verbatim from OutlineMode's erase region.)
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

        /// <summary>
        /// True when any two non-adjacent segments of the closed polyline
        /// properly cross. (Moved verbatim from OutlineMode.)
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

        /// <summary>Proper-crossing test between segments p1–p2 and p3–p4.</summary>
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
        /// Segment/segment intersection returning the crossing point. Treats
        /// proper crossings only (no collinear-overlap handling — sufficient
        /// for a freehand stroke). Mirrors the sign logic in SegmentsIntersect
        /// but also computes the intersection coordinate.
        /// </summary>
        public static bool TryGetSegmentIntersection(Point p1, Point p2, Point p3, Point p4, out Point hit)
        {
            hit = default;

            double d1x = p2.X - p1.X, d1y = p2.Y - p1.Y;
            double d2x = p4.X - p3.X, d2y = p4.Y - p3.Y;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-9) return false; // parallel / degenerate

            double t = ((p3.X - p1.X) * d2y - (p3.Y - p1.Y) * d2x) / denom;
            double u = ((p3.X - p1.X) * d1y - (p3.Y - p1.Y) * d1x) / denom;

            if (t < 0.0 || t > 1.0 || u < 0.0 || u > 1.0) return false;

            hit = new Point(p1.X + t * d1x, p1.Y + t * d1y);
            return true;
        }

        public static double Cross(Point a, Point b, Point c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>Index of the point in pts closest to target.</summary>
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
        /// Sub-path of the cyclic list from index i to index j (endpoints
        /// included). forward = true walks i → i+1 → … → j; forward = false
        /// walks i → i-1 → … → j.
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

        /// <summary>
        /// Resamples a closed polyline to approximately uniform arc-length
        /// spacing. This ensures Laplacian smoothing applies equal pressure at
        /// every vertex, preventing corners from drifting based on local point
        /// density. The last point is assumed to be a closure duplicate of the
        /// first and is preserved as such after resampling.
        ///
        /// This is the single copy of the resampler formerly private to
        /// OutlineMode. GeometryCalculations.ResampleClosed remains the
        /// count-based sibling used for EFA; if their walks are identical,
        /// this method can later be reduced to
        ///   ResampleClosed(pts, round(perimeter / targetSpacing)).
        /// </summary>
        public static List<Point> ResampleClosedUniformSpacing(List<Point> points, double targetSpacing)
        {
            if (points == null || points.Count < 3) return points;

            // Build cumulative arc-length table (excluding the closure duplicate)
            int n = points.Count;
            bool hasClosure = HasClosureDuplicate(points);
            int open = hasClosure ? n - 1 : n; // number of distinct vertices

            var lengths = new double[open];
            lengths[0] = 0;
            for (int i = 1; i < open; i++)
            {
                double dx = points[i].X - points[i - 1].X;
                double dy = points[i].Y - points[i - 1].Y;
                lengths[i] = lengths[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            // Close the loop: distance from last distinct vertex back to first
            {
                double dx = points[0].X - points[open - 1].X;
                double dy = points[0].Y - points[open - 1].Y;
                double totalLength = lengths[open - 1] + Math.Sqrt(dx * dx + dy * dy);

                if (totalLength < 1e-6) return points;

                // How many evenly-spaced samples fit around the perimeter?
                int count = Math.Max(3, (int)Math.Round(totalLength / targetSpacing));
                double step = totalLength / count;

                var result = new List<Point>(count + 1);
                int seg = 0;
                for (int k = 0; k < count; k++)
                {
                    double target = k * step;
                    // Advance segment pointer
                    while (seg < open - 1 && lengths[seg + 1] < target) seg++;

                    // Interpolate within the current segment (wraps: last->first)
                    double segStart = lengths[seg];
                    double segEnd = seg < open - 1 ? lengths[seg + 1]
                                                   : lengths[open - 1] + Math.Sqrt(
                                                       (points[0].X - points[open - 1].X) * (points[0].X - points[open - 1].X) +
                                                       (points[0].Y - points[open - 1].Y) * (points[0].Y - points[open - 1].Y));
                    double t = (segEnd > segStart) ? (target - segStart) / (segEnd - segStart) : 0;

                    Point a = points[seg];
                    Point b = seg < open - 1 ? points[seg + 1] : points[0];
                    result.Add(new Point(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
                }

                // Re-add closure point
                if (hasClosure) result.Add(result[0]);
                return result;
            }
        }
    }
}