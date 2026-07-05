using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Policy;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DinoLino
{
    // The on-image Operation Count HUD: recomputes all n_* values from
    // UndoRedoManager.History, drag-to-move behavior, and the View-menu toggle.
    // Split from MainWindow.xaml.cs; no logic changes.
    public partial class MainWindow
    {

        // ---- Attempt counter (Curvature + Triangle operations) ----

        // Recomputes the four counts directly from the undo/redo history, so the display
        // can never drift: committing grows history, undo shrinks it, redo regrows it,
        // Clear All empties it. Draw/Outline operations are intentionally not counted.
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

        private void AttemptCounter_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _counterDragging = true;
            _counterDragStart = e.GetPosition(UI_WorkSpace);
            _counterStartX = UI_AttemptCounterTransform.X;
            _counterStartY = UI_AttemptCounterTransform.Y;
            UI_AttemptCounter.CaptureMouse();
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
            e.Handled = true;
        }

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