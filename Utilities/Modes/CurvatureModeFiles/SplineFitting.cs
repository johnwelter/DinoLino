// Utilities/SplineFitting.cs
//
// Pure spline geometry shared by CurvatureMode: centripetal Catmull-Rom
// interpolation and Schneider's recursive cubic-Bézier fitting. Works on Vector2
// lists and System math only — no WPF, no Path, no UI state — matching the
// GeometryCalculations / EllipticFourierAnalysis split. CurvatureMode turns the
// points and segments returned here into WPF shapes.

using DinoLino.DataTypes;
using System;
using System.Collections.Generic;

namespace DinoLino.Utilities
{
    public static class SplineFitting
    {
        // =====================================================================
        // CATMULL-ROM
        // =====================================================================

        // Dense interpolated points along a centripetal Catmull-Rom spline through
        // controlPoints. Phantom endpoints duplicate the first and last control
        // points so every segment has a full 4-point neighborhood.
        public static List<Vector2> GetCatmullRomPoints(List<Vector2> controlPoints, int samplesPerSegment)
        {
            var result = new List<Vector2>();
            if (controlPoints == null || controlPoints.Count < 2) return result;

            var pts = new List<Vector2>(controlPoints.Count + 2);
            pts.Add(controlPoints[0]);
            pts.AddRange(controlPoints);
            pts.Add(controlPoints[controlPoints.Count - 1]);

            for (int i = 1; i < pts.Count - 2; i++)
            {
                for (int j = 0; j < samplesPerSegment; j++)
                {
                    double t = (double)j / samplesPerSegment;
                    result.Add(CatmullRom(pts[i - 1], pts[i], pts[i + 1], pts[i + 2], t));
                }
            }
            result.Add(controlPoints[controlPoints.Count - 1]);
            return result;
        }

        // Centripetal Catmull-Rom (alpha = 0.5): evaluates the curve between p1 and
        // p2 at parameter t in [0,1] via Barry-Goldman recursive interpolation.
        private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, double t)
        {
            double t0 = 0;
            double t1 = t0 + KnotInterval(p0, p1);
            double t2 = t1 + KnotInterval(p1, p2);
            double t3 = t2 + KnotInterval(p2, p3);

            double s = t1 + t * (t2 - t1);

            Vector2 A1 = t1 > t0 ? p0 * ((t1 - s) / (t1 - t0)) + p1 * ((s - t0) / (t1 - t0)) : p1;
            Vector2 A2 = p1 * ((t2 - s) / (t2 - t1)) + p2 * ((s - t1) / (t2 - t1));
            Vector2 A3 = t3 > t2 ? p2 * ((t3 - s) / (t3 - t2)) + p3 * ((s - t2) / (t3 - t2)) : p2;

            Vector2 B1 = t2 > t0 ? A1 * ((t2 - s) / (t2 - t0)) + A2 * ((s - t0) / (t2 - t0)) : A2;
            Vector2 B2 = t3 > t1 ? A2 * ((t3 - s) / (t3 - t1)) + A3 * ((s - t1) / (t3 - t1)) : A2;

            return B1 * ((t2 - s) / (t2 - t1)) + B2 * ((s - t1) / (t2 - t1));
        }

        // alpha = 0.5 knot interval = distance^0.5 = (dx² + dy²)^0.25
        private static double KnotInterval(Vector2 a, Vector2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Max(Math.Pow(dx * dx + dy * dy, 0.25), 1e-6);
        }

        // =====================================================================
        // SCHNEIDER CUBIC-BÉZIER FITTING
        // =====================================================================

        // One fitted cubic Bézier segment: endpoints P0/P3, control handles P1/P2.
        public struct CubicBezierSegmentData
        {
            public Vector2 P0;
            public Vector2 P1;
            public Vector2 P2;
            public Vector2 P3;

            public CubicBezierSegmentData(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
            {
                P0 = p0;
                P1 = p1;
                P2 = p2;
                P3 = p3;
            }
        }

        // Fits one or more cubic Bézier segments to points within tolerance
        // (Schneider, "An Algorithm for Automatically Fitting Digitized Curves", 1990).
        public static List<CubicBezierSegmentData> FitSchneiderBezier(List<Vector2> points, double tolerance)
        {
            var result = new List<CubicBezierSegmentData>();
            if (points == null || points.Count < 2)
                return result;

            FitSchneiderBezierRecursive(points, 0, points.Count - 1, tolerance, result);
            return result;
        }

        private static void FitSchneiderBezierRecursive(
            List<Vector2> points, int first, int last, double tolerance,
            List<CubicBezierSegmentData> output)
        {
            int count = last - first + 1;
            if (count < 2)
                return;

            if (count == 2)
            {
                Vector2 p0 = points[first];
                Vector2 p3 = points[last];
                Vector2 d = (p3 - p0) * (1.0 / 3.0);
                output.Add(new CubicBezierSegmentData(p0, p0 + d, p3 - d, p3));
                return;
            }

            Vector2 tHat1 = ComputeStartTangent(points, first, last);
            Vector2 tHat2 = ComputeEndTangent(points, first, last);

            var bez = GenerateBezier(points, first, last, tHat1, tHat2);
            int splitPoint = FindMaxErrorPoint(points, first, last, bez, out double maxError);

            if (maxError <= tolerance || splitPoint <= first + 1 || splitPoint >= last - 1)
            {
                output.Add(bez);
                return;
            }

            FitSchneiderBezierRecursive(points, first, splitPoint, tolerance, output);
            FitSchneiderBezierRecursive(points, splitPoint, last, tolerance, output);
        }

