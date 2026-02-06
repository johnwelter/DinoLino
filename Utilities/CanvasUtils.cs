using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DinoLino.Utilities
{
    public static class CanvasUtils
    {
        public static void SetPosition(this UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            Canvas.SetRight(element, 1);
        }
    }
}
