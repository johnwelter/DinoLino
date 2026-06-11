using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    public class CurvatureMode : WorkMode
    {
        #region Shared Curvature Infrastructure
        //-----BROAD/SHARED CURVATURE SECTION-----//
        public override UserControl CreateControlPanel() => new CurvatureControlPanel(this);
        public override string TabName => "Curvature";
        public override bool IsStartingNewOperation => CurrentStep == 0 || CurrentStep == 3;

        // enums for toggling between curvature methods and spline methods
        public enum CurvatureMethod
        {
            None,
            CircularArc,
            ParabolicArc,
            NPointSpline
        }

        public enum SplineAlgorithm { CatmullRom, Bezier }

        // set default nethod to none until selection made
        private CurvatureMethod _currentMethod = CurvatureMethod.None;
        public CurvatureMethod CurrentMethod
        {
            get => _currentMethod;
            set
            {
                if (!SetField(ref _currentMethod, value)) return;
                OnPropertyChanged(nameof(IsCircularArcSelected));
                OnPropertyChanged(nameof(IsParabolicArcSelected));
                OnPropertyChanged(nameof(IsNPointSplineSelected));
                OnTipChanged?.Invoke();
            }
        }

        public bool IsCircularArcSelected => CurrentMethod == CurvatureMethod.CircularArc;
        public bool IsParabolicArcSelected => CurrentMethod == CurvatureMethod.ParabolicArc;
        public bool IsNPointSplineSelected => CurrentMethod == CurvatureMethod.NPointSpline;

        // Current UI line to modify during mouse move
        private Line CurrentUILine = null;

        public void SelectCurvature(string? option)
        {
            if (!Enum.TryParse<CurvatureMethod>(option, ignoreCase: true, out var selection))
                return;

            switch (selection)
            {
                case CurvatureMethod.None:
                    SelectNone();
                    break;
                case CurvatureMethod.CircularArc:
                    SelectCircularArc();
                    break;
                case CurvatureMethod.ParabolicArc:
                    SelectParabolicArc();
                    break;
                case CurvatureMethod.NPointSpline:
                    SelectNPointSpline();
                    break;
            }
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            return CurrentMethod switch
            {
                CurvatureMethod.NPointSpline => FindTurningAngleMode
                    ? ProcessFindTurningAngleClick(mousePos)
                    : ProcessSplineClick(mousePos),
                CurvatureMethod.CircularArc => ProcessCircArcClick(mousePos),
                CurvatureMethod.ParabolicArc => ProcessParArcClick(mousePos),
                _ => new List<UIElement>()
            };
        }

        // no operation selected 
        public void SelectNone()
        {
            CurrentMethod = CurvatureMethod.None;
            ResetDrawingState();
        }

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            // "Find turning angle": lock the cursor circle to the nearest point on
            // the most recent spline so the user can probe it without clicking.
            if (FindTurningAngleMode)
            {
                if (TryProjectOntoSpline(mousePos, out Vector2 onCurve, out int idx))
                {
                    _turningIndex = idx;
                    UpdateWindowOval(idx);
                    return onCurve;
                }
                return mousePos;
            }

            if (CurrentMethod == CurvatureMethod.None)
                return mousePos;

            if (CurrentUILine == null) return mousePos;

            Vector2 modifiedPos = mousePos;
            switch (CurrentStep)
            {
                case 1:
                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    break;

                case 2:

                    // we want to lock everything to a given 2D vector
                    // origin at the Midpoint, project down and across
                    // we can do this by making a 2D vector of the mouse position, and dotting it to get the new magnitude
                    // add normalized direction + scale to midpoint to get new point

                    Vector2 toMouse = mousePos - Midpoint;
                    double newMag = Orthogonal | toMouse;

                    Vector2 newDist = Orthogonal * newMag;

                    CurrentUILine.X2 = Midpoint.X + newDist.X;
                    CurrentUILine.Y2 = Midpoint.Y + newDist.Y;
                    modifiedPos = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);
                    break;

            }
            return modifiedPos;
        }

        public override void ClearMetadata()
        {
            CentralAngleResult = 0;
            AspectRatioResult = 0;
            ChordArcRatioResult = 0;
            XYFunctionResult = "";
            PChordArcRatioResult = 0;
            RiseSpanRatioResult = 0;
            VertexCurvatureResult = 0;
            TurningAngleArcRatioResult = 0;
            SChordArcRatioResult = 0;
            SumTurningAnglesResult = 0;
            MeanTurningAngleResult = 0;
            VarianceTurningAnglesResult = 0;
            SplineLengthScaledResult = ScaledPlaceholder;
        }

        public override void RefreshScalePlaceholders()
        {
            SplineLengthScaledResult = ScaledPlaceholder;
        }

        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            _splinePoints.Clear();
            _splinePreview = null;
            _splineCurrentOperation.Clear();
            FindTurningAngleMode = false;
        }

        public override void Reset()
        {
            base.Reset();
            ClearMetadata();
            ResetDrawingState();
            _lastSplineDense = null;
            FindTurningAngleDisplay = "";
        }
        #endregion

        #region 3-Point Arc Section
        //-----THREE-POINT ARC SECTION-----//

        // Tracking 3-click line groups 
        private List<UIElement> CurrentOperation = new List<UIElement>();

        // All major POIs in generating curvature
        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 Midpoint;
        public Vector2 Orthogonal;
        public Vector2 PointC;
        public Vector2 Intersection;
        public Vector2 ACMid;
        public Vector2 BCMid;

        // Helper functions
        private void StartChord(Vector2 mousePos, List<UIElement> outputElements)
        {
            PointA = new Vector2(mousePos.X, mousePos.Y);
            CurrentUILine = MakeLine(mousePos, mousePos);
            outputElements.Add(CurrentUILine);
            CurrentOperation.Add(CurrentUILine);
            CurrentStep++;
        }

        private void FinishChord(Vector2 mousePos, List<UIElement> outputElements)
        {
            CurrentUILine.X2 = mousePos.X;
            CurrentUILine.Y2 = mousePos.Y;
            PointB = mousePos;
        }

        private void StartBisector(Vector2 mousePos, List<UIElement> outputElements)
        {
            Midpoint = (PointA + PointB) * 0.5;
            CurrentUILine = MakeLine(Midpoint, Midpoint);
            outputElements.Add(CurrentUILine);
            CurrentOperation.Add(CurrentUILine);

            // make the orthogonal
            Vector3 p1 = new Vector3(PointA.X, PointA.Y, 1);
            Vector3 p2 = new Vector3(PointB.X, PointB.Y, 1);
            Orthogonal = (p1 ^ p2).ToVector2();
            Orthogonal.Normalize();

            CurrentStep++;
        }
       

        #region Circular Arc Section
        //-----CIRCULAR ARCS-----//

        // Bindable results of curvature calculations
        // private/public pair used to handle propagation of results to UI bindings
        private double _chordArcRatioResult;
        public double ChordArcRatioResult
        {
            get => _chordArcRatioResult;
            set => SetField(ref _chordArcRatioResult, value);
        }

        private double _centralAngleResult;
        public double CentralAngleResult
        {
            get => _centralAngleResult;
            set => SetField(ref _centralAngleResult, value);
        }

        private double _aspectRatioResult;
        public double AspectRatioResult
        {
            get => _aspectRatioResult;
            set => SetField(ref _aspectRatioResult, value);
        }

        // switch to circular arc operation
        public void SelectCircularArc()
        {
            CurrentMethod = CurvatureMethod.CircularArc;
            CurrentStep = 0;
            ResetDrawingState();
        }

        private List<UIElement> ProcessCircArcClick(Vector2 mousePos)
        {
            List<UIElement> outputElements = new List<UIElement>();
            ClearElementsToRemove();

            switch (CurrentStep)
            {
                case 0: // Start the first chord

                    StartChord(mousePos, outputElements);
                    break;

                case 1: // End the first chord and start the bisector line 

                    FinishChord(mousePos, outputElements);
                    StartBisector(mousePos, outputElements);
                    break;

                case 2: // Send Bisector line, calculate all remaining POIs, and calculate the final results.

                    // finish bisector
                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    //midpoints for those lines
                    ACMid = (PointA + PointC) * 0.5;
                    BCMid = (PointB + PointC) * 0.5;

                    //generate the orthogonal lines, and find their intersection point

                    Vector2 Ray13 = (new Vector3(PointA.X, PointA.Y, 1) ^ new Vector3(PointC.X, PointC.Y, 1)).ToVector2();
                    Ray13.Normalize();

                    Vector2 Ray23 = (new Vector3(PointB.X, PointB.Y, 1) ^ new Vector3(PointC.X, PointC.Y, 1)).ToVector2();
                    Ray23.Normalize();

                    double dx = BCMid.X - ACMid.X;
                    double dy = BCMid.Y - ACMid.Y;
                    double det = Ray23 ^ Ray13;

                    if (Math.Abs(det) <= 0.00001)
                    {
                        //don't allow 0 height, just ignore the click and try again
                        break;
                    }

                    double u = (dy * Ray23.X - dx * Ray23.Y) / det;

                    Vector2 offset = Ray13 * u;

                    Intersection = ACMid + offset;

                    double radius = (PointA - Intersection).Magnitude();
                    var circularArc = MakeCircularArc(Intersection, PointA, PointB, radius);

                    // add theta label at the intersection point
                    var thetaLabel = MakeThetaLabel(Intersection);

                    outputElements.Add(circularArc);
                    CurrentOperation.Add(circularArc);
                    outputElements.Add(thetaLabel);
                    CurrentOperation.Add(thetaLabel);

                    CurrentUILine = null;

                    var line5 = MakeLine(PointA, Intersection);
                    var line6 = MakeLine(PointB, Intersection);

                    outputElements.Add(line5);
                    outputElements.Add(line6);

                    CurrentOperation.Add(line5);
                    CurrentOperation.Add(line6);

                    CalculateCircularArcResults();

                    CurrentStep++;

                    // add to history
                    CommitOperation(new CircularArcOperation
                    {
                        OperationKind = "Circular Arc",
                        SourceMode = this,
                        Elements = new List<UIElement>(CurrentOperation),
                        CentralAngle = CentralAngleResult,
                        AspectRatio = AspectRatioResult,
                        ChordArcRatio = ChordArcRatioResult
                    });

                    CurrentOperation.Clear();

                    break;
                case 3:
                    ResetDrawingState();
                    // reuse this click as the first step
                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = MakeLine(mousePos, mousePos);
                    outputElements.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep = 1;
                    break;
            }

            return outputElements;
        }

        // method to place theta label on central angle
        private TextBlock MakeThetaLabel(Vector2 position)
        {
            TextBlock textBlock = new TextBlock();
            textBlock.Text = "\u03B8"; // Unicode for Greek lowercase Theta
            textBlock.Foreground = this.LineColor;
            textBlock.FontSize = 22;
            textBlock.FontWeight = FontWeights.Bold;
            Canvas.SetLeft(textBlock, position.X - 7);
            Canvas.SetTop(textBlock, position.Y - 30);
            textBlock.TextAlignment = TextAlignment.Center;

            return textBlock;
        }

        private Path MakeCircularArc(Vector2 center, Vector2 start, Vector2 end, double radius)
        {
            // SweepDirection 
            double crossProduct = (PointC.X - start.X) * (end.Y - start.Y) - (PointC.Y - start.Y) * (end.X - start.X);
            SweepDirection direction = crossProduct > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

            // A 3-point arc is >180 degrees if the center point lies inside triangle ABC
            bool isLargeArc = GeometryCalculations.IsPointInTriangle(center, start, end, PointC);

            var figure = new PathFigure();
            figure.StartPoint = new Point(start.X, start.Y);

            var arc = new ArcSegment
            {
                Point = new Point(end.X, end.Y),
                Size = new Size(radius, radius),
                RotationAngle = 0,
                IsLargeArc = isLargeArc,
                SweepDirection = direction,
                IsStroked = true
            };

            figure.Segments.Add(arc);

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Path
            {
                Stroke = this.LineColor,
                StrokeThickness = 2,
                Data = geometry
            };
        }

        // function to calculate results
        private void CalculateCircularArcResults()
        {
            CentralAngleResult = GeometryCalculations.CentralAngle(PointA, PointB, PointC, Intersection);
            double chordLength = (PointB - PointA).Magnitude();
            double bisectorLength = (PointC - Midpoint).Magnitude();
            AspectRatioResult = GeometryCalculations.CircularArcAspectRatio(chordLength, bisectorLength);
            double radius = (PointA - Intersection).Magnitude();
            double arcLength = GeometryCalculations.CircularArcLength(radius, CentralAngleResult);
            ChordArcRatioResult = GeometryCalculations.ChordArcRatio(chordLength, arcLength);
        }
        #endregion

        #region Parabolic Arc Section
        //-----PARABOLIC ARCS-----//

        private double ParabolaA;
        private double ParabolaB;
        private double ParabolaC;

        private string _xyFunctionResult;
        public string XYFunctionResult
        {
            get => _xyFunctionResult;
            set => SetField(ref _xyFunctionResult, value);
        }

        private double _pChordArcRatioResult;
        public double PChordArcRatioResult
        {
            get => _pChordArcRatioResult;
            set => SetField(ref _pChordArcRatioResult, value);
        }

        private double _riseSpanRatioResult;
        public double RiseSpanRatioResult
        {
            get => _riseSpanRatioResult;
            set => SetField(ref _riseSpanRatioResult, value);
        }

        private double _vertexCurvatureResult;
        public double VertexCurvatureResult
        {
            get => _vertexCurvatureResult;
            set => SetField(ref _vertexCurvatureResult, value);
        }

        // switch to parabolic arc operation
        public void SelectParabolicArc()
        {
            CurrentMethod = CurvatureMethod.ParabolicArc;
            CurrentStep = 0;
            ResetDrawingState();
        }

        private List<UIElement> ProcessParArcClick(Vector2 mousePos)
        {
            List<UIElement> outputElements = new List<UIElement>();
            ClearElementsToRemove();
            switch (CurrentStep)
            {
                case 0: // Start the first chord

                    StartChord(mousePos, outputElements);
                    break;

                case 1: // End the first chord and start the bisector line 

                    FinishChord(mousePos, outputElements);
                    StartBisector(mousePos, outputElements);
                    break;

                case 2: // finish Bisector line, calculate all remaining POIs, and calculate the final results.

                    // finish bisector
                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    // draw parabolic arc through points A, B, and C
                    var parabola = MakeParabolicArc(PointA, PointB, PointC);

                    outputElements.Add(parabola);
                    CurrentOperation.Add(parabola);

                    // calculate results
                    CalculateParabolicArcResults();

                    CurrentStep++;

                    // add to history
                    CommitOperation(new ParabolaOperation
                    {
                        OperationKind = "Parabolic Arc",
                        SourceMode = this,
                        Elements = new List<UIElement>(CurrentOperation),
                        XYFunction = XYFunctionResult,
                        RiseSpanRatio = RiseSpanRatioResult,
                        PChordArcRatio = PChordArcRatioResult,
                        VertexCurvature = VertexCurvatureResult
                    });

                    CurrentOperation.Clear();

                    break;
                case 3:
                    ResetDrawingState();
                    // reuse this click as the first step
                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = MakeLine(mousePos, mousePos);
                    outputElements.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep = 1;
                    break;
            }

            return outputElements;
        }

        private Path MakeParabolicArc(Vector2 pointA, Vector2 pointB, Vector2 pointC)
        {
            if (!GeometryCalculations.BuildLocalBasis(pointA, pointB, out Vector2 xAxis, out Vector2 yAxis, out double chordLength))
                return null;

            Vector2 cDelta = pointC - pointA;
            Vector2 cL = new Vector2(
                (cDelta | xAxis) / chordLength,
                (cDelta | yAxis) / chordLength
            );

            (ParabolaA, ParabolaB, ParabolaC) = GeometryCalculations.SolveParabola(0, 0, 1, 0, cL.X, cL.Y);

            if (ParabolaA == 0 && ParabolaB == 0 && ParabolaC == 0)
                return null;

            List<Vector2> worldPoints = SampleParabolaWorldPoints(pointA, xAxis, yAxis, chordLength, 64);

            PathFigure figure = new PathFigure { StartPoint = new Point(worldPoints[0].X, worldPoints[0].Y), IsClosed = false };
            PolyLineSegment segment = new PolyLineSegment();
            for (int i = 1; i < worldPoints.Count; i++)
                segment.Points.Add(new Point(worldPoints[i].X, worldPoints[i].Y));
            figure.Segments.Add(segment);

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Path { Data = geometry, Stroke = this.LineColor, StrokeThickness = 2 };
        }

        private List<Vector2> SampleParabolaWorldPoints(Vector2 origin, Vector2 xAxis, Vector2 yAxis, double chordLength, int count)
        {
            var points = new List<Vector2>(count + 1);
            for (int i = 0; i <= count; i++)
            {
                double t = (double)i / count;
                double yNorm = ParabolaA * t * t + ParabolaB * t + ParabolaC;
                points.Add(origin + new Vector2(
                    xAxis.X * t * chordLength + yAxis.X * yNorm * chordLength,
                    xAxis.Y * t * chordLength + yAxis.Y * yNorm * chordLength));
            }
            return points;
        }

        // function to calculate results
        private void CalculateParabolicArcResults()
        {
            double pChordLength = (PointB - PointA).Magnitude();
            double rise = (PointC - Midpoint).Magnitude();

            RiseSpanRatioResult = GeometryCalculations.RiseSpanRatio(rise, pChordLength);
            VertexCurvatureResult = GeometryCalculations.ParabolaVertexCurvature(ParabolaA);

            XYFunctionResult = $"y = {ParabolaA:F3}x² + {ParabolaB:F3}x + {ParabolaC:F3}";

            if (!GeometryCalculations.BuildLocalBasis(PointA, PointB, out Vector2 xAxis, out Vector2 yAxis, out double chordLength))
                return;
            List<Vector2> worldPoints = SampleParabolaWorldPoints(PointA, xAxis, yAxis, chordLength, 64);
            double arcLength = GeometryCalculations.ArcLength(worldPoints);

            PChordArcRatioResult = GeometryCalculations.ChordArcRatio(pChordLength, arcLength);
        }
        #endregion
        #endregion

        #region n-point spline section
        //-----N-POINT SPLINE SECTION-----//
        // Spline mode fields
        private List<Vector2> _splinePoints = new List<Vector2>();
        private UIElement _splinePreview = null;
        private List<UIElement> _splineCurrentOperation = new List<UIElement>();

        private SplineAlgorithm _splineAlgorithm = SplineAlgorithm.CatmullRom;
        public SplineAlgorithm CurrentSplineAlgorithm
        {
            get => _splineAlgorithm;
            set
            {
                if (!SetField(ref _splineAlgorithm, value)) return;
                OnPropertyChanged(nameof(CurrentSplineAlgorithm));
                OnPropertyChanged(nameof(IsCatmullRomSelected));
                OnPropertyChanged(nameof(IsBezierSelected));
                OnTipChanged?.Invoke();
                ResetDrawingState(); // switching algorithm mid-draw starts fresh
            }
        }

        public bool IsCatmullRomSelected
        {
            get => _splineAlgorithm == SplineAlgorithm.CatmullRom;
            set { if (value) CurrentSplineAlgorithm = SplineAlgorithm.CatmullRom; }
        }

        public bool IsBezierSelected
        {
            get => _splineAlgorithm == SplineAlgorithm.Bezier;
            set { if (value) CurrentSplineAlgorithm = SplineAlgorithm.Bezier; }
        }

        // Bindable results of curvature calculations
        // private/public pair used to handle propagation of results to UI bindings
        private double _turningAngleArcRatioResult;
        public double TurningAngleArcRatioResult
        {
            get => _turningAngleArcRatioResult;
            set
            {
                _turningAngleArcRatioResult = value;
                OnPropertyChanged(nameof(TurningAngleArcRatioResult));
            }
        }

        private double _sumTurningAnglesResult;
        public double SumTurningAnglesResult
        {
            get => _sumTurningAnglesResult;
            set { _sumTurningAnglesResult = value; OnPropertyChanged(nameof(SumTurningAnglesResult)); }
        }

        private double _meanTurningAngleResult;
        public double MeanTurningAngleResult
        {
            get => _meanTurningAngleResult;
            set { _meanTurningAngleResult = value; OnPropertyChanged(nameof(MeanTurningAngleResult)); }
        }

        private double _varianceTurningAnglesResult;
        public double VarianceTurningAnglesResult
        {
            get => _varianceTurningAnglesResult;
            set { _varianceTurningAnglesResult = value; OnPropertyChanged(nameof(VarianceTurningAnglesResult)); }
        }

        private double _sChordArcRatioResult;
        public double SChordArcRatioResult
        {
            get => _sChordArcRatioResult;
            set
            {
                _sChordArcRatioResult = value;
                OnPropertyChanged(nameof(SChordArcRatioResult));
            }
        }

        private string _splineLengthScaledResult = "Scale to measure";
        public string SplineLengthScaledResult
        {
            get => _splineLengthScaledResult;
            set => SetField(ref _splineLengthScaledResult, value);
        }

        private Ellipse MakeDot(Vector2 pos)
        {
            Ellipse dot = new Ellipse();
            dot.Fill = this.LineColor;
            dot.Width = 8;
            dot.Height = 8;
            Canvas.SetLeft(dot, pos.X - 4);
            Canvas.SetTop(dot, pos.Y - 4);
            return dot;
        }

        private List<UIElement> ProcessSplineClick(Vector2 mousePos)
        {
            List<UIElement> output = new List<UIElement>();
            ClearElementsToRemove();

            // add the point
            _splinePoints.Add(mousePos);

            // draw a visible dot marker
            var dot = MakeDot(mousePos);
            _splineCurrentOperation.Add(dot);
            output.Add(dot);

            // once we have at least 2 points, update the spline preview
            if (_splinePoints.Count >= 2)
            {
                // remove old preview from operation list if it exists
                if (_splinePreview != null)
                {
                    AddElementsToRemove(_splinePreview);
                    _splineCurrentOperation.Remove(_splinePreview);
                }

                // generate new spline through all current points
                _splinePreview = _splineAlgorithm == SplineAlgorithm.Bezier
                    ? MakeSchneiderBezierPath(_splinePoints)
                    : MakeCatmullRomPath(_splinePoints);
                _splineCurrentOperation.Add(_splinePreview);
                output.Add(_splinePreview);
            }

            return output;
        }

        // switch to n-point spline operation
        public void SelectNPointSpline()
        {
            CurrentMethod = CurvatureMethod.NPointSpline;
            _splineAlgorithm = SplineAlgorithm.CatmullRom;
            OnPropertyChanged(nameof(IsCatmullRomSelected));
            OnPropertyChanged(nameof(IsBezierSelected));
            ResetDrawingState();
        }

        public override List<UIElement> ProcessDoubleClick(Vector2 mousePos)
        {
            if (CurrentMethod != CurvatureMethod.NPointSpline)
                return new List<UIElement>();

            if (FindTurningAngleMode)
                return new List<UIElement>();   // double-click is a no-op while probing

            if (_splinePoints.Count < 3)
            {
                // not enough points, reset and try again
                ResetDrawingState();
                return new List<UIElement>();
            }

        // calculate results
        List<Vector2> splinePointsDense = _splineAlgorithm == SplineAlgorithm.Bezier
                ? SplineFitting.GetSchneiderBezierPoints(_splinePoints, 50)
                : SplineFitting.GetCatmullRomPoints(_splinePoints, 50);
            _lastSplineDense = splinePointsDense;   // keep for the Find-turning-angle tool
            double splineLength = GeometryCalculations.ArcLength(splinePointsDense);
            SplineLengthScaledResult = Scale != null && Scale.IsCalibrated
                ? $"{Scale.ToUnits(splineLength):F2} {Scale.Unit}"
                : "Scale to measure";
            TurningAngleArcRatioResult = Math.Round(GeometryCalculations.TurningAnglePerUnitLength(splinePointsDense), 2);
            SChordArcRatioResult = Math.Round(CalculateSChordArcRatio(splinePointsDense, _splinePoints), 2);
            SumTurningAnglesResult = GeometryCalculations.SumTurningAnglesOpen(splinePointsDense);
            MeanTurningAngleResult = GeometryCalculations.MeanTurningAngleOpen(splinePointsDense);
            VarianceTurningAnglesResult = GeometryCalculations.VarianceTurningAnglesOpen(splinePointsDense);

            // store in history
            CommitOperation(new SplineOperation
            {
                OperationKind = _splineAlgorithm == SplineAlgorithm.Bezier
                    ? "n-Point Bezier Spline"
                    : "n-Point Catmull-Rom Spline",
                SourceMode = this,
                Elements = new List<UIElement>(_splineCurrentOperation),
                TurningAngleArcRatio = TurningAngleArcRatioResult,
                SChordArcRatio = SChordArcRatioResult,
                SumTurningAngles = SumTurningAnglesResult,
                MeanTurningAngle = MeanTurningAngleResult,
                VarianceTurningAngles = VarianceTurningAnglesResult
            });

            var output = new List<UIElement>(_splineCurrentOperation);

            // reset drawing state for next spline
            _splinePoints.Clear();
            _splinePreview = null;
            _splineCurrentOperation.Clear();

            return output;
        }

        // ---- Find turning angle (probe the most recent spline) ----

        // Dense samples of the most recently finished spline, retained so the
        // "Find turning angle" tool can snap to it and measure local bend.
        private List<Vector2> _lastSplineDense = null;

        // Index on _lastSplineDense the oval is currently centred on.
        private int _turningIndex = 0;

        // Half-width of the measured section, in dense-polyline samples. The angle is
        // measured between the curve direction this many samples before and after the
        // centre, and the oval is drawn to enclose exactly that span.
        private int _turningAngleWindow = 5;
        public int TurningAngleWindow
        {
            get => _turningAngleWindow;
            set
            {
                int clamped = Math.Max(1, Math.Min(50, value));
                if (!SetField(ref _turningAngleWindow, clamped)) return;
                if (FindTurningAngleMode && _lastSplineDense != null && _lastSplineDense.Count >= 3)
                {
                    UpdateWindowOval(_turningIndex);
                    FindTurningAngleDisplay =
                        $"{LocalTurningAngle(_lastSplineDense, _turningIndex, _turningAngleWindow):F2}\u00B0";
                }
            }
        }

        private string _findTurningAngleDisplay = "";
        public string FindTurningAngleDisplay
        {
            get => _findTurningAngleDisplay;
            set => SetField(ref _findTurningAngleDisplay, value);
        }

        private bool _findTurningAngleMode = false;
        public bool FindTurningAngleMode
        {
            get => _findTurningAngleMode;
            set
            {
                if (!SetField(ref _findTurningAngleMode, value)) return;
                if (value) BeginFindTurningAngle();
                else OnTurningWindowClear?.Invoke(_windowOval);
                OnTipChanged?.Invoke();
            }
        }

        // MainWindow wires these: Ready adds the oval to the canvas (and hides the dot
        // cursor); Clear removes it (and restores the cursor).
        public Action<UIElement> OnTurningWindowReady;
        public Action<UIElement> OnTurningWindowClear;

        // The oriented oval that wraps the measured section of the spline.
        private Ellipse _windowOval;
        private System.Windows.Media.RotateTransform _windowOvalRotate;

        private Ellipse EnsureWindowOval()
        {
            if (_windowOval == null)
            {
                _windowOvalRotate = new System.Windows.Media.RotateTransform(0);
                _windowOval = new Ellipse
                {
                    Stroke = Brushes.LightBlue,
                    StrokeThickness = 2,
                    Fill = null,
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = _windowOvalRotate
                };
            }
            return _windowOval;
        }

        private void BeginFindTurningAngle()
        {
            if (_lastSplineDense == null || _lastSplineDense.Count < 3) return;
            OnTurningWindowReady?.Invoke(EnsureWindowOval());
            _turningIndex = 0;
            UpdateWindowOval(_turningIndex);
        }

        private List<UIElement> ProcessFindTurningAngleClick(Vector2 mousePos)
        {
            ClearElementsToRemove();
            if (_lastSplineDense == null || _lastSplineDense.Count < 3)
                return new List<UIElement>();

            if (!TryProjectOntoSpline(mousePos, out _, out int idx))
                return new List<UIElement>();

            _turningIndex = idx;
            UpdateWindowOval(idx);
            FindTurningAngleDisplay =
                $"{LocalTurningAngle(_lastSplineDense, idx, _turningAngleWindow):F2}\u00B0";
            return new List<UIElement>();
        }

        // Projects `mouse` onto the dense spline polyline, returning the closest point on
        // the curve and the index of the nearest dense vertex (the section centre).
        private bool TryProjectOntoSpline(Vector2 mouse, out Vector2 onCurve, out int nearestIndex)
        {
            onCurve = mouse;
            nearestIndex = -1;
            var pts = _lastSplineDense;
            if (pts == null || pts.Count < 2) return false;

            double bestDist2 = double.MaxValue;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                Vector2 ab = b - a;
                double len2 = ab.X * ab.X + ab.Y * ab.Y;
                double t = len2 > 1e-9 ? ((mouse - a) | ab) / len2 : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                Vector2 proj = a + ab * t;
                double dx = mouse.X - proj.X, dy = mouse.Y - proj.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDist2)
                {
                    bestDist2 = d2;
                    onCurve = proj;
                    nearestIndex = (t < 0.5) ? i : i + 1;
                }
            }
            return nearestIndex >= 0;
        }

        // The sample span [i0, i1] used for both the angle and the oval, centred on
        // `index` with a half-width of `window` samples (clamped to the polyline ends).
        private static (int i0, int i1) TurningWindowBounds(int count, int index, int window)
        {
            int i0 = Math.Max(0, index - window);
            int i1 = Math.Min(count - 1, index + window);
            if (i0 == index) i0 = Math.Max(0, index - 1);
            if (i1 == index) i1 = Math.Min(count - 1, index + 1);
            return (i0, i1);
        }

        // Local turning angle (degrees): the bend between the curve direction at the
        // start and end of the measured span. Wider windows give a smoother, coarser reading.
        private static double LocalTurningAngle(List<Vector2> pts, int index, int window)
        {
            int n = pts.Count;
            if (n < 3) return 0;

            var (i0, i1) = TurningWindowBounds(n, index, window);
            Vector2 vIn = pts[index] - pts[i0];
            Vector2 vOut = pts[i1] - pts[index];
            if (vIn.Magnitude() < 1e-9 || vOut.Magnitude() < 1e-9) return 0;

            return Math.Round(Math.Abs(Vector2.AngleBetween(vIn, vOut)), 2);
        }

        // Sizes and orients the oval so it encloses the measured span pts[i0..i1]:
        // major axis along the span's chord, minor axis covering how far the arc bows
        // off that chord, rotated to match.
        private void UpdateWindowOval(int index)
        {
            if (_windowOval == null || _lastSplineDense == null) return;
            int n = _lastSplineDense.Count;
            if (n < 2) return;

            var (i0, i1) = TurningWindowBounds(n, index, _turningAngleWindow);
            Vector2 a = _lastSplineDense[i0];
            Vector2 b = _lastSplineDense[i1];
            Vector2 chord = b - a;
            double chordLen = chord.Magnitude();
            if (chordLen < 1e-6) return;

            Vector2 unit = new Vector2(chord.X / chordLen, chord.Y / chordLen);
            Vector2 normal = new Vector2(-unit.Y, unit.X);

            double bow = 0;
            for (int i = i0; i <= i1; i++)
            {
                double dev = Math.Abs((_lastSplineDense[i] - a) | normal);
                if (dev > bow) bow = dev;
            }

            const double pad = 14.0;
            double major = Math.Max(16.0, chordLen + pad * 2);
            double minor = Math.Max(16.0, bow * 2 + pad * 2);

            Vector2 center = (a + b) * 0.5;
            _windowOval.Width = major;
            _windowOval.Height = minor;
            Canvas.SetLeft(_windowOval, center.X - major / 2);
            Canvas.SetTop(_windowOval, center.Y - minor / 2);
            _windowOvalRotate.Angle = Math.Atan2(chord.Y, chord.X) * 180.0 / Math.PI;
        }

        // Builds a WPF Path from the dense Catmull-Rom samples produced by SplineFitting.
        private Path MakeCatmullRomPath(List<Vector2> controlPoints)
        {
            if (controlPoints.Count < 2) return null;

            var pts = SplineFitting.GetCatmullRomPoints(controlPoints, 20);
            if (pts.Count < 2) return null;

            var figure = new PathFigure { IsClosed = false, StartPoint = new Point(pts[0].X, pts[0].Y) };
            var polyline = new PolyLineSegment();
            for (int i = 1; i < pts.Count; i++)
                polyline.Points.Add(new Point(pts[i].X, pts[i].Y));
            figure.Segments.Add(polyline);

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return new Path { Stroke = this.LineColor, StrokeThickness = 2, Data = geometry };
        }

        // Builds a WPF Path from the cubic Bézier segments fitted by SplineFitting.
        private Path MakeSchneiderBezierPath(List<Vector2> controlPoints, double tolerance = 2.0)
        {
            if (controlPoints == null || controlPoints.Count < 2) return null;

            var segments = SplineFitting.FitSchneiderBezier(controlPoints, tolerance);
            if (segments.Count == 0) return null;

            var figure = new PathFigure
            {
                StartPoint = new Point(segments[0].P0.X, segments[0].P0.Y),
                IsClosed = false
            };

            var pathSegments = new PathSegmentCollection();
            foreach (var seg in segments)
            {
                pathSegments.Add(new BezierSegment(
                    new Point(seg.P1.X, seg.P1.Y),
                    new Point(seg.P2.X, seg.P2.Y),
                    new Point(seg.P3.X, seg.P3.Y),
                    true));
            }

            figure.Segments = pathSegments;
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return new Path { Stroke = this.LineColor, StrokeThickness = 2, Data = geometry };
        }
        #endregion

        #region results and tips
        private double CalculateSChordArcRatio(List<Vector2> densePoints, List<Vector2> controlPoints)
        {
            double arcLength = GeometryCalculations.ArcLength(densePoints);
            double chordLength = (controlPoints[controlPoints.Count - 1] - controlPoints[0]).Magnitude();
            return GeometryCalculations.ArcChordRatio(arcLength, chordLength);
        }

        public override string[] GetTips()
        {
            if (IsCircularArcSelected)
                return new[]
                {
            "💡 Approximate a curve as the arc of a circle. First click each endpoint of the arc, then click its midpoint.",
            "💡 Central angle measures the angle between the radii that define the circular arc. Higher angles correspond to larger arcs.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Rise/span ratio measures how tall an arc is relative to its width.",
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
            "💡 Zoom in or out using the scroll wheel.",
            "💡 Toggle tip visibility in the View menu."
        };
            if (IsParabolicArcSelected)
                return new[]
                {
            "💡 Approximate a curve as a parabolic arc. First click each endpoint of the arc, then click its midpoint.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Rise/span ratio measures how tall an arc is relative to its width.",
            "💡 Vertex curvature describes sharpness of the curve at its peak. This is the 'm' of 'y=mx^2'.",
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
            "💡 Zoom in or out using the scroll wheel.",
            "💡 Toggle tip visibility in the View menu."
        };
            if (IsNPointSplineSelected)
                return IsCatmullRomSelected
                    ? new[]
                {
                    "💡 Draw a curve of any shape using any number of points. Double click to finish drawing.",
                    "💡 Catmull-Rom splines use local smoothing and must pass through every clicked point. This operation draws a centripetal Catmull-Rom spline.",
                    "💡 Bézier splines use global smoothing and may not pass through every clicked point. Points are used to approximate a smooth curve.",
                    "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
                    "💡 Turn.Angles/Length (Turning angle - spline length ratio) measures how sharply the curve bends, on average, along its length.",
                    "💡 Sum Turn. Angles measures the total amount of directional change along the spline. This is sensitive to scale.",
                    "💡 Mean Turn. Angles measures the average degree of directional change along the spline.",
                    "💡 Turn. Angle Var. (Turning Angle Variance) measures how consistent or uneven curvature of the spline is.",
                    "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
                    "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Toggle tip visibility in the View menu."
                }
            : new[]
                {
                    "💡 Bézier splines use global smoothing and may not pass through every clicked point. Points are used to approximate a smooth curve.",
                    "💡 This operation uses Schneider's Bézier fitting to convert points into one or more smooth cubic Bézier segments.",
                    "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
                    "💡 Turn.Angles/Length (Turning angle - spline length ratio) measures how sharply the curve bends, on average, along its length.",
                    "💡 Sum Turn. Angles measures the total amount of directional change along the spline. This is sensitive to scale.",
                    "💡 Mean Turn. Angles measures the average degree of directional change along the spline.",
                    "💡 Turn. Angle Var. (Turning Angle Variance) measures how consistent or uneven curvature of the spline is.",
                    "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
                    "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Toggle tip visibility in the View menu."
                };
                return new[] 
            { 
                "💡 Select a curvature method to begin.",
                "💡 The user guide and software information can be found in the Help menu.",
                "💡 Press 'Ctrl+F' to open an image, or select 'Open Image' in the File menu.",
                "💡 Zoom in or out using the scroll wheel.",
                "💡 Toggle tip visibility in the View menu."
            };
        }
        #endregion

    }
}
