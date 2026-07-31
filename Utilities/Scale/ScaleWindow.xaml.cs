using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DinoLino
{
    public partial class ScaleWindow : Window
    {
        public double LengthValue { get; private set; }
        public string SelectedUnit { get; private set; } = "mm";

        public ScaleWindow() => InitializeComponent();

        private void LengthBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            string proposed = ((TextBox)sender).Text + e.Text;   // digits + one decimal point
            e.Handled = !Regex.IsMatch(proposed, @"^\d*\.?\d*$");
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(UI_LengthBox.Text, NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out double val) || val <= 0)
            {
                MessageBox.Show("Please enter a positive number.", "Invalid length",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LengthValue = val;
            SelectedUnit = (UI_UnitBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "mm";
            DialogResult = true;   // closes the modal and returns true
        }
    }
}