using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using ShapeConstraint = DinoLino.Utilities.Modes.DrawMode.ShapeConstraint;

namespace DinoLino.Utilities
{
    /// <summary>The Batch Workshop rows, each of which has one wide table.</summary>
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
    /// restored from the edit window at any time.
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

        public static void Restore(string table) => _hidden.Remove(table);

        public static int HiddenCount(string table)
        {
            HashSet<string> set;
            return _hidden.TryGetValue(table, out set) ? set.Count : 0;
        }
    }

    // =====================
    // Table description
    // =====================

    /// <summary>One measurement column and how to read it from an operation.</summary>
    public sealed class WorkshopColumn
    {
        public string Header;
        public Func<WorkOperation, string> Value;
    }

    /// All columns contributed by one operation kind. Each kind fills its own columns
    /// and leaves the others blank on the same row.
    public sealed class WorkshopColumnGroup
    {
        public Type OperationType;

        // Extra condition beyond the type, e.g. outlines that have metadata, or
        // shapes drawn with one particular constraint.
        public Func<WorkOperation, bool> Filter;

        public List<WorkshopColumn> Columns = new List<WorkshopColumn>();

        public bool Accepts(WorkOperation op) =>
            op != null
            && OperationType.IsInstanceOfType(op)
            && (Filter == null || Filter(op));
    }

    // =====================
    // Built table
    // =====================

    /// <summary>One row of the wide table: an attempt index across all operation kinds.</summary>
    public sealed class WorkshopRow
    {
        public int Attempt;                    // 1-based; 0 means a placeholder row
        public string[] Cells;                 // aligned with WorkshopTable.MeasurementHeaders

        /// The operations that supplied this row's values, so the edit window can
        /// delete exactly what the row shows.
        public List<WorkOperation> Operations = new List<WorkOperation>();
    }

    /// <summary>One specimen's block of rows.</summary>
    public sealed class WorkshopBlock
    {
        public string Name;
        public SpecimenRecord Record;          // null for the active specimen
        public bool IsActive;
        public IReadOnlyList<WorkOperation> AllOperations;
        public List<WorkshopRow> Rows = new List<WorkshopRow>();
    }

    /// <summary>A set of column groups as one wide table.</summary>
    public sealed class WorkshopTable
    {
        /// Key under which columns are hidden. Null or empty means the table is not
        /// filtered and always shows every column.
        public string Key;

        public string Title;
        public bool AllowColumnHiding = true;

        public string[] MeasurementHeaders;    // excludes Specimen and Attempt
        public List<WorkshopBlock> Blocks = new List<WorkshopBlock>();

        /// <summary>Headers actually shown, with hidden columns dropped.</summary>
        public List<int> VisibleColumnIndexes()
        {
            var keep = new List<int>(MeasurementHeaders.Length);
            bool filtered = !string.IsNullOrEmpty(Key);

            for (int i = 0; i < MeasurementHeaders.Length; i++)
            {
                if (!filtered || !WorkshopColumnFilter.IsHidden(Key, MeasurementHeaders[i]))
                    keep.Add(i);
            }
            return keep;
        }

        /// Flattens to CSV form: Specimen, Attempt, then the visible measurement
        /// columns in order.
        public (string[] Headers, List<string[]> Rows) ToCsv()
        {
            var keep = VisibleColumnIndexes();

            var headers = new string[keep.Count + 2];
            headers[0] = "Specimen";
            headers[1] = "Attempt";
            for (int i = 0; i < keep.Count; i++)
                headers[i + 2] = MeasurementHeaders[keep[i]];

            var rows = new List<string[]>();
            foreach (var block in Blocks)
            {
                foreach (var row in block.Rows)
                {
                    var cells = new string[keep.Count + 2];
                    cells[0] = block.Name;
                    cells[1] = row.Attempt > 0 ? row.Attempt.ToString() : "";
                    for (int i = 0; i < keep.Count; i++)
                        cells[i + 2] = row.Cells[keep[i]] ?? "";
                    rows.Add(cells);
                }
            }

            return (headers, rows);
        }

        /// <summary>True when no specimen contributed an actual measurement.</summary>
        public bool IsEmpty =>
            Blocks.All(b => b.Rows.All(r => r.Operations.Count == 0));
    }

    // =====================
    // Builder
    // =====================

    /// <summary>
    /// Builds wide tables from column groups. Operations of different kinds are
    /// joined by attempt number rather than stacked in separate sections, so attempt
    /// 1 of every kind shares a row and kinds with fewer attempts leave blank cells.
    /// The column definitions here are the single source for the Batch Workshop
    /// tables, the History window's tabs, and every export built from either.
    /// </summary>
    public static class WorkshopTables
    {
        /// <summary>One Batch Workshop category as a table.</summary>
        public static WorkshopTable Build(
            WorkshopCategory category, UndoRedoManager undoRedo, string currentName, ScaleCalibration scale)
        {
            var table = BuildFromGroups(
                KeyFor(category), ColumnGroups(category, undoRedo, scale), undoRedo, currentName);

            table.Title = TitleFor(category);

            // The silhouette export has no column-based CSV, so hiding a column there
            // would not correspond to anything.
            table.AllowColumnHiding = category != WorkshopCategory.Outlines2D;

            return table;
        }

        /// Builds a table from any subset of column groups, so a caller can take one
        /// operation kind out of a category and table it on its own.
        public static WorkshopTable BuildFromGroups(
            string key, IReadOnlyList<WorkshopColumnGroup> groups,
            UndoRedoManager undoRedo, string currentName)
        {
            var headers = new List<string>();

            // Column index where each group's columns start, so a group can write into
            // its own slice of the row and leave the rest blank.
            var offsets = new int[groups.Count];

            for (int g = 0; g < groups.Count; g++)
            {
                offsets[g] = headers.Count;
                foreach (var column in groups[g].Columns)
                    headers.Add(column.Header);
            }

            var table = new WorkshopTable
            {
                Key = key,
                MeasurementHeaders = headers.ToArray()
            };

            if (undoRedo == null) return table;

            foreach (var block in EnumerateBlocks(undoRedo, currentName))
            {
                // Each group's operations for this specimen, in the order they were made.
                var perGroup = new List<List<WorkOperation>>();
                int rowCount = 0;

                foreach (var group in groups)
                {
                    var ops = block.AllOperations.Where(group.Accepts).ToList();
                    perGroup.Add(ops);
                    if (ops.Count > rowCount) rowCount = ops.Count;
                }

                if (rowCount == 0)
                {
                    // Keep the specimen visible with one empty row.
                    block.Rows.Add(new WorkshopRow
                    {
                        Attempt = 0,
                        Cells = new string[table.MeasurementHeaders.Length]
                    });
                    table.Blocks.Add(block);
                    continue;
                }

                for (int i = 0; i < rowCount; i++)
                {
                    var row = new WorkshopRow
                    {
                        Attempt = i + 1,
                        Cells = new string[table.MeasurementHeaders.Length]
                    };

                    for (int g = 0; g < groups.Count; g++)
                    {
                        var ops = perGroup[g];

                        // This kind has fewer attempts than the widest one, so its
                        // cells stay blank on this row.
                        if (i >= ops.Count) continue;

                        var op = ops[i];
                        row.Operations.Add(op);

                        var columns = groups[g].Columns;
                        for (int c = 0; c < columns.Count; c++)
                            row.Cells[offsets[g] + c] = Safe(columns[c].Value, op);
                    }

                    block.Rows.Add(row);
                }

                table.Blocks.Add(block);
            }

            return table;
        }

        // A malformed operation should blank one cell rather than break the table.
        private static string Safe(Func<WorkOperation, string> getter, WorkOperation op)
        {
            try
            {
                return getter(op) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static IEnumerable<WorkshopBlock> EnumerateBlocks(UndoRedoManager undoRedo, string currentName)
        {
            foreach (var record in undoRedo.Archive)
            {
                yield return new WorkshopBlock
                {
                    Name = record.SpecimenName,
                    Record = record,
                    IsActive = false,
                    AllOperations = record.Operations
                };
            }

            yield return new WorkshopBlock
            {
                Name = currentName,
                Record = null,
                IsActive = true,
                AllOperations = undoRedo.History
            };
        }

        public static string KeyFor(WorkshopCategory category)
        {
            switch (category)
            {
                case WorkshopCategory.Curvature: return "Curvature";
                case WorkshopCategory.Angle: return "Angle";
                case WorkshopCategory.Shape: return "Shape";
                case WorkshopCategory.OutlineMetadata: return "Outline";
                case WorkshopCategory.Efa: return "EFA";
                default: return "Outlines2D";
            }
        }

        public static string TitleFor(WorkshopCategory category)
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

        public static string FileNameFor(WorkshopCategory category)
        {
            switch (category)
            {
                case WorkshopCategory.Curvature: return "curvature_data.csv";
                case WorkshopCategory.Angle: return "angle_data.csv";
                case WorkshopCategory.Shape: return "shape_data.csv";
                case WorkshopCategory.OutlineMetadata: return "outline_metadata.csv";
                case WorkshopCategory.Efa: return "efa_data.csv";
                default: return "outlines.csv";
            }
        }

        // =====================
        // Column definitions
        // =====================

        // Every header is lowercase, so nothing downstream has to remember which
        // variable was capitalised. Each is prefixed by the operation kind that
        // produced it, which is what keeps one wide table readable: circ_, para_ and
        // spline_ for curvature; tri_ for triangles; rect_, sqr_, ellipse_ and circ_
        // for the four drawn shapes; line_ for lines; outline_ and efa_ for outlines.
        // Note circ_ means the circular arc in the Curvature table and the drawn
        // circle in the Shape table — separate tables, so the names never meet.

        /// One category's column groups, in table order. Callers can take a subset to
        /// table a single operation kind on its own.
        public static List<WorkshopColumnGroup> ColumnGroups(
            WorkshopCategory category, UndoRedoManager undoRedo, ScaleCalibration scale)
        {
            switch (category)
            {
                case WorkshopCategory.Curvature:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(CircularArcOperation),
                            Columns = new List<WorkshopColumn>
                            {
                                Col("circ_centangle", o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).CentralAngle)),
                                Col("circ_chordarc", o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).ChordArcRatio)),
                                Col("circ_risespan", o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).AspectRatio))
                            }
                        },
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(ParabolaOperation),
                            Columns = new List<WorkshopColumn>
                            {
                                Col("para_chordarc", o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).PChordArcRatio)),
                                Col("para_risespan", o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).RiseSpanRatio)),
                                Col("para_vertcurv", o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).VertexCurvature))
                            }
                        },
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(SplineOperation),
                            Columns = new List<WorkshopColumn>
                            {
                                Col("spline_turnangle", o => GeomOpHistoryWindow.Fmt(((SplineOperation)o).TurningAngleArcRatio)),
                                Col("spline_chordarc", o => GeomOpHistoryWindow.Fmt(((SplineOperation)o).SChordArcRatio)),
                                Col("spline_length", o => GeomOpHistoryWindow.FmtLength(((SplineOperation)o).SplineLengthPixels, scale))
                            }
                        }
                    };

                case WorkshopCategory.Angle:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(GetAngleOperation),
                            Columns = new List<WorkshopColumn>
                            {
                                Col("tri_anglea", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleA)),
                                Col("tri_angleb", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleB)),
                                Col("tri_anglec", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleC)),
                                Col("tri_aspect", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).TriAspectRatio)),
                                Col("tri_area", o => GeomOpHistoryWindow.FmtArea(((GetAngleOperation)o).TriArea, scale))
                            }
                        }
                    };

                case WorkshopCategory.Shape:
                    // One group per shape kind. Because the builder walks each group's
                    // operations independently, attempt n means the nth rectangle, the
                    // nth ellipse, and so on, rather than the nth shape of any kind.
                    return new List<WorkshopColumnGroup>
                    {
                        ShapeGroup(ShapeConstraint.Rectangle, "rect", hasAspect: true, scale: scale),
                        ShapeGroup(ShapeConstraint.Square, "sqr", hasAspect: false, scale: scale),
                        ShapeGroup(ShapeConstraint.Ellipse, "ellipse", hasAspect: true, scale: scale),
                        ShapeGroup(ShapeConstraint.Circle, "circ", hasAspect: false, scale: scale),

                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(LineOperation),
                            Columns = new List<WorkshopColumn>
                            {
                                Col("line_length", o => GeomOpHistoryWindow.FmtLength(((LineOperation)o).LineLength, scale)),
                                Col("line_ratio", o => GeomOpHistoryWindow.FmtRatio(((LineOperation)o).LineLengthRatio)),
                                Col("line_angle", o => GeomOpHistoryWindow.FmtRatio(((LineOperation)o).LineAngle))
                            }
                        }
                    };

                case WorkshopCategory.OutlineMetadata:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(OutlineOperation),
                            Filter = o => ((OutlineOperation)o).HasMetadata,
                            Columns = new List<WorkshopColumn>
                            {
                                Col("outline_aspect", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).AspectRatio)),
                                Col("outline_perim", o => GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).Perimeter, scale)),
                                Col("outline_area", o => GeomOpHistoryWindow.FmtArea(((OutlineOperation)o).Area, scale)),
                                Col("outline_perimarea", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).PerimeterAreaRatio)),
                                Col("outline_circ", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).Circularity)),
                                Col("outline_solidity", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).Solidity)),
                                Col("outline_sumturn", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).SumTurningAngles)),
                                Col("outline_turnlength", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).TurningAngleLength))
                            }
                        }
                    };

                case WorkshopCategory.Efa:
                    return new List<WorkshopColumnGroup> { EfaGroup(undoRedo) };

                default:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(OutlineOperation),
                            Columns = new List<WorkshopColumn>
                            {
                                Col("outline_vertices", o => VertexCount((OutlineOperation)o).ToString()),
                                Col("outline_hasmeta", o => ((OutlineOperation)o).HasMetadata ? "yes" : "no"),
                                Col("outline_perim", o => ((OutlineOperation)o).HasMetadata
                                    ? GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).Perimeter, scale) : ""),
                                Col("outline_area", o => ((OutlineOperation)o).HasMetadata
                                    ? GeomOpHistoryWindow.FmtArea(((OutlineOperation)o).Area, scale) : "")
                            }
                        }
                    };
            }
        }

        /// One drawn shape kind as its own set of columns.
        /// A square and a circle are equilateral by construction, so their aspect
        /// ratio is always 1 and no column is emitted for it.
        private static WorkshopColumnGroup ShapeGroup(
            ShapeConstraint kind, string prefix, bool hasAspect, ScaleCalibration scale)
        {
            var group = new WorkshopColumnGroup
            {
                OperationType = typeof(ShapeOperation),
                Filter = o => ((ShapeOperation)o).ShapeKind == kind
            };

            if (hasAspect)
                group.Columns.Add(Col(prefix + "_aspect",
                    o => GeomOpHistoryWindow.Fmt(((ShapeOperation)o).DrawAspectRatio)));

            group.Columns.Add(Col(prefix + "_area",
                o => GeomOpHistoryWindow.FmtArea(((ShapeOperation)o).ShapeArea, scale)));

            return group;
        }

        /// EFA columns are one set of four per harmonic, so their count depends on the
        /// deepest analysis in the session.
        private static WorkshopColumnGroup EfaGroup(UndoRedoManager undoRedo)
        {
            int maxHarmonics = 0;

            if (undoRedo != null)
            {
                foreach (var record in undoRedo.Archive)
                    maxHarmonics = Math.Max(maxHarmonics, MaxHarmonicsIn(record.Operations));
                maxHarmonics = Math.Max(maxHarmonics, MaxHarmonicsIn(undoRedo.History));
            }

            var group = new WorkshopColumnGroup
            {
                OperationType = typeof(OutlineOperation),
                Filter = o => HarmonicsOf((OutlineOperation)o) > 0
            };

            group.Columns.Add(Col("efa_harmonics", o => HarmonicsOf((OutlineOperation)o).ToString()));

            for (int h = 1; h <= maxHarmonics; h++)
            {
                int harmonic = h;   // captured per iteration
                group.Columns.Add(Col($"efa_a{harmonic}", o => Coefficient(o, harmonic, 0)));
                group.Columns.Add(Col($"efa_b{harmonic}", o => Coefficient(o, harmonic, 1)));
                group.Columns.Add(Col($"efa_c{harmonic}", o => Coefficient(o, harmonic, 2)));
                group.Columns.Add(Col($"efa_d{harmonic}", o => Coefficient(o, harmonic, 3)));
            }

            return group;
        }

        private static int MaxHarmonicsIn(IEnumerable<WorkOperation> ops)
        {
            int max = 0;
            foreach (var op in ops.OfType<OutlineOperation>())
                max = Math.Max(max, HarmonicsOf(op));
            return max;
        }

        private static int HarmonicsOf(OutlineOperation op) =>
            op.EFDCoefficients == null ? 0 : op.EFDCoefficients.Length / 4;

        // An outline with fewer harmonics than the widest one leaves the extra
        // coefficient cells blank.
        private static string Coefficient(WorkOperation op, int harmonic, int component)
        {
            var outline = (OutlineOperation)op;
            int index = (harmonic - 1) * 4 + component;

            if (outline.EFDCoefficients == null || index >= outline.EFDCoefficients.Length)
                return "";

            return GeomOpHistoryWindow.Fmt4(outline.EFDCoefficients[index]);
        }

        private static int VertexCount(OutlineOperation op)
        {
            var polyline = op.Elements?.OfType<System.Windows.Shapes.Polyline>().FirstOrDefault();
            return polyline == null ? 0 : polyline.Points.Count;
        }

        private static WorkshopColumn Col(string header, Func<WorkOperation, string> value) =>
            new WorkshopColumn { Header = header, Value = value };
    }
}