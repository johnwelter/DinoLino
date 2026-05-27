using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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

            var window = new Window
            {
                Title = "EFD Coefficients",
                Width = 480,
                Height = 400,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this)
            };

            var textBox = new TextBox
            {
                Text = sb.ToString(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(8)
            };

            window.Content = textBox;
            window.Show();
        }
    }
}