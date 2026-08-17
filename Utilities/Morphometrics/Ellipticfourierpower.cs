using System;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Per-outline harmonic power summary.
    /// </summary>
    public sealed class HarmonicPowerProfile
    {
        /// <summary>Number of harmonics analyzed.</summary>
        public int HarmonicCount { get; }

        /// <summary>True when harmonic 1 was excluded from the power total.</summary>
        public bool FirstHarmonicDropped { get; }

        /// <summary>Target cumulative fraction in the range [0, 1].</summary>
        public double Threshold { get; }

        /// <summary>Power for each harmonic, where index 0 corresponds to harmonic 1.</summary>
        public double[] Power { get; }

        /// <summary>
        /// Cumulative fraction of the counted power after each harmonic.
        /// If harmonic 1 is dropped, the curve starts at 0 and rises from harmonic 2 onward.
        /// </summary>
        public double[] CumulativeFraction { get; }

        /// <summary>Total power included in the threshold calculation.</summary>
        public double TotalPower { get; }

        /// <summary>
        /// Smallest harmonic count whose cumulative fraction reaches the threshold.
        /// This count is used for reconstruction.
        /// </summary>
        public int SelectedHarmonics { get; }

        /// <summary>True when the cumulative fraction reaches the requested threshold.</summary>
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
    /// Computes harmonic power from a flat EFD coefficient array.
    /// </summary>
    public static class EllipticFourierPower
    {
        /// <summary>
        /// Analyzes harmonic power and finds the smallest harmonic count that reaches the threshold.
        /// </summary>
        /// <param name="coefficients">Flat EFD coefficients [a1,b1,c1,d1, a2,...]. The DC term is not included.</param>
        /// <param name="threshold">Target cumulative fraction in the range [0, 1].</param>
        /// <param name="dropFirstHarmonic">
        /// When true, harmonic 1 is excluded from the percentage calculation.
        /// This keeps the result focused on detail harmonics instead of the dominant outline ellipse.
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

                // Power is the squared magnitude of the four coefficients for one harmonic.
                power[n] = 0.5 * (a * a + b * b + c * c + d * d);
            }

            // Skip harmonic 1 when requested, but only if another harmonic exists to count.
            int start = (dropFirstHarmonic && h > 1) ? 1 : 0;

            double total = 0;
            for (int n = start; n < h; n++)
                total += power[n];

            var cum = new double[h];

            if (total <= 1e-300)
            {
                // No detail power remains; the outline is effectively just the base ellipse.
                for (int n = 0; n < h; n++)
                    cum[n] = n >= start ? 1.0 : 0.0;

                int sel = dropFirstHarmonic ? 1 : h;
                return new HarmonicPowerProfile(h, dropFirstHarmonic, threshold, power, cum, total, sel, true);
            }

            double running = 0;
            int selected = -1;

            for (int n = 0; n < h; n++)
            {
                if (n >= start)
                    running += power[n];

                cum[n] = running / total;

                if (selected < 0 && n >= start && cum[n] >= threshold)
                    selected = n + 1; // Convert from zero-based index to a 1-based harmonic count.
            }

            bool reached = selected > 0;
            if (!reached) selected = h;

            return new HarmonicPowerProfile(h, dropFirstHarmonic, threshold, power, cum, total, selected, reached);
        }
    }
}