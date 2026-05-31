using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility.Collapsed;
    }
    public class IntEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int i && parameter is string s && int.TryParse(s, out int p) && i == p;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true && parameter is string s && int.TryParse(s, out int p) ? p : Binding.DoNothing;
    }

    public partial class OutlineControlPanel : UserControl
    {
        private OutlineMode _mode;
        public OutlineControlPanel(OutlineMode mode)
        {
            InitializeComponent();
            _mode = mode;
            DataContext = mode;
        }

        private void ShowEFDDetails_Click(object sender, RoutedEventArgs e)
        {
            EfdHarmonicsBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            // Always regenerate so the coefficients reflect the current harmonic count
            _mode.GenerateMetadata();

            if (_mode.EFDCoefficientsResult == null || _mode.EFDCoefficientsResult.Length == 0)
            {
                MessageBox.Show("No EFD data available. Please draw an outline first.",
                                "EFD Coefficients", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int harmonics = _mode.EfdHarmonics; // use the current setting, not back-calculated

            // ── Tab 1: coefficient text (existing behavior) ──
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Elliptic Fourier Descriptors ({harmonics} harmonics)");
            sb.AppendLine(new string('─', 48));
            for (int h = 0; h < harmonics; h++)
            {
                int k = h * 4;
                sb.AppendLine($"  n={h + 1,2}:  a={_mode.EFDCoefficientsResult[k]:F6}  " +
                              $"b={_mode.EFDCoefficientsResult[k + 1]:F6}  " +
                              $"c={_mode.EFDCoefficientsResult[k + 2]:F6}  " +
                              $"d={_mode.EFDCoefficientsResult[k + 3]:F6}");
            }

            var coeffTextBox = new TextBox
            {
                Text = sb.ToString(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(8),
                FontFamily = new FontFamily("Consolas")
            };

            var exportButton = new Button
            {
                Content = "Export CSV...",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            exportButton.Click += (s, ev) => ExportCoefficientsCsv(harmonics);

            var coeffDock = new DockPanel();
            DockPanel.SetDock(exportButton, Dock.Bottom);
            coeffDock.Children.Add(exportButton);
            coeffDock.Children.Add(coeffTextBox);

            var coeffTab = new TabItem { Header = "Coefficients", Content = coeffDock };

            // ── Tab 2: x(t) and y(t) projection plots ──
            int sampleCount = Math.Max(200, harmonics * 20);
            var (xSeries, ySeries) = SampleXYProjections(harmonics, sampleCount);

            var plotsPanel = new Grid { Margin = new Thickness(8) };
            plotsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            plotsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var xPlot = BuildProjectionPlot("X-Coordinates", xSeries, Brushes.SteelBlue);
            Grid.SetRow(xPlot, 0);
            var yPlot = BuildProjectionPlot("Y-Coordinates", ySeries, Brushes.IndianRed);
            Grid.SetRow(yPlot, 1);

            plotsPanel.Children.Add(xPlot);
            plotsPanel.Children.Add(yPlot);

            var plotsExportButton = new Button
            {
                Content = "Export Plots as PNG...",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            plotsExportButton.Click += (s, ev) => ExportProjectionPlotsPng(harmonics);

            var plotsDock = new DockPanel();
            DockPanel.SetDock(plotsExportButton, Dock.Bottom);
            plotsDock.Children.Add(plotsExportButton);
            plotsDock.Children.Add(plotsPanel);

            var plotsTab = new TabItem { Header = "Projections (x(t), y(t))", Content = plotsDock };

            // ── Assemble tabs and window ──
            var tabs = new TabControl();
            tabs.Items.Add(coeffTab);
            tabs.Items.Add(plotsTab);

            var window = new Window
            {
                Title = "EFD Coefficients",
                Width = 560,
                Height = 520,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Content = tabs
            };

            window.Show();
        }

        // Reconstructs x(t) and y(t) separately from the raw EFD coefficients,
        // using the same harmonic summation as EllipticFourierAnalysis.Reconstruct
        // but keeping the two projections independent for plotting against t.
        // The DC offset (centroid) is intentionally omitted: each series is plotted
        // relative to its own mean, matching the paper's amplitude-vs-t projections.
        private (List<double> x, List<double> y) SampleXYProjections(int harmonics, int sampleCount)
        {
            var coeffs = _mode.EFDCoefficientsResult;
            int maxHarmonics = coeffs.Length / 4;
            harmonics = Math.Min(harmonics, maxHarmonics);

            var xs = new List<double>(sampleCount + 1);
            var ys = new List<double>(sampleCount + 1);

            for (int s = 0; s <= sampleCount; s++)
            {
                double baseAngle = 2.0 * Math.PI * (double)s / sampleCount;
                double cosBase = Math.Cos(baseAngle);
                double sinBase = Math.Sin(baseAngle);

                double cosH = cosBase, sinH = sinBase;
                double x = 0, y = 0;

                for (int h = 1; h <= harmonics; h++)
                {
                    int k = (h - 1) * 4;
                    x += coeffs[k] * cosH + coeffs[k + 1] * sinH;
                    y += coeffs[k + 2] * cosH + coeffs[k + 3] * sinH;

                    // Angle addition to advance to the next harmonic's trig values
                    double newCos = cosBase * cosH - sinBase * sinH;
                    double newSin = sinBase * cosH + cosBase * sinH;
                    cosH = newCos;
                    sinH = newSin;
                }

                xs.Add(x);
                ys.Add(y);
            }

            return (xs, ys);
        }

        // Builds a single labeled plot: a titled box with a polyline of `series`
        // drawn against a t-axis labeled 0, pi/2, pi, 3pi/2, 2pi (paper style),
        // plus a zero baseline. Auto-scales the vertical axis to the series range.
        private Border BuildProjectionPlot(string title, List<double> series, Brush stroke)
        {
            var canvas = new Canvas { ClipToBounds = true, Background = Brushes.Transparent };

            var titleBlock = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4, 2, 0, 2)
            };

            var dock = new DockPanel();
            DockPanel.SetDock(titleBlock, Dock.Top);
            dock.Children.Add(titleBlock);
            dock.Children.Add(canvas);

            // Defer drawing until the canvas has a real size
            canvas.SizeChanged += (s, e) =>
                DrawProjection(canvas, series, stroke);

            canvas.Loaded += (s, e) =>
                DrawProjection(canvas, series, stroke);

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 6),
                Child = dock
            };
        }

        private void DrawProjection(Canvas canvas, List<double> series, Brush stroke)
        {
            canvas.Children.Clear();
            if (series == null || series.Count < 2) return;

            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w < 20 || h < 20) return;

            const double leftPad = 36;   // room for y labels
            const double rightPad = 8;
            const double topPad = 6;
            const double bottomPad = 20; // room for t labels

            double plotW = w - leftPad - rightPad;
            double plotH = h - topPad - bottomPad;
            if (plotW <= 0 || plotH <= 0) return;

            // Vertical range
            double min = double.MaxValue, max = double.MinValue;
            foreach (double v in series) { if (v < min) min = v; if (v > max) max = v; }
            if (max - min < 1e-9) { max += 1; min -= 1; }
            double range = max - min;

            double MapX(int i) => leftPad + plotW * i / (series.Count - 1);
            double MapY(double v) => topPad + plotH * (1.0 - (v - min) / range);

            // Axes box
            var axisBrush = Brushes.Gray;
            canvas.Children.Add(new Line
            {
                X1 = leftPad,
                Y1 = topPad,
                X2 = leftPad,
                Y2 = topPad + plotH,
                Stroke = axisBrush,
                StrokeThickness = 1
            });
            canvas.Children.Add(new Line
            {
                X1 = leftPad,
                Y1 = topPad + plotH,
                X2 = leftPad + plotW,
                Y2 = topPad + plotH,
                Stroke = axisBrush,
                StrokeThickness = 1
            });

            // Zero baseline (if 0 falls within range)
            if (min < 0 && max > 0)
            {
                double zeroY = MapY(0);
                canvas.Children.Add(new Line
                {
                    X1 = leftPad,
                    Y1 = zeroY,
                    X2 = leftPad + plotW,
                    Y2 = zeroY,
                    Stroke = Brushes.LightGray,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                });
            }

            // t-axis tick labels: 0, pi/2, pi, 3pi/2, 2pi
            string[] tLabels = { "0", "π/2", "π", "3π/2", "2π" };
            for (int t = 0; t < tLabels.Length; t++)
            {
                double frac = t / 4.0;
                double tx = leftPad + plotW * frac;
                canvas.Children.Add(new Line
                {
                    X1 = tx,
                    Y1 = topPad + plotH,
                    X2 = tx,
                    Y2 = topPad + plotH + 3,
                    Stroke = axisBrush,
                    StrokeThickness = 1
                });
                var lbl = new TextBlock { Text = tLabels[t], FontSize = 10, Foreground = Brushes.Gray };
                lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(lbl, tx - lbl.DesiredSize.Width / 2);
                Canvas.SetTop(lbl, topPad + plotH + 3);
                canvas.Children.Add(lbl);
            }

            // y-axis min/max labels
            void AddYLabel(double value, double y)
            {
                var lbl = new TextBlock
                {
                    Text = value.ToString("F0", CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Foreground = Brushes.Gray
                };
                lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(lbl, leftPad - lbl.DesiredSize.Width - 4);
                Canvas.SetTop(lbl, y - lbl.DesiredSize.Height / 2);
                canvas.Children.Add(lbl);
            }
            AddYLabel(max, MapY(max));
            AddYLabel(min, MapY(min));

            // The series polyline
            var poly = new Polyline { Stroke = stroke, StrokeThickness = 1.5 };
            for (int i = 0; i < series.Count; i++)
                poly.Points.Add(new Point(MapX(i), MapY(series[i])));
            canvas.Children.Add(poly);
        }

        // Walks up the visual/logical tree to find the owning MainWindow and reads
        // the current specimen name. Falls back to a default if not found, so export
        // still works even if the panel is hosted outside the main window.
        private string GetSpecimenName()
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            string name = mainWindow?.SpecimenManager?.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? "Specimen" : name;
        }

        // Exports the current EFD coefficients to a CSV file. The first row holds the
        // specimen name; the second row is the column header; each subsequent row is
        // one harmonic with its four coefficients.
        private void ExportCoefficientsCsv(int harmonics)
        {
            var coeffs = _mode.EFDCoefficientsResult;
            if (coeffs == null || coeffs.Length == 0)
            {
                MessageBox.Show("No EFD data available to export.",
                                "Export CSV", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string specimenName = GetSpecimenName();

            var dialog = new SaveFileDialog
            {
                Title = "Export EFD Coefficients",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = "csv",
                FileName = SanitizeFileName(specimenName) + "_EFD.csv"
            };

            if (dialog.ShowDialog() != true) return;

            var csv = new System.Text.StringBuilder();

            // Row 1: specimen name (escaped, in case it contains a comma or quote)
            csv.AppendLine(CsvEscape(specimenName));

            // Row 2: column headers
            csv.AppendLine("harmonic,a,b,c,d");

            // Rows 3+: one row per harmonic
            int available = coeffs.Length / 4;
            int count = Math.Min(harmonics, available);
            for (int h = 0; h < count; h++)
            {
                int k = h * 4;
                csv.AppendLine(string.Join(",",
                    (h + 1).ToString(CultureInfo.InvariantCulture),
                    coeffs[k].ToString("R", CultureInfo.InvariantCulture),
                    coeffs[k + 1].ToString("R", CultureInfo.InvariantCulture),
                    coeffs[k + 2].ToString("R", CultureInfo.InvariantCulture),
                    coeffs[k + 3].ToString("R", CultureInfo.InvariantCulture)));
            }

            try
            {
                File.WriteAllText(dialog.FileName, csv.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not write the file:\n{ex.Message}",
                                "Export CSV", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Wraps a field in quotes and doubles internal quotes if it contains
        // a comma, quote, or newline — standard CSV escaping.
        private static string CsvEscape(string field)
        {
            if (field == null) return "";
            bool needsQuoting = field.Contains(',') || field.Contains('"') ||
                                field.Contains('\n') || field.Contains('\r');
            if (!needsQuoting) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        // Strips characters that are invalid in file names so the suggested
        // filename derived from the specimen name is always valid.
        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // Renders both projection plots to a single PNG at a fixed export size.
        // Builds a fresh, off-screen copy of the plots rather than capturing the
        // on-screen panel, so export works regardless of the current window size
        // or whether the tab has been laid out yet.
        private void ExportProjectionPlotsPng(int harmonics)
        {
            var coeffs = _mode.EFDCoefficientsResult;
            if (coeffs == null || coeffs.Length == 0)
            {
                MessageBox.Show("No EFD data available to export.",
                                "Export Plots", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string specimenName = GetSpecimenName();

            var dialog = new SaveFileDialog
            {
                Title = "Export Projection Plots",
                Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
                DefaultExt = "png",
                FileName = SanitizeFileName(specimenName) + "_projections.png"
            };

            if (dialog.ShowDialog() != true) return;

            // Fixed export dimensions (logical pixels), rendered at 2x for crispness.
            const double exportWidth = 700;
            const double exportHeight = 500;
            const double scale = 2.0;

            int sampleCount = Math.Max(200, harmonics * 20);
            var (xSeries, ySeries) = SampleXYProjections(harmonics, sampleCount);

            // Build an off-screen panel identical to the on-screen one
            var panel = new Grid
            {
                Width = exportWidth,
                Height = exportHeight,
                Background = Brushes.White,
                Margin = new Thickness(8)
            };
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var xPlot = BuildProjectionPlot("X-Coordinates", xSeries, Brushes.SteelBlue);
            Grid.SetRow(xPlot, 0);
            var yPlot = BuildProjectionPlot("Y-Coordinates", ySeries, Brushes.IndianRed);
            Grid.SetRow(yPlot, 1);
            panel.Children.Add(xPlot);
            panel.Children.Add(yPlot);

            // Force layout so the canvases get a real size and draw themselves
            var size = new Size(exportWidth, exportHeight);
            panel.Measure(size);
            panel.Arrange(new Rect(size));
            panel.UpdateLayout();

            try
            {
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(exportWidth * scale), (int)(exportHeight * scale),
                    96 * scale, 96 * scale,
                    PixelFormats.Pbgra32);
                rtb.Render(panel);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));

                using var stream = File.Create(dialog.FileName);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not write the image:\n{ex.Message}",
                                "Export Plots", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}