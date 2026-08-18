// Utilities/GeometryCalculations.cs
//
// A static utility class centralizing all geometric calculations shared across
// CurvatureMode, OutlineMode, GetAngleMode, and DrawMode.
//
// Callers pass in raw data (points, lengths, prior values) and receive results
// back as plain return values — no UI or WorkMode state is touched here.

using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities
{
    public static class GeometryCalculations
    {
        // =====================================================================
        // BUILD LOCAL BASIS
        /// Builds a local orthonormal basis along the chord from origin to target.
        /// xAxis points along the chord, yAxis is perpendicular (90° CCW).
        /// length is the chord length. Returns false if the points are coincident.
        // =====================================================================
        public static bool BuildLocalBasis(Vector2 origin, Vector2 target, out Vector2 xAxis, out Vector2 yAxis, out double length)
        {
            Vector2 ab = target - origin;
            length = ab.Magnitude();
            if (length < 1e-8) { xAxis = yAxis = new Vector2(0, 0); return false; }
            xAxis = new Vector2(ab.X / length, ab.Y / length);
            yAxis = new Vector2(-xAxis.Y, xAxis.X);
            return true;
        }
        // =====================================================================
        // ASPECT RATIO
        // Also called Rise-Span Ratio in CurvatureMode when the inputs are
        // (rise = bisector length, span = chord length).
        // =====================================================================

        /// Generic aspect ratio: longer dimension / shorter dimension.
        /// Used by DrawMode (shape bounding box) and as the base formula
        /// for all other aspect-ratio variants below.
        /// Returns 0 if the shorter dimension is effectively zero.
        public static double AspectRatio(double dimensionA, double dimensionB)
        {
            double longer = Math.Max(dimensionA, dimensionB);
            double shorter = Math.Min(dimensionA, dimensionB);
            return shorter > 1e-5 ? Math.Round(longer / shorter, 2) : 0;
        }

        /// Circular-arc aspect ratio: chord length / bisector (rise) length.
        /// The bisector runs from the chord midpoint to the arc apex (PointC).
        /// Returns 0 if the bisector is effectively zero (flat arc).
        public static double CircularArcAspectRatio(double chordLength, double bisectorLength)
        {
            return bisectorLength > 1e-5 ? Math.Round(chordLength / bisectorLength, 2) : 0;
        }

        /// Parabolic rise-span ratio: rise / span (chord length).
        /// Rise is the distance from the chord midpoint to PointC.
        /// Returns 0 if the chord is effectively zero.
        public static double RiseSpanRatio(double rise, double chordLength)
        {
            return chordLength > 1e-5 ? Math.Round(rise / chordLength, 2) : 0;
        }

        /// Triangle aspect ratio: longest side / triangle height.
        /// Height is derived from area so no explicit altitude point is needed.
        /// Returns 0 if the height is effectively zero (degenerate triangle).
        public static double TriangleAspectRatio(double longestSide, double triangleArea)
        {
            double height = triangleArea > 1e-5 ? (triangleArea * 2.0) / longestSide : 0;
            return height > 1e-5 ? Math.Round(longestSide / height, 2) : 0;
        }

        /// Bounding-box aspect ratio for a polygon: width / height.
        /// Width and height come from the axis-aligned bounding box of the outline.
        /// Returns 0 if height is effectively zero.
        public static double BoundingBoxAspectRatio(double bboxWidth, double bboxHeight)
        {
            return bboxHeight > 1e-5 ? Math.Round(bboxWidth / bboxHeight, 2) : 0;
        }

        // =====================================================================
        // CONVEX HULL & SOLIDITY
        // =====================================================================

        /// Computes the convex hull of a list of canvas points using the Graham scan.
        /// Returns the hull vertices in counter-clockwise order.
        /// Returns an empty list if fewer than 3 points are provided.
        public static List<Point> ConvexHull(List<Point> pts)
        {
            if (pts == null || pts.Count < 3) return new List<Point>();

            Point pivot = pts[0];
            foreach (var p in pts)
                if (p.Y < pivot.Y || (p.Y == pivot.Y && p.X < pivot.X))
                    pivot = p;

            var sorted = new List<Point>(pts.Count);
            foreach (var p in pts)
                if (p != pivot)
                    sorted.Add(p);

            sorted.Sort((a, b) =>
            {
                double angleA = Math.Atan2(a.Y - pivot.Y, a.X - pivot.X);
                double angleB = Math.Atan2(b.Y - pivot.Y, b.X - pivot.X);
                if (Math.Abs(angleA - angleB) < 1e-10)
                {
                    double distA = (a.X - pivot.X) * (a.X - pivot.X) + (a.Y - pivot.Y) * (a.Y - pivot.Y);
                    double distB = (b.X - pivot.X) * (b.X - pivot.X) + (b.Y - pivot.Y) * (b.Y - pivot.Y);
                    return distA.CompareTo(distB);
                }
                return angleA.CompareTo(angleB);
            });

            int i = sorted.Count - 1;
            if (i >= 1)
            {
                double lastAngle = Math.Atan2(sorted[i].Y - pivot.Y, sorted[i].X - pivot.X);
                while (i > 0)
                {
                    double prevAngle = Math.Atan2(sorted[i - 1].Y - pivot.Y, sorted[i - 1].X - pivot.X);
                    if (Math.Abs(prevAngle - lastAngle) >= 1e-10) break;
                    i--;
                }
                sorted.Reverse(i, sorted.Count - i);
            }

            var hull = new List<Point> { pivot };
            foreach (var p in sorted)
            {
                while (hull.Count >= 2)
                {
                    Point a = hull[hull.Count - 2];
                    Point b = hull[hull.Count - 1];
                    double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                    if (cross < 0) hull.RemoveAt(hull.Count - 1);
                    else break;
                }
                hull.Add(p);
            }

            return hull;
        }

        /// Area of the convex hull of a set of points.
        /// Returns 0 for degenerate inputs.
        public static double ConvexHullArea(List<Point> pts)
        {
            var hull = ConvexHull(pts);
            return PolygonArea(hull);
        }

        /// Maximum caliper length of a closed outline, and the maximum width measured
        /// perpendicular to that long axis. Returns (0, 0) for degenerate input.
        public static (double length, double width) MaxLengthAndWidth(List<Point> pts)
        {
            if (pts == null || pts.Count < 2) return (0, 0);

            // The two farthest-apart points of a set are always hull vertices, so the
            // search only has to consider the hull — dozens of points, not hundreds.
            var hull = ConvexHull(pts);
            if (hull.Count < 2) hull = pts;

            double bestSq = -1;
            Point a = hull[0], b = hull[0];
            for (int i = 0; i < hull.Count; i++)
                for (int j = i + 1; j < hull.Count; j++)
                {
                    double dx = hull[j].X - hull[i].X, dy = hull[j].Y - hull[i].Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 > bestSq) { bestSq = d2; a = hull[i]; b = hull[j]; }
                }

            double length = Math.Sqrt(Math.Max(0, bestSq));
            if (length < 1e-9) return (0, 0);

            // Width is the span of the outline along the unit normal to the long axis.
            // Projecting the hull suffices: every original point lies inside it.
            double nx = -(b.Y - a.Y) / length, ny = (b.X - a.X) / length;
            double min = double.MaxValue, max = double.MinValue;
            foreach (var p in hull)
            {
                double proj = p.X * nx + p.Y * ny;
                if (proj < min) min = proj;
                if (proj > max) max = proj;
            }

            return (length, max - min);
        }

        /// Solidity: ratio of polygon area to its convex hull area.
        /// A value of 1 means fully convex; lower values indicate concavities.
        /// Returns 0 if the convex hull area is effectively zero.
        public static double Solidity(double polygonArea, double convexHullArea)
        {
            return convexHullArea > 1e-5
                ? Math.Round(polygonArea / convexHullArea, 4)
                : 0;
        }


        // =====================================================================
        // TURNING ANGLE STATISTICS
        // =====================================================================

        /// Computes the signed turning angle at each vertex of a closed polygon.
        /// Returns one angle per vertex (same count as input points).
        /// Positive = left turn (CCW), negative = right turn (CW).
        public static List<double> TurningAngles(List<Point> pts)
        {
            int n = pts.Count;
            var angles = new List<double>(n);

            for (int i = 0; i < n; i++)
            {
                Point prev = pts[(i - 1 + n) % n];
                Point curr = pts[i];
                Point next = pts[(i + 1) % n];

                double ax = curr.X - prev.X, ay = curr.Y - prev.Y;
                double bx = next.X - curr.X, by = next.Y - curr.Y;

                double lenA = Math.Sqrt(ax * ax + ay * ay);
                double lenB = Math.Sqrt(bx * bx + by * by);

                if (lenA < 1e-10 || lenB < 1e-10)
                {
                    angles.Add(0);
                    continue;
                }

                // Signed angle from incoming to outgoing segment
                double cross = ax * by - ay * bx;
                double dot = ax * bx + ay * by;
                angles.Add(Math.Atan2(cross, dot) * 180.0 / Math.PI);
            }

            return angles;
        }

        /// Sum of absolute turning angles across all vertices (degrees).
        /// For a simple closed convex polygon this equals 360°.
        public static double SumTurningAngles(List<Point> pts)
        {
            var angles = TurningAngles(pts);
            double sum = 0;
            foreach (double a in angles) sum += Math.Abs(a);
            return Math.Round(sum, 2);
        }

        /// Computes the absolute turning angle at each interior vertex of an open polyline.
        /// Endpoints are excluded — no wrap-around. Returns Count - 2 values.
        public static List<double> TurningAnglesOpen(List<Vector2> pts)
        {
            var angles = new List<double>();
            for (int i = 1; i < pts.Count - 1; i++)
            {
                Vector2 seg1 = pts[i] - pts[i - 1];
                Vector2 seg2 = pts[i + 1] - pts[i];
                double len1 = seg1.Magnitude();
                double len2 = seg2.Magnitude();
                if (len1 < 1e-10 || len2 < 1e-10) { angles.Add(0); continue; }
                double cross = seg1.X * seg2.Y - seg1.Y * seg2.X;
                double dot = seg1.X * seg2.X + seg1.Y * seg2.Y;
                angles.Add(Math.Abs(Math.Atan2(cross, dot) * 180.0 / Math.PI));
            }
            return angles;
        }

        /// Sum of absolute turning angles at interior vertices of an open polyline (degrees).
        public static double SumTurningAnglesOpen(List<Vector2> pts)
        {
            double sum = 0;
            foreach (double a in TurningAnglesOpen(pts)) sum += a;
            return Math.Round(sum, 3);
        }

        public static double TurningAnglePerLength(double sumTurningAngles, double perimeter)
        {
            return perimeter > 1e-5 ? Math.Round(sumTurningAngles / perimeter, 4) : 0;
        }

        // =====================================================================
        // POLYLINE SIMPLIFICATION
        // =====================================================================

        /// Simplifies a polyline using the Ramer-Douglas-Peucker algorithm.
        /// Returns a reduced list of points where no removed point was further
        /// than epsilon from the simplified line.
        public static List<Point> DouglasPeucker(List<Point> points, double epsilon)
        {
            // Guard: the recursion dereferences points[0] and points[Count-1], so anything
            // shorter than a triangle would throw. Nothing to simplify below 3 points anyway.
            if (points == null || points.Count < 3)
                return points != null ? new List<Point>(points) : new List<Point>();

            var result = new List<Point>();
            DouglasPeuckerRecursive(points, 0, points.Count - 1, epsilon, result);
            result.Add(points[points.Count - 1]);
            return result;
        }

        private static void DouglasPeuckerRecursive(
            List<Point> points, int start, int end, double epsilon, List<Point> result)
        {
            if (end <= start + 1)
            {
                result.Add(points[start]);
                return;
            }

            double maxDist = 0;
            int maxIndex = start;
            Point a = points[start], b = points[end];

            for (int i = start + 1; i < end; i++)
            {
                double d = PerpendicularDistance(points[i], a, b);
                if (d > maxDist) { maxDist = d; maxIndex = i; }
            }

            if (maxDist > epsilon)
            {
                DouglasPeuckerRecursive(points, start, maxIndex, epsilon, result);
                DouglasPeuckerRecursive(points, maxIndex, end, epsilon, result);
            }
            else
            {
                result.Add(points[start]);
            }
        }

        /// Perpendicular distance from point p to the line segment defined by a and b.
        public static double PerpendicularDistance(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (dx == 0 && dy == 0)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            double t = Math.Max(0, Math.Min(1,
                ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy)));
            double projX = a.X + t * dx, projY = a.Y + t * dy;
            return Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
        }

        /// Resamples a closed contour to exactly n points equally spaced by arc length,
        /// starting at contour[0]. Input may or may not include a closing duplicate;
        /// the returned list has no closure duplicate.
        public static List<Point> ResampleClosed(List<Point> contour, int n)
        {
            if (contour == null || contour.Count < 3 || n < 3)
                return contour != null ? new List<Point>(contour) : new List<Point>();

            var pts = new List<Point>(contour);
            // Drop a closing duplicate if present.
            if (pts.Count > 1)
            {
                Point f = pts[0], l = pts[pts.Count - 1];
                if ((f.X - l.X) * (f.X - l.X) + (f.Y - l.Y) * (f.Y - l.Y) < 1e-12)
                    pts.RemoveAt(pts.Count - 1);
            }
            int m = pts.Count;
            if (m < 3) return pts;

            var cum = new double[m + 1];
            for (int i = 0; i < m; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % m];
                cum[i + 1] = cum[i] + Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            }
            double total = cum[m];
            var outPts = new List<Point>(n);
            if (total < 1e-12) { for (int k = 0; k < n; k++) outPts.Add(pts[0]); return outPts; }

            double step = total / n;
            int seg = 0;
            for (int k = 0; k < n; k++)
            {
                double target = k * step;
                while (seg < m - 1 && cum[seg + 1] < target) seg++;
                Point a = pts[seg], b = pts[(seg + 1) % m];
                double segLen = cum[seg + 1] - cum[seg];
                double t = segLen > 1e-12 ? (target - cum[seg]) / segLen : 0.0;
                outPts.Add(new Point(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
            }
            return outPts;
        }

        // =====================================================================
        // AREA
        // =====================================================================

        /// Signed area of a polygon via the shoelace formula.
        /// Positive result = counter-clockwise winding; negative = clockwise.
        /// Pass the result through Math.Abs() when only magnitude is needed.
        public static double SignedPolygonArea(List<Point> pts)
        {
            double area = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2.0;
        }

        /// Absolute (unsigned) area of a polygon via the shoelace formula.
        /// Convenience wrapper around SignedPolygonArea.
        public static double PolygonArea(List<Point> pts)
        {
            return Math.Abs(SignedPolygonArea(pts));
        }

        /// Area of an ellipse or circle: π · (width/2) · (height/2).
        /// Pass equal width and height for a circle.
        public static double EllipseArea(double width, double height)
        {
            return Math.PI * (width / 2.0) * (height / 2.0);
        }

        /// Area of a rectangle or square: width · height.
        public static double RectangleArea(double width, double height)
        {
            return width * height;
        }

        /// Unsigned area of a triangle from three 2-D points using the cross product.
        public static double TriangleArea(Vector2 a, Vector2 b, Vector2 c)
        {
            // Cross product of AB × AC, halved
            return Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2.0;
        }


        // =====================================================================
        // RELATIVE AREA
        // =====================================================================

        /// Ratio of currentArea to previousArea.
        /// Returns the string "N/A" (boxed as object) when no valid prior area exists,
        /// matching the mixed double / "N/A" pattern used throughout the modes.
        public static object RelativeArea(double currentArea, double previousArea)
        {
            return previousArea > 1e-5
                ? (object)Math.Round(currentArea / previousArea, 2)
                : "N/A";
        }


        // =====================================================================
        // LENGTH
        // =====================================================================

        /// Euclidean distance between two 2-D points.
        public static double Length(Vector2 a, Vector2 b)
        {
            return (b - a).Magnitude();
        }

        /// Total arc length along a polyline defined by an ordered list of points.
        /// Sums the Euclidean distance between each consecutive pair.
        public static double ArcLength(List<Vector2> points)
        {
            double length = 0;
            for (int i = 1; i < points.Count; i++)
                length += (points[i] - points[i - 1]).Magnitude();
            return length;
        }

        /// Perimeter of a closed polygon defined by an ordered list of canvas points.
        /// The segment from the last point back to the first is included.
        public static double Perimeter(List<Point> pts)
        {
            double p = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                p += Math.Sqrt(dx * dx + dy * dy);
            }
            return p;
        }

        /// Arc length of a circular arc given its radius and central angle in degrees.
        public static double CircularArcLength(double radius, double centralAngleDegrees)
        {
            return radius * centralAngleDegrees * Math.PI / 180.0;
        }

        // =====================================================================
        // PARABOLAS
        // =====================================================================

        /// Fits a quadratic y = ax² + bx + c through three (x,y) pairs.
        /// Returns (a, b, c). 
        public static (double a, double b, double c) SolveParabola(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            double denom = (x1 - x2) * (x1 - x3) * (x2 - x3);
            if (Math.Abs(denom) < 1e-8) return (0, 0, 0);

            double a = (x3 * (y2 - y1) + x2 * (y1 - y3) + x1 * (y3 - y2)) / denom;
            double b = (x3 * x3 * (y1 - y2) + x2 * x2 * (y3 - y1) + x1 * x1 * (y2 - y3)) / denom;
            double c = (x2 * x3 * (x2 - x3) * y1 + x3 * x1 * (x3 - x1) * y2 + x1 * x2 * (x1 - x2) * y3) / denom;
            return (a, b, c);
        }

        // =====================================================================
        // RELATIVE LENGTH
        // =====================================================================

        /// Ratio of currentLength to previousLength.
        /// Returns "N/A" (boxed as object) when no valid prior length exists,
        /// matching the mixed double / "N/A" pattern used in DrawMode.
        public static object RelativeLength(double currentLength, double previousLength)
        {
            return previousLength > 1e-5
                ? (object)Math.Round(currentLength / previousLength, 2)
                : "N/A";
        }

        /// Chord-to-arc ratio for a circular arc.
        /// Values closer to 1 indicate a nearly-straight arc.
        /// Returns 0 if arc length is effectively zero.
        public static double ChordArcRatio(double chordLength, double arcLength)
        {
            return arcLength > 1e-5 ? Math.Round(chordLength / arcLength, 2) : 0;
        }

        /// Arc-to-chord ratio for a spline (SChordArcRatio in CurvatureMode).
        /// Values closer to 1 indicate a nearly-straight spline.
        /// Returns 0 if chord length is effectively zero.
        public static double ArcChordRatio(double arcLength, double chordLength)
        {
            return chordLength > 1e-5 ? Math.Round(arcLength / chordLength, 2) : 0;
        }


        // =====================================================================
        // LINE DIRECTION
        // =====================================================================

        /// Returns the normalized direction vector from point a to point b.
        /// Returns the zero vector if the two points are coincident.
        public static Vector2 LineDirection(Vector2 a, Vector2 b)
        {
            Vector2 dir = b - a;
            double len = dir.Magnitude();
            if (len < 1e-6) return new Vector2(0, 0);
            dir.Normalize();
            return dir;
        }

        /// Returns the direction vector perpendicular to the line a→b
        /// (rotated 90° counter-clockwise).
        /// Computed via the homogeneous cross product of the two points,
        /// then normalized — matching the bisector construction in CurvatureMode.
        public static Vector2 PerpendicularDirection(Vector2 a, Vector2 b)
        {
            Vector3 p1 = new Vector3(a.X, a.Y, 1);
            Vector3 p2 = new Vector3(b.X, b.Y, 1);
            Vector2 perp = (p1 ^ p2).ToVector2();

            double len = perp.Magnitude();
            if (len < 1e-6) return new Vector2(0, 0);


            perp.Normalize();
            return perp;
        }

        /// Returns the direction of the line in degrees measured from the positive X axis,
        /// in the range [0, 360).
        public static double LineAngleDegrees(Vector2 a, Vector2 b)
        {
            double degrees = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI;
            if (degrees < 0) degrees += 360.0;
            return degrees;
        }


        // =====================================================================
        // ANGLE
        // =====================================================================

        /// Central angle of a circular arc in degrees, given three points and the
        /// arc center. The angle is the sweep from PointA to PointB about the center,
        /// corrected to the major arc when PointC (the arc apex) lies inside the
        /// triangle formed by A, B, and center — matching CurvatureMode behavior.
        public static double CentralAngle(
    Vector2 pointA, Vector2 pointB, Vector2 pointC, Vector2 center)
        {
            // C is the arc midpoint: it lies on the perpendicular bisector of AB and on the
            // circle, so it sits on the drawn arc and splits it into two halves that are each
            // <= 180 degrees. The central angle of the arc THROUGH C is therefore the sum of
            // the two sub-arc angles A->C and C->B. Because each half is <= 180, the unsigned
            // angle between the radius vectors measures each sub-arc exactly. Summing the two
            // halves is independent of winding/orientation — unlike a single signed A->B
            // sweep, which was the source of the 40 vs 320 flip.
            Vector2 da = pointA - center;
            Vector2 dc = pointC - center;
            Vector2 db = pointB - center;

            double half1 = Math.Abs(Vector2.AngleBetween(da, dc));
            double half2 = Math.Abs(Vector2.AngleBetween(dc, db));

            return Math.Round(half1 + half2, 1);
        }

        /// Interior angle at vertex B in the triangle A-B-C, in degrees.
        /// Returns the angle between rays BA and BC.
        public static double InteriorAngle(Vector2 a, Vector2 b, Vector2 c)
        {
            return Math.Round(Math.Abs(Vector2.AngleBetween(a - b, c - b)), 1);
        }

        /// Cumulative turning angle per unit length along a dense polyline.
        /// Used by CurvatureMode's spline analysis (TurningAngleResult).
        /// Returns 0 for degenerate inputs (fewer than 3 points or zero length).
        public static double TurningAnglePerUnitLength(List<Vector2> points)
        {
            if (points.Count < 3) return 0;

            double totalTurning = 0;
            // Sum ALL segment lengths once — unambiguous.
            double totalLength = 0;
            for (int i = 1; i < points.Count; i++)
                totalLength += (points[i] - points[i - 1]).Magnitude();

            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector2 seg1 = points[i] - points[i - 1];
                Vector2 seg2 = points[i + 1] - points[i];
                if (seg1.Magnitude() < 1e-5 || seg2.Magnitude() < 1e-5) continue;
                totalTurning += Math.Abs(Vector2.AngleBetween(seg1, seg2));
            }

            return totalLength > 1e-5 ? totalTurning / totalLength : 0;
        }

        /// Vertex curvature at the parabola's apex: κ = |2a|.
        /// 'a' is the leading coefficient of the normalized quadratic y = ax² + bx + c.
        public static double ParabolaVertexCurvature(double parabolaA)
        {
            return Math.Round(Math.Abs(2 * parabolaA), 2);
        }


        // =====================================================================
        // PERIMETER / AREA RATIO  &  CIRCULARITY
        // =====================================================================

        /// Perimeter-to-area ratio of a closed polygon.
        /// Returns 0 if the area is effectively zero.
        public static double PerimeterAreaRatio(double perimeter, double area)
        {
            return area > 1e-5 ? Math.Round(perimeter / area, 4) : 0;
        }

        /// Circularity (also called the isoperimetric quotient or shape factor):
        ///   C = 4π · A / P²
        /// A perfect circle gives C = 1; elongated or complex shapes approach 0.
        /// Returns 0 if the perimeter is effectively zero.
        public static double Circularity(double perimeter, double area)
        {
            return perimeter > 1e-5
                ? Math.Round((4.0 * Math.PI * area) / (perimeter * perimeter), 4)
                : 0;
        }


        // =====================================================================
        // BOUNDING BOX
        // =====================================================================

        /// Computes the axis-aligned bounding box of a list of canvas points.
        /// Returns [minX, minY, maxX, maxY].
        public static double[] BoundingBox(List<Point> pts)
        {
            if (pts == null || pts.Count == 0)
                return new double[] { 0, 0, 0, 0 };

            double minX = pts[0].X, maxX = pts[0].X;
            double minY = pts[0].Y, maxY = pts[0].Y;

            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            return new double[] { minX, minY, maxX, maxY };
        }


        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        // Barycentric point-in-triangle test used by CentralAngle.
        // Returns true when p lies inside (or on the boundary of) triangle a-b-c.
        internal static bool IsPointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            double d1 = TriangleSign(p, a, b);
            double d2 = TriangleSign(p, b, c);
            double d3 = TriangleSign(p, c, a);

            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;

            return !(hasNeg && hasPos);
        }

        // Signed area of the triangle formed by three points (2× the actual area).
        // Used only for the same-sign test in IsPointInTriangle.
        private static double TriangleSign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
        }
    }
}
