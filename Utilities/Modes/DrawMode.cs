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

        // method tracker
        public enum DrawMethod
        {
            None,
            Shape,
            Line
        }
        public DrawMethod CurrentMethod { get; set; } = DrawMethod.None;

        // shape type tracker
        public enum ShapeConstraint
        {
            None,
            Ellipse,
            Circle,
            Rectangle,
            Square,
        }
        public ShapeConstraint CurrentShape { get; set; } = ShapeConstraint.None;

        // line type tracker
        public enum LineConstraint
        {
            None,
            Parallel,
            Perpendicular,
            AngleLocked
        }
        public LineConstraint CurrentLineType { get; set; } = LineConstraint.None;

        // Helper function to find the most recent line operation in history for line length ratio calculations
        private LineOperation FindPreviousLine(int skipLast)
        {
            var prev = UndoRedoManager.History
                    .Reverse()
                    .Skip(skipLast)
                    .OfType<LineOperation>()
                    .FirstOrDefault(op => op.LineLength > 0.00001);

            return prev;
        }

        protected override void OnOperationUndone(WorkOperation operation)
        {
            if (operation is LineOperation undone && undone.LineLength > 0.00001)
            {
                bool anyConstrainedLinesRemain = UndoRedoManager.History
                    .OfType<LineOperation>()
                    .Any(op => op.LineLength > 0.00001);

                if (!anyConstrainedLinesRemain)
                {
                    _hasReferenceLineDirection = false;
                    _referenceLineDirection = default;
                }
            }

            // Restore from the new top of the global history
            var prevLine = UndoRedoManager.History
                .OfType<LineOperation>()
                .LastOrDefault();

            var prevShape = UndoRedoManager.History
                .OfType<ShapeOperation>()
                .LastOrDefault();

            if (prevLine != null)
            {
                // restore aspect ratio and area
                DrawAspectRatioResult = prevShape.DrawAspectRatio;

                var previousPrev = UndoRedoManager.History
                    .OfType<ShapeOperation>()
                    .Reverse()
                    .Skip(1)
                    .FirstOrDefault(op => op.ShapeArea > 0.00001);

                if (previousPrev != null && previousPrev.ShapeArea > 0.00001)
                    ShapeAreaResult = Math.Round(prevShape.ShapeArea / previousPrev.ShapeArea, 2);
                else
                    ShapeAreaResult = "N/A";

                // restore line length ratio
                if (prevLine.LineLength > 0.00001)
                {
                    var prevPrevLine = FindPreviousLine(1);
                    if (prevPrevLine != null)
                        LineLengthRatioResult = Math.Round(prevLine.LineLength / prevPrevLine.LineLength, 2);
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
            if (operation is ShapeOperation prevShape)
            {
                DrawAspectRatioResult = prevShape.DrawAspectRatio;

                var prevInHistory = UndoRedoManager.History
                    .OfType<ShapeOperation>()
                    .Reverse()
                    .Skip(1)
                    .FirstOrDefault(prevOp => prevOp.ShapeArea > 0.00001);

                if (prevInHistory != null && prevInHistory.ShapeArea > 0.00001)
                    ShapeAreaResult = Math.Round(prevShape.ShapeArea / prevInHistory.ShapeArea, 2);
                else
                    ShapeAreaResult = "N/A";
            } 
            
            if (operation is LineOperation prevLine && prevLine.LineLength > 0.00001)
            {
                var prevPrevLine = FindPreviousLine(1);
                if (prevPrevLine != null)
                    LineLengthRatioResult = Math.Round(prevLine.LineLength / prevPrevLine.LineLength, 2);
                else
                    LineLengthRatioResult = "N/A";
            }
            else
            {
                LineLengthRatioResult = "N/A";
            }
        }

        // Tracking drawn elements
        private List<UIElement> CurrentOperation = new();
        private Vector2 _dragStart;
        private Shape _currentShape = null;
        private Line _currentLine = null;
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
            _dragStart = default;
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
            _dragStart = default;
            _referenceLineDirection = default;
            _hasReferenceLineDirection = false;
            CurrentOperation.Clear();
            CurrentStep = 0;
        }

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            if (CurrentMethod == DrawMethod.Shape)
            {
                if (CurrentShape == ShapeConstraint.None)
                { 
                    return mousePos; 
                }
                
                if (_currentShape != null)
                {
                    var (width, height) = GetConstrainedShapeSize(mousePos);

                    double x = mousePos.X >= _dragStart.X ? _dragStart.X : _dragStart.X - width;
                    double y = mousePos.Y >= _dragStart.Y ? _dragStart.Y : _dragStart.Y - height;

                    _currentShape.Width = width;
                    _currentShape.Height = height;
                    Canvas.SetLeft(_currentShape, x);
                    Canvas.SetTop(_currentShape, y);
                }
                return mousePos;
            }

            if (CurrentMethod == DrawMethod.Line)
            {
                if (_currentLine != null)
                {
                    Vector2 constrained = ApplyLineConstraint(_dragStart, mousePos);
                    _currentLine.X2 = constrained.X;
                    _currentLine.Y2 = constrained.Y;
                }
                return mousePos;
            }
            return mousePos;
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            List<UIElement> output = new();

            switch (CurrentMethod)
            {
                // line mode selected
                case DrawMethod.Line:
                    return ProcessLineClick(mousePos, output);

                // shape mode selected
                case DrawMethod.Shape:
                    return ProcessShapeClick(mousePos, output);

                // no mode selected
                default:
                    return output;
            }
        }

        private List<UIElement> ProcessLineClick(Vector2 mousePos, List<UIElement> output)
        {
            switch (CurrentStep)
            {
                case 0:
                    _dragStart = mousePos;
                    _currentLine = MakeLine(mousePos, mousePos);
                    output.Add(_currentLine);
                    CurrentOperation.Add(_currentLine);
                    CurrentStep = 1;
                    break;

                case 1:
                    Vector2 finalPoint = ApplyLineConstraint(_dragStart, mousePos);
                    _currentLine.X2 = finalPoint.X;
                    _currentLine.Y2 = finalPoint.Y;

                    // Capture reference direction from first constrained line
                    Vector2 rawDir = new Vector2(_currentLine.X2 - _currentLine.X1, _currentLine.Y2 - _currentLine.Y1);
                    double lenSq = rawDir.X * rawDir.X + rawDir.Y * rawDir.Y;
                    if (lenSq > 0.000001)
                    {
                        double len = Math.Sqrt(lenSq);
                        rawDir = new Vector2(rawDir.X / len, rawDir.Y / len);
                    }
                    if (CurrentLineType != LineConstraint.None && !_hasReferenceLineDirection && lenSq > 0.000001)
                    {
                        _referenceLineDirection = rawDir;
                        _hasReferenceLineDirection = true;
                    }

                    double dx = _currentLine.X2 - _currentLine.X1;
                    double dy = _currentLine.Y2 - _currentLine.Y1;
                    double length = Math.Sqrt(dx * dx + dy * dy);

                    var prev = FindPreviousLine(0);
                    LineLengthRatioResult = (prev != null && prev.LineLength > 0.00001)
                        ? Math.Round(length / prev.LineLength, 2)
                        : "N/A";

                    CommitOperation(new LineOperation
                    {
                        OperationKind = "Lines",
                        SourceMode = this,
                        Elements = new List<UIElement>(CurrentOperation),
                        LineLength = length,
                        LineLengthRatio = LineLengthRatioResult
                    });

                    CurrentOperation.Clear();
                    _currentLine = null;
                    CurrentStep = 0;
                    break;
            }

            return output;
        }

        private List<UIElement> ProcessShapeClick(Vector2 mousePos, List<UIElement> output)
        {
            if (CurrentShape == ShapeConstraint.None)
                return output;

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
                    var (width, height) = GetConstrainedShapeSize(mousePos);

                    CalculateAndUpdateResults(width, height);

                    CommitOperation(new ShapeOperation
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

        // Helper function
        private Vector2 ApplyLineConstraint(Vector2 start, Vector2 mousePos)
        {
            if (CurrentLineType == LineConstraint.None || !_hasReferenceLineDirection)
                return mousePos;

            if (CurrentLineType == LineConstraint.AngleLocked)
                return ConstrainToAngle(start, mousePos, LockedAngleDegrees);

            Vector2 constrainDir = CurrentLineType == LineConstraint.Parallel
                ? _referenceLineDirection
                : new Vector2(-_referenceLineDirection.Y, _referenceLineDirection.X);

            Vector2 toMouse = mousePos - start;
            double magnitude = (toMouse.X * constrainDir.X) + (toMouse.Y * constrainDir.Y);
            return new Vector2(start.X + constrainDir.X * magnitude, start.Y + constrainDir.Y * magnitude);
        }

        // Helper function
        private (double width, double height) GetConstrainedShapeSize(Vector2 mousePos)
        {
            double rawW = Math.Abs(mousePos.X - _dragStart.X);
            double rawH = Math.Abs(mousePos.Y - _dragStart.Y);

            if (CurrentShape == ShapeConstraint.Square || CurrentShape == ShapeConstraint.Circle)
            {
                double size = Math.Max(rawW, rawH);
                return (size, size);
            }
            return (rawW, rawH);
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
            if (_hasReferenceLineDirection)
            {
                double baseAngleRadians = Math.Atan2(_referenceLineDirection.Y, _referenceLineDirection.X);
                double offsetRadians = angleDegrees * Math.PI / 180.0;
                double lockedRadians = baseAngleRadians + offsetRadians;

                Vector2 direction = new Vector2(Math.Cos(lockedRadians), Math.Sin(lockedRadians));
                Vector2 toMouse = mousePos - origin;
                double magnitude = (toMouse.X * direction.X) + (toMouse.Y * direction.Y);

                return new Vector2(origin.X + direction.X * magnitude, origin.Y + direction.Y * magnitude);
            }
            else
            {
                return mousePos; // no reference yet, fall back to free movement
            }
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
                case ShapeConstraint.Ellipse:
                case ShapeConstraint.Circle:
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
                case ShapeConstraint.Ellipse:
                case ShapeConstraint.Circle:
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
                .OfType<ShapeOperation>()
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
