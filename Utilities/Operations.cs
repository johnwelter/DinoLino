// Utilities/Operations/Operations.cs
using DinoLino.DataTypes;
using DinoLino.Utilities.Modes;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities.Operations
{
    public abstract class WorkOperation
    {
        public string OperationKind { get; set; }
        public List<UIElement> Elements { get; set; } = new List<UIElement>();
        public WorkMode SourceMode { get; set; }

        // Each concrete operation implements how to restore its metadata
        public abstract void ApplyMetadataToMode();
    }
    
    public class CurvatureOperation : WorkOperation
    {
        public double CentralAngle { get; set; }
        public double AspectRatio { get; set; }
        public double ChordArcRatio { get; set; }
        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.CentralAngleResult = CentralAngle;
                mode.AspectRatioResult = AspectRatio;
                mode.ChordArcRatioResult = ChordArcRatio;
            }
        }
    }

    // Stores metadata for n-point Catmull-Rom spline operations in CurvatureMode
    public class SplineOperation : WorkOperation
    {
        public double TurningAngle { get; set; }
        public double SChordArcRatio { get; set; }
        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.TurningAngleResult = TurningAngle;
                mode.SChordArcRatioResult = SChordArcRatio;
            }
        }
    }

    public class GetAngleOperation : WorkOperation
    {
        public double AngleA { get; set; }
        public double AngleB { get; set; }
        public double AngleC { get; set; }
        public double TriAspectRatio { get; set; }
        public double TriArea { get; set; }
        public object RelativeArea { get; set; }
        public override void ApplyMetadataToMode()
        {
            if (SourceMode is GetAngleMode mode)
            {
                mode.AngleAResult = AngleA;
                mode.AngleBResult = AngleB;
                mode.AngleCResult = AngleC;
                mode.TriAspectRatioResult = TriAspectRatio;
                mode.RelativeAreaResult = RelativeArea;
            }
        }
    }

    public class ShapeOperation : WorkOperation
    {
        public double DrawAspectRatio { get; set; }
        public object RelativeArea { get; set; }
        public double ShapeArea { get; set; }
        public override void ApplyMetadataToMode()
        {
            if (SourceMode is DrawMode mode)
            {
                mode.DrawAspectRatioResult = DrawAspectRatio;
                mode.RelativeAreaResult = RelativeArea;
            }
        }
    }

    public class LineOperation : WorkOperation
    {
        public double LineLength { get; set; }
        public Vector2 LineDirection { get; set; }
        public object LineLengthRatio { get; set; }
        public override void ApplyMetadataToMode()
        {
            if (SourceMode is DrawMode mode)
            {
                mode.LineLengthRatioResult = LineLengthRatio;
            }
        }
    }
}