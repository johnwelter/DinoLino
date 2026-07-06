using System;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Per-outline harmonic-power analysis: how much of an outline's Fourier "signal" each
    /// harmonic carries, and the smallest harmonic count whose cumulative power reaches a
    /// </summary>
    public sealed class HarmonicPowerProfile
    {
        /// <summary>Number of harmonics analyzed (the H in harmonics 1..H).</summary>
        public int HarmonicCount { get; }

        /// <summary>Whether the fundamental (harmonic 1) was excluded from the power accounting.</summary>
        public bool FirstHarmonicDropped { get; }

        /// <summary>Target cumulative fraction in [0,1] (e.g. 0.99 for 99%).</summary>
        public double Threshold { get; }

        /// <summary>Per-harmonic power, index 0 = harmonic 1. Power = (a^2+b^2+c^2+d^2)/2.</summary>
        public double[] Power { get; }

        /// <summary>
        /// Cumulative fraction of the accounted power after each harmonic, index 0 = harmonic 1,
        /// in [0,1]. When the fundamental is dropped, index 0 is 0 and the curve starts rising at
        /// harmonic 2. The last entry is always 1.0 (everything analyzed is 100% of itself).
        /// </summary>
        public double[] CumulativeFraction { get; }

        /// <summary>Sum of the accounted power (excludes harmonic 1 when dropped).</summary>
        public double TotalPower { get; }

        /// <summary>
        /// Smallest harmonic count (1-based) whose cumulative power reaches the threshold. This is
        /// a count to USE for reconstruction (harmonics 1..n) even when harmonic 1 was excluded
        /// from the percentage.
        /// </summary>
        public int SelectedHarmonics { get; }

        /// <summary>
        /// True once the threshold is met. Because the cumulative fraction is normalised to the
        /// analyzed total, this is satisfied for any threshold &lt;= 1 (at worst at the last harmonic).
        /// </summary>
        public bool ThresholdReached { get; }

        public HarmonicPowerProfile(int harmonicCount, bool firstHarmonicDropped, double threshold,
            double[] power, double[] cumulativeFraction, double totalPower,
            int selectedHarmonics, bool thresholdReached)
        {
            HarmonicCount = harmonicCount;
            FirstHarmonicDropped = firstHarmonicDropped;
            Threshold = threshold;
            Power = power ?? Array.Empty<double>();
            CumulativeFraction = cumulativeFraction ?? Array.Empty<double>();
            TotalPower = totalPower;
            SelectedHarmonics = selectedHarmonics;
            ThresholdReached = thresholdReached;
        }

        public static HarmonicPowerProfile Empty(double threshold, bool firstHarmonicDropped)
            => new HarmonicPowerProfile(0, firstHarmonicDropped, threshold,
                Array.Empty<double>(), Array.Empty<double>(), 0, 0, false);
    }

    /// <summary>
    /// Computes harmonic power from a flat EFD coefficient array. Pure and stateless.
    /// The proportions are scale-invariant, so raw and normalized coefficients give identical
    /// cumulative fractions (normalization only rescales every harmonic by one shared constant).
    /// </summary>
    public static class EllipticFourierPower
    {
        /// <summary>
        /// Analyzes harmonic power and finds the smallest harmonic count reaching the threshold.
        /// </summary>
        /// <param name="coefficients">Flat EFD coefficients [a1,b1,c1,d1, a2,...]; the DC term is NOT part of this array.</param>
        /// <param name="threshold">Target cumulative fraction in [0,1] (e.g. 0.99 for 99%).</param>
        /// <param name="dropFirstHarmonic">
        /// When true (default), the fundamental (harmonic 1) is excluded from the accounting. The
        /// fundamental is the overall ellipse and almost always dominates total power, so including
        /// it makes the threshold trivial to reach; excluding it measures how many harmonics of
        /// *detail* the outline needs. 
        /// The fundamental is still used when reconstructing — it is only left out of the percentage.
        /// </param>
        public static HarmonicPowerProfile Analyze(double[] coefficients, double threshold, bool dropFirstHarmonic = true)
        {
            threshold = Math.Min(1.0, Math.Max(0.0, threshold));
            int h = (coefficients?.Length ?? 0) / 4;
            if (h < 1)
                return HarmonicPowerProfile.Empty(threshold, dropFirstHarmonic);

            var power = new double[h];
            for (int n = 0; n < h; n++)
            {
                int k = n * 4;
                double a = coefficients[k], b = coefficients[k + 1], c = coefficients[k + 2], d = coefficients[k + 3];
                power[n] = 0.5 * (a * a + b * b + c * c + d * d);
            }

            // First index counted in the accounting (skip harmonic 1 when dropping, but only if
            // there is more than one harmonic to fall back on).
            int start = (dropFirstHarmonic && h > 1) ? 1 : 0;

            double total = 0;
            for (int n = start; n < h; n++) total += power[n];

            var cum = new double[h];

            if (total <= 1e-300)
            {
                // No detail power: the outline is (essentially) the fundamental ellipse alone.
                for (int n = 0; n < h; n++) cum[n] = n >= start ? 1.0 : 0.0;
                int sel = dropFirstHarmonic ? 1 : h;
                return new HarmonicPowerProfile(h, dropFirstHarmonic, threshold, power, cum, total, sel, true);
            }

            double running = 0;
            int selected = -1;
            for (int n = 0; n < h; n++)
            {
                if (n >= start) running += power[n];
                cum[n] = running / total;
                if (selected < 0 && n >= start && cum[n] >= threshold)
                    selected = n + 1; // 1-based harmonic count
            }

            bool reached = selected > 0;
            if (!reached) selected = h; // defensive; not expected for threshold <= 1

            return new HarmonicPowerProfile(h, dropFirstHarmonic, threshold, power, cum, total, selected, reached);
        }
    }
}