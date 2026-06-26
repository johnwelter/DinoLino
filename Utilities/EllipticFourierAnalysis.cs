using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Quality flag describing how reliable a normalization was. The first harmonic
    /// defines the ellipse used to remove rotation and scale; when that ellipse is
    /// near-circular or near-zero, its orientation is ambiguous and the normalized
    /// rotation/scale should be treated with caution (Momocs warns about the same case
    /// for near-circular or bilaterally symmetric outlines).
    /// </summary>
    public enum EfdNormalizationStatus
    {
        /// <summary>First-harmonic ellipse is well-formed; normalization is reliable.</summary>
        Ok,
        /// <summary>First-harmonic ellipse is near-circular: orientation is ambiguous.</summary>
        NearlyCircular,
        /// <summary>First-harmonic magnitude is ~0: scale and orientation cannot be defined.</summary>
        Degenerate
    }

    /// <summary>
    /// Raw elliptic Fourier coefficients for one outline plus the DC (centroid) term.
    /// "Raw" means harmonic amplitudes in the coordinate space the outline was supplied in
    /// (no size/rotation/start-point normalization). The DC term is kept separate so it can
    /// be reattached for reconstruction or dropped for a position-invariant comparison.
    /// </summary>
    public sealed class EfdCoefficients
    {
        /// <summary>Number of harmonics represented.</summary>
        public int Harmonics { get; }

        /// <summary>Flat coefficient array, four per harmonic: [a1,b1,c1,d1, a2,b2,c2,d2, ...].</summary>
        public double[] Coefficients { get; }

        /// <summary>DC term in X: the arc-length-weighted centroid of the contour (EFD A0).</summary>
        public double A0 { get; }

        /// <summary>DC term in Y: the arc-length-weighted centroid of the contour (EFD C0).</summary>
        public double C0 { get; }

        /// <summary>Total contour arc length (the period T used in the transform).</summary>
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
    /// Result of normalizing a set of <see cref="EfdCoefficients"/>: the normalized
    /// coefficients plus the diagnostics that produced them. Exposing the diagnostics lets
    /// callers detect and handle the ambiguous-orientation cases instead of silently
    /// trusting an unstable result.
    /// </summary>
    public sealed class EfdNormalizationResult
    {
        /// <summary>Normalized coefficients, four per harmonic. Size/rotation/start-point invariant when <see cref="Status"/> is Ok.</summary>
        public double[] Coefficients { get; }

        /// <summary>Reliability of the normalization (see <see cref="EfdNormalizationStatus"/>).</summary>
        public EfdNormalizationStatus Status { get; }

        /// <summary>Semi-major axis of the first-harmonic ellipse (the scale divisor).</summary>
        public double FirstHarmonicMajor { get; }

        /// <summary>Semi-minor axis of the first-harmonic ellipse.</summary>
        public double FirstHarmonicMinor { get; }

        /// <summary>Start-phase rotation (theta) removed to fix the starting point.</summary>
        public double StartPhase { get; }

        /// <summary>Orientation (psi) removed to align the first-harmonic major axis to +X.</summary>
        public double Orientation { get; }

        /// <summary>Minor/major axis ratio of the first-harmonic ellipse. 1.0 means a circle, i.e. ambiguous orientation.</summary>
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
    /// Stage 1 of the EFA pipeline: turns an ordered outline into raw elliptic Fourier
    /// coefficients (Kuhl &amp; Giardina 1982) plus the DC/centroid term. Pure and stateless,
    /// so it can be unit-tested against known contours in isolation.
    /// </summary>
    public static class EllipticFourierCalculator
    {
        /// <summary>
        /// Computes raw coefficients and the DC term for a closed outline.
        /// </summary>
        /// <param name="pts">Ordered outline vertices WITHOUT a closing duplicate. Treated as a closed loop.</param>
        /// <param name="harmonics">Number of harmonics to compute (must be &gt;= 1).</param>
        /// <param name="canonicalizeWinding">
        /// When true (default) the traversal direction is made canonical (signed area &gt;= 0) before
        /// computing, so two outlines of the same shape produce comparable coefficients even if one
        /// was traced/drawn clockwise and the other anticlockwise. This does NOT change the
        /// reconstructed shape or the DC term; it only fixes the handedness of the coefficients.
        /// Pass false to keep the supplied point order exactly.
        /// </param>
        public static EfdCoefficients Compute(IReadOnlyList<Point> pts, int harmonics, bool canonicalizeWinding = true)
        {
            int h0 = Math.Max(0, harmonics);
            if (pts == null || pts.Count < 3 || harmonics < 1)
                return new EfdCoefficients(h0, new double[h0 * 4], 0, 0, 0);

            // Canonicalize traversal direction (keeps the same starting vertex, reverses the cycle).
            IReadOnlyList<Point> contour = pts;
            if (canonicalizeWinding && SignedArea(pts) < 0)
                contour = ReverseKeepingStart(pts);

            int n = contour.Count;

            // Arc-length parametrization over the CLOSED contour (segment n-1 -> 0 included).
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

            // DC term (A0, C0): the average of x(t)/y(t) over one period. For a piecewise-linear
            // contour this equals the segment-midpoint average weighted by segment length — the
            // true EFD DC term, and robust to non-uniform vertex spacing (unlike a plain vertex
            // average, which is biased toward densely-sampled parts of the outline).
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

        // Signed polygon area (shoelace) over the closed loop. Sign encodes traversal direction.
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

        // Reverses traversal direction while keeping pts[0] as the starting vertex:
        // [p0, p1, ..., p(n-1)] -> [p0, p(n-1), ..., p1].
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
    /// Stage 2 of the EFA pipeline: normalizes raw coefficients for size, rotation and
    /// starting point using the first-harmonic ellipse, and reports how trustworthy that
    /// normalization is. Pure and stateless.
    /// </summary>
    public static class EllipticFourierNormalizer
    {
        // First-harmonic magnitude below which orientation/scale are undefined.
        private const double DegenerateEpsilon = 1e-10;
        // Minor/major ratio at or above which the first harmonic is treated as near-circular.
        private const double NearlyCircularAxisRatio = 0.95;

        public static EfdNormalizationResult Normalize(EfdCoefficients raw)
        {
            int harmonics = raw?.Harmonics ?? 0;
            double[] src = raw?.Coefficients ?? Array.Empty<double>();
            var normalized = new double[harmonics * 4];

            if (harmonics < 1 || src.Length < 4)
                return new EfdNormalizationResult(normalized, EfdNormalizationStatus.Degenerate, 0, 0, 0, 0);

            double a1 = src[0], b1 = src[1], c1 = src[2], d1 = src[3];

            // Start-phase theta: rotate the parametrization so t = 0 lands on an extremum of
            // the first-harmonic ellipse, removing dependence on where tracing began.
            double theta = 0.5 * Math.Atan2(
                2.0 * (a1 * b1 + c1 * d1),
                a1 * a1 - b1 * b1 + c1 * c1 - d1 * d1);
            double cosT = Math.Cos(theta), sinT = Math.Sin(theta);

            // First-harmonic ellipse axes in the start-aligned frame.
            double aStar = a1 * cosT + b1 * sinT;
            double cStar = c1 * cosT + d1 * sinT;
            double bStar = -a1 * sinT + b1 * cosT;
            double dStar = -c1 * sinT + d1 * cosT;

            double major = Math.Sqrt(aStar * aStar + cStar * cStar); // scale divisor
            double minor = Math.Sqrt(bStar * bStar + dStar * dStar);

            if (major < DegenerateEpsilon)
            {
                // Orientation and scale are undefined: hand back the raw coefficients unchanged.
                Array.Copy(src, normalized, normalized.Length);
                return new EfdNormalizationResult(normalized, EfdNormalizationStatus.Degenerate, major, minor, theta, 0);
            }

            // Orientation psi: rotate so the first-harmonic major axis lies along +X.
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

            var status = (minor / major) >= NearlyCircularAxisRatio
                ? EfdNormalizationStatus.NearlyCircular
                : EfdNormalizationStatus.Ok;

            return new EfdNormalizationResult(normalized, status, major, minor, theta, psi);
        }
    }

    /// <summary>
    /// Stage 3 of the EFA pipeline: samples a closed curve from coefficients. Pure and
    /// stateless. Works on any coefficient array — pass raw coefficients with the contour's
    /// own DC for a faithful overlay, or normalized coefficients with a zero offset to view
    /// the canonical (size/rotation-normalized) shape.
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

                // Angle addition advances cos/sin per harmonic without a trig call each time.
                double cosH = cosBase, sinH = sinBase;
                double x = 0, y = 0;
                for (int h = 1; h <= harmonics; h++)
                {
                    int k = (h - 1) * 4;
                    x += coeffs[k] * cosH + coeffs[k + 1] * sinH;
                    y += coeffs[k + 2] * cosH + coeffs[k + 3] * sinH;

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
    /// Orchestrates the three EFA stages (calculate -&gt; normalize -&gt; reconstruct) and caches
    /// the most recent result. This is the same public surface OutlineMode used before, with
    /// the coordinate spaces now explicit:
    ///   - raw image/canvas space: the coordinates passed to <see cref="ComputeNormalized"/>;
    ///   - centered space: the contour shifted so its DC term (A0/C0) is the origin;
    ///   - analysis space: the size/rotation/start-point normalized coefficients.
    /// The heavy lifting lives in the three stateless classes above so each can be tested on
    /// its own; this class just wires them together for the UI.
    /// </summary>
    internal sealed class EllipticFourierAnalysis
    {
        private EfdCoefficients _raw;
        private EfdNormalizationResult _norm;

        /// <summary>
        /// Raw harmonic coefficients [a1,b1,c1,d1, ...] from the last computation, in the SAME
        /// coordinate space as the points passed in (canvas space). For preview reconstruction
        /// only — not normalized.
        /// </summary>
        public double[] RawCoefficients => _raw?.Coefficients;

        /// <summary>Normalized coefficients from the last computation (size/rotation/start-point invariant).</summary>
        public double[] NormalizedCoefficients => _norm?.Coefficients;

        /// <summary>
        /// X of the contour's DC term (arc-length centroid, EFD A0) from the last computation.
        /// This is the canonical reconstruction offset — prefer it over a raw vertex average.
        /// </summary>
        public double CentroidX => _raw?.A0 ?? 0.0;

        /// <summary>Y of the contour's DC term (arc-length centroid, EFD C0) from the last computation.</summary>
        public double CentroidY => _raw?.C0 ?? 0.0;

        /// <summary>
        /// Reliability of the last normalization. NearlyCircular / Degenerate mean the
        /// first-harmonic orientation was ambiguous, so the normalized rotation and scale
        /// should not be trusted for cross-specimen comparison.
        /// </summary>
        public EfdNormalizationStatus NormalizationStatus => _norm?.Status ?? EfdNormalizationStatus.Degenerate;

        /// <summary>Minor/major axis ratio of the first-harmonic ellipse (1.0 means a circle, i.e. ambiguous orientation).</summary>
        public double FirstHarmonicAxisRatio => _norm?.AxisRatio ?? 1.0;

        /// <summary>Full raw result from the last computation (coefficients + DC + period), or null.</summary>
        public EfdCoefficients LastRaw => _raw;

        /// <summary>Full normalization result from the last computation (coefficients + diagnostics), or null.</summary>
        public EfdNormalizationResult LastNormalization => _norm;

        /// <summary>
        /// Computes raw coefficients (+ DC) then normalizes them. Returns the normalized
        /// coefficients and caches everything for the reconstruction / diagnostic accessors.
        /// Signature is unchanged from before; <paramref name="canonicalizeWinding"/> is a new
        /// optional argument that defaults to the safe behavior.
        /// </summary>
        public double[] ComputeNormalized(List<Point> pts, int harmonics, bool canonicalizeWinding = true)
        {
            _raw = EllipticFourierCalculator.Compute(pts, harmonics, canonicalizeWinding);
            _norm = EllipticFourierNormalizer.Normalize(_raw);
            return _norm.Coefficients;
        }

        /// <summary>
        /// Display reconstruction: the caller supplies the translation (e.g. to drop the curve
        /// onto the canvas at a chosen point). This is a DISPLAY transform, not the canonical
        /// shape — the offset comes from outside the coefficients. Kept for backward compatibility.
        /// </summary>
        public List<Point> Reconstruct(int harmonics, double dcX, double dcY, int sampleCount = -1)
            => _raw == null ? null
                : EllipticFourierReconstructor.Reconstruct(_raw.Coefficients, harmonics, dcX, dcY, sampleCount);

        /// <summary>
        /// Canonical reconstruction: offsets by the contour's own DC term (A0/C0), so the curve
        /// lands exactly where the outline is without any externally-supplied centroid. Prefer
        /// this for the preview overlay.
        /// </summary>
        public List<Point> ReconstructCanonical(int harmonics, int sampleCount = -1)
            => _raw == null ? null
                : EllipticFourierReconstructor.Reconstruct(_raw.Coefficients, harmonics, _raw.A0, _raw.C0, sampleCount);


        /// <summary>
        /// One-shot harmonic-power analysis on a set of outline points, computed at a high harmonic
        /// count independent of the display setting. Does NOT disturb the cached display coefficients,
        /// so the live overlay and stored values are untouched.
        /// </summary>
        public HarmonicPowerProfile AnalyzeHarmonicPower(
            IReadOnlyList<Point> pts, int maxHarmonics, double threshold, bool dropFirstHarmonic = true)
        {
            var raw = EllipticFourierCalculator.Compute(pts, maxHarmonics);
            return EllipticFourierPower.Analyze(raw.Coefficients, threshold, dropFirstHarmonic);
        }

        /// <summary>Clears stored coefficients, e.g. on reset.</summary>
        public void Clear()
        {
            _raw = null;
            _norm = null;
        }
    }
}
