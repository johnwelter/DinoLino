using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Collects EFD coefficient tables for export across a session.
    /// Each specimen is stored as its own block and can be exported together at the end.
    /// </summary>
    public class EfdCsvCollector
    {
        private sealed class Entry
        {
            public string Name;
            public double[] Coefficients; // Flattened as [a1, b1, c1, d1, a2, ...].
        }

        private readonly List<Entry> _entries = new();

        /// <summary>
        /// Number of specimens currently stored for export.
        /// </summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Adds a specimen copy to the export batch.
        /// Returns false when there is no usable coefficient data.
        /// </summary>
        public bool AddSpecimen(string specimenName, double[] coefficients)
        {
            if (coefficients == null || coefficients.Length < 4) return false;

            _entries.Add(new Entry
            {
                Name = string.IsNullOrWhiteSpace(specimenName)
                    ? $"Specimen {_entries.Count + 1}"
                    : specimenName,
                Coefficients = (double[])coefficients.Clone()
            });

            return true;
        }

        /// <summary>
        /// Removes all stored specimens.
        /// </summary>
        public void Clear() => _entries.Clear();

        // =====================
        // Vertical export
        // =====================

        /// <summary>
        /// Builds a stacked CSV with one block per specimen.
        /// </summary>
        public string BuildCsv()
        {
            var sb = new StringBuilder();

            for (int e = 0; e < _entries.Count; e++)
            {
                if (e > 0) sb.AppendLine(); // Blank line separates specimen blocks.

                var entry = _entries[e];
                sb.AppendLine(CsvEscape(entry.Name));
                sb.AppendLine("harmonic,a,b,c,d");

                // Each harmonic contributes four coefficients: a, b, c, d.
                int harmonics = entry.Coefficients.Length / 4;
                for (int h = 0; h < harmonics; h++)
                {
                    int k = h * 4;

                    // k points to harmonic h's first coefficient:
                    //   a = coeff[k + 0]
                    //   b = coeff[k + 1]
                    //   c = coeff[k + 2]
                    //   d = coeff[k + 3]
                    sb.AppendLine(string.Join(",",
                        (h + 1).ToString(CultureInfo.InvariantCulture),
                        entry.Coefficients[k].ToString("R", CultureInfo.InvariantCulture),
                        entry.Coefficients[k + 1].ToString("R", CultureInfo.InvariantCulture),
                        entry.Coefficients[k + 2].ToString("R", CultureInfo.InvariantCulture),
                        entry.Coefficients[k + 3].ToString("R", CultureInfo.InvariantCulture)));
                }
            }

            return sb.ToString();
        }

        // =====================
        // Wide export
        // =====================

        /// <summary>
        /// Builds a wide CSV with one row per specimen and one column per coefficient.
        /// </summary>
        public string BuildWideCsv()
        {
            var sb = new StringBuilder();
            if (_entries.Count == 0) return sb.ToString();

            int maxH = 0;
            foreach (var e in _entries)
                maxH = Math.Max(maxH, e.Coefficients.Length / 4);

            // Columns are grouped by coefficient letter first, then harmonic index:
            // A1..An, B1..Bn, C1..Cn, D1..Dn.
            sb.Append("specimen");
            foreach (char label in new[] { 'A', 'B', 'C', 'D' })
            {
                for (int i = 1; i <= maxH; i++)
                    sb.Append(',').Append(label).Append(i);
            }
            sb.AppendLine();

            foreach (var e in _entries)
            {
                int harmonics = e.Coefficients.Length / 4;
                sb.Append(CsvEscape(e.Name));

                // The coefficients are stored as repeating 4-value groups:
                // [a1,b1,c1,d1, a2,b2,c2,d2, ...].
                // The outer loop picks which value in the group to emit.
                for (int comp = 0; comp < 4; comp++)
                {
                    // The inner loop walks harmonics in order.
                    for (int i = 0; i < maxH; i++)
                    {
                        sb.Append(',');

                        // If this specimen has fewer harmonics than the table width,
                        // leave the cell blank so the CSV stays rectangular.
                        if (i < harmonics)
                        {
                            sb.Append(e.Coefficients[i * 4 + comp]
                                .ToString("R", CultureInfo.InvariantCulture));
                        }
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string CsvEscape(string field)
        {
            if (field == null) return "";

            bool needsQuoting = field.Contains(',') || field.Contains('"') ||
                                field.Contains('\n') || field.Contains('\r');

            return needsQuoting ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
        }
    }
}