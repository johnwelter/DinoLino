using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    /// Three-click triangle measurement: interior angles, aspect ratio, area, and the
    /// area relative to the previous triangle.
    public class GetAngleMode : WorkMode
    {
        #region Mode identity

        public override UserControl CreateControlPanel() => new TriangleControlPanel(this);
        public override string TabName => "Triangle";
        public override bool IsStartingNewOperation => CurrentStep == 0 || CurrentStep == 3;

        #endregion

        #region Drawing state

        public Line CurrentUILine = null;

        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 PointC;

        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            PointA = PointB = PointC = default;
        }

        #endregion

        #region Results

        private double _angleAResult;
        public double AngleAResult
        {
            get => _angleAResult;
            set => SetField(ref _angleAResult, value);
        }

        private double _angleBResult;
        public double AngleBResult
        {
            get => _angleBResult;
            set => SetField(ref _angleBResult, value);
        }

        private double _angleCResult;
        public double AngleCResult
        {
            get => _angleCResult;
            set => SetField(ref _angleCResult, value);
        }

        // Longest side divided by the height measured against that side.
        private double _triAspectRatioResult;
        public double TriAspectRatioResult
        {
            get => _triAspectRatioResult;
            set => SetField(ref _triAspectRatioResult, value);
        }

        // Current triangle area divided by the area of the previous triangle, or
        // "N/A" when there is no previous triangle to compare against.
        private object _relativeAreaResult;
        public object RelativeAreaResult
        {
            get => _relativeAreaResult;
            set => SetField(ref _relativeAreaResult, value);
        }

        private string _triAreaScaledResult = "Unscaled";
        public string TriAreaScaledResult
        {
            get => _triAreaScaledResult;
            set => SetField(ref _triAreaScaledResult, value);
        }

        // Canvas-space area of the displayed triangle, kept so the scaled row can be
        // re-derived whenever the calibration changes or an undo restores a
        // different triangle.
        private double _canvasArea;
        private bool _hasCanvasArea;

        private void RecomputeScaledResults()
        {
            TriAreaScaledResult = FormatScaledArea(_canvasArea, _hasCanvasArea);
        }

        /// <summary>Restores the canvas-space area behind the scaled row.</summary>
        public void RestoreScaledMeasurements(double canvasArea)
        {
            _canvasArea = canvasArea;
            _hasCanvasArea = true;
            RecomputeScaledResults();
        }

        public override void ClearMetadata()
        {
            AngleAResult = 0;
            AngleBResult = 0;
            AngleCResult = 0;
            TriAspectRatioResult = 0;
            RelativeAreaResult = "N/A";
            _canvasArea = 0;
            _hasCanvasArea = false;
            RecomputeScaledResults();
        }

        public override void RefreshScalePlaceholders()
        {
            RecomputeScaledResults();
            OnPropertyChanged(nameof(AvgTriAreaScaledResult));
        }

        #endregion

        #region Click handling

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            if (CurrentUILine != null)
            {
                CurrentUILine.X2 = mousePos.X;
                CurrentUILine.Y2 = mousePos.Y;
            }

            return mousePos;
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            List<UIElement> output = new();
            switch (CurrentStep)
            {
                case 0: // Start the first line and store the first point

                    PointA = mousePos;
                    CurrentUILine = MakeLine(PointA, PointA);
                    output.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep++;
                    break;

                case 1: // End the first line, store the second point, and start the second line

                    PointB = mousePos;
                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    CurrentUILine = MakeLine(PointB, PointB);
                    output.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep++;
                    break;

                case 2: // Store the third point, close the triangle, measure it, and commit

                    PointC = mousePos;

                    var ab = MakeLine(PointA, PointB);
                    var bc = MakeLine(PointB, PointC);
                    var ca = MakeLine(PointC, PointA);

                    output.Add(ab);
                    output.Add(bc);
                    output.Add(ca);

                    var labelA = MakeLabel("A", PointA);
                    var labelB = MakeLabel("B", PointB);
                    var labelC = MakeLabel("C", PointC);

                    output.Add(labelA);
                    output.Add(labelB);
                    output.Add(labelC);

                    CurrentOperation.Add(ab);
                    CurrentOperation.Add(bc);
                    CurrentOperation.Add(ca);
                    CurrentOperation.Add(labelA);
                    CurrentOperation.Add(labelB);
                    CurrentOperation.Add(labelC);

                    CalculateAndUpdateResults();

                    CommitCurrentOperation(new GetAngleOperation
                    {
                        OperationKind = "Triangle",
                        AngleA = AngleAResult,
                        AngleB = AngleBResult,
                        AngleC = AngleCResult,
                        TriAspectRatio = TriAspectRatioResult,
                        TriArea = _canvasArea,
                        RelativeArea = RelativeAreaResult
                    });

                    CurrentUILine = null;
                    CurrentStep++;
                    break;

                case 3: // Reuse this click as the first point of the next triangle

                    ResetDrawingState();
                    PointA = mousePos;

                    CurrentUILine = MakeLine(PointA, PointA);
                    output.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);

                    CurrentStep = 1;
                    break;
            }
            return output;
        }

        private void CalculateAndUpdateResults()
        {
            Vector2 AB = PointB - PointA;
            Vector2 AC = PointC - PointA;
            Vector2 BC = PointC - PointB;
            Vector2 CA = PointA - PointC;

            // Reject near-collinear points (not a triangle)
            double cross = (AB ^ AC);
            if (Math.Abs(cross) < 0.0001)
                return;

            AngleAResult = GeometryCalculations.InteriorAngle(PointB, PointA, PointC);
            AngleBResult = GeometryCalculations.InteriorAngle(PointA, PointB, PointC);
            AngleCResult = GeometryCalculations.InteriorAngle(PointA, PointC, PointB);

            double sideAB = AB.Magnitude();
            double sideBC = BC.Magnitude();
            double sideCA = CA.Magnitude();

            double area = Math.Abs(cross) / 2.0;
            _canvasArea = area;
            _hasCanvasArea = true;
            RecomputeScaledResults();

            double longestSide = Math.Max(sideAB, Math.Max(sideBC, sideCA));
            TriAspectRatioResult = GeometryCalculations.TriangleAspectRatio(longestSide, area);

            // Compare area to the previous triangle's area if one exists
            var previousTriangle = TriangleOps.LastOrDefault();

            RelativeAreaResult = GeometryCalculations.RelativeArea(area, previousTriangle?.TriArea ?? 0);
        }

        #endregion

        #region Operation averages

        // Live averages of each numeric triangle output across all attempts, read from the
        // undo/redo history so they stay correct through commit/undo/redo/clear. "Relative
        // Size" is excluded: it's a ratio between consecutive attempts, not an absolute
        // measurement, and is stored as a mixed value, so a mean of it isn't well-defined.

        private IEnumerable<GetAngleOperation> TriangleOps => OperationsOfKind<GetAngleOperation>();

        public string AvgAngleAResult => FormatAverage(TriangleOps.Select(o => o.AngleA));
        public string AvgAngleBResult => FormatAverage(TriangleOps.Select(o => o.AngleB));
        public string AvgAngleCResult => FormatAverage(TriangleOps.Select(o => o.AngleC));
        public string AvgTriAspectRatioResult => FormatAverage(TriangleOps.Select(o => o.TriAspectRatio));
        public string AvgTriAreaScaledResult => FormatScaledAreaAverage(TriangleOps.Select(o => o.TriArea));

        protected override void RecomputeAverages()
        {
            OnPropertyChanged(nameof(AvgAngleAResult));
            OnPropertyChanged(nameof(AvgAngleBResult));
            OnPropertyChanged(nameof(AvgAngleCResult));
            OnPropertyChanged(nameof(AvgTriAspectRatioResult));
            OnPropertyChanged(nameof(AvgTriAreaScaledResult));
        }

        #endregion

        #region Tips

        private static readonly string[] TriangleTips = BuildTips(
            "💡 Click three points to define a triangle. Results update automatically after the third click.",
            "💡 The aspect ratio of any triangle is the length of its longest side divided by its height.");

        public override string[] GetTips() => TriangleTips;

        #endregion
    }
}