// Utilities/SplineFitting.cs
//
// Pure spline geometry used by CurvatureMode.
// Catmull-Rom generates dense interpolated points, and Schneider fitting reduces a
// polyline to one or more cubic Bézier segments.
// This file depends only on Vector2 and System math so it stays UI-agnostic.

using DinoLino.DataTypes;
using System;
using System.Collections.Generic;

namespace DinoLino.Utilities
{
    public static class SplineFitting
    {
        // =====================
        // Catmull-Rom sampling
        // =====================

        /// <summary>
        /// Samples a centripetal Catmull-Rom spline through the supplied control points.
        /// </summary>
        public static List<Vector2> GetCatmullRomPoints(List<Vector2> controlPoints, int samplesPerSegment)
        {
            var result = new List<Vector2>();
            if (controlPoints == null || controlPoints.Count < 2) return result;

            // Duplicate the endpoints so every segment has four points available.
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

            // Include the final control point so the sampled polyline reaches the end.
            result.Add(controlPoints[controlPoints.Count - 1]);
            return result;
        }

        /// <summary>
        /// Evaluates a centripetal Catmull-Rom segment between p1 and p2.
        /// </summary>
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

        /// <summary>
        /// Returns the centripetal knot spacing for two points.
        /// </summary>
        private static double KnotInterval(Vector2 a, Vector2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Max(Math.Pow(dx * dx + dy * dy, 0.25), 1e-6);
        }

        // =====================
        // Schneider fitting
        // =====================

        /// <summary>
        /// One fitted cubic Bézier segment.
        /// </summary>
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

        /// <summary>
        /// Fits one or more cubic Bézier segments to a polyline within the given tolerance.
        /// </summary>
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
                // With only two points, approximate the segment using handles one-third in from each end.
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

        /// <summary>
        /// Estimates the tangent at the start of the fitted span.
        /// </summary>
        private static Vector2 ComputeStartTangent(List<Vector2> points, int first, int last)
        {
            Vector2 t = points[first + 1] - points[first];
            if (t.Magnitude() < 1e-9 && last > first + 1)
                t = points[first + 2] - points[first];

            t.Normalize();
            return t;
        }

        /// <summary>
        /// Estimates the tangent at the end of the fitted span.
        /// </summary>
        private static Vector2 ComputeEndTangent(List<Vector2> points, int first, int last)
        {
            Vector2 t = points[last - 1] - points[last];
            if (t.Magnitude() < 1e-9 && last > first + 1)
                t = points[last - 2] - points[last];

            t.Normalize();
            return t;
        }

        /// <summary>
        /// Builds a cubic Bézier approximation for the current point span.
        /// </summary>
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

                // Residual between the data point and the current end-point blend.
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

        /// <summary>
        /// Finds the point with the largest fit error.
        /// </summary>
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

        // =====================
        // Sampling
        // =====================

        /// <summary>
        /// Samples fitted Bézier segments into a dense polyline.
        /// </summary>
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

        /// <summary>
        /// Evaluates a cubic Bézier curve at t in [0,1].
        /// </summary>
        private static Vector2 EvaluateCubicBezier(CubicBezierSegmentData bez, double t)
        {
            double mt = 1.0 - t;
            double b0 = mt * mt * mt;
            double b1 = 3 * mt * mt * t;
            double b2 = 3 * mt * t * t;
            double b3 = t * t * t;

            return bez.P0 * b0 + bez.P1 * b1 + bez.P2 * b2 + bez.P3 * b3;
        }

        /// <summary>
        /// Parameterizes points by cumulative chord length.
        /// </summary>
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