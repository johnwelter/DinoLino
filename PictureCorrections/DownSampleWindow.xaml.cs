using System;
using System.Windows;
using System.Windows.Input;

namespace DinoLino
{
    public partial class DownSampleWindow : Window
    {
        // Fired only when the user clicks Accept; passes the new total pixel count
        public Action<long> OnPixelsChanged;

        private long _originalPixelCount;
        private long _pendingPixelCount;
        private bool _isSyncing = false; // prevents cross-update loops

        public DownSampleWindow(long originalPixelCount)
        {
            InitializeComponent();
            _originalPixelCount = originalPixelCount;
            _pendingPixelCount = originalPixelCount;

            UI_PixelNumberBox.Text = originalPixelCount.ToString();
            UI_PixelPercentBox.Text = "100";
            UpdateCurrentPixelCountLabel(_pendingPixelCount);
        }

        // --- Pixel Number Box ---

        private void PixelNumber_KeyUp(object sender, KeyEventArgs e)
        {
            if (_isSyncing) return;
            if (long.TryParse(UI_PixelNumberBox.Text, out long pixels))
            {
                pixels = Math.Max(1, Math.Min(pixels, _originalPixelCount));
                _pendingPixelCount = pixels;

                // Sync the percent box
                _isSyncing = true;
                double pct = (double)pixels / _originalPixelCount * 100.0;
                UI_PixelPercentBox.Text = Math.Round(pct, 2).ToString();
                _isSyncing = false;
            }
        }

        private void PixelNumber_LostFocus(object sender, RoutedEventArgs e)
        {
            // Clamp and reformat on focus loss
            if (long.TryParse(UI_PixelNumberBox.Text, out long pixels))
            {
                pixels = Math.Max(1, Math.Min(pixels, _originalPixelCount));
                _pendingPixelCount = pixels;
                UI_PixelNumberBox.Text = pixels.ToString();

                _isSyncing = true;
                double pct = (double)pixels / _originalPixelCount * 100.0;
                UI_PixelPercentBox.Text = Math.Round(pct, 2).ToString();
                _isSyncing = false;
            }
            else
            {
                UI_PixelNumberBox.Text = _pendingPixelCount.ToString();
            }
        }

        // --- Pixel Percent Box ---

        private void PixelPercent_KeyUp(object sender, KeyEventArgs e)
        {
            if (_isSyncing) return;
            if (double.TryParse(UI_PixelPercentBox.Text, out double pct))
            {
                pct = Math.Max(0.001, Math.Min(pct, 100.0));
                long pixels = (long)Math.Round(_originalPixelCount * pct / 100.0);
                pixels = Math.Max(1, Math.Min(pixels, _originalPixelCount));
                _pendingPixelCount = pixels;

                // Sync the pixel number box
                _isSyncing = true;
                UI_PixelNumberBox.Text = pixels.ToString();
                _isSyncing = false;
            }
        }

        private void PixelPercent_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(UI_PixelPercentBox.Text, out double pct))
            {
                pct = Math.Max(0.001, Math.Min(pct, 100.0));
                long pixels = (long)Math.Round(_originalPixelCount * pct / 100.0);
                pixels = Math.Max(1, Math.Min(pixels, _originalPixelCount));
                _pendingPixelCount = pixels;

                UI_PixelPercentBox.Text = Math.Round(pct, 2).ToString();

                _isSyncing = true;
                UI_PixelNumberBox.Text = pixels.ToString();
                _isSyncing = false;
            }
            else
            {
                // Revert to last valid state
                double currentPct = (double)_pendingPixelCount / _originalPixelCount * 100.0;
                UI_PixelPercentBox.Text = Math.Round(currentPct, 2).ToString();
            }
        }

        // --- Buttons ---

        private void Reset_PixelCount(object sender, RoutedEventArgs e)
        {
            _pendingPixelCount = _originalPixelCount;
            UI_PixelNumberBox.Text = _originalPixelCount.ToString();
            UI_PixelPercentBox.Text = "100";
            UpdateCurrentPixelCountLabel(_originalPixelCount);
            OnPixelsChanged?.Invoke(_originalPixelCount);
        }

        private void Accept_PixelCount(object sender, RoutedEventArgs e)
        {
            UpdateCurrentPixelCountLabel(_pendingPixelCount);
            OnPixelsChanged?.Invoke(_pendingPixelCount);
        }

        private void UpdateCurrentPixelCountLabel(long count)
        {
            UI_CurrentPixelCountLabel.Content = count.ToString("N0");
        }
    }
}