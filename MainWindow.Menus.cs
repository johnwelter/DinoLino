using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DinoLino
{
    /// Menu handlers, tip rotation, undo/redo bindings, and shared view toggles for the
    /// main window.
    public partial class MainWindow
    {
        // ---- Dialogs ----

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow
            {
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };
            about.ShowDialog();
        }

        private void Menu_UserGuide(object sender, RoutedEventArgs e)
        {
            var userguide = new UserGuideWindow
            {
                FontFamily = _currentFont,
                FontSize = _currentFontSize
            };
            userguide.ShowDialog();
        }

        // ---- Undo / Redo ----

        private void Menu_Undo(object sender, RoutedEventArgs e)
        {
            var result = UndoRedoManager.Undo();
            if (result == null) return;

            // Remove the visual elements that were added by the undone operation.
            foreach (var el in result.Elements)
                UI_WorkCanvas.Children.Remove(el);
        }

        private void Menu_Redo(object sender, RoutedEventArgs e)
        {
            var result = UndoRedoManager.Redo();
            if (result == null) return;

            // Re-add the visuals associated with the redone operation.
            foreach (var el in result.Elements)
                AddElementToWorkSpace(el);
        }

        private void BindUndoRedoMenuItems()
        {
            var undoBinding = new Binding(nameof(WorkMode.CanUndo)) { Source = UndoRedoManager };
            UI_MenuUndo.SetBinding(MenuItem.IsEnabledProperty, undoBinding);

            var redoBinding = new Binding(nameof(WorkMode.CanRedo)) { Source = UndoRedoManager };
            UI_MenuRedo.SetBinding(MenuItem.IsEnabledProperty, redoBinding);
        }

        // ---- History ----

        private void Menu_SeeHistory(object sender, RoutedEventArgs e)
        {
            var window = new GeomOpHistoryWindow(UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };
            window.Show();
        }

        private void Menu_ExportHistory(object sender, RoutedEventArgs e)
        {
            GeomOpHistoryWindow.ExportAllOperationHistory(
                UndoRedoManager,
                SpecimenManager.DisplayName,
                ScaleCalibration);
        }

        // ---- Image cache ----

        /// Removes every cached specimen image in one step, after confirming with the
        /// user.
        private void Menu_ClearImageCache(object sender, RoutedEventArgs e)
        {
            int cachedCount = SpecimenManager.CachedImageCount;

            if (cachedCount == 0)
            {
                MessageBox.Show(
                    this,
                    "There are no cached images to clear.",
                    "Clear Image Cache",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"Remove all {cachedCount} cached image(s)?\n\n" +
                "The specimen \u25b2/\u25bc arrows will no longer cycle through those images, " +
                "and they cannot be brought back without re-opening their files.\n\n" +
                "Specimen names and all measurements are kept, so the History window and " +
                "exported tables are unaffected.",
                "Clear Image Cache",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            SpecimenManager.ClearAllImages();
        }

        /// <summary>Opens the cache roster so images can be removed one at a time.</summary>
        private void Menu_EditImageCache(object sender, RoutedEventArgs e)
        {
            var window = new EditImageCacheWindow(SpecimenManager)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            window.ShowDialog();
        }

        // ---- Scale calibration ----

        /// Arms the scale-calibration capture (Tools ▸ Set Scale). The capture
        /// itself lives with the workspace code, which owns BeginScaleCapture.
        private void Menu_SetScale(object sender, RoutedEventArgs e)
        {
            BeginScaleCapture();
        }

        // ---- Alignment ----

        /// Arms the axis-drawing capture (Tools ▸ Align Image, and the control
        /// panel's Align button). The capture itself lives in the alignment file,
        /// which owns BeginAlignCapture.
        private void Menu_AlignImage(object sender, RoutedEventArgs e)
        {
            BeginAlignCapture();
        }

        // ---- Image tool availability ----

        /// Enables or disables the tools that need a loaded image, across every
        /// entry point each one has. Called wherever the working image arrives or
        /// is cleared, so no tool is offered when there is nothing to measure and
        /// the menu items cannot drift apart from the buttons.
        internal void SetImageToolsEnabled(bool enabled)
        {
            UI_MenuAlignImage.IsEnabled = enabled;
            UI_AlignButton.IsEnabled = enabled;

            UI_MenuSetScale.IsEnabled = enabled;
            UI_ScaleButton.IsEnabled = enabled;

            UI_MenuScreenshot.IsEnabled = enabled;
            UI_MenuPictureCorrections.IsEnabled = enabled;
            UI_MenuDecimate.IsEnabled = enabled;
            UI_MenuFlip.IsEnabled = enabled;
            UI_MenuRotate.IsEnabled = enabled;
        }

        /// Enables Clear Specimen Data only while the loaded specimen has something
        /// to clear. Undone operations count: they are still on the specimen's
        /// record and the button discards them along with the rest.
        internal void UpdateClearSpecimenEnabled()
        {
            UI_ClearSpecimenButton.IsEnabled =
                UndoRedoManager.CanUndo || UndoRedoManager.CanRedo;
        }

        /// Enables Clear All only while the session holds at least one measurement,
        /// in an archived specimen or in the live one. Undone operations count: they
        /// are still recoverable, and the reset discards them along with everything
        /// else.
        internal void UpdateClearAllEnabled()
        {
            bool anyArchived = false;
            foreach (var record in UndoRedoManager.Archive)
            {
                if (record.Operations.Count > 0)
                {
                    anyArchived = true;
                    break;
                }
            }

            UI_AAClearButton.IsEnabled =
                anyArchived || UndoRedoManager.CanUndo || UndoRedoManager.CanRedo;
        }

        /// Refreshes every control whose availability depends on what the session
        /// has recorded. Call this wherever measurements are added or removed.
        internal void UpdateDataDependentControls()
        {
            UpdateClearSpecimenEnabled();
            UpdateClearAllEnabled();
            UpdateWorkshopButtonsEnabled();
        }

        // ---- Tips ----

        private bool _tipsVisible = true;
        private DispatcherTimer _tipCycleTimer;
        private int _tipIndex = 0;

        private void Menu_SeeTips(object sender, RoutedEventArgs e)
        {
            _tipsVisible = UI_SeeTips.IsChecked;
            UI_TipsBar.Visibility = _tipsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TipCycle_Tick(object sender, EventArgs e)
        {
            var tips = CurrentWorkMode?.GetTips();
            if (tips == null || tips.Length <= 1)
            {
                _tipCycleTimer.Stop();
                return;
            }

            _tipIndex = (_tipIndex + 1) % tips.Length;
            FadeTip(tips[_tipIndex]);
        }

        /// Displays the first tip for the active mode and starts cycling if more tips
        /// exist.
        public void UpdateTip()
        {
            if (!_tipsVisible) return;

            _tipCycleTimer.Stop();
            _tipIndex = 0;

            var tips = CurrentWorkMode?.GetTips();
            if (tips == null || tips.Length == 0 || string.IsNullOrEmpty(tips[0]))
            {
                UI_TipText.Text = string.Empty;
                return;
            }

            ShowTip(tips[0]);

            if (tips.Length > 1)
                _tipCycleTimer.Start();
        }

        private void ShowTip(string text)
        {
            UI_TipText.Text = text;
            UI_TipText.Opacity = 1;
        }

        private void FadeTip(string newText)
        {
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(800));
            fadeOut.Completed += (s, e) =>
            {
                UI_TipText.Text = newText;

                // Fade the new tip back in after the old one disappears.
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800));
                UI_TipText.BeginAnimation(TextBlock.OpacityProperty, fadeIn);
            };

            UI_TipText.BeginAnimation(TextBlock.OpacityProperty, fadeOut);
        }

        // ---- Workspace display ----

        private void Menu_SeePrevOps(object sender, RoutedEventArgs e)
        {
            bool isChecked = UI_SeePrevOps.IsChecked;

            // Apply the setting to every work mode so the workspace behaves consistently.
            foreach (var mode in AllWorkModes)
                mode.SeePreviousOperations = isChecked;

            // Refresh the visible workspace so the change takes effect immediately.
            ClearWorkspaceVisualsOnly();

            if (isChecked)
            {
                foreach (var operation in UndoRedoManager.History)
                    foreach (var el in operation.Elements)
                        AddElementToWorkSpace(el);
            }
        }

        /// Toggles the image-axes compass. The overlay itself lives in the alignment
        /// file, which owns SetImageAxesVisible.
        private void Menu_SeeImageAxes(object sender, RoutedEventArgs e)
        {
            SetImageAxesVisible(UI_SeeImageAxes.IsChecked);
        }

        /// Toggles the mini-map overview panel. The panel itself lives in
        /// MainWindow_Navigation.cs, which owns SetMiniMapVisible.
        private void Menu_SeeMiniMap(object sender, RoutedEventArgs e)
        {
            SetMiniMapVisible(UI_SeeMiniMap.IsChecked);
        }

        /// Toggles the Workshop sidebar. The sidebar itself lives in
        /// MainWindow_Workshop.cs, which owns SetWorkshopVisible.
        private void Menu_SeeWorkshop(object sender, RoutedEventArgs e)
        {
            SetWorkshopVisible(UI_SeeWorkshop.IsChecked);
        }

        /// Toggles the Directory panel. The browser itself lives in
        /// MainWindow_Directory.cs; the sidebar layout lives in MainWindow_Workshop.cs.
        private void Menu_SeeDirectory(object sender, RoutedEventArgs e)
        {
            SetDirectoryVisible(UI_SeeDirectory.IsChecked);
        }

        /// Shows or hides the creature artwork at the bottom of the control panel.
        private void Menu_SeeRex(object sender, RoutedEventArgs e)
        {
            UI_CreatureArtContainer.Visibility =
                UI_SeeRex.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Menu_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                menuItem.IsSubmenuOpen = false;
            }
        }

        /// The menu names one drawing color for the whole workspace, so the brush goes
        /// to every work mode rather than only the tab that happens to be open. A mode
        /// that never heard the choice would otherwise keep drawing in the color it
        /// started with, which is what a tab switched to after picking a color did.
        private void Menu_Color_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null)
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(rb.Tag.ToString());

                if (AllWorkModes == null)
                {
                    CurrentWorkMode.LineColor = brush;
                    return;
                }

                foreach (var mode in AllWorkModes)
                    mode.LineColor = brush;
            }
        }

        // ---- Settings ----

        /// Toggles whether the View settings are kept for later sessions. The reading
        /// and writing lives in MainWindow_Settings.cs, which owns SetSaveSettings.
        private void Menu_SaveSettings(object sender, RoutedEventArgs e)
        {
            SetSaveSettings(UI_MenuSaveSettings.IsChecked);
        }

        /// Returns every View setting to how the program first opens. The defaults
        /// themselves live in MainWindow_Settings.cs, which owns RestoreDefaultSettings.
        private void Menu_RestoreDefaults(object sender, RoutedEventArgs e)
        {
            RestoreDefaultSettings();
        }

        // ---- Font ----

        private void Menu_Font(object sender, RoutedEventArgs e)
        {
            var fontWindow = new FontWindow(_currentFontSize, _currentFont)
            {
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            fontWindow.OnFontSizeChanged = size =>
            {
                ApplyFontSize(size);
                fontWindow.FontSize = size;
            };

            fontWindow.OnFontFamilyChanged = family =>
            {
                ApplyFontFamily(family);
                fontWindow.FontFamily = family;
            };

            // Use a modeless window so font changes can be previewed live in the main UI.
            fontWindow.Show();
        }

        /// Applies a font size to every part of the main window that follows the
        /// View ▸ Font setting.
        private void ApplyFontSize(double size)
        {
            _currentFontSize = size;
            TextElement.SetFontSize(UI_ControlPanel, size);
            TextElement.SetFontSize(UI_WorkshopPanel, size);
            UI_TipText.FontSize = size;

            // Set on the counter overlay itself rather than row by row, so rows
            // added to it later (the per-shape tallies, and anything after them)
            // scale without another edit here. The rows carry no local FontSize,
            // which is what lets this inherit down to them.
            TextElement.SetFontSize(UI_AttemptCounter, size);
        }

        /// <summary>Applies a font family everywhere the size setting reaches.</summary>
        private void ApplyFontFamily(FontFamily family)
        {
            _currentFont = family;
            TextElement.SetFontFamily(UI_ControlPanel, family);
            TextElement.SetFontFamily(UI_WorkshopPanel, family);
            UI_TipText.FontFamily = family;
            TextElement.SetFontFamily(UI_AttemptCounter, family);
        }
    }
}