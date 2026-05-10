using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DinoLino
{
    public class ImageAdjuster
    {
        private byte[] _originalPixels;
        private int _originalStride;
        private int _originalPixelWidth;
        private int _originalPixelHeight;
        private PixelFormat _originalPixelFormat;
        private double _dpiX;
        private double _dpiY;

        private DispatcherTimer _adjustmentTimer;
        private double _pendingContrast;
        private double _pendingBrightness;
        private double _pendingSaturation;    

        public bool HasImage => _originalPixels != null;

        // Callback so ImageAdjuster can tell MainWindow to update the image source
        public Action<WriteableBitmap> OnAdjustmentApplied;

        public ImageAdjuster()
        {
            _adjustmentTimer = new DispatcherTimer();
            _adjustmentTimer.Interval = TimeSpan.FromMilliseconds(80);
            _adjustmentTimer.Tick += (s, e) =>
            {
                _adjustmentTimer.Stop();
                var result = Apply(_pendingContrast, _pendingBrightness, _pendingSaturation);
                if (result != null)
                    OnAdjustmentApplied?.Invoke(result);
            };
        }

        public void CacheImage(BitmapImage image)
        {
            if (image == null) return;

            _originalPixelWidth = image.PixelWidth;
            _originalPixelHeight = image.PixelHeight;
            _originalPixelFormat = image.Format;
            _originalStride = (_originalPixelWidth * image.Format.BitsPerPixel + 7) / 8;
            _originalPixels = new byte[_originalStride * _originalPixelHeight];
            _dpiX = image.DpiX;
            _dpiY = image.DpiY;
            image.CopyPixels(_originalPixels, _originalStride, 0);
        }

        public void RequestAdjustment(double contrast, double brightness, double saturation)
        {
            _pendingContrast = contrast;
            _pendingBrightness = brightness;
            _pendingSaturation = saturation;
            _adjustmentTimer.Stop();
            _adjustmentTimer.Start();
        }

        private WriteableBitmap Apply(double contrast, double brightness, double saturation)
        {
            if (!HasImage) return null;
            double contrastFactor = 1.0 + contrast;
            byte[] adjustedPixels = new byte[_originalPixels.Length];
            int bytesPerPixel = (_originalPixelFormat.BitsPerPixel + 7) / 8;

            Parallel.For(0, _originalPixelHeight, y =>
            {
                int rowStart = y * _originalStride;
                for (int x = 0; x < _originalPixelWidth; x++)
                {
                    int i = rowStart + x * bytesPerPixel;

                    // Read channels (WPF bitmaps are typically BGR order)
                    double b = _originalPixels[i + 0] / 255.0;
                    double g = _originalPixels[i + 1] / 255.0;
                    double r = _originalPixels[i + 2] / 255.0;

                    // Apply saturation by blending toward luminance (grayscale)
                    double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                    double satFactor = 1.0 + saturation;
                    r = lum + (r - lum) * satFactor;
                    g = lum + (g - lum) * satFactor;
                    b = lum + (b - lum) * satFactor;

                    // Apply brightness and contrast to each channel
                    double[] channels = { b, g, r };
                    for (int c = 0; c < 3; c++)
                    {
                        double val = channels[c];
                        val += brightness;
                        val = (val - 0.5) * contrastFactor + 0.5;
                        val = Math.Max(0, Math.Min(1, val));
                        adjustedPixels[i + c] = (byte)(val * 255);
                    }

                    if (bytesPerPixel == 4)
                        adjustedPixels[i + 3] = _originalPixels[i + 3];
                }
            });

            var wb = new WriteableBitmap(_originalPixelWidth, _originalPixelHeight,
                                          _dpiX, _dpiY, _originalPixelFormat, null);
            wb.WritePixels(new Int32Rect(0, 0, _originalPixelWidth, _originalPixelHeight),
                           adjustedPixels, _originalStride, 0);
            return wb;
        }
    }
}