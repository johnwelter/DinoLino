using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// The narrow view of OutlineMode that the outline tools (erase, smooth,
    /// hand-draw) are allowed to see. Tools never touch the mode directly, so
    /// their full dependency surface is this interface — which is also what
    /// makes them testable against a stub.
    /// </summary>
    public interface IOutlineToolContext
    {
        /// <summary>
        /// The committed/live outline the tools operate on (canvas-space
        /// vertex list), or null when none exists yet.
        /// </summary>
        Polyline ActivePolyline { get; }

        /// <summary>Current canvas ↔ image mapping (zoom + pan).</summary>
        ViewTransform Transform { get; }

        /// <summary>Douglas-Peucker epsilon shared with the automatic outline.</summary>
        double SimplifyEpsilon { get; }

        /// <summary>Stroke brush for committed outlines and previews.</summary>
        Brush LineColor { get; }

        /// <summary>True when an image is loaded (hand-draw refuses to start otherwise).</summary>
        bool HasImage { get; }

        /// <summary>True while the hand-draw tool is the selected tool.</summary>
        bool IsHandDrawActive { get; }

        /// <summary>
        /// Called by the hand-draw tool on the first press of a brand-new
        /// stroke. The mode begins an undo operation and clears prior
        /// metadata/EFD results, exactly as the old inline code did.
        /// </summary>
        void OnHandStrokeStarted();

        /// <summary>
        /// Routes a finished hand-drawn loop through the SAME commit path as
        /// an automatic outline (sets the active polyline, snapshots for
        /// smoothing, commits an OutlineOperation, fires OutlineReady).
        /// </summary>
        void CommitOutline(Polyline outline);
    }

    /// <summary>
    /// Shared visual constants for the outline mode and its tools. Frozen
    /// Freezables are shareable across elements and threads; this is the ONE
    /// copy of the 4-2 preview dash pattern that HandDrawTool and OutlineMode
    /// each used to build privately.
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
    /// Minimal INotifyPropertyChanged base for the tool objects, mirroring
    /// the SetField helper the modes inherit from WorkMode so XAML can bind
    /// tool parameters (e.g. {Binding Erase.BrushRadius}) directly.
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