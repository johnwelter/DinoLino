using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Reliability flag for EFD normalization.
    /// </summary>
    public enum EfdNormalizationStatus
    {
        /// <summary>First-harmonic ellipse is well-formed.</summary>
        Ok,
        /// <summary>First-harmonic ellipse is close to circular, so orientation is ambiguous.</summary>
        NearlyCircular,
        /// <summary>First harmonic is too small to define a stable scale or orientation.</summary>
        Degenerate
    }

    /// <summary>
    /// Raw elliptic Fourier coefficients for one outline, plus the DC term.
    /// </summary>
    public sealed class EfdCoefficients
    {
        /// <summary>Number of harmonics represented.</summary>
        public int Harmonics { get; }

        /// <summary>Flattened coefficients: [a1,b1,c1,d1, a2,b2,c2,d2, ...].</summary>
        public double[] Coefficients { get; }

        /// <summary>X component of the DC term, equal to the arc-length-weighted contour centroid.</summary>
        public double A0 { get; }

        /// <summary>Y component of the DC term, equal to the arc-length-weighted contour centroid.</summary>
        public double C0 { get; }

        /// <summary>Total contour length used as the Fourier period.</summary>
        public double Period { get; }

        public EfdCoefficients(int harmonics, double[] coefficients, double a0, double c0, double period)
        {
            Harmonics = harmonics;
            Coefficients = coefficients ?? Array.Empty<double>();
            A0 = a0;
            C0 = c0;
            Period = period;
        }
    }

    /// <summary>
    /// Normalized EFD coefficients plus the diagnostics needed to judge stability.
    /// </summary>
    public sealed class EfdNormalizationResult
    {
        /// <summary>Normalized coefficients, four per harmonic.</summary>
        public double[] Coefficients { get; }

        /// <summary>Reliability of the normalization.</summary>
        public EfdNormalizationStatus Status { get; }

        /// <summary>Major axis length of the first-harmonic ellipse.</summary>
        public double FirstHarmonicMajor { get; }

        /// <summary>Minor axis length of the first-harmonic ellipse.</summary>
        public double FirstHarmonicMinor { get; }

        /// <summary>Phase shift removed to align the start point.</summary>
        public double StartPhase { get; }

        /// <summary>Rotation removed to align the first-harmonic major axis with +X.</summary>
        public double Orientation { get; }

        /// <summary>Minor-to-major ratio of the first harmonic.</summary>
        public double AxisRatio => FirstHarmonicMajor > 1e-12 ? FirstHarmonicMinor / FirstHarmonicMajor : 1.0;

        public EfdNormalizationResult(
            double[] coefficients, EfdNormalizationStatus status,
            double firstHarmonicMajor, double firstHarmonicMinor,
            double startPhase, double orientation)
        {
            Coefficients = coefficients ?? Array.Empty<double>();
            Status = status;
            FirstHarmonicMajor = firstHarmonicMajor;
            FirstHarmonicMinor = firstHarmonicMinor;
            StartPhase = startPhase;
            Orientation = orientation;
        }
    }

    /// <summary>
    /// Computes raw EFD coefficients and the DC term for a closed outline.
    /// </summary>
    public static class EllipticFourierCalculator
    {
        public static EfdCoefficients Compute(IReadOnlyList<Point> pts, int harmonics, bool canonicalizeWinding = true)
        {
            int h0 = Math.Max(0, harmonics);
            if (pts == null || pts.Count < 3 || harmonics < 1)
                return new EfdCoefficients(h0, new double[h0 * 4], 0, 0, 0);

            IReadOnlyList<Point> contour = pts;
            if (canonicalizeWinding && SignedArea(pts) < 0)
                contour = ReverseKeepingStart(pts);

            int n = contour.Count;

            // Build cumulative arc length around the closed contour.
            var dt = new double[n];
            var t = new double[n + 1];
            t[0] = 0;
            for (int i = 0; i < n; i++)
            {
                Point a = contour[i], b = contour[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                dt[i] = Math.Sqrt(dx * dx + dy * dy);
                t[i + 1] = t[i] + dt[i];
            }

            double period = t[n];
            if (period < 1e-10)
                return new EfdCoefficients(harmonics, new double[harmonics * 4], contour[0].X, contour[0].Y, 0);

            var coeffs = new double[harmonics * 4];
            for (int h = 1; h <= harmonics; h++)
            {
                double an = 0, bn = 0, cn = 0, dn = 0;
                double w = 2.0 * Math.PI * h / period;
                double scale = period / (2.0 * h * h * Math.PI * Math.PI);

                for (int i = 0; i < n; i++)
                {
                    if (dt[i] < 1e-10) continue;

                    Point a = contour[i], b = contour[(i + 1) % n];
                    double dxi = b.X - a.X;
                    double dyi = b.Y - a.Y;
                    double cosDiff = Math.Cos(w * t[i + 1]) - Math.Cos(w * t[i]);
                    double sinDiff = Math.Sin(w * t[i + 1]) - Math.Sin(w * t[i]);
                    double inv = 1.0 / dt[i];

                    an += dxi * inv * cosDiff;
                    bn += dxi * inv * sinDiff;
                    cn += dyi * inv * cosDiff;
                    dn += dyi * inv * sinDiff;
                }

                int k = (h - 1) * 4;
                coeffs[k] = scale * an;
                coeffs[k + 1] = scale * bn;
                coeffs[k + 2] = scale * cn;
                coeffs[k + 3] = scale * dn;
            }

            // DC term: arc-length-weighted centroid, not a plain vertex average.
            double a0 = 0, c0 = 0;
            for (int i = 0; i < n; i++)
            {
                Point a = contour[i], b = contour[(i + 1) % n];
                a0 += (a.X + b.X) * 0.5 * dt[i];
                c0 += (a.Y + b.Y) * 0.5 * dt[i];
            }
            a0 /= period;
            c0 /= period;

            return new EfdCoefficients(harmonics, coeffs, a0, c0, period);
        }

        private static double SignedArea(IReadOnlyList<Point> pts)
        {
            double area = 0;
            int n = pts.Count;

            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }

            return area * 0.5;
        }

        private static IReadOnlyList<Point> ReverseKeepingStart(IReadOnlyList<Point> pts)
        {
            int n = pts.Count;
            var rev = new Point[n];
            rev[0] = pts[0];
            for (int i = 1; i < n; i++) rev[i] = pts[n - i];
            return rev;
        }
    }

    /// <summary>
    /// Normalizes raw EFD coefficients for size, rotation, and start-point dependence.
    /// </summary>
    public static class EllipticFourierNormalizer
    {
        private const double DegenerateEpsilon = 1e-10;
        private const double NearlyCircularAxisRatio = 0.95;

        public static EfdNormalizationResult Normalize(EfdCoefficients raw)
        {
            int harmonics = raw?.Harmonics ?? 0;
            double[] src = raw?.Coefficients ?? Array.Empty<double>();
            var normalized = new double[harmonics * 4];

            if (harmonics < 1 || src.Length < 4)
                return new EfdNormalizationResult(normalized, EfdNormalizationStatus.Degenerate, 0, 0, 0, 0);

            double a1 = src[0], b1 = src[1], c1 = src[2], d1 = src[3];

            // Rotate the parameterization so the first harmonic starts at an extremum.
            double theta = 0.5 * Math.Atan2(
                2.0 * (a1 * b1 + c1 * d1),
                a1 * a1 - b1 * b1 + c1 * c1 - d1 * d1);

            double cosT = Math.Cos(theta), sinT = Math.Sin(theta);

            double aStar = a1 * cosT + b1 * sinT;
            double cStar = c1 * cosT + d1 * sinT;
            double bStar = -a1 * sinT + b1 * cosT;
            double dStar = -c1 * sinT + d1 * cosT;

            double major = Math.Sqrt(aStar * aStar + cStar * cStar);
            double minor = Math.Sqrt(bStar * bStar + dStar * dStar);

            if (major < DegenerateEpsilon)
            {
                Array.Copy(src, normalized, normalized.Length);
                return new EfdNormalizationResult(normalized, EfdNormalizationStatus.Degenerate, major, minor, theta, 0);
            }

            // Rotate so the major axis lies on +X.
            double psi = Math.Atan2(cStar, aStar);
            double cosP = Math.Cos(psi), sinP = Math.Sin(psi);

            for (int h = 1; h <= harmonics; h++)
            {
                int k = (h - 1) * 4;
                double ah = src[k], bh = src[k + 1], ch = src[k + 2], dh = src[k + 3];

                double angle = h * theta;
                double cosA = Math.Cos(angle), sinA = Math.Sin(angle);

                double ahr = ah * cosA + bh * sinA;
                double bhr = -ah * sinA + bh * cosA;
                double chr = ch * cosA + dh * sinA;
                double dhr = -ch * sinA + dh * cosA;

                normalized[k] = (ahr * cosP + chr * sinP) / major;
                normalized[k + 1] = (bhr * cosP + dhr * sinP) / major;
                normalized[k + 2] = (-ahr * sinP + chr * cosP) / major;
                normalized[k + 3] = (-bhr * sinP + dhr * cosP) / major;
            }

            // Fix the mirror branch so the same contour normalizes to one canonical sign.
            if (normalized.Length >= 4 && normalized[3] < 0)
            {
                for (int h = 1; h <= harmonics; h++)
                {
                    int k = (h - 1) * 4;
                    normalized[k + 1] = -normalized[k + 1];
                    normalized[k + 2] = -normalized[k + 2];
                    normalized[k + 3] = -normalized[k + 3];
                }
            }

            var status = (minor / major) >= NearlyCircularAxisRatio
                ? EfdNormalizationStatus.NearlyCircular
                : EfdNormalizationStatus.Ok;

            return new EfdNormalizationResult(normalized, status, major, minor, theta, psi);
        }
    }

    /// <summary>
    /// Reconstructs a closed contour from EFD coefficients.
    /// </summary>
    public static class EllipticFourierReconstructor
    {
        public static List<Point> Reconstruct(double[] coeffs, int harmonics, double dcX, double dcY, int sampleCount = -1)
        {
            if (coeffs == null || coeffs.Length == 0) return null;

            int maxHarmonics = coeffs.Length / 4;
            harmonics = Math.Min(harmonics, maxHarmonics);
            if (harmonics < 1) return null;

            if (sampleCount < 0)
                sampleCount = Math.Max(100, harmonics * 20);

            var points = new List<Point>(sampleCount + 1);
            for (int s = 0; s <= sampleCount; s++)
            {
                double baseAngle = 2.0 * Math.PI * (double)s / sampleCount;
                double cosBase = Math.Cos(baseAngle);
                double sinBase = Math.Sin(baseAngle);

                double cosH = cosBase, sinH = sinBase;
                double x = 0, y = 0;

                for (int h = 1; h <= harmonics; h++)
                {
                    int k = (h - 1) * 4;
                    x += coeffs[k] * cosH + coeffs[k + 1] * sinH;
                    y += coeffs[k + 2] * cosH + coeffs[k + 3] * sinH;

                    // Advance to the next harmonic without calling trig again.
                    double newCos = cosBase * cosH - sinBase * sinH;
                    double newSin = sinBase * cosH + cosBase * sinH;
                    cosH = newCos;
                    sinH = newSin;
                }

                points.Add(new Point(dcX + x, dcY + y));
            }

            return points;
        }
    }

    /// <summary>
    /// Convenience wrapper that runs EFD calculation, normalization, and reconstruction.
    /// </summary>
    internal sealed class EllipticFourierAnalysis
    {
        private EfdCoefficients _raw;
        private EfdNormalizationResult _norm;

        /// <summary>Raw coefficients from the last computation.</summary>
        public double[] RawCoefficients => _raw?.Coefficients;

        /// <summary>Normalized coefficients from the last computation.</summary>
        public double[] NormalizedCoefficients => _norm?.Coefficients;

        /// <summary>DC X term from the last computation.</summary>
        public double CentroidX => _raw?.A0 ?? 0.0;

        /// <summary>DC Y term from the last computation.</summary>
        public double CentroidY => _raw?.C0 ?? 0.0;

        /// <summary>Reliability of the last normalization.</summary>
        public EfdNormalizationStatus NormalizationStatus => _norm?.Status ?? EfdNormalizationStatus.Degenerate;

        /// <summary>First-harmonic minor/major axis ratio.</summary>
        public double FirstHarmonicAxisRatio => _norm?.AxisRatio ?? 1.0;

        /// <summary>Full raw result from the last computation.</summary>
        public EfdCoefficients LastRaw => _raw;

        /// <summary>Full normalization result from the last computation.</summary>
        public EfdNormalizationResult LastNormalization => _norm;

        public double[] ComputeNormalized(List<Point> pts, int harmonics, bool canonicalizeWinding = true)
        {
            _raw = EllipticFourierCalculator.Compute(pts, harmonics, canonicalizeWinding);
            _norm = EllipticFourierNormalizer.Normalize(_raw);
            return _norm.Coefficients;
        }

        /// <summary>
        /// Reconstructs the last raw contour at the supplied offset.
        /// </summary>
        public List<Point> Reconstruct(int harmonics, double dcX, double dcY, int sampleCount = -1)
            => _raw == null ? null
                : EllipticFourierReconstructor.Reconstruct(_raw.Coefficients, harmonics, dcX, dcY, sampleCount);

        /// <summary>
        /// Reconstructs the last contour at its own DC offset.
        /// </summary>
        public List<Point> ReconstructCanonical(int harmonics, int sampleCount = -1)
            => _raw == null ? null
                : EllipticFourierReconstructor.Reconstruct(_raw.Coefficients, harmonics, _raw.A0, _raw.C0, sampleCount);

        /// <summary>
        /// Measures harmonic power without changing the cached display state.
        /// </summary>
        public HarmonicPowerProfile AnalyzeHarmonicPower(
            IReadOnlyList<Point> pts, int maxHarmonics, double threshold, bool dropFirstHarmonic = true)
        {
            var raw = EllipticFourierCalculator.Compute(pts, maxHarmonics);
            return EllipticFourierPower.Analyze(raw.Coefficients, threshold, dropFirstHarmonic);
        }

        /// <summary>Clears cached analysis results.</summary>
        public void Clear()
        {
            _raw = null;
            _norm = null;
        }
    }
}