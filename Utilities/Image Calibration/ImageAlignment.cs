using DinoLino.DataTypes;
using DinoLino.Utilities;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

// This one file holds the alignment data layer (AlignmentAxis, AlignmentState and
// ImageAlignment, in DinoLino.Utilities) and the workspace flow that sets it
// (AlignmentCapture plus the MainWindow menu and input hooks, in DinoLino).

namespace DinoLino.Utilities
{
    /// <summary>
    /// The session's live alignment, reachable from the work modes. MainWindow owns the
    /// one ImageAlignment instance and rebinds it as specimens change; this holds that
    /// same object, so a mode can express a measurement in the specimen's own frame
    /// without a path back to the window.
    /// </summary>
    public static class ActiveAlignment
    {
        // Stands in before Bind, and in any tooling that runs a mode headless. Unset,
        // so it leaves vectors in canvas space rather than throwing.
        private static readonly ImageAlignment _unset = new ImageAlignment();

        private static ImageAlignment _current;

        /// <summary>Supplies MainWindow's alignment. Call once at startup.</summary>
        public static void Bind(ImageAlignment alignment) => _current = alignment;

        public static ImageAlignment Current => _current ?? _unset;
    }
    /// <summary>Which of the specimen's two axes the user drew.</summary>
    public enum AlignmentAxis { X, Y }

    /// <summary>
    /// One specimen's orientation, as the direction of its +X axis. Immutable, and
    /// constructible only from a usable axis line, so a set state always carries a
    /// meaningful angle.
    /// </summary>
    public readonly struct AlignmentState : IEquatable<AlignmentState>
    {
        /// <summary>Shortest axis line, in canvas pixels, that yields a reliable angle.</summary>
        public const double MinimumLinePixels = 8.0;

        public bool IsSet { get; }

        /// Direction of the specimen's +X axis in canvas coordinates. Canvas Y grows
        /// downward, so a positive angle turns clockwise on screen.
        public double RotationRadians { get; }

        /// <summary>Which axis the user drew to establish this orientation.</summary>
        public AlignmentAxis DrawnAxis { get; }

        private AlignmentState(double rotationRadians, AlignmentAxis drawnAxis)
        {
            IsSet = true;
            RotationRadians = rotationRadians;
            DrawnAxis = drawnAxis;
        }

        /// <summary>An unaligned specimen, which is also the default value.</summary>
        public static AlignmentState None => default;

        /// The orientation a drawn axis line describes, or None when the line is too
        /// short for its angle to mean anything. Both the accept action and the
        /// dialog's ready check read this, so there is one definition of usable.
        public static AlignmentState FromLine(Point from, Point to, AlignmentAxis axis)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            if (dx * dx + dy * dy < MinimumLinePixels * MinimumLinePixels) return None;

            // With Y growing downward the specimen's +Y axis sits 90 degrees
            // counter-clockwise from its +X axis, so a drawn Y axis turns forward by
            // that much to give +X. This is the convention the axes compass draws:
            // X right, Y up.
            if (axis == AlignmentAxis.Y)
            {
                double t = dx;
                dx = -dy;
                dy = t;
            }

            return new AlignmentState(Math.Atan2(dy, dx), axis);
        }

        public bool Equals(AlignmentState other) =>
            IsSet == other.IsSet
            && RotationRadians == other.RotationRadians
            && DrawnAxis == other.DrawnAxis;

