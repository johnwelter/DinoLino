using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Accumulates EFD coefficient tables across a working session so the user can
    /// add multiple specimens and export them all to a single CSV at the end.
    /// One instance lives on OutlineMode for the session; it is intentionally NOT
    /// cleared on image-open or workspace-clear, so the batch survives those.
    /// Each specimen becomes a vertically-stacked block:
    ///     &lt;specimen name&gt;
    ///     harmonic,a,b,c,d
    ///     1,...
    ///     ...
    /// with a blank line between consecutive blocks. Harmonic counts may differ
    /// between specimens; stacking handles that naturally.
    /// </summary>
    public class EfdCsvCollector
    {
        private sealed class Entry
        {
            public string Name;
            public double[] Coefficients; // flattened [a1,b1,c1,d1, a2,...]
        }

        private readonly List<Entry> _entries = new();

        /// Number of specimens currently pending export.
        public int Count => _entries.Count;

        /// Appends a COPY of the given coefficients under the given specimen name.
        /// Returns false if there is nothing valid to add.
        public bool AddSpecimen(string specimenName, double[] coefficients)
        {
            if (coefficients == null || coefficients.Length < 4) return false;

            // Copy so later recomputation of the live coefficients can't mutate
            // what we have already banked.
            _entries.Add(new Entry
            {
                Name = string.IsNullOrWhiteSpace(specimenName)
                    ? $"Specimen {_entries.Count + 1}"
                    : specimenName,
                Coefficients = (double[])coefficients.Clone()
            });
            return true;
        }

        /// Removes all pending specimens.
        public void Clear() => _entries.Clear();

        /// Builds the full CSV text for every pending specimen, blocks stacked
        /// vertically with a blank separator line between them.
        public string BuildCsv()
        {
            var sb = new StringBuilder();
            for (int e = 0; e < _entries.Count; e++)
            {
                if (e > 0) sb.AppendLine(); // blank separator row between blocks

                var entry = _entries[e];
                sb.AppendLine(CsvEscape(entry.Name));
                sb.AppendLine("harmonic,a,b,c,d");

                int harmonics = entry.Coefficients.Length / 4;
                for (int h = 0; h < harmonics; h++)
                {
                    int k = h * 4;
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

        public string BuildWideCsv()
        {
            var sb = new StringBuilder();
            if (_entries.Count == 0) return sb.ToString();

            int maxH = 0;
            foreach (var e in _entries) maxH = Math.Max(maxH, e.Coefficients.Length / 4);

            // Header
            sb.Append("specimen");
            foreach (char L in new[] { 'A', 'B', 'C', 'D' })
                for (int i = 1; i <= maxH; i++) sb.Append(',').Append(L).Append(i);
            sb.AppendLine();

            // One row per specimen; offset picks a=0,b=1,c=2,d=3 within each harmonic quad.
            foreach (var e in _entries)
            {
                int h = e.Coefficients.Length / 4;
                sb.Append(CsvEscape(e.Name));
                for (int comp = 0; comp < 4; comp++)
                    for (int i = 0; i < maxH; i++)
                    {
                        sb.Append(',');
                        if (i < h)
                            sb.Append(e.Coefficients[i * 4 + comp].ToString("R", CultureInfo.InvariantCulture));
                        // else: blank cell (ragged padded)
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