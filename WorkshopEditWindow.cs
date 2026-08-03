using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    /// <summary>The Batch Workshop rows, each of which opens its own edit window.</summary>
    public enum WorkshopCategory
    {
        Curvature,
        Angle,
        Shape,
        OutlineMetadata,
        Efa,
        Outlines2D
    }

    /// <summary>
    /// Remembers which columns the user has hidden, per table, for the session.
    /// Hiding is display-and-export only: no measurement is destroyed, so it can be
    /// undone from the edit window at any time.
    /// </summary>
    public static class WorkshopColumnFilter
    {
        private static readonly Dictionary<string, HashSet<string>> _hidden =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public static bool IsHidden(string table, string column)
        {
            HashSet<string> set;
            return _hidden.TryGetValue(table, out set) && set.Contains(column);
        }

        public static void Hide(string table, string column)
        {
            HashSet<string> set;
            if (!_hidden.TryGetValue(table, out set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _hidden[table] = set;
            }
            set.Add(column);
        }

        public static void Restore(string table)
        {
            _hidden.Remove(table);
        }

        public static int HiddenCount(string table)
        {
            HashSet<string> set;
            return _hidden.TryGetValue(table, out set) ? set.Count : 0;
        }

        /// Drops the hidden columns from an already-built table, keeping headers and
        /// every row aligned. Used by the Batch Workshop CSV exports.
        public static void Apply(string table, ref string[] headers, List<string[]> rows)
        {
            if (headers == null || HiddenCount(table) == 0) return;

            var keep = new List<int>(headers.Length);
            for (int i = 0; i < headers.Length; i++)
            {
                if (!IsHidden(table, headers[i]))
                    keep.Add(i);
            }

            if (keep.Count == headers.Length) return;

            var newHeaders = new string[keep.Count];
            for (int i = 0; i < keep.Count; i++)
                newHeaders[i] = headers[keep[i]];
            headers = newHeaders;

            if (rows == null) return;

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var newRow = new string[keep.Count];
                for (int i = 0; i < keep.Count; i++)
                {
                    int src = keep[i];

                    // Short rows can occur if a builder emits a placeholder; keep the
                    // table rectangular rather than throwing.
                    newRow[i] = src < row.Length ? row[src] : "";
                }
                rows[r] = newRow;
            }
        }
    }

    /// <summary>
    /// Edit window for one Batch Workshop category: shows every measurement of that
    /// kind for every specimen and allows deleting rows, columns, and specimens.
    /// </summary>
    public class WorkshopEditWindow : Window
    {
        // ---- Table description ----

        private sealed class ColumnDef
        {
            public string Header;
            public Func<WorkOperation, string> Value;
        }

        private sealed class TableDef
        {
            public string Title;                       // also the key for hidden columns
            public Func<WorkOperation, bool> Matches;
            public List<ColumnDef> Columns;

            // EFA and 2D Outlines have no fixed CSV column set, so hiding a column
            // there would not mean anything at export time.
            public bool AllowColumnDelete = true;
        }

        // One specimen's operations, together with what is needed to delete it.
        private sealed class Block
        {
            public string Name;
            public SpecimenRecord Record;              // null for the active specimen
            public IReadOnlyList<WorkOperation> Ops;
            public bool IsActive;
        }

        private readonly UndoRedoManager _undoRedo;
        private readonly string _currentName;
        private readonly ScaleCalibration _scale;
        private readonly List<TableDef> _tables;
        private readonly StackPanel _body = new StackPanel();
        private readonly List<WorkOperation> _removed = new List<WorkOperation>();

        /// Operations deleted while the window was open, so the caller can drop their
        /// visuals from the workspace.
        public IReadOnlyList<WorkOperation> RemovedOperations => _removed;

        public WorkshopEditWindow(
            WorkshopCategory category, UndoRedoManager undoRedo, string currentName, ScaleCalibration scale)
        {
            _undoRedo = undoRedo;
            _currentName = currentName;
            _scale = scale;
            _tables = BuildTables(category);

            Title = "Edit " + CategoryTitle(category);
            Width = 900;
            Height = 620;
            MinWidth = 520;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };

            var note = new TextBlock
            {
                Text = "Deleting a row or a specimen permanently removes those measurements from the " +
                       "session \u2014 this cannot be undone with Ctrl+Z. Hiding a column only removes it " +
                       "from the table and its export, and can be restored below.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var restore = new Button
            {
                Content = "Restore hidden columns",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            restore.Click += (s, e) =>
            {
                foreach (var table in _tables)
                    WorkshopColumnFilter.Restore(table.Title);
                Rebuild();
            };
            footer.Children.Add(restore);

            var close = new Button
            {
                Content = "Close",
                MinWidth = 84,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true
            };
            close.Click += (s, e) => Close();
            footer.Children.Add(close);

            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            root.Children.Add(new ScrollViewer
            {
                Content = _body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            Content = root;
            Rebuild();
        }

        private static string CategoryTitle(WorkshopCategory category)
        {
            switch (category)
            {
                case WorkshopCategory.Curvature: return "Curvature Data";
                case WorkshopCategory.Angle: return "Angle Data";
                case WorkshopCategory.Shape: return "Shape Data";
                case WorkshopCategory.OutlineMetadata: return "Outline Metadata";
                case WorkshopCategory.Efa: return "EFA Data";
                default: return "2D Outlines";
            }
        }

        // ---- Rebuild ----

        // The tables are small and deletions change grouping, so the whole body is
        // rebuilt after every edit rather than patched in place.
        private void Rebuild()
        {
            _body.Children.Clear();

            bool anyRows = false;

            foreach (var table in _tables)
            {
                var blocks = BlocksWith(table);
                if (blocks.Count == 0) continue;

                anyRows = true;
                _body.Children.Add(new TextBlock
                {
                    Text = table.Title,
                    FontWeight = FontWeights.Bold,
                    FontSize = 15,
                    Margin = new Thickness(0, 10, 0, 6)
                });

                _body.Children.Add(BuildTable(table, blocks));
            }

            if (!anyRows)
            {
                _body.Children.Add(new TextBlock
                {
                    Text = "No measurements of this kind have been recorded yet.",
                    Opacity = 0.6,
                    Margin = new Thickness(0, 10, 0, 0)
                });
            }
        }

        // Specimens that actually hold at least one operation of this table's kind.
        private List<Block> BlocksWith(TableDef table)
        {
            var result = new List<Block>();

            foreach (Block block in AllBlocks())
            {
                if (block.Ops.Any(o => table.Matches(o)))
                    result.Add(block);
            }

            return result;
        }

        private IEnumerable<Block> AllBlocks()
        {
            foreach (var record in _undoRedo.Archive)
            {
                yield return new Block
                {
                    Name = record.SpecimenName,
                    Record = record,
                    Ops = record.Operations,
                    IsActive = false
                };
            }

            yield return new Block
            {
                Name = _currentName,
                Record = null,
                Ops = _undoRedo.History,
                IsActive = true
            };
        }

        // ---- Table rendering ----

        private UIElement BuildTable(TableDef table, List<Block> blocks)
        {
            var visible = table.Columns
                .Where(c => !WorkshopColumnFilter.IsHidden(table.Title, c.Header))
                .ToList();

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };

            // Attempt column, one per visible measurement, then the row-delete button.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < visible.Count; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int row = 0;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddCell(grid, HeaderText("Attempt"), row, 0);

            for (int i = 0; i < visible.Count; i++)
            {
                var column = visible[i];
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(HeaderText(column.Header));

                if (table.AllowColumnDelete)
                {
                    var drop = SmallButton("\u2715", "Hide this column (removes it from the export too)");
                    drop.Click += (s, e) =>
                    {
                        WorkshopColumnFilter.Hide(table.Title, column.Header);
                        Rebuild();
                    };
                    head.Children.Add(drop);
                }

                AddCell(grid, head, row, i + 1);
            }

            row++;

            foreach (var block in blocks)
            {
                // Specimen banner with its own delete button.
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var banner = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 8, 0, 2)
                };
                banner.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(block.Name) ? "(unnamed specimen)" : block.Name,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (block.IsActive)
                {
                    banner.Children.Add(new TextBlock
                    {
                        Text = "  (loaded)",
                        Opacity = 0.6,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                var wipe = new Button
                {
                    Content = "Delete specimen",
                    Margin = new Thickness(10, 0, 0, 0),
                    Padding = new Thickness(8, 1, 8, 1),
                    ToolTip = "Remove every measurement recorded for this specimen"
                };

                var captured = block;
                wipe.Click += (s, e) => DeleteSpecimen(captured);
                banner.Children.Add(wipe);

                AddCell(grid, banner, row, 0, span: visible.Count + 2);
                row++;

                foreach (var op in block.Ops.Where(o => table.Matches(o)).ToList())
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    int attempt = AttemptNumber(block, table, op);
                    AddCell(grid, BodyText(attempt.ToString()), row, 0);

                    for (int i = 0; i < visible.Count; i++)
                        AddCell(grid, BodyText(visible[i].Value(op)), row, i + 1);

                    var kill = SmallButton("\u2715", "Delete this row");
                    var capturedOp = op;
                    kill.Click += (s, e) => DeleteRow(capturedOp);
                    AddCell(grid, kill, row, visible.Count + 1);

                    row++;
                }
            }

            return grid;
        }

        // Attempt numbers restart per specimen and per table kind, matching the
        // exports and the History window.
        private static int AttemptNumber(Block block, TableDef table, WorkOperation op)
        {
            int n = 0;
            foreach (var candidate in block.Ops)
            {
                if (!table.Matches(candidate)) continue;
                n++;
                if (ReferenceEquals(candidate, op)) break;
            }
            return n;
        }

        // ---- Deletion ----

        private void DeleteRow(WorkOperation op)
        {
            var confirm = MessageBox.Show(
                this,
                "Delete this measurement?\n\nIt is removed from the session permanently and cannot be restored with Undo.",
                "Delete row",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            if (_undoRedo.RemoveOperation(op))
                _removed.Add(op);

            Rebuild();
        }

        private void DeleteSpecimen(Block block)
        {
            int count = block.Ops.Count;

            var confirm = MessageBox.Show(
                this,
                $"Delete all {count} measurement(s) recorded for \"{block.Name}\"?\n\n" +
                "This removes every kind of measurement for that specimen, not just the ones shown here, " +
                "and cannot be restored with Undo. The specimen's image and name are kept.",
                "Delete specimen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            // Snapshot first: the lists are emptied by the calls below.
            _removed.AddRange(block.Ops);

            if (block.IsActive)
                _undoRedo.RemoveActiveSpecimenOperations();
            else
                _undoRedo.RemoveArchivedSpecimen(block.Record);

            Rebuild();
        }

        // ---- Small UI helpers ----

        private static void AddCell(Grid grid, UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            if (span > 1) Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }

        private static TextBlock HeaderText(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Center
        };

        private static TextBlock BodyText(string text) => new TextBlock
        {
            Text = text ?? "",
            Margin = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center
        };

        private static Button SmallButton(string glyph, string tip) => new Button
        {
            Content = glyph,
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 10,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center
        };

        // ---- Table definitions ----

        // Column headers match the export headers exactly, so hiding one here hides
        // the matching column in that category's CSV.
        private List<TableDef> BuildTables(WorkshopCategory category)
        {
            var scale = _scale;

            switch (category)
            {
                case WorkshopCategory.Curvature:
                    return new List<TableDef>
                    {
                        new TableDef
                        {
                            Title = "Circular Arc",
                            Matches = o => o is CircularArcOperation,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Central angle", Value = o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).CentralAngle) },
                                new ColumnDef { Header = "Chord-arc ratio", Value = o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).ChordArcRatio) },
                                new ColumnDef { Header = "Rise-span ratio", Value = o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).AspectRatio) }
                            }
                        },
                        new TableDef
                        {
                            Title = "Parabolic Arc",
                            Matches = o => o is ParabolaOperation,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Chord-arc ratio", Value = o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).PChordArcRatio) },
                                new ColumnDef { Header = "Rise-span ratio", Value = o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).RiseSpanRatio) },
                                new ColumnDef { Header = "Vertex curvature", Value = o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).VertexCurvature) }
                            }
                        },
                        new TableDef
                        {
                            Title = "n-Point Spline",
                            Matches = o => o is SplineOperation,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Turn. Angles / Length", Value = o => GeomOpHistoryWindow.Fmt(((SplineOperation)o).TurningAngleArcRatio) },
                                new ColumnDef { Header = "Chord-arc ratio", Value = o => GeomOpHistoryWindow.Fmt(((SplineOperation)o).SChordArcRatio) },
                                new ColumnDef { Header = "Length", Value = o => GeomOpHistoryWindow.FmtLength(((SplineOperation)o).SplineLengthPixels, scale) }
                            }
                        }
                    };

                case WorkshopCategory.Angle:
                    return new List<TableDef>
                    {
                        new TableDef
                        {
                            Title = "Triangle",
                            Matches = o => o is GetAngleOperation,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Angle A", Value = o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleA) },
                                new ColumnDef { Header = "Angle B", Value = o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleB) },
                                new ColumnDef { Header = "Angle C", Value = o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleC) },
                                new ColumnDef { Header = "Area", Value = o => GeomOpHistoryWindow.FmtArea(((GetAngleOperation)o).TriArea, scale) }
                            }
                        }
                    };

                case WorkshopCategory.Shape:
                    return new List<TableDef>
                    {
                        new TableDef
                        {
                            Title = "Shapes",
                            Matches = o => o is ShapeOperation,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Aspect ratio", Value = o => GeomOpHistoryWindow.Fmt(((ShapeOperation)o).DrawAspectRatio) },
                                new ColumnDef { Header = "Area", Value = o => GeomOpHistoryWindow.FmtArea(((ShapeOperation)o).ShapeArea, scale) },
                                new ColumnDef { Header = "Relative area", Value = o => GeomOpHistoryWindow.FmtRatio(((ShapeOperation)o).RelativeArea) }
                            }
                        },
                        new TableDef
                        {
                            Title = "Lines",
                            Matches = o => o is LineOperation,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Length", Value = o => GeomOpHistoryWindow.FmtLength(((LineOperation)o).LineLength, scale) },
                                new ColumnDef { Header = "Line ratio", Value = o => GeomOpHistoryWindow.FmtRatio(((LineOperation)o).LineLengthRatio) }
                            }
                        }
                    };

                case WorkshopCategory.OutlineMetadata:
                    return new List<TableDef>
                    {
                        new TableDef
                        {
                            Title = "Outline",
                            Matches = o => o is OutlineOperation outline && outline.HasMetadata,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Aspect ratio", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).AspectRatio) },
                                new ColumnDef { Header = "Perimeter", Value = o => GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).Perimeter, scale) },
                                new ColumnDef { Header = "Area", Value = o => GeomOpHistoryWindow.FmtArea(((OutlineOperation)o).Area, scale) },
                                new ColumnDef { Header = "Perim / Area", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).PerimeterAreaRatio) },
                                new ColumnDef { Header = "Circularity", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).Circularity) },
                                new ColumnDef { Header = "Solidity", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).Solidity) },
                                new ColumnDef { Header = "Sum Turn. Angles", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).SumTurningAngles) },
                                new ColumnDef { Header = "Turn. Angles / Length", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).TurningAngleLength) }
                            }
                        }
                    };

                case WorkshopCategory.Efa:
                    return new List<TableDef>
                    {
                        new TableDef
                        {
                            // The EFA export writes one column per coefficient, so the
                            // useful edit here is which outlines take part.
                            Title = "EFA",
                            AllowColumnDelete = false,
                            Matches = o => o is OutlineOperation outline
                                           && outline.EFDCoefficients != null
                                           && outline.EFDCoefficients.Length >= 4,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Harmonics", Value = o => (((OutlineOperation)o).EFDCoefficients.Length / 4).ToString() },
                                new ColumnDef { Header = "a1", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).EFDCoefficients[0]) },
                                new ColumnDef { Header = "b1", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).EFDCoefficients[1]) },
                                new ColumnDef { Header = "c1", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).EFDCoefficients[2]) },
                                new ColumnDef { Header = "d1", Value = o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).EFDCoefficients[3]) },
                                new ColumnDef { Header = "Normalization", Value = o => string.IsNullOrEmpty(((OutlineOperation)o).NormalizationWarning) ? "ok" : "unstable" }
                            }
                        }
                    };

                default:
                    return new List<TableDef>
                    {
                        new TableDef
                        {
                            // Silhouette export takes any traced outline, with or
                            // without generated metadata.
                            Title = "Outlines",
                            AllowColumnDelete = false,
                            Matches = o => o is OutlineOperation,
                            Columns = new List<ColumnDef>
                            {
                                new ColumnDef { Header = "Vertices", Value = o => VertexCount((OutlineOperation)o).ToString() },
                                new ColumnDef { Header = "Metadata", Value = o => ((OutlineOperation)o).HasMetadata ? "yes" : "no" },
                                new ColumnDef { Header = "Perimeter", Value = o => ((OutlineOperation)o).HasMetadata ? GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).Perimeter, scale) : "\u2014" },
                                new ColumnDef { Header = "Area", Value = o => ((OutlineOperation)o).HasMetadata ? GeomOpHistoryWindow.FmtArea(((OutlineOperation)o).Area, scale) : "\u2014" }
                            }
                        }
                    };
            }
        }

        private static int VertexCount(OutlineOperation op)
        {
            var polyline = op.Elements?.OfType<System.Windows.Shapes.Polyline>().FirstOrDefault();
            return polyline == null ? 0 : polyline.Points.Count;
        }
    }
}