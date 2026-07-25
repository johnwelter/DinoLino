using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities
{
    /// <summary>Immutable mapping between image-space and canvas-space coordinates.</summary>
    /// <remarks>
    /// Canvas coordinates are computed as:
    /// canvas = image * Scale + Offset
    /// </remarks>
    public readonly struct ViewTransform : IEquatable<ViewTransform>
    {
        public static readonly ViewTransform Identity = new ViewTransform(1, 1, 0, 0);

        public double ScaleX { get; }
        public double ScaleY { get; }
        public double OffsetX { get; }
        public double OffsetY { get; }

        public ViewTransform(double scaleX, double scaleY, double offsetX, double offsetY)
        {
            ScaleX = scaleX;
            ScaleY = scaleY;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        /// <summary>Returns true when the transform can be inverted.</summary>
        public bool IsValid => ScaleX > 0 && ScaleY > 0;

        /// <summary>Converts a point from image space to canvas space.</summary>
        public Point ImageToCanvas(Point image) =>
            new Point(image.X * ScaleX + OffsetX, image.Y * ScaleY + OffsetY);

        /// <summary>Converts a point from canvas space to image space.</summary>
        public Point CanvasToImage(Point canvas) =>
            new Point((canvas.X - OffsetX) / ScaleX, (canvas.Y - OffsetY) / ScaleY);

        /// Converts a point from cropped image space to canvas space, where (cropX,
        /// cropY) is the full-image position of the crop's top-left pixel.
        public Point ImageToCanvas(Point cropped, double cropX, double cropY) =>
            ImageToCanvas(new Point(cropped.X + cropX, cropped.Y + cropY));

        /// Converts canvas coordinates into the cropped image space whose top-left
        /// pixel sits at (cropX, cropY) in the full image.
        public Point CanvasToImage(Point canvas, double cropX, double cropY)
        {
            Point image = CanvasToImage(canvas);
            return new Point(image.X - cropX, image.Y - cropY);
        }

        /// <summary>Creates a copy with updated scale values.</summary>
        public ViewTransform WithScale(double scaleX, double scaleY) =>
            new ViewTransform(scaleX, scaleY, OffsetX, OffsetY);

        /// <summary>Creates a copy with updated offset values.</summary>
        public ViewTransform WithOffset(double offsetX, double offsetY) =>
            new ViewTransform(ScaleX, ScaleY, offsetX, offsetY);

        /// <summary>Compares transform values for equality.</summary>
        public bool Equals(ViewTransform other) =>
            ScaleX == other.ScaleX && ScaleY == other.ScaleY &&
            OffsetX == other.OffsetX && OffsetY == other.OffsetY;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is ViewTransform t && Equals(t);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = ScaleX.GetHashCode();
                hash = (hash * 397) ^ ScaleY.GetHashCode();
                hash = (hash * 397) ^ OffsetX.GetHashCode();
                hash = (hash * 397) ^ OffsetY.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(ViewTransform a, ViewTransform b) => a.Equals(b);
        public static bool operator !=(ViewTransform a, ViewTransform b) => !a.Equals(b);
    }
}

namespace DinoLino.Utilities.Modes
{
    /// Narrow outline-tool context exposed to erase, smooth, and hand-draw helpers.
    public interface IOutlineToolContext
    {
        /// Current outline in canvas coordinates, or null if no outline exists yet.
        Polyline ActivePolyline { get; }

        /// <summary>Current canvas-to-image transform, including zoom and pan.</summary>
        ViewTransform Transform { get; }

        /// <summary>Simplification tolerance shared with the auto-generated outline.</summary>
        double SimplifyEpsilon { get; }

        /// <summary>Stroke brush used for committed outlines and previews.</summary>
        Brush LineColor { get; }

        /// <summary>True when an image is loaded and hand-draw is allowed to start.</summary>
        bool HasImage { get; }

        /// <summary>True when hand-draw is the active tool.</summary>
        bool IsHandDrawActive { get; }

        /// <summary>Starts a new hand-drawn stroke and clears any stale outline results.</summary>
        void OnHandStrokeStarted();

        /// Commits a finished outline through the same path used by automatic tracing.
        void CommitOutline(Polyline outline);
    }

    /// <summary>Shared outline rendering constants and factories.</summary>
    internal static class OutlineVisuals
    {
        internal static readonly DoubleCollection PreviewDashes = CreateFrozenDashes(4, 2);

        private static DoubleCollection CreateFrozenDashes(params double[] values)
        {
            var d = new DoubleCollection(values);
            d.Freeze();
            return d;
        }

        /// <summary>Creates an empty outline polyline in the standard stroke style.</summary>
        internal static Polyline CreateOutlinePolyline(Brush stroke, bool dashed = false)
        {
            var poly = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = 2,
                FillRule = FillRule.EvenOdd
            };

            if (dashed) poly.StrokeDashArray = PreviewDashes;
            return poly;
        }
    }

    /// Minimal observable base class for tool parameter bindings, with the re-entrancy
    /// guard the drag-driven tools share.
    public abstract class ObservableToolBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private bool _dragInProgress;

        /// <summary>Claims the drag guard.</summary>
        protected bool TryBeginDrag()
        {
            if (_dragInProgress) return false;
            _dragInProgress = true;
            return true;
        }

        /// <summary>Releases the drag guard.</summary>
        protected void EndDrag() => _dragInProgress = false;
    }
}