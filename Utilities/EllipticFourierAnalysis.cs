using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Computes and reconstructs Elliptic Fourier Descriptors (Kuhl & Giardina 1982).
    /// Normalized descriptors are invariant to size, rotation, and starting point.
    /// Raw (unnormalized) coefficients are retained separately for shape reconstruction.
    /// </summary>
    internal class EllipticFourierAnalysis
    {
        // Raw coefficients from the last computation, in canvas space.
        // Used for preview reconstruction — not normalized.
        public double[] RawCoefficients { get; private set; }

        /// <summary>
        /// Computes normalized EFDs from a list of canvas-space polyline points.
        /// Returns array of length harmonics*4: [a1,b1,c1,d1, a2,b2,c2,d2, ...]
        /// Also stores raw coefficients in RawCoefficients for preview use.
        /// </summary>
        public double[] ComputeNormalized(List<Point> pts, int harmonics)
        {
            int n = pts.Count;

            // Arc-length parametrization
            double[] dt = new double[n];
            double[] T = new double[n + 1];
            T[0] = 0;
            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                dt[i] = Math.Sqrt(dx * dx + dy * dy);
                T[i + 1] = T[i] + dt[i];
            }

            double totalLen = T[n];
            if (totalLen < 1e-10) return new double[harmonics * 4];

            double[] coeffs = new double[harmonics * 4];

            for (int h = 1; h <= harmonics; h++)
            {
                double an = 0, bn = 0, cn = 0, dn = 0;
                double twoPI_n_T = 2.0 * Math.PI * h / totalLen;
                double scale = totalLen / (2.0 * h * h * Math.PI * Math.PI);

                for (int i = 0; i < n; i++)
                {
                    Point a = pts[i], b = pts[(i + 1) % n];
                    double dxi = b.X - a.X;
                    double dyi = b.Y - a.Y;
                    if (dt[i] < 1e-10) continue;

                    double cos1 = Math.Cos(twoPI_n_T * T[i + 1]) - Math.Cos(twoPI_n_T * T[i]);
                    double sin1 = Math.Sin(twoPI_n_T * T[i + 1]) - Math.Sin(twoPI_n_T * T[i]);
                    double inv = 1.0 / dt[i];

                    an += dxi * inv * cos1;
                    bn += dxi * inv * sin1;
                    cn += dyi * inv * cos1;
                    dn += dyi * inv * sin1;
                }

                int k = (h - 1) * 4;
                coeffs[k] = scale * an;
                coeffs[k + 1] = scale * bn;
                coeffs[k + 2] = scale * cn;
                coeffs[k + 3] = scale * dn;
            }

            // Save raw coefficients before normalizing
            RawCoefficients = (double[])coeffs.Clone();

            // Normalize using the first harmonic
            double a1 = coeffs[0], b1 = coeffs[1], c1 = coeffs[2], d1 = coeffs[3];
            double theta1 = 0.5 * Math.Atan2(
                2 * (a1 * b1 + c1 * d1),
                a1 * a1 - b1 * b1 + c1 * c1 - d1 * d1);

            double cosT = Math.Cos(theta1), sinT = Math.Sin(theta1);
            double scaleA = a1 * cosT + b1 * sinT;
            double scaleC = c1 * cosT + d1 * sinT;
            double normScale = Math.Sqrt(scaleA * scaleA + scaleC * scaleC);

            if (normScale < 1e-10) return coeffs;

            double psi = Math.Atan2(scaleC, scaleA);
            double cosP = Math.Cos(psi), sinP = Math.Sin(psi);

            var normalized = new double[harmonics * 4];
            for (int h = 1; h <= harmonics; h++)
            {
                int k = (h - 1) * 4;
                double ah = coeffs[k], bh = coeffs[k + 1], ch = coeffs[k + 2], dh = coeffs[k + 3];

                double angle = h * theta1;
                double cosA = Math.Cos(angle), sinA = Math.Sin(angle);

                double ahr = ah * cosA + bh * sinA;
                double bhr = -ah * sinA + bh * cosA;
                double chr = ch * cosA + dh * sinA;
                double dhr = -ch * sinA + dh * cosA;

                normalized[k] = (ahr * cosP + chr * sinP) / normScale;
                normalized[k + 1] = (bhr * cosP + dhr * sinP) / normScale;
                normalized[k + 2] = (-ahr * sinP + chr * cosP) / normScale;
                normalized[k + 3] = (-bhr * sinP + dhr * cosP) / normScale;
            }

            return normalized;
        }

        /// <summary>
        /// Reconstructs a closed curve from raw coefficients using the given number
        /// of harmonics and the centroid DC offset. Returns canvas-space points.
        /// Returns null if no raw coefficients are available.
        /// </summary>
        public List<Point> Reconstruct(int harmonics, double dcX, double dcY, int sampleCount = -1)
        {
            if (RawCoefficients == null || RawCoefficients.Length == 0) return null;

            int maxHarmonics = RawCoefficients.Length / 4;
            harmonics = Math.Min(harmonics, maxHarmonics);
            if (harmonics < 1) return null;

            if (sampleCount < 0)
                sampleCount = Math.Max(100, harmonics * 20);

            var points = new List<Point>(sampleCount + 1);

            double cosBase = 0, sinBase = 0;
            for (int s = 0; s <= sampleCount; s++)
            {
                double baseAngle = 2.0 * Math.PI * (double)s / sampleCount;
                cosBase = Math.Cos(baseAngle);
                sinBase = Math.Sin(baseAngle);

                // Use angle addition to avoid per-harmonic trig calls
                double cosH = cosBase, sinH = sinBase;
                double x = 0, y = 0;

                for (int h = 1; h <= harmonics; h++)
                {
                    int k = (h - 1) * 4;
                    x += RawCoefficients[k] * cosH + RawCoefficients[k + 1] * sinH;
                    y += RawCoefficients[k + 2] * cosH + RawCoefficients[k + 3] * sinH;

                    double newCos = cosBase * cosH - sinBase * sinH;
                    double newSin = sinBase * cosH + cosBase * sinH;
                    cosH = newCos;
                    sinH = newSin;
                }

                points.Add(new Point(dcX + x, dcY + y));
            }

            return points;
        }

        /// <summary>
        /// Clears stored coefficients, e.g. on reset.
        /// </summary>
        public void Clear()
        {
            RawCoefficients = null;
        }
    }
}