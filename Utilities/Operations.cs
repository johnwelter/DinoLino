using DinoLino.Utilities.Modes;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities.Operations
{
    /// <summary>
    /// Base type for an operation stored in undo/redo history.
    /// Each operation keeps the visuals it created and any metadata needed to restore the mode state.
    /// </summary>
    public abstract class WorkOperation
    {
        public string OperationKind { get; set; }
        public List<UIElement> Elements { get; set; } = new List<UIElement>();
        public WorkMode SourceMode { get; set; }

        /// <summary>
        /// Restores the mode-specific metadata saved with this operation.
        /// </summary>
        public abstract void ApplyMetadataToMode();
    }

    /// <summary>
    /// History entry for a circular-arc measurement.
    /// </summary>
    public class CircularArcOperation : WorkOperation
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

    /// <summary>
    /// History entry for a parabola measurement.
    /// </summary>
    public class ParabolaOperation : WorkOperation
    {
        public string XYFunction { get; set; }
        public double RiseSpanRatio { get; set; }
        public double PChordArcRatio { get; set; }
        public double VertexCurvature { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.XYFunctionResult = XYFunction;
                mode.RiseSpanRatioResult = RiseSpanRatio;
                mode.PChordArcRatioResult = PChordArcRatio;
                mode.VertexCurvatureResult = VertexCurvature;
            }
        }
    }

    /// <summary>
    /// History entry for an n-point spline measurement.
    /// </summary>
    public class SplineOperation : WorkOperation
    {
        public double TurningAngleArcRatio { get; set; }
        public double SChordArcRatio { get; set; }
        public double SplineLengthPixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.TurningAngleArcRatioResult = TurningAngleArcRatio;
                mode.SChordArcRatioResult = SChordArcRatio;
            }
        }
    }

    /// <summary>
    /// History entry for a triangle angle measurement.
    /// </summary>
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

    /// <summary>
    /// History entry for a drawn shape measurement.
    /// </summary>
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

    /// <summary>
    /// History entry for a line measurement.
    /// </summary>
    public class LineOperation : WorkOperation
    {
        public double LineLength { get; set; }
        public object LineLengthRatio { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is DrawMode mode)
            {
                mode.LineLengthRatioResult = LineLengthRatio;
            }
        }
    }

    /// <summary>
    /// History entry for outline analysis metadata.
    /// </summary>
    public class OutlineOperation : WorkOperation
    {
        public double AspectRatio { get; set; }
        public double PerimeterAreaRatio { get; set; }
        public double Circularity { get; set; }
        public double Solidity { get; set; }
        public double SumTurningAngles { get; set; }
        public double TurningAngleLength { get; set; }

        // Flattened coefficient array: [a1, b1, c1, d1, a2, b2, c2, d2, ...].
        public double[] EFDCoefficients { get; set; }

        // Measured in canvas coordinates so the values stay aligned with scale calibration.
        public double Perimeter { get; set; }
        public double Area { get; set; }

        public bool HasMetadata { get; set; }

        // Stored text shown by the outline panel so undo/redo can restore the exact UI state.
        public string MetadataSummary { get; set; } = "";
        public string NormalizationWarning { get; set; } = "";

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is OutlineMode mode)
            {
                mode.AspectRatioResult = AspectRatio;
                mode.PerimeterAreaRatioResult = PerimeterAreaRatio;
                mode.CircularityResult = Circularity;
                mode.SolidityResult = Solidity;
                mode.SumTurningAnglesResult = SumTurningAngles;
                mode.TurningAngleLengthResult = TurningAngleLength;
                mode.EFDCoefficientsResult = EFDCoefficients;

                // Restore the scaled measurements and summary text only when metadata exists.
                if (HasMetadata)
                {
                    mode.RestoreScaledMeasurements(Perimeter, Area);
                    mode.MetadataSummary = MetadataSummary;
                    mode.NormalizationWarning = NormalizationWarning;
                }
            }
        }
    }
}