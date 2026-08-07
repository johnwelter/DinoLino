using DinoLino.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino
{
    /// <summary>
    /// Marker shapes offered for plotted points. None hides the overlaid points on
    /// a boxplot; plots whose data *is* points fall back to Circle rather than
    /// drawing nothing.
    /// </summary>
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
        public const string ManualPaletteName = "Manual";
        public const string DefaultPaletteName = "Colorblind friendly";

        /// <summary>Group column the plot colours by, or "None".</summary>
        public string ColourBy { get; set; } = MainWindow.ColourNone;

        /// <summary>Palette the colour levels cycle through.</summary>
        public string Palette { get; set; } = DefaultPaletteName;

        /// Colours used when Palette is "Manual", in cycling order. Entries are
        /// either a named colour or a "#RRGGBB" string.
        public List<string> ManualPalette { get; set; } =
            new List<string> { "Blue", "Orange", "Green" };

        /// Fill for shapes that enclose an area (boxplot boxes, histogram bars)
        /// when nothing is colour-split.
        public string SingleFill { get; set; } = "Blue";

        /// Colour for plotted points when nothing is colour-split. Kept separate
        /// from SingleFill because a scatter plot has points but no filled areas,
        /// and a histogram the reverse.
        public string PointColor { get; set; } = "Blue";

        /// <summary>Ink for box outlines, bar borders, and trend lines.</summary>
        public string Outline { get; set; } = "Black";

        public PlotPointShape PointShape { get; set; } = PlotPointShape.Circle;

        // Blank means "use the generated text", so a user who clears a box gets
        // the default back rather than an empty title.
        public string Title { get; set; } = "";
        public string XAxisTitle { get; set; } = "";
        public string YAxisTitle { get; set; } = "";

        // ---- Named choices ----

        public static readonly string[] FillNames =
        {
            "Blue", "Orange", "Green", "Purple", "Red", "Gray", "Black"
        };

        public static readonly string[] PaletteNames =
        {
            DefaultPaletteName, "Bright", "Muted", "Grayscale", ManualPaletteName
        };

        private static readonly Dictionary<string, Color> FillColors = new()
        {
            ["Blue"] = Color.FromRgb(0x3E, 0x7C, 0xB8),
            ["Orange"] = Color.FromRgb(0xD5, 0x5E, 0x00),
            ["Green"] = Color.FromRgb(0x00, 0x9E, 0x73),
            ["Purple"] = Color.FromRgb(0x88, 0x5E, 0xB0),
            ["Red"] = Color.FromRgb(0xC0, 0x39, 0x2B),
            ["Gray"] = Color.FromRgb(0x88, 0x88, 0x88),
            ["Black"] = Color.FromRgb(0x22, 0x22, 0x22)
        };

        /// A brush for a named colour or a "#RRGGBB" string, at the given alpha.
        /// Anything unrecognised falls back rather than throwing, so a malformed
        /// stored value cannot break a redraw.
        internal static Brush BrushFor(string name, byte alpha)
        {
            Color c;

            if (name != null && FillColors.TryGetValue(name, out var known))
            {
                c = known;
            }
            else if (name != null && name.StartsWith("#"))
            {
                try { c = (Color)ColorConverter.ConvertFromString(name); }
                catch { c = FillColors["Blue"]; }
            }
            else
            {
                c = FillColors["Blue"];
            }

            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        /// <summary>Swatch colour for the dialog's own preview squares.</summary>
        internal static Brush SwatchFor(string name) => BrushFor(name, 0xFF);

        /// Accepts a known colour name or a hex string with or without the leading
        /// hash, and returns it in the form the options store.
        internal static bool TryNormalizeColor(string text, out string name)
        {
            name = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            text = text.Trim();

            foreach (string known in FillNames)
            {
                if (string.Equals(known, text, StringComparison.OrdinalIgnoreCase))
                {
                    name = known;
                    return true;
                }
            }

            if (!text.StartsWith("#")) text = "#" + text;

            try
            {
                var c = (Color)ColorConverter.ConvertFromString(text);
                name = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                return true;
            }
            catch
            {
                return false;
            }
        }

        public Brush SingleFillBrush() => BrushFor(SingleFill, 0xB4);

        public Brush PointBrush() => BrushFor(PointColor, 0xC8);

        public Brush OutlineBrush() => BrushFor(Outline, 0xFF);

        public Brush TrendBrush() => BrushFor(Outline, 0xCC);

        /// <summary>Series colours for the chosen palette, in cycling order.</summary>
        public Brush[] PaletteBrushes()
        {
            // An empty manual list would leave every series brushless, so it falls
            // through to the default rather than drawing nothing.
            if (Palette == ManualPaletteName && ManualPalette.Count > 0)
                return ManualPalette.Select(n => BrushFor(n, 0xC8)).ToArray();

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

        /// Drops a colour-by column that no longer exists, which keeps a stale
        /// choice from silently colouring by the fallback.
        public void PruneMissingColumns()
        {
            if (ColourBy == MainWindow.ColourNone || ColourBy == MainWindow.CategorySpecimen)
                return;

            if (!SpecimenGroups.Columns.Any(
                    c => string.Equals(c, ColourBy, StringComparison.OrdinalIgnoreCase)))
            {
                ColourBy = MainWindow.ColourNone;
            }
        }

        /// Deep copy. The manual palette is copied explicitly, or the dialog's
        /// working copy would edit the live list and Cancel would not undo it.
        public PlotOptions Clone()
        {
            var copy = (PlotOptions)MemberwiseClone();
            copy.ManualPalette = new List<string>(ManualPalette);
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
        public bool SupportsColour;
        public bool SupportsFill;
        public bool SupportsPointColor;
        public bool SupportsPoints;
        public bool SupportsOutline;
        public bool SupportsXAxisTitle;
        public bool SupportsYAxisTitle;

        // Explain a greyed-out row, so "why can't I set this" is answerable from
        // the dialog itself.
        public string ColourNote = "";
        public string FillNote = "";
        public string PointNote = "";
        public string OutlineNote = "";

        public static PlotCapabilities For(string plotType)
        {
            switch (plotType)
            {
                case "Boxplot":
                    // The only plot with both: filled boxes and, when a shape is
                    // chosen, the individual points overlaid on them.
                    return new PlotCapabilities
                    {
                        SupportsColour = true,
                        SupportsFill = true,
                        SupportsPointColor = true,
                        SupportsPoints = true,
                        SupportsOutline = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true
                    };

                case "Scatter plot":
                    return new PlotCapabilities
                    {
                        SupportsColour = true,

                        // Nothing on a scatter plot encloses an area; the markers
                        // take their colour from Point color instead.
                        SupportsFill = false,
                        SupportsPointColor = true,
                        SupportsPoints = true,
                        SupportsOutline = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        FillNote = "Scatter plots have no filled areas \u2014 use Point color."
                    };

                case "Histogram":
                    return new PlotCapabilities
                    {
                        // Splitting bars by colour changes what their heights
                        // mean, so a histogram takes the single fill only.
                        SupportsColour = false,
                        SupportsFill = true,
                        SupportsPointColor = false,
                        SupportsPoints = false,
                        SupportsOutline = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        ColourNote = "Histograms use a single fill.",
                        PointNote = "Histograms draw bars, not points."
                    };

                case "QQ plot":
                    return new PlotCapabilities
                    {
                        SupportsColour = true,
                        SupportsFill = false,
                        SupportsPointColor = true,
                        SupportsPoints = true,

                        // Outline inks the reference line.
                        SupportsOutline = true,
                        SupportsXAxisTitle = true,
                        SupportsYAxisTitle = true,
                        FillNote = "QQ plots have no filled areas \u2014 use Point color."
                    };

                case "Dot plot":
                    return new PlotCapabilities
                    {
                        SupportsColour = true,
                        SupportsFill = false,
                        SupportsPointColor = true,
                        SupportsPoints = true,
                        SupportsOutline = false,
                        SupportsXAxisTitle = true,

                        // The vertical axis is a stack count with no scale drawn.
                        SupportsYAxisTitle = false,
                        FillNote = "Dot plots have no filled areas \u2014 use Point color.",
                        OutlineNote = "Dot plots draw no outlined shapes."
                    };

                default:
                    return new PlotCapabilities();
            }
        }
    }

    /// <summary>
    /// "Advanced +" dialog: colour, fill, titles, and point shape for the current
    /// plot. Edits a copy so Cancel leaves the plot untouched, and disables
    /// whatever the chosen plot type does not use.
    /// </summary>
    internal class PlotAdvancedWindow : Window
    {
        private readonly PlotOptions _working;
        private readonly PlotCapabilities _capabilities;

        private ComboBox _colourByBox;
        private ComboBox _paletteBox;
        private ComboBox _fillBox;
        private ComboBox _pointColorBox;
        private ComboBox _outlineBox;
        private ComboBox _shapeBox;
        private TextBox _titleBox;
        private TextBox _xTitleBox;
        private TextBox _yTitleBox;

        // Manual palette editor.
        private StackPanel _manualPanel;
        private ListBox _manualList;
        private ComboBox _manualAddBox;

        internal PlotAdvancedWindow(PlotOptions current, PlotCapabilities capabilities)
        {
            _working = current.Clone();
            _capabilities = capabilities;

            Title = "Advanced plot options";
            Width = 440;
            Height = 600;
            MinWidth = 380;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(16) };

            var note = new TextBlock
            {
                Text = "Options that do not apply to the current plot type are greyed out.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            // Docked BEFORE the scroll viewer is added, and built once: the bar
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

            panel.Children.Add(SectionHeader("Color"));

            // Colour-by offers the same choices the Sample tab's groups define.
            _colourByBox = new ComboBox { ItemsSource = MainWindow.PlotColourChoices() };
            _colourByBox.SelectedItem =
                ((List<string>)_colourByBox.ItemsSource).Contains(_working.ColourBy)
                    ? _working.ColourBy
                    : MainWindow.ColourNone;
            _colourByBox.SelectionChanged += (s, e) => UpdateColourDependentRows();
            panel.Children.Add(Row("Color by", _colourByBox,
                "Split the plot's colors by specimen or by a group column"));

            _paletteBox = new ComboBox { ItemsSource = PlotOptions.PaletteNames.ToList() };
            _paletteBox.SelectedItem =
                PlotOptions.PaletteNames.Contains(_working.Palette)
                    ? _working.Palette
                    : PlotOptions.DefaultPaletteName;
            _paletteBox.SelectionChanged += (s, e) => UpdateColourDependentRows();
            panel.Children.Add(Row("Palette", _paletteBox,
                "Colors the levels cycle through when coloring is on"));

            panel.Children.Add(Row("Manual colors", BuildManualEditor(),
                "Colors used, in order, when the palette is set to Manual"));

            _fillBox = MakeSwatchBox(_working.SingleFill);
            panel.Children.Add(Row("Fill", _fillBox,
                "Fill for boxes and bars when the plot is not split by color"));

            _pointColorBox = MakeSwatchBox(_working.PointColor);
            panel.Children.Add(Row("Point color", _pointColorBox,
                "Color for plotted points when the plot is not split by color"));

            _outlineBox = MakeSwatchBox(_working.Outline);
            panel.Children.Add(Row("Outline", _outlineBox,
                "Ink for box outlines, bar borders, and trend lines"));

            panel.Children.Add(SectionHeader("Points"));

            _shapeBox = new ComboBox
            {
                ItemsSource = Enum.GetValues(typeof(PlotPointShape)).Cast<PlotPointShape>().ToList(),
                SelectedItem = _working.PointShape
            };
            panel.Children.Add(Row("Shape", _shapeBox,
                "Marker drawn for each point. On a boxplot this overlays the "
                + "individual measurements; None hides them."));

            panel.Children.Add(SectionHeader("Titles"));

            _titleBox = new TextBox { Text = _working.Title, MaxLength = 60 };
            panel.Children.Add(Row("Plot title", _titleBox, "Leave blank to use the generated title"));

            _xTitleBox = new TextBox { Text = _working.XAxisTitle, MaxLength = 40 };
            panel.Children.Add(Row("X axis title", _xTitleBox, "Leave blank for no x axis title"));

            _yTitleBox = new TextBox { Text = _working.YAxisTitle, MaxLength = 40 };
            panel.Children.Add(Row("Y axis title", _yTitleBox, "Leave blank for no y axis title"));

            return panel;
        }

        // The whole editor is enabled or disabled as one unit, which greys every
        // control inside it without wiring each separately.
        private FrameworkElement BuildManualEditor()
        {
            _manualPanel = new StackPanel();

            _manualList = new ListBox { Height = 92, Margin = new Thickness(0, 0, 0, 4) };
            foreach (string name in _working.ManualPalette)
                _manualList.Items.Add(ManualItem(name));
            _manualPanel.Children.Add(_manualList);

            _manualAddBox = MakeSwatchBox(PlotOptions.FillNames[0], includeCustom: true);
            _manualAddBox.Margin = new Thickness(0, 0, 0, 4);
            _manualPanel.Children.Add(_manualAddBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };

            var add = new Button { Content = "Add", Width = 60, Height = 24 };
            add.Click += (s, e) => AddManualColor();
            buttons.Children.Add(add);

            var remove = new Button
            {
                Content = "Remove",
                Width = 68,
                Height = 24,
                Margin = new Thickness(6, 0, 0, 0)
            };
            remove.Click += (s, e) =>
            {
                if (_manualList.SelectedIndex >= 0)
                    _manualList.Items.RemoveAt(_manualList.SelectedIndex);
            };
            buttons.Children.Add(remove);

            var clear = new Button
            {
                Content = "Clear",
                Width = 60,
                Height = 24,
                Margin = new Thickness(6, 0, 0, 0)
            };
            clear.Click += (s, e) => _manualList.Items.Clear();
            buttons.Children.Add(clear);

            _manualPanel.Children.Add(buttons);
            return _manualPanel;
        }

        private void AddManualColor()
        {
            string picked = SwatchValue(_manualAddBox, fallback: null);

            // The Custom entry carries no tag: ask for a hex value instead.
            if (picked == null)
            {
                string entered = TextPromptWindow.Show(
                    this, "Custom color", "Enter a hex color, for example #7C3AED:", "#");

                if (entered == null) return;

                if (!PlotOptions.TryNormalizeColor(entered, out picked))
                {
                    MessageBox.Show(this,
                        "That is not a color this can read.\n\n" +
                        "Use a hex value such as #7C3AED, or pick a named color.",
                        "Custom color", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _manualList.Items.Add(ManualItem(picked));
            _manualList.SelectedIndex = _manualList.Items.Count - 1;
        }

        private static ListBoxItem ManualItem(string name) => new ListBoxItem
        {
            Content = SwatchRow(name),
            Tag = name
        };

        private static StackPanel SwatchRow(string name)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            row.Children.Add(new Rectangle
            {
                Width = 12,
                Height = 12,
                Fill = PlotOptions.SwatchFor(name),
                Stroke = Brushes.Gray,
                StrokeThickness = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center
            });

            return row;
        }

        // Colour name plus its swatch, so the choice is visible without applying it.
        private static ComboBox MakeSwatchBox(string selected, bool includeCustom = false)
        {
            var box = new ComboBox();

            foreach (string name in PlotOptions.FillNames)
                box.Items.Add(new ComboBoxItem { Content = SwatchRow(name), Tag = name });

            // A null tag marks the entry that prompts for a hex value.
            if (includeCustom)
                box.Items.Add(new ComboBoxItem { Content = "Custom\u2026", Tag = null });

            foreach (ComboBoxItem item in box.Items)
            {
                if ((string)item.Tag == selected)
                {
                    box.SelectedItem = item;
                    break;
                }
            }

            if (box.SelectedItem == null && box.Items.Count > 0)
                box.SelectedIndex = 0;

            return box;
        }

        private static string SwatchValue(ComboBox box, string fallback = "Blue")
        {
            if (box.SelectedItem is ComboBoxItem item) return item.Tag as string;
            return fallback;
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
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 8, 0),
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

        // Fixed-size buttons in a horizontal row along the bottom, above nothing
        // else, with a rule separating them from the settings.
        private FrameworkElement BuildButtonBar()
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var ok = new Button
            {
                Content = "OK",
                Width = 92,
                Height = 28,
                IsDefault = true
            };
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
            _colourByBox.IsEnabled = _capabilities.SupportsColour;
            _shapeBox.IsEnabled = _capabilities.SupportsPoints;
            _outlineBox.IsEnabled = _capabilities.SupportsOutline;
            _xTitleBox.IsEnabled = _capabilities.SupportsXAxisTitle;
            _yTitleBox.IsEnabled = _capabilities.SupportsYAxisTitle;

            if (!string.IsNullOrEmpty(_capabilities.ColourNote))
                _colourByBox.ToolTip = _capabilities.ColourNote;
            if (!string.IsNullOrEmpty(_capabilities.PointNote))
                _shapeBox.ToolTip = _capabilities.PointNote;
            if (!string.IsNullOrEmpty(_capabilities.OutlineNote))
                _outlineBox.ToolTip = _capabilities.OutlineNote;

            UpdateColourDependentRows();
        }

        // Two things gate these rows: whether the plot type uses them at all, and
        // whether coloring is currently on, since a palette and a single color are
        // mutually exclusive.
        private void UpdateColourDependentRows()
        {
            bool colouring = _capabilities.SupportsColour &&
                             (_colourByBox.SelectedItem as string) != MainWindow.ColourNone;
            bool manual = (_paletteBox.SelectedItem as string) == PlotOptions.ManualPaletteName;

            _paletteBox.IsEnabled = colouring;
            _manualPanel.IsEnabled = colouring && manual;
            _fillBox.IsEnabled = _capabilities.SupportsFill && !colouring;
            _pointColorBox.IsEnabled = _capabilities.SupportsPointColor && !colouring;

            _paletteBox.ToolTip = colouring
                ? "Colors the levels cycle through"
                : "Set \"Color by\" to use a palette";

            _manualPanel.ToolTip = !colouring
                ? "Set \"Color by\" to use a palette"
                : manual
                    ? "Colors used, in order, for each level"
                    : "Set the palette to Manual to edit these";

            _fillBox.ToolTip = !_capabilities.SupportsFill
                ? (string.IsNullOrEmpty(_capabilities.FillNote)
                    ? "This plot type has no filled areas."
                    : _capabilities.FillNote)
                : colouring
                    ? "Not used while the plot is split by color"
                    : "Fill for boxes and bars";

            _pointColorBox.ToolTip = !_capabilities.SupportsPointColor
                ? "This plot type draws no points."
                : colouring
                    ? "Not used while the plot is split by color"
                    : "Color for plotted points";
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
            {
                target.Palette = _paletteBox.SelectedItem as string ?? PlotOptions.DefaultPaletteName;

                // Committed whenever the palette row is live, not only while
                // Manual is selected, so edits are not lost by switching away.
                target.ManualPalette = _manualList.Items
                    .Cast<ListBoxItem>()
                    .Select(i => (string)i.Tag)
                    .ToList();
            }

            if (_fillBox.IsEnabled) target.SingleFill = SwatchValue(_fillBox);
            if (_pointColorBox.IsEnabled) target.PointColor = SwatchValue(_pointColorBox);
            if (_outlineBox.IsEnabled) target.Outline = SwatchValue(_outlineBox);

            if (_shapeBox.IsEnabled && _shapeBox.SelectedItem is PlotPointShape shape)
                target.PointShape = shape;

            target.Title = _titleBox.Text?.Trim() ?? "";
            if (_xTitleBox.IsEnabled) target.XAxisTitle = _xTitleBox.Text?.Trim() ?? "";
            if (_yTitleBox.IsEnabled) target.YAxisTitle = _yTitleBox.Text?.Trim() ?? "";
        }
    }
}