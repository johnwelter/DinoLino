using DinoLino.Utilities;
using DinoLino.Utilities.Operations;
using System.Windows;
using System.Windows.Input;

namespace DinoLino
{
    /// <summary>
    /// On-image operation count display, drag behavior for the counter, and the View menu toggle.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Attempt counter
        // =====================

        /// <summary>
        /// Recomputes the on-screen counts directly from undo history.
        /// This keeps the display synchronized with commit, undo, redo, and clear actions.
        /// </summary>
        private void UpdateAttemptCounter()
        {
            if (UndoRedoManager == null) return;

            int nCirc = 0, nPara = 0, nSpline = 0, nAngle = 0, nLine = 0, nOutline = 0;
            foreach (var op in UndoRedoManager.History)
            {
                if (op is CircularArcOperation) nCirc++;
                else if (op is ParabolaOperation) nPara++;
                else if (op is SplineOperation) nSpline++;
                else if (op is GetAngleOperation) nAngle++;
                else if (op is LineOperation) nLine++;
                else if (op is OutlineOperation o && o.HasMetadata) nOutline++;
            }

            UI_AttemptCirc.Text = $"n_circ = {nCirc}";
            UI_AttemptPara.Text = $"n_para = {nPara}";
            UI_AttemptSpline.Text = $"n_spline = {nSpline}";
            UI_AttemptAngle.Text = $"n_angle = {nAngle}";
            UI_AttemptLine.Text = $"n_line = {nLine}";
            UI_AttemptOutline.Text = $"n_outline = {nOutline}";
        }

        // =====================
        // Counter dragging
        // =====================

        private void AttemptCounter_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _counterDragging = true;
            _counterDragStart = e.GetPosition(UI_WorkSpace);
            _counterStartX = UI_AttemptCounterTransform.X;
            _counterStartY = UI_AttemptCounterTransform.Y;
            UI_AttemptCounter.CaptureMouse();

            // Prevent the mouse-down from bubbling into other workspace interactions.
            e.Handled = true;
        }

        private void AttemptCounter_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_counterDragging) return;

            Point now = e.GetPosition(UI_WorkSpace);
            UI_AttemptCounterTransform.X = _counterStartX + (now.X - _counterDragStart.X);
            UI_AttemptCounterTransform.Y = _counterStartY + (now.Y - _counterDragStart.Y);
        }

        private void AttemptCounter_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_counterDragging) return;

            _counterDragging = false;
            UI_AttemptCounter.ReleaseMouseCapture();

            // Mark the drag as handled so the release does not trigger unrelated UI logic.
            e.Handled = true;
        }

        // =====================
        // View toggle
        // =====================

        private void Menu_SeeAttempts(object sender, RoutedEventArgs e)
        {
            UI_AttemptCounter.Visibility =
                UI_SeeAttempts.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        // Drag state for the attempt-counter overlay.
        private bool _counterDragging;
        private Point _counterDragStart;
        private double _counterStartX, _counterStartY;
    }
}