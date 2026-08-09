using DinoLino.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino
{
    /// <summary>Marker shapes a plotted point can take.</summary>
    public enum PlotPointShape
    {
        None,
        Circle,
        Square,
        Triangle,
        Diamond,
        Cross,
        Plus
    }

    /// <summary>
    /// Everything the Advanced dialog edits. Held by MainWindow for the session,
    /// so the choices survive closing the dialog and switching plot types.
    /// </summary>
    public class PlotOptions
    {
        // Point color and Point shape each hold one of these, or the name of a
        // group column, in which case each level of that column gets its own
        // color or shape.
        public const string AestheticNone = "None";
        public const string AestheticBlack = "Black";
        public const string AestheticCircle = "Circle";

        public const string DefaultPaletteName = "Colorblind friendly";

        /// <summary>Group column the fill is split by, or "None".</summary>
        public string ColourBy { get; set; } = MainWindow.ColourNone;

        /// <summary>Palette the fill and point-color levels cycle through.</summary>
        public string Palette { get; set; } = DefaultPaletteName;

        /// <summary>"None", "Black", or a group column name.</summary>
        public string PointColor { get; set; } = AestheticBlack;

        /// <summary>"None", "Circle", or a group column name.</summary>
        public string PointShape { get; set; } = AestheticCircle;

        /// Draw a 95% confidence ellipse around each point-color group. Only the
        /// PCA scores plot reads this; the other plot types ignore it.
        public bool ConfidenceEllipses { get; set; } = false;

        // Blank means "use the generated text", so a user who clears a box gets
        // the default back rather than an empty title.
        public string Title { get; set; } = "";
        public string XAxisTitle { get; set; } = "";
        public string YAxisTitle { get; set; } = "";

        // ---- Named choices ----

        public static readonly string[] PaletteNames =
        {
            DefaultPaletteName, "Bright", "Muted", "Grayscale"
        };

        /// Shapes handed out, in order, when Point shape is mapped to a column.
        /// Circle leads so a two-level split reads as circle-versus-triangle,
        /// the pairing that stays clearest at small sizes.
        public static readonly PlotPointShape[] ShapeCycle =
        {
            PlotPointShape.Circle,
            PlotPointShape.Triangle,
            PlotPointShape.Square,
            PlotPointShape.Diamond,
            PlotPointShape.Plus,
            PlotPointShape.Cross
        };

        private static readonly Dictionary<string, Color> NamedColors = new()
        {
            ["Blue"] = Color.FromRgb(0x3E, 0x7C, 0xB8),
            ["Orange"] = Color.FromRgb(0xD5, 0x5E, 0x00),
            ["Green"] = Color.FromRgb(0x00, 0x9E, 0x73),
            ["Purple"] = Color.FromRgb(0x88, 0x5E, 0xB0),
            ["Red"] = Color.FromRgb(0xC0, 0x39, 0x2B),
            ["Gray"] = Color.FromRgb(0x88, 0x88, 0x88),
            ["Black"] = Color.FromRgb(0x22, 0x22, 0x22)
        };

        // An unrecognised name falls back rather than throwing, so a stale stored
        // value cannot break a redraw.
        private static Brush BrushFor(string name, byte alpha)
        {
            if (name == null || !NamedColors.TryGetValue(name, out var c))
                c = NamedColors["Blue"];

            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        // ---- Fixed inks ----
        // Fill, outline, and trend colors are no longer user-editable, so they
        // are constants rather than settings.

        private static readonly Brush _defaultFill = BrushFor("Blue", 0xB4);
        private static readonly Brush _outline = BrushFor("Black", 0xFF);
        private static readonly Brush _trend = BrushFor("Black", 0xCC);
        private static readonly Brush _defaultPoint = BrushFor("Black", 0xC8);

        /// <summary>Fill for boxes and bars that are not split by color.</summary>
        public static Brush DefaultFillBrush() => _defaultFill;

        /// <summary>Ink for box outlines and bar borders.</summary>
        public static Brush OutlineBrush() => _outline;

        /// <summary>Ink for trend lines and QQ reference lines.</summary>
        public static Brush TrendBrush() => _trend;

        /// <summary>Point color when not mapped to a column.</summary>
        public static Brush DefaultPointBrush() => _defaultPoint;

        /// <summary>Series colours for the chosen palette, in cycling order.</summary>
        public Brush[] PaletteBrushes()
        {
            // The default is Okabe-Ito derived, chosen to stay distinguishable in
            // common forms of colour blindness.
            string[] names = Palette switch
            {
                "Bright" => new[] { "Blue", "Orange", "Green", "Red", "Purple", "Gray" },
                "Muted" => new[] { "Gray", "Blue", "Green", "Purple", "Orange", "Black" },
                "Grayscale" => new[] { "Black", "Gray" },
                _ => new[] { "Blue", "Orange", "Green", "Purple", "Red", "Gray", "Black" }
            };

            return names.Select(n => BrushFor(n, 0xC8)).ToArray();
        }

        /// Drops any mapping whose group column no longer exists, which keeps a
        /// stale choice from silently falling back to something else.
        public void PruneMissingColumns()
        {
            ColourBy = KeepIfPresent(ColourBy, MainWindow.ColourNone,
                MainWindow.ColourNone, MainWindow.CategorySpecimen);

            PointColor = KeepIfPresent(PointColor, AestheticBlack,
                AestheticNone, AestheticBlack);

            PointShape = KeepIfPresent(PointShape, AestheticCircle,
                AestheticNone, AestheticCircle);
        }

        private static string KeepIfPresent(string value, string fallback, params string[] reserved)
        {
            if (reserved.Contains(value)) return value;

            return SpecimenGroups.Columns.Any(
                c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase))
                ? value
                : fallback;
        }

        public PlotOptions Clone() => (PlotOptions)MemberwiseClone();
    }

    /// <summary>
    /// Which advanced settings apply to one plot type. Anything false is shown
    /// disabled in the dialog rather than hidden, so the full set of options stays
    /// discoverable while staying uninteractable.
    /// </summary>
    public sealed class PlotCapabilities
    {
        /// True when the plot has filled areas that a column can split.
        public bool SupportsFill;

        public bool SupportsPoints;
        public bool SupportsXAxisTitle;
        public bool SupportsYAxisTitle;

        // Explain a disabled row, so "why can't I set this" is answerable from
        // the dialog itself.
        public string FillNote = "";
        public string PointNote = "";

        public static PlotCapabilities For(string plotType)
        {
            switch (plotType)
            {
                case "Boxplot":
                    // The only plot with both: filled boxes, and the individual
                    // measurements overlaid on them.
                    return new PlotCapabilities
                    {
                        SupportsFill = true,
                        SupportsPoints = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true
                    };

                case "Scatter plot":
                    return new PlotCapabilities
                    {
                        SupportsFill = false,
                        SupportsPoints = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        FillNote = "Scatter plots have no filled areas."
                    };

                case "Histogram":
                    return new PlotCapabilities
                    {
                        // Splitting bars by colour changes what their heights
                        // mean, so a histogram takes one fill for every bar.
                        SupportsFill = false,
                        SupportsPoints = false,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        FillNote = "Histograms use a single fill.",
                        PointNote = "Histograms draw bars, not points."
                    };

                case "QQ plot":
                    return new PlotCapabilities
                    {
                        SupportsFill = false,
                        SupportsPoints = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        FillNote = "QQ plots have no filled areas."
                    };

                case "Dot plot":
                    return new PlotCapabilities
                    {
                        SupportsFill = false,
                        SupportsPoints = true,
                        SupportsXAxisTitle = true,

                        // The vertical axis is a stack count with no scale drawn.
                        SupportsYAxisTitle = false,
                        FillNote = "Dot plots have no filled areas."
                    };

                default:
                    return new PlotCapabilities();
            }
        }
    }

    /// <summary>
    /// "Advanced +" dialog: fill, points, and titles for the current plot. Edits
    /// a copy so Cancel leaves the plot untouched, and disables whatever the
    /// chosen plot type does not use.
    /// </summary>
    internal class PlotAdvancedWindow : Window
    {
        private readonly PlotOptions _working;
        private readonly PlotCapabilities _capabilities;

        private ComboBox _colourByBox;
        private ComboBox _paletteBox;
        private ComboBox _pointColorBox;
        private ComboBox _shapeBox;
        private TextBox _titleBox;
        private TextBox _xTitleBox;
        private TextBox _yTitleBox;

        internal PlotAdvancedWindow(PlotOptions current, PlotCapabilities capabilities)
        {
            _working = current.Clone();
            _capabilities = capabilities;

            Title = "Advanced plot options";
            Width = 440;
            Height = 480;
            MinWidth = 380;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(16) };

            var note = new TextBlock
            {
                Text = "Manually set plot parameters.",
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            // Docked before the scroll viewer is added, and built once: the bar
            // has to be a child in its own right for the dock to apply, and the
            // last child added is the one that fills the remaining space.
            var buttonBar = BuildButtonBar();
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            root.Children.Add(new ScrollViewer
            {
                Content = BuildBody(),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            Content = root;
            ApplyCapabilities();
        }

        // ---- Layout ----

        private UIElement BuildBody()
        {
            var panel = new StackPanel();

            panel.Children.Add(SectionHeader("Fill"));

            // Color by offers the same choices the Sample tab's groups define.
            _colourByBox = new ComboBox { ItemsSource = MainWindow.PlotColourChoices() };
            _colourByBox.SelectedItem = Select(_colourByBox, _working.ColourBy, MainWindow.ColourNone);
            _colourByBox.SelectionChanged += (s, e) => UpdateDependentRows();
            panel.Children.Add(Row("Color by", _colourByBox,
                "Split the fill of boxes by specimen or by a group column"));

            _paletteBox = new ComboBox { ItemsSource = PlotOptions.PaletteNames.ToList() };
            _paletteBox.SelectedItem =
                Select(_paletteBox, _working.Palette, PlotOptions.DefaultPaletteName);
            panel.Children.Add(Row("Palette", _paletteBox,
                "Colors the levels cycle through"));

            panel.Children.Add(SectionHeader("Points"));

            _pointColorBox = new ComboBox { ItemsSource = MainWindow.PlotPointColorChoices() };
            _pointColorBox.SelectedItem =
                Select(_pointColorBox, _working.PointColor, PlotOptions.AestheticBlack);
            _pointColorBox.SelectionChanged += (s, e) => UpdateDependentRows();
            panel.Children.Add(Row("Point color", _pointColorBox,
                "Black draws every point the same. A group column gives each of "
                + "its groups its own color; None hides the points."));

            _shapeBox = new ComboBox { ItemsSource = MainWindow.PlotPointShapeChoices() };
            _shapeBox.SelectedItem =
                Select(_shapeBox, _working.PointShape, PlotOptions.AestheticCircle);
            panel.Children.Add(Row("Point shape", _shapeBox,
                "Circle draws every point the same. A group column gives each of "
                + "its groups its own marker; None hides the points."));

            panel.Children.Add(SectionHeader("Titles"));

            _titleBox = new TextBox { Text = _working.Title, MaxLength = 60 };
            panel.Children.Add(Row("Plot title", _titleBox, "Leave blank to use the generated title"));

            _xTitleBox = new TextBox { Text = _working.XAxisTitle, MaxLength = 40 };
            panel.Children.Add(Row("X axis title", _xTitleBox, "Leave blank for no x axis title"));

            _yTitleBox = new TextBox { Text = _working.YAxisTitle, MaxLength = 40 };
            panel.Children.Add(Row("Y axis title", _yTitleBox, "Leave blank for no y axis title"));

            return panel;
        }

        // A stored choice whose group column has since gone falls back rather
        // than leaving the box blank.
        private static object Select(ComboBox box, string value, string fallback)
        {
            var items = (List<string>)box.ItemsSource;
            return items.Contains(value) ? value : fallback;
        }

        private static TextBlock SectionHeader(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 6, 0, 6)
        };

        // Label and control on one line, with the label column wide enough that
        // every control starts at the same x.
        private Grid Row(string label, FrameworkElement control, string tip)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            control.ToolTip = tip;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            // The label dims with its control, so a disabled row reads as one unit.
            control.IsEnabledChanged += (s, e) => text.Opacity = control.IsEnabled ? 1.0 : 0.45;

            return grid;
        }

        // Fixed-size buttons in a horizontal row along the bottom, under
        // everything else, with a rule separating them from the settings.
        private FrameworkElement BuildButtonBar()
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var ok = new Button { Content = "OK", Width = 92, Height = 28, IsDefault = true };
            ok.Click += (s, e) =>
            {
                // DialogResult may only be set while running modally; guard it the
                // same way ScaleWindow does.
                try
                {
                    DialogResult = true;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            };
            buttons.Children.Add(ok);

            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                Width = 92,
                Height = 28,
                Margin = new Thickness(10, 0, 0, 0),
                IsCancel = true
            });

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 12, 0, 0),
                Margin = new Thickness(0, 12, 0, 0),
                Child = buttons
            };
        }

        // ---- Enablement ----

        private void ApplyCapabilities()
        {
            _colourByBox.IsEnabled = _capabilities.SupportsFill;
            _pointColorBox.IsEnabled = _capabilities.SupportsPoints;
            _shapeBox.IsEnabled = _capabilities.SupportsPoints;
            _xTitleBox.IsEnabled = _capabilities.SupportsXAxisTitle;
            _yTitleBox.IsEnabled = _capabilities.SupportsYAxisTitle;

            if (!_capabilities.SupportsFill && !string.IsNullOrEmpty(_capabilities.FillNote))
                _colourByBox.ToolTip = _capabilities.FillNote;

            if (!_capabilities.SupportsPoints && !string.IsNullOrEmpty(_capabilities.PointNote))
            {
                _pointColorBox.ToolTip = _capabilities.PointNote;
                _shapeBox.ToolTip = _capabilities.PointNote;
            }

            UpdateDependentRows();
        }

        // The palette feeds two things now, so it stays live while either the
        // fill or the point color is split by a column.
        private void UpdateDependentRows()
        {
            bool fillSplit = _capabilities.SupportsFill &&
                             (_colourByBox.SelectedItem as string) != MainWindow.ColourNone;

            bool pointSplit = _capabilities.SupportsPoints &&
                              MainWindow.IsGroupAesthetic(_pointColorBox.SelectedItem as string);

            _paletteBox.IsEnabled = fillSplit || pointSplit;
            _paletteBox.ToolTip = _paletteBox.IsEnabled
                ? "Colors the levels cycle through"
                : "Set \"Color by\" or \"Point color\" to a group to use a palette";
        }

        // ---- Commit ----

        /// Writes the edited values back. Disabled rows are skipped, so a setting
        /// the current plot cannot use keeps whatever it had for the plot types
        /// that can.
        internal void CommitTo(PlotOptions target)
        {
            if (_colourByBox.IsEnabled)
                target.ColourBy = _colourByBox.SelectedItem as string ?? MainWindow.ColourNone;

            if (_paletteBox.IsEnabled)
                target.Palette = _paletteBox.SelectedItem as string ?? PlotOptions.DefaultPaletteName;

            if (_pointColorBox.IsEnabled)
                target.PointColor = _pointColorBox.SelectedItem as string ?? PlotOptions.AestheticBlack;

            if (_shapeBox.IsEnabled)
                target.PointShape = _shapeBox.SelectedItem as string ?? PlotOptions.AestheticCircle;

            target.Title = _titleBox.Text?.Trim() ?? "";
            if (_xTitleBox.IsEnabled) target.XAxisTitle = _xTitleBox.Text?.Trim() ?? "";
            if (_yTitleBox.IsEnabled) target.YAxisTitle = _yTitleBox.Text?.Trim() ?? "";
        }
    }

    /// <summary>
    /// One selectable item in the PCA variable list. Most entries stand for a
    /// single measurement column; the EFA entry stands for the whole block of
    /// coefficient columns, which are added and removed together.
    /// </summary>
    public sealed class PcaVariableEntry
    {
        /// <summary>Stable id stored in the dataframe.</summary>
        public string Key;

        /// <summary>Text shown in the list.</summary>
        public string Label;

        /// <summary>Heading this entry is listed under, i.e. the mode it came from.</summary>
        public string Group;

        /// <summary>Measurement headers this entry stands for.</summary>
        public IReadOnlyList<string> Columns = new string[0];

        /// <summary>True for an entry covering several columns at once.</summary>
        public bool IsBundle;

        /// <summary>Optional grey line shown under the row, explaining the entry.</summary>
        public string Note;
    }

    /// <summary>
    /// The set of variables a PCA runs on, in the order they were added. This is
    /// the Plot tab's own selection: it is unrelated to the Batch Workshop's
    /// tables, its hidden-column filter, and its exports.
    /// </summary>
    public class PcaDataFrame
    {
        private readonly List<string> _keys = new List<string>();

        /// <summary>Entry keys, in the order the user added them.</summary>
        public IReadOnlyList<string> Keys => _keys;

        public int Count => _keys.Count;

        public bool Contains(string key) => key != null && _keys.Contains(key);

        /// <summary>Adds an entry. Adding one already present is a no-op.</summary>
        public bool Add(string key)
        {
            if (key == null || _keys.Contains(key)) return false;
            _keys.Add(key);
            return true;
        }

        public bool Remove(string key) => key != null && _keys.Remove(key);

        public void Clear() => _keys.Clear();

        /// Drops staged entries the catalog no longer offers, which happens when
        /// the specimens that supplied a variable are removed from the sample.
        public void PruneMissing(IEnumerable<PcaVariableEntry> catalog)
        {
            var live = new HashSet<string>(catalog.Select(e => e.Key));
            _keys.RemoveAll(k => !live.Contains(k));
        }

        public void CopyFrom(PcaDataFrame other)
        {
            _keys.Clear();
            if (other != null) _keys.AddRange(other._keys);
        }

        public PcaDataFrame Clone()
        {
            var copy = new PcaDataFrame();
            copy._keys.AddRange(_keys);
            return copy;
        }
    }

    /// <summary>
    /// "Advanced +" dialog for the PCA plot type. Lists every variable measured
    /// this session and lets each be added to or removed from the PCA dataframe,
    /// and sets the color and marker the scores plot draws its points with. Edits
    /// copies of both, so Cancel leaves the staged dataframe and the plot options
    /// untouched.
    /// </summary>
    internal class PcaAdvancedWindow : Window
    {
        private readonly PcaDataFrame _working;
        private readonly PlotOptions _workingOptions;
        private readonly List<PcaVariableEntry> _catalog;

        // Each row's buttons are enabled or disabled as the dataframe changes, so
        // every row keeps a handle on its own pair.
        private readonly List<(PcaVariableEntry Entry, Button Add, Button Remove, TextBlock Label)>
            _rows = new List<(PcaVariableEntry, Button, Button, TextBlock)>();

        private TextBlock _includedHeader;
        private TextBlock _includedText;
        private Button _runButton;

        private ComboBox _pointColorBox;
        private ComboBox _pointShapeBox;
        private TextBox _titleBox;
        private CheckBox _ellipseBox;

        internal PcaAdvancedWindow(
            PcaDataFrame current, PlotOptions options, IEnumerable<PcaVariableEntry> catalog)
        {
            _working = current?.Clone() ?? new PcaDataFrame();
            _workingOptions = (options ?? new PlotOptions()).Clone();
            _catalog = catalog?.ToList() ?? new List<PcaVariableEntry>();

            Title = "Advanced PCA options";
            Width = 460;
            Height = 700;
            MinWidth = 400;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(16) };

            var note = new TextBlock
            {
                Text = "Choose the variables the PCA runs on. This dataframe is the "
                     + "Plot tab's own: it does not affect the Batch Workshop tables "
                     + "or their exports.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            // Docked bottom-first: in a DockPanel the earlier child takes the outer
            // edge, so these are added in the order they stack upwards from the
            // bottom — buttons, then points, then the included list above both.
            var buttonBar = BuildButtonBar();
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            var display = BuildDisplayPanel();
            DockPanel.SetDock(display, Dock.Bottom);
            root.Children.Add(display);

            var included = BuildIncludedPanel();
            DockPanel.SetDock(included, Dock.Bottom);
            root.Children.Add(included);

            root.Children.Add(new ScrollViewer
            {
                Content = BuildList(),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            Content = root;
            UpdateState();
        }

        // ---- Available list ----

        private UIElement BuildList()
        {
            var panel = new StackPanel();

            if (_catalog.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No measurements have been recorded this session yet. "
                         + "Perform operations on a specimen, then reopen this window.",
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap
                });
                return panel;
            }

            // The catalog arrives grouped by mode, so a heading is emitted whenever
            // the group changes rather than by sorting again here.
            string lastGroup = null;

            foreach (var entry in _catalog)
            {
                if (!string.Equals(entry.Group, lastGroup, StringComparison.Ordinal))
                {
                    panel.Children.Add(SectionHeader(entry.Group));
                    lastGroup = entry.Group;
                }

                panel.Children.Add(BuildRow(entry));
            }

            return panel;
        }

        // One row per catalog entry, bundles included: the EFA coefficients are
        // added and removed as a unit here, and only broken out one by one in the
        // included list below.
        private UIElement BuildRow(PcaVariableEntry entry)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = entry.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 8, 0),
                ToolTip = entry.IsBundle
                    ? $"{entry.Columns.Count} columns, added and removed together"
                    : null
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var add = SmallButton("+", "Add to the PCA dataframe");
            var remove = SmallButton("\u2212", "Remove from the PCA dataframe");

            add.Click += (s, e) => { _working.Add(entry.Key); UpdateState(); };
            remove.Click += (s, e) => { _working.Remove(entry.Key); UpdateState(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(add);
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            _rows.Add((entry, add, remove, label));

            // A bare row needs no wrapper, so only entries carrying a note pay for one.
            if (string.IsNullOrEmpty(entry.Note))
            {
                grid.Margin = new Thickness(0, 0, 0, 4);
                return grid;
            }

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            stack.Children.Add(grid);
            stack.Children.Add(new TextBlock
            {
                Text = entry.Note,
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 34, 0)
            });

            return stack;
        }

        // Segoe UI is pinned so the minus sign renders in its own box rather than
        // falling back, matching the sidebar's minimize button.
        private static Button SmallButton(string glyph, string tip) => new Button
        {
            Content = glyph,
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI"),
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static TextBlock SectionHeader(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 6)
        };

        // ---- Running list ----

        private FrameworkElement BuildIncludedPanel()
        {
            _includedHeader = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            };

            _includedText = new TextBlock { TextWrapping = TextWrapping.Wrap };

            var stack = new StackPanel();
            stack.Children.Add(_includedHeader);

            stack.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Padding = new Thickness(6),
                Child = new ScrollViewer
                {
                    // Taller than one line's worth: a staged EFA table lists every
                    // one of its coefficients here.
                    Height = 90,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _includedText
                }
            });

            var clear = new Button
            {
                Content = "Clear",
                Width = 70,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "Empty the PCA dataframe"
            };
            clear.Click += (s, e) => { _working.Clear(); UpdateState(); };
            stack.Children.Add(clear);

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 0),
                Margin = new Thickness(0, 10, 0, 0),
                Child = stack
            };
        }

        // ---- Display ----

        /// Title, point aesthetics, and the group ellipse toggle. The point settings
        /// are the same session-wide ones the other plot types' Advanced window
        /// edits, so a group given a color here keeps it on every plot.
        private FrameworkElement BuildDisplayPanel()
        {
            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = "Display",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            _titleBox = new TextBox { Text = _workingOptions.Title, MaxLength = 60 };
            stack.Children.Add(Row("Plot title", _titleBox,
                "Leave blank to use the generated title"));

            _pointColorBox = new ComboBox { ItemsSource = MainWindow.PlotPointColorChoices() };
            _pointColorBox.SelectedItem = SelectedOrFallback(
                _pointColorBox, _workingOptions.PointColor, PlotOptions.AestheticBlack);
            stack.Children.Add(Row("Point color", _pointColorBox,
                "Black draws every specimen the same. A group column gives each of "
                + "its groups its own color."));

            _pointShapeBox = new ComboBox { ItemsSource = MainWindow.PlotPointShapeChoices() };
            _pointShapeBox.SelectedItem = SelectedOrFallback(
                _pointShapeBox, _workingOptions.PointShape, PlotOptions.AestheticCircle);
            stack.Children.Add(Row("Point shape", _pointShapeBox,
                "Circle draws every specimen the same. A group column gives each of "
                + "its groups its own marker."));

            _ellipseBox = new CheckBox
            {
                Content = "95% confidence intervals",
                IsChecked = _workingOptions.ConfidenceEllipses,
                Margin = new Thickness(2, 2, 0, 4),
                ToolTip = "Draw a 95% confidence ellipse around each group of scores. "
                        + "Groups are the ones Point color splits by, and a group needs "
                        + "at least three specimens to define an ellipse."
            };
            stack.Children.Add(_ellipseBox);

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 0),
                Margin = new Thickness(0, 10, 0, 0),
                Child = stack
            };
        }

        // A stored choice whose group column has since gone falls back rather than
        // leaving the box blank.
        private static object SelectedOrFallback(ComboBox box, string value, string fallback)
        {
            var items = (List<string>)box.ItemsSource;
            return items.Contains(value) ? value : fallback;
        }

        // Label and control on one line, with the label column wide enough that
        // both controls start at the same x.
        private static Grid Row(string label, FrameworkElement control, string tip)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            control.ToolTip = tip;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            return grid;
        }

        // ---- Buttons ----

        private FrameworkElement BuildButtonBar()
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _runButton = new Button { Content = "Run PCA", Width = 104, Height = 28, IsDefault = true };
            _runButton.Click += (s, e) =>
            {
                // DialogResult may only be set while running modally; guard it the
                // same way ScaleWindow does.
                try
                {
                    DialogResult = true;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            };
            buttons.Children.Add(_runButton);

            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                Width = 92,
                Height = 28,
                Margin = new Thickness(10, 0, 0, 0),
                IsCancel = true
            });

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 12, 0, 0),
                Margin = new Thickness(0, 12, 0, 0),
                Child = buttons
            };
        }

        // ---- State ----

        // A row's + is live only while the entry is out of the dataframe and its
        // − only while it is in, so the buttons themselves say what is staged.
        private void UpdateState()
        {
            foreach (var row in _rows)
            {
                bool included = _working.Contains(row.Entry.Key);
                row.Add.IsEnabled = !included;
                row.Remove.IsEnabled = included;
                row.Label.FontWeight = included ? FontWeights.Bold : FontWeights.Normal;
            }

            var staged = StagedColumns();
            int n = staged.Count;

            _includedHeader.Text = n switch
            {
                0 => "In the PCA dataframe: none",
                1 => "In the PCA dataframe: 1 variable",
                _ => $"In the PCA dataframe: {n} variables"
            };

            _includedText.Text = n == 0
                ? "Use + to add a variable."
                : string.Join(", ", staged);

            _includedText.Opacity = n == 0 ? 0.6 : 1.0;

            // Counted in columns rather than entries: the analysis needs two columns
            // to have anything to rotate, and the EFA table supplies several on its
            // own, so it is a usable dataframe by itself.
            _runButton.IsEnabled = n >= 2;
            _runButton.ToolTip = n >= 2
                ? "Run the analysis and plot the component scores"
                : "Add at least two variables first";
        }

        /// Every measurement column the staged entries stand for, in the order they
        /// were added. A bundle contributes each of its columns separately, so the
        /// EFA coefficients are counted and listed one by one even though the list
        /// above adds and removes them as a unit.
        private List<string> StagedColumns()
        {
            var columns = new List<string>();

            foreach (string key in _working.Keys)
            {
                var entry = _catalog.FirstOrDefault(e => e.Key == key);

                // Staged before its variable left the catalog: name it by its key
                // rather than dropping it silently.
                if (entry == null || entry.Columns.Count == 0)
                {
                    columns.Add(entry?.Label ?? key);
                    continue;
                }

                columns.AddRange(entry.Columns);
            }

            return columns;
        }

        // ---- Commit ----

        internal void CommitTo(PcaDataFrame target) => target.CopyFrom(_working);

        /// Writes the title and point settings back. They are shared with the other
        /// plot types, so a group keeps its color and marker across the whole tab.
        internal void CommitTo(PlotOptions target)
        {
            target.Title = _titleBox.Text?.Trim() ?? "";
            target.PointColor = _pointColorBox.SelectedItem as string ?? PlotOptions.AestheticBlack;
            target.PointShape = _pointShapeBox.SelectedItem as string ?? PlotOptions.AestheticCircle;
            target.ConfidenceEllipses = _ellipseBox.IsChecked == true;
        }
    }
}