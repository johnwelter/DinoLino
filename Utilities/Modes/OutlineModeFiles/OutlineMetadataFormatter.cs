using DinoLino.DataTypes;
using System.Text;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Builds the user-facing metadata strings. Extracted from
    /// OutlineMode.GenerateMetadata so the mode computes NUMBERS and this
    /// class (a presentation concern) turns them into text. The panel itself
    /// binds the individual numeric properties; these strings serve the
    /// clipboard/summary consumers.
    /// </summary>
    public static class OutlineMetadataFormatter
    {
        public static string BuildNormalizationWarning(
            EfdNormalizationStatus status, double firstHarmonicAxisRatio)
        {
            switch (status)
            {
                case EfdNormalizationStatus.NearlyCircular:
                    return $"⚠ Near-circular first harmonic (axis ratio {firstHarmonicAxisRatio:F2}); " +
                           "rotation/start-point alignment is unstable — normalized coefficients may not be comparable across specimens.";
                case EfdNormalizationStatus.Degenerate:
                    return "⚠ First harmonic ~0; orientation and scale can't be defined for this outline.";
                default:
                    return "";
            }
        }

        public static string BuildSummary(
            double aspectRatio,
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
                sb.AppendLine($"  ⚠ Orientation ambiguous (1st-harmonic axis ratio {firstHarmonicAxisRatio:F2}); normalized rotation may be unstable.");

            sb.AppendLine($"Aspect Ratio:       {aspectRatio:F3}");
            sb.AppendLine($"Perim / Area:       {perimeterAreaRatio:F4}");
            sb.AppendLine($"Circularity:        {circularity:F4}");
            sb.AppendLine($"Solidity:           {solidity:F4}");
            sb.AppendLine($"Turn/Length: {turningAnglePerLength:F4}");
            sb.AppendLine($"EFD harmonics ({harmonics}):");
            for (int h = 0; h < harmonics; h++)
            {
                int k = h * 4;
                sb.AppendLine($"  n={h + 1}: a={efdCoefficients[k]:F4} b={efdCoefficients[k + 1]:F4} c={efdCoefficients[k + 2]:F4} d={efdCoefficients[k + 3]:F4}");
            }
            return sb.ToString();
        }
    }
}