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
    public class DrawMode : WorkMode
    {
        #region Shared Draw Infrastructure
        //-----Shared Code Across Draw Operations-----//
        public override UserControl CreateControlPanel() => new DrawControlPanel(this);
        public override string TabName => "Draw";
        public override bool IsStartingNewOperation => CurrentStep == 0 || CurrentStep == 3;

        public override void ClearMetadata()
        {
            DrawAspectRatioResult = 0;
            RelativeAreaResult = "N/A";
            LineLengthRatioResult = "N/A";
        }

        // method tracker
        public enum DrawMethod
        {
            None,
            Shape,
            Line
        }
        private DrawMethod _currentMethod = DrawMethod.None;

        public DrawMethod CurrentMethod
        {
            get => _currentMethod;
            set
            {
                if (!SetField(ref _currentMethod, value)) return;
                OnPropertyChanged(nameof(IsShapeSelected));
                OnPropertyChanged(nameof(IsLineSelected));
                OnTipChanged?.Invoke();
            }
        }

        public bool IsShapeSelected => CurrentMethod == DrawMethod.Shape;
        public bool IsLineSelected => CurrentMethod == DrawMethod.Line;

        // Tracking drawn elements
        private List<UIElement> CurrentOperation = new();
        private Vector2 _dragStart;

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            return CurrentMethod switch
            {
                DrawMethod.Line => ProcessLineClick(mousePos),
                DrawMethod.Shape => ProcessShapeClick(mousePos),
                _ => new List<UIElement>()
            };
        }

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            return CurrentMethod switch
            {
                DrawMethod.Shape => ProcessShapeMouseMovement(mousePos),
                DrawMethod.Line => ProcessLineMouseMovement(mousePos),
                _ => mousePos
            };
        }

        public void SelectDrawMethod(string tag)
        {
            if (Enum.TryParse(tag, out DrawMethod method))
            {
                CurrentMethod = method;
                ResetDrawingState();
            }
        }

        private void FinishOperation()
        {
            CurrentOperation.Clear();
            _currentShape = null;
            _currentLine = null;
            CurrentStep = 0;
        }

        internal override void OnHistoryChanged()
        {
            // Reset the captured reference direction once no constrained line remains in
            // history, so the next line drawn establishes a fresh reference. (This is
            // HandleReferenceLineUndo's logic, finally on a hook that actually runs.)
            bool anyConstrainedLinesRemain = UndoRedoManager?.History
                .OfType<LineOperation>()
                .Any(op => op.LineLength > 0.00001) ?? false;

            if (!anyConstrainedLinesRemain)
            {
                _hasReferenceLineDirection = false;
                _referenceLineDirection = default;
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
            RelativeAreaResult = "N/A";
            LineLengthRatioResult = "N/A";
            _currentShape = null;
            _currentLine = null;
            _dragStart = default;
            _referenceLineDirection = default;
            _hasReferenceLineDirection = false;
            CurrentOperation.Clear();
            CurrentStep = 0;
        }

        #endregion

        #region Shape Operations
        //-----Shape Operation Code-----//
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

        // Tracking drawn elements
        private Shape _currentShape = null;

        // Bindable results of shape calculations
        // private/public pairs used to handle propagation of results to UI bindings
        private double _drawAspectRatioResult;
        private object _relativeAreaResult;
        private double _currentShapeArea = 0;

        public double DrawAspectRatioResult
        {
            get => _drawAspectRatioResult;
            set => SetField(ref _drawAspectRatioResult, value);
        }

        public object RelativeAreaResult
        {
            get => _relativeAreaResult;
            set => SetField(ref _relativeAreaResult, value);
        }

        // switch to shape operation
        public void SelectShape(string tag)
        {
            if (Enum.TryParse(tag, out ShapeConstraint shape))
            {
                CurrentShape = shape;
                ResetDrawingState();
            }
        }

        private Vector2 ProcessShapeMouseMovement(Vector2 mousePos)
        {
            if (CurrentShape == ShapeConstraint.None || _currentShape == null)
                return mousePos;

            var (width, height) = GetConstrainedShapeSize(mousePos);
            var (x, y) = GetShapePosition(mousePos, width, height);

            _currentShape.Width = width;
            _currentShape.Height = height;

            Canvas.SetLeft(_currentShape, x);
            Canvas.SetTop(_currentShape, y);

            return mousePos;
        }

        private List<UIElement> ProcessShapeClick(Vector2 mousePos)
        {
            List<UIElement> output = new();

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
                        RelativeArea = RelativeAreaResult,
                        ShapeArea = _currentShapeArea
                    });

                    FinishOperation();
                    break;
            }

            return output;
        }

        private (double x, double y) GetShapePosition(Vector2 mousePos,double width,double height)
        {
            double x = mousePos.X >= _dragStart.X
                ? _dragStart.X
                : _dragStart.X - width;

            double y = mousePos.Y >= _dragStart.Y
                ? _dragStart.Y
                : _dragStart.Y - height;

            return (x, y);
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
        #endregion

        #region Line Operations
        //-----Line Operation Code-----//
        // line type tracker
        public enum LineConstraint
        {
            None,
            Parallel,
            Perpendicular,
            AngleLocked
        }
        public LineConstraint CurrentLineType { get; set; } = LineConstraint.None;

        // Tracking drawn elements
        private Line _currentLine = null;
        private Vector2 _referenceLineDirection;
        private bool _hasReferenceLineDirection;
        public double LockedAngleDegrees { get; set; } = 0;

        // Bindable results of shape calculations
        // private/public pairs used to handle propagation of results to UI bindings
        private object _lineLengthRatioResult;

        public object LineLengthRatioResult
        {
            get => _lineLengthRatioResult;
            set => SetField(ref _lineLengthRatioResult, value);
        }

        // switch to line operation
        public void SelectLineConstraint(string tag)
        {
            if (Enum.TryParse(tag, out LineConstraint constraint))
            {
                CurrentLineType = constraint;
                ResetDrawingState();
            }
        }

        private Vector2 ProcessLineMouseMovement(Vector2 mousePos)
        {
            if (_currentLine == null)
                return mousePos;

            Vector2 constrained = ApplyLineConstraint(_dragStart, mousePos);

            _currentLine.X2 = constrained.X;
            _currentLine.Y2 = constrained.Y;

            return mousePos;
        }

        private List<UIElement> ProcessLineClick(Vector2 mousePos)
        {
            List<UIElement> output = new();
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

                    TryCaptureReferenceDirection();
                    CommitLine();

                    FinishOperation();
                    break;
            }
            return output;
        }

        private void TryCaptureReferenceDirection()
        {
            if (CurrentLineType == LineConstraint.None || _hasReferenceLineDirection)
                return;

            Vector2 rawDir = new Vector2(_currentLine.X2 - _currentLine.X1, _currentLine.Y2 - _currentLine.Y1);
            double lenSq = rawDir.X * rawDir.X + rawDir.Y * rawDir.Y;

            if (lenSq <= 0.000001) return;

            double len = Math.Sqrt(lenSq);
            _referenceLineDirection = new Vector2(rawDir.X / len, rawDir.Y / len);
            _hasReferenceLineDirection = true;
        }

        private void CommitLine()
        {
            double dx = _currentLine.X2 - _currentLine.X1;
            double dy = _currentLine.Y2 - _currentLine.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);

            var prev = FindPreviousLine(0);
            LineLengthRatioResult = GeometryCalculations.RelativeLength(length, prev?.LineLength ?? 0);

            CommitOperation(new LineOperation
            {
                OperationKind = "Lines",
                SourceMode = this,
                Elements = new List<UIElement>(CurrentOperation),
                LineLength = length,
                LineLengthRatio = LineLengthRatioResult
            });
        }

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

        private Vector2 ConstrainToAngle(Vector2 origin, Vector2 mousePos, double angleDegrees)
        {
            double baseAngleRadians = Math.Atan2(_referenceLineDirection.Y, _referenceLineDirection.X);
            double lockedRadians = baseAngleRadians + angleDegrees * Math.PI / 180.0;

            Vector2 direction = new Vector2(Math.Cos(lockedRadians), Math.Sin(lockedRadians));
            Vector2 toMouse = mousePos - origin;
            double magnitude = (toMouse.X * direction.X) + (toMouse.Y * direction.Y);

            return new Vector2(origin.X + direction.X * magnitude, origin.Y + direction.Y * magnitude);
        }

        

        #endregion

        #region Results
        private void CalculateAndUpdateResults(double width, double height)
        {
            double area = (CurrentShape == ShapeConstraint.Ellipse || CurrentShape == ShapeConstraint.Circle)
                ? GeometryCalculations.EllipseArea(width, height)
                : GeometryCalculations.RectangleArea(width, height);
            _currentShapeArea = area;
            DrawAspectRatioResult = height > 1e-5 ? Math.Round(width / height, 2) : 0;

            var prev = UndoRedoManager.History
                .OfType<ShapeOperation>()
                .LastOrDefault();

            double previousArea = prev?.ShapeArea ?? 0;
            RelativeAreaResult = GeometryCalculations.RelativeArea(area, previousArea);
        }

        public override string[] GetTips()
        {
            if (IsShapeSelected)
                return new[]
                {
            "💡 Any number of shapes or lines may be overlaid on the image. Each click adds a new shape or line.",
            "💡 Aspect ratio is the horizontal length of the shape divided by its maximum height.",
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
            "💡 Zoom in or out using the scroll wheel.",
            "💡 Press 'Ctrl' and left click to drag the image.",
            "💡 The user guide and software information can be found in the Help menu.",
            "💡 Toggle tip visibility in the View menu."
        };
            if (IsLineSelected)
                return new[]
                {
            "💡 Any number of shapes or lines may be overlaid on the image. Each click adds a new shape or line.",
            "💡 Line ratio is the length of the most recently drawn line (Line n) divided by the length of the line drawn before it (Line n-1).",
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
            "💡 Zoom in or out using the scroll wheel.",
            "💡 Press 'Ctrl' and left click to drag the image.",
            "💡 The user guide and software information can be found in the Help menu.",
            "💡 Toggle tip visibility in the View menu."
        };
            return new[]
            {
                "💡 Select a drawing method to begin.",
                "💡 The user guide and software information can be found in the Help menu.",
                "💡 Press 'Ctrl+F' to open an image, or select 'Open Image' in the File menu.",
                "💡 Zoom in or out using the scroll wheel.",
                "💡 Press 'Ctrl' and left click to drag the image.",
                "💡 Toggle tip visibility in the View menu."
            };
        }
        #endregion
    }
}
