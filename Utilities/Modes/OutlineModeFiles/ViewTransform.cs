using System;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// The mapping between IMAGE space (pixel coordinates of the loaded bitmap)
    /// and CANVAS space (the workspace the user sees, after zoom and pan).
    ///
    ///     canvas = image * Scale + Offset
    ///
    /// Replaces the four independent ScaleX/ScaleY/OffsetX/OffsetY properties:
    /// an immutable value type means the four numbers can never be observed
    /// half-updated (e.g. new scale with an old offset mid-zoom), and a single
    /// property-changed notification covers the whole transform.
    /// </summary>
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

        /// <summary>True when the transform can be inverted (both scales positive).</summary>
        public bool IsValid => ScaleX > 0 && ScaleY > 0;

        public Point ImageToCanvas(Point image) =>
            new Point(image.X * ScaleX + OffsetX, image.Y * ScaleY + OffsetY);

        public Point CanvasToImage(Point canvas) =>
            new Point((canvas.X - OffsetX) / ScaleX, (canvas.Y - OffsetY) / ScaleY);

        public ViewTransform WithScale(double scaleX, double scaleY) =>
            new ViewTransform(scaleX, scaleY, OffsetX, OffsetY);

        public ViewTransform WithOffset(double offsetX, double offsetY) =>
            new ViewTransform(ScaleX, ScaleY, offsetX, offsetY);

        public bool Equals(ViewTransform other) =>
            ScaleX == other.ScaleX && ScaleY == other.ScaleY &&
            OffsetX == other.OffsetX && OffsetY == other.OffsetY;

        public override bool Equals(object obj) => obj is ViewTransform t && Equals(t);

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