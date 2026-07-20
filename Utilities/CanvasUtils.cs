using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Helper methods for positioning and transforming WPF elements on a Canvas.
    /// </summary>
    public static class CanvasUtils
    {
        /// <summary>
        /// Places a UI element at an absolute Canvas position.
        /// </summary>
        public static void SetPosition(this UIElement element, double x, double y)
        {
            // Canvas.Left/Top only affect direct children of a Canvas.
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);

            // Preserve existing layout behavior for elements that also use Canvas.Right.
            Canvas.SetRight(element, 1);
        }

        /// <summary>
        /// Assigns a scale + translate transform pair to the element.
        /// </summary>
        public static void InitializeGroupTransform(this UIElement element, Point origin)
        {
            var group = new TransformGroup();

            // Scale first, then translate, so zoom and pan stay independent.
            var scaleTransform = new ScaleTransform();
            group.Children.Add(scaleTransform);

            var translateTransform = new TranslateTransform();
            group.Children.Add(translateTransform);

            element.RenderTransform = group;
            element.RenderTransformOrigin = origin;
        }

        /// <summary>
        /// Gets the element's translate transform from its render transform group.
        /// </summary>
        public static TranslateTransform GetTranslateTransform(this UIElement element)
        {
            return (TranslateTransform)((TransformGroup)element.RenderTransform)
                .Children.First(tr => tr is TranslateTransform);
        }

        /// <summary>
        /// Gets the element's scale transform from its render transform group.
        /// </summary>
        public static ScaleTransform GetScaleTransform(this UIElement element)
        {
            return (ScaleTransform)((TransformGroup)element.RenderTransform)
                .Children.First(tr => tr is ScaleTransform);
        }

        /// <summary>
        /// Restores zoom to 100% and translation to zero.
        /// </summary>
        public static void ResetZoom(this UIElement element)
        {
            var st = GetScaleTransform(element);
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;

            var tt = GetTranslateTransform(element);
            tt.X = 0.0;
            tt.Y = 0.0;
        }

        /// <summary>
        /// Zooms around the given point while keeping that point anchored visually.
        /// </summary>
        public static void ZoomElement(this UIElement element, double delta, Point relativeTo)
        {
            var st = GetScaleTransform(element);
            var tt = GetTranslateTransform(element);

            double zoom = delta > 0 ? .2 : -.2;

            // Prevent zooming out too far, which makes the content hard to recover.
            if (!(delta > 0) && (st.ScaleX < .4 || st.ScaleY < .4))
                return;

            // Convert the mouse point into the element's current transformed coordinates.
            double absoluteX = relativeTo.X * st.ScaleX + tt.X;
            double absoluteY = relativeTo.Y * st.ScaleY + tt.Y;

            st.ScaleX += zoom;
            st.ScaleY += zoom;

            // Recompute translation so the point under the cursor stays fixed.
            tt.X = absoluteX - relativeTo.X * st.ScaleX;
            tt.Y = absoluteY - relativeTo.Y * st.ScaleY;
        }

        /// <summary>
        /// Copies scale and translation from one element to another.
        /// </summary>
        public static void CopyTransforms(this UIElement element, UIElement fromElement)
        {
            var st = GetScaleTransform(element);
            var tt = GetTranslateTransform(element);

            var fst = GetScaleTransform(fromElement);
            var ftt = GetTranslateTransform(fromElement);

            st.ScaleX = fst.ScaleX;
            st.ScaleY = fst.ScaleY;

            tt.X = ftt.X;
            tt.Y = ftt.Y;
        }
    }
}