        private static Vector2 ComputeStartTangent(List<Vector2> points, int first, int last)
        {
            Vector2 t = points[first + 1] - points[first];
            if (t.Magnitude() < 1e-9 && last > first + 1)
                t = points[first + 2] - points[first];
            t.Normalize();
            return t;
        }

        private static Vector2 ComputeEndTangent(List<Vector2> points, int first, int last)
        {
            Vector2 t = points[last - 1] - points[last];
            if (t.Magnitude() < 1e-9 && last > first + 1)
                t = points[last - 2] - points[last];
            t.Normalize();
            return t;
        }

        private static CubicBezierSegmentData GenerateBezier(
            List<Vector2> points, int first, int last, Vector2 tHat1, Vector2 tHat2)
        {
            Vector2 p0 = points[first];
            Vector2 p3 = points[last];

            int nPts = last - first + 1;
            var u = ChordLengthParameterize(points, first, last);

            double c00 = 0, c01 = 0, c11 = 0;
            double x0 = 0, x1 = 0;

            for (int i = 0; i < nPts; i++)
            {
                double ui = u[i];
                double b0 = Bernstein0(ui);
                double b1 = Bernstein1(ui);
                double b2 = Bernstein2(ui);
                double b3 = Bernstein3(ui);

                Vector2 a1 = tHat1 * b1;
                Vector2 a2 = tHat2 * b2;

                Vector2 tmp = points[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));

                c00 += a1 | a1;
                c01 += a1 | a2;
                c11 += a2 | a2;

                x0 += a1 | tmp;
                x1 += a2 | tmp;
            }

            double det = c00 * c11 - c01 * c01;
            double alphaL, alphaR;

            if (Math.Abs(det) > 1e-12)
            {
                alphaL = (x0 * c11 - x1 * c01) / det;
                alphaR = (c00 * x1 - c01 * x0) / det;
            }
            else
            {
                double dist = (p3 - p0).Magnitude() / 3.0;
                alphaL = alphaR = dist;
            }

            double segLength = (p3 - p0).Magnitude();
            double epsilon = segLength * 1e-6;

            if (alphaL < epsilon || alphaR < epsilon)
            {
                double dist = segLength / 3.0;
                alphaL = alphaR = dist;
            }

            Vector2 p1 = p0 + tHat1 * alphaL;
            Vector2 p2 = p3 + tHat2 * alphaR;

            return new CubicBezierSegmentData(p0, p1, p2, p3);
        }

        private static int FindMaxErrorPoint(
            List<Vector2> points, int first, int last,
            CubicBezierSegmentData bez, out double maxError)
        {
            maxError = -1;
            int splitPoint = (first + last) / 2;

            int samples = last - first + 1;
            for (int i = 1; i < samples - 1; i++)
            {
                double u = (double)i / (samples - 1);
                Vector2 curvePt = EvaluateCubicBezier(bez, u);
                double err = (points[first + i] - curvePt).Magnitude();

                if (err > maxError)
                {
                    maxError = err;
                    splitPoint = first + i;
                }
            }

            return splitPoint;
        }

        // =====================================================================
        // SAMPLING
        // =====================================================================

        // Samples every fitted Bézier segment to a dense polyline (used for metrics).
        public static List<Vector2> GetSchneiderBezierPoints(
            List<Vector2> controlPoints, int samplesPerSegment, double tolerance = 2.0)
        {
            var segments = FitSchneiderBezier(controlPoints, tolerance);
            var result = new List<Vector2>();

            foreach (var seg in segments)
            {
                for (int i = 0; i < samplesPerSegment; i++)
                {
                    double t = (double)i / samplesPerSegment;
                    result.Add(EvaluateCubicBezier(seg, t));
                }
            }

            if (segments.Count > 0)
                result.Add(segments[segments.Count - 1].P3);

            return result;
        }

        private static Vector2 EvaluateCubicBezier(CubicBezierSegmentData bez, double t)
        {
            double mt = 1.0 - t;
            double b0 = mt * mt * mt;
            double b1 = 3 * mt * mt * t;
            double b2 = 3 * mt * t * t;
            double b3 = t * t * t;

            return bez.P0 * b0 + bez.P1 * b1 + bez.P2 * b2 + bez.P3 * b3;
        }

        private static double[] ChordLengthParameterize(List<Vector2> points, int first, int last)
        {
            int n = last - first + 1;
            var u = new double[n];
            u[0] = 0;

            double total = 0;
            for (int i = first + 1; i <= last; i++)
                total += (points[i] - points[i - 1]).Magnitude();

            if (total < 1e-12)
            {
                for (int i = 1; i < n; i++)
                    u[i] = (double)i / (n - 1);
                return u;
            }

            double accum = 0;
            for (int i = first + 1; i <= last; i++)
            {
                accum += (points[i] - points[i - 1]).Magnitude();
                u[i - first] = accum / total;
            }

            return u;
        }

        private static double Bernstein0(double t) => Math.Pow(1 - t, 3);
        private static double Bernstein1(double t) => 3 * t * Math.Pow(1 - t, 2);
        private static double Bernstein2(double t) => 3 * t * t * (1 - t);
        private static double Bernstein3(double t) => t * t * t;
    }
}