using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DinoLino
{
    // Working-image lifecycle: opening 2D images, swapping/clearing the workspace,
    // zoom math, flips/rotations, screenshot export, picture adjustment, downsampling,
    // and the scale-calibration flow. Split from MainWindow.xaml.cs; no logic changes.
    public partial class MainWindow
    {

        // image adjuster fields
        private ImageAdjuster _imageAdjuster = new ImageAdjuster();

        // Picture adjustment state
        private double _currentContrast = 0;
        private double _currentBrightness = 0;
        private double _currentSaturation = 0;

        // --- Scale Image capture state ---
        private bool _scaleMode = false;
        private int _scaleClicks = 0;
        private Line _scaleLine;

        // Full reset: clears the undo/redo history (counter returns to zero, undo/redo
        // disabled) AND clears the workspace visuals. Used by "Clear All" and Ctrl+C.
        // NOT used on image-open, so the counter survives loading a new image.
        private void ClearAllOperations()
        {
            UndoRedoManager?.Clear();
            ClearWorkspace();
        }
        private void ClearWorkspace()
        {
            // Clear everything in the workspace, put the cursor back in
            UI_WorkCanvas.Children.Clear();
            AddElementToWorkSpace(UI_DotCursor);
            UI_DotCursor.SetPosition(0, 0);
            OutlineMode?.ClearEFDPreview();

            //reset the work mode
            CurrentWorkMode.Reset();
        }

        private void AddElementToWorkSpace(UIElement element)
        {
            if (element == null) return;

            // Remove from logical parent first
            if (element is FrameworkElement fe && fe.Parent is Panel logicalPanel)
            {
                logicalPanel.Children.Remove(element);
            }
            else
            {
                // Fall back to visual parent
                var visualParent = VisualTreeHelper.GetParent(element);
                if (visualParent is Panel visualPanel)
                    visualPanel.Children.Remove(element);
            }

            // Add to workspace if not already there
            if (!UI_WorkCanvas.Children.Contains(element))
                UI_WorkCanvas.Children.Add(element);
        }

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


        private void Menu_OpenImage(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            if (openFileDialog.ShowDialog() != true)
                return;

            if (SpecimenManager.HasOpenedImage)
                UndoRedoManager.ArchiveAndReset(SpecimenManager.DisplayName);

            BitmapImage bmp = new BitmapImage(
                new Uri(openFileDialog.FileName, UriKind.RelativeOrAbsolute));

            SetWorkspaceImage(bmp, openFileDialog.SafeFileName, registerAsNewSpecimen: true);

            // A 2D image can't be re-posed: disable reposition and drop any retained mesh.
            _workingImageIsModelCapture = false;
            _activeMesh = null;
            _activeModelName = null;
            UI_MenuReposition3D.IsEnabled = false;
        }

        // Swaps the working image and refreshes everything derived from it. When
        // registerAsNewSpecimen is true this counts as opening a new specimen (advances the
        // specimen counter and updates the loaded-file label); repositioning a 3D model passes
        // false, because a new capture of the same object is still the same specimen.
        private void SetWorkspaceImage(BitmapSource bmp, string specimenName, bool registerAsNewSpecimen)
        {
            WorkingImage = bmp;
            UI_WorkImage.Source = WorkingImage;

            if (registerAsNewSpecimen)
                SpecimenManager.OnImageOpened(specimenName);

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

        private void SyncOutlineImageTransform()
        {
            if (WorkingImage == null) return;
            double displayW = UI_WorkImage.ActualWidth;
            double displayH = UI_WorkImage.ActualHeight;
            if (displayW <= 0 || displayH <= 0) return;

            OutlineMode.ScaleX = displayW / WorkingImage.PixelWidth;
            OutlineMode.ScaleY = displayH / WorkingImage.PixelHeight;

            // Offset of the image within the canvas
            var imagePos = UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkCanvas);
            OutlineMode.OffsetX = imagePos.X;
            OutlineMode.OffsetY = imagePos.Y;
        }

        private void Menu_FlipHorizontal(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new ScaleTransform(-1, 1));

        private void Menu_FlipVertical(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new ScaleTransform(1, -1));

        private void Menu_RotateRight(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new RotateTransform(90));

        private void Menu_RotateLeft(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new RotateTransform(270));   // 270° clockwise = 90° counter-clockwise

        // Applies a geometric transform (mirror flip or 90° rotation) to the working image.
        // TransformedBitmap supports negative ScaleTransforms (mirroring) and RotateTransforms
        // at 0/90/180/270 degrees. The result is re-encoded to a BitmapImage — the same
        // round-trip the image adjuster uses — so it matches WorkingImage's type and can be
        // re-cached by OutlineMode.
        private void ApplyImageTransform(Transform transform)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var transformed = new TransformedBitmap(WorkingImage, transform);

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

            // Flipping or rotating changes the image geometry, so existing overlays would no
            // longer line up, and a canvas-space scale calibration may no longer be valid
            // (a 90° rotation of a non-square image changes the on-screen fit). Treat it like
            // loading a fresh image: reset zoom, clear the scale, clear the workspace.
            ResetWorkSpaceZoom();
            ScaleCalibration.Clear();
            ClearWorkspace();
            RefreshAllScalePlaceholders();

            OutlineMode.SourceImage = WorkingImage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(SyncOutlineImageTransform));
        }

        private void Menu_Screenshot(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var rtb = RenderWorkspaceToBitmap(2.0); // 2x supersample for a crisp capture
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

            // Choose the encoder from the extension the user actually saved with, so a
            // hand-typed ".tif" is honored even if the filter dropdown says PNG.
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

        // Renders the workspace (image + every visible overlay, at the current zoom/pan)
        // to a bitmap, supersampled by `scale`. UI_WorkSpace has ClipToBounds and the bitmap
        // is sized to it, so any panned-off part of the image is clipped away. The white
        // follow-cursor dot is hidden during the capture so it doesn't appear in the file.
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
                UI_DotCursor.Visibility = cursorVis;   // always restore, even if Render throws
                UI_WorkSpace.UpdateLayout();
            }
        }

        // Strips characters invalid in file names so the specimen-derived default is always
        // valid; falls back to "screenshot" if the specimen name is blank.
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "screenshot";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
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
                _imageAdjuster.RequestAdjustment(
                    contrast / 100.0,
                    brightness / 100.0,
                    saturation / 100.0);
            };

            adjustWindow.Show();
        }

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
                if (result != null)
                    UI_WorkImage.Source = result;
                else
                    UI_WorkImage.Source = WorkingImage;
            };

            sampleWindow.Show();
        }

        private void GlobalTools_Clear(object sender, RoutedEventArgs e)
        {
            ClearAllOperations();
        }

        private void RefreshAllScalePlaceholders()
        {
            if (AllWorkModes == null) return;
            foreach (var mode in AllWorkModes)
                mode.RefreshScalePlaceholders();
        }

        // Helper method for single click and double click finishing
        private void RemovePendingElements()
        {
            foreach (UIElement element in CurrentWorkMode.ElementsToRemove)
            {
                UI_WorkCanvas.Children.Remove(element);
            }
            CurrentWorkMode.ClearElementsToRemove();
        }

        // Helper method to clear the drawings but keep the cursor
        private void ClearWorkspaceVisualsOnly()
        {
            // Loop backwards to safely remove children while keeping the UI_DotCursor
            for (int i = UI_WorkCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (UI_WorkCanvas.Children[i] != UI_DotCursor)
                {
                    UI_WorkCanvas.Children.RemoveAt(i);
                }
            }
        }

        private void GlobalTools_ScaleImage(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_scaleLine != null) { UI_WorkCanvas.Children.Remove(_scaleLine); _scaleLine = null; }
            _scaleClicks = 0;
            _scaleMode = true;   // next two workspace clicks define the calibration line
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

            if (pixelLength < 1e-3)
            {
                if (_scaleLine != null) { UI_WorkCanvas.Children.Remove(_scaleLine); _scaleLine = null; }
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

            if (_scaleLine != null) { UI_WorkCanvas.Children.Remove(_scaleLine); _scaleLine = null; }
        }
    }
}