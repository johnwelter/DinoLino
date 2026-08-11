using DinoLino.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // Trend fits offered on a scatter plot. "lm" and "loess" are the strings
        // DrawTrend dispatches on, so they are named here rather than typed twice.
        public const string TrendNone = "None";
        public const string TrendLinear = "lm";
        public const string TrendLoess = "loess";

        public static readonly string[] TrendChoices = { TrendNone, TrendLinear, TrendLoess };

        /// <summary>Group column the fill is split by, or "None".</summary>
        public string ColourBy { get; set; } = MainWindow.ColourNone;

        /// <summary>Palette the fill and point-color levels cycle through.</summary>
        public string Palette { get; set; } = DefaultPaletteName;

        /// <summary>"None", "Black", or a group column name.</summary>
        public string PointColor { get; set; } = AestheticBlack;

        /// <summary>"None", "Circle", or a group column name.</summary>
        public string PointShape { get; set; } = AestheticCircle;

        /// "None", "Specimen", or a group column name. Labels sit beside the points,
        /// so a plot that is not drawing points does not draw them either.
        public string LabelBy { get; set; } = AestheticNone;

        /// <summary>"None", "lm", or "loess". Scatter plots only.</summary>
        public string Trend { get; set; } = TrendNone;

        /// Write the fitted line's equation and its R² on the plot. Both read the
        /// least-squares fit, so neither applies to a loess curve.
        public bool ShowEquation { get; set; } = false;
        public bool ShowRSquared { get; set; } = false;

        /// Draw a 95% confidence ellipse around each point-color group. Only the
        /// PCA scores plot reads this; the other plot types ignore it.
        public bool ConfidenceEllipses { get; set; } = false;

        /// Fewest specimens a group needs before a confidence ellipse means
        /// anything: two points define a line, not an area. Shared so the checkbox
        /// and the renderer never disagree about which groups qualify.
        public const int MinEllipseGroupSize = 3;

        /// Draw each extreme specimen's stored silhouette beside its point. Only the
        /// PCA scores plot reads this.
        public bool ShowSilhouettes { get; set; } = false;

        /// Components the scores plot draws. Chosen in the PCA Advanced window
        /// rather than the Plot tab, so the dialog can fit a preview against the
        /// staged dataframe and know which specimens are extreme.
        public string PcaXComponent { get; set; } = "PC1";
        public string PcaYComponent { get; set; } = "PC2";

        /// specimen -> the stored outline name chosen for it. A specimen missing from
        /// the map, or naming an outline that has since gone, falls back to whichever
        /// the store lists first.
        public Dictionary<string, string> SilhouetteChoices =
            new Dictionary<string, string>(StringComparer.Ordinal);

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

            LabelBy = KeepIfPresent(LabelBy, AestheticNone,
                AestheticNone, MainWindow.CategorySpecimen);
        }

        private static string KeepIfPresent(string value, string fallback, params string[] reserved)
        {
            if (reserved.Contains(value)) return value;

            return SpecimenGroups.Columns.Any(
                c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase))
                ? value
                : fallback;
        }

        /// The dictionary is copied rather than shared: the dialog edits a clone, so
        /// Cancel must leave the live options untouched.
        public PlotOptions Clone()
        {
            var copy = (PlotOptions)MemberwiseClone();
            copy.SilhouetteChoices =
                new Dictionary<string, string>(SilhouetteChoices, StringComparer.Ordinal);
            return copy;
        }
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
        /// True when the plot draws one mark per datapoint for a label to sit beside.
        public bool SupportsLabels;

        /// True when the plot can carry a fitted trend line.
        public bool SupportsTrend;

        public string TrendNote = "";

        /// True when those marks are optional, so labels follow the point settings
        /// rather than being available outright. Only the boxplot works this way.
        public bool LabelsRequirePoints;

        public string LabelNote = "";
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
                        SupportsLabels = true,
                        LabelsRequirePoints = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true
                    };

                case "Scatter plot":
                    return new PlotCapabilities
                    {
                        SupportsFill = false,
                        SupportsPoints = true,
                        SupportsLabels = true,
                        SupportsTrend = true,
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
                        PointNote = "Histograms draw bars, not points.",
                        LabelNote = "Datapoints are not drawn and thus cannot be labeled."
                    };

                case "QQ plot":
                    return new PlotCapabilities
                    {
                        SupportsFill = false,
                        SupportsPoints = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        FillNote = "QQ plots have no filled areas.",
                    };

                case "Dot plot":
                    return new PlotCapabilities
                    {
                        SupportsFill = false,
                        SupportsPoints = true,
                        SupportsLabels = true,
                        SupportsXAxisTitle = true,

                        // The vertical axis is a stack count with no scale drawn.
                        SupportsYAxisTitle = false,
                        FillNote = "Dot plots have no filled areas."
                    };

                case "PCA":
                    return new PlotCapabilities
                    {
                        SupportsFill = false,
                        SupportsPoints = true,
                        SupportsLabels = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        FillNote = "Scores plots have no filled areas."
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
        private ComboBox _labelBox;
        private ComboBox _trendBox;
        private CheckBox _equationBox;
        private CheckBox _rSquaredBox;
        private TextBox _titleBox;
        private TextBox _xTitleBox;
        private TextBox _yTitleBox;
        private const string LabelTip =
            "Write each point's specimen name, or its group under a column, beside it";

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

            // Also drives the dependent rows: on a boxplot the labels are only
            // available once both point settings are drawing something.
            _shapeBox = new ComboBox { ItemsSource = MainWindow.PlotPointShapeChoices() };
            _shapeBox.SelectedItem =
                Select(_shapeBox, _working.PointShape, PlotOptions.AestheticCircle);
            _shapeBox.SelectionChanged += (s, e) => UpdateDependentRows();
            panel.Children.Add(Row("Point shape", _shapeBox,
                "Circle draws every point the same. A group column gives each of "
                + "its groups its own marker; None hides the points."));

            _labelBox = new ComboBox { ItemsSource = MainWindow.PlotLabelChoices() };
            _labelBox.SelectedItem = Select(_labelBox, _working.LabelBy, PlotOptions.AestheticNone);
            panel.Children.Add(Row("Add Labels", _labelBox, LabelTip));

            panel.Children.Add(SectionHeader("Trend line"));

            _trendBox = new ComboBox { ItemsSource = PlotOptions.TrendChoices.ToList() };
            _trendBox.SelectedItem = Select(_trendBox, _working.Trend, PlotOptions.TrendNone);
            _trendBox.SelectionChanged += (s, e) => UpdateDependentRows();
            panel.Children.Add(Row("Fit", _trendBox,
                "lm fits a straight least-squares line; loess fits a local regression curve"));

            _equationBox = new CheckBox
            {
                Content = "Show regression equation",
                IsChecked = _working.ShowEquation,
                Margin = new Thickness(2, 2, 0, 4)
            };
            panel.Children.Add(_equationBox);

            _rSquaredBox = new CheckBox
            {
                Content = "Show R\u00B2",
                IsChecked = _working.ShowRSquared,
                Margin = new Thickness(2, 0, 0, 8)
            };
            panel.Children.Add(_rSquaredBox);

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
            _trendBox.IsEnabled = _capabilities.SupportsTrend;

            if (!_capabilities.SupportsTrend && !string.IsNullOrEmpty(_capabilities.TrendNote))
                _trendBox.ToolTip = _capabilities.TrendNote;

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

            // Labels sit beside the markers, so on a boxplot — the one plot whose
            // points are optional — they follow whether those points are drawn.
            bool pointsShown =
                (_pointColorBox.SelectedItem as string) != PlotOptions.AestheticNone &&
                (_shapeBox.SelectedItem as string) != PlotOptions.AestheticNone;

            bool labelsUsable = _capabilities.SupportsLabels &&
                                (!_capabilities.LabelsRequirePoints || pointsShown);

            _labelBox.IsEnabled = labelsUsable;
            _labelBox.ToolTip =
                labelsUsable ? LabelTip
                : !_capabilities.SupportsLabels
                    ? (string.IsNullOrEmpty(_capabilities.LabelNote)
                        ? "Labels are not available on this plot type."
                        : _capabilities.LabelNote)
                    : "Add points to the boxplot first: set Point color and Point shape "
                      + "to something other than None.";

            // Both readings come off the least-squares fit, so a loess curve — which
            // has no single equation to write down — leaves them unavailable.
            bool linear = _capabilities.SupportsTrend &&
                          (_trendBox.SelectedItem as string) == PlotOptions.TrendLinear;

            _equationBox.IsEnabled = linear;
            _rSquaredBox.IsEnabled = linear;

            string fitNote = !_capabilities.SupportsTrend
                ? (string.IsNullOrEmpty(_capabilities.TrendNote)
                    ? "Trend lines are not available on this plot type."
                    : _capabilities.TrendNote)
                : "Set Fit to lm to write the equation and R\u00B2 on the plot";

            _equationBox.ToolTip = linear
                ? "Write the fitted line's equation at the top of the plot" : fitNote;
            _rSquaredBox.ToolTip = linear
                ? "Write the fit's R\u00B2 at the top of the plot" : fitNote;
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

            if (_labelBox.IsEnabled)
                target.LabelBy = _labelBox.SelectedItem as string ?? PlotOptions.AestheticNone;

            if (_trendBox.IsEnabled)
                target.Trend = _trendBox.SelectedItem as string ?? PlotOptions.TrendNone;

            if (_equationBox.IsEnabled) target.ShowEquation = _equationBox.IsChecked == true;
            if (_rSquaredBox.IsEnabled) target.ShowRSquared = _rSquaredBox.IsChecked == true;

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

    /// <summary>Which side of its point a silhouette is drawn on.</summary>
    public enum SilhouetteSide { Left, Right, Above, Below }

    /// <summary>
    /// One extreme datapoint on the scores plot: the specimen holding the highest
    /// or lowest score on a plotted component. Silhouettes attach to these.
    /// </summary>
    public sealed class PcaExtreme
    {
        /// <summary>Row of the PCA result, so the plot can read its coordinates.</summary>
        public int Row;

        public string Specimen;

        /// <summary>Shown to the user, e.g. "Highest PC1".</summary>
        public string Role;

        /// Outboard of the extreme it holds, so no other datapoint can lie between
        /// the silhouette and its own point.
        public SilhouetteSide Side;

        /// "PC3" -> 2. Negative when the text is not a component name.
        public static int ComponentIndex(string name)
        {
            if (name == null || !name.StartsWith("PC", StringComparison.Ordinal)) return -1;
            return int.TryParse(name.Substring(2), out int n) && n > 0 ? n - 1 : -1;
        }

        public static List<string> ComponentNames(int count)
        {
            var names = new List<string>();
            for (int i = 0; i < count; i++) names.Add($"PC{i + 1}");
            return names;
        }

        /// The four points a silhouette can attach to. Ties go to the first row
        /// reaching the value, which is the specimen order the tables list. Static
        /// so the Advanced window can read them off its own preview fit, without a
        /// run having been committed.
        public static List<PcaExtreme> Find(
            PcaAnalysis.Result result, IReadOnlyList<string> rowSpecimens,
            string xName, string yName)
        {
            var extremes = new List<PcaExtreme>();

            if (result == null || rowSpecimens == null) return extremes;
            if (result.ObservationCount == 0) return extremes;
            if (rowSpecimens.Count < result.ObservationCount) return extremes;

            int xi = ComponentIndex(xName), yi = ComponentIndex(yName);
            if (xi < 0 || yi < 0) return extremes;
            if (xi >= result.ComponentCount || yi >= result.ComponentCount) return extremes;

            int maxX = 0, minX = 0, maxY = 0, minY = 0;

            for (int r = 1; r < result.ObservationCount; r++)
            {
                if (result.Scores[r, xi] > result.Scores[maxX, xi]) maxX = r;
                if (result.Scores[r, xi] < result.Scores[minX, xi]) minX = r;
                if (result.Scores[r, yi] > result.Scores[maxY, yi]) maxY = r;
                if (result.Scores[r, yi] < result.Scores[minY, yi]) minY = r;
            }

            extremes.Add(new PcaExtreme
            {
                Row = maxX,
                Specimen = rowSpecimens[maxX],
                Role = "Highest " + xName,
                Side = SilhouetteSide.Right
            });
            extremes.Add(new PcaExtreme
            {
                Row = minX,
                Specimen = rowSpecimens[minX],
                Role = "Lowest " + xName,
                Side = SilhouetteSide.Left
            });
            extremes.Add(new PcaExtreme
            {
                Row = maxY,
                Specimen = rowSpecimens[maxY],
                Role = "Highest " + yName,
                Side = SilhouetteSide.Above
            });
            extremes.Add(new PcaExtreme
            {
                Row = minY,
                Specimen = rowSpecimens[minY],
                Role = "Lowest " + yName,
                Side = SilhouetteSide.Below
            });

            return extremes;
        }
    }

    /// <summary>
    /// A PCA fitted against a dataframe the user has staged but not yet run. The
    /// Advanced window asks for one so its component list and its silhouette
    /// option are live from the moment it opens.
    /// </summary>
    public sealed class PcaPreview
    {
        public PcaAnalysis.Result Result;
        public List<string> RowSpecimens;

        /// <summary>Why there is no result, ready to show the user.</summary>
        public string Failure;
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
        private ComboBox _labelBox;
        private TextBox _titleBox;
        private CheckBox _ellipseBox;
        private CheckBox _silhouetteBox;
        private ComboBox _xBox;
        private ComboBox _yBox;

        // Refitted whenever the staged dataframe changes, so the component list and
        // the silhouette option are live without a committed run.
        private readonly Func<PcaDataFrame, PcaPreview> _previewFor;
        private PcaPreview _preview = new PcaPreview();
        private List<PcaExtreme> _extremes = new List<PcaExtreme>();
        private readonly Func<IReadOnlyList<string>, string, List<int>> _groupSizesFor;

        // The available-variable list, held so its height can be set from the rows
        // themselves once they have been laid out.
        private ScrollViewer _listScroller;

        // Every element of that list in order, headers included, so the height of
        // the first six variables can be summed.
        private readonly List<(FrameworkElement Element, bool IsVariable)> _listItems =
            new List<(FrameworkElement, bool)>();

        internal PcaAdvancedWindow(
            PcaDataFrame current, PlotOptions options,
            IEnumerable<PcaVariableEntry> catalog,
            Func<PcaDataFrame, PcaPreview> previewFor,
            Func<IReadOnlyList<string>, string, List<int>> groupSizesFor)
        {
            _working = current?.Clone() ?? new PcaDataFrame();
            _workingOptions = (options ?? new PlotOptions()).Clone();
            _catalog = catalog?.ToList() ?? new List<PcaVariableEntry>();
            _previewFor = previewFor;
            _groupSizesFor = groupSizesFor;

            Title = "Advanced PCA options";
            Width = 470;
            Height = 780;
            MinWidth = 400;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(16) };

            // Run and Cancel sit outside the scroller, so they stay reachable no
            // matter how short the window is.
            var buttonBar = BuildButtonBar();
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            // Everything else stacks in reading order and scrolls as one.
            var body = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            body.Children.Add(BuildNote());
            body.Children.Add(BuildListPanel());
            body.Children.Add(BuildIncludedPanel());
            body.Children.Add(BuildDisplayPanel());

            root.Children.Add(new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,

                // Disabled rather than hidden: it holds the content to the viewport
                // width, so the note and the tips wrap to what is visible.
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            });

            Content = root;
            UpdateState();

            // Row heights are only known once the list has been laid out.
            Loaded += (s, e) => SizeListToSixEntries();
        }

        private static TextBlock BuildNote() => new TextBlock
        {
            Text = "Choose the variables the PCA runs on. This dataframe is the "
                 + "Plot tab's own: it does not affect the Batch Workshop tables "
                 + "or their exports.",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };

        // ---- Available list ----

        /// The available-variable list: a fixed-height pane with its own bar, deep
        /// enough to show six variables at once.
        private FrameworkElement BuildListPanel()
        {
            _listScroller = new ScrollViewer
            {
                Content = BuildList(),
                Height = 200,                    // replaced once the rows are measured
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            _listScroller.PreviewMouseWheel += InnerListWheel;

            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Padding = new Thickness(6, 2, 4, 4),
                Child = _listScroller
            };
        }

        /// Sets the list's height from the rows themselves, so at least six
        /// variables are visible. Measured rather than assumed: a row carrying a
        /// note is nearly twice the height of a bare one, and the mode headers sit
        /// between them.
        private void SizeListToSixEntries()
        {
            const int Target = 6;
            const double Floor = 90;
            const double Ceiling = 360;

            if (_listItems.Count == 0) return;

            // Loaded can precede the first arrange, which would leave every
            // ActualHeight at zero.
            _listScroller.UpdateLayout();

            double total = 0;
            int seen = 0;

            foreach (var item in _listItems)
            {
                total += item.Element.ActualHeight
                       + item.Element.Margin.Top + item.Element.Margin.Bottom;

                if (item.IsVariable && ++seen == Target) break;
            }

            if (seen == 0 || total <= 0) return;

            // A sliver of the seventh row shows there is more below.
            if (seen == Target) total += 10;

            _listScroller.Height = Math.Max(Floor, Math.Min(Ceiling, total));
        }

        // A nested ScrollViewer swallows the wheel even with nothing left to
        // scroll, which would strand the window's own bar whenever the pointer sat
        // over a list. Hand the gesture up once the inner one has reached its end.
        private static void InnerListWheel(object sender, MouseWheelEventArgs e)
        {
            var viewer = (ScrollViewer)sender;

            bool atTop = viewer.VerticalOffset <= 0;
            bool atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight;

            if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom)) return;

            e.Handled = true;

            if (VisualTreeHelper.GetParent(viewer) is UIElement parent)
            {
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = viewer
                });
            }
        }
        private UIElement BuildList()
        {
            var panel = new StackPanel();
            _listItems.Clear();

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
                    var header = SectionHeader(entry.Group);
                    panel.Children.Add(header);
                    _listItems.Add((header, false));
                    lastGroup = entry.Group;
                }

                var row = BuildRow(entry);
                panel.Children.Add(row);
                _listItems.Add((row, true));
            }

            return panel;
        }

        // One row per catalog entry, bundles included: the EFA coefficients are
        // added and removed as a unit here, and only broken out one by one in the
        // included list below.
        private FrameworkElement BuildRow(PcaVariableEntry entry)
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
                Child = BuildIncludedScroller(),
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

        private ScrollViewer BuildIncludedScroller()
        {
            // Taller than one line's worth: a staged EFA table lists every one of
            // its coefficients here.
            var viewer = new ScrollViewer
            {
                Height = 90,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _includedText
            };

            viewer.PreviewMouseWheel += InnerListWheel;
            return viewer;
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

            _xBox = new ComboBox();
            _xBox.SelectionChanged += (s, e) => OnComponentChanged();
            stack.Children.Add(Row("X", _xBox, "Component drawn on the horizontal axis"));

            _yBox = new ComboBox();
            _yBox.SelectionChanged += (s, e) => OnComponentChanged();
            stack.Children.Add(Row("Y", _yBox, "Component drawn on the vertical axis"));

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

            _labelBox = new ComboBox { ItemsSource = MainWindow.PlotLabelChoices() };
            _labelBox.SelectedItem = SelectedOrFallback(
                _labelBox, _workingOptions.LabelBy, PlotOptions.AestheticNone);
            stack.Children.Add(Row("Add Labels", _labelBox,
                "Write each specimen's name, or its group under a column, beside its score"));

            _ellipseBox = new CheckBox
            {
                Content = "95% confidence ellipses",
                IsChecked = _workingOptions.ConfidenceEllipses,
                Margin = new Thickness(2, 2, 0, 4),
                ToolTip = "Draw a 95% confidence ellipse around each group of scores. "
                        + "A group needs "
                        + "at least three specimens to define an ellipse."
            };
            stack.Children.Add(_ellipseBox);

            // Grouping follows the point colour, so changing it changes which
            // groups exist and how big they are. Subscribed here rather than where
            // the box is built, so the checkbox above already exists.
            _pointColorBox.SelectionChanged += (s, e) => ApplyEllipseAvailability();

            _silhouetteBox = new CheckBox
            {
                Content = "Add Silhouettes",
                IsChecked = _workingOptions.ShowSilhouettes,
                Margin = new Thickness(2, 8, 0, 2)
            };
            _silhouetteBox.Checked += (s, e) => OnSilhouettesTicked();
            stack.Children.Add(_silhouetteBox);

            stack.Children.Add(new TextBlock
            {
                Text = "2D outlines are added for the data points that exhibit maximum "
                     + "and minimum X and Y values.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 4)
            });

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

        /// The option needs a run to have produced extremes, and every one of those
        /// specimens to have a stored silhouette; a disabled box says which is missing.
        private void ApplySilhouetteAvailability()
        {
            ToolTipService.SetShowOnDisabled(_silhouetteBox, true);

            if (_extremes.Count == 0)
            {
                _silhouetteBox.IsEnabled = false;
                _silhouetteBox.IsChecked = false;
                _silhouetteBox.ToolTip =
                    "Run the PCA and choose both components first.";
                return;
            }

            var missing = _extremes
                .Select(x => x.Specimen)
                .Distinct(StringComparer.Ordinal)
                .Where(s => CommittedOutlineStore.CountFor(s) == 0)
                .ToList();

            if (missing.Count > 0)
            {
                _silhouetteBox.IsEnabled = false;
                _silhouetteBox.IsChecked = false;
                _silhouetteBox.ToolTip =
                    "No outline has been stored for " + string.Join(", ", missing) + ". "
                    + "Commit an outline for each of those specimens first.";
                return;
            }

            _silhouetteBox.IsEnabled = true;
            _silhouetteBox.ToolTip =
                "Draw each extreme specimen's stored silhouette beside its point";
        }

        /// A 95% ellipse needs a group with enough specimens to define one, so the
        /// option is offered only when at least one group has them. Groups that
        /// fall short are simply not drawn, which the plot reports underneath.
        private void ApplyEllipseAvailability()
        {
            if (_ellipseBox == null) return;

            var specimens = _preview.RowSpecimens;
            string color = _pointColorBox?.SelectedItem as string ?? _workingOptions.PointColor;

            var sizes = specimens == null || specimens.Count == 0
                ? new List<int>()
                : (_groupSizesFor?.Invoke(specimens, color) ?? new List<int>());

            int usable = sizes.Count(n => n >= PlotOptions.MinEllipseGroupSize);

            _ellipseBox.IsEnabled = usable > 0;
            if (usable == 0) _ellipseBox.IsChecked = false;

            if (usable > 0)
            {
                _ellipseBox.ToolTip = sizes.Count > usable
                    ? $"Drawn for the {usable} of {sizes.Count} groups holding at least "
                      + $"{PlotOptions.MinEllipseGroupSize} specimens. The rest are left bare."
                    : "Draw a 95% confidence ellipse around each group of scores.";
            }
            else if (sizes.Count == 0)
            {
                _ellipseBox.ToolTip = string.IsNullOrEmpty(_preview.Failure)
                    ? "Add at least two variables to the dataframe first."
                    : _preview.Failure;
            }
            else
            {
                _ellipseBox.ToolTip =
                    $"No group holds the {PlotOptions.MinEllipseGroupSize} specimens an "
                    + "ellipse needs. Choose a coarser group column under Point color, "
                    + "or measure more specimens.";
            }

            ToolTipService.SetShowOnDisabled(_ellipseBox, true);
        }

        /// Refits against the staged dataframe and refills the component boxes. The
        /// user's choices survive as long as the run still produces them.
        private void RefreshPreview()
        {
            _preview = _previewFor?.Invoke(_working) ?? new PcaPreview();

            int count = _preview.Result?.ComponentCount ?? 0;
            var names = PcaExtreme.ComponentNames(count);

            string keepX = _xBox.SelectedItem as string ?? _workingOptions.PcaXComponent;
            string keepY = _yBox.SelectedItem as string ?? _workingOptions.PcaYComponent;

            _xBox.ItemsSource = names;
            _yBox.ItemsSource = names;

            _xBox.IsEnabled = count > 0;
            _yBox.IsEnabled = count > 0;

            if (_extremes.Count == 0)
            {
                _silhouetteBox.IsEnabled = false;
                _silhouetteBox.IsChecked = false;
                _silhouetteBox.ToolTip = string.IsNullOrEmpty(_preview.Failure)
                    ? "Choose both components first."
                    : _preview.Failure;
                return;
            }

            string tip = count > 0
                ? "Component drawn on this axis"
                : "Add at least two variables to the dataframe first.";
            _xBox.ToolTip = tip;
            _yBox.ToolTip = tip;
            ToolTipService.SetShowOnDisabled(_xBox, true);
            ToolTipService.SetShowOnDisabled(_yBox, true);

            RecomputeExtremes();
        }

        private void RecomputeExtremes()
        {
            _extremes = PcaExtreme.Find(
                _preview.Result, _preview.RowSpecimens,
                _xBox.SelectedItem as string, _yBox.SelectedItem as string);

            ApplySilhouetteAvailability();
            ApplyEllipseAvailability();
        }

        // Changing an axis moves which specimens are extreme, but not the fit.
        private void OnComponentChanged() => RecomputeExtremes();

        /// Asks which outline to use as soon as the box is ticked, but only when some
        /// extreme specimen holds more than one: with a single outline apiece there is
        /// nothing to decide.
        private void OnSilhouettesTicked()
        {
            bool anyChoice = _extremes
                .Select(x => x.Specimen)
                .Distinct(StringComparer.Ordinal)
                .Any(s => CommittedOutlineStore.CountFor(s) > 1);

            if (!anyChoice) return;

            var picker = new SilhouettePickerWindow(_extremes, _workingOptions.SilhouetteChoices)
            {
                Owner = this,
                FontSize = FontSize,
                FontFamily = FontFamily
            };

            if (picker.ShowDialog() == true)
            {
                _workingOptions.SilhouetteChoices = picker.Choices;
                return;
            }

            // Cancelled: nothing was chosen, so the option does not take effect.
            _silhouetteBox.IsChecked = false;
        }

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

            // The staged dataframe decides the fit, so anything reading it refreshes
            // whenever a variable is added or removed.
            RefreshPreview();
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
            target.LabelBy = _labelBox.SelectedItem as string ?? PlotOptions.AestheticNone;
            target.ShowSilhouettes = _silhouetteBox.IsEnabled && _silhouetteBox.IsChecked == true;
            target.SilhouetteChoices = new Dictionary<string, string>(
                _workingOptions.SilhouetteChoices, StringComparer.Ordinal);
            target.PcaXComponent = _xBox.SelectedItem as string ?? target.PcaXComponent;
            target.PcaYComponent = _yBox.SelectedItem as string ?? target.PcaYComponent;
        }

        /// <summary>
        /// Asks which stored silhouette to draw for each extreme specimen. Opened from
        /// the PCA Advanced window the moment "Add Silhouettes" is ticked, so the plot
        /// never has to guess between a specimen's outlines.
        /// </summary>
        internal class SilhouettePickerWindow : Window
        {
            private const int PreviewPixels = 128;
            private const double PreviewSize = 72;

            /// <summary>specimen -> chosen outline name; valid once ShowDialog returns true.</summary>
            internal Dictionary<string, string> Choices { get; } =
                new Dictionary<string, string>(StringComparer.Ordinal);

            private readonly List<(string Specimen, ComboBox Box)> _rows =
                new List<(string, ComboBox)>();

            internal SilhouettePickerWindow(
                IReadOnlyList<PcaExtreme> extremes, IDictionary<string, string> current)
            {
                Title = "Choose silhouettes";
                Width = 470;
                SizeToContent = SizeToContent.Height;
                MaxHeight = 640;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;

                var root = new DockPanel { Margin = new Thickness(16) };

                var note = new TextBlock
                {
                    Text = "These specimens hold the extreme scores on the plotted components. "
                         + "Choose which stored outline the plot draws for each.",
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                DockPanel.SetDock(note, Dock.Top);
                root.Children.Add(note);

                var buttons = BuildButtonBar();
                DockPanel.SetDock(buttons, Dock.Bottom);
                root.Children.Add(buttons);

                root.Children.Add(new ScrollViewer
                {
                    Content = BuildRows(extremes, current),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                });

                Content = root;
            }

            // One row per specimen rather than per extreme: a specimen holding two of
            // them is drawn once, so it is chosen once.
            private UIElement BuildRows(
                IReadOnlyList<PcaExtreme> extremes, IDictionary<string, string> current)
            {
                var panel = new StackPanel();

                var roles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var order = new List<string>();

                foreach (var extreme in extremes)
                {
                    if (!roles.TryGetValue(extreme.Specimen, out var list))
                    {
                        list = new List<string>();
                        roles[extreme.Specimen] = list;
                        order.Add(extreme.Specimen);
                    }

                    list.Add(extreme.Role);
                }

                foreach (string specimen in order)
                    panel.Children.Add(BuildRow(specimen, roles[specimen], current));

                return panel;
            }

            private UIElement BuildRow(
                string specimen, List<string> roles, IDictionary<string, string> current)
            {
                var stored = CommittedOutlineStore.OutlinesFor(specimen);

                var names = new List<string>();
                foreach (var outline in stored) names.Add(outline.Name);

                var box = new ComboBox { ItemsSource = names, MinWidth = 210 };

                // A choice made on a previous visit is kept, unless that outline has
                // since been renamed or deleted.
                string chosen = null;
                if (current != null &&
                    current.TryGetValue(specimen, out string stashed) &&
                    names.Contains(stashed))
                {
                    chosen = stashed;
                }

                box.SelectedItem = chosen ?? (names.Count > 0 ? names[0] : null);

                // Nothing to choose between with one outline; the row still shows which
                // one will be drawn.
                box.IsEnabled = names.Count > 1;

                var preview = new Image
                {
                    Width = PreviewSize,
                    Height = PreviewSize,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(14, 0, 0, 0)
                };

                void RefreshPreview()
                {
                    CommittedOutline outline = FindByName(stored, box.SelectedItem as string);
                    preview.Source = outline == null
                        ? null
                        : OutlineShapeExporter.RenderThumbnail(outline, PreviewPixels);
                }

                box.SelectionChanged += (s, e) => RefreshPreview();
                RefreshPreview();

                var heading = new TextBlock
                {
                    Text = specimen + "  \u2014  " + string.Join(", ", roles),
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(box);
                line.Children.Add(preview);

                var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
                stack.Children.Add(heading);
                stack.Children.Add(line);

                _rows.Add((specimen, box));
                return stack;
            }

            private static CommittedOutline FindByName(List<CommittedOutline> stored, string name)
            {
                if (name == null) return null;

                foreach (var outline in stored)
                    if (string.Equals(outline.Name, name, StringComparison.Ordinal)) return outline;

                return null;
            }

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
                    foreach (var row in _rows)
                        if (row.Box.SelectedItem is string name) Choices[row.Specimen] = name;

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
        }
    }
}