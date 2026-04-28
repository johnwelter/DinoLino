using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace DinoLino.Utilities.Modes
{
    public class UndoResult
    {
        public List<UIElement> Elements { get; set; }
        public double Angle { get; set; }
        public double AspectRatio { get; set; }
    }
}
