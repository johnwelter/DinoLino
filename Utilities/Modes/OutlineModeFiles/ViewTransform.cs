using System;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Immutable mapping between image-space and canvas-space coordinates.
    /// </summary>
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

        /// <summary>
        /// Returns true when the transform can be inverted.
        /// </summary>
        public bool IsValid => ScaleX > 0 && ScaleY > 0;

        /// <summary>
        /// Converts a point from image space to canvas space.
        /// </summary>
        public Point ImageToCanvas(Point image) =>
            new Point(image.X * ScaleX + OffsetX, image.Y * ScaleY + OffsetY);

        /// <summary>
        /// Converts a point from canvas space to image space.
        /// </summary>
        public Point CanvasToImage(Point canvas) =>
            new Point((canvas.X - OffsetX) / ScaleX, (canvas.Y - OffsetY) / ScaleY);

        /// <summary>
        /// Creates a copy with updated scale values.
        /// </summary>
        public ViewTransform WithScale(double scaleX, double scaleY) =>
            new ViewTransform(scaleX, scaleY, OffsetX, OffsetY);

        /// <summary>
        /// Creates a copy with updated offset values.
        /// </summary>
        public ViewTransform WithOffset(double offsetX, double offsetY) =>
            new ViewTransform(ScaleX, ScaleY, offsetX, offsetY);

        /// <summary>
        /// Compares transform values for equality.
        /// </summary>
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