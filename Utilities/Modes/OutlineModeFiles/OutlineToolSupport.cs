using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Narrow outline-tool context exposed to erase, smooth, and hand-draw helpers.
    /// </summary>
    public interface IOutlineToolContext
    {
        /// <summary>
        /// Current outline in canvas coordinates, or null if no outline exists yet.
        /// </summary>
        Polyline ActivePolyline { get; }

        /// <summary>
        /// Current canvas-to-image transform, including zoom and pan.
        /// </summary>
        ViewTransform Transform { get; }

        /// <summary>
        /// Simplification tolerance shared with the auto-generated outline.
        /// </summary>
        double SimplifyEpsilon { get; }

        /// <summary>
        /// Stroke brush used for committed outlines and previews.
        /// </summary>
        Brush LineColor { get; }

        /// <summary>
        /// True when an image is loaded and hand-draw is allowed to start.
        /// </summary>
        bool HasImage { get; }

        /// <summary>
        /// True when hand-draw is the active tool.
        /// </summary>
        bool IsHandDrawActive { get; }

        /// <summary>
        /// Starts a new hand-drawn stroke and clears any stale outline results.
        /// </summary>
        void OnHandStrokeStarted();

        /// <summary>
        /// Commits a finished outline through the same path used by automatic tracing.
        /// </summary>
        void CommitOutline(Polyline outline);
    }

    /// <summary>
    /// Shared outline rendering constants.
    /// </summary>
    internal static class OutlineVisuals
    {
        internal static readonly System.Windows.Media.DoubleCollection PreviewDashes = CreateFrozenDashes(4, 2);

        private static System.Windows.Media.DoubleCollection CreateFrozenDashes(params double[] values)
        {
            var d = new System.Windows.Media.DoubleCollection(values);
            d.Freeze();
            return d;
        }
    }

    /// <summary>
    /// Minimal observable base class for tool parameter bindings.
    /// </summary>
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
    }
}