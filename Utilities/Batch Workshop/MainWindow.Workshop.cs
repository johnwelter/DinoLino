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

        // Which panels the View menu has switched on. These match the initial
        // IsChecked values of UI_SeeWorkshop and UI_SeeDirectory, and the row heights
        // set in the XAML so the first paint needs no layout pass.
        private bool _workshopVisible = true;
        private bool _directoryVisible = true;

        // Whether the sidebar column itself is showing. It is present whenever at
        // least one of the two panels is.
        private bool _sidebarVisible = true;

        // Remembered panel width so hiding and re-showing preserves the user's resize.
        private double _sidebarWidth = 260;

        // Width of the sidebar's GridSplitter column.
        private const double SidebarSplitterWidth = 4;

        /// <summary>Shows or hides the Batch Workshop panel.</summary>
        private void SetWorkshopVisible(bool visible)
        {
            if (visible == _workshopVisible) return;
            _workshopVisible = visible;
            UpdateSidebarLayout();
        }

        /// <summary>Shows or hides the Directory panel.</summary>
        private void SetDirectoryVisible(bool visible)
        {
            if (visible == _directoryVisible) return;
            _directoryVisible = visible;

            // Fill the tree the first time the panel is shown rather than at startup.
            if (visible && UI_DirectoryTree.Items.Count == 0)
                RebuildDirectoryRoots();

            UpdateSidebarLayout();
        }

        /// <summary>Hides the Batch Workshop panel and unchecks its View menu item.</summary>
        private void Workshop_Minimize(object sender, RoutedEventArgs e)
        {
            UI_SeeWorkshop.IsChecked = false;
            SetWorkshopVisible(false);
        }

        /// <summary>Hides the Directory panel and unchecks its View menu item.</summary>
        private void Directory_Minimize(object sender, RoutedEventArgs e)
        {
            UI_SeeDirectory.IsChecked = false;
            SetDirectoryVisible(false);
        }

        /// Applies the current toggles to the sidebar: which panels are shown, how the
        /// rows divide the column, and whether the column exists at all.
        private void UpdateSidebarLayout()
        {
            UI_WorkshopPanel.Visibility = _workshopVisible ? Visibility.Visible : Visibility.Collapsed;
            UI_DirectoryPanel.Visibility = _directoryVisible ? Visibility.Visible : Visibility.Collapsed;

            // The divider only means anything with a panel on each side of it.
            UI_SidebarDivider.Visibility =
                (_workshopVisible && _directoryVisible) ? Visibility.Visible : Visibility.Collapsed;

            // With the Directory below it the Workshop takes only the height it needs;
            // on its own it fills the column, leaving the blank space underneath.
            UI_WorkshopRow.Height = !_workshopVisible
                ? new GridLength(0)
                : _directoryVisible ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

            UI_DirectoryRow.Height = _directoryVisible
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);

            SetSidebarVisible(_workshopVisible || _directoryVisible);
        }

        /// Adds or removes the sidebar column, resizing the window rather than the
        /// workspace so the image keeps its size.
        private void SetSidebarVisible(bool visible)
        {
            if (visible == _sidebarVisible) return;
            _sidebarVisible = visible;

            if (visible)
            {
                // Grow the window first so the workspace keeps its current width.
                ResizeWindowForSidebar(_sidebarWidth + SidebarSplitterWidth);

                UI_SidebarColumn.MinWidth = 180;
                UI_SidebarColumn.MaxWidth = 500;
                UI_SidebarColumn.Width = new GridLength(_sidebarWidth);
                UI_SidebarSplitterColumn.Width = new GridLength(SidebarSplitterWidth);

                UI_Sidebar.Visibility = Visibility.Visible;
                UI_SidebarSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                // Remember the current width, including any resize the user made.
                if (UI_SidebarColumn.ActualWidth > 0)
                    _sidebarWidth = UI_SidebarColumn.ActualWidth;

                UI_Sidebar.Visibility = Visibility.Collapsed;
                UI_SidebarSplitter.Visibility = Visibility.Collapsed;

                // MinWidth must be cleared before the column can collapse to zero.
                UI_SidebarColumn.MinWidth = 0;
                UI_SidebarColumn.Width = new GridLength(0);
                UI_SidebarSplitterColumn.Width = new GridLength(0);

                ResizeWindowForSidebar(-(_sidebarWidth + SidebarSplitterWidth));
            }
        }

        /// Widens or narrows the window by the sidebar's width so the workspace area
        /// is unaffected. Maximized windows are left alone, since they cannot grow.
        private void ResizeWindowForSidebar(double delta)
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
        // history. Each category writes one wide CSV, matching the table shown by its
        // edit button.

        private void Workshop_ExportCurvature(object sender, RoutedEventArgs e)
            => ExportWorkshopCategory(WorkshopCategory.Curvature);

        private void Workshop_ExportAngle(object sender, RoutedEventArgs e)
            => ExportWorkshopCategory(WorkshopCategory.Angle);

        private void Workshop_ExportShape(object sender, RoutedEventArgs e)
            => ExportWorkshopCategory(WorkshopCategory.Shape);

        private void Workshop_ExportOutline(object sender, RoutedEventArgs e)
            => ExportWorkshopCategory(WorkshopCategory.OutlineMetadata);

        private void Workshop_ExportEfa(object sender, RoutedEventArgs e)
            => ExportWorkshopCategory(WorkshopCategory.Efa);

        private void ExportWorkshopCategory(WorkshopCategory category)
        {
            if (UndoRedoManager == null) return;

            GeomOpHistoryWindow.ExportWorkshopCsv(
                category, UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
        }

        /// Writes one xlsx holding the tables staged in the History window, or every
        /// table when none have been staged.
        private void Workshop_ExportAllGeometric(object sender, RoutedEventArgs e)
        {
            if (UndoRedoManager == null) return;

            GeomOpHistoryWindow.ExportAllGeometricData(
                UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
        }
        
        // =====================
        // Editing
        // =====================

        private void Workshop_EditCurvature(object sender, RoutedEventArgs e)
            => OpenWorkshopEditor(WorkshopCategory.Curvature);

        private void Workshop_EditAngle(object sender, RoutedEventArgs e)
            => OpenWorkshopEditor(WorkshopCategory.Angle);

        private void Workshop_EditShape(object sender, RoutedEventArgs e)
            => OpenWorkshopEditor(WorkshopCategory.Shape);

        private void Workshop_EditOutline(object sender, RoutedEventArgs e)
            => OpenWorkshopEditor(WorkshopCategory.OutlineMetadata);

        private void Workshop_EditEfa(object sender, RoutedEventArgs e)
            => OpenWorkshopEditor(WorkshopCategory.Efa);

        /// Opens the folder of stored silhouettes, where they can be renamed,
        /// duplicated, and deleted. Only what survives there is exported.
        private void Workshop_Edit2DOutlines(object sender, RoutedEventArgs e)
        {
            var window = new OutlineGalleryWindow
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            window.ShowDialog();
        }

        /// Opens the History window, where each tab can be staged for the workbook
        /// that the All Geometric Data export writes.
        private void Workshop_EditAllGeometric(object sender, RoutedEventArgs e)
            => Menu_SeeHistory(this, new RoutedEventArgs());

        /// Opens the table editor for one category and clears the workspace visuals of
        /// anything deleted while it was open.
        private void OpenWorkshopEditor(WorkshopCategory category)
        {
            if (UndoRedoManager == null) return;

            var window = new WorkshopEditWindow(
                category, UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            window.ShowDialog();

            // Deleted operations may still have drawings on the canvas; the operation
            // itself is already gone from history.
            foreach (var op in window.RemovedOperations)
            {
                if (op.Elements == null) continue;
                foreach (var element in op.Elements)
                    UI_WorkCanvas.Children.Remove(element);
            }

            UpdateAttemptCounter();
        }

        private void Workshop_Export2DOutlines(object sender, RoutedEventArgs e)
        {
            var availability = OutlineShapeExporter.Survey();

            // Nothing to export until the user has stored at least one silhouette.
            if (availability.TracedCount == 0)
            {
                MessageBox.Show(
                    this,
                    "There are no stored outlines to export yet.\n\n" +
                    "Trace or hand-draw an outline, generate its metadata, then use " +
                    "\"Commit Outline to History\" in the Outline panel to keep it.",
                    "Export 2D Outlines",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new OutlineExportWindow(availability)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                int written = OutlineShapeExporter.ExportAll(dialog.Options);

                MessageBox.Show(
                    this,
                    written == 1
                        ? $"1 outline exported to:\n{dialog.Options.Folder}"
                        : $"{written} outlines exported to:\n{dialog.Options.Folder}",
                    "Export 2D Outlines",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex) 
            {
                MessageBox.Show(
                        this,
                        $"Could not finish the export:\n{ex.Message}",
                        "Export failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            }
        }
    }
}