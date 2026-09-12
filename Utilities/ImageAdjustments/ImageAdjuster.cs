using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DinoLino
{
    /// <summary>
    /// Caches an image and produces adjusted or downsampled writeable bitmaps.
    /// </summary>
    public class ImageAdjuster
    {
        // =====================
        // Cached source image
        // =====================

        private byte[] _originalPixels;
        private int _originalStride;
        private int _originalPixelWidth;
        private int _originalPixelHeight;
        private PixelFormat _originalPixelFormat;
        private double _dpiX;
        private double _dpiY;

        // =====================
        // Deferred adjustment
        // =====================

        private readonly DispatcherTimer _adjustmentTimer;
        private double _pendingContrast;
        private double _pendingBrightness;
        private double _pendingSaturation;

        public bool HasImage => _originalPixels != null;

        /// <summary>
        /// Raised when a new adjusted bitmap is ready.
        /// </summary>
        public Action<WriteableBitmap> OnAdjustmentApplied;

        public ImageAdjuster()
        {
            _adjustmentTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };

            // Coalesce rapid slider updates into one render pass.
            _adjustmentTimer.Tick += (s, e) =>
            {
                _adjustmentTimer.Stop();
                var result = Apply(_pendingContrast, _pendingBrightness, _pendingSaturation);
                if (result != null)
                    OnAdjustmentApplied?.Invoke(result);
            };
        }

        // =====================
        // Cache source pixels
        // =====================

        /// <summary>
        /// Stores the current bitmap pixels so later adjustments can be applied from the same source.
        /// </summary>
        public void CacheImage(BitmapSource image)
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

        /// <summary>
        /// Queues a new adjustment request and delays rendering slightly to avoid repeated work.
        /// </summary>
        public void RequestAdjustment(double contrast, double brightness, double saturation)
        {
            _pendingContrast = contrast;
            _pendingBrightness = brightness;
            _pendingSaturation = saturation;
            _adjustmentTimer.Stop();
            _adjustmentTimer.Start();
        }

        /// Renders an adjustment straight away instead of waiting for the coalescing
        /// timer. Used when the cached image itself has just been replaced, where the
        /// delay would leave the unadjusted pixels on screen in the meantime.
        public void ApplyNow(double contrast, double brightness, double saturation)
        {
            _pendingContrast = contrast;
            _pendingBrightness = brightness;
            _pendingSaturation = saturation;

            // Any queued render would work from these same values, so it has nothing
            // left to do.
            _adjustmentTimer.Stop();

            var result = Apply(contrast, brightness, saturation);
            if (result != null)
                OnAdjustmentApplied?.Invoke(result);
        }

        // =====================
        // Adjustment pipeline
        // =====================

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

                    // WPF commonly stores pixels in BGR(A) order.
                    double b = _originalPixels[i + 0] / 255.0;
                    double g = _originalPixels[i + 1] / 255.0;
                    double r = _originalPixels[i + 2] / 255.0;

                    // Blend toward luminance to reduce or increase saturation.
                    double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                    double satFactor = 1.0 + saturation;
                    r = lum + (r - lum) * satFactor;
                    g = lum + (g - lum) * satFactor;
                    b = lum + (b - lum) * satFactor;

                    // Apply brightness, then contrast around the mid-point.
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

        // =====================
        // Downsampling
        // =====================

        private long CountPixels()
        {
            if (!HasImage) return 0;
            return (long)_originalPixelWidth * _originalPixelHeight;
        }

        /// <summary>
        /// Creates a scaled-down copy of the cached image using nearest-neighbor sampling.
        /// </summary>
        public WriteableBitmap DownSample(long targetPixelCount)
        {
            if (!HasImage) return null;

            long current = CountPixels();
            if (targetPixelCount <= 0 || targetPixelCount >= current) return null;

            // Scale is chosen so the new area is approximately targetPixelCount.
            double scale = Math.Sqrt((double)targetPixelCount / current);
            int newWidth = Math.Max(1, (int)Math.Round(_originalPixelWidth * scale));
            int newHeight = Math.Max(1, (int)Math.Round(_originalPixelHeight * scale));

            int bytesPerPixel = (_originalPixelFormat.BitsPerPixel + 7) / 8;
            int newStride = (newWidth * _originalPixelFormat.BitsPerPixel + 7) / 8;
            byte[] newPixels = new byte[newStride * newHeight];

            Parallel.For(0, newHeight, y =>
            {
                // Map each output row back to a source row.
                int srcY = (int)Math.Floor((double)y / newHeight * _originalPixelHeight);
                srcY = Math.Min(srcY, _originalPixelHeight - 1);

                for (int x = 0; x < newWidth; x++)
                {
                    // Map each output column back to a source column.
                    int srcX = (int)Math.Floor((double)x / newWidth * _originalPixelWidth);
                    srcX = Math.Min(srcX, _originalPixelWidth - 1);

                    int srcIndex = srcY * _originalStride + srcX * bytesPerPixel;
                    int dstIndex = y * newStride + x * bytesPerPixel;

                    for (int c = 0; c < bytesPerPixel; c++)
                        newPixels[dstIndex + c] = _originalPixels[srcIndex + c];
                }
            });

            var wb = new WriteableBitmap(newWidth, newHeight, _dpiX, _dpiY, _originalPixelFormat, null);
            wb.WritePixels(new Int32Rect(0, 0, newWidth, newHeight), newPixels, newStride, 0);
            return wb;
        }
    }
}