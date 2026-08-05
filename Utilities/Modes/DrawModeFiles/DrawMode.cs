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
    /// <summary>Draw mode for creating constrained shapes and lines on the canvas.</summary>
    public class DrawMode : WorkMode
    {
        // ---- Shared draw state ----

        public override UserControl CreateControlPanel() => new DrawControlPanel(this);
        public override string TabName => "Draw";
        public override bool IsStartingNewOperation => CurrentStep == 0;

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
            // Clear the transient state so the next draw starts cleanly.
            CurrentOperation.Clear();
            _currentShape = null;
            _currentLine = null;
            CurrentStep = 0;
        }

        internal override void OnHistoryChanged()
        {
            base.OnHistoryChanged();

            // Clear the stored reference direction once no constrained line remains.
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
            // Reset the in-progress gesture without clearing the full mode state.
            CurrentStep = 0;
            _currentShape = null;
            _currentLine = null;
            _dragStart = default;
            _referenceLineDirection = default;
            _hasReferenceLineDirection = false;
            CurrentOperation.Clear();
        }

        // ---- Scaled measurements ----

        // Canvas-space measurements behind the scaled rows, kept so those rows can be
        // re-derived whenever the calibration changes or an undo restores a different
        // shape or line.
        private double _canvasShapeArea;
        private bool _hasCanvasShapeArea;
        private double _canvasLineLength;
        private bool _hasCanvasLineLength;

        private void RecomputeScaledResults()
        {
            ShapeAreaScaledResult = FormatScaledArea(_canvasShapeArea, _hasCanvasShapeArea);
            LineLengthScaledResult = FormatScaledLength(_canvasLineLength, _hasCanvasLineLength);
        }

        /// <summary>Restores the canvas-space area behind the shape row.</summary>
        public void RestoreShapeMeasurement(double canvasArea)
        {
            _canvasShapeArea = canvasArea;
            _hasCanvasShapeArea = true;
            RecomputeScaledResults();
        }

        /// <summary>Restores the canvas-space length behind the line row.</summary>
        public void RestoreLineMeasurement(double canvasLength)
        {
            _canvasLineLength = canvasLength;
            _hasCanvasLineLength = true;
            RecomputeScaledResults();
        }

        public override void ClearMetadata()
        {
            DrawAspectRatioResult = 0;
            RelativeAreaResult = "N/A";
            LineLengthRatioResult = "N/A";
            LineAngleResult = "N/A";
            _canvasShapeArea = 0;
            _hasCanvasShapeArea = false;
            _canvasLineLength = 0;
            _hasCanvasLineLength = false;
            RecomputeScaledResults();
        }

        public override void RefreshScalePlaceholders()
        {
            RecomputeScaledResults();
        }

        // ---- Shape tools ----

        public enum ShapeConstraint
        {
            None,
            Ellipse,
            Circle,
            Rectangle,
            Square,
        }

        public ShapeConstraint CurrentShape { get; set; } = ShapeConstraint.None;

        private Shape _currentShape = null;

        private double _drawAspectRatioResult;
        private object _relativeAreaResult;

        private string _shapeAreaScaledResult = "Unscaled";
        public string ShapeAreaScaledResult
        {
            get => _shapeAreaScaledResult;
            set => SetField(ref _shapeAreaScaledResult, value);
        }

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

            // Constrain size first, then reposition the shape so it grows from the anchor.
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
                    // First click records the anchor and creates the preview shape.
                    _dragStart = mousePos;
                    _currentShape = MakeShape(mousePos, mousePos);
                    output.Add(_currentShape);
                    CurrentOperation.Add(_currentShape);
                    CurrentStep++;
                    break;

                case 1:
                    // Second click finalizes the size, computes results, and commits the operation.
                    var (width, height) = GetConstrainedShapeSize(mousePos);

                    CalculateAndUpdateResults(width, height);

                    CommitCurrentOperation(new ShapeOperation
                    {
                        OperationKind = "Shape",

                        // The kind has to survive the draw: the workshop table keeps a
                        // separate column set and a separate attempt count per shape.
                        ShapeKind = CurrentShape,

                        DrawAspectRatio = DrawAspectRatioResult,
                        RelativeArea = RelativeAreaResult,
                        ShapeArea = _canvasShapeArea
                    });

                    FinishOperation();
                    break;
            }

            return output;
        }

        private (double x, double y) GetShapePosition(Vector2 mousePos, double width, double height)
        {
            // Keep the original click as the anchor even when the pointer moves left/up.
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
            // Width/height are stored separately from the position so the shape can be resized live.
            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);

            Shape shape;
            switch (CurrentShape)
            {
                case ShapeConstraint.Ellipse:
                case ShapeConstraint.Circle:
                    shape = new Ellipse();
                    break;
                default:
                    shape = new Rectangle();
                    break;
            }

            shape.Stroke = LineColor;
            shape.StrokeThickness = 2;
            shape.Width = width;
            shape.Height = height;
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);

            return shape;
        }

        // ---- Line tools ----

        public enum LineConstraint
        {
            None,
            Parallel,
            Perpendicular,
            AngleLocked
        }

        public LineConstraint CurrentLineType { get; set; } = LineConstraint.None;

        private Line _currentLine = null;
        private Vector2 _referenceLineDirection;
        private bool _hasReferenceLineDirection;

        private string _lineLengthScaledResult = "Unscaled";
        public string LineLengthScaledResult
        {
            get => _lineLengthScaledResult;
            set => SetField(ref _lineLengthScaledResult, value);
        }

        public double LockedAngleDegrees { get; set; } = 0;

        private object _lineLengthRatioResult;
        public object LineLengthRatioResult
        {
            get => _lineLengthRatioResult;
            set => SetField(ref _lineLengthRatioResult, value);
        }

        private object _lineAngleResult = "N/A";

        /// Clockwise angle in degrees between the line just drawn and the line it was
        /// drawn against, or "N/A" for the first line. Boxed as a double or that
        /// string, matching LineLengthRatioResult.
        public object LineAngleResult
        {
            get => _lineAngleResult;
            set => SetField(ref _lineAngleResult, value);
        }

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

            // Recompute the live endpoint each frame so the preview follows the constraint.
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
                    // First click creates the preview line and stores the anchor point.
                    _dragStart = mousePos;
                    _currentLine = MakeLine(mousePos, mousePos);
                    output.Add(_currentLine);
                    CurrentOperation.Add(_currentLine);
                    CurrentStep = 1;
                    break;

                case 1:
                    // Second click locks the endpoint, updates measurements, and commits the line.
                    Vector2 finalPoint = ApplyLineConstraint(_dragStart, mousePos);
                    _currentLine.X2 = finalPoint.X;
                    _currentLine.Y2 = finalPoint.Y;

                    // Measured before the reference direction is captured below: the
                    // line that DEFINES the reference was drawn freely, so its own
                    // angle belongs to the line before it, not to itself.
                    object angle = MeasureLineAngle();

                    TryCaptureReferenceDirection();
                    CommitLine(angle);

                    FinishOperation();
                    break;
            }

            return output;
        }

        private void TryCaptureReferenceDirection()
        {
            // The first constrained line defines the direction used by later parallel/perpendicular lines.
            if (CurrentLineType == LineConstraint.None || _hasReferenceLineDirection)
                return;

            Vector2 rawDir = new Vector2(_currentLine.X2 - _currentLine.X1, _currentLine.Y2 - _currentLine.Y1);
            double lenSq = rawDir.X * rawDir.X + rawDir.Y * rawDir.Y;

            if (lenSq <= 0.000001) return;

            double len = Math.Sqrt(lenSq);
            _referenceLineDirection = new Vector2(rawDir.X / len, rawDir.Y / len);
            _hasReferenceLineDirection = true;
        }

        private void CommitLine(object angleToPrevious)
        {
            // Measure the final line in canvas pixels before converting to calibrated units.
            double dx = _currentLine.X2 - _currentLine.X1;
            double dy = _currentLine.Y2 - _currentLine.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);

            _canvasLineLength = length;
            _hasCanvasLineLength = true;
            RecomputeScaledResults();

            // Compare against the previous measured line, if one exists.
            var prev = PreviousLine();
            LineLengthRatioResult = GeometryCalculations.RelativeLength(length, prev?.LineLength ?? 0);
            LineAngleResult = angleToPrevious;

            CommitCurrentOperation(new LineOperation
            {
                OperationKind = "Lines",
                LineLength = length,
                LineLengthRatio = LineLengthRatioResult,
                LineAngle = LineAngleResult,
                HeadingDegrees = HeadingOf(dx, dy)
            });
        }

        /// The most recent committed line with real length, or null when this is the
        /// first one. Called before the new line is committed, so it never finds
        /// the line being drawn.
        private LineOperation PreviousLine() =>
            OperationsOfKind<LineOperation>().LastOrDefault(op => op.LineLength > 0.00001);

        // ---- Line angle ----

        /// Clockwise angle from the line this one was drawn against to the line just
        /// drawn, in degrees within [0, 360). "N/A" when there is nothing to measure
        /// against, matching how the line-ratio row reports the first line.
        private object MeasureLineAngle()
        {
            double dx = _currentLine.X2 - _currentLine.X1;
            double dy = _currentLine.Y2 - _currentLine.Y1;

            // A click that never moved has no direction to report.
            if (Math.Sqrt(dx * dx + dy * dy) < 0.00001) return "N/A";

            double heading = HeadingOf(dx, dy);

            // A constrained line is drawn against the reference direction rather than
            // against whatever happened to be drawn last, so that is what its angle is
            // measured from. A run of perpendicular lines therefore reads 90, 90, 90
            // instead of 90 followed by zeroes.
            if (CurrentLineType != LineConstraint.None && _hasReferenceLineDirection)
            {
                return Sweep(heading - HeadingOf(_referenceLineDirection.X, _referenceLineDirection.Y));
            }

            // Free-drawn: measure against the line before it.
            var previous = PreviousLine();
            if (previous == null) return "N/A";

            return Sweep(heading - previous.HeadingDegrees);
        }

        // Canvas Y grows downwards, so atan2 already sweeps clockwise on screen:
        // 0° points right, 90° down, 180° left, 270° up. Reading a clock face, a line
        // drawn towards 11 o'clock after one drawn towards 12 gives 330, not 30.
        private static double HeadingOf(double dx, double dy) =>
            Normalize360(Math.Atan2(dy, dx) * 180.0 / Math.PI);

        private static double Sweep(double degrees)
        {
            double value = Math.Round(Normalize360(degrees), 1);

            // Rounding can push 359.97 over the top; a full turn is no turn.
            return value >= 360.0 ? 0.0 : value;
        }

        private static double Normalize360(double degrees)
        {
            degrees %= 360.0;
            return degrees < 0 ? degrees + 360.0 : degrees;
        }

        // ---- Line constraints ----

        private Vector2 ApplyLineConstraint(Vector2 start, Vector2 mousePos)
        {
            if (CurrentLineType == LineConstraint.None || !_hasReferenceLineDirection)
                return mousePos;

            if (CurrentLineType == LineConstraint.AngleLocked)
                return ConstrainToAngle(start, mousePos, LockedAngleDegrees);

            // Parallel uses the captured direction; perpendicular rotates that direction by 90°.
            Vector2 constrainDir = CurrentLineType == LineConstraint.Parallel
                ? _referenceLineDirection
                : new Vector2(-_referenceLineDirection.Y, _referenceLineDirection.X);

            Vector2 toMouse = mousePos - start;
            double magnitude = (toMouse.X * constrainDir.X) + (toMouse.Y * constrainDir.Y);

            // Project the mouse vector onto the constraint direction.
            return new Vector2(start.X + constrainDir.X * magnitude, start.Y + constrainDir.Y * magnitude);
        }

        private (double width, double height) GetConstrainedShapeSize(Vector2 mousePos)
        {
            // Measure raw size from the anchor point.
            double rawW = Math.Abs(mousePos.X - _dragStart.X);
            double rawH = Math.Abs(mousePos.Y - _dragStart.Y);

            // Squares/circles lock both dimensions to the larger drag distance.
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
                LockedAngleDegrees = val;
            }
            else
            {
                // Invalid input disables the explicit angle lock until the user enters a number again.
                LockedAngleDegrees = 0;
            }
        }

        private Vector2 ConstrainToAngle(Vector2 origin, Vector2 mousePos, double angleDegrees)
        {
            // Start from the reference line direction, then add the user-specified offset.
            double baseAngleRadians = Math.Atan2(_referenceLineDirection.Y, _referenceLineDirection.X);
            double lockedRadians = baseAngleRadians + angleDegrees * Math.PI / 180.0;

            Vector2 direction = new Vector2(Math.Cos(lockedRadians), Math.Sin(lockedRadians));
            Vector2 toMouse = mousePos - origin;
            double magnitude = (toMouse.X * direction.X) + (toMouse.Y * direction.Y);

            // Project onto the rotated direction to keep the line at the requested angle.
            return new Vector2(origin.X + direction.X * magnitude, origin.Y + direction.Y * magnitude);
        }

        // ---- Results and tips ----

        private void CalculateAndUpdateResults(double width, double height)
        {
            // Use ellipse math for round shapes and rectangle math for box shapes.
            double area = (CurrentShape == ShapeConstraint.Ellipse || CurrentShape == ShapeConstraint.Circle)
                ? GeometryCalculations.EllipseArea(width, height)
                : GeometryCalculations.RectangleArea(width, height);

            _canvasShapeArea = area;
            _hasCanvasShapeArea = true;
            RecomputeScaledResults();

            DrawAspectRatioResult = height > 1e-5 ? Math.Round(width / height, 2) : 0;

            // Relative area compares against the most recent shape OF THE SAME KIND,
            // so a circle is measured against the previous circle rather than against
            // whatever shape happened to be drawn before it.
            var previous = OperationsOfKind<ShapeOperation>()
                .LastOrDefault(op => op.ShapeKind == CurrentShape);

            RelativeAreaResult = GeometryCalculations.RelativeArea(area, previous?.ShapeArea ?? 0);
        }

        private static readonly string[] ShapeTips = BuildTips(
            "💡 Any number of shapes or lines may be overlaid on the image. Each click adds a new shape or line.",
            "💡 Aspect ratio is the horizontal length of the shape divided by its maximum height.",
            "💡 Each shape kind is counted and exported separately: rectangles, squares, ellipses, and circles have their own columns.");

        private static readonly string[] LineTips = BuildTips(
            "💡 Any number of shapes or lines may be overlaid on the image. Each click adds a new shape or line.",
            "💡 Line ratio is the length of the most recently drawn line (Line n) divided by the length of the line drawn before it (Line n-1).",
            "💡 Angle is measured clockwise from the previous line, so a line drawn towards 11 o'clock after one towards 12 reads 330°.");

        private static readonly string[] NoMethodTips = BuildTips(
            "💡 Select a drawing method to begin.");

        public override string[] GetTips()
        {
            if (IsShapeSelected) return ShapeTips;
            if (IsLineSelected) return LineTips;
            return NoMethodTips;
        }
    }
}