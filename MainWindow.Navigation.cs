using DinoLino.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DinoLino
{
    /// <summary>
    /// Scrollbar navigation and the mini-map overview for the zoomable workspace.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // State
        // =====================

        // Guards against feedback loops while scrollbar values are being synchronized.
        private bool _syncingNavigation;

        // Title-bar drag state for the mini-map panel.
        private bool _miniMapDraggingPanel;
        private Point _miniMapDragStart;   // Mouse position in workspace coordinates when the drag started.
        private double _miniMapStartTx, _miniMapStartTy;

        // True while a click or drag inside the map area is panning the main view.
        private bool _miniMapPanning;

        // Longest side, in pixels, of the low-resolution mini-map copy.
        private const double MiniMapMaxPixels = 320;

        // =====================
        // Helpers
        // =====================

        /// <summary>
        /// Constrains a value to the given range. Math.Clamp is unavailable on
        /// .NET Framework, so the navigation code uses this instead.
        /// </summary>
        private static double Clamp(double value, double min, double max)
        {
            if (max < min) return min;   // Degenerate range: prefer the lower bound.
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // =====================
        // Initialization
        // =====================

        /// <summary>
        /// Wires up the events the scrollbars and mini-map depend on.
        /// Call once from the MainWindow constructor, after the workspace transforms are initialized.
        /// </summary>
        private void InitializeNavigationAids()
        {
            // Every zoom, pan, and scroll mutates the image's transform group, so one
            // subscription keeps the scrollbars and mini-map in sync with all of them.
            if (UI_WorkImage.RenderTransform is TransformGroup transformGroup)
                transformGroup.Changed += (s, e) => SyncViewportNavigation();

            // Resizing the window or loading a differently shaped image changes what is visible.
            UI_WorkSpace.SizeChanged += (s, e) => SyncViewportNavigation();
            UI_WorkImage.SizeChanged += (s, e) => SyncViewportNavigation();

            // Resizing the mini-map re-letterboxes its picture, which moves the view rectangle.
            UI_MiniMapImage.SizeChanged += (s, e) => UpdateMiniMapViewRect();

            // Rebuild the low-res copy whenever the displayed source changes
            // (open image, picture corrections, decimate, flips and rotations, 3D captures).
            DependencyPropertyDescriptor
                .FromProperty(Image.SourceProperty, typeof(Image))
                .AddValueChanged(UI_WorkImage, (s, e) =>
                {
                    RefreshMiniMapImage();

                    // Layout has not run for the new source yet, so sync after it completes.
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(SyncViewportNavigation));
                });
        }

        // =====================
        // Scrollbars
        // =====================

        /// <summary>
        /// Recomputes both scrollbars and the mini-map view rectangle from the current zoom and pan.
        /// </summary>
        private void SyncViewportNavigation()
        {
            if (_syncingNavigation) return;

            _syncingNavigation = true;
            try
            {
                SyncScrollBars();
                UpdateMiniMapViewRect();
            }
            finally
            {
                _syncingNavigation = false;
            }
        }

        private void SyncScrollBars()
        {
            double viewW = UI_WorkSpace.ActualWidth;
            double viewH = UI_WorkSpace.ActualHeight;
            double imgW = UI_WorkImage.ActualWidth;
            double imgH = UI_WorkImage.ActualHeight;

            if (viewW <= 0 || viewH <= 0 || imgW <= 0 || imgH <= 0)
            {
                DisableScrollBar(UI_HScrollBar);
                DisableScrollBar(UI_VScrollBar);
                return;
            }

            var scale = UI_WorkImage.GetScaleTransform();

            // Top-left corner of the displayed image in workspace coordinates,
            // including the current zoom and pan.
            Point contentTopLeft = UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkSpace);

            SyncScrollBarAxis(UI_HScrollBar, imgW * scale.ScaleX, viewW, contentTopLeft.X);
            SyncScrollBarAxis(UI_VScrollBar, imgH * scale.ScaleY, viewH, contentTopLeft.Y);
        }

        private static void SyncScrollBarAxis(ScrollBar bar, double contentSize, double viewportSize, double contentOffset)
        {
            // Nothing to scroll when the image fits inside the viewport on this axis.
            if (contentSize <= viewportSize + 0.5)
            {
                DisableScrollBar(bar);
                return;
            }

            bar.IsEnabled = true;
            bar.Maximum = contentSize - viewportSize;
            bar.ViewportSize = viewportSize;   // Sizes the draggable thumb proportionally.
            bar.SmallChange = 20;
            bar.LargeChange = viewportSize * 0.9;

            // -contentOffset is how much of the image is hidden before the viewport's edge.
            bar.Value = Clamp(-contentOffset, 0, bar.Maximum);
        }

        private static void DisableScrollBar(ScrollBar bar)
        {
            bar.Value = 0;
            bar.Maximum = 0;
            bar.ViewportSize = 1;
            bar.IsEnabled = false;
        }

        private void WorkSpaceScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Ignore value changes that SyncScrollBars itself produced.
            if (_syncingNavigation) return;
            if (UI_WorkImage.ActualWidth <= 0 || UI_WorkImage.ActualHeight <= 0) return;

            var tt = UI_WorkImage.GetTranslateTransform();

            // The image's layout position with the render transform removed.
            Point contentTopLeft = UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkSpace);
            double baseX = contentTopLeft.X - tt.X;
            double baseY = contentTopLeft.Y - tt.Y;

            // Scrolling to value v means v pixels of content sit before the viewport edge.
            if (ReferenceEquals(sender, UI_HScrollBar))
                tt.X = -UI_HScrollBar.Value - baseX;
            else
                tt.Y = -UI_VScrollBar.Value - baseY;

            // Keep the drawing layer aligned with the image, exactly like mouse panning does.
            UI_WorkBorder.CopyTransforms(UI_WorkImage);
        }

        // =====================
        // Mini-map visibility
        // =====================
        // The View menu handler (Menu_SeeMiniMap) lives in MainWindow.Menus.cs
        // with the rest of the menu handlers and calls SetMiniMapVisible below.

        private void MiniMapClose_Click(object sender, RoutedEventArgs e)
        {
            UI_SeeMiniMap.IsChecked = false;
            SetMiniMapVisible(false);
        }

        private void SetMiniMapVisible(bool visible)
        {
            UI_MiniMap.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            if (!visible) return;

            RefreshMiniMapImage();

            // Position the view rectangle once the newly visible picture has been laid out.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(UpdateMiniMapViewRect));
        }

        // =====================
        // Mini-map picture
        // =====================

        /// <summary>
        /// Rebuilds the low-resolution copy of the image shown in the mini-map.
        /// </summary>
        private void RefreshMiniMapImage()
        {
            // Skip the work while the mini-map is hidden; it is rebuilt when shown again.
            if (UI_MiniMap.Visibility != Visibility.Visible)
            {
                UI_MiniMapImage.Source = null;
                return;
            }

            if (UI_WorkImage.Source is not BitmapSource src || src.PixelWidth <= 0 || src.PixelHeight <= 0)
            {
                UI_MiniMapImage.Source = null;
                UI_MiniMapViewRect.Visibility = Visibility.Collapsed;
                return;
            }

            // Downscale so the longest side is at most MiniMapMaxPixels.
            double factor = Math.Min(1.0, MiniMapMaxPixels / Math.Max(src.PixelWidth, src.PixelHeight));

            BitmapSource small = src;
            if (factor < 1.0)
            {
                var scaled = new TransformedBitmap(src, new ScaleTransform(factor, factor));
                if (scaled.CanFreeze) scaled.Freeze();
                small = scaled;
            }

            UI_MiniMapImage.Source = small;
        }

        // =====================
        // Mini-map view rectangle
        // =====================

        /// <summary>
        /// Positions the highlight rectangle over the part of the image currently
        /// visible in the workspace viewport.
        /// </summary>
        private void UpdateMiniMapViewRect()
        {
            if (UI_MiniMap.Visibility != Visibility.Visible) return;

            double imgW = UI_WorkImage.ActualWidth;
            double imgH = UI_WorkImage.ActualHeight;
            double miniW = UI_MiniMapImage.ActualWidth;
            double miniH = UI_MiniMapImage.ActualHeight;

            if (imgW <= 0 || imgH <= 0 || miniW <= 0 || miniH <= 0 || UI_MiniMapImage.Source == null)
            {
                UI_MiniMapViewRect.Visibility = Visibility.Collapsed;
                return;
            }

            var scale = UI_WorkImage.GetScaleTransform();
            double scaledW = imgW * scale.ScaleX;
            double scaledH = imgH * scale.ScaleY;
            if (scaledW <= 0 || scaledH <= 0)
            {
                UI_MiniMapViewRect.Visibility = Visibility.Collapsed;
                return;
            }

            Point contentTopLeft = UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkSpace);

            // Fraction of the image that falls inside the workspace viewport.
            double fracLeft = Clamp(-contentTopLeft.X / scaledW, 0, 1);
            double fracTop = Clamp(-contentTopLeft.Y / scaledH, 0, 1);
            double fracRight = Clamp((UI_WorkSpace.ActualWidth - contentTopLeft.X) / scaledW, 0, 1);
            double fracBottom = Clamp((UI_WorkSpace.ActualHeight - contentTopLeft.Y) / scaledH, 0, 1);

            // The mini-map picture is letterboxed inside its area, so offset by its position.
            Point miniOrigin = UI_MiniMapImage.TranslatePoint(new Point(0, 0), UI_MiniMapOverlay);

            Canvas.SetLeft(UI_MiniMapViewRect, miniOrigin.X + fracLeft * miniW);
            Canvas.SetTop(UI_MiniMapViewRect, miniOrigin.Y + fracTop * miniH);
            UI_MiniMapViewRect.Width = Math.Max(2, (fracRight - fracLeft) * miniW);
            UI_MiniMapViewRect.Height = Math.Max(2, (fracBottom - fracTop) * miniH);
            UI_MiniMapViewRect.Visibility = Visibility.Visible;
        }

        // =====================
        // Mini-map click-to-pan
        // =====================

        private void MiniMapArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (UI_MiniMapImage.Source == null) return;

            _miniMapPanning = true;
            UI_MiniMapArea.CaptureMouse();   // Keep panning even if the pointer leaves the map.
            PanMainViewToMiniMapPoint(e.GetPosition(UI_MiniMapImage));
            e.Handled = true;
        }

        private void MiniMapArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_miniMapPanning) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndMiniMapPan();
                return;
            }

            PanMainViewToMiniMapPoint(e.GetPosition(UI_MiniMapImage));
            e.Handled = true;
        }

        private void MiniMapArea_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_miniMapPanning || e.ChangedButton != MouseButton.Left) return;
            EndMiniMapPan();
            e.Handled = true;
        }

        private void EndMiniMapPan()
        {
            _miniMapPanning = false;
            UI_MiniMapArea.ReleaseMouseCapture();
        }

        /// <summary>
        /// Centers the workspace view on the image point under the given mini-map position.
        /// </summary>
        private void PanMainViewToMiniMapPoint(Point posOnMiniImage)
        {
            double miniW = UI_MiniMapImage.ActualWidth;
            double miniH = UI_MiniMapImage.ActualHeight;
            double imgW = UI_WorkImage.ActualWidth;
            double imgH = UI_WorkImage.ActualHeight;
            if (miniW <= 0 || miniH <= 0 || imgW <= 0 || imgH <= 0) return;

            // Clicks in the letterbox band clamp to the nearest image edge.
            double fracX = Clamp(posOnMiniImage.X / miniW, 0, 1);
            double fracY = Clamp(posOnMiniImage.Y / miniH, 0, 1);

            var scale = UI_WorkImage.GetScaleTransform();
            var tt = UI_WorkImage.GetTranslateTransform();

            // The image's layout position with the render transform removed.
            Point contentTopLeft = UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkSpace);
            double baseX = contentTopLeft.X - tt.X;
            double baseY = contentTopLeft.Y - tt.Y;

            // Choose the translation that puts the chosen image point at the viewport center.
            tt.X = UI_WorkSpace.ActualWidth / 2 - baseX - fracX * imgW * scale.ScaleX;
            tt.Y = UI_WorkSpace.ActualHeight / 2 - baseY - fracY * imgH * scale.ScaleY;

            // Keep the drawing layer aligned with the image, exactly like mouse panning does.
            UI_WorkBorder.CopyTransforms(UI_WorkImage);
        }

        // =====================
        // Mini-map panel drag
        // =====================

        private void MiniMapTitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            _miniMapDraggingPanel = true;
            _miniMapDragStart = e.GetPosition(UI_WorkSpace);
            _miniMapStartTx = UI_MiniMapTranslate.X;
            _miniMapStartTy = UI_MiniMapTranslate.Y;

            (sender as UIElement)?.CaptureMouse();
            e.Handled = true;
        }

        private void MiniMapTitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_miniMapDraggingPanel) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _miniMapDraggingPanel = false;
                (sender as UIElement)?.ReleaseMouseCapture();
                return;
            }

            Point now = e.GetPosition(UI_WorkSpace);
            double tx = _miniMapStartTx + (now.X - _miniMapDragStart.X);
            double ty = _miniMapStartTy + (now.Y - _miniMapDragStart.Y);

            // Keep the panel inside the workspace so it cannot be dragged out of reach.
            double minTx = -UI_MiniMap.Margin.Left;
            double minTy = -UI_MiniMap.Margin.Top;
            double maxTx = Math.Max(minTx, UI_WorkSpace.ActualWidth - UI_MiniMap.ActualWidth - UI_MiniMap.Margin.Left);
            double maxTy = Math.Max(minTy, UI_WorkSpace.ActualHeight - UI_MiniMap.ActualHeight - UI_MiniMap.Margin.Top);

            UI_MiniMapTranslate.X = Clamp(tx, minTx, maxTx);
            UI_MiniMapTranslate.Y = Clamp(ty, minTy, maxTy);
        }

        private void MiniMapTitleBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_miniMapDraggingPanel || e.ChangedButton != MouseButton.Left) return;

            _miniMapDraggingPanel = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            e.Handled = true;
        }

        // =====================
        // Mini-map resize
        // =====================

        private void MiniMapResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Allow the panel to grow up to 80% of the workspace in each direction.
            double maxW = Math.Max(UI_MiniMap.MinWidth, UI_WorkSpace.ActualWidth * 0.8);
            double maxH = Math.Max(UI_MiniMap.MinHeight, UI_WorkSpace.ActualHeight * 0.8);

            UI_MiniMap.Width = Clamp(UI_MiniMap.ActualWidth + e.HorizontalChange, UI_MiniMap.MinWidth, maxW);
            UI_MiniMap.Height = Clamp(UI_MiniMap.ActualHeight + e.VerticalChange, UI_MiniMap.MinHeight, maxH);
        }
    }
}