using DinoLino.Utilities;
using DinoLino.Utilities.Operations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShapeConstraint = DinoLino.Utilities.Modes.DrawMode.ShapeConstraint;

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

        // Spacing under the header, dropped when the header is all there is so the
        // empty overlay closes up around the title.
        private static readonly Thickness HeaderWithRows = new Thickness(0, 0, 0, 4);
        private static readonly Thickness HeaderAlone = new Thickness(0);

        /// <summary>
        /// Recomputes the on-screen counts directly from undo history.
        /// This keeps the display synchronized with commit, undo, redo, and clear actions.
        /// </summary>
        /// <remarks>
        /// Only kinds with something recorded are shown: a row appears the first time
        /// that operation is performed and disappears again if undo takes the count
        /// back to zero, so the overlay stays as small as the specimen's work allows.
        /// Rows keep their fixed order rather than the order they were first used, so
        /// a given kind is always in the same place once it appears.
        ///
        /// Drawn shapes are tallied per kind rather than lumped together, so the
        /// counter answers "how many ellipses have I traced on this specimen" — the
        /// same question the Shape Data table's Attempt column answers.
        /// </remarks>
        private void UpdateAttemptCounter()
        {
            if (UndoRedoManager == null) return;

            int nCirc = 0, nPara = 0, nSpline = 0, nAngle = 0, nLine = 0, nOutline = 0;
            int nRect = 0, nSqr = 0, nEllipse = 0, nCircle = 0;

            foreach (var op in UndoRedoManager.History)
            {
                if (op is CircularArcOperation) nCirc++;
                else if (op is ParabolaOperation) nPara++;
                else if (op is SplineOperation) nSpline++;
                else if (op is GetAngleOperation) nAngle++;
                else if (op is LineOperation) nLine++;
                else if (op is OutlineOperation o && o.HasMetadata) nOutline++;
                else if (op is ShapeOperation shape)
                {
                    switch (shape.ShapeKind)
                    {
                        case ShapeConstraint.Rectangle: nRect++; break;
                        case ShapeConstraint.Square: nSqr++; break;
                        case ShapeConstraint.Ellipse: nEllipse++; break;
                        case ShapeConstraint.Circle: nCircle++; break;
                    }
                }
            }

            bool any = false;

            any |= ShowCount(UI_AttemptCirc, "n_circ", nCirc);
            any |= ShowCount(UI_AttemptPara, "n_para", nPara);
            any |= ShowCount(UI_AttemptSpline, "n_spline", nSpline);
            any |= ShowCount(UI_AttemptAngle, "n_angle", nAngle);

            any |= ShowCount(UI_AttemptRect, "n_rect", nRect);
            any |= ShowCount(UI_AttemptSquare, "n_sqr", nSqr);
            any |= ShowCount(UI_AttemptEllipse, "n_ellipse", nEllipse);
            any |= ShowCount(UI_AttemptCircle, "n_circle", nCircle);

            any |= ShowCount(UI_AttemptLine, "n_line", nLine);
            any |= ShowCount(UI_AttemptOutline, "n_outline", nOutline);

            UI_AttemptHeader.Margin = any ? HeaderWithRows : HeaderAlone;
        }

        /// Fills in one row and returns whether it is showing. A count of zero
        /// collapses the row rather than hiding it: a hidden row still holds its line
        /// open, and the point is for the overlay to shrink around what is left.
        private static bool ShowCount(TextBlock row, string label, int count)
        {
            if (count <= 0)
            {
                row.Text = string.Empty;
                row.Visibility = Visibility.Collapsed;
                return false;
            }

            row.Text = $"{label} = {count}";
            row.Visibility = Visibility.Visible;
            return true;
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