using DinoLino.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;

namespace DinoLino
{
    /// <summary>
    /// Workshop sidebar: per-category CSV export for every specimen in the session,
    /// plus the sidebar's visibility toggle.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Sidebar visibility
        // =====================

        // The sidebar starts visible, matching UI_SeeWorkshop.IsChecked in the View menu.
        private bool _workshopVisible = true;

        // Remembered panel width so hiding and re-showing preserves the user's resize.
        private double _workshopWidth = 260;

        // Width of the workshop's GridSplitter column.
        private const double WorkshopSplitterWidth = 4;

        /// <summary>
        /// Shows or hides the Workshop sidebar, resizing the window rather than the workspace.
        /// </summary>
        private void SetWorkshopVisible(bool visible)
        {
            if (visible == _workshopVisible) return;
            _workshopVisible = visible;

            if (visible)
            {
                // Grow the window first so the workspace keeps its current width.
                ResizeWindowForWorkshop(_workshopWidth + WorkshopSplitterWidth);

                UI_WorkshopColumn.MinWidth = 180;
                UI_WorkshopColumn.MaxWidth = 500;
                UI_WorkshopColumn.Width = new GridLength(_workshopWidth);
                UI_WorkshopSplitterColumn.Width = new GridLength(WorkshopSplitterWidth);

                UI_WorkshopPanel.Visibility = Visibility.Visible;
                UI_WorkshopSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                // Remember the current width, including any resize the user made.
                if (UI_WorkshopColumn.ActualWidth > 0)
                    _workshopWidth = UI_WorkshopColumn.ActualWidth;

                UI_WorkshopPanel.Visibility = Visibility.Collapsed;
                UI_WorkshopSplitter.Visibility = Visibility.Collapsed;

                // MinWidth must be cleared before the column can collapse to zero.
                UI_WorkshopColumn.MinWidth = 0;
                UI_WorkshopColumn.Width = new GridLength(0);
                UI_WorkshopSplitterColumn.Width = new GridLength(0);

                ResizeWindowForWorkshop(-(_workshopWidth + WorkshopSplitterWidth));
            }
        }

        /// Widens or narrows the window by the sidebar's width so the workspace area
        /// is unaffected. Maximized windows are left alone, since they cannot grow.
        private void ResizeWindowForWorkshop(double delta)
        {
            if (WindowState != WindowState.Normal) return;

            double available = SystemParameters.WorkArea.Width;
            double target = ActualWidth + delta;

            // Never exceed the screen or shrink past the window's own minimum.
            if (target > available) target = available;
            if (target < MinWidth) target = MinWidth;

            Width = target;

            // Pull the window back on-screen if growing pushed its right edge off.
            double right = Left + Width;
            if (right > SystemParameters.WorkArea.Right)
                Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width);
        }

        // =====================
        // Exports
        // =====================

        // Every export covers all specimens: the archived records plus the live
        // history, which is what GeomOpHistoryWindow walks internally.

        private void Workshop_ExportCurvature(object sender, RoutedEventArgs e)
        {
            if (!HasAnyOperations("Curvature Data")) return;
            GeomOpHistoryWindow.ExportCurvatureCsv(
                UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
        }

        private void Workshop_ExportAngle(object sender, RoutedEventArgs e)
        {
            if (!HasAnyOperations("Angle Data")) return;
            GeomOpHistoryWindow.ExportAngleCsv(
                UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
        }

        private void Workshop_ExportShape(object sender, RoutedEventArgs e)
        {
            if (!HasAnyOperations("Shape Data")) return;
            GeomOpHistoryWindow.ExportShapeCsv(
                UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
        }

        private void Workshop_ExportOutline(object sender, RoutedEventArgs e)
        {
            if (!HasAnyOperations("Outline Metadata")) return;
            GeomOpHistoryWindow.ExportOutlineCsv(
                UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
        }

        private void Workshop_ExportEfa(object sender, RoutedEventArgs e)
        {
            if (!HasAnyOperations("EFA Data")) return;
            GeomOpHistoryWindow.ExportEfaCsv(
                UndoRedoManager, SpecimenManager.DisplayName);
        }

        /// Guards an export when nothing has been measured at all, so the user gets an
        /// explanation instead of a file of empty rows.
        private bool HasAnyOperations(string category)
        {
            if (UndoRedoManager == null) return false;

            if (UndoRedoManager.History.Count > 0) return true;
            foreach (var record in UndoRedoManager.Archive)
                if (record.Operations.Count > 0) return true;

            MessageBox.Show(
                this,
                "There are no measurements to export yet.",
                $"Export {category}",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }
    }
}