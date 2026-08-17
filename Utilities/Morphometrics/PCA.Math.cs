using System;
using System.Collections.Generic;
using System.Linq;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Principal component analysis for a complete numeric data matrix.
    ///
    /// Input layout:
    /// - Rows: observations/specimens
    /// - Columns: continuous variables
    ///
    /// The analysis centers every variable. When standardize is true, it also
    /// divides each column by its sample standard deviation before PCA.
    /// </summary>
    public static class PcaAnalysis
    {
        public sealed class Result
        {
            /// <summary>Names of input variables, in input-column order.</summary>
            public string[] VariableNames { get; internal set; }

            /// <summary>Column means calculated from the original input data.</summary>
            public double[] Means { get; internal set; }

            /// <summary>
            /// Sample standard deviations from the original input data.
            /// Values are 1 when standardization was disabled.
            /// </summary>
            public double[] StandardDeviations { get; internal set; }

            /// <summary>
            /// Variable coefficients for each PC.
            /// Dimensions: variable count × retained-component count.
            /// Access as Loadings[variableIndex, componentIndex].
            /// </summary>
            public double[,] Loadings { get; internal set; }

            /// <summary>
            /// Coordinates of each specimen in PC space.
            /// Dimensions: observation count × retained-component count.
            /// Access as Scores[observationIndex, componentIndex].
            /// </summary>
            public double[,] Scores { get; internal set; }

            /// <summary>Variance represented by each retained PC.</summary>
            public double[] Eigenvalues { get; internal set; }

            /// <summary>Percent of total variance explained by each retained PC.</summary>
            public double[] ExplainedVariancePercent { get; internal set; }

            /// <summary>Cumulative percent of total variance explained.</summary>
            public double[] CumulativeExplainedVariancePercent { get; internal set; }

            public int ObservationCount => Scores?.GetLength(0) ?? 0;
            public int VariableCount => VariableNames?.Length ?? 0;
            public int ComponentCount => Scores?.GetLength(1) ?? 0;

            public double PC1(int observationIndex) => Scores[observationIndex, 0];

            public double PC2(int observationIndex)
            {
                if (ComponentCount < 2)
                    throw new InvalidOperationException(
                        "PC2 is unavailable because this PCA has fewer than two components.");

                return Scores[observationIndex, 1];
            }
        }

        /// <summary>
        /// Performs PCA on continuous numeric variables.
        /// </summary>
        /// <param name="values">
        /// Observations by variables matrix. Each inner array is one specimen;
        /// each value position is one continuous variable.
        /// </param>
        /// <param name="variableNames">
        /// Optional names for columns. Supply null to generate Variable1, Variable2, etc.
        /// </param>
        /// <param name="standardize">
        /// True: center and scale each variable to sample SD = 1.
        /// False: mean-center only.
        /// </param>
        /// <param name="componentCount">
        /// Number of PCs to retain. Use 0 to retain all possible components.
        /// </param>
        public static Result Fit(
            IReadOnlyList<double[]> values,
            IReadOnlyList<string> variableNames = null,
            bool standardize = true,
            int componentCount = 0)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (values.Count < 2)
                throw new ArgumentException(
                    "PCA needs at least two observations.", nameof(values));

            if (values[0] == null || values[0].Length < 2)
                throw new ArgumentException(
                    "PCA needs at least two continuous variables.", nameof(values));

            int rowCount = values.Count;
            int columnCount = values[0].Length;

            for (int row = 0; row < rowCount; row++)
            {
                if (values[row] == null || values[row].Length != columnCount)
                {
                    throw new ArgumentException(
                        "Every observation must contain the same number of variables.",
                        nameof(values));
                }

                for (int col = 0; col < columnCount; col++)
                {
                    if (double.IsNaN(values[row][col]) || double.IsInfinity(values[row][col]))
                    {
                        throw new ArgumentException(
                            $"PCA does not accept missing or non-finite values. " +
                            $"Invalid value found at row {row + 1}, column {col + 1}.",
                            nameof(values));
                    }
                }
            }

            string[] names = BuildVariableNames(variableNames, columnCount);
            int maxComponents = Math.Min(rowCount - 1, columnCount);

            if (componentCount < 0 || componentCount > maxComponents)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(componentCount),
                    $"Component count must be between 0 and {maxComponents}.");
            }

            int retainedCount = componentCount == 0 ? maxComponents : componentCount;

            double[] means = ColumnMeans(values, rowCount, columnCount);
            double[] standardDeviations = ColumnStandardDeviations(
                values, means, rowCount, columnCount);

            for (int col = 0; col < columnCount; col++)
            {
                if (standardDeviations[col] <= 1e-12)
                {
                    throw new ArgumentException(
                        $"'{names[col]}' has zero variance and cannot be included in PCA. " +
                        "Remove this variable before running the analysis.");
                }
            }

            double[,] z = CenterAndScale(
                values,
                means,
                standardDeviations,
                rowCount,
                columnCount,
                standardize);

            double[,] covariance = CovarianceMatrix(z, rowCount, columnCount);
            EigenDecomposition(covariance, out double[] eigenvalues, out double[,] eigenvectors);

            int[] order = Enumerable.Range(0, columnCount)
                .OrderByDescending(i => eigenvalues[i])
                .ToArray();

            double totalVariance = eigenvalues
                .Where(v => v > 0)
                .Sum();

            var loadings = new double[columnCount, retainedCount];
            var scores = new double[rowCount, retainedCount];
            var retainedEigenvalues = new double[retainedCount];
            var explained = new double[retainedCount];
            var cumulativeExplained = new double[retainedCount];

            for (int pc = 0; pc < retainedCount; pc++)
            {
                int sourceColumn = order[pc];
                double eigenvalue = Math.Max(0, eigenvalues[sourceColumn]);

                retainedEigenvalues[pc] = eigenvalue;
                explained[pc] = totalVariance <= 0
                    ? 0
                    : eigenvalue / totalVariance * 100.0;

                cumulativeExplained[pc] = explained[pc] +
                    (pc == 0 ? 0 : cumulativeExplained[pc - 1]);

                for (int variable = 0; variable < columnCount; variable++)
                    loadings[variable, pc] = eigenvectors[variable, sourceColumn];

                // PCA signs are mathematically arbitrary. Make the result stable
                // by orienting each PC so its largest absolute loading is positive.
                OrientComponentPositive(loadings, pc, columnCount);

                for (int row = 0; row < rowCount; row++)
                {
                    double score = 0;

                    for (int variable = 0; variable < columnCount; variable++)
                        score += z[row, variable] * loadings[variable, pc];

                    scores[row, pc] = score;
                }
            }

            return new Result
            {
                VariableNames = names,
                Means = means,
                StandardDeviations = standardize
                    ? standardDeviations
                    : Enumerable.Repeat(1.0, columnCount).ToArray(),
                Loadings = loadings,
                Scores = scores,
                Eigenvalues = retainedEigenvalues,
                ExplainedVariancePercent = explained,
                CumulativeExplainedVariancePercent = cumulativeExplained
            };
        }

        private static string[] BuildVariableNames(
            IReadOnlyList<string> variableNames,
            int columnCount)
        {
            if (variableNames == null)
            {
                return Enumerable.Range(1, columnCount)
                    .Select(i => $"Variable{i}")
                    .ToArray();
            }

            if (variableNames.Count != columnCount)
            {
                throw new ArgumentException(
                    "The number of variable names must equal the number of input columns.",
                    nameof(variableNames));
            }

            return variableNames
                .Select((name, index) => string.IsNullOrWhiteSpace(name)
                    ? $"Variable{index + 1}"
                    : name.Trim())
                .ToArray();
        }

        private static double[] ColumnMeans(
            IReadOnlyList<double[]> values,
            int rowCount,
            int columnCount)
        {
            var means = new double[columnCount];

            for (int col = 0; col < columnCount; col++)
            {
                double sum = 0;

                for (int row = 0; row < rowCount; row++)
                    sum += values[row][col];

                means[col] = sum / rowCount;
            }

            return means;
        }

        private static double[] ColumnStandardDeviations(
            IReadOnlyList<double[]> values,
            double[] means,
            int rowCount,
            int columnCount)
        {
            var sds = new double[columnCount];

            for (int col = 0; col < columnCount; col++)
            {
                double sumSquares = 0;

                for (int row = 0; row < rowCount; row++)
                {
                    double difference = values[row][col] - means[col];
                    sumSquares += difference * difference;
                }

                sds[col] = Math.Sqrt(sumSquares / (rowCount - 1));
            }

            return sds;
        }

        private static double[,] CenterAndScale(
            IReadOnlyList<double[]> values,
            double[] means,
            double[] standardDeviations,
            int rowCount,
            int columnCount,
            bool standardize)
        {
            var result = new double[rowCount, columnCount];

            for (int row = 0; row < rowCount; row++)
            {
                for (int col = 0; col < columnCount; col++)
                {
                    double centered = values[row][col] - means[col];

                    result[row, col] = standardize
                        ? centered / standardDeviations[col]
                        : centered;
                }
            }

            return result;
        }

        private static double[,] CovarianceMatrix(
            double[,] values,
            int rowCount,
            int columnCount)
        {
            var covariance = new double[columnCount, columnCount];

            for (int left = 0; left < columnCount; left++)
            {
                for (int right = left; right < columnCount; right++)
                {
                    double sum = 0;

                    for (int row = 0; row < rowCount; row++)
                        sum += values[row, left] * values[row, right];

                    double value = sum / (rowCount - 1);

                    covariance[left, right] = value;
                    covariance[right, left] = value;
                }
            }

            return covariance;
        }

        /// <summary>
        /// Jacobi eigendecomposition for a real, symmetric covariance matrix.
        /// Eigenvectors are returned as columns in eigenvectors.
        /// </summary>
        private static void EigenDecomposition(
            double[,] matrix,
            out double[] eigenvalues,
            out double[,] eigenvectors)
        {
            int n = matrix.GetLength(0);

            var a = (double[,])matrix.Clone();
            eigenvectors = new double[n, n];

            for (int i = 0; i < n; i++)
                eigenvectors[i, i] = 1.0;

            const double tolerance = 1e-12;
            int maxIterations = Math.Max(100, 100 * n * n);

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                int p = 0;
                int q = 1;
                double largest = 0;

                for (int row = 0; row < n; row++)
                {
                    for (int col = row + 1; col < n; col++)
                    {
                        double magnitude = Math.Abs(a[row, col]);

                        if (magnitude > largest)
                        {
                            largest = magnitude;
                            p = row;
                            q = col;
                        }
                    }
                }

                if (largest < tolerance)
                    break;

                double app = a[p, p];
                double aqq = a[q, q];
                double apq = a[p, q];

                double tau = (aqq - app) / (2.0 * apq);
                double t = tau >= 0
                    ? 1.0 / (tau + Math.Sqrt(1.0 + tau * tau))
                    : -1.0 / (-tau + Math.Sqrt(1.0 + tau * tau));

                double cosine = 1.0 / Math.Sqrt(1.0 + t * t);
                double sine = t * cosine;

                a[p, p] = app - t * apq;
                a[q, q] = aqq + t * apq;
                a[p, q] = 0;
                a[q, p] = 0;

                for (int k = 0; k < n; k++)
                {
                    if (k == p || k == q)
                        continue;

                    double akp = a[k, p];
                    double akq = a[k, q];

                    a[k, p] = cosine * akp - sine * akq;
                    a[p, k] = a[k, p];

                    a[k, q] = sine * akp + cosine * akq;
                    a[q, k] = a[k, q];
                }

                for (int k = 0; k < n; k++)
                {
                    double vkp = eigenvectors[k, p];
                    double vkq = eigenvectors[k, q];

                    eigenvectors[k, p] = cosine * vkp - sine * vkq;
                    eigenvectors[k, q] = sine * vkp + cosine * vkq;
                }
            }

            eigenvalues = new double[n];

            for (int i = 0; i < n; i++)
                eigenvalues[i] = a[i, i];
        }

        private static void OrientComponentPositive(
            double[,] loadings,
            int component,
            int variableCount)
        {
            int largestIndex = 0;
            double largestMagnitude = 0;

            for (int variable = 0; variable < variableCount; variable++)
            {
                double magnitude = Math.Abs(loadings[variable, component]);

                if (magnitude > largestMagnitude)
                {
                    largestMagnitude = magnitude;
                    largestIndex = variable;
                }
            }

            if (loadings[largestIndex, component] >= 0)
                return;

            for (int variable = 0; variable < variableCount; variable++)
                loadings[variable, component] *= -1.0;
        }
    }
}