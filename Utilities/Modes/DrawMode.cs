using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    public class DrawMode : WorkMode
    {
        public override void ClearMetadata()
        {
            DrawAspectRatioResult = 0;
            ShapeAreaResult = "N/A";
            LineLengthRatioResult = "N/A";
        }

        // shape type tracker
        public enum DrawShape
        {
            None,
            Ellipse,
            Circle,
            Rectangle,
            Square,
            Line,
            Angle
        }
        public DrawShape CurrentShape { get; set; } = DrawShape.None;

        // line type tracker
        public enum LineConstraint
        {
            None,
            Parallel,
            Perpendicular
        }
        public LineConstraint CurrentLineConstraint { get; set; } = LineConstraint.None;

        // Helper function to find the most recent line operation in history for line length ratio calculations
        private DrawOperation FindPreviousLine(int skipLast)
        {
            var prev = UndoRedoManager.History
                    .Reverse()
                    .Skip(skipLast)
                    .OfType<DrawOperation>()
                    .FirstOrDefault(op => op.LineLength > 0.00001);

            return prev;
        }

        protected override void OnOperationUndone(WorkOperation operation)
        {
            if (operation is DrawOperation undone && undone.LineLength > 0.00001)
            {
                bool anyConstrainedLinesRemain = UndoRedoManager.History
                    .OfType<DrawOperation>()
                    .Any(op => op.LineLength > 0.00001);

                if (!anyConstrainedLinesRemain)
                {
                    _hasReferenceLineDirection = false;
                    _referenceLineDirection = default;
                }
            }

            // Restore from the new top of the global history
            var prev = UndoRedoManager.History
                .OfType<DrawOperation>()
                .LastOrDefault();

            if (prev != null)
            {
                // restore aspect ratio and area
                DrawAspectRatioResult = prev.DrawAspectRatio;

                var previousPrev = UndoRedoManager.History
                    .OfType<DrawOperation>()
                    .Reverse()
                    .Skip(1)
                    .FirstOrDefault(op => op.ShapeArea > 0.00001);

                if (previousPrev != null && previousPrev.ShapeArea > 0.00001)
                    ShapeAreaResult = Math.Round(prev.ShapeArea / previousPrev.ShapeArea, 2);
                else
                    ShapeAreaResult = "N/A";

                // restore line length ratio
                if (prev.LineLength > 0.00001)
                {
                    var prevLine = FindPreviousLine(1);
                    if (prevLine != null)
                        LineLengthRatioResult = Math.Round(prev.LineLength / prevLine.LineLength, 2);
                    else
                        LineLengthRatioResult = "N/A";
                }
                else
                {
                    LineLengthRatioResult = "N/A";
                }
            }
            else
            {
                DrawAspectRatioResult = 0;
                ShapeAreaResult = "N/A";
                LineLengthRatioResult = "N/A";
            }
        }

        protected override void OnOperationRedone(WorkOperation operation)
        {
            if (operation is DrawOperation op)
            {
                DrawAspectRatioResult = op.DrawAspectRatio;

                var prevInHistory = UndoRedoManager.History
                    .OfType<DrawOperation>()
                    .Reverse()
                    .Skip(1)
                    .FirstOrDefault(prevOp => prevOp.ShapeArea > 0.00001);

                if (prevInHistory != null && prevInHistory.ShapeArea > 0.00001)
                    ShapeAreaResult = Math.Round(op.ShapeArea / prevInHistory.ShapeArea, 2);
                else
                    ShapeAreaResult = "N/A";

                if (op.LineLength > 0.00001)
                {
                    var prevLine = FindPreviousLine(1);
                    if (prevLine != null)
                        LineLengthRatioResult = Math.Round(op.LineLength / prevLine.LineLength, 2);
                    else
                        LineLengthRatioResult = "N/A";
                }
                else
                {
                    LineLengthRatioResult = "N/A";
                }
            }
        }

        // Tracking drawn elements
        private List<UIElement> CurrentOperation = new();
        private Vector2 _dragStart;
        private Shape _currentShape = null;
        private Line _currentLine = null;
        private Vector2 _pointA;
        private Vector2 _pointB;
        private Line _currentAngleLine = null;
        private Vector2 _firstLineDirection;
        private Vector2 _referenceLineDirection;
        private bool _hasReferenceLineDirection;

        public double LockedAngleDegrees { get; set; } = 0;

        // Bindable results of shape calculations

        // private/public pairs used to handle propagation of results to UI bindings
        private double _drawAspectRatioResult;
        private object _shapeAreaResult;
        private double _currentShapeArea = 0;
        private object _lineLengthRatioResult;

        public double DrawAspectRatioResult
        {
            get => _drawAspectRatioResult;
            set
            {
                _drawAspectRatioResult = value;
                OnPropertyChanged(nameof(DrawAspectRatioResult));
            }
        }

        public object ShapeAreaResult
        {
            get => _shapeAreaResult;
            set
            {
                _shapeAreaResult = value;
                OnPropertyChanged(nameof(ShapeAreaResult));
            }
        }

        public object LineLengthRatioResult
        {
            get => _lineLengthRatioResult;
            set
            {
                _lineLengthRatioResult = value;
                OnPropertyChanged(nameof(LineLengthRatioResult));
            }
        }

        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            _currentShape = null;
            _currentLine = null;
            _currentAngleLine = null;
            _dragStart = default;
            _pointA = default;
            _pointB = default;
            _firstLineDirection = default;
            _referenceLineDirection = default;
            _hasReferenceLineDirection = false;
            CurrentOperation.Clear();
        }

        public override void Reset()
        {
            base.Reset();
            DrawAspectRatioResult = 0;
            ShapeAreaResult = "N/A";
            LineLengthRatioResult = "N/A";
            _currentShape = null;
            _currentLine = null;
            _currentAngleLine = null;
            _dragStart = default;
            _pointA = default;
            _pointB = default;
            _firstLineDirection = default;
            _referenceLineDirection = default;
            _hasReferenceLineDirection = false;
            CurrentOperation.Clear();
            CurrentStep = 0;
        }

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            if (CurrentShape == DrawShape.None)
                return mousePos;

            if (CurrentShape == DrawShape.Angle)
            {
                // safeguard: check that input angle is valid
                if (CurrentStep == 0 && LockedAngleDegrees == 0)
                {
                    return mousePos;
                }

                // step 1: dragging line AB freely
                if (CurrentStep == 1 && _currentLine != null)
                {
                    _currentLine.X2 = mousePos.X;
                    _currentLine.Y2 = mousePos.Y;
                }
                // step 2: dragging line BC locked to angle
                else if (CurrentStep == 2 && _currentAngleLine != null)
                {
                    Vector2 constrained = ConstrainToAngle(_pointB, mousePos, LockedAngleDegrees);
                    _currentAngleLine.X2 = constrained.X;
                    _currentAngleLine.Y2 = constrained.Y;
                }
                return mousePos;
            }

            if (CurrentShape == DrawShape.Line)
            {
                if (_currentLine != null)
                {
                    if (CurrentLineConstraint != LineConstraint.None && _hasReferenceLineDirection)
                    {
                        Vector2 constrainDir = CurrentLineConstraint == LineConstraint.Parallel
                            ? _referenceLineDirection
                            : new Vector2(-_referenceLineDirection.Y, _referenceLineDirection.X);

                        Vector2 toMouse = mousePos - _dragStart;
                        double magnitude = (toMouse.X * constrainDir.X) + (toMouse.Y * constrainDir.Y);

                        _currentLine.X2 = _dragStart.X + constrainDir.X * magnitude;
                        _currentLine.Y2 = _dragStart.Y + constrainDir.Y * magnitude;
                    }
                    else
                    {
                        _currentLine.X2 = mousePos.X;
                        _currentLine.Y2 = mousePos.Y;
                    }
                }
                return mousePos;
            }

            if (_currentShape != null)
            {
                double rawWidth = Math.Abs(mousePos.X - _dragStart.X);
                double rawHeight = Math.Abs(mousePos.Y - _dragStart.Y);

                double width, height;
                if (CurrentShape == DrawShape.Square || CurrentShape == DrawShape.Circle)
                {
                    double size = Math.Max(rawWidth, rawHeight);
                    width = size;
                    height = size;
                }
                else
                {
                    width = rawWidth;
                    height = rawHeight;
                }

                double x = mousePos.X >= _dragStart.X ? _dragStart.X : _dragStart.X - width;
                double y = mousePos.Y >= _dragStart.Y ? _dragStart.Y : _dragStart.Y - height;

                _currentShape.Width = width;
                _currentShape.Height = height;
                Canvas.SetLeft(_currentShape, x);
                Canvas.SetTop(_currentShape, y);
            }
            return mousePos;
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            List<UIElement> output = new();

            if (CurrentShape == DrawShape.Angle)
            {
                // safeguard: stop first click if angle is invalid / not set
                if (CurrentStep == 0 && LockedAngleDegrees == 0)
                    return output;

                switch (CurrentStep)
                {
                    case 0: // first click — point A, start line AB
                        _pointA = mousePos;
                        _currentLine = MakeLine(_pointA, _pointA);
                        output.Add(_currentLine);
                        CurrentOperation.Add(_currentLine);
                        CurrentStep++;
                        break;

                    case 1: // second click — point B, finalize AB, start BC locked to angle
                        _pointB = mousePos;
                        _currentLine.X2 = _pointB.X;
                        _currentLine.Y2 = _pointB.Y;
                        _currentLine = null;

                        // start the BC line from B
                        _currentAngleLine = MakeLine(_pointB, _pointB);
                        output.Add(_currentAngleLine);
                        CurrentOperation.Add(_currentAngleLine);
                        CurrentStep++;
                        break;

                    case 2: // third click — point C, finalize BC and calculate results
                        Vector2 pointC = ConstrainToAngle(_pointB, mousePos, LockedAngleDegrees);
                        _currentAngleLine.X2 = pointC.X;
                        _currentAngleLine.Y2 = pointC.Y;

                        // calculate lengths
                        double dxAB = _pointB.X - _pointA.X;
                        double dyAB = _pointB.Y - _pointA.Y;
                        double lengthAB = Math.Sqrt(dxAB * dxAB + dyAB * dyAB);

                        double dxBC = pointC.X - _pointB.X;
                        double dyBC = pointC.Y - _pointB.Y;
                        double lengthBC = Math.Sqrt(dxBC * dxBC + dyBC * dyBC);

                        // ratio of AB to BC
                        LineLengthRatioResult = lengthBC > 0.00001
                            ? Math.Round(lengthAB / lengthBC, 2)
                            : "N/A";

                        DrawAspectRatioResult = 0;
                        ShapeAreaResult = "N/A";

                        CommitOperation(new DrawOperation
                        {
                            OperationKind = "Lines",
                            SourceMode = this,
                            Elements = new List<UIElement>(CurrentOperation),
                            DrawAspectRatio = 0,
                            ShapeArea = 0,
                            LineLength = lengthAB, // store AB as the reference length
                            LineDirection = _referenceLineDirection
                        });

                        CurrentOperation.Clear();
                        _currentAngleLine = null;
                        CurrentStep = 0;
                        break;
                }
                return output;
            }

            if (CurrentShape == DrawShape.None)
                return output;

            if (CurrentShape == DrawShape.Line)
            {
                switch (CurrentStep)
                {
                    case 0: // start line
                        _dragStart = mousePos;
                        _currentLine = MakeLine(mousePos, mousePos);
                        output.Add(_currentLine);
                        CurrentOperation.Add(_currentLine);
                        CurrentStep = 1;
                        break;

                    case 1: // finish line
                        Vector2 finalPoint = mousePos;

                        if (CurrentLineConstraint != LineConstraint.None && _hasReferenceLineDirection)
                        {
                            Vector2 constrainDir = CurrentLineConstraint == LineConstraint.Parallel
                                ? _referenceLineDirection
                                : new Vector2(-_referenceLineDirection.Y, _referenceLineDirection.X);

                            Vector2 toMouse = mousePos - _dragStart;
                            double magnitude = (toMouse.X * constrainDir.X) + (toMouse.Y * constrainDir.Y);

                            finalPoint = new Vector2(
                                _dragStart.X + constrainDir.X * magnitude,
                                _dragStart.Y + constrainDir.Y * magnitude);
                        }

                        _currentLine.X2 = finalPoint.X;
                        _currentLine.Y2 = finalPoint.Y;

                        // calculate direction ONLY for first constrained line
                        Vector2 rawDir = new Vector2(
                            (float)(_currentLine.X2 - _currentLine.X1),
                            (float)(_currentLine.Y2 - _currentLine.Y1));

                        double lenSq = rawDir.X * rawDir.X + rawDir.Y * rawDir.Y;

                        if (lenSq > 0.000001)
                        {
                            rawDir.Normalize();
                        }

                        if (CurrentLineConstraint != LineConstraint.None && !_hasReferenceLineDirection && lenSq > 0.000001)
                        {
                            _referenceLineDirection = rawDir;
                            _hasReferenceLineDirection = true;
                        }

                        // calculate length
                        double dx = mousePos.X - _dragStart.X;
                        double dy = mousePos.Y - _dragStart.Y;
                        double length = Math.Sqrt(dx * dx + dy * dy);

                        DrawAspectRatioResult = 0;
                        ShapeAreaResult = "N/A";

                        var prev = FindPreviousLine(0);
                        if (prev != null && prev.LineLength > 0.00001)
                            LineLengthRatioResult = Math.Round(length / prev.LineLength, 2);
                        else
                            LineLengthRatioResult = "N/A";


                        CommitOperation(new DrawOperation
                        {
                            OperationKind = "Lines",
                            SourceMode = this,
                            Elements = new List<UIElement>(CurrentOperation),
                            DrawAspectRatio = 0,
                            ShapeArea = 0,
                            LineLength = length
                        });

                        CurrentOperation.Clear();
                        _currentLine = null;
                        CurrentStep = 0;
                        break;
                }

                return output;
            }

            // existing shape logic
            switch (CurrentStep)
            {
                case 0:
                    _dragStart = mousePos;
                    _currentShape = MakeShape(mousePos, mousePos);
                    output.Add(_currentShape);
                    CurrentOperation.Add(_currentShape);
                    CurrentStep++;
                    break;

                case 1:
                    double rawW = Math.Abs(mousePos.X - _dragStart.X);
                    double rawH = Math.Abs(mousePos.Y - _dragStart.Y);

                    double finalWidth, finalHeight;
                    if (CurrentShape == DrawShape.Square || CurrentShape == DrawShape.Circle)
                    {
                        double size = Math.Max(rawW, rawH);
                        finalWidth = size;
                        finalHeight = size;
                    }
                    else
                    {
                        finalWidth = rawW;
                        finalHeight = rawH;
                    }

                    CalculateAndUpdateResults(finalWidth, finalHeight);

                    CommitOperation(new DrawOperation   
                    {
                        OperationKind = "Shape",
                        SourceMode = this,
                        Elements = new List<UIElement>(CurrentOperation),
                        DrawAspectRatio = DrawAspectRatioResult,
                        ShapeArea = _currentShapeArea
                    });

                    CurrentOperation.Clear();
                    _currentShape = null;
                    CurrentStep = 0;
                    break;
            }
            return output;
        }

        public void UpdateAngle(string textInput)
        {
            if (double.TryParse(textInput, out double val))
            {
                this.LockedAngleDegrees = val;
            }
            else
            {
                // If the user clears the box or types nonsense, 
                // we set it to 0 to trigger our safeguards.
                this.LockedAngleDegrees = 0;
            }
        }

        private Line MakeLine(Vector2 a, Vector2 b)
        {
            Line L = new();
            L.Stroke = this.LineColor;
            L.StrokeThickness = 2;
            L.X1 = a.X;
            L.Y1 = a.Y;
            L.X2 = b.X;
            L.Y2 = b.Y;
            return L;
        }

        private Vector2 ConstrainToAngle(Vector2 origin, Vector2 mousePos, double angleDegrees)
        {
            // get the direction of line AB (from A to B)
            Vector2 AB = _pointB - _pointA;
            double abAngleRadians = Math.Atan2(AB.Y, AB.X);

            // convert requested angle to radians
            // negate because screen Y increases downward, flipping clockwise/counterclockwise
            double offsetRadians = angleDegrees * Math.PI / 180.0;
            double lockedRadians = abAngleRadians + offsetRadians;

            // direction vector for BC
            Vector2 direction = new Vector2(Math.Cos(lockedRadians), Math.Sin(lockedRadians));

            // project mouse onto direction to get distance, no clamping
            Vector2 toMouse = mousePos - origin;
            double magnitude = (toMouse.X * direction.X) + (toMouse.Y * direction.Y);

            return new Vector2(origin.X + direction.X * magnitude, origin.Y + direction.Y * magnitude);
        }

        private Shape MakeShape(Vector2 start, Vector2 end)
        {
            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);

            Shape shape;
            switch (CurrentShape)
            {
                case DrawShape.Ellipse:
                case DrawShape.Circle:
                    shape = new System.Windows.Shapes.Ellipse();
                    break;
                default:
                    shape = new System.Windows.Shapes.Rectangle();
                    break;
            }    

            shape.Stroke = this.LineColor;
            shape.StrokeThickness = 2;
            shape.Width = width;
            shape.Height = height;
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);

            return shape;
        }

        private void CalculateAndUpdateResults(double width, double height)
        {
            double area;
            switch (CurrentShape)
            {
                case DrawShape.Ellipse:
                case DrawShape.Circle:
                    area = Math.PI * (width / 2.0) * (height / 2.0);
                    break;
                default: // Rectangle and Square
                    area = width * height;
                    break;
            }

            _currentShapeArea = area;

            double longer = Math.Max(width, height);
            double shorter = Math.Min(width, height);
            DrawAspectRatioResult = shorter > 0.00001 ? Math.Round(longer / shorter, 2) : 0;

            var prev = UndoRedoManager.History
                .OfType<DrawOperation>()
                .LastOrDefault();

            if (prev != null && prev.ShapeArea > 0.00001)
                ShapeAreaResult = Math.Round(area / prev.ShapeArea, 2);
            else
                ShapeAreaResult = "N/A";
        }

        public void BindDrawResults(
            System.Windows.Controls.Label DrawAspectRatioOutput,
            System.Windows.Controls.Label ShapeAreaOutput,
            System.Windows.Controls.Label lineLengthRatioOutput)
        {
            DrawAspectRatioOutput.DataContext = this;
            ShapeAreaOutput.DataContext = this;
            lineLengthRatioOutput.DataContext = this;

            DrawAspectRatioOutput.SetBinding(Label.ContentProperty, new Binding(nameof(DrawAspectRatioResult)));
            ShapeAreaOutput.SetBinding(Label.ContentProperty, new Binding(nameof(ShapeAreaResult)));
            lineLengthRatioOutput.SetBinding(Label.ContentProperty, new Binding(nameof(LineLengthRatioResult)));
        }
    }
}
