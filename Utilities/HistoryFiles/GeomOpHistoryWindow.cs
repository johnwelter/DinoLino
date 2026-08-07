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
    // Per-session operation viewer: one tab per operation kind, grouped by specimen.
    // Every tab takes its columns from the Batch Workshop table that measures the
    // same kind, so the two views and their exports always carry the same variables.
    // Specimen group columns appear in every grid, CSV, and workbook sheet.
    public class GeomOpHistoryWindow : Window
    {
        #region Fields and tab definitions

        // Sheet names staged for the workbook. Static so the selection outlives the
        // window and the Batch Workshop's All Geometric Data export can reuse it.
        private static readonly HashSet<string> _selectedSheets = new();

        private TextBlock _workbookStatus;
        private Button _exportWorkbookButton;

        // Kept so the workbook is rebuilt from live history at export time rather
        // than from the rows captured when the tabs were drawn.
        private readonly UndoRedoManager _undoRedo;
        private readonly string _currentName;
        private readonly ScaleCalibration _scale;

        // One flattened table staged for export: sheet name, column headers, and
        // rows already formatted as display strings.
        private class WorkbookSheet
        {
            public string Name;
            public string[] Headers;
            public List<string[]> Rows;
        }

        // Backs the editable "Attempt" column header.
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

        // One grid row: the attempt label plus the cells, where the cell array is the
        // specimen's group values followed by the measurement values.
        private class HistoryRow
        {
            public string Attempt { get; set; }
            public string[] Cells { get; set; }
        }

        // One tab: the Batch Workshop table its columns come from, and which of that
        // table's column groups belong to it.
        private sealed class TabSpec
        {
            public string Name;
            public string FileName;
            public WorkshopCategory Category;
            public Func<WorkshopColumnGroup, bool> Pick;
        }

        // Tab order also fixes sheet order in the exported workbooks.
        private static readonly TabSpec[] Tabs =
        {
            new TabSpec
            {
                Name = "Circular Arc",
                FileName = "circular_arc_history.csv",
                Category = WorkshopCategory.Curvature,
                Pick = g => g.OperationType == typeof(CircularArcOperation)
            },
            new TabSpec
            {
                Name = "Parabolic Arc",
                FileName = "parabolic_arc_history.csv",
                Category = WorkshopCategory.Curvature,
                Pick = g => g.OperationType == typeof(ParabolaOperation)
            },
            new TabSpec
            {
                Name = "n-Point Spline",
                FileName = "spline_history.csv",
                Category = WorkshopCategory.Curvature,
                Pick = g => g.OperationType == typeof(SplineOperation)
            },
            new TabSpec
            {
                Name = "Triangle",
                FileName = "triangle_history.csv",
                Category = WorkshopCategory.Angle,
                Pick = g => g.OperationType == typeof(GetAngleOperation)
            },
            new TabSpec
            {
                // The Shape Data table holds one group per shape kind; all four
                // belong to this tab.
                Name = "Shapes",
                FileName = "shape_history.csv",
                Category = WorkshopCategory.Shape,
                Pick = g => g.OperationType == typeof(ShapeOperation)
            },
            new TabSpec
            {
                Name = "Lines",
                FileName = "line_history.csv",
                Category = WorkshopCategory.Shape,
                Pick = g => g.OperationType == typeof(LineOperation)
            },
            new TabSpec
            {
                Name = "Outline",
                FileName = "outline_history.csv",
                Category = WorkshopCategory.OutlineMetadata,
                Pick = g => g.OperationType == typeof(OutlineOperation)
            }
        };

        public GeomOpHistoryWindow(UndoRedoManager undoRedo, string specimenName, ScaleCalibration scale)
        {
            _undoRedo = undoRedo;
            _currentName = specimenName;
            _scale = scale;

            Title = "History of operations";
            Width = 720;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var footer = BuildWorkbookFooter();
            UpdateWorkbookStatus();

            var tabs = new TabControl();
            foreach (var spec in Tabs)
                tabs.Items.Add(BuildTab(spec));

            var root = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(tabs);
            Content = root;
        }

        // Every specimen, oldest first: archived records then the live one.
        private static IEnumerable<(string Name, IReadOnlyList<WorkOperation> Ops)> Blocks(
            UndoRedoManager ur, string currentName)
        {
            foreach (var rec in ur.Archive)
                yield return (rec.SpecimenName, rec.Operations);
            yield return (currentName, ur.History);
        }

        #endregion

        #region Tab building

        // One tab's table. The null filter key means the History view shows every
        // column, whatever has been hidden in a Batch Workshop edit window.
        private static WorkshopTable BuildTable(
            TabSpec spec, UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var groups = WorkshopTables.ColumnGroups(spec.Category, ur, scale)
                .Where(spec.Pick)
                .ToList();

            return WorkshopTables.BuildFromGroups(null, groups, ur, currentName);
        }

        // For each specimen, a header and a grid of that kind's operations. The grid
        // shows the specimen's group columns before the measurement columns, and the
        // tab's CSV comes from the same table, so the file matches what is on screen.
        private TabItem BuildTab(TabSpec spec)
        {
            var table = BuildTable(spec, _undoRedo, _currentName, _scale);
            var groupColumns = SpecimenGroups.Columns;

            var panel = new StackPanel();
            var attemptHeader = new AttemptHeader();

            foreach (var block in table.Blocks)
            {
                panel.Children.Add(SpecimenHeader(block.Name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(HistoryRow.Attempt), 70);

                for (int g = 0; g < groupColumns.Count; g++)
                    AddColumn(grid, groupColumns[g], $"{nameof(HistoryRow.Cells)}[{g}]");

                for (int i = 0; i < table.MeasurementHeaders.Length; i++)
                    AddColumn(grid, table.MeasurementHeaders[i],
                        $"{nameof(HistoryRow.Cells)}[{groupColumns.Count + i}]");

                var groupValues = SpecimenGroups.ValuesFor(block.Name);

                // Attempt 0 marks the placeholder row a specimen with no operations of
                // this kind gets; it belongs in the export but not on screen.
                grid.ItemsSource = block.Rows
                    .Where(r => r.Attempt > 0)
                    .Select(r => new HistoryRow
                    {
                        Attempt = r.Attempt.ToString(),
                        Cells = groupValues.Concat(r.Cells).ToArray()
                    })
                    .ToList();

                panel.Children.Add(grid);
            }

            var (csvHeaders, csvRows) = table.ToCsv();
            return WrapTab(spec.Name, panel, csvHeaders, csvRows, spec.FileName);
        }

        #endregion

        #region Tab chrome and grid helpers

        // Wraps a tab's specimen panel in a scroll viewer + button row (Add to
        // workbook / Export to CSV).
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
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = "Include this table in the exported workbook"
            };
            addButton.Click += (s, e) =>
            {
                // Toggle: stage this tab's sheet, or drop it if already staged.
                if (!_selectedSheets.Remove(header))
                    _selectedSheets.Add(header);

                addButton.Content = WorkbookButtonLabel(IsInWorkbook(header));
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

        private static bool IsInWorkbook(string sheetName) => _selectedSheets.Contains(sheetName);

        private static string WorkbookButtonLabel(bool added) =>
            added ? "\u2713 Added to workbook" : "Add to workbook";

        private static TextBlock SpecimenHeader(string name) => new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 12, 8, 4)
        };

        // Shared grid style: read-only cells (the Attempt column overrides this),
        // no add/delete/sort/reorder, own scrolling off (the tab's ScrollViewer handles it).
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

        // Read-only column bound to one cell of the row array.
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

        // Editable header for the Attempt column: a borderless TextBox two-way bound
        // to the tab's shared AttemptHeader, so typing relabels the column live.
        private static TextBox MakeAttemptHeaderBox(AttemptHeader model)
        {
            var box = new TextBox
            {
                MinWidth = 54,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontWeight = FontWeights.Bold,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Edit this column title (affects this window only)",
                DataContext = model
            };
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(AttemptHeader.Text))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            return box;
        }

        // The Attempt column is user-editable (unlike AddColumn's read-only ones) so
        // attempt numbers can be relabeled; commits on LostFocus to avoid re-rendering
        // mid-edit.
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

        #endregion

        #region Workbook footer and CSV export

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
            int n = _selectedSheets.Count;
            _workbookStatus.Text = n == 0
                ? "No tables added to workbook"
                : n == 1 ? "1 table added to workbook"
                         : $"{n} tables added to workbook";
            _exportWorkbookButton.IsEnabled = n > 0;
        }

        // Writes one tab's already-formatted rows to a CSV file (UTF-8 with BOM so
        // Excel reads non-ASCII units correctly).
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

        // Quotes a field only if it contains a delimiter/quote/newline; embedded
        // quotes are doubled per the CSV convention.
        private static string CsvEscape(string field)
        {
            field ??= "";
            if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        private void ExportWorkbook()
        {
            var sheets = StagedSheets(_undoRedo, _currentName, _scale);
            if (sheets.Count == 0) return;

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
                WriteXlsx(dlg.FileName, sheets);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the workbook:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region Workbook entry points

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

        /// Writes the Batch Workshop's All Geometric Data workbook: the sheets staged
        /// in the History window, or every sheet when none have been staged.
        public static void ExportAllGeometricData(
            UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var sheets = _selectedSheets.Count > 0
                ? StagedSheets(ur, currentName, scale)
                : BuildAllSheets(ur, currentName, scale);

            if (sheets.Count == 0) return;

            var dlg = new SaveFileDialog
            {
                Title = "Export All Geometric Data",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                DefaultExt = ".xlsx",
                FileName = "all_geometric_data.xlsx",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteXlsx(dlg.FileName, sheets);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the workbook:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Staged sheets, rebuilt from live history rather than from the rows captured
        // when the tabs were drawn, so a workbook exported after an edit is current.
        private static List<WorkbookSheet> StagedSheets(
            UndoRedoManager ur, string currentName, ScaleCalibration scale) =>
            BuildAllSheets(ur, currentName, scale)
                .Where(s => _selectedSheets.Contains(s.Name))
                .ToList();

        // One sheet per tab, in tab order.
        private static List<WorkbookSheet> BuildAllSheets(
            UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var sheets = new List<WorkbookSheet>();

            foreach (var spec in Tabs)
            {
                var (headers, rows) = BuildTable(spec, ur, currentName, scale).ToCsv();
                sheets.Add(new WorkbookSheet { Name = spec.Name, Headers = headers, Rows = rows });
            }

            return sheets;
        }

        #endregion

        #region Workshop sidebar exports

        /// Every specimen's operations in order: the archived records followed by the
        /// live history. Exposed so other exporters walk history the same way.
        public static IEnumerable<(string Name, IReadOnlyList<WorkOperation> Ops)> SpecimenBlocks(
            UndoRedoManager ur, string currentName) => Blocks(ur, currentName);

        /// <summary>Writes one Batch Workshop category's wide table to a CSV.</summary>
        public static void ExportWorkshopCsv(
            WorkshopCategory category, UndoRedoManager ur, string currentName, ScaleCalibration scale)
        {
            var table = WorkshopTables.Build(category, ur, currentName, scale);

            if (table.IsEmpty)
            {
                MessageBox.Show(
                    $"No {WorkshopTables.TitleFor(category)} has been recorded yet.",
                    "Export " + WorkshopTables.TitleFor(category),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Hidden columns are already dropped by ToCsv; group columns are included.
            var (headers, rows) = table.ToCsv();
            ExportCsv(headers, rows, WorkshopTables.FileNameFor(category));
        }

        #endregion

        #region Minimal XLSX writer (OOXML, no external dependency)

        // Hand-builds a minimal .xlsx: a workbook part, one worksheet part per sheet,
        // and a bare styles part (Excel requires styles.xml even when unstyled).
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

            // One worksheet part per sheet, related back to the workbook by rId.
            for (int i = 1; i <= sheets.Count; i++)
            {
                var sheetUri = new Uri($"/xl/worksheets/sheet{i}.xml", UriKind.Relative);
                var sheetPart = pkg.CreatePart(sheetUri, ctWorksheet);
                WritePartText(sheetPart, BuildSheetXml(sheets[i - 1]));
                wbPart.CreateRelationship(sheetUri, TargetMode.Internal, relWorksheet, $"rId{i}");
            }

            // workbook.xml: the <sheets> list Excel uses to find and name each tab.
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

        // Smallest styles.xml Excel accepts: one default font/fill/border/format,
        // nothing actually styled.
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

        // One <row> per data row (header row first); the cell reference (A1, B1, …)
        // comes from column position via ColumnLetter.
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

        // Numeric cells (<v>) when the value parses as a number, so Excel treats it
        // as a number (sortable, usable in formulas); otherwise an inline string.
        private static string Cell(string reference, string value)
        {
            if (!string.IsNullOrEmpty(value) &&
                double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return $"<c r=\"{reference}\"><v>{value}</v></c>";
            return InlineStringCell(reference, value);
        }

        private static string InlineStringCell(string reference, string value) =>
            $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c>";

        // 0-based index -> spreadsheet column letters (0->A, 25->Z, 26->AA):
        // bijective base-26, no zero digit.
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

        // Excel sheet-name rules: no : \ / ? * [ ], max 31 chars, non-empty.
        // Falls back to "Sheet{ordinal}" when blank (or blank after stripping).
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

        #endregion

        #region Formatting helpers

        internal static string Fmt(double v) =>
            Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        internal static string Fmt4(double v) =>
            Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);

        // LineLengthRatio is boxed as a double or "N/A"; handle both.
        internal static string FmtRatio(object ratio) =>
            ratio is double d ? Fmt(d) : ratio?.ToString() ?? "";

        // Real-world units when calibrated, else raw canvas pixels so the cell is
        // never blank.
        internal static string FmtLength(double pixels, ScaleCalibration scale) =>
            scale != null && scale.IsCalibrated
                ? $"{scale.ToUnits(pixels):F2} {scale.Unit}"
                : $"{Math.Round(pixels, 1).ToString(CultureInfo.InvariantCulture)} px";

        internal static string FmtArea(double pixelArea, ScaleCalibration scale) =>
            scale != null && scale.IsCalibrated
                ? $"{scale.ToUnitsArea(pixelArea):F2} {scale.Unit}\u00B2"
                : $"{Math.Round(pixelArea, 1).ToString(CultureInfo.InvariantCulture)} px\u00B2";

        #endregion
    }
}