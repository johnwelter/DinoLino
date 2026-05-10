using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DinoLino
{
    public partial class PictureAdjustmentWindow : Window
    {
        public Action<double, double, double> OnAdjustmentChanged;

        public PictureAdjustmentWindow(double contrast, double brightness, double saturation)
        {
            InitializeComponent();
            UI_ContrastSlider.Value = contrast;
            UI_BrightnessSlider.Value = brightness;
            UI_SaturationSlider.Value = saturation;
        }

        private void Contrast_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (UI_ContrastText == null) return;
            UI_ContrastText.Text = e.NewValue.ToString("0");
            FireCallback();
        }

        private void Contrast_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && double.TryParse(UI_ContrastText.Text, out double val)
                && val >= -100 && val <= 100)
                UI_ContrastSlider.Value = val;
        }

        private void Contrast_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(UI_ContrastText.Text, out double val) && val >= -100 && val <= 100)
                UI_ContrastSlider.Value = val;
            else
                UI_ContrastText.Text = UI_ContrastSlider.Value.ToString("0");
        }

        private void Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (UI_BrightnessText == null) return;
            UI_BrightnessText.Text = e.NewValue.ToString("0");
            FireCallback();
        }

        private void Brightness_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && double.TryParse(UI_BrightnessText.Text, out double val)
                && val >= -100 && val <= 100)
                UI_BrightnessSlider.Value = val;
        }

        private void Brightness_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(UI_BrightnessText.Text, out double val) && val >= -100 && val <= 100)
                UI_BrightnessSlider.Value = val;
            else
                UI_BrightnessText.Text = UI_BrightnessSlider.Value.ToString("0");
        }

        private void Saturation_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (UI_SaturationText == null) return;
            UI_SaturationText.Text = e.NewValue.ToString("0");
            FireCallback();
        }

        private void Saturation_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && double.TryParse(UI_SaturationText.Text, out double val)
                && val >= -100 && val <= 100)
                UI_SaturationSlider.Value = val;
        }

        private void Saturation_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(UI_SaturationText.Text, out double val) && val >= -100 && val <= 100)
                UI_SaturationSlider.Value = val;
            else
                UI_SaturationText.Text = UI_SaturationSlider.Value.ToString("0");
        }

        private void FireCallback()
        {
            OnAdjustmentChanged?.Invoke(
                UI_ContrastSlider.Value,
                UI_BrightnessSlider.Value,
                UI_SaturationSlider.Value);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}