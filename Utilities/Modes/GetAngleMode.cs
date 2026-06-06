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
    public class GetAngleMode : WorkMode
    {
        public override UserControl CreateControlPanel() => new TriangleControlPanel(this);
        public override string TabName => "Triangle";
        public override bool IsStartingNewOperation => CurrentStep == 0 || CurrentStep == 3;

        public override void ClearMetadata()
        {
            AngleAResult = 0;
            AngleBResult = 0;
            AngleCResult = 0;
            TriAspectRatioResult = 0;
            RelativeAreaResult = "N/A";
        }

        private TextBlock MakeLabel(string text, Vector2 pos)
        {
            TextBlock label = new TextBlock();
            label.Text = text;
            label.Foreground = this.LineColor;
            label.FontSize = 28;
            label.FontWeight = FontWeights.Bold;

            // offset so it doesn't sit exactly on the point
            Canvas.SetLeft(label, pos.X + 5);
            Canvas.SetTop(label, pos.Y + 5);

            return label;
        }

        // Tracking 3-click line groups 
        private List<UIElement> CurrentOperation = new();

        // Current UI line to modify during mouse move
        public Line CurrentUILine = null;

        // All major POIs in generating angle
        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 PointC;

        // Bindable results of angle calculation

        // private/public pairs used to handle propagation of results to UI bindings
        private double _angleAResult;
        private double _angleBResult;
        private double _angleCResult;
        private double _TriAspectRatioResult;
        private object _relativeAreaResult;
        private double _currentArea = 0;

        public double AngleAResult
        {
            get => _angleAResult;
            set => SetField(ref _angleAResult, value);
        }

        public double AngleBResult
        {
            get => _angleBResult;
            set => SetField(ref _angleBResult, value);
        }

        public double AngleCResult
        {
            get => _angleCResult;
            set => SetField(ref _angleCResult, value);
        }

        // aspect ratio of the triangle, calculated as longest side / height
        public double TriAspectRatioResult
        {
            get => _TriAspectRatioResult;
            set => SetField(ref _TriAspectRatioResult, value);
        }

        // ratio of triangle areas. Current triangle area divided by area of previous triangle.
        public object RelativeAreaResult
        {
            get => _relativeAreaResult;
            set => SetField(ref _relativeAreaResult, value);
        }

        // ---------- TRIANGLE MODE ---------------- //

        public override void Reset()
        {
            base.Reset();
            AngleAResult = AngleBResult = AngleCResult = 0;
            TriAspectRatioResult = 0;
            RelativeAreaResult = "N/A";
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            PointA = PointB = PointC = default;
        }

        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            PointA = PointB = PointC = default;
        }

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

                case 2: // End the second line, store the third point, create a third line connecting the three points (triangle), and calculate the final results.

                    PointC = mousePos;

                    // Triangle edges
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

                    // calculate the three angles

                    CalculateAndUpdateResults();

                    CommitOperation(new GetAngleOperation
                    {
                        OperationKind = "Triangle",
                        SourceMode = this,
                        Elements = new List<UIElement>(CurrentOperation),
                        AngleA = AngleAResult,
                        AngleB = AngleBResult,
                        AngleC = AngleCResult,
                        TriAspectRatio = TriAspectRatioResult,
                        TriArea = _currentArea,
                        RelativeArea = RelativeAreaResult
                    });

                    CurrentOperation.Clear();
                    CurrentUILine = null;
                    CurrentStep++;
                    break;

                case 3:
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

            // reject near-collinear points (not a triangle)
            double cross = (AB ^ AC);
            if (Math.Abs(cross) < 0.0001)
                return;

            AngleAResult = GeometryCalculations.InteriorAngle(PointB, PointA, PointC);
            AngleBResult = GeometryCalculations.InteriorAngle(PointA, PointB, PointC);
            AngleCResult = GeometryCalculations.InteriorAngle(PointA, PointC, PointB);

            // Aspect ratio: width (longest side) / height (area * 2 / base)
            double sideAB = AB.Magnitude();
            double sideBC = BC.Magnitude();
            double sideCA = CA.Magnitude();

            double area = Math.Abs(cross) / 2.0;
            _currentArea = area;
            double longestSide = Math.Max(sideAB, Math.Max(sideBC, sideCA));
            TriAspectRatioResult = GeometryCalculations.TriangleAspectRatio(longestSide, area);

            // Compare area to previous triangle's area if one exists
            var previousTriangle = UndoRedoManager?.History
                .OfType<GetAngleOperation>()
                .LastOrDefault();

            RelativeAreaResult = GeometryCalculations.RelativeArea(area, previousTriangle?.TriArea ?? 0);
        }

        public override string[] GetTips() => new[]
        {
            "💡 Click three points to define a triangle. Results update automatically after the third click.",
            "💡 The aspect ratio of any triangle is the length of its longest side divided by its height.",
            "💡 The user guide and software information can be found in the Help menu.",
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
            "💡 Zoom in or out using the scroll wheel.",
            "💡 Press 'Ctrl' and left click to drag the image.",
            "💡 Toggle tip visibility in the View menu."
        };
    }
}
