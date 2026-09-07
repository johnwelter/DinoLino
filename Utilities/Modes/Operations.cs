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
        public double RadiusImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.CentralAngleResult = CentralAngle;
                mode.AspectRatioResult = AspectRatio;
                mode.ChordArcRatioResult = ChordArcRatio;
                mode.RestoreCircularArcRadius(RadiusImagePixels);
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
        public double VertexRadiusImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.XYFunctionResult = XYFunction;
                mode.RiseSpanRatioResult = RiseSpanRatio;
                mode.PChordArcRatioResult = PChordArcRatio;
                mode.VertexCurvatureResult = VertexCurvature;
                mode.RestoreParabolicVertexRadius(VertexRadiusImagePixels);
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

        // Measured in image pixels, which do not change with window size, so a value
        // stored here converts to the same real-world number whenever it is read.
        public double SplineLengthImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.TurningAngleArcRatioResult = TurningAngleArcRatio;
                mode.SChordArcRatioResult = SChordArcRatio;
                mode.RestoreScaledMeasurements(SplineLengthImagePixels);
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

        // Measured in square image pixels.
        public double TriAreaImagePixels { get; set; }

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
                mode.RestoreScaledMeasurements(TriAreaImagePixels);
            }
        }
    }

    /// <summary>
    /// History entry for a drawn shape measurement.
    /// </summary>
    public class ShapeOperation : WorkOperation
    {
        /// Which constrained shape this was. A rectangle and an ellipse are not the
        /// same measurement, so the kind travels with the operation: the Batch
        /// Workshop gives each kind its own columns and its own attempt numbering,
        /// and the on-image counter tallies them separately.
        public DrawMode.ShapeConstraint ShapeKind { get; set; }

        public double DrawAspectRatio { get; set; }

        /// Area against the previous shape OF THE SAME KIND, or "N/A" when this is
        /// the first of its kind. Boxed as a double or that string.
        public object RelativeArea { get; set; }

        // Measured in square image pixels.
        public double ShapeAreaImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is DrawMode mode)
            {
                mode.DrawAspectRatioResult = DrawAspectRatio;
                mode.RelativeAreaResult = RelativeArea;
                mode.RestoreShapeMeasurement(ShapeAreaImagePixels);
            }
        }
    }

    /// <summary>
    /// History entry for a line measurement.
    /// </summary>
    public class LineOperation : WorkOperation
    {
        // Measured in image pixels.
        public double LineLengthImagePixels { get; set; }

        /// Extent of the line along the specimen's X axis, in image pixels, taken from
        /// the orientation set by Tools ▸ Align Image. An unaligned specimen falls back
        /// to the image's own axes, which is what ImageAlignment hands back when no
        /// orientation has been drawn.
        public double LineDeltaXImagePixels { get; set; }

        /// <summary>Extent along the specimen's Y axis, in image pixels.</summary>
        public double LineDeltaYImagePixels { get; set; }

        public object LineLengthRatio { get; set; }
        public object LineAngle { get; set; }
        public double HeadingDegrees { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is DrawMode mode)
            {
                mode.LineLengthRatioResult = LineLengthRatio;
                mode.LineAngleResult = LineAngle;
                mode.RestoreLineMeasurement(
                    LineLengthImagePixels, LineDeltaXImagePixels, LineDeltaYImagePixels);
            }
        }
    }

    /// <summary>
    /// History entry for outline analysis metadata.
    /// </summary>
    public class OutlineOperation : WorkOperation
    {
        public double AspectRatio { get; set; }
        public double Circularity { get; set; }
        public double Solidity { get; set; }
        public double SumTurningAngles { get; set; }
        public double TurningAngleLength { get; set; }

        // Flattened coefficient array: [a1, b1, c1, d1, a2, b2, c2, d2, ...].
        public double[] EFDCoefficients { get; set; }

        // Measured in image pixels, which do not change with window size, so a value
        // stored here converts to the same real-world number whenever it is read.
        public double PerimeterImagePixels { get; set; }
        public double AreaImagePixels { get; set; }
        public double MaxLengthImagePixels { get; set; }
        public double MaxWidthImagePixels { get; set; }
        // Vertex spacing the metrics above were measured at, in image pixels, and
        // the vertex count it produced. Recorded because every number on this
        // operation is conditional on them.
        public double MeasurementSpacingImagePixels { get; set; }
        public int MeasurementPointCount { get; set; }

        public bool HasMetadata { get; set; }

        // Stored text shown by the outline panel so undo/redo can restore the exact UI state.
        public string MetadataSummary { get; set; } = "";
        public string NormalizationWarning { get; set; } = "";

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is OutlineMode mode)
            {
                mode.AspectRatioResult = AspectRatio;
                mode.CircularityResult = Circularity;
                mode.SolidityResult = Solidity;
                mode.SumTurningAnglesResult = SumTurningAngles;
                mode.TurningAngleLengthResult = TurningAngleLength;
                mode.EFDCoefficientsResult = EFDCoefficients;

                // Restore the scaled measurements and summary text only when metadata exists.
                if (HasMetadata)
                {
                    mode.RestoreScaledMeasurements(
                        PerimeterImagePixels, AreaImagePixels,
                        MaxLengthImagePixels, MaxWidthImagePixels,
                        MeasurementSpacingImagePixels, MeasurementPointCount);
                    mode.MetadataSummary = MetadataSummary;
                    mode.NormalizationWarning = NormalizationWarning;
                }
            }
        }
    }
}