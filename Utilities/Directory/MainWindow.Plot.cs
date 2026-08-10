using DinoLino.Utilities;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino
{
    /// <summary>
    /// Plot tab of the Directory panel: graphs measurements recorded this session.
    /// The variables offered are the columns the Batch Workshop tables carry,
    /// gathered across every specimen (archived records plus the live history).
    /// Categories, fills, and point aesthetics come from the Sample tab's group
    /// columns. Rendering is hand-rolled on a Canvas, keeping the project
    /// dependency-free.
    /// </summary>
    public partial class MainWindow
    {
        private void Plot_Export(object sender, RoutedEventArgs e)
        {
            if (UI_PlotCanvas == null ||
                UI_PlotCanvas.ActualWidth < 1 ||
                UI_PlotCanvas.ActualHeight < 1)
            {
                MessageBox.Show(
                    this,
                    "There is no plot available to export.",
                    "Export Plot",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export Plot",
                FileName = "plot",
                AddExtension = true,
                OverwritePrompt = true,
                Filter =
                    "PNG image (*.png)|*.png|" +
                    "PDF document (*.pdf)|*.pdf|" +
                    "JPEG image (*.jpg)|*.jpg;*.jpeg|" +
                    "TIFF image (*.tif)|*.tif;*.tiff|" +
                    "SVG image (*.svg)|*.svg",
                FilterIndex = 1
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                PlotExporter.Export(UI_PlotCanvas, dialog.FileName);

                MessageBox.Show(
                    this,
                    $"Plot exported to:\n{dialog.FileName}",
                    "Export Plot",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Could not export the plot:\n{ex.Message}",
                    "Export failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        // =====================
        // Data collection
        // =====================

        // One numeric measurement, tagged with its origin so a scatter plot can
        // pair two variables from the same attempt and so any plot can look up
        // the specimen's group values.
        private sealed class PlotSample
        {
            public string Specimen;
            public int Attempt;
            public double Value;
        }

        private sealed class ScatterPoint
        {
            public string Specimen;
            public double X;
            public double Y;
        }

        // One fill split of the data: every item sharing a value of the Color by
        // column. With splitting off there is a single unnamed series, so the
        // boxplot renderer can use one code path.
        private sealed class PlotSeries<T>
        {
            public string Label;
            public Brush Fill;
            public List<T> Items = new List<T>();
        }

        // variable -> samples. _plotVariables preserves table and column order for
        // the drop-downs, which keeps each mode's variables grouped together.
        private readonly Dictionary<string, List<PlotSample>> _plotData =
            new Dictionary<string, List<PlotSample>>();
        private readonly List<string> _plotVariables = new List<string>();

        // EFA is deliberately absent: its columns are four coefficients per
        // harmonic, which would swamp the drop-down and are not values anyone
        // plots one at a time. Outlines2D is absent because its numeric columns
        // repeat OutlineMetadata's, and duplicate headers would collide here.
        private static readonly WorkshopCategory[] PlotCategories =
        {
            WorkshopCategory.Curvature,
            WorkshopCategory.Angle,
            WorkshopCategory.Shape,
            WorkshopCategory.OutlineMetadata
        };

        internal const string CategorySpecimen = "Specimen";
        internal const string ColourNone = "None";
        private const string UnassignedLabel = "(unassigned)";

        // Set while the choice boxes are repopulated, so their SelectionChanged
        // handlers do not redraw once per programmatic change.
        private bool _plotSuppressEvents;

        // Everything the Advanced dialog edits. Held on the window so the choices
        // survive closing the dialog and switching tabs.
        private readonly PlotOptions _plotOptions = new PlotOptions();

        // Variables staged for the PCA, likewise session-scoped. Kept apart from
        // the Batch Workshop's tables: nothing here changes what those export.
        private readonly PcaDataFrame _pcaDataFrame = new PcaDataFrame();

        /// Refreshes the tab from live history. Called when the tab is selected,
        /// from the Refresh link, and after a group is assigned, so new
        /// measurements and new group columns appear without a manual reload.
        internal void RefreshPlotTab()
        {
            RebuildPlotData();

            if (SelectedPlotType == "PCA")
            {
                // A staged variable whose specimens have gone must stop counting
                // toward the dataframe.
                _pcaDataFrame.PruneMissing(BuildPcaCatalog());

                // An analysis already on screen is re-fitted against the refreshed
                // data rather than left showing stale scores.
                if (_pcaResult != null || _pcaFailure != null) RunPca();
            }

            RepopulatePlotChoices();
            RedrawPlot();
        }

        // =====================
        // PCA dataframe
        // =====================

        /// Key of the single entry standing for the whole EFA coefficient block.
        /// It is not a measurement header, so it cannot collide with one.
        internal const string PcaEfaTableKey = "efa_coefficients_table";

        // Every table the PCA can draw columns from. Unlike PlotCategories this
        // includes EFA, whose coefficients are offered as one bundled entry.
        private static readonly WorkshopCategory[] PcaCategories =
        {
            WorkshopCategory.Curvature,
            WorkshopCategory.Angle,
            WorkshopCategory.Shape,
            WorkshopCategory.OutlineMetadata,
            WorkshopCategory.Efa
        };

        // Result of the last run, and the specimen behind each of its rows. Null
        // until the user runs an analysis, which is what keeps the PC choices out
        // of the X and Y boxes until there are components to choose.
        private PcaAnalysis.Result _pcaResult;
        private List<string> _pcaRowSpecimens;

        // Why the last run produced nothing, shown on the canvas in place of a plot.
        private string _pcaFailure;

        // Columns dropped for having the same value in every specimen, and
        // specimens dropped for missing a staged variable. Both are reported under
        // the plot so a surprising n is explainable.
        private List<string> _pcaConstantColumns = new List<string>();
        private int _pcaIncompleteSpecimens;

        /// Every variable measured this session, grouped by the mode that produced
        /// it. EFA is collapsed into one entry rather than listing a4, b4, c4 and
        /// the rest of its coefficients individually.
        private List<PcaVariableEntry> BuildPcaCatalog()
        {
            var entries = new List<PcaVariableEntry>();
            if (UndoRedoManager == null) return entries;

            foreach (var category in PlotCategories)
            {
                var table = WorkshopTables.Build(
                    category, UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);

                string group = WorkshopTables.TitleFor(category);

                for (int c = 0; c < table.MeasurementHeaders.Length; c++)
                {
                    string header = table.MeasurementHeaders[c];

                    // circ_ appears in two tables; the first one to claim it wins,
                    // as it does in the other plot types' variable list.
                    if (entries.Any(e => e.Key == header)) continue;
                    if (!ColumnHasValues(table, c)) continue;

                    entries.Add(new PcaVariableEntry
                    {
                        Key = header,
                        Label = header,
                        Group = group,
                        Columns = new[] { header }
                    });
                }
            }

            var efa = WorkshopTables.Build(
                WorkshopCategory.Efa, UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);

            // Outlines are analysed to whatever harmonic count each one supports,
            // so the deepest specimen has coefficients the shallowest never
            // measured. Only the harmonics every specimen reached describe the
            // same thing in every row, so the entry covers those and stops.
            int shared = SharedEfaHarmonics(efa);

            if (shared > 0)
            {
                var coefficients = new List<string>();
                for (int h = 1; h <= shared; h++)
                {
                    coefficients.Add($"efa_a{h}");
                    coefficients.Add($"efa_b{h}");
                    coefficients.Add($"efa_c{h}");
                    coefficients.Add($"efa_d{h}");
                }

                entries.Add(new PcaVariableEntry
                {
                    Key = PcaEfaTableKey,
                    Label = "EFA coefficients table",
                    Group = WorkshopTables.TitleFor(WorkshopCategory.Efa),
                    Columns = coefficients,
                    IsBundle = true,
                    Note = $"Pruned to harmonics 1\u2013{shared} ({coefficients.Count} coefficients), "
                         + "the most every measured specimen shares. Higher harmonics are left "
                         + "out so every specimen contributes the same columns."
                });
            }

            return entries;
        }

        /// Highest harmonic every EFA-bearing row reached. Rows with no
        /// coefficients at all are ignored: a specimen that was never outlined
        /// would otherwise prune the block to nothing for everyone.
        private static int SharedEfaHarmonics(WorkshopTable efa)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int c = 0; c < efa.MeasurementHeaders.Length; c++)
                index[efa.MeasurementHeaders[c]] = c;

            var measured = new List<WorkshopRow>();

            foreach (var block in efa.Blocks)
            {
                foreach (var row in block.Rows)
                {
                    if (row.Attempt <= 0) continue;              // placeholder row
                    if (HarmonicPresent(row, index, 1)) measured.Add(row);
                }
            }

            if (measured.Count == 0) return 0;

            int shared = 0;
            for (int h = 1; index.ContainsKey($"efa_a{h}"); h++)
            {
                if (!measured.All(r => HarmonicPresent(r, index, h))) break;
                shared = h;
            }

            return shared;
        }

        // A harmonic counts as present only with all four of its coefficients, so
        // a partly-filled harmonic never enters the matrix.
        private static bool HarmonicPresent(
            WorkshopRow row, Dictionary<string, int> index, int harmonic)
        {
            foreach (char component in new[] { 'a', 'b', 'c', 'd' })
            {
                if (!index.TryGetValue($"efa_{component}{harmonic}", out int c)) return false;
                if (!TryParsePlotValue(row.Cells[c], out _)) return false;
            }

            return true;
        }

        // True when at least one specimen recorded a numeric value in this column.
        private static bool ColumnHasValues(WorkshopTable table, int column)
        {
            foreach (var block in table.Blocks)
            {
                foreach (var row in block.Rows)
                {
                    if (row.Attempt <= 0) continue;   // placeholder row
                    if (TryParsePlotValue(row.Cells[column], out _)) return true;
                }
            }

            return false;
        }

        // =====================
        // PCA analysis
        // =====================

        // One complete numeric matrix, ready for PcaAnalysis.Fit.
        private sealed class PcaMatrix
        {
            public List<string> Specimens = new List<string>();
            public List<string> Columns = new List<string>();
            public List<double[]> Rows = new List<double[]>();
            public List<string> ConstantColumns = new List<string>();
            public int IncompleteSpecimens;
        }

        /// Fits a PCA to the staged dataframe. Stores either a result or the
        /// reason there is none; the caller refreshes the choices and redraws.
        private void RunPca()
        {
            _pcaResult = null;
            _pcaRowSpecimens = null;
            _pcaFailure = null;
            _pcaConstantColumns = new List<string>();
            _pcaIncompleteSpecimens = 0;

            var matrix = BuildPcaMatrix(out string failure);
            if (matrix == null)
            {
                _pcaFailure = failure;
                return;
            }

            _pcaConstantColumns = matrix.ConstantColumns;
            _pcaIncompleteSpecimens = matrix.IncompleteSpecimens;

            try
            {
                // Standardized, because the staged variables sit on unrelated
                // scales: an area in px² would otherwise dominate every component
                // over an EFD coefficient near zero.
                _pcaResult = PcaAnalysis.Fit(matrix.Rows, matrix.Columns, standardize: true);
                _pcaRowSpecimens = matrix.Specimens;
            }
            catch (Exception ex)
            {
                _pcaFailure = "The PCA could not run:\n" + ex.Message;
            }
        }

        /// Drops the staged PCA variables and any result fitted from them. Used by
        /// Clear All, since both describe data that no longer exists.
        internal void ClearPcaAnalysis()
        {
            _pcaDataFrame.Clear();
            _pcaResult = null;
            _pcaRowSpecimens = null;
            _pcaFailure = null;
            _pcaConstantColumns = new List<string>();
            _pcaIncompleteSpecimens = 0;
        }

        /// One row per specimen, each variable averaged over that specimen's
        /// attempts. A specimen missing any staged variable is left out rather
        /// than filled in, since PCA cannot take a gap.
        private PcaMatrix BuildPcaMatrix(out string failure)
        {
            failure = null;

            var catalog = BuildPcaCatalog();

            // Staged keys in the order they were added, dropping any the catalog
            // no longer offers.
            var staged = _pcaDataFrame.Keys
                .Select(k => catalog.FirstOrDefault(e => e.Key == k))
                .Where(e => e != null)
                .ToList();

            if (staged.Count == 0)
            {
                failure = "Please specify dataframe in the Advanced plot editing window.";
                return null;
            }

            var matrix = new PcaMatrix();

            foreach (var entry in staged)
                foreach (var column in entry.Columns)
                    if (!matrix.Columns.Contains(column)) matrix.Columns.Add(column);

            if (matrix.Columns.Count < 2)
            {
                failure = "A PCA needs at least two variables.\n" +
                          "Add another from the Advanced plot editing window.";
                return null;
            }

            CollectPcaValues(matrix.Columns, out var specimens, out var values);

            foreach (string specimen in specimens)
            {
                var row = new double[matrix.Columns.Count];
                bool complete = true;

                for (int i = 0; i < matrix.Columns.Count; i++)
                {
                    if (values.TryGetValue(matrix.Columns[i], out var perSpecimen) &&
                        perSpecimen.TryGetValue(specimen, out double v))
                    {
                        row[i] = v;
                    }
                    else
                    {
                        complete = false;
                        break;
                    }
                }

                if (complete)
                {
                    matrix.Specimens.Add(specimen);
                    matrix.Rows.Add(row);
                }
                else
                {
                    matrix.IncompleteSpecimens++;
                }
            }

            if (matrix.Rows.Count < 2)
            {
                failure = matrix.IncompleteSpecimens > 0
                    ? "Fewer than two specimens have every staged variable.\n" +
                      "Measure the missing variables, or remove them from the dataframe."
                    : "A PCA needs at least two specimens.";
                return null;
            }

            DropConstantColumns(matrix);

            if (matrix.Columns.Count < 2)
            {
                failure = "Fewer than two staged variables vary between specimens.\n" +
                          "A variable with the same value everywhere carries nothing " +
                          "for the analysis to rotate.";
                return null;
            }

            return matrix;
        }

        // A column identical in every row has zero variance, which the standardized
        // fit divides by. Normalized EFD guarantees this for a1, b1 and c1, so the
        // check is not a corner case.
        private static void DropConstantColumns(PcaMatrix matrix)
        {
            var keep = new List<int>();

            for (int c = 0; c < matrix.Columns.Count; c++)
            {
                double first = matrix.Rows[0][c];
                bool varies = matrix.Rows.Any(r => Math.Abs(r[c] - first) > 1e-12);

                if (varies) keep.Add(c);
                else matrix.ConstantColumns.Add(matrix.Columns[c]);
            }

            if (keep.Count == matrix.Columns.Count) return;

            matrix.Columns = keep.Select(c => matrix.Columns[c]).ToList();

            for (int r = 0; r < matrix.Rows.Count; r++)
            {
                var trimmed = new double[keep.Count];
                for (int i = 0; i < keep.Count; i++) trimmed[i] = matrix.Rows[r][keep[i]];
                matrix.Rows[r] = trimmed;
            }
        }

        /// Mean of each requested column per specimen, plus the specimen order the
        /// tables list. A header claimed by an earlier table is not re-read from a
        /// later one, matching how the catalog resolves a shared name.
        private void CollectPcaValues(
            IReadOnlyList<string> headers,
            out List<string> specimens,
            out Dictionary<string, Dictionary<string, double>> values)
        {
            specimens = new List<string>();
            values = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

            if (UndoRedoManager == null) return;

            var wanted = new HashSet<string>(headers, StringComparer.Ordinal);
            var sums = new Dictionary<string, Dictionary<string, (double Sum, int Count)>>(
                StringComparer.Ordinal);

            foreach (var category in PcaCategories)
            {
                var table = WorkshopTables.Build(
                    category, UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);

                // Blocks are the same for every category, so the first table seen
                // fixes the specimen order.
                if (specimens.Count == 0)
                    specimens.AddRange(table.Blocks.Select(b => b.Name));

                for (int c = 0; c < table.MeasurementHeaders.Length; c++)
                {
                    string header = table.MeasurementHeaders[c];
                    if (!wanted.Contains(header) || sums.ContainsKey(header)) continue;

                    var perSpecimen = new Dictionary<string, (double, int)>(StringComparer.Ordinal);

                    foreach (var block in table.Blocks)
                    {
                        foreach (var row in block.Rows)
                        {
                            if (row.Attempt <= 0) continue;   // placeholder row
                            if (!TryParsePlotValue(row.Cells[c], out double v)) continue;

                            perSpecimen.TryGetValue(block.Name, out var running);
                            perSpecimen[block.Name] = (running.Item1 + v, running.Item2 + 1);
                        }
                    }

                    sums[header] = perSpecimen;
                }
            }

            foreach (var header in sums)
            {
                var means = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var entry in header.Value)
                    means[entry.Key] = entry.Value.Sum / entry.Value.Count;

                values[header.Key] = means;
            }
        }

        /// Component names the X and Y boxes offer. Empty until a PCA has been
        /// run, which is what keeps those boxes unusable beforehand.
        private List<string> PcaComponentChoices()
        {
            var choices = new List<string>();
            if (_pcaResult == null) return choices;

            for (int i = 0; i < _pcaResult.ComponentCount; i++)
                choices.Add($"PC{i + 1}");

            return choices;
        }

        // "PC3" -> 2. Negative when the text is not a component name.
        private static int PcaComponentIndex(string name)
        {
            if (name == null || !name.StartsWith("PC", StringComparison.Ordinal)) return -1;
            return int.TryParse(name.Substring(2), out int n) && n > 0 ? n - 1 : -1;
        }

        private void RebuildPlotData()
        {
            _plotData.Clear();
            _plotVariables.Clear();

            if (UndoRedoManager == null) return;

            foreach (var category in PlotCategories)
            {
                var table = WorkshopTables.Build(
                    category, UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);

                for (int c = 0; c < table.MeasurementHeaders.Length; c++)
                {
                    string header = table.MeasurementHeaders[c];
                    if (_plotData.ContainsKey(header)) continue;   // header already claimed

                    List<PlotSample> samples = null;

                    foreach (var block in table.Blocks)
                    {
                        foreach (var row in block.Rows)
                        {
                            if (row.Attempt <= 0) continue;   // placeholder row
                            if (!TryParsePlotValue(row.Cells[c], out double v)) continue;

                            samples ??= new List<PlotSample>();
                            samples.Add(new PlotSample
                            {
                                Specimen = block.Name,
                                Attempt = row.Attempt,
                                Value = v
                            });
                        }
                    }

                    // Only variables with at least one numeric value are offered.
                    if (samples != null)
                    {
                        _plotData[header] = samples;
                        _plotVariables.Add(header);
                    }
                }
            }
        }

        // Cells are display strings ("12.3", "4.56 cm", "270 px²", "N/A", "yes"),
        // so read the leading token and skip anything non-numeric.
        private static bool TryParsePlotValue(string cell, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(cell)) return false;

            string token = cell.Trim();
            int space = token.IndexOf(' ');
            if (space > 0) token = token.Substring(0, space);

            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // =====================
        // Categories and groups
        // =====================

        // Group values are resolved by specimen name, which walks the roster, so
        // each name is looked up once per redraw.
        private readonly Dictionary<string, string[]> _plotGroupCache =
            new Dictionary<string, string[]>();

        /// The category a specimen falls in under one column. "Specimen" makes each
        /// specimen its own category; a group column reads that specimen's group,
        /// with unassigned specimens collected under one label.
        private string CategoryValue(string column, string specimenName)
        {
            if (string.IsNullOrEmpty(column) || column == ColourNone) return null;
            if (column == CategorySpecimen) return specimenName;

            var columns = SpecimenGroups.Columns;
            int index = -1;
            for (int i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i], column, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            // The column vanished between selection and redraw; fall back rather
            // than dropping the data.
            if (index < 0) return specimenName;

            if (!_plotGroupCache.TryGetValue(specimenName, out var values))
            {
                values = SpecimenGroups.ValuesFor(specimenName);
                _plotGroupCache[specimenName] = values;
            }

            string value = index < values.Length ? values[index] : "";
            return string.IsNullOrEmpty(value) ? UnassignedLabel : value;
        }

        /// Splits items into fill series. Levels are sorted by name so a series
        /// keeps its colour between redraws; splitting off yields one series in
        /// the fixed default fill.
        private List<PlotSeries<T>> SplitByFill<T>(
            IEnumerable<T> items, string colourBy, Func<T, string> specimenOf)
        {
            var index = new Dictionary<string, PlotSeries<T>>();
            var series = new List<PlotSeries<T>>();

            foreach (var item in items)
            {
                string label = colourBy == null
                    ? ""
                    : CategoryValue(colourBy, specimenOf(item)) ?? UnassignedLabel;

                if (!index.TryGetValue(label, out var s))
                {
                    s = new PlotSeries<T> { Label = label };
                    index[label] = s;
                    series.Add(s);
                }
                s.Items.Add(item);
            }

            if (colourBy == null)
            {
                foreach (var s in series) s.Fill = PlotOptions.DefaultFillBrush();
                return series;
            }

            var palette = _plotOptions.PaletteBrushes();
            var ordered = series.OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].Fill = palette[i % palette.Length];
            return ordered;
        }

        private void RepopulatePlotChoices()
        {
            _plotSuppressEvents = true;
            try
            {
                string keepX = UI_PlotXBox.SelectedItem as string;
                string keepY = UI_PlotYBox.SelectedItem as string;

                var variables = _plotVariables.ToList();

                string type = SelectedPlotType;
                bool boxplot = type == "Boxplot";
                bool pca = type == "PCA";

                // PCA plots components against components, and has none to offer
                // until an analysis has been run. A boxplot's X lists categories;
                // everything else lists the measured variables.
                var components = pca ? PcaComponentChoices() : null;

                var xItems = pca ? components
                          : boxplot ? PlotCategoryChoices()
                          : variables.ToList();

                var yItems = pca ? components.ToList() : variables;

                UI_PlotXBox.ItemsSource = xItems;
                UI_PlotYBox.ItemsSource = yItems;

                // Selections survive a refresh as long as they still exist.
                if (keepX != null && xItems.Contains(keepX))
                    UI_PlotXBox.SelectedItem = keepX;
                else if (boxplot)
                    UI_PlotXBox.SelectedItem = CategorySpecimen;   // always available
                else if (pca && xItems.Count > 0)
                    UI_PlotXBox.SelectedItem = xItems[0];          // PC1

                if (keepY != null && yItems.Contains(keepY))
                    UI_PlotYBox.SelectedItem = keepY;
                else if (pca && yItems.Count > 1)
                    UI_PlotYBox.SelectedItem = yItems[1];          // PC2

                _plotOptions.PruneMissingColumns();
            }
            finally
            {
                _plotSuppressEvents = false;
            }
        }

        /// Categories a boxplot can group by: Specimen always, plus every group
        /// column created from the Sample tab.
        internal static List<string> PlotCategoryChoices()
        {
            var categories = new List<string> { CategorySpecimen };
            categories.AddRange(SpecimenGroups.Columns);
            return categories;
        }

        /// Fill-split choices: no split, by specimen, or by any group column.
        internal static List<string> PlotColourChoices()
        {
            var colours = new List<string> { ColourNone, CategorySpecimen };
            colours.AddRange(SpecimenGroups.Columns);
            return colours;
        }

        /// <summary>Point color choices: hidden, one flat color, or by group.</summary>
        internal static List<string> PlotPointColorChoices()
        {
            var choices = new List<string> { PlotOptions.AestheticNone, PlotOptions.AestheticBlack };
            choices.AddRange(SpecimenGroups.Columns);
            return choices;
        }

        /// <summary>Point shape choices: hidden, one flat shape, or by group.</summary>
        internal static List<string> PlotPointShapeChoices()
        {
            var choices = new List<string> { PlotOptions.AestheticNone, PlotOptions.AestheticCircle };
            choices.AddRange(SpecimenGroups.Columns);
            return choices;
        }

        /// True when a point aesthetic names a group column rather than one of the
        /// two reserved values.
        internal static bool IsGroupAesthetic(string value) =>
            value != null &&
            value != PlotOptions.AestheticNone &&
            value != PlotOptions.AestheticBlack &&
            value != PlotOptions.AestheticCircle;

        // =====================
        // Point aesthetics
        // =====================

        // level -> index for whichever column each point aesthetic is mapped to.
        // Built once per redraw so a level keeps its colour and shape across the
        // whole plot, and rebuilt each time so a new group appears.
        private readonly Dictionary<string, int> _pointColorLevels = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _pointShapeLevels = new Dictionary<string, int>();

        private bool PointColorIsColumn => IsGroupAesthetic(_plotOptions.PointColor);
        private bool PointShapeIsColumn => IsGroupAesthetic(_plotOptions.PointShape);

        // Points vanish entirely when either aesthetic is None. forceVisible is
        // set by plots whose data *is* the points, where hiding them would leave
        // an empty frame.
        private bool PointsVisible(bool forceVisible) =>
            forceVisible ||
            (_plotOptions.PointColor != PlotOptions.AestheticNone &&
             _plotOptions.PointShape != PlotOptions.AestheticNone);

        /// Numbers the levels of each mapped column, in name order, so the palette
        /// and the shape cycle hand out stable values.
        private void PreparePointAesthetics(IEnumerable<string> specimenNames)
        {
            _pointColorLevels.Clear();
            _pointShapeLevels.Clear();

            var names = specimenNames.Distinct().ToList();

            if (PointColorIsColumn) NumberLevels(_pointColorLevels, _plotOptions.PointColor, names);
            if (PointShapeIsColumn) NumberLevels(_pointShapeLevels, _plotOptions.PointShape, names);
        }

        private void NumberLevels(Dictionary<string, int> map, string column, List<string> names)
        {
            var levels = names
                .Select(n => CategoryValue(column, n) ?? UnassignedLabel)
                .Distinct()
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < levels.Count; i++) map[levels[i]] = i;
        }

        private int LevelIndex(Dictionary<string, int> map, string column, string specimenName)
        {
            string level = CategoryValue(column, specimenName) ?? UnassignedLabel;
            return map.TryGetValue(level, out int i) ? i : 0;
        }

        /// The brush a specimen's points take, or null when they are hidden.
        private Brush PointBrushFor(string specimenName, bool forceVisible)
        {
            if (_plotOptions.PointColor == PlotOptions.AestheticNone)
                return forceVisible ? PlotOptions.DefaultPointBrush() : null;

            if (!PointColorIsColumn) return PlotOptions.DefaultPointBrush();

            var palette = _plotOptions.PaletteBrushes();
            int index = LevelIndex(_pointColorLevels, _plotOptions.PointColor, specimenName);
            return palette[index % palette.Length];
        }

        /// The shape a specimen's points take, or None when they are hidden.
        private PlotPointShape PointShapeFor(string specimenName, bool forceVisible)
        {
            if (_plotOptions.PointShape == PlotOptions.AestheticNone)
                return forceVisible ? PlotPointShape.Circle : PlotPointShape.None;

            if (!PointShapeIsColumn) return PlotPointShape.Circle;

            int index = LevelIndex(_pointShapeLevels, _plotOptions.PointShape, specimenName);
            return PlotOptions.ShapeCycle[index % PlotOptions.ShapeCycle.Length];
        }

        /// Groups items by point-color level, so a statistic drawn per color (a
        /// trend line, a QQ reference line) matches what the eye groups. One
        /// series when the color is not mapped to a column.
        private List<(string Label, Brush Brush, List<T> Items)> SplitByPointColor<T>(
            IEnumerable<T> items, Func<T, string> specimenOf)
        {
            var result = new List<(string, Brush, List<T>)>();

            if (!PointColorIsColumn)
            {
                result.Add(("", PlotOptions.DefaultPointBrush(), items.ToList()));
                return result;
            }

            var palette = _plotOptions.PaletteBrushes();

            foreach (var group in items
                .GroupBy(i => CategoryValue(_plotOptions.PointColor, specimenOf(i)) ?? UnassignedLabel)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                int index = _pointColorLevels.TryGetValue(group.Key, out int i) ? i : 0;
                result.Add((group.Key, palette[index % palette.Length], group.ToList()));
            }

            return result;
        }

        // =====================
        // UI events
        // =====================

        private string SelectedPlotType =>
            (UI_PlotTypeBox?.SelectedItem as ComboBoxItem)?.Content as string;

        private string SelectedTrend =>
            (UI_PlotTrendBox?.SelectedItem as ComboBoxItem)?.Content as string;

        // Null when the fill is not split or the plot type has no fill, so the
        // renderers can test one thing.
        private string ActiveColourBy
        {
            get
            {
                if (!PlotCapabilities.For(SelectedPlotType).SupportsFill) return null;
                return _plotOptions.ColourBy == ColourNone ? null : _plotOptions.ColourBy;
            }
        }

        private void Plot_TypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UI_PlotVarPanel == null) return;   // fires during InitializeComponent

            UpdatePlotControlVisibility();

            // A boxplot's X lists categories while a scatter plot's lists
            // variables, so the box is refilled whenever the type changes.
            RepopulatePlotChoices();
            RedrawPlot();
        }

        private void Plot_OptionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UI_PlotCanvas == null || _plotSuppressEvents) return;
            RedrawPlot();
        }

        // Fires when the tab first lays out and on every sidebar resize, so the
        // plot always fills the panel.
        private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawPlot();

        /// Re-reads the session's measurements and redraws. The tab refreshes
        /// itself when it is selected, so this covers the case where the Plot tab
        /// stayed on screen while new operations were performed.
        private void Plot_Refresh(object sender, RoutedEventArgs e) => RefreshPlotTab();

        private void Plot_OpenAdvanced(object sender, RoutedEventArgs e)
        {
            if (SelectedPlotType == "PCA")
            {
                // Built fresh on open, so a variable measured since the last visit
                // is listed and a vanished one is dropped from what is staged.
                var catalog = BuildPcaCatalog();
                _pcaDataFrame.PruneMissing(catalog);

                var pcaDialog = new PcaAdvancedWindow(_pcaDataFrame, _plotOptions, catalog)
                {
                    Owner = this,
                    FontSize = _currentFontSize,
                    FontFamily = _currentFont
                };

                // Run PCA closes with true; Cancel leaves the staged dataframe and
                // any existing result alone.
                if (pcaDialog.ShowDialog() == true)
                {
                    pcaDialog.CommitTo(_pcaDataFrame);
                    pcaDialog.CommitTo(_plotOptions);
                    RunPca();

                    // The components the run produced are what the X and Y boxes
                    // now offer, so they are refilled before the redraw.
                    RepopulatePlotChoices();
                    RedrawPlot();
                }

                return;
            }

            var capabilities = PlotCapabilities.For(SelectedPlotType);

            var dialog = new PlotAdvancedWindow(_plotOptions, capabilities)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            if (dialog.ShowDialog() == true)
            {
                dialog.CommitTo(_plotOptions);
                RedrawPlot();
            }
        }

        
        private void UpdatePlotControlVisibility()
        {
            string type = SelectedPlotType;
            bool scatter = type == "Scatter plot";
            bool boxplot = type == "Boxplot";
            bool pca = type == "PCA";
            bool twoVariable = scatter || boxplot || pca;

            UI_PlotVarPanel.Visibility = type == null ? Visibility.Collapsed : Visibility.Visible;

            ShowPlotRow(UI_PlotXLabel, UI_PlotXBox, twoVariable);
            ShowPlotRow(UI_PlotTrendLabel, UI_PlotTrendBox, scatter);

            if (twoVariable)
            {
                UI_PlotXLabel.Text = "X";
                UI_PlotYLabel.Text = "Y";

                // Second pair back in its own columns, beside the first.
                Grid.SetColumn(UI_PlotYLabel, 2);
                Grid.SetColumn(UI_PlotYBox, 3);
                Grid.SetColumnSpan(UI_PlotYBox, 1);
            }
            else
            {
                // One variable, so it moves into the first pair's columns and its
                // box spans the rest. Left where it was, the pair would sit in the
                // right half: the hidden X box's star column still claims its share
                // of the width even with nothing in it.
                UI_PlotYLabel.Text = type == "QQ plot" ? "Variable" : "X";

                Grid.SetColumn(UI_PlotYLabel, 0);
                Grid.SetColumn(UI_PlotYBox, 1);
                Grid.SetColumnSpan(UI_PlotYBox, 3);
            }
        }

        private static void ShowPlotRow(UIElement label, UIElement box, bool visible)
        {
            var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = visibility;
            box.Visibility = visibility;
        }

        // =====================
        // Redraw
        // =====================

        private void RedrawPlot()
        {
            if (UI_PlotCanvas == null) return;

            UI_PlotCanvas.Children.Clear();
            _plotGroupCache.Clear();
            _legendLayout = null;
            _plotBottomExtra = 0;
            _plotLeftExtra = 0;

            double w = UI_PlotCanvas.ActualWidth, h = UI_PlotCanvas.ActualHeight;
            if (w < 80 || h < 80) return;   // not laid out yet, or too small to draw

            string type = SelectedPlotType;
            if (type == null)
            {
                PlotMessage("Select a plot type above.");
                return;
            }

            if (type == "PCA")
            {
                if (_pcaDataFrame.Count == 0)
                {
                    PlotMessage("Please specify dataframe in the Advanced plot editing window.");
                    return;
                }

                if (_pcaResult == null)
                {
                    PlotMessage(_pcaFailure
                        ?? "Run the PCA from the Advanced plot editing window.");
                    return;
                }

                string xPc = UI_PlotXBox.SelectedItem as string;
                string yPc = UI_PlotYBox.SelectedItem as string;

                if (xPc == null || yPc == null)
                {
                    PlotMessage("Choose the components for X and Y.");
                    return;
                }

                DrawPcaScores(xPc, yPc);
                return;
            }

            if (_plotVariables.Count == 0)
            {
                PlotMessage("No measurements have been recorded this session yet.\n" +
                            "Perform operations on a specimen, then reopen this tab.");
                return;
            }

            if (type == "Scatter plot")
            {
                string xVar = UI_PlotXBox.SelectedItem as string;
                string yVar = UI_PlotYBox.SelectedItem as string;

                if (xVar == null || yVar == null)
                {
                    PlotMessage("Choose the X and Y variables.");
                    return;
                }

                var pairs = PairPlotSamples(xVar, yVar);
                if (pairs.Count == 0)
                {
                    PlotMessage("No attempt has values for both variables.");
                    return;
                }

                DrawScatter(pairs, xVar, yVar);
                return;
            }

            string variable = UI_PlotYBox.SelectedItem as string;
            if (variable == null)
            {
                PlotMessage("Choose a variable to plot.");
                return;
            }

            var samples = _plotData[variable];

            switch (type)
            {
                case "Boxplot":
                    string category = UI_PlotXBox.SelectedItem as string ?? CategorySpecimen;
                    DrawBoxplot(variable, category, ActiveColourBy);
                    break;
                case "Histogram":
                    DrawHistogram(variable);
                    break;
                case "QQ plot":
                    if (samples.Count < 3)
                    {
                        PlotMessage("A QQ plot needs at least 3 values.");
                        return;
                    }
                    DrawQQPlot(variable);
                    break;
                case "Dot plot":
                    DrawDotPlot(variable);
                    break;
            }
        }

        // The user's title when set, otherwise whatever the renderer generated.
        private string EffectiveXAxisTitle(string generated) =>
            string.IsNullOrWhiteSpace(_plotOptions.XAxisTitle) ? generated : _plotOptions.XAxisTitle;

        private string EffectiveYAxisTitle(string generated) =>
            string.IsNullOrWhiteSpace(_plotOptions.YAxisTitle) ? generated : _plotOptions.YAxisTitle;

        /// Reserves room for whichever axis titles will be drawn. Must run before
        /// PlotArea is read.
        private void PrepareAxisTitles(string xGenerated = null, string yGenerated = null)
        {
            _plotLeftExtra = string.IsNullOrWhiteSpace(EffectiveYAxisTitle(yGenerated))
                ? 0 : PlotAxisTitleHeight;
            _plotBottomExtra += string.IsNullOrWhiteSpace(EffectiveXAxisTitle(xGenerated))
                ? 0 : PlotAxisTitleHeight;
        }

        /// Draws whichever axis titles apply. The y title is rotated to read
        /// bottom-to-top, as axis labels conventionally do.
        private void DrawAxisTitles(string xGenerated = null, string yGenerated = null)
        {
            var (l, t, w, h) = PlotArea();

            string xTitle = EffectiveXAxisTitle(xGenerated);
            string yTitle = EffectiveYAxisTitle(yGenerated);

            if (!string.IsNullOrWhiteSpace(xTitle))
                PlotText(xTitle, l + w / 2, t + h + 20, anchorX: 0.5, fontSize: 10);

            if (!string.IsNullOrWhiteSpace(yTitle))
            {
                var tb = new TextBlock
                {
                    Text = yTitle,
                    FontSize = 10,
                    Foreground = PlotAxis,
                    RenderTransform = new RotateTransform(-90),
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                // Rotation happens about the centre, so position the unrotated box
                // centred on where the rotated text should sit.
                Canvas.SetLeft(tb, 4 + PlotAxisTitleHeight / 2 - tb.DesiredSize.Width / 2);
                Canvas.SetTop(tb, t + h / 2 - tb.DesiredSize.Height / 2);
                UI_PlotCanvas.Children.Add(tb);
            }
        }

        // X and Y are joined on (specimen, attempt): attempt n of one kind pairs
        // with attempt n of the other on the same specimen, matching how the wide
        // tables align kinds row by row.
        private List<ScatterPoint> PairPlotSamples(string xVar, string yVar)
        {
            var pairs = new List<ScatterPoint>();
            if (!_plotData.TryGetValue(xVar, out var xs) ||
                !_plotData.TryGetValue(yVar, out var ys)) return pairs;

            var yByKey = new Dictionary<(string, int), double>();
            foreach (var s in ys)
                yByKey[(s.Specimen, s.Attempt)] = s.Value;

            foreach (var s in xs)
            {
                if (yByKey.TryGetValue((s.Specimen, s.Attempt), out double y))
                    pairs.Add(new ScatterPoint { Specimen = s.Specimen, X = s.Value, Y = y });
            }

            return pairs;
        }

        // =====================
        // Shared drawing helpers
        // =====================

        private const double PlotMarginL = 46;   // room for y-axis labels
        private const double PlotMarginR = 12;
        private const double PlotMarginT = 24;   // room for the title
        private const double PlotMarginB = 34;   // room for x-axis labels
        private const double PlotAxisTitleHeight = 15;
        private const double LegendRowHeight = 15;

        private static readonly Brush PlotAxis = Brushes.Black;

        /// One legend item. A null shape draws the filled square a fill series
        /// uses; a shape draws that marker, for the point aesthetics.
        private sealed class LegendEntry
        {
            public string Label;
            public Brush Fill;
            public PlotPointShape? Shape;
        }

        // Extra bottom and left space claimed by the legend and axis titles, so
        // the plot area shrinks to make room rather than being drawn over.
        private double _plotBottomExtra;
        private double _plotLeftExtra;
        private List<(LegendEntry Entry, double X, int Row)> _legendLayout;

        private (double L, double T, double W, double H) PlotArea()
        {
            double w = UI_PlotCanvas.ActualWidth, h = UI_PlotCanvas.ActualHeight;
            double left = PlotMarginL + _plotLeftExtra;
            return (left, PlotMarginT,
                    Math.Max(10, w - left - PlotMarginR),
                    Math.Max(10, h - PlotMarginT - PlotMarginB - _plotBottomExtra));
        }

        /// Reserves room for whichever axis titles the user supplied. Must run
        /// before PlotArea is read.
        private void PrepareAxisTitles()
        {
            _plotLeftExtra = string.IsNullOrWhiteSpace(_plotOptions.YAxisTitle)
                ? 0 : PlotAxisTitleHeight;
            _plotBottomExtra += string.IsNullOrWhiteSpace(_plotOptions.XAxisTitle)
                ? 0 : PlotAxisTitleHeight;
        }

        /// Legend entries for the fill split, empty when the fill is not split.
        private List<LegendEntry> FillLegendEntries<T>(List<PlotSeries<T>> series, string colourBy)
        {
            if (colourBy == null) return new List<LegendEntry>();

            return series
                .Select(s => new LegendEntry { Label = s.Label, Fill = s.Fill })
                .ToList();
        }

        /// Legend entries for whichever point aesthetics are mapped to a column.
        /// One column driving both colour and shape gives a single set showing
        /// both, rather than two sets repeating the same level names.
        private List<LegendEntry> PointLegendEntries(bool pointsDrawn)
        {
            var entries = new List<LegendEntry>();
            if (!pointsDrawn) return entries;

            bool sameColumn = PointColorIsColumn && PointShapeIsColumn &&
                string.Equals(_plotOptions.PointColor, _plotOptions.PointShape,
                    StringComparison.OrdinalIgnoreCase);

            if (PointColorIsColumn)
            {
                var palette = _plotOptions.PaletteBrushes();

                foreach (var level in _pointColorLevels.OrderBy(kv => kv.Value))
                {
                    entries.Add(new LegendEntry
                    {
                        Label = level.Key,
                        Fill = palette[level.Value % palette.Length],
                        Shape = sameColumn
                            ? PlotOptions.ShapeCycle[level.Value % PlotOptions.ShapeCycle.Length]
                            : PlotPointShape.Circle
                    });
                }
            }

            if (PointShapeIsColumn && !sameColumn)
            {
                foreach (var level in _pointShapeLevels.OrderBy(kv => kv.Value))
                {
                    entries.Add(new LegendEntry
                    {
                        Label = level.Key,
                        Fill = PlotOptions.DefaultPointBrush(),
                        Shape = PlotOptions.ShapeCycle[level.Value % PlotOptions.ShapeCycle.Length]
                    });
                }
            }

            return entries;
        }

        /// Lays the legend out and reserves its height. Must run before PlotArea is
        /// read, since it changes how much room the plot itself gets.
        private void PrepareLegend(List<LegendEntry> entries)
        {
            _legendLayout = new List<(LegendEntry, double, int)>();

            if (entries == null || entries.Count == 0) return;

            double available = Math.Max(60, UI_PlotCanvas.ActualWidth - 8);
            double x = 4;
            int row = 0;

            foreach (var entry in entries)
            {
                double itemWidth = 14 + MeasureTextWidth(entry.Label, 10) + 10;

                // Wrap rather than run off the edge: the sidebar is narrow.
                if (x > 4 && x + itemWidth > available)
                {
                    row++;
                    x = 4;
                }

                _legendLayout.Add((entry, x, row));
                x += itemWidth;
            }

            _plotBottomExtra += (row + 1) * LegendRowHeight + 4;
        }

        private void DrawLegend()
        {
            if (_legendLayout == null || _legendLayout.Count == 0) return;

            // The legend sits below everything, under any x-axis title.
            int rows = _legendLayout.Max(item => item.Row) + 1;
            double top = UI_PlotCanvas.ActualHeight - (rows * LegendRowHeight + 4) + 2;

            foreach (var (entry, x, row) in _legendLayout)
            {
                double y = top + row * LegendRowHeight;

                if (entry.Shape.HasValue)
                {
                    DrawMarker(x + 5, y + 7, 4, entry.Fill, entry.Shape.Value);
                }
                else
                {
                    var swatch = new Rectangle
                    {
                        Width = 10,
                        Height = 10,
                        Fill = entry.Fill,
                        Stroke = PlotAxis,
                        StrokeThickness = 0.5
                    };
                    Canvas.SetLeft(swatch, x);
                    Canvas.SetTop(swatch, y + 2);
                    UI_PlotCanvas.Children.Add(swatch);
                }

                PlotText(entry.Label, x + 14, y);
            }
        }

        /// Draws whichever axis titles were supplied. The y title is rotated to
        /// read bottom-to-top, as axis labels conventionally do.
        private void DrawAxisTitles()
        {
            var (l, t, w, h) = PlotArea();

            if (!string.IsNullOrWhiteSpace(_plotOptions.XAxisTitle))
                PlotText(_plotOptions.XAxisTitle, l + w / 2, t + h + 20, anchorX: 0.5, fontSize: 10);

            if (!string.IsNullOrWhiteSpace(_plotOptions.YAxisTitle))
            {
                var tb = new TextBlock
                {
                    Text = _plotOptions.YAxisTitle,
                    FontSize = 10,
                    Foreground = PlotAxis,
                    RenderTransform = new RotateTransform(-90),
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                // Rotation happens about the centre, so position the unrotated box
                // centred on where the rotated text should sit.
                Canvas.SetLeft(tb, 4 + PlotAxisTitleHeight / 2 - tb.DesiredSize.Width / 2);
                Canvas.SetTop(tb, t + h / 2 - tb.DesiredSize.Height / 2);
                UI_PlotCanvas.Children.Add(tb);
            }
        }

        private static double MeasureTextWidth(string text, double fontSize)
        {
            var tb = new TextBlock { Text = text, FontSize = fontSize };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tb.DesiredSize.Width;
        }

        private void PlotLine(double x1, double y1, double x2, double y2,
            Brush stroke = null, double thickness = 1)
        {
            UI_PlotCanvas.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = stroke ?? PlotAxis,
                StrokeThickness = thickness
            });
        }

        // Draws only the part of the segment inside the plot frame, so a steep
        // trend line stops at the axis instead of crossing the labels.
        private void PlotClippedLine(double x1, double y1, double x2, double y2,
            double l, double t, double w, double h, Brush stroke, double thickness)
        {
            if (ClipSegment(ref x1, ref y1, ref x2, ref y2, l, t, l + w, t + h))
                PlotLine(x1, y1, x2, y2, stroke, thickness);
        }

        // anchorX/anchorY place the text relative to (x, y):
        // 0 = left/top edge at the point, 0.5 = centred, 1 = right/bottom edge.
        private void PlotText(string text, double x, double y,
            double anchorX = 0, double anchorY = 0, double fontSize = 10, Brush foreground = null)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = foreground ?? PlotAxis
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, x - tb.DesiredSize.Width * anchorX);
            Canvas.SetTop(tb, y - tb.DesiredSize.Height * anchorY);
            UI_PlotCanvas.Children.Add(tb);
        }

        // Category names collide in a narrow panel, so each is trimmed to its slot
        // and carries the full name as a tooltip.
        private void PlotSlotLabel(string text, double left, double width, double top)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = PlotAxis,
                Width = Math.Max(8, width - 2),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = text
            };
            Canvas.SetLeft(tb, left + 1);
            Canvas.SetTop(tb, top);
            UI_PlotCanvas.Children.Add(tb);
        }

        /// One point in the given shape. Filled shapes take the brush as a fill;
        /// the cross and plus are stroked, so they read as marks rather than blobs
        /// at small sizes. A null brush or the None shape draws nothing.
        private void DrawMarker(double cx, double cy, double r, Brush fill, PlotPointShape shape)
        {
            if (fill == null || shape == PlotPointShape.None) return;

            switch (shape)
            {
                case PlotPointShape.Square:
                    {
                        var sq = new Rectangle { Width = r * 2, Height = r * 2, Fill = fill };
                        Canvas.SetLeft(sq, cx - r);
                        Canvas.SetTop(sq, cy - r);
                        UI_PlotCanvas.Children.Add(sq);
                        break;
                    }
                case PlotPointShape.Triangle:
                    {
                        var tri = new Polygon
                        {
                            Fill = fill,
                            Points = new PointCollection
                        {
                            new Point(cx, cy - r),
                            new Point(cx + r, cy + r * 0.8),
                            new Point(cx - r, cy + r * 0.8)
                        }
                        };
                        UI_PlotCanvas.Children.Add(tri);
                        break;
                    }
                case PlotPointShape.Diamond:
                    {
                        var dia = new Polygon
                        {
                            Fill = fill,
                            Points = new PointCollection
                        {
                            new Point(cx, cy - r),
                            new Point(cx + r, cy),
                            new Point(cx, cy + r),
                            new Point(cx - r, cy)
                        }
                        };
                        UI_PlotCanvas.Children.Add(dia);
                        break;
                    }
                case PlotPointShape.Cross:
                    PlotLine(cx - r, cy - r, cx + r, cy + r, fill, 1.6);
                    PlotLine(cx - r, cy + r, cx + r, cy - r, fill, 1.6);
                    break;
                case PlotPointShape.Plus:
                    PlotLine(cx - r, cy, cx + r, cy, fill, 1.6);
                    PlotLine(cx, cy - r, cx, cy + r, fill, 1.6);
                    break;
                default:
                    PlotDot(cx, cy, r, fill);
                    break;
            }
        }

        /// One data point, in whatever colour and shape its specimen resolves to.
        private void DrawPoint(double cx, double cy, double r, string specimen, bool forceVisible)
        {
            DrawMarker(cx, cy, r,
                PointBrushFor(specimen, forceVisible),
                PointShapeFor(specimen, forceVisible));
        }

        // Deterministic offset in [-1, 1] for spreading stacked points sideways.
        // Derived from the index rather than a live RNG so a redraw, a resize, or
        // an option change never reshuffles the same data.
        private static double PointJitter(int index)
        {
            unchecked
            {
                int h = index * 1103515245 + 12345;
                h ^= h >> 13;
                h *= 1274126177;
                h ^= h >> 16;
                return ((h & 0xFFFF) / 32767.5) - 1.0;
            }
        }

        private void PlotDot(double cx, double cy, double r, Brush fill)
        {
            var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill };
            Canvas.SetLeft(dot, cx - r);
            Canvas.SetTop(dot, cy - r);
            UI_PlotCanvas.Children.Add(dot);
        }

        /// The plot title: the user's own when set, otherwise the generated one.
        private void PlotTitle(string generated)
        {
            var (l, _, w, _) = PlotArea();
            string text = string.IsNullOrWhiteSpace(_plotOptions.Title)
                ? generated
                : _plotOptions.Title;
            PlotText(text, l + w / 2, 4, anchorX: 0.5, fontSize: 11);
        }

        private void PlotMessage(string text)
        {
            double w = UI_PlotCanvas.ActualWidth, h = UI_PlotCanvas.ActualHeight;
            var tb = new TextBlock
            {
                Text = text,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = Math.Max(40, w - 24)
            };
            tb.Measure(new Size(tb.MaxWidth, double.PositiveInfinity));
            Canvas.SetLeft(tb, (w - tb.DesiredSize.Width) / 2);
            Canvas.SetTop(tb, (h - tb.DesiredSize.Height) / 2);
            UI_PlotCanvas.Children.Add(tb);
        }

        private void DrawYAxis(double l, double t, double h,
            Func<double, double> toPy, IEnumerable<double> ticks)
        {
            PlotLine(l, t, l, t + h);
            foreach (double tick in ticks)
            {
                double py = toPy(tick);
                if (py < t - 0.5 || py > t + h + 0.5) continue;
                PlotLine(l - 4, py, l, py);
                PlotText(FormatTick(tick), l - 6, py, anchorX: 1, anchorY: 0.5);
            }
        }

        private void DrawXAxis(double l, double t, double w, double h,
            Func<double, double> toPx, IEnumerable<double> ticks)
        {
            double baseY = t + h;
            PlotLine(l, baseY, l + w, baseY);
            foreach (double tick in ticks)
            {
                double px = toPx(tick);
                if (px < l - 0.5 || px > l + w + 0.5) continue;
                PlotLine(px, baseY, px, baseY + 4);
                PlotText(FormatTick(tick), px, baseY + 6, anchorX: 0.5);
            }
        }

        // =====================
        // Math helpers
        // =====================

        private static (double Min, double Max) PadRange(double min, double max, double frac = 0.05)
        {
            if (max > min)
            {
                double pad = (max - min) * frac;
                return (min - pad, max + pad);
            }

            // A flat series still needs a visible range.
            double d = Math.Abs(min) > 1e-9 ? Math.Abs(min) * 0.1 : 1.0;
            return (min - d, max + d);
        }

        // Tick positions at a "nice" step (1, 2, or 5 times a power of ten).
        private static List<double> NiceTicks(double min, double max, int target = 5)
        {
            var ticks = new List<double>();
            double range = max - min;
            if (range <= 0) { ticks.Add(min); return ticks; }

            double rawStep = range / target;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double norm = rawStep / mag;
            double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;

            for (double v = Math.Ceiling(min / step) * step; v <= max + step * 1e-9; v += step)
                ticks.Add(v);
            return ticks;
        }

        private static string FormatTick(double v)
        {
            double a = Math.Abs(v);
            if (a >= 100000 || (a > 0 && a < 0.001))
                return v.ToString("0.##e0", CultureInfo.InvariantCulture);
            return Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);
        }

        // Linear-interpolation quantile of an ascending list (R's default type 7).
        private static double Quantile(List<double> sorted, double p)
        {
            if (sorted.Count == 1) return sorted[0];
            double pos = p * (sorted.Count - 1);
            int lo = (int)Math.Floor(pos);
            int hi = lo + 1 < sorted.Count ? lo + 1 : lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        // Acklam's rational approximation of the inverse standard-normal CDF
        // (relative error ~1.15e-9), used for the QQ plot's theoretical quantiles.
        private static readonly double[] NqA =
        {
            -3.969683028665376e+01,  2.209460984245205e+02, -2.759285104469687e+02,
             1.383577518672690e+02, -3.066479806614716e+01,  2.506628277459239e+00
        };
        private static readonly double[] NqB =
        {
            -5.447609879822406e+01,  1.615858368580409e+02, -1.556989798598866e+02,
             6.680131188771972e+01, -1.328068155288572e+01
        };
        private static readonly double[] NqC =
        {
            -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
            -2.549732539343734e+00,  4.374664141464968e+00,  2.938163982698783e+00
        };
        private static readonly double[] NqD =
        {
             7.784695709041462e-03,  3.224671290700398e-01,  2.445134137142996e+00,
             3.754408661907416e+00
        };

        private static double NormalQuantile(double p)
        {
            const double pLow = 0.02425, pHigh = 1 - pLow;
            double q;

            if (p < pLow)
            {
                q = Math.Sqrt(-2 * Math.Log(p));
                return (((((NqC[0] * q + NqC[1]) * q + NqC[2]) * q + NqC[3]) * q + NqC[4]) * q + NqC[5]) /
                       ((((NqD[0] * q + NqD[1]) * q + NqD[2]) * q + NqD[3]) * q + 1);
            }
            if (p > pHigh)
            {
                q = Math.Sqrt(-2 * Math.Log(1 - p));
                return -(((((NqC[0] * q + NqC[1]) * q + NqC[2]) * q + NqC[3]) * q + NqC[4]) * q + NqC[5]) /
                        ((((NqD[0] * q + NqD[1]) * q + NqD[2]) * q + NqD[3]) * q + 1);
            }

            q = p - 0.5;
            double r = q * q;
            return (((((NqA[0] * r + NqA[1]) * r + NqA[2]) * r + NqA[3]) * r + NqA[4]) * r + NqA[5]) * q /
                   (((((NqB[0] * r + NqB[1]) * r + NqB[2]) * r + NqB[3]) * r + NqB[4]) * r + 1);
        }

        // Liang-Barsky clip of a segment to a rectangle.
        private static bool ClipSegment(ref double x1, ref double y1, ref double x2, ref double y2,
            double xLo, double yLo, double xHi, double yHi)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x1 - xLo, xHi - x1, y1 - yLo, yHi - y1 };

            for (int i = 0; i < 4; i++)
            {
                if (p[i] == 0)
                {
                    if (q[i] < 0) return false;
                    continue;
                }
                double t = q[i] / p[i];
                if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }

            double nx1 = x1 + t0 * dx, ny1 = y1 + t0 * dy;
            double nx2 = x1 + t1 * dx, ny2 = y1 + t1 * dy;
            x1 = nx1; y1 = ny1; x2 = nx2; y2 = ny2;
            return true;
        }

        // =====================
        // Trend fitting
        // =====================

        // Fraction of the points entering each local fit, matching R's loess
        // default. Lower values track the data more closely and noisily.
        private const double LoessSpan = 0.75;
        private const int LoessSteps = 60;

        /// Ordinary least squares y = intercept + slope·x. False when every x is
        /// the same, which has no line to fit.
        private static bool FitLinear(IReadOnlyList<ScatterPoint> pts,
            out double intercept, out double slope, out double r2)
        {
            intercept = slope = r2 = 0;
            int n = pts.Count;
            if (n < 2) return false;

            double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
            foreach (var p in pts)
            {
                sx += p.X; sy += p.Y;
                sxx += p.X * p.X; sxy += p.X * p.Y; syy += p.Y * p.Y;
            }

            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-12) return false;

            slope = (n * sxy - sx * sy) / denom;
            intercept = (sy - slope * sx) / n;

            double ssTot = syy - sy * sy / n;
            double ssRes = 0;
            foreach (var p in pts)
            {
                double e = p.Y - (intercept + slope * p.X);
                ssRes += e * e;
            }

            // A flat response has nothing for the line to explain, so report 0
            // rather than the 0/0 that the formula would give.
            r2 = ssTot > 1e-12 ? Math.Max(0, 1 - ssRes / ssTot) : 0;
            return true;
        }

        /// Locally weighted linear regression evaluated on a grid across the x
        /// range: at each point the nearest span·n neighbours are fitted with
        /// tricube weights. Empty when the data cannot support a local fit.
        private static List<Point> LoessCurve(IReadOnlyList<ScatterPoint> pts)
        {
            var curve = new List<Point>();
            int n = pts.Count;
            if (n < 3) return curve;

            var xs = new double[n];
            var ys = new double[n];
            for (int i = 0; i < n; i++) { xs[i] = pts[i].X; ys[i] = pts[i].Y; }

            double xMin = xs.Min(), xMax = xs.Max();
            if (xMax - xMin < 1e-12) return curve;

            int q = Math.Min(n, Math.Max(2, (int)Math.Ceiling(LoessSpan * n)));

            var dist = new double[n];
            var sortedDist = new double[n];

            for (int s = 0; s < LoessSteps; s++)
            {
                double x0 = xMin + (xMax - xMin) * s / (LoessSteps - 1.0);

                for (int i = 0; i < n; i++) dist[i] = Math.Abs(xs[i] - x0);
                Array.Copy(dist, sortedDist, n);
                Array.Sort(sortedDist);
                double d = sortedDist[q - 1];

                double sw = 0, swx = 0, swy = 0, swxx = 0, swxy = 0;
                for (int i = 0; i < n; i++)
                {
                    // With every neighbour sitting on x0 the bandwidth collapses,
                    // so only the coincident points count.
                    double u = d > 1e-12 ? dist[i] / d : (dist[i] <= 1e-12 ? 0 : 2);
                    if (u >= 1) continue;

                    double tri = 1 - u * u * u;
                    double wgt = tri * tri * tri;

                    sw += wgt;
                    swx += wgt * xs[i];
                    swy += wgt * ys[i];
                    swxx += wgt * xs[i] * xs[i];
                    swxy += wgt * xs[i] * ys[i];
                }

                if (sw <= 1e-12) continue;

                double denom = sw * swxx - swx * swx;

                // Singular locally (all contributing x equal): the weighted mean
                // is the best the neighbourhood supports.
                double y0 = Math.Abs(denom) < 1e-12
                    ? swy / sw
                    : (swy * swxx - swx * swxy + (sw * swxy - swx * swy) * x0) / denom;

                curve.Add(new Point(x0, y0));
            }

            return curve;
        }

        // =====================
        // Confidence ellipses
        // =====================

        private const int EllipseSegments = 72;

        // Squared Mahalanobis radius of the 95% ellipse of a bivariate normal: the
        // 0.95 quantile of a chi-squared on two degrees of freedom. A constant
        // because the level is fixed at 95%.
        private const double Chi2TwoDf95 = 5.991464547;

        /// The 95% confidence ellipse of a group, in data space, or null when the
        /// group cannot define one. This is the region expected to hold 95% of the
        /// group under a bivariate normal, so it encloses the specimens themselves
        /// rather than bounding the precision of their mean.
        private static List<Point> ConfidenceEllipse(IReadOnlyList<ScatterPoint> pts)
        {
            // Two points define a line, not an area: the covariance is singular and
            // the ellipse would collapse onto the segment joining them.
            int n = pts?.Count ?? 0;
            if (n < 3) return null;

            double mx = pts.Average(p => p.X);
            double my = pts.Average(p => p.Y);

            double sxx = 0, syy = 0, sxy = 0;
            foreach (var p in pts)
            {
                double dx = p.X - mx, dy = p.Y - my;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            // Sample covariance, so the ellipse describes the population the group
            // was drawn from rather than only the specimens in hand.
            sxx /= n - 1;
            syy /= n - 1;
            sxy /= n - 1;

            // Eigenvalues and orientation of the 2x2 covariance, in closed form.
            double mid = (sxx + syy) / 2;
            double half = (sxx - syy) / 2;
            double root = Math.Sqrt(half * half + sxy * sxy);

            double major = mid + root;
            double minor = mid - root;

            // Every specimen at one spot: nothing to draw. A perfectly collinear
            // group keeps its major axis and draws as a sliver.
            if (major <= 1e-12) return null;
            if (minor < 0) minor = 0;

            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            double cos = Math.Cos(angle), sin = Math.Sin(angle);

            double a = Math.Sqrt(Chi2TwoDf95 * major);
            double b = Math.Sqrt(Chi2TwoDf95 * minor);

            var ring = new List<Point>(EllipseSegments + 1);
            for (int i = 0; i <= EllipseSegments; i++)
            {
                double t = 2 * Math.PI * i / EllipseSegments;
                double u = a * Math.Cos(t), v = b * Math.Sin(t);
                ring.Add(new Point(mx + u * cos - v * sin, my + u * sin + v * cos));
            }

            return ring;
        }

        // Drawn segment by segment through the same clip the trend lines use, so a
        // ring never spills over the axis labels.
        private void DrawEllipseRing(
            List<Point> ring, Func<double, double> toPx, Func<double, double> toPy,
            double l, double t, double w, double h, Brush ink)
        {
            for (int i = 1; i < ring.Count; i++)
            {
                PlotClippedLine(
                    toPx(ring[i - 1].X), toPy(ring[i - 1].Y),
                    toPx(ring[i].X), toPy(ring[i].Y),
                    l, t, w, h, ink, 1.2);
            }
        }

        // =====================
        // Renderers
        // =====================

        /// Boxplot: one group of boxes per level of the categorical X, and within
        /// each level one box per fill series present, dodged side by side. The
        /// individual measurements are overlaid on the boxes unless either point
        /// aesthetic is set to None.
        private void DrawBoxplot(string yVar, string category, string colourBy)
        {
            var samples = _plotData[yVar];
            var series = SplitByFill(samples, colourBy, s => s.Specimen);

            PreparePointAesthetics(samples.Select(s => s.Specimen));

            var legend = new List<LegendEntry>();
            legend.AddRange(FillLegendEntries(series, colourBy));
            legend.AddRange(PointLegendEntries(PointsVisible(false)));
            PrepareLegend(legend);
            PrepareAxisTitles();

            var levels = samples
                .Select(s => CategoryValue(category, s.Specimen) ?? CategorySpecimen)
                .Distinct()
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var (l, t, w, h) = PlotArea();

            var all = samples.Select(s => s.Value).ToList();
            var (yMin, yMax) = PadRange(all.Min(), all.Max());
            double ToPy(double v) => t + h - (v - yMin) / (yMax - yMin) * h;

            DrawYAxis(l, t, h, ToPy, NiceTicks(yMin, yMax));
            PlotLine(l, t + h, l + w, t + h);
            PlotTitle($"{yVar} by {category}   (n = {samples.Count})");

            double slot = w / levels.Count;

            for (int li = 0; li < levels.Count; li++)
            {
                string level = levels[li];
                double slotLeft = l + li * slot;

                var present = series
                    .Select(s => new
                    {
                        s.Fill,
                        Items = s.Items
                            .Where(it => (CategoryValue(category, it.Specimen) ?? CategorySpecimen) == level)
                            .OrderBy(it => it.Value)
                            .ToList()
                    })
                    .Where(p => p.Items.Count > 0)
                    .ToList();

                double sub = slot / Math.Max(1, present.Count);

                for (int si = 0; si < present.Count; si++)
                {
                    double cx = slotLeft + sub * (si + 0.5);
                    double half = Math.Min(sub * 0.34, 26);
                    DrawOneBox(present[si].Items, cx, half, present[si].Fill, ToPy);
                }

                PlotSlotLabel(level, slotLeft, slot, t + h + 6);
            }

            DrawAxisTitles();
            DrawLegend();
        }

        // Tukey whiskers: out to the farthest value within 1.5 IQR of the box.
        // Items arrive sorted by value.
        private void DrawOneBox(List<PlotSample> items, double cx, double half,
            Brush fill, Func<double, double> toPy)
        {
            var sorted = items.Select(i => i.Value).ToList();

            double q1 = Quantile(sorted, 0.25);
            double median = Quantile(sorted, 0.50);
            double q3 = Quantile(sorted, 0.75);
            double iqr = q3 - q1;

            double loFence = q1 - 1.5 * iqr, hiFence = q3 + 1.5 * iqr;
            double whiskerLo = sorted.First(v => v >= loFence);
            double whiskerHi = sorted.Last(v => v <= hiFence);

            Brush outline = PlotOptions.OutlineBrush();

            PlotLine(cx, toPy(whiskerLo), cx, toPy(q1), outline);
            PlotLine(cx, toPy(q3), cx, toPy(whiskerHi), outline);
            PlotLine(cx - half * 0.6, toPy(whiskerLo), cx + half * 0.6, toPy(whiskerLo), outline);
            PlotLine(cx - half * 0.6, toPy(whiskerHi), cx + half * 0.6, toPy(whiskerHi), outline);

            var box = new Rectangle
            {
                Width = half * 2,
                Height = Math.Max(1, toPy(q1) - toPy(q3)),
                Stroke = outline,
                StrokeThickness = 1.2,
                Fill = fill
            };
            Canvas.SetLeft(box, cx - half);
            Canvas.SetTop(box, toPy(q3));
            UI_PlotCanvas.Children.Add(box);

            PlotLine(cx - half, toPy(median), cx + half, toPy(median), outline, 2);

            if (PointsVisible(false))
            {
                // Every measurement, spread sideways so points at similar values
                // do not stack into one mark on the centre line.
                double spread = half * 0.72;
                for (int i = 0; i < items.Count; i++)
                {
                    DrawPoint(cx + PointJitter(i) * spread, toPy(items[i].Value), 2.6,
                        items[i].Specimen, forceVisible: false);
                }
            }
            else
            {
                // Points are hidden, but an outlier is part of reading the box, so
                // it stays as a plain dot.
                foreach (double v in sorted)
                    if (v < loFence || v > hiFence)
                        PlotDot(cx, toPy(v), 3, PlotOptions.OutlineBrush());
            }
        }

        private void DrawScatter(List<ScatterPoint> pairs, string xVar, string yVar)
        {
            PreparePointAesthetics(pairs.Select(p => p.Specimen));
            PrepareLegend(PointLegendEntries(pointsDrawn: true));
            PrepareAxisTitles();

            var (l, t, w, h) = PlotArea();

            var (xMin, xMax) = PadRange(pairs.Min(p => p.X), pairs.Max(p => p.X));
            var (yMin, yMax) = PadRange(pairs.Min(p => p.Y), pairs.Max(p => p.Y));

            double ToPx(double v) => l + (v - xMin) / (xMax - xMin) * w;
            double ToPy(double v) => t + h - (v - yMin) / (yMax - yMin) * h;

            DrawYAxis(l, t, h, ToPy, NiceTicks(yMin, yMax));
            DrawXAxis(l, t, w, h, ToPx, NiceTicks(xMin, xMax));
            PlotTitle($"{yVar} vs {xVar}   (n = {pairs.Count})");

            foreach (var p in pairs)
                DrawPoint(ToPx(p.X), ToPy(p.Y), 3, p.Specimen, forceVisible: true);

            string trend = SelectedTrend;
            if (trend == "lm" || trend == "loess")
            {
                // One fit per point-color group, so a colored split compares
                // trends rather than pooling them into a single misleading line.
                var groups = SplitByPointColor(pairs, p => p.Specimen);

                foreach (var group in groups)
                {
                    Brush ink = PointColorIsColumn ? group.Brush : PlotOptions.TrendBrush();
                    DrawTrend(group.Items, trend, xMin, xMax, ToPx, ToPy, ink, l, t, w, h);
                }

                // The caption would be ambiguous with several fits on screen.
                if (trend == "lm" && groups.Count == 1 &&
                    FitLinear(pairs, out double a, out double b, out double r2))
                {
                    string sign = a < 0 ? "\u2212" : "+";
                    PlotText(
                        $"y = {Math.Round(b, 4).ToString(CultureInfo.InvariantCulture)}x " +
                        $"{sign} {Math.Round(Math.Abs(a), 4).ToString(CultureInfo.InvariantCulture)}   " +
                        $"R\u00B2 = {r2:F3}",
                        l + w, t + h - 2, anchorX: 1, anchorY: 1, fontSize: 10);
                }
            }

            DrawAxisTitles();
            DrawLegend();
        }

        private void DrawTrend(List<ScatterPoint> pts, string kind,
            double xMin, double xMax, Func<double, double> toPx, Func<double, double> toPy,
            Brush ink, double l, double t, double w, double h)
        {
            if (kind == "lm")
            {
                if (!FitLinear(pts, out double a, out double b, out _)) return;

                PlotClippedLine(
                    toPx(xMin), toPy(a + b * xMin),
                    toPx(xMax), toPy(a + b * xMax),
                    l, t, w, h, ink, 1.6);
                return;
            }

            var curve = LoessCurve(pts);
            for (int i = 1; i < curve.Count; i++)
            {
                PlotClippedLine(
                    toPx(curve[i - 1].X), toPy(curve[i - 1].Y),
                    toPx(curve[i].X), toPy(curve[i].Y),
                    l, t, w, h, ink, 1.6);
            }
        }

        /// Histogram. Colour splitting is deliberately absent: overlapping or
        /// stacked bars change what the bar heights mean, so every bar takes the
        /// one fill.
        private void DrawHistogram(string variable)
        {
            var samples = _plotData[variable];
            PrepareAxisTitles();

            var (l, t, w, h) = PlotArea();

            double dataMin = samples.Min(s => s.Value), dataMax = samples.Max(s => s.Value);
            if (dataMax <= dataMin) { dataMin -= 0.5; dataMax += 0.5; }

            int bins = Math.Max(3, Math.Min(25, (int)Math.Ceiling(Math.Sqrt(samples.Count))));
            double binW = (dataMax - dataMin) / bins;

            var counts = new int[bins];
            foreach (var s in samples)
            {
                int i = (int)((s.Value - dataMin) / binW);
                if (i >= bins) i = bins - 1;   // the maximum lands in the last bin
                counts[i]++;
            }

            int maxCount = counts.Max();

            double ToPx(double v) => l + (v - dataMin) / (dataMax - dataMin) * w;
            double ToPy(double count) => t + h - count / (maxCount * 1.06) * h;

            // Counts get whole-number ticks so the axis never shows "2.5 bars".
            var yTicks = new List<double>();
            int stepC = Math.Max(1, (int)Math.Ceiling(maxCount / 5.0));
            for (int c = 0; c <= maxCount; c += stepC) yTicks.Add(c);

            DrawYAxis(l, t, h, ToPy, yTicks);
            DrawXAxis(l, t, w, h, ToPx, NiceTicks(dataMin, dataMax));
            PlotTitle($"{variable}   (n = {samples.Count})");

            var fill = PlotOptions.DefaultFillBrush();
            var outline = PlotOptions.OutlineBrush();

            for (int i = 0; i < bins; i++)
            {
                if (counts[i] == 0) continue;

                double x0 = ToPx(dataMin + i * binW);
                double x1 = ToPx(dataMin + (i + 1) * binW);
                double yTop = ToPy(counts[i]);

                var bar = new Rectangle
                {
                    Width = Math.Max(1, x1 - x0 - 1),   // 1 px gap between bars
                    Height = Math.Max(1, t + h - yTop),
                    Fill = fill,
                    Stroke = outline,
                    StrokeThickness = 0.8
                };
                Canvas.SetLeft(bar, x0);
                Canvas.SetTop(bar, yTop);
                UI_PlotCanvas.Children.Add(bar);
            }

            DrawAxisTitles();
        }

        /// QQ plot against a normal. Each point-color group is ranked and
        /// referenced against its own fitted line, so a split compares
        /// distributions rather than ranking the pooled data.
        private void DrawQQPlot(string variable)
        {
            var samples = _plotData[variable];
            PreparePointAesthetics(samples.Select(s => s.Specimen));
            PrepareLegend(PointLegendEntries(pointsDrawn: true));
            PrepareAxisTitles();

            var (l, t, w, h) = PlotArea();

            // Groups of one or two cannot be ranked meaningfully.
            var usable = SplitByPointColor(samples, s => s.Specimen)
                .Where(g => g.Items.Count >= 3)
                .ToList();

            if (usable.Count == 0)
            {
                PlotMessage("Each color needs at least 3 values for a QQ plot.");
                return;
            }

            var prepared = usable.Select(g =>
            {
                var items = g.Items.OrderBy(i => i.Value).ToList();
                var sorted = items.Select(i => i.Value).ToList();
                int n = sorted.Count;

                // Theoretical quantiles at the midpoints of n equal probability
                // intervals, which avoids asking for the 0 or 1 quantile.
                var theoretical = new double[n];
                for (int i = 0; i < n; i++) theoretical[i] = NormalQuantile((i + 0.5) / n);

                double mean = sorted.Average();
                double sd = Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / (n - 1));

                return new
                {
                    g.Brush,
                    Items = items,
                    Sorted = sorted,
                    Theoretical = theoretical,
                    Mean = mean,
                    Sd = sd
                };
            }).ToList();

            var (xMin, xMax) = PadRange(
                prepared.Min(p => p.Theoretical.First()),
                prepared.Max(p => p.Theoretical.Last()));
            var (yMin, yMax) = PadRange(
                prepared.Min(p => p.Sorted.First()),
                prepared.Max(p => p.Sorted.Last()));

            double ToPx(double v) => l + (v - xMin) / (xMax - xMin) * w;
            double ToPy(double v) => t + h - (v - yMin) / (yMax - yMin) * h;

            DrawYAxis(l, t, h, ToPy, NiceTicks(yMin, yMax));
            DrawXAxis(l, t, w, h, ToPx, NiceTicks(xMin, xMax));
            PlotTitle($"{variable} \u2014 normal QQ  (n = {samples.Count})");

            foreach (var p in prepared)
            {
                // Reference line y = mean + sd·x for this group.
                PlotClippedLine(
                    ToPx(xMin), ToPy(p.Mean + p.Sd * xMin),
                    ToPx(xMax), ToPy(p.Mean + p.Sd * xMax),
                    l, t, w, h,
                    PointColorIsColumn ? p.Brush : PlotOptions.TrendBrush(), 1);

                for (int i = 0; i < p.Items.Count; i++)
                {
                    DrawPoint(ToPx(p.Theoretical[i]), ToPy(p.Sorted[i]), 3,
                        p.Items[i].Specimen, forceVisible: true);
                }
            }

            DrawAxisTitles();
            DrawLegend();
        }

        private void DrawDotPlot(string variable)
        {
            var samples = _plotData[variable];
            PreparePointAesthetics(samples.Select(s => s.Specimen));
            PrepareLegend(PointLegendEntries(pointsDrawn: true));
            PrepareAxisTitles();

            var (l, t, w, h) = PlotArea();

            double dataMin = samples.Min(s => s.Value), dataMax = samples.Max(s => s.Value);
            if (dataMax <= dataMin) { dataMin -= 0.5; dataMax += 0.5; }

            var (xMin, xMax) = PadRange(dataMin, dataMax, 0.04);

            int bins = Math.Max(8, Math.Min(50, (int)(w / 14)));
            double binW = (dataMax - dataMin) / bins;

            // Bin the samples themselves, so each drawn dot still knows which
            // specimen it came from and can resolve its own colour and shape.
            var binned = new List<PlotSample>[bins];
            for (int i = 0; i < bins; i++) binned[i] = new List<PlotSample>();

            // Colour groups are stacked together, so one group's dots sit in a
            // contiguous run rather than interleaving with another's.
            foreach (var group in SplitByPointColor(samples, s => s.Specimen))
            {
                foreach (var s in group.Items)
                {
                    int i = (int)((s.Value - dataMin) / binW);
                    if (i >= bins) i = bins - 1;
                    binned[i].Add(s);
                }
            }

            int maxStack = binned.Max(b => b.Count);

            double ToPx(double v) => l + (v - xMin) / (xMax - xMin) * w;

            DrawXAxis(l, t, w, h, ToPx, NiceTicks(xMin, xMax));
            PlotTitle($"{variable}   (n = {samples.Count})");

            // Marker size fits both the bin width and the tallest stack; very
            // crowded data overlaps rather than shrinking to invisibility.
            double r = Math.Min((w / bins) / 2 - 1, h / (2.0 * maxStack));
            r = Math.Max(1.5, Math.Min(6, r));
            double spacing = Math.Min(2 * r, h / maxStack);

            double baseY = t + h;
            for (int i = 0; i < bins; i++)
            {
                double cx = ToPx(dataMin + (i + 0.5) * binW);

                for (int k = 0; k < binned[i].Count; k++)
                {
                    DrawPoint(cx, baseY - r - k * spacing, r,
                        binned[i][k].Specimen, forceVisible: true);
                }
            }

            DrawAxisTitles();
            DrawLegend();
        }

        /// Scores plot: one point per specimen in the plane of two components,
        /// taking the same colour and shape mappings every other plot uses, and
        /// optionally a 95% confidence ellipse around each colour group.
        private void DrawPcaScores(string xPc, string yPc)
        {
            int xi = PcaComponentIndex(xPc);
            int yi = PcaComponentIndex(yPc);

            if (xi < 0 || yi < 0 ||
                xi >= _pcaResult.ComponentCount || yi >= _pcaResult.ComponentCount)
            {
                PlotMessage("Choose the components for X and Y.");
                return;
            }

            var points = new List<ScatterPoint>();
            for (int r = 0; r < _pcaResult.ObservationCount; r++)
            {
                points.Add(new ScatterPoint
                {
                    Specimen = _pcaRowSpecimens[r],
                    X = _pcaResult.Scores[r, xi],
                    Y = _pcaResult.Scores[r, yi]
                });
            }

            string xTitle = $"{xPc} ({_pcaResult.ExplainedVariancePercent[xi]:F1}%)";
            string yTitle = $"{yPc} ({_pcaResult.ExplainedVariancePercent[yi]:F1}%)";

            PreparePointAesthetics(points.Select(p => p.Specimen));
            PrepareLegend(PointLegendEntries(pointsDrawn: true));
            PrepareAxisTitles(xTitle, yTitle);

            // One ellipse per point-colour group, matching how the scatter plot fits
            // one trend line per group.
            var groups = _plotOptions.ConfidenceEllipses
                ? SplitByPointColor(points, p => p.Specimen)
                : new List<(string Label, Brush Brush, List<ScatterPoint> Items)>();

            var ellipses = groups
                .Select(g => new
                {
                    Ink = PointColorIsColumn ? g.Brush : PlotOptions.TrendBrush(),
                    Ring = ConfidenceEllipse(g.Items)
                })
                .Where(e => e.Ring != null)
                .ToList();

            int smallGroups = groups.Count - ellipses.Count;

            var (l, t, w, h) = PlotArea();

            // A ring is part of the plot, so the axes reach around it rather than
            // cutting it off at the outermost specimen.
            var xs = points.Select(p => p.X)
                .Concat(ellipses.SelectMany(e => e.Ring.Select(q => q.X)));
            var ys = points.Select(p => p.Y)
                .Concat(ellipses.SelectMany(e => e.Ring.Select(q => q.Y)));

            var (xMin, xMax) = PadRange(xs.Min(), xs.Max());
            var (yMin, yMax) = PadRange(ys.Min(), ys.Max());

            double ToPx(double v) => l + (v - xMin) / (xMax - xMin) * w;
            double ToPy(double v) => t + h - (v - yMin) / (yMax - yMin) * h;

            DrawYAxis(l, t, h, ToPy, NiceTicks(yMin, yMax));
            DrawXAxis(l, t, w, h, ToPx, NiceTicks(xMin, xMax));

            PlotTitle($"PCA scores   (n = {points.Count}, " +
                      $"{_pcaResult.VariableCount} variables)");

            // The scores are centred, so the origin is the sample mean: worth a
            // reference line, since distance from it is what the plot shows.
            var guide = PlotOptions.TrendBrush();
            PlotClippedLine(ToPx(0), t, ToPx(0), t + h, l, t, w, h, guide, 0.6);
            PlotClippedLine(l, ToPy(0), l + w, ToPy(0), l, t, w, h, guide, 0.6);

            // Rings before the markers, so a specimen sitting on the boundary is
            // drawn over its own ellipse rather than under it.
            foreach (var e in ellipses)
                DrawEllipseRing(e.Ring, ToPx, ToPy, l, t, w, h, e.Ink);

            foreach (var p in points)
                DrawPoint(ToPx(p.X), ToPy(p.Y), 3, p.Specimen, forceVisible: true);

            // A dropped specimen or column changes what the plot is of, so say so
            // rather than leaving an unexplained n.
            var caveats = new List<string>();

            if (_pcaIncompleteSpecimens > 0)
            {
                caveats.Add(_pcaIncompleteSpecimens == 1
                    ? "1 specimen omitted (missing a variable)"
                    : $"{_pcaIncompleteSpecimens} specimens omitted (missing a variable)");
            }

            if (_pcaConstantColumns.Count > 0)
            {
                caveats.Add(_pcaConstantColumns.Count == 1
                    ? "1 constant column dropped"
                    : $"{_pcaConstantColumns.Count} constant columns dropped");
            }

            // A group of one or two has no ellipse; without a line saying so, its
            // absence looks like a bug.
            if (smallGroups > 0)
            {
                caveats.Add(smallGroups == 1
                    ? "1 group too small for an ellipse"
                    : $"{smallGroups} groups too small for an ellipse");
            }

            if (caveats.Count > 0)
            {
                PlotText(string.Join("  \u2022  ", caveats),
                    l + w, t + h - 2, anchorX: 1, anchorY: 1, fontSize: 10);
            }

            DrawAxisTitles(xTitle, yTitle);
            DrawLegend();
        }
    }
}