        public override bool Equals(object obj) => obj is AlignmentState s && Equals(s);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = IsSet.GetHashCode();
                hash = (hash * 397) ^ RotationRadians.GetHashCode();
                hash = (hash * 397) ^ (int)DrawnAxis;
                return hash;
            }
        }

        public static bool operator ==(AlignmentState a, AlignmentState b) => a.Equals(b);
        public static bool operator !=(AlignmentState a, AlignmentState b) => !a.Equals(b);
    }

    /// <summary>
    /// The loaded specimen's orientation. The image is never rotated: this is a
    /// frame of reference measurements can be expressed in.
    /// </summary>
    public class ImageAlignment : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private AlignmentState _state = AlignmentState.None;
        private Specimen _owner;

        // =====================
        // Alignment state
        // =====================

        public bool IsAligned => _state.IsSet;
        public AlignmentAxis DrawnAxis => _state.DrawnAxis;
        public double RotationRadians => _state.RotationRadians;
        public double RotationDegrees => _state.RotationRadians * 180.0 / Math.PI;
        /// The opposite turn. A label carried around by the axes graphic spins back
        /// by this much, so its letter stays upright instead of ending up on its head.
        public double CounterRotationDegrees => -RotationDegrees;

        /// The live orientation. Assigning writes through to the bound specimen, so
        /// what is displayed and what is stored cannot drift apart.
        public AlignmentState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                if (_owner != null) _owner.Alignment = value;
                NotifyAll();
            }
        }

        /// Makes a specimen's stored orientation the live one and the destination for
        /// every later change. Pass null when no specimen is loaded.
        public void BindTo(Specimen specimen)
        {
            _owner = specimen;
            _state = specimen?.Alignment ?? AlignmentState.None;
            NotifyAll();
        }

        public void SetFromLine(Point from, Point to, AlignmentAxis axis)
            => State = AlignmentState.FromLine(from, to, axis);

        public void Clear() => State = AlignmentState.None;

        // =====================
        // Conversion helpers
        // =====================

        /// Rotates a canvas-space vector into the specimen's frame, where +X runs
        /// along its drawn axis. An unaligned specimen returns the vector unchanged,
        /// so callers need no special case.
        public Vector ToAligned(Vector canvasVector)
        {
            if (!_state.IsSet) return canvasVector;

            double c = Math.Cos(-_state.RotationRadians), s = Math.Sin(-_state.RotationRadians);
            return new Vector(canvasVector.X * c - canvasVector.Y * s,
                              canvasVector.X * s + canvasVector.Y * c);
        }

        /// Rotates a canvas-space point about the canvas origin. Extents, spans and
        /// shape metrics are unaffected by the choice of centre, so no reference
        /// point is needed; anything positional should rotate about its own centroid.
        public Point ToAligned(Point canvasPoint) => (Point)ToAligned((Vector)canvasPoint);

        /// <summary>Re-expresses a canvas-space angle relative to the specimen's +X axis.</summary>
        public double ToAlignedAngle(double canvasAngleRadians)
        {
            if (!_state.IsSet) return canvasAngleRadians;

            double a = canvasAngleRadians - _state.RotationRadians;
            while (a <= -Math.PI) a += 2 * Math.PI;
            while (a > Math.PI) a -= 2 * Math.PI;
            return a;
        }

        public string StatusText =>
            _state.IsSet ? $"" : "Alignment: not set";

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsAligned));
            OnPropertyChanged(nameof(DrawnAxis));
            OnPropertyChanged(nameof(RotationRadians));
            OnPropertyChanged(nameof(RotationDegrees));
            OnPropertyChanged(nameof(CounterRotationDegrees));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}

namespace DinoLino
{
    /// <summary>
    /// One run of the axis-drawing tool: owns the dialog, the line on the canvas, and
    /// the two-click state, and applies the result to the shared alignment.
    /// </summary>
    /// <remarks>
    /// Given only the canvas it draws on and the element whose cursor it changes, so
    /// it cannot reach the rest of the window. Create one per capture and let it die
    /// with the dialog.
    /// </remarks>
    internal sealed class AlignmentCapture
    {
        private readonly ImageAlignment _alignment;
        private readonly AlignWindow _window;
        private readonly Canvas _canvas;
        private readonly FrameworkElement _workspace;

        private Line _line;
        private bool _awaitingSecondPoint;

        /// <summary>Raised once the capture has torn down, however it ended.</summary>
        public event Action Finished;

        public AlignmentCapture(ImageAlignment alignment, AlignWindow window,
            Canvas canvas, FrameworkElement workspace)
        {
            _alignment = alignment ?? throw new ArgumentNullException(nameof(alignment));
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

            _window.Accepted += Accept;
            _window.RetryRequested += ClearLine;

            // Accept, Retry's close, Cancel, Esc and the title-bar X all end here,
            // so teardown has one home.
            _window.Closed += (s, e) => TearDown();
        }

        /// <summary>Arms the workspace and shows the dialog.</summary>
        public void Start()
        {
            _workspace.Cursor = Cursors.Cross;
            _window.Show();
        }

        /// <summary>Brings the open dialog forward.</summary>
        public void Focus() => _window.Activate();

        /// <summary>Ends the capture from outside, leaving any stored alignment intact.</summary>
        public void Cancel() => _window.Close();

        /// The orientation the line on screen describes right now. Read at accept
        /// time, so switching the X/Y radio reinterprets a line already drawn.
        private AlignmentState Pending => _line == null
            ? AlignmentState.None
            : AlignmentState.FromLine(
                new Point(_line.X1, _line.Y1),
                new Point(_line.X2, _line.Y2),
                _window.SelectedAxis);

        /// Places one end of the axis. A click after the second one starts a fresh
        /// line, so the axis can be redrawn without reaching for Retry.
        public void HandleClick(Point canvasPoint)
        {
            if (!_awaitingSecondPoint)
            {
                ClearLine();

                _line = new Line
                {
                    Stroke = Brushes.Cyan,
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 6, 3 },
                    X1 = canvasPoint.X,
                    Y1 = canvasPoint.Y,
                    X2 = canvasPoint.X,
                    Y2 = canvasPoint.Y
                };
                _canvas.Children.Add(_line);

                _awaitingSecondPoint = true;
                _window.SetLineReady(false);
                return;
            }

            _line.X2 = canvasPoint.X;
            _line.Y2 = canvasPoint.Y;
            _awaitingSecondPoint = false;
            _window.SetLineReady(Pending.IsSet);
        }

        /// <summary>Trails the far end of the axis from the cursor between the two clicks.</summary>
        public void TrackCursor(Point canvasPoint)
        {
            if (!_awaitingSecondPoint || _line == null) return;
            _line.X2 = canvasPoint.X;
            _line.Y2 = canvasPoint.Y;
        }

        private void Accept()
        {
            var state = Pending;
            if (state.IsSet) _alignment.State = state;
        }

        private void ClearLine()
        {
            if (_line != null)
            {
                _canvas.Children.Remove(_line);
                _line = null;
            }
            _awaitingSecondPoint = false;
        }

        private void TearDown()
        {
            ClearLine();
            _workspace.Cursor = null;
            Finished?.Invoke();
        }
    }

    public partial class MainWindow
    {
        private AlignmentCapture _alignCapture;

        private void Menu_AlignImage(object sender, RoutedEventArgs e) => BeginAlignCapture();

        /// Opens the axis dialog and arms the workspace. The dialog is modeless
        /// because the user has to reach the image while it is open.
        internal void BeginAlignCapture()
        {
            if (WorkingImage == null) return;

            if (_alignCapture != null)
            {
                _alignCapture.Focus();
                return;
            }

            // Both captures claim the next workspace click, so only one can be armed.
            CancelScaleCapture();

            var dialog = new AlignWindow
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            _alignCapture = new AlignmentCapture(ImageAlignment, dialog, UI_WorkCanvas, UI_WorkSpace);
            _alignCapture.Finished += () => _alignCapture = null;
            _alignCapture.Start();
        }

        /// <summary>Ends an in-progress capture, leaving any stored alignment intact.</summary>
        internal void CancelAlignCapture() => _alignCapture?.Cancel();

        /// <summary>Routes a workspace click to the capture. False when none is armed.</summary>
        private bool AlignCaptureClick(Vector2 mousePos)
        {
            if (_alignCapture == null) return false;
            _alignCapture.HandleClick(new Point(mousePos.X, mousePos.Y));
            return true;
        }

        /// <summary>Routes cursor movement to the capture. False when none is armed.</summary>
        private bool AlignCaptureMove(Vector2 mousePos)
        {
            if (_alignCapture == null) return false;
            _alignCapture.TrackCursor(new Point(mousePos.X, mousePos.Y));
            UI_DotCursor.SetPosition(mousePos.X - 5, mousePos.Y - 5);
            return true;
        }

        // ---- Image axes compass (View ▸ See Image Axes) ----

        // Where the pointer and the overlay stood when the drag began, so the widget
        // follows the cursor instead of jumping its centre under it.
        private Point _axesDragPointer;
        private Point _axesDragOrigin;
        private bool _axesDragging;

        /// Shows or hides the axis compass. Its orientation is bound to ImageAlignment,
        /// so there is nothing to push into it here.
        internal void SetImageAxesVisible(bool visible)
        {
            UI_ImageAxes.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ImageAxes_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _axesDragPointer = e.GetPosition(UI_WorkSpace);
            _axesDragOrigin = new Point(UI_ImageAxesTransform.X, UI_ImageAxesTransform.Y);
            _axesDragging = UI_ImageAxes.CaptureMouse();
            e.Handled = true;
        }

        private void ImageAxes_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_axesDragging) return;

            Point now = e.GetPosition(UI_WorkSpace);
            UI_ImageAxesTransform.X = _axesDragOrigin.X + (now.X - _axesDragPointer.X);
            UI_ImageAxesTransform.Y = _axesDragOrigin.Y + (now.Y - _axesDragPointer.Y);
        }

        private void ImageAxes_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_axesDragging) return;

            _axesDragging = false;
            UI_ImageAxes.ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}