using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using System.Windows;
using System.Windows.Input;

namespace DinoLino
{
    /// Routes raw keyboard and mouse input to workspace actions and the active work
    /// mode.
    public partial class MainWindow
    {
        // ---- Pan state ----

        private bool _isPanning = false;
        private Point _panStartMouse; // Mouse position in workspace coordinates when the drag started.
        private double _panStartImageTx, _panStartImageTy;

        // ---- Keyboard input ----

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                ClearAllOperations();
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                Menu_OpenImage(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
            {
                Menu_SeeHistory(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
            {
                Menu_ExportHistory(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                Menu_Screenshot(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                Menu_Undo(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                Menu_Redo(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Finish an in-progress spline when Enter is pressed.
            if (e.Key == Key.Enter && CurrentWorkMode is CurvatureMode cm && cm.CanFinalizeSpline)
            {
                // Remove the preview element queued by the last click before committing the final spline.
                RemovePendingElements();

                foreach (UIElement element in cm.FinalizeSpline())
                    AddElementToWorkSpace(element);

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_scaleMode)
                {
                    _scaleMode = false;
                    _scaleClicks = 0;

                    if (_scaleLine != null)
                    {
                        UI_WorkCanvas.Children.Remove(_scaleLine);
                        _scaleLine = null;
                    }

                    e.Handled = true;
                    return;
                }

                // Cancel the active probe interaction without changing the underlying specimen data.
                if (CurrentWorkMode is CurvatureMode probeCm && probeCm.FindTurningAngleMode)
                {
                    probeCm.FindTurningAngleMode = false;
                    e.Handled = true;
                    return;
                }

                CurrentWorkMode?.CancelCurrentOperation();
                e.Handled = true;
                return;
            }
        }

        // ---- Workspace clicks ----

        private void WorkSpace_Click(object sender, MouseButtonEventArgs e)
        {
            // Ctrl + left drag starts image panning instead of a drawing action.
            if (e.ChangedButton == MouseButton.Left &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                _isPanning = true;
                _panStartMouse = e.GetPosition(UI_WorkSpace);

                var tt = UI_WorkImage.GetTranslateTransform();
                _panStartImageTx = tt.X;
                _panStartImageTy = tt.Y;

                (sender as UIElement)?.CaptureMouse();  // Keep receiving drag events even if the pointer leaves the canvas.
                Mouse.OverrideCursor = Cursors.SizeAll;  // Show pan cursor feedback.
                e.Handled = true;
                return;
            }

            if (_scaleMode)
            {
                HandleScaleClick(new Vector2(Mouse.GetPosition(UI_WorkCanvas)));
                return;
            }

            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));

            if (CurrentWorkMode is OutlineMode)
                SyncOutlineImageTransform();

            // Begin a freehand stroke in outline mode.
            if (CurrentWorkMode is OutlineMode handOm && handOm.HandDrawMode &&
                e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            {
                if (!handOm.SeePreviousOperations && handOm.IsStartingNewOperation)
                    ClearWorkspaceVisualsOnly();

                handOm.HandDraw.BeginStroke(mousePos);
                (sender as UIElement)?.CaptureMouse();  // Keep receiving movement while the stroke is active.
                e.Handled = true;
                return;
            }

            // Skip clearing the workspace for probe interactions or other non-destructive actions.
            bool probing = CurrentWorkMode.IsProbeInteraction
                || (CurrentWorkMode is CurvatureMode probeGuardCm && probeGuardCm.FindTurningAngleMode)
                || (CurrentWorkMode is OutlineMode probeGuardOm
                    && (probeGuardOm.EraseOutlineMode || probeGuardOm.SmoothOutlineMode
                        || probeGuardOm.OutlineMetadataMode));

            if (!CurrentWorkMode.SeePreviousOperations
                && CurrentWorkMode.IsStartingNewOperation
                && !probing)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[workspace] clearing visuals on click (seePrev={CurrentWorkMode.SeePreviousOperations}, " +
                    $"starting={CurrentWorkMode.IsStartingNewOperation}, probe={probing}, mode={CurrentWorkMode.GetType().Name})");

                // Preserve the cursor overlay while removing old workspace drawings.
                ClearWorkspaceVisualsOnly();
            }

            RemovePendingElements();

            foreach (UIElement element in CurrentWorkMode.ProcessClick(mousePos))
                AddElementToWorkSpace(element);
        }

        // ---- Workspace drag ----

        private void WorkSpace_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point now = e.GetPosition(UI_WorkSpace);
                var it = UI_WorkImage.GetTranslateTransform();
                it.X = _panStartImageTx + (now.X - _panStartMouse.X);
                it.Y = _panStartImageTy + (now.Y - _panStartMouse.Y);

                // Keep the border and overlay layers aligned with the image while panning.
                UI_WorkBorder.CopyTransforms(UI_WorkImage);
                return;
            }

            if (_scaleMode && _scaleClicks == 1 && _scaleLine != null)
            {
                Vector2 p = new Vector2(Mouse.GetPosition(UI_WorkCanvas));
                _scaleLine.X2 = p.X;
                _scaleLine.Y2 = p.Y;
                UI_DotCursor.SetPosition(p.X - 5, p.Y - 5);
                return;
            }

            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));
            Vector2 centeredCursorPos = CurrentWorkMode.ProcessMouseMovement(mousePos) - new Vector2(5, 5);
            UI_DotCursor.SetPosition(centeredCursorPos.X, centeredCursorPos.Y);

            if (e.LeftButton == MouseButtonState.Pressed && CurrentWorkMode is OutlineMode om)
            {
                if (om.EraseOutlineMode)
                    om.Erase.ProcessDrag(mousePos);
                else if (om.SmoothOutlineMode && om.Smooth.IsLocalScope)
                    om.Smooth.ProcessLocalDrag(mousePos);
                else if (om.HandDrawMode)
                    om.HandDraw.ProcessDrag(mousePos);
            }
        }

        private void WorkSpace_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.ChangedButton == MouseButton.Left)
            {
                _isPanning = false;
                (sender as UIElement)?.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
                e.Handled = true;
                return;
            }

            // Pause the freehand stroke; the stroke remains resumable until the mode ends it.
            if (e.ChangedButton == MouseButton.Left && CurrentWorkMode is OutlineMode om && om.HandDrawMode)
            {
                om.HandDraw.EndStroke();
                (sender as UIElement)?.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void WorkSpace_LostCapture(object sender, MouseEventArgs e)
        {
            _isPanning = false;
            Mouse.OverrideCursor = null;
        }

        // ---- Workspace zoom ----

        private void WorkSpace_ScrollZoom(object sender, MouseWheelEventArgs e)
        {
            UpdateWorkSpaceZoom(e.Delta, e.GetPosition(UI_WorkImage));
        }
    }
}