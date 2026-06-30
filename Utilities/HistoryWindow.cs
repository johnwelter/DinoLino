using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    // Read-only view of the operation history, one tab per operation type. Within each tab,
    // operations are grouped into per-specimen blocks: every archived (frozen) specimen in
    // order, then the current live specimen at the bottom. Each block is labelled with its
    // specimen's name and numbers its attempts from 1. Specimens with no operations of a
    // tab's type still appear as a labelled, empty block.
    //
    // Each tab can be exported to CSV via a button at the bottom. The CSV is a flat table
    // (one row per operation, with a leading Specimen column) captured at the moment the
    // window was opened — consistent with the grids, which are likewise a one-time snapshot.
    public class HistoryWindow : Window
    {
        public HistoryWindow(UndoRedoManager undoRedo, string specimenName, ScaleCalibration scale)
        {
            Title = "History of operations";
            Width = 720;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var tabs = new TabControl();
            tabs.Items.Add(BuildCircularArcTab(undoRedo, specimenName));
            tabs.Items.Add(BuildParabolicArcTab(undoRedo, specimenName));
            tabs.Items.Add(BuildSplineTab(undoRedo, specimenName, scale));
            tabs.Items.Add(BuildTriangleTab(undoRedo, specimenName, scale));

            Content = tabs;
        }

        // One block per specimen: each archived record in order, then the current live
        // specimen last (labelled with the live name). Tabs filter each block by type.
        private static IEnumerable<(string Name, IReadOnlyList<WorkOperation> Ops)> Blocks(
            UndoRedoManager ur, string currentName)
        {
            foreach (var rec in ur.Archive)
                yield return (rec.SpecimenName, rec.Operations);
            yield return (currentName, ur.History);
        }

        // ── Tab builders ─────────────────────────────────────────────────────────

        private static TabItem BuildCircularArcTab(UndoRedoManager ur, string currentName)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddColumn(grid, "Attempt", nameof(CircularArcHistoryRow.Attempt), 70);
                AddColumn(grid, "Central angle", nameof(CircularArcHistoryRow.CentralAngle));
                AddColumn(grid, "Chord-arc ratio", nameof(CircularArcHistoryRow.ChordArcRatio));
                AddColumn(grid, "Rise-span ratio", nameof(CircularArcHistoryRow.RiseSpanRatio));

                var rows = new List<CircularArcHistoryRow>();
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<CircularArcOperation>())
                {
                    any = true;
                    var r = new CircularArcHistoryRow
                    {
                        Attempt = attempt++,
                        CentralAngle = Fmt(op.CentralAngle),
                        ChordArcRatio = Fmt(op.ChordArcRatio),
                        RiseSpanRatio = Fmt(op.AspectRatio)   // panel's "Rise-Span Ratio" binds AspectRatio
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt.ToString(), r.CentralAngle, r.ChordArcRatio, r.RiseSpanRatio });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Central angle", "Chord-arc ratio", "Rise-span ratio" };
            return WrapTab("Circular Arc", panel, headers, csvRows, "circular_arc_history.csv");
        }

        private static TabItem BuildParabolicArcTab(UndoRedoManager ur, string currentName)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddColumn(grid, "Attempt", nameof(ParabolicArcHistoryRow.Attempt), 70);
                AddColumn(grid, "Chord-arc ratio", nameof(ParabolicArcHistoryRow.ChordArcRatio));
                AddColumn(grid, "Rise-span ratio", nameof(ParabolicArcHistoryRow.RiseSpanRatio));
                AddColumn(grid, "Vertex curvature", nameof(ParabolicArcHistoryRow.VertexCurvature));

                var rows = new List<ParabolicArcHistoryRow>();
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<ParabolaOperation>())
                {
                    any = true;
                    var r = new ParabolicArcHistoryRow
                    {
                        Attempt = attempt++,
                        ChordArcRatio = Fmt(op.PChordArcRatio),
                        RiseSpanRatio = Fmt(op.RiseSpanRatio),
                        VertexCurvature = Fmt(op.VertexCurvature)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt.ToString(), r.ChordArcRatio, r.RiseSpanRatio, r.VertexCurvature });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Chord-arc ratio", "Rise-span ratio", "Vertex curvature" };
            return WrapTab("Parabolic Arc", panel, headers, csvRows, "parabolic_arc_history.csv");
        }

        private static TabItem BuildSplineTab(UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddColumn(grid, "Attempt", nameof(SplineHistoryRow.Attempt), 70);
                AddColumn(grid, "Turn. Angles / Length", nameof(SplineHistoryRow.TurnPerLength));
                AddColumn(grid, "Sum Turn. Angles", nameof(SplineHistoryRow.SumTurning));
                AddColumn(grid, "Chord-arc ratio", nameof(SplineHistoryRow.ChordArcRatio));
                AddColumn(grid, "Length", nameof(SplineHistoryRow.Length));

                // Both Catmull-Rom and Bézier are SplineOperation, so they share this tab.
                var rows = new List<SplineHistoryRow>();
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<SplineOperation>())
                {
                    any = true;
                    var r = new SplineHistoryRow
                    {
                        Attempt = attempt++,
                        TurnPerLength = Fmt(op.TurningAngleArcRatio),
                        SumTurning = Fmt(op.SumTurningAngles),
                        ChordArcRatio = Fmt(op.SChordArcRatio),
                        Length = FmtLength(op.SplineLengthPixels, scale)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt.ToString(), r.TurnPerLength, r.SumTurning, r.ChordArcRatio, r.Length });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Turn. Angles / Length", "Sum Turn. Angles", "Chord-arc ratio", "Length" };
            return WrapTab("n-Point Spline", panel, headers, csvRows, "spline_history.csv");
        }

        private static TabItem BuildTriangleTab(UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddColumn(grid, "Attempt", nameof(TriangleHistoryRow.Attempt), 70);
                AddColumn(grid, "Angle A", nameof(TriangleHistoryRow.AngleA));
                AddColumn(grid, "Angle B", nameof(TriangleHistoryRow.AngleB));
                AddColumn(grid, "Angle C", nameof(TriangleHistoryRow.AngleC));
                AddColumn(grid, "Area", nameof(TriangleHistoryRow.Area));

                var rows = new List<TriangleHistoryRow>();
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<GetAngleOperation>())
                {
                    any = true;
                    var r = new TriangleHistoryRow
                    {
                        Attempt = attempt++,
                        AngleA = Fmt(op.AngleA),
                        AngleB = Fmt(op.AngleB),
                        AngleC = Fmt(op.AngleC),
                        Area = FmtArea(op.TriArea, scale)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt.ToString(), r.AngleA, r.AngleB, r.AngleC, r.Area });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Angle A", "Angle B", "Angle C", "Area" };
            return WrapTab("Triangle", panel, headers, csvRows, "triangle_history.csv");
        }

        // ── Shared construction helpers ──────────────────────────────────────────

        // Scrollable stack of per-specimen blocks with an "Export to CSV" button docked
        // beneath it. The button writes the pre-built rows for THIS tab only.
        private static TabItem WrapTab(string header, StackPanel panel,
            string[] csvHeaders, List<string[]> csvRows, string suggestedFileName)
        {
            var scroll = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var exportButton = new Button
            {
                Content = "Export to CSV…",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8),
                Padding = new Thickness(12, 4, 12, 4)
            };
            exportButton.Click += (s, e) => ExportCsv(csvHeaders, csvRows, suggestedFileName);

            var dock = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(exportButton, Dock.Bottom);
            dock.Children.Add(exportButton);
            dock.Children.Add(scroll);   // fills remaining space
            return new TabItem { Header = header, Content = dock };
        }

        private static TextBlock SpecimenHeader(string name) => new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 12, 8, 4)
        };

        private static DataGrid MakeGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserSortColumns = false,                       // keep attempts in order
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                Margin = new Thickness(8, 0, 8, 12)
            };
            // Size each grid to its content and let the outer ScrollViewer scroll the whole
            // stack, rather than each grid scrolling internally.
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
            return grid;
        }

        private static void AddColumn(DataGrid grid, string header, string path, double? fixedWidth = null)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = fixedWidth.HasValue
                    ? new DataGridLength(fixedWidth.Value)
                    : new DataGridLength(1, DataGridLengthUnitType.Star)
            });
        }

        // ── CSV export ───────────────────────────────────────────────────────────

        // Prompts for a path and writes the given header + rows as CSV. Written with a UTF-8
        // BOM so Excel renders the unit symbols (°, ²) correctly on open.
        private static void ExportCsv(string[] headers, List<string[]> rows, string suggestedFileName)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export to CSV",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = suggestedFileName,
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(CsvEscape)));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", row.Select(CsvEscape)));

            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the file:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Quotes a field if it contains a comma, quote, or newline; doubles any inner quotes.
        private static string CsvEscape(string field)
        {
            field ??= "";
            if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        // ── Value formatting ─────────────────────────────────────────────────────

        private static string Fmt(double v) =>
            Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        private static string FmtLength(double pixels, ScaleCalibration scale) =>
            scale != null && scale.IsCalibrated
                ? $"{scale.ToUnits(pixels):F2} {scale.Unit}"
                : $"{Math.Round(pixels, 1).ToString(CultureInfo.InvariantCulture)} px";

        private static string FmtArea(double pixelArea, ScaleCalibration scale) =>
            scale != null && scale.IsCalibrated
                ? $"{scale.ToUnitsArea(pixelArea):F2} {scale.Unit}\u00B2"
                : $"{Math.Round(pixelArea, 1).ToString(CultureInfo.InvariantCulture)} px\u00B2";
    }

    // ── Row view-models (one per operation type) ─────────────────────────────────
    public class CircularArcHistoryRow
    {
        public int Attempt { get; set; }
        public string CentralAngle { get; set; }
        public string ChordArcRatio { get; set; }
        public string RiseSpanRatio { get; set; }
    }

    public class ParabolicArcHistoryRow
    {
        public int Attempt { get; set; }
        public string ChordArcRatio { get; set; }
        public string RiseSpanRatio { get; set; }
        public string VertexCurvature { get; set; }
    }

    public class SplineHistoryRow
    {
        public int Attempt { get; set; }
        public string TurnPerLength { get; set; }
        public string SumTurning { get; set; }
        public string ChordArcRatio { get; set; }
        public string Length { get; set; }
    }

    public class TriangleHistoryRow
    {
        public int Attempt { get; set; }
        public string AngleA { get; set; }
        public string AngleB { get; set; }
        public string AngleC { get; set; }
        public string Area { get; set; }
    }
}