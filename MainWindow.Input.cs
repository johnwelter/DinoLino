using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using System.Windows;
using System.Windows.Input;

namespace DinoLino
{
    // Raw input routing: the window KeyDown handler and the work-canvas mouse handlers
    // (click dispatch to the active WorkMode, hand-draw, erase, pan, scroll zoom),
    // plus the pan-drag state those handlers share. Split from MainWindow.xaml.cs.
    public partial class MainWindow
    {

        // --- Ctrl+drag panning state ---
        private bool _isPanning = false;
        private Point _panStartMouse;                       // mouse position at pan start (UI_WorkSpace frame)
        private double _panStartImageTx, _panStartImageTy;

        // Keyboard shortcuts
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl + C to reset workspace
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                ClearAllOperations();
            }

            // Ctrl + F to open image
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                Menu_OpenImage(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Ctrl + H to view history
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
            {
                Menu_SeeHistory(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Ctrl + E to export operation history
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
            {
                Menu_ExportHistory(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Ctrl + S to take screenshot
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                Menu_Screenshot(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Ctrl + Z to undo
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                Menu_Undo(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Ctrl + Y to redo
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                Menu_Redo(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Enter finalizes an in-progress n-point spline
            if (e.Key == Key.Enter && CurrentWorkMode is CurvatureMode cm && cm.CanFinalizeSpline)
            {
                foreach (UIElement element in cm.FinalizeSpline())
                    AddElementToWorkSpace(element);
                e.Handled = true;
                return;
            }

            // Esc to cancel operation
            if (e.Key == Key.Escape)
            {
                if (_scaleMode)
                {
                    _scaleMode = false;
                    _scaleClicks = 0;
                    if (_scaleLine != null) { UI_WorkCanvas.Children.Remove(_scaleLine); _scaleLine = null; }
                    e.Handled = true;
                    return;
                }
                CurrentWorkMode?.CancelCurrentOperation();
                e.Handled = true;
                return;
            }
        }


        private void WorkSpace_Click(object sender, MouseButtonEventArgs e)
        {
            // Ctrl + left button starts a pan-drag instead of a drawing action.
            if (e.ChangedButton == MouseButton.Left &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                _isPanning = true;
                _panStartMouse = e.GetPosition(UI_WorkSpace);

                var tt = UI_WorkImage.GetTranslateTransform();
                _panStartImageTx = tt.X;
                _panStartImageTy = tt.Y;

                (sender as UIElement)?.CaptureMouse();   // keep receiving move/up if cursor leaves
                Mouse.OverrideCursor = Cursors.SizeAll;  // visual feedback
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

            // Hand-draw: a left press begins/continues a freehand stroke.
            if (CurrentWorkMode is OutlineMode handOm && handOm.HandDrawMode &&
                e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            {
                if (!handOm.SeePreviousOperations && handOm.IsStartingNewOperation)
                    ClearWorkspaceVisualsOnly();

                handOm.BeginHandStroke(mousePos);
                (sender as UIElement)?.CaptureMouse();   // keep getting moves if cursor leaves
                e.Handled = true;
                return;
            }

            if (!CurrentWorkMode.SeePreviousOperations && CurrentWorkMode.IsStartingNewOperation)
            {
                // don't use Children.Clear() because we want to keep the DotCursor
                ClearWorkspaceVisualsOnly();
            }

            RemovePendingElements();

            foreach (UIElement element in CurrentWorkMode.ProcessClick(mousePos))
            {
                AddElementToWorkSpace(element);
            }
        }

        private void WorkSpace_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point now = e.GetPosition(UI_WorkSpace);
                var it = UI_WorkImage.GetTranslateTransform();
                it.X = _panStartImageTx + (now.X - _panStartMouse.X);
                it.Y = _panStartImageTy + (now.Y - _panStartMouse.Y);

                // Keep the drawing layer (border + canvas + all drawn elements) locked to the image
                UI_WorkBorder.CopyTransforms(UI_WorkImage);
                return;   // don't run cursor/erase logic while panning
            }

            if (_scaleMode && _scaleClicks == 1 && _scaleLine != null)
            {
                Vector2 p = new Vector2(Mouse.GetPosition(UI_WorkCanvas));
                _scaleLine.X2 = p.X;
                _scaleLine.Y2 = p.Y;
                UI_DotCursor.SetPosition(p.X - 5, p.Y - 5);
                return;   // live-preview the calibration line; skip mode logic
            }

            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));
            Vector2 centeredCursorPos = CurrentWorkMode.ProcessMouseMovement(mousePos) - new Vector2(5, 5);
            UI_DotCursor.SetPosition(centeredCursorPos.X, centeredCursorPos.Y);
            if (e.LeftButton == MouseButtonState.Pressed && CurrentWorkMode is OutlineMode om)
            {
                if (om.EraseOutlineMode)
                    om.ProcessEraseDrag(mousePos);
                else if (om.HandDrawMode)
                    om.ProcessHandDrawDrag(mousePos);
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

            // Hand-draw: releasing pauses the stroke (it stays open and resumable).
            if (e.ChangedButton == MouseButton.Left && CurrentWorkMode is OutlineMode om && om.HandDrawMode)
            {
                om.EndHandStroke();
                (sender as UIElement)?.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void WorkSpace_LostCapture(object sender, MouseEventArgs e)
        {
            _isPanning = false;
            Mouse.OverrideCursor = null;
        }

        // TODO: encapsulate and make generic someplace else
        private void WorkSpace_ScrollZoom(object sender, MouseWheelEventArgs e)
        {
            UpdateWorkSpaceZoom(e.Delta, e.GetPosition(UI_WorkImage));
        }
    }
}