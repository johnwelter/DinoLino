using DinoLino.Utilities.Operations;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    public class HistoryWindow : Window
    {
        private readonly List<WorkbookSheet> _workbook = new();
        private TextBlock _workbookStatus;
        private Button _exportWorkbookButton;

        private class WorkbookSheet
        {
            public string Name;
            public string[] Headers;
            public List<string[]> Rows;
        }

        // Backs the editable "Attempt" header for one tab.
        private class AttemptHeader : INotifyPropertyChanged
        {
            private string _text = "Attempt";
            public string Text
            {
                get => _text;
                set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        public HistoryWindow(UndoRedoManager undoRedo, string specimenName, ScaleCalibration scale)
        {
            Title = "History of operations";
            Width = 720;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var footer = BuildWorkbookFooter();
            UpdateWorkbookStatus();

            var tabs = new TabControl();
            tabs.Items.Add(BuildCircularArcTab(undoRedo, specimenName));
            tabs.Items.Add(BuildParabolicArcTab(undoRedo, specimenName));
            tabs.Items.Add(BuildSplineTab(undoRedo, specimenName, scale));
            tabs.Items.Add(BuildTriangleTab(undoRedo, specimenName, scale));
            tabs.Items.Add(BuildLineTab(undoRedo, specimenName, scale));
            tabs.Items.Add(BuildOutlineTab(undoRedo, specimenName, scale));

            var root = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(tabs);
            Content = root;
        }

        private static IEnumerable<(string Name, IReadOnlyList<WorkOperation> Ops)> Blocks(
            UndoRedoManager ur, string currentName)
        {
            foreach (var rec in ur.Archive)
                yield return (rec.SpecimenName, rec.Operations);
            yield return (currentName, ur.History);
        }

        private TabItem BuildLineTab(UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();
            var attemptHeader = new AttemptHeader();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(LineHistoryRow.Attempt), 70);
                AddColumn(grid, "Length", nameof(LineHistoryRow.Length));

                var rows = new List<LineHistoryRow>();
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<LineOperation>())
                {
                    any = true;
                    var r = new LineHistoryRow
                    {
                        Attempt = (attempt++).ToString(),
                        Length = FmtLength(op.LineLength, scale)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt, r.Length });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Length" };
            return WrapTab("Lines", panel, headers, csvRows, "line_history.csv");
        }

        private TabItem BuildOutlineTab(UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();
            var attemptHeader = new AttemptHeader();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(OutlineHistoryRow.Attempt), 70);
                AddColumn(grid, "Aspect ratio", nameof(OutlineHistoryRow.AspectRatio));
                AddColumn(grid, "Perimeter", nameof(OutlineHistoryRow.Perimeter));
                AddColumn(grid, "Area", nameof(OutlineHistoryRow.Area));
                AddColumn(grid, "Perim / Area", nameof(OutlineHistoryRow.PerimeterAreaRatio));
                AddColumn(grid, "Circularity", nameof(OutlineHistoryRow.Circularity));
                AddColumn(grid, "Solidity", nameof(OutlineHistoryRow.Solidity));
                AddColumn(grid, "Turn. Angles / Length", nameof(OutlineHistoryRow.TurningAngleLength));

                var rows = new List<OutlineHistoryRow>();
                int attempt = 1;
                bool any = false;
                // Only finalized outlines with generated metadata (HasMetadata) — matching the
                // n_outline counter. EFD/EFA coefficients are omitted; they export separately
                // from the EFA detail window.
                foreach (var op in ops.OfType<OutlineOperation>().Where(o => o.HasMetadata))
                {
                    any = true;
                    var r = new OutlineHistoryRow
                    {
                        Attempt = (attempt++).ToString(),
                        AspectRatio = Fmt4(op.AspectRatio),
                        Perimeter = FmtLength(op.Perimeter, scale),
                        Area = FmtArea(op.Area, scale),
                        PerimeterAreaRatio = Fmt4(op.PerimeterAreaRatio),
                        Circularity = Fmt4(op.Circularity),
                        Solidity = Fmt4(op.Solidity),
                        TurningAngleLength = Fmt4(op.TurningAngleLength)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt, r.AspectRatio, r.Perimeter, r.Area, r.PerimeterAreaRatio, r.Circularity, r.Solidity, r.TurningAngleLength });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "", "", "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Aspect ratio", "Perimeter", "Area", "Perim / Area", "Circularity", "Solidity", "Sum Turn. Angles", "Turn. Angles / Length" };
            return WrapTab("Outline", panel, headers, csvRows, "outline_history.csv");
        }

        private TabItem BuildCircularArcTab(UndoRedoManager ur, string currentName)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();
            var attemptHeader = new AttemptHeader();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(CircularArcHistoryRow.Attempt), 70);
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
                        Attempt = (attempt++).ToString(),
                        CentralAngle = Fmt(op.CentralAngle),
                        ChordArcRatio = Fmt(op.ChordArcRatio),
                        RiseSpanRatio = Fmt(op.AspectRatio)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt, r.CentralAngle, r.ChordArcRatio, r.RiseSpanRatio });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Central angle", "Chord-arc ratio", "Rise-span ratio" };
            return WrapTab("Circular Arc", panel, headers, csvRows, "circular_arc_history.csv");
        }

        private TabItem BuildParabolicArcTab(UndoRedoManager ur, string currentName)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();
            var attemptHeader = new AttemptHeader();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(ParabolicArcHistoryRow.Attempt), 70);
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
                        Attempt = (attempt++).ToString(),
                        ChordArcRatio = Fmt(op.PChordArcRatio),
                        RiseSpanRatio = Fmt(op.RiseSpanRatio),
                        VertexCurvature = Fmt(op.VertexCurvature)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt, r.ChordArcRatio, r.RiseSpanRatio, r.VertexCurvature });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Chord-arc ratio", "Rise-span ratio", "Vertex curvature" };
            return WrapTab("Parabolic Arc", panel, headers, csvRows, "parabolic_arc_history.csv");
        }

        private TabItem BuildSplineTab(UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();
            var attemptHeader = new AttemptHeader();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(SplineHistoryRow.Attempt), 70);
                AddColumn(grid, "Turn. Angles / Length", nameof(SplineHistoryRow.TurnPerLength));
                AddColumn(grid, "Chord-arc ratio", nameof(SplineHistoryRow.ChordArcRatio));
                AddColumn(grid, "Length", nameof(SplineHistoryRow.Length));

                var rows = new List<SplineHistoryRow>();
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<SplineOperation>())
                {
                    any = true;
                    var r = new SplineHistoryRow
                    {
                        Attempt = (attempt++).ToString(),
                        TurnPerLength = Fmt(op.TurningAngleArcRatio),
                        ChordArcRatio = Fmt(op.SChordArcRatio),
                        Length = FmtLength(op.SplineLengthPixels, scale)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt, r.TurnPerLength, r.ChordArcRatio, r.Length });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Turn. Angles / Length", "Sum Turn. Angles", "Chord-arc ratio", "Length" };
            return WrapTab("n-Point Spline", panel, headers, csvRows, "spline_history.csv");
        }

        private TabItem BuildTriangleTab(UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var panel = new StackPanel();
            var csvRows = new List<string[]>();
            var attemptHeader = new AttemptHeader();

            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                panel.Children.Add(SpecimenHeader(name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(TriangleHistoryRow.Attempt), 70);
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
                        Attempt = (attempt++).ToString(),
                        AngleA = Fmt(op.AngleA),
                        AngleB = Fmt(op.AngleB),
                        AngleC = Fmt(op.AngleC),
                        Area = FmtArea(op.TriArea, scale)
                    };
                    rows.Add(r);
                    csvRows.Add(new[] { name, r.Attempt, r.AngleA, r.AngleB, r.AngleC, r.Area });
                }
                if (!any)
                    csvRows.Add(new[] { name, "", "", "", "", "" });
                grid.ItemsSource = rows;

                panel.Children.Add(grid);
            }

            var headers = new[] { "Specimen", "Attempt", "Angle A", "Angle B", "Angle C", "Area" };
            return WrapTab("Triangle", panel, headers, csvRows, "triangle_history.csv");
        }

        private TabItem WrapTab(string header, StackPanel panel,
            string[] csvHeaders, List<string[]> csvRows, string suggestedFileName)
        {
            var scroll = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var addButton = new Button
            {
                Content = WorkbookButtonLabel(IsInWorkbook(header)),
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4)
            };
            addButton.Click += (s, e) =>
            {
                var existing = _workbook.FirstOrDefault(w => w.Name == header);
                if (existing != null)
                    _workbook.Remove(existing);
                else
                    _workbook.Add(new WorkbookSheet { Name = header, Headers = csvHeaders, Rows = csvRows });

                addButton.Content = WorkbookButtonLabel(existing == null);
                UpdateWorkbookStatus();
            };

            var csvButton = new Button
            {
                Content = "Export to CSV…",
                Padding = new Thickness(12, 4, 12, 4)
            };
            csvButton.Click += (s, e) => ExportCsv(csvHeaders, csvRows, suggestedFileName);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            buttonRow.Children.Add(addButton);
            buttonRow.Children.Add(csvButton);

            var dock = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(buttonRow, Dock.Bottom);
            dock.Children.Add(buttonRow);
            dock.Children.Add(scroll);
            return new TabItem { Header = header, Content = dock };
        }

        private bool IsInWorkbook(string sheetName) => _workbook.Any(w => w.Name == sheetName);

        private static string WorkbookButtonLabel(bool added) =>
            added ? "\u2713 Added to workbook" : "Add to workbook";

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
                IsReadOnly = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Auto),
                Margin = new Thickness(8, 0, 8, 12)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
            return grid;
        }

        private static void AddColumn(DataGrid grid, string header, string path, double? fixedWidth = null)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                IsReadOnly = true,
                Width = fixedWidth.HasValue
                    ? new DataGridLength(fixedWidth.Value)
                    : new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        private static TextBox MakeAttemptHeaderBox(AttemptHeader model)
        {
            var box = new TextBox
            {
                MinWidth = 54,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontWeight = FontWeights.Bold,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Edit this column title (affects this window and its exports only)",
                DataContext = model
            };
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(AttemptHeader.Text))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            return box;
        }

        private static void AddAttemptColumn(DataGrid grid, TextBox headerBox, string path, double fixedWidth)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = headerBox,
                Binding = new Binding(path)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                },
                IsReadOnly = false,
                Width = new DataGridLength(fixedWidth)
            });
        }

        private FrameworkElement BuildWorkbookFooter()
        {
            _workbookStatus = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            _exportWorkbookButton = new Button
            {
                Content = "Export workbook…",
                Padding = new Thickness(12, 4, 12, 4)
            };
            _exportWorkbookButton.Click += (s, e) => ExportWorkbook();

            var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            DockPanel.SetDock(_exportWorkbookButton, Dock.Right);
            bar.Children.Add(_exportWorkbookButton);
            bar.Children.Add(_workbookStatus);

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = bar
            };
        }

        private void UpdateWorkbookStatus()
        {
            int n = _workbook.Count;
            _workbookStatus.Text = n == 0
                ? "No tables added to workbook"
                : n == 1 ? "1 table added to workbook"
                         : $"{n} tables added to workbook";
            _exportWorkbookButton.IsEnabled = n > 0;
        }

        private static void ExportCsv(string[] headers, List<string[]> rows, string suggestedFileName)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export Table to CSV",
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

        private static string CsvEscape(string field)
        {
            field ??= "";
            if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        private void ExportWorkbook()
        {
            if (_workbook.Count == 0) return;

            var dlg = new SaveFileDialog
            {
                Title = "Export workbook",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                DefaultExt = ".xlsx",
                FileName = "history_workbook.xlsx",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteXlsx(dlg.FileName, _workbook);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the workbook:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void ExportAllOperationHistory(
            UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export operation history",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                DefaultExt = ".xlsx",
                FileName = "operation_history.xlsx",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteXlsx(dlg.FileName, BuildAllSheets(ur, currentName, scale));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the workbook:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static List<WorkbookSheet> BuildAllSheets(
            UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var sheets = new List<WorkbookSheet>();

            var (h1, r1) = BuildCircularArcData(ur, currentName);
            sheets.Add(new WorkbookSheet { Name = "Circular Arc", Headers = h1, Rows = r1 });

            var (h2, r2) = BuildParabolicArcData(ur, currentName);
            sheets.Add(new WorkbookSheet { Name = "Parabolic Arc", Headers = h2, Rows = r2 });

            var (h3, r3) = BuildSplineData(ur, currentName, scale);
            sheets.Add(new WorkbookSheet { Name = "n-Point Spline", Headers = h3, Rows = r3 });

            var (h4, r4) = BuildTriangleData(ur, currentName, scale);
            sheets.Add(new WorkbookSheet { Name = "Triangle", Headers = h4, Rows = r4 });

            var (h5, r5) = BuildLineData(ur, currentName, scale);           
            sheets.Add(new WorkbookSheet { Name = "Lines", Headers = h5, Rows = r5 });

            var (h6, r6) = BuildOutlineData(ur, currentName, scale);
            sheets.Add(new WorkbookSheet { Name = "Outline", Headers = h6, Rows = r6 });

            return sheets;
        }

        private static (string[] Headers, List<string[]> Rows) BuildCircularArcData(
            UndoRedoManager ur, string currentName)
        {
            var headers = new[] { "Specimen", "Attempt", "Central angle", "Chord-arc ratio", "Rise-span ratio" };
            var rows = new List<string[]>();
            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<CircularArcOperation>())
                {
                    any = true;
                    rows.Add(new[] { name, (attempt++).ToString(), Fmt(op.CentralAngle), Fmt(op.ChordArcRatio), Fmt(op.AspectRatio) });
                }
                if (!any) rows.Add(new[] { name, "", "", "", "" });
            }
            return (headers, rows);
        }

        private static (string[] Headers, List<string[]> Rows) BuildParabolicArcData(
            UndoRedoManager ur, string currentName)
        {
            var headers = new[] { "Specimen", "Attempt", "Chord-arc ratio", "Rise-span ratio", "Vertex curvature" };
            var rows = new List<string[]>();
            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<ParabolaOperation>())
                {
                    any = true;
                    rows.Add(new[] { name, (attempt++).ToString(), Fmt(op.PChordArcRatio), Fmt(op.RiseSpanRatio), Fmt(op.VertexCurvature) });
                }
                if (!any) rows.Add(new[] { name, "", "", "", "" });
            }
            return (headers, rows);
        }

        private static (string[] Headers, List<string[]> Rows) BuildSplineData(
            UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var headers = new[] { "Specimen", "Attempt", "Turn. Angles / Length", "Sum Turn. Angles", "Chord-arc ratio", "Length" };
            var rows = new List<string[]>();
            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<SplineOperation>())
                {
                    any = true;
                    rows.Add(new[] { name, (attempt++).ToString(), Fmt(op.TurningAngleArcRatio), Fmt(op.SChordArcRatio), FmtLength(op.SplineLengthPixels, scale) });
                }
                if (!any) rows.Add(new[] { name, "", "", "", "", "" });
            }
            return (headers, rows);
        }

        private static (string[] Headers, List<string[]> Rows) BuildTriangleData(
            UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var headers = new[] { "Specimen", "Attempt", "Angle A", "Angle B", "Angle C", "Area" };
            var rows = new List<string[]>();
            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<GetAngleOperation>())
                {
                    any = true;
                    rows.Add(new[] { name, (attempt++).ToString(), Fmt(op.AngleA), Fmt(op.AngleB), Fmt(op.AngleC), FmtArea(op.TriArea, scale) });
                }
                if (!any) rows.Add(new[] { name, "", "", "", "", "" });
            }
            return (headers, rows);
        }

        private static (string[] Headers, List<string[]> Rows) BuildLineData(
    UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var headers = new[] { "Specimen", "Attempt", "Length", "Line ratio" };
            var rows = new List<string[]>();
            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<LineOperation>())
                {
                    any = true;
                    rows.Add(new[] { name, (attempt++).ToString(), FmtLength(op.LineLength, scale), FmtRatio(op.LineLengthRatio) });
                }
                if (!any) rows.Add(new[] { name, "", "", "" });
            }
            return (headers, rows);
        }

        private static (string[] Headers, List<string[]> Rows) BuildOutlineData(
    UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var headers = new[] { "Specimen", "Attempt", "Aspect ratio", "Perimeter", "Area", "Perim / Area", "Circularity", "Solidity", "Sum Turn. Angles", "Turn. Angles / Length" };
            var rows = new List<string[]>();
            foreach (var (name, ops) in Blocks(ur, currentName))
            {
                int attempt = 1;
                bool any = false;
                foreach (var op in ops.OfType<OutlineOperation>().Where(o => o.HasMetadata))
                {
                    any = true;
                    rows.Add(new[]
                    {
                name, (attempt++).ToString(),
                Fmt4(op.AspectRatio),
                FmtLength(op.Perimeter, scale),
                FmtArea(op.Area, scale),
                Fmt4(op.PerimeterAreaRatio),
                Fmt4(op.Circularity),
                Fmt4(op.Solidity),
                Fmt4(op.TurningAngleLength)
            });
                }
                if (!any) rows.Add(new[] { name, "", "", "", "", "", "", "", "", "" });
            }
            return (headers, rows);
        }

        private static void WriteXlsx(string path, List<WorkbookSheet> sheets)
        {
            const string nsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            const string ctWorkbook = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
            const string ctWorksheet = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
            const string ctStyles = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
            const string relOfficeDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
            const string relWorksheet = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
            const string relStyles = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

            using var pkg = Package.Open(path, FileMode.Create);

            var wbUri = new Uri("/xl/workbook.xml", UriKind.Relative);
            var wbPart = pkg.CreatePart(wbUri, ctWorkbook);
            pkg.CreateRelationship(wbUri, TargetMode.Internal, relOfficeDoc, "rId1");

            var stylesUri = new Uri("/xl/styles.xml", UriKind.Relative);
            var stylesPart = pkg.CreatePart(stylesUri, ctStyles);
            WritePartText(stylesPart, StylesXml());
            wbPart.CreateRelationship(stylesUri, TargetMode.Internal, relStyles, "rIdStyles");

            for (int i = 1; i <= sheets.Count; i++)
            {
                var sheetUri = new Uri($"/xl/worksheets/sheet{i}.xml", UriKind.Relative);
                var sheetPart = pkg.CreatePart(sheetUri, ctWorksheet);
                WritePartText(sheetPart, BuildSheetXml(sheets[i - 1]));
                wbPart.CreateRelationship(sheetUri, TargetMode.Internal, relWorksheet, $"rId{i}");
            }

            var wb = new StringBuilder();
            wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            wb.Append($"<workbook xmlns=\"{nsMain}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (int i = 1; i <= sheets.Count; i++)
                wb.Append($"<sheet name=\"{XmlEscape(SafeSheetName(sheets[i - 1].Name, i))}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
            wb.Append("</sheets></workbook>");
            WritePartText(wbPart, wb.ToString());
        }

        private static void WritePartText(PackagePart part, string content)
        {
            using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        private static string StylesXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border/></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        private static string BuildSheetXml(WorkbookSheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

            sb.Append("<row r=\"1\">");
            for (int c = 0; c < sheet.Headers.Length; c++)
                sb.Append(InlineStringCell($"{ColumnLetter(c)}1", sheet.Headers[c]));
            sb.Append("</row>");

            int rowNum = 2;
            foreach (var row in sheet.Rows)
            {
                sb.Append($"<row r=\"{rowNum}\">");
                for (int c = 0; c < row.Length; c++)
                    sb.Append(Cell($"{ColumnLetter(c)}{rowNum}", row[c]));
                sb.Append("</row>");
                rowNum++;
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static string Cell(string reference, string value)
        {
            if (!string.IsNullOrEmpty(value) &&
                double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return $"<c r=\"{reference}\"><v>{value}</v></c>";
            return InlineStringCell(reference, value);
        }

        private static string InlineStringCell(string reference, string value) =>
            $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c>";

        private static string ColumnLetter(int index)
        {
            string s = "";
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                s = (char)('A' + rem) + s;
                index = (index - 1) / 26;
            }
            return s;
        }

        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }

        private static string SafeSheetName(string name, int ordinal)
        {
            if (string.IsNullOrWhiteSpace(name)) name = $"Sheet{ordinal}";
            foreach (char bad in new[] { ':', '\\', '/', '?', '*', '[', ']' })
                name = name.Replace(bad, ' ');
            name = name.Trim();
            if (name.Length > 31) name = name.Substring(0, 31);
            if (name.Length == 0) name = $"Sheet{ordinal}";
            return name;
        }

        private static string Fmt(double v) =>
            Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        private static string Fmt4(double v) =>
    Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);

        // The line-length ratio is boxed as either a rounded double or the string "N/A"
        // (see GeometryCalculations.RelativeLength), so format each case accordingly.
        private static string FmtRatio(object ratio) =>
            ratio is double d ? Fmt(d) : ratio?.ToString() ?? "";

        private static string FmtLength(double pixels, ScaleCalibration scale) =>
            scale != null && scale.IsCalibrated
                ? $"{scale.ToUnits(pixels):F2} {scale.Unit}"
                : $"{Math.Round(pixels, 1).ToString(CultureInfo.InvariantCulture)} px";

        private static string FmtArea(double pixelArea, ScaleCalibration scale) =>
            scale != null && scale.IsCalibrated
                ? $"{scale.ToUnitsArea(pixelArea):F2} {scale.Unit}\u00B2"
                : $"{Math.Round(pixelArea, 1).ToString(CultureInfo.InvariantCulture)} px\u00B2";
    }

    public class CircularArcHistoryRow
    {
        public string Attempt { get; set; }
        public string CentralAngle { get; set; }
        public string ChordArcRatio { get; set; }
        public string RiseSpanRatio { get; set; }
    }

    public class ParabolicArcHistoryRow
    {
        public string Attempt { get; set; }
        public string ChordArcRatio { get; set; }
        public string RiseSpanRatio { get; set; }
        public string VertexCurvature { get; set; }
    }

    public class SplineHistoryRow
    {
        public string Attempt { get; set; }
        public string TurnPerLength { get; set; }
        public string ChordArcRatio { get; set; }
        public string Length { get; set; }
    }

    public class TriangleHistoryRow
    {
        public string Attempt { get; set; }
        public string AngleA { get; set; }
        public string AngleB { get; set; }
        public string AngleC { get; set; }
        public string Area { get; set; }
    }

    public class LineHistoryRow
    {
        public string Attempt { get; set; }
        public string Length { get; set; }
    }

    public class OutlineHistoryRow
    {
        public string Attempt { get; set; }
        public string AspectRatio { get; set; }
        public string Perimeter { get; set; }
        public string Area { get; set; }
        public string PerimeterAreaRatio { get; set; }
        public string Circularity { get; set; }
        public string Solidity { get; set; }
        public string TurningAngleLength { get; set; }
    }
}