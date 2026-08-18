using DinoLino.DataTypes;
using System.Text;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Formats outline measurements for display and copy/export text.
    /// </summary>
    public static class OutlineMetadataFormatter
    {
        // =====================
        // Warnings
        // =====================

        /// <summary>
        /// Builds the warning text for outlines whose first harmonic is unstable or degenerate.
        /// </summary>
        public static string BuildNormalizationWarning(
            EfdNormalizationStatus status, double firstHarmonicAxisRatio)
        {
            switch (status)
            {
                case EfdNormalizationStatus.NearlyCircular:
                    // Near-circular outlines have an unstable principal axis, so normalized EFDs can vary.
                    return $"⚠ Near-circular first harmonic (axis ratio {firstHarmonicAxisRatio:F2}); " +
                           "rotation/start-point alignment is unstable — normalized coefficients may not be comparable across specimens.";
                case EfdNormalizationStatus.Degenerate:
                    // A zero-length first harmonic means the outline has no usable orientation or scale basis.
                    return "⚠ First harmonic ~0; orientation and scale can't be defined for this outline.";
                default:
                    return "";
            }
        }

        // =====================
        // Summary text
        // =====================

        /// <summary>
        /// Builds the multi-line metadata summary shown to users or copied to the clipboard.
        /// </summary>
        public static string BuildSummary(
            double aspectRatio,
            double maxLength,
            double maxWidth,
            string unit,
            double perimeterAreaRatio,
            double circularity,
            double solidity,
            double turningAnglePerLength,
            int harmonics,
            double[] efdCoefficients,
            EfdNormalizationStatus normalizationStatus,
            double firstHarmonicAxisRatio)
        {
            var sb = new StringBuilder();

            if (normalizationStatus != EfdNormalizationStatus.Ok)
            {
                // Surface the warning inline so the summary clearly explains why normalized values may be unstable.
                sb.AppendLine($"  ⚠ Orientation ambiguous (1st-harmonic axis ratio {firstHarmonicAxisRatio:F2}); normalized rotation may be unstable.");
            }

            sb.AppendLine($"Aspect Ratio:       {aspectRatio:F3}");
            sb.AppendLine($"Max Length:         {maxLength:F2} {unit}");
            sb.AppendLine($"Max Width:          {maxWidth:F2} {unit}");
            sb.AppendLine($"Perim / Area:       {perimeterAreaRatio:F4}");
            sb.AppendLine($"Circularity:        {circularity:F4}");
            sb.AppendLine($"Solidity:           {solidity:F4}");
            sb.AppendLine($"Turn/Length: {turningAnglePerLength:F4}");
            sb.AppendLine($"EFD harmonics ({harmonics}):");

            // Each harmonic contributes four coefficients: a, b, c, d.
            for (int h = 0; h < harmonics; h++)
            {
                int k = h * 4;
                sb.AppendLine($"  n={h + 1}: a={efdCoefficients[k]:F4} b={efdCoefficients[k + 1]:F4} c={efdCoefficients[k + 2]:F4} d={efdCoefficients[k + 3]:F4}");
            }

            return sb.ToString();
        }
    }
}