using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    // Read-only snapshot of the operation history, grouped by operation type into tabs.
    // Built programmatically (same style as the EFD details window) so it is fully
    // self-contained. It reflects the undo/redo history at the moment it is opened:
    // undone and cleared operations are absent because they are no longer in History.
    // Attempts are numbered per tab (Circular Arc 1..n, Parabolic 1..n, etc.), matching
    // the per-type counts shown in the operation counter.
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

        // ── Tab builders ─────────────────────────────────────────────────────────

        private static TabItem BuildCircularArcTab(UndoRedoManager ur, string specimen)
        {
            var grid = MakeGrid();
            AddColumn(grid, "Attempt", nameof(CircularArcHistoryRow.Attempt), 70);
            AddColumn(grid, "Central angle", nameof(CircularArcHistoryRow.CentralAngle));
            AddColumn(grid, "Chord-arc ratio", nameof(CircularArcHistoryRow.ChordArcRatio));
            AddColumn(grid, "Rise-span ratio", nameof(CircularArcHistoryRow.RiseSpanRatio));

            var rows = new List<CircularArcHistoryRow>();
            int attempt = 1;
            foreach (var op in ur.History.OfType<CircularArcOperation>())
                rows.Add(new CircularArcHistoryRow
                {
                    Attempt = attempt++,
                    CentralAngle = Fmt(op.CentralAngle),
                    ChordArcRatio = Fmt(op.ChordArcRatio),
                    RiseSpanRatio = Fmt(op.AspectRatio)   // panel's "Rise-Span Ratio" binds AspectRatio
                });
            grid.ItemsSource = rows;

            return new TabItem
            {
                Header = "Circular Arc",
                Content = BuildTabContent(specimen, "Circular Arc Data", grid)
            };
        }

        private static TabItem BuildParabolicArcTab(UndoRedoManager ur, string specimen)
        {
            var grid = MakeGrid();
            AddColumn(grid, "Attempt", nameof(ParabolicArcHistoryRow.Attempt), 70);
            AddColumn(grid, "Chord-arc ratio", nameof(ParabolicArcHistoryRow.ChordArcRatio));
            AddColumn(grid, "Rise-span ratio", nameof(ParabolicArcHistoryRow.RiseSpanRatio));
            AddColumn(grid, "Vertex curvature", nameof(ParabolicArcHistoryRow.VertexCurvature));

            var rows = new List<ParabolicArcHistoryRow>();
            int attempt = 1;
            foreach (var op in ur.History.OfType<ParabolaOperation>())
                rows.Add(new ParabolicArcHistoryRow
                {
                    Attempt = attempt++,
                    ChordArcRatio = Fmt(op.PChordArcRatio),
                    RiseSpanRatio = Fmt(op.RiseSpanRatio),
                    VertexCurvature = Fmt(op.VertexCurvature)
                });
            grid.ItemsSource = rows;

            return new TabItem
            {
                Header = "Parabolic Arc",
                Content = BuildTabContent(specimen, "Parabolic Arc Data", grid)
            };
        }

        private static TabItem BuildSplineTab(UndoRedoManager ur, string specimen, ScaleCalibration scale)
        {
            var grid = MakeGrid();
            AddColumn(grid, "Attempt", nameof(SplineHistoryRow.Attempt), 70);
            AddColumn(grid, "Turn. Angles / Length", nameof(SplineHistoryRow.TurnPerLength));
            AddColumn(grid, "Sum Turn. Angles", nameof(SplineHistoryRow.SumTurning));
            AddColumn(grid, "Chord-arc ratio", nameof(SplineHistoryRow.ChordArcRatio));
            AddColumn(grid, "Length", nameof(SplineHistoryRow.Length));

            var rows = new List<SplineHistoryRow>();
            int attempt = 1;
            // Both Catmull-Rom and Bézier are SplineOperation, so they share this tab
            // (matching how n_spline counts them together).
            foreach (var op in ur.History.OfType<SplineOperation>())
                rows.Add(new SplineHistoryRow
                {
                    Attempt = attempt++,
                    TurnPerLength = Fmt(op.TurningAngleArcRatio),
                    SumTurning = Fmt(op.SumTurningAngles),
                    ChordArcRatio = Fmt(op.SChordArcRatio),
                    Length = FmtLength(op.SplineLengthPixels, scale)
                });
            grid.ItemsSource = rows;

            return new TabItem
            {
                Header = "n-Point Spline",
                Content = BuildTabContent(specimen, "n-Point Spline Data", grid)
            };
        }

        private static TabItem BuildTriangleTab(UndoRedoManager ur, string specimen, ScaleCalibration scale)
        {
            var grid = MakeGrid();
            AddColumn(grid, "Attempt", nameof(TriangleHistoryRow.Attempt), 70);
            AddColumn(grid, "Angle A", nameof(TriangleHistoryRow.AngleA));
            AddColumn(grid, "Angle B", nameof(TriangleHistoryRow.AngleB));
            AddColumn(grid, "Angle C", nameof(TriangleHistoryRow.AngleC));
            AddColumn(grid, "Area", nameof(TriangleHistoryRow.Area));

            var rows = new List<TriangleHistoryRow>();
            int attempt = 1;
            foreach (var op in ur.History.OfType<GetAngleOperation>())
                rows.Add(new TriangleHistoryRow
                {
                    Attempt = attempt++,
                    AngleA = Fmt(op.AngleA),
                    AngleB = Fmt(op.AngleB),
                    AngleC = Fmt(op.AngleC),
                    Area = FmtArea(op.TriArea, scale)
                });
            grid.ItemsSource = rows;

            return new TabItem
            {
                Header = "Triangle",
                Content = BuildTabContent(specimen, "Triangle Data", grid)
            };
        }

        // ── Shared construction helpers ──────────────────────────────────────────

        // Specimen-name row, "<Op> Data" row, then the table beneath.
        private static FrameworkElement BuildTabContent(string specimenName, string dataTitle, DataGrid grid)
        {
            var specimenBlock = new TextBlock
            {
                Text = specimenName,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(8, 8, 8, 2)
            };
            var dataBlock = new TextBlock
            {
                Text = dataTitle,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray,
                Margin = new Thickness(8, 0, 8, 6)
            };

            var dock = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(specimenBlock, Dock.Top);
            DockPanel.SetDock(dataBlock, Dock.Top);
            dock.Children.Add(specimenBlock);
            dock.Children.Add(dataBlock);
            dock.Children.Add(grid);   // fills remaining space
            return dock;
        }

        private static DataGrid MakeGrid() => new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserSortColumns = false,                       // keep attempts in order
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            Margin = new Thickness(8, 0, 8, 8)
        };

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