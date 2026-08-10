using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DinoLino
{
    /// <summary>
    /// Working-image lifecycle, workspace transforms, image export, picture adjustment, and scale calibration.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Image adjustment
        // =====================

        private ImageAdjuster _imageAdjuster = new ImageAdjuster();

        // Current adjustment values, stored so the dialog can reopen with the last settings.
        private double _currentContrast = 0;
        private double _currentBrightness = 0;
        private double _currentSaturation = 0;

        // =====================
        // Scale capture
        // =====================

        private bool _scaleMode = false;
        private int _scaleClicks = 0;
        private Line _scaleLine;

        // Marker that follows the cursor while scale capture is active, so the
        // user can see that the tool is armed.
        private Ellipse _scaleCueDot;

        // =====================
        // Workspace reset
        // =====================

        /// <summary>
        /// Clears the active operation history and the current workspace visuals.
        /// </summary>
        private void ClearAllOperations()
        {
            UndoRedoManager?.Clear();
            ClearWorkspace();
        }

        /// <summary>
        /// Removes workspace overlays, restores the cursor, and resets the active mode.
        /// </summary>
        private void ClearWorkspace()
        {
            // A hard reset invalidates any half-finished calibration line, so end
            // the capture and remove its cue before wiping the canvas.
            CancelScaleCapture();

            UI_WorkCanvas.Children.Clear();
            AddElementToWorkSpace(UI_DotCursor);
            UI_DotCursor.SetPosition(0, 0);

            // Clear any outline-specific preview that depends on the previous workspace state.
            OutlineMode?.ClearEFDPreview();

            CurrentWorkMode.Reset();
        }

        /// <summary>
        /// Ensures an element is attached to the workspace canvas and not to another parent.
        /// </summary>
        private void AddElementToWorkSpace(UIElement element)
        {
            if (element == null) return;

            if (element is FrameworkElement fe && fe.Parent is Panel logicalPanel)
            {
                logicalPanel.Children.Remove(element);
            }
            else
            {
                // Some workspace elements may still be attached through the visual tree.
                var visualParent = VisualTreeHelper.GetParent(element);
                if (visualParent is Panel visualPanel)
                    visualPanel.Children.Remove(element);
            }

            if (!UI_WorkCanvas.Children.Contains(element))
                UI_WorkCanvas.Children.Add(element);
        }

        // =====================
        // Zoom
        // =====================

        private void UpdateWorkSpaceZoom(double delta, Point relativeTo)
        {
            UI_WorkImage.ZoomElement(delta, relativeTo);
            UI_WorkBorder.CopyTransforms(UI_WorkImage);
        }

        private void ResetWorkSpaceZoom()
        {
            UI_WorkImage.ResetZoom();
            UI_WorkBorder.CopyTransforms(UI_WorkImage);
        }

        // =====================
        // Open image
        // =====================

        private void Menu_OpenImage(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                // Start in the Directory panel's working folder when one is set.
                InitialDirectory = DialogInitialDirectory
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            OpenImageFromPath(openFileDialog.FileName);
        }

        /// Loads an image file as a new specimen. Shared by the File menu and the
        /// Directory panel, so it validates the file rather than trusting the caller.
        internal void OpenImageFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            BitmapImage bmp;
            try
            {
                // OnLoad reads the file up front and releases the handle, so the image
                // can still be renamed or deleted from the Directory panel afterwards.
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
                bmp.EndInit();
                bmp.Freeze();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not open this image:\n{ex.Message}",
                    "Open Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Only stash the outgoing specimen once the new image has actually loaded.
            if (SpecimenManager.HasOpenedImage)
                UndoRedoManager.StashActiveSpecimen(SpecimenManager.CurrentSpecimen, SpecimenManager.DisplayName);

            SetWorkspaceImage(bmp, System.IO.Path.GetFileName(path), registerAsNewSpecimen: true);

            // A 2D image does not use the 3D reposition workflow.
            _workingImageIsModelCapture = false;
            _activeMesh = null;
            _activeModelName = null;
            UI_MenuReposition3D.IsEnabled = false;
        }

        /// <summary>
        /// Loads an image into the workspace and refreshes all state derived from it.
        /// </summary>
        private void SetWorkspaceImage(BitmapSource bmp, string specimenName, bool registerAsNewSpecimen)
        {
            WorkingImage = bmp;
            UI_WorkImage.Source = WorkingImage;

            if (registerAsNewSpecimen)
                SpecimenManager.OnImageOpened(bmp, specimenName);

            ResetWorkSpaceZoom();
            ScaleCalibration.Clear();
            ClearWorkspace();
            RefreshAllScalePlaceholders();

            _imageAdjuster.CacheImage(WorkingImage);
            OutlineMode.SourceImage = WorkingImage;

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(SyncOutlineImageTransform));
        }

        /// Empties the workspace entirely. Used when the loaded specimen is deleted and
        /// no other specimen still holds an image to fall back to.
        internal void ClearWorkspaceImage()
        {
            WorkingImage = null;
            UI_WorkImage.Source = null;

            ScaleCalibration.Clear();
            ResetWorkSpaceZoom();
            ClearWorkspace();
            RefreshAllScalePlaceholders();

            // OutlineMode treats a null source as "no image", which stops its pending
            // analysis and makes clicks no-op.
            OutlineMode.SourceImage = null;

            // A blank workspace is not a 3D capture either.
            _workingImageIsModelCapture = false;
            _activeMesh = null;
            _activeModelName = null;
            UI_MenuReposition3D.IsEnabled = false;
        }

        /// <summary>
        /// Aligns outline-mode coordinates with the displayed image after layout completes.
        /// </summary>
        private void SyncOutlineImageTransform()
        {
            if (WorkingImage == null) return;

            double displayW = UI_WorkImage.ActualWidth;
            double displayH = UI_WorkImage.ActualHeight;
            if (displayW <= 0 || displayH <= 0) return;

            OutlineMode.ScaleX = displayW / WorkingImage.PixelWidth;
            OutlineMode.ScaleY = displayH / WorkingImage.PixelHeight;

            // TranslatePoint gives the image's offset in canvas coordinates.
            var imagePos = UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkCanvas);
            OutlineMode.OffsetX = imagePos.X;
            OutlineMode.OffsetY = imagePos.Y;
        }

        // =====================
        // Image transforms
        // =====================

        private void Menu_FlipHorizontal(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new ScaleTransform(-1, 1));

        private void Menu_FlipVertical(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new ScaleTransform(1, -1));

        private void Menu_RotateRight(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new RotateTransform(90));

        private void Menu_RotateLeft(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new RotateTransform(270));   // 270° clockwise equals 90° counter-clockwise.

        /// <summary>
        /// Applies a geometric transform to the active image and reloads the workspace state.
        /// </summary>
        private void ApplyImageTransform(Transform transform)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var transformed = new TransformedBitmap(WorkingImage, transform);

            // Re-encode the transformed bitmap so it can be cached and reused like a normal image source.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(transformed));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            var bmi = new BitmapImage();
            bmi.BeginInit();
            bmi.CacheOption = BitmapCacheOption.OnLoad;
            bmi.StreamSource = stream;
            bmi.EndInit();
            bmi.Freeze();

            WorkingImage = bmi;
            UI_WorkImage.Source = WorkingImage;

            // A flip or rotation changes the image geometry, so existing overlays and scale calibration
            // must be rebuilt against the new image.
            ResetWorkSpaceZoom();
            ScaleCalibration.Clear();
            ClearWorkspace();
            RefreshAllScalePlaceholders();

            OutlineMode.SourceImage = WorkingImage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(SyncOutlineImageTransform));
        }

        // =====================
        // Screenshot export
        // =====================

        private void Menu_Screenshot(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var rtb = RenderWorkspaceToBitmap(2.0); // Supersample for a sharper export.
            if (rtb == null) return;

            var dlg = new SaveFileDialog
            {
                Title = "Save Screenshot",
                FileName = SanitizeFileName(SpecimenManager.DisplayName),
                Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|TIFF image (*.tif)|*.tif",
                DefaultExt = ".png",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            // Use the extension the user actually chose so typed filenames are respected.
            BitmapEncoder encoder = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".tif" or ".tiff" => new TiffBitmapEncoder(),
                _ => new PngBitmapEncoder()
            };
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            try
            {
                using var stream = System.IO.File.Create(dlg.FileName);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the screenshot:\n{ex.Message}",
                    "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Renders the workspace, including visible overlays, to a bitmap.
        /// </summary>
        private RenderTargetBitmap RenderWorkspaceToBitmap(double scale)
        {
            double w = UI_WorkSpace.ActualWidth, h = UI_WorkSpace.ActualHeight;
            if (w <= 0 || h <= 0) return null;

            var cursorVis = UI_DotCursor.Visibility;
            try
            {
                UI_DotCursor.Visibility = Visibility.Collapsed;
                UI_WorkSpace.UpdateLayout();

                var rtb = new RenderTargetBitmap(
                    (int)(w * scale), (int)(h * scale),
                    96 * scale, 96 * scale,
                    PixelFormats.Pbgra32);
                rtb.Render(UI_WorkSpace);
                return rtb;
            }
            finally
            {
                UI_DotCursor.Visibility = cursorVis;   // Restore the cursor even if rendering fails.
                UI_WorkSpace.UpdateLayout();
            }
        }

        /// <summary>
        /// Removes invalid filename characters and returns a safe default when needed.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "screenshot";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // =====================
        // Picture adjustment
        // =====================

        private void Menu_PictureAdjustment(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PictureAdjustmentWindow adjustWindow = new PictureAdjustmentWindow(_currentContrast, _currentBrightness, _currentSaturation);
            adjustWindow.FontSize = _currentFontSize;
            adjustWindow.FontFamily = _currentFont;

            adjustWindow.OnAdjustmentChanged = (contrast, brightness, saturation) =>
            {
                _currentContrast = contrast;
                _currentBrightness = brightness;
                _currentSaturation = saturation;

                // Values are stored as percentages in the dialog and converted to normalized adjustments here.
                _imageAdjuster.RequestAdjustment(
                    contrast / 100.0,
                    brightness / 100.0,
                    saturation / 100.0);
            };

            adjustWindow.Show();
        }

        // =====================
        // Downsampling
        // =====================

        private void Menu_DownSample(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long originalPixelCount = (long)WorkingImage.PixelWidth * WorkingImage.PixelHeight;

            DownSampleWindow sampleWindow = new DownSampleWindow(originalPixelCount);
            sampleWindow.FontSize = _currentFontSize;
            sampleWindow.FontFamily = _currentFont;

            sampleWindow.OnPixelsChanged = targetPixels =>
            {
                var result = _imageAdjuster.DownSample(targetPixels);
                UI_WorkImage.Source = result ?? WorkingImage;
            };

            sampleWindow.Show();
        }

        // =====================
        // Global actions
        // =====================

        // Remembers "Don't show this message again" for the rest of the session.
        private bool _suppressClearAllPrompt;

        /// Clear All: returns the program to how it opened — every specimen, every
        /// measurement, and every cached image gone.
        private void GlobalTools_Clear(object sender, RoutedEventArgs e)
        {
            if (!_suppressClearAllPrompt)
            {
                bool dontAskAgain;
                bool confirmed = ConfirmPromptWindow.Show(
                    this,
                    "Clear All",
                    "Are you sure? This will clear all data for all specimens",
                    out dontAskAgain,
                    confirmText: "OK");

                // The preference is remembered even when the user cancels, matching
                // how "don't ask again" behaves elsewhere.
                if (dontAskAgain) _suppressClearAllPrompt = true;
                if (!confirmed) return;
            }

            ResetSession();
        }

        /// Empties every piece of session state: the specimens, their measurements,
        /// their images, and everything derived from them.
        private void ResetSession()
        {
            // Ticks first: they name specimens that are about to be discarded, and
            // the Sample list redraws as soon as the roster changes.
            _sampleChecked.Clear();

            // Measurements before the specimens that owned them, so the modes are
            // still told to blank the panels showing those numbers.
            UndoRedoManager.ResetSession();
            SpecimenManager.ResetSession();

            // Session-scoped table state: group columns belong to specimens that are
            // gone, hidden columns to tables that are now empty, and the staged
            // workbook sheets to a history that no longer exists.
            SpecimenGroups.Clear();
            WorkshopColumnFilter.RestoreAll();
            GeomOpHistoryWindow.ClearStagedSheets();

            // The workspace, its calibration, and the mesh kept in memory for a
            // reposition. ClearWorkspaceImage covers the rest of the 3D state.
            ClearWorkspaceImage();
            _activeModelPath = null;

            // Picture corrections are per-image, so they start from zero again.
            _currentContrast = 0;
            _currentBrightness = 0;
            _currentSaturation = 0;

            ClearPcaAnalysis();

            RebuildSampleList();
            UpdateAttemptCounter();
            RefreshPlotTab();
        }

        private void RefreshAllScalePlaceholders()
        {
            if (AllWorkModes == null) return;
            foreach (var mode in AllWorkModes)
                mode.RefreshScalePlaceholders();
        }

        /// <summary>
        /// Removes all overlay elements except the cursor.
        /// </summary>
        private void RemovePendingElements()
        {
            foreach (UIElement element in CurrentWorkMode.ElementsToRemove)
                UI_WorkCanvas.Children.Remove(element);

            CurrentWorkMode.ClearElementsToRemove();
        }

        /// <summary>
        /// Clears workspace drawings while keeping the cursor overlay in place.
        /// </summary>
        private void ClearWorkspaceVisualsOnly()
        {
            for (int i = UI_WorkCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (UI_WorkCanvas.Children[i] != UI_DotCursor)
                    UI_WorkCanvas.Children.RemoveAt(i);
            }
        }

        // =====================
        // Scale calibration
        // =====================

        /// Arms scale-calibration capture: the next two workspace clicks define the
        /// calibration line. Invoked from Tools ▸ Set Scale (Menu_SetScale).
        internal void BeginScaleCapture()
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Restarting always discards any half-finished capture.
            CancelScaleCapture();

            _scaleClicks = 0;
            _scaleMode = true;   // The next two workspace clicks define the calibration line.

            ShowScaleCue(new Vector2(Mouse.GetPosition(UI_WorkCanvas)));
        }

        /// <summary>
        /// Cancels an in-progress scale capture and removes its visuals.
        /// </summary>
        internal void CancelScaleCapture()
        {
            _scaleMode = false;
            _scaleClicks = 0;

            if (_scaleLine != null)
            {
                UI_WorkCanvas.Children.Remove(_scaleLine);
                _scaleLine = null;
            }

            EndScaleCue();
        }

        /// Places the cue dot at the given canvas position and switches to a
        /// crosshair cursor, signalling that scale capture has started.
        private void ShowScaleCue(Vector2 pos)
        {
            if (_scaleCueDot == null)
            {
                _scaleCueDot = new Ellipse
                {
                    Fill = Brushes.Yellow,   // Matches the dashed calibration line.
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Width = 11,
                    Height = 11
                };
                AddElementToWorkSpace(_scaleCueDot);
            }

            MoveScaleCue(pos);
            UI_WorkSpace.Cursor = Cursors.Cross;
        }

        /// <summary>
        /// Keeps the cue dot centred on the cursor while scale capture is active.
        /// </summary>
        internal void MoveScaleCue(Vector2 pos)
        {
            _scaleCueDot?.SetPosition(pos.X - 5.5, pos.Y - 5.5);
        }

        /// <summary>
        /// Removes the cue dot and restores the normal workspace cursor.
        /// </summary>
        private void EndScaleCue()
        {
            if (_scaleCueDot != null)
            {
                UI_WorkCanvas.Children.Remove(_scaleCueDot);
                _scaleCueDot = null;
            }

            UI_WorkSpace.Cursor = null;
        }

        private void HandleScaleClick(Vector2 mousePos)
        {
            if (_scaleClicks == 0)
            {
                _scaleLine = new Line
                {
                    Stroke = Brushes.Yellow,
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    X1 = mousePos.X,
                    Y1 = mousePos.Y,
                    X2 = mousePos.X,
                    Y2 = mousePos.Y
                };
                AddElementToWorkSpace(_scaleLine);
                _scaleClicks = 1;
                return;
            }

            _scaleLine.X2 = mousePos.X;
            _scaleLine.Y2 = mousePos.Y;

            double dx = _scaleLine.X2 - _scaleLine.X1;
            double dy = _scaleLine.Y2 - _scaleLine.Y1;
            FinishScaleCapture(Math.Sqrt(dx * dx + dy * dy));
        }

        private void FinishScaleCapture(double pixelLength)
        {
            _scaleMode = false;
            _scaleClicks = 0;

            // Capture is over: remove the cue dot and restore the cursor before
            // the dialog appears.
            EndScaleCue();

            if (pixelLength < 1e-3)
            {
                if (_scaleLine != null)
                {
                    UI_WorkCanvas.Children.Remove(_scaleLine);
                    _scaleLine = null;
                }
                return;
            }

            var dlg = new ScaleWindow
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            if (dlg.ShowDialog() == true)
            {
                ScaleCalibration.SetFromLine(pixelLength, dlg.LengthValue, dlg.SelectedUnit);
                RefreshAllScalePlaceholders();
            }

            if (_scaleLine != null)
            {
                UI_WorkCanvas.Children.Remove(_scaleLine);
                _scaleLine = null;
            }
        }
    }
}