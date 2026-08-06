using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DinoLino
{
    public partial class FontWindow : Window
    {
        public Action<double> OnFontSizeChanged;
        public Action<FontFamily> OnFontFamilyChanged;

        private TextBox _fontSizeTextBox;
        private DispatcherTimer _fontSizeTimer;
        private double _lastValidSize = 14;

        public FontWindow(double currentSize, FontFamily currentFamily)
        {
            InitializeComponent();

            _lastValidSize = currentSize;

            // Debounce timer — applies size 500ms after user stops typing
            _fontSizeTimer = new DispatcherTimer();
            _fontSizeTimer.Interval = TimeSpan.FromMilliseconds(500);
            _fontSizeTimer.Tick += (s, e) =>
            {
                _fontSizeTimer.Stop();
                if (_fontSizeTextBox != null &&
                    double.TryParse(_fontSizeTextBox.Text, out double size) &&
                    size >= 10 && size <= 50)
                {
                    _lastValidSize = size;
                    ApplyFontSize(size);
                }
                else if (_fontSizeTextBox != null)
                {
                    // Reset to last valid value if out of range
                    _fontSizeTextBox.Text = _lastValidSize.ToString("0");
                }
            };

            // Hook into the inner TextBox once the ComboBox is rendered
            UI_FontSizeCombo.Loaded += (s, e) =>
            {
                _fontSizeTextBox = UI_FontSizeCombo.Template
                    .FindName("PART_EditableTextBox", UI_FontSizeCombo) as TextBox;

                if (_fontSizeTextBox != null)
                {
                    _fontSizeTextBox.Text = currentSize.ToString("0");
                    _fontSizeTextBox.TextChanged += (ts, te) =>
                    {
                        _fontSizeTimer.Stop();
                        _fontSizeTimer.Start();
                    };
                }
            };

            // Set font family ComboBox
            foreach (ComboBoxItem item in UI_FontTypeCombo.Items)
            {
                if (item.Content.ToString() == currentFamily.Source)
                {
                    UI_FontTypeCombo.SelectedItem = item;
                    break;
                }
            }

            if (UI_FontTypeCombo.SelectedItem == null)
                UI_FontTypeCombo.SelectedIndex = 0;
        }

        private void FontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fontSizeTextBox == null) return; // guard for initialization ordering

            if (UI_FontSizeCombo.SelectedItem is ComboBoxItem item &&
                double.TryParse(item.Content.ToString(), out double size) &&
                size >= 10 && size <= 50)
            {
                _fontSizeTimer.Stop();
                _lastValidSize = size;
                ApplyFontSize(size);
            }
        }

        private void ApplyFontSize(double size)
        {
            if (_fontSizeTextBox == null) return; // guard for initialization ordering

            _fontSizeTextBox.Text = size.ToString("0");
            OnFontSizeChanged?.Invoke(size);
        }

        private void FontType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UI_FontTypeCombo.SelectedItem is ComboBoxItem item)
                OnFontFamilyChanged?.Invoke(new FontFamily(item.Content.ToString()));
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}