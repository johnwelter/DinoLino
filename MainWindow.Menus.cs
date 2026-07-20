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
    // Menu handlers not owned by a feature file (help, undo/redo, history window and
    // export, color, font) plus the rotating tip engine and view toggles.
    // Split from MainWindow.xaml.cs; no logic changes.
    public partial class MainWindow
    {
        private void Menu_About(object sender, RoutedEventArgs e)
        {
            AboutWindow about = new AboutWindow();
            about.FontSize = _currentFontSize;
            about.FontFamily = _currentFont;
            about.ShowDialog();
        }

        // Added User Guide
        private void Menu_UserGuide(object sender, RoutedEventArgs e)
        {
            UserGuideWindow userguide = new UserGuideWindow();
            userguide.FontFamily = _currentFont;
            userguide.FontSize = _currentFontSize;
            userguide.ShowDialog();
        }

        // Undo function
        private void Menu_Undo(object sender, RoutedEventArgs e)
        {
            var result = UndoRedoManager.Undo();

            if (result == null) return;
            foreach (var el in result.Elements)
                UI_WorkCanvas.Children.Remove(el);
        }

        // Redo function
        private void Menu_Redo(object sender, RoutedEventArgs e)
        {
            var result = UndoRedoManager.Redo();

            if (result == null) return;
            {
                foreach (var el in result.Elements)
                {
                    AddElementToWorkSpace(el);
                }
            }
        }

        private void BindUndoRedoMenuItems()
        {
            Binding undoBinding = new Binding(nameof(WorkMode.CanUndo));
            undoBinding.Source = UndoRedoManager;
            UI_MenuUndo.SetBinding(MenuItem.IsEnabledProperty, undoBinding);

            Binding redoBinding = new Binding(nameof(WorkMode.CanRedo));
            redoBinding.Source = UndoRedoManager;
            UI_MenuRedo.SetBinding(MenuItem.IsEnabledProperty, redoBinding);
        }

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
            GeomOpHistoryWindow.ExportAllOperationHistory(UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
        }

        // Tips visibility
        private bool _tipsVisible = true;
        private DispatcherTimer _tipCycleTimer;
        private int _tipIndex = 0;

        // Tools > Clear Image Cache: releases every cached specimen bitmap in one
        // sweep. Only Specimen.Image refs are dropped — names, file names, live
        // history, and the archive are untouched, so exports are unaffected.
        private void Menu_ClearImageCache(object sender, RoutedEventArgs e)
        {
            int n = SpecimenManager.CachedImageCount;
            if (n == 0)
            {
                MessageBox.Show(this, "There are no cached images to clear.",
                    "Clear Image Cache", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this,
                $"Release all {n} cached image(s)?\n\n" +
                "The specimen \u25b2/\u25bc arrows will no longer cycle through past images, " +
                "and released images cannot be brought back without re-opening their files.\n\n" +
                "Specimen names and all measurements are kept — the History window and " +
                "exported tables are unaffected.",
                "Clear Image Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SpecimenManager.ClearAllImages();
        }

        // Tools > Edit Image Cache: per-image release via a pop-up roster.
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
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800));
                UI_TipText.BeginAnimation(TextBlock.OpacityProperty, fadeIn);
            };
            UI_TipText.BeginAnimation(TextBlock.OpacityProperty, fadeOut);
        }

        private void Menu_SeePrevOps(object sender, RoutedEventArgs e)
        {
            bool isChecked = UI_SeePrevOps.IsChecked;

            // Update the global preference 
            foreach (var mode in AllWorkModes)
            {
                mode.SeePreviousOperations = isChecked;
            }

            // Refresh screen
            if (!isChecked)
            {
                ClearWorkspaceVisualsOnly();
            }
            else
            {
                ClearWorkspaceVisualsOnly();

                foreach (var operation in UndoRedoManager.History)
                {
                    foreach (var el in operation.Elements)
                        AddElementToWorkSpace(el);
                }
            }
        }

        private void Menu_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                menuItem.IsSubmenuOpen = false;
            }
        }

        private void Menu_Color_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null)
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(rb.Tag.ToString());
                CurrentWorkMode.LineColor = brush;
            }
        }

        private void Menu_Font(object sender, RoutedEventArgs e)
        {
            FontWindow fontWindow = new FontWindow(_currentFontSize, _currentFont);
            fontWindow.FontSize = _currentFontSize;
            fontWindow.FontFamily = _currentFont;

            fontWindow.OnFontSizeChanged = size =>
            {
                _currentFontSize = size;
                TextElement.SetFontSize(UI_ControlPanel, size);
                UI_TipText.FontSize = size;

                UI_AttemptHeader.FontSize = size;
                UI_AttemptCirc.FontSize = size;
                UI_AttemptPara.FontSize = size;
                UI_AttemptSpline.FontSize = size;
                UI_AttemptAngle.FontSize = size;
                UI_AttemptLine.FontSize = size;
                UI_AttemptOutline.FontSize = size;

                fontWindow.FontSize = size;
            };

            fontWindow.OnFontFamilyChanged = family =>
            {
                _currentFont = family;
                TextElement.SetFontFamily(UI_ControlPanel, family);
                UI_TipText.FontFamily = family;

                UI_AttemptHeader.FontFamily = family;
                UI_AttemptCirc.FontFamily = family;
                UI_AttemptPara.FontFamily = family;
                UI_AttemptSpline.FontFamily = family;
                UI_AttemptAngle.FontFamily = family;
                UI_AttemptLine.FontFamily = family;
                UI_AttemptOutline.FontFamily = family;

                fontWindow.FontFamily = family;
            };

            fontWindow.Show(); // use Show() instead of ShowDialog() so the user can adjust font while seeing the main window update live
        }
    }
}