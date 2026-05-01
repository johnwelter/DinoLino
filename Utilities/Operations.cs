// Utilities/Operations/Operations.cs
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities.Operations
{
    public class WorkOperation
    {
        public List<UIElement> Elements { get; set; } = new List<UIElement>();
    }
    
    public class CurvatureOperation : WorkOperation
    {
        public double Angle { get; set; }
        public double AspectRatio { get; set; }
    }

    public class GetAngleOperation : WorkOperation
    {
        public double AngleA { get; set; }
        public double AngleB { get; set; }
        public double AngleC { get; set; }
        public double TriAspectRatio { get; set; }
        public double TriArea { get; set; }
    }

    public class DrawOperation : WorkOperation
    {
        public double DrawAspectRatio { get; set; }
        public double ShapeArea { get; set; }
        public double LineLength { get; set; }
    }
}