using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
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
        private CurvatureMethod _currentMethod { get; set; } = CurvatureMethod.None;
        public CurvatureMethod CurrentMethod
        {
            get => _currentMethod;
            set
            {
                _currentMethod = value;
                OnPropertyChanged(nameof(CurrentMethod));
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
                CurvatureMethod.NPointSpline => ProcessSplineClick(mousePos),
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
        }

        protected override void OnOperationUndone(WorkOperation operation)
        {
            if (operation is CircularArcOperation)
            {
                CentralAngleResult = 0;
                AspectRatioResult = 0;
                ChordArcRatioResult = 0;
            }
            else if (operation is ParabolaOperation)
            {
                XYFunctionResult = "";
                RiseSpanRatioResult = 0;
                PChordArcRatioResult = 0;
                VertexCurvatureResult = 0;
            }
            else if (operation is SplineOperation)
            {
                TurningAngleArcRatioResult = 0;
                SChordArcRatioResult = 0;
                SumTurningAnglesResult = 0;
                MeanTurningAngleResult = 0;
                VarianceTurningAnglesResult = 0;
            }
        }

        protected override void OnOperationRedone(WorkOperation operation)
        {
            if (operation is CircularArcOperation op)
            {
                CentralAngleResult = op.CentralAngle;
                AspectRatioResult = op.AspectRatio;
                ChordArcRatioResult = op.ChordArcRatio;
            }
            else if (operation is ParabolaOperation pop)
            {
                XYFunctionResult = pop.XYFunction;
                RiseSpanRatioResult = pop.RiseSpanRatio;
                PChordArcRatioResult = pop.PChordArcRatio;
                VertexCurvatureResult = pop.VertexCurvature;
            }

            else if (operation is SplineOperation sop)
            {
                TurningAngleArcRatioResult = sop.TurningAngleArcRatio;
                SChordArcRatioResult = sop.SChordArcRatio;
            }
        }

        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            _splinePoints.Clear();
            _splineDots.Clear();
            _splinePreview = null;
            _splineCurrentOperation.Clear();
        }

        public override void Reset()
        {
            base.Reset();
            ClearMetadata();
            ResetDrawingState();
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
            set
            {
                _chordArcRatioResult = value;
                OnPropertyChanged(nameof(ChordArcRatioResult));
            }
        }

        private double _centralAngleResult;
        public double CentralAngleResult
        {
            get { return _centralAngleResult; }
            set
            {
                _centralAngleResult = value;
                OnPropertyChanged(nameof(CentralAngleResult));
            }
        }

        private double _aspectRatioResult;
        public double AspectRatioResult
        {
            get { return _aspectRatioResult; }
            set
            {
                _aspectRatioResult = value;
                OnPropertyChanged(nameof(AspectRatioResult));
            }
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
            set { _xyFunctionResult = value; OnPropertyChanged(nameof(XYFunctionResult)); }
        }

        private double _pChordArcRatioResult;
        public double PChordArcRatioResult
        {
            get => _pChordArcRatioResult;
            set { _pChordArcRatioResult = value; OnPropertyChanged(nameof(PChordArcRatioResult)); }
        }

        private double _riseSpanRatioResult;
        public double RiseSpanRatioResult
        {
            get => _riseSpanRatioResult;
            set { _riseSpanRatioResult = value; OnPropertyChanged(nameof(RiseSpanRatioResult)); }
        }

        private double _vertexCurvatureResult;
        public double VertexCurvatureResult
        {
            get => _vertexCurvatureResult;
            set { _vertexCurvatureResult = value; OnPropertyChanged(nameof(VertexCurvatureResult)); }
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
        private List<UIElement> _splineDots = new List<UIElement>();
        private UIElement _splinePreview = null;
        private List<UIElement> _splineCurrentOperation = new List<UIElement>();

        private SplineAlgorithm _splineAlgorithm = SplineAlgorithm.CatmullRom;
        public SplineAlgorithm CurrentSplineAlgorithm
        {
            get => _splineAlgorithm;
            set
            {
                _splineAlgorithm = value;
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
            _splineDots.Add(dot);
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
            {
                return new List<UIElement>();
            }

            if (_splinePoints.Count < 3)
            {
                // not enough points, reset and try again
                ResetDrawingState();
                return new List<UIElement>();
            }

            // calculate results
            List<Vector2> splinePointsDense = _splineAlgorithm == SplineAlgorithm.Bezier
                ? GetSchneiderBezierPoints(_splinePoints, 50)
                : GetCatmullRomPoints(_splinePoints, 50);
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
            _splineDots.Clear();
            _splinePreview = null;
            _splineCurrentOperation.Clear();

            return output;
        }

        // generates a dense list of interpolated points along a Catmull-Rom spline
        private List<Vector2> GetCatmullRomPoints(List<Vector2> controlPoints, int samplesPerSegment)
        {
            List<Vector2> result = new List<Vector2>();

            // duplicate first and last points to create phantom endpoints
            List<Vector2> pts = new List<Vector2>();
            pts.Add(controlPoints[0]);
            pts.AddRange(controlPoints);
            pts.Add(controlPoints[controlPoints.Count - 1]);

            for (int i = 1; i < pts.Count - 2; i++)
            {
                for (int j = 0; j < samplesPerSegment; j++)
                {
                    double t = (double)j / samplesPerSegment;
                    result.Add(CatmullRom(pts[i - 1], pts[i], pts[i + 1], pts[i + 2], t));
                }
            }
            result.Add(controlPoints[controlPoints.Count - 1]);
            return result;
        }

        // Catmull-Rom interpolation between p1 and p2
        // Centripetal Catmull-Rom: evaluates the curve at parameter t, 
        // where t is between t1 and t2 in the knot sequence.
        // alpha = 0.5 gives centripetal parameterization.
        private Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, double t)
        {
            // Compute centripetal knot intervals (alpha = 0.5)
            double t0 = 0;
            double t1 = t0 + KnotInterval(p0, p1);
            double t2 = t1 + KnotInterval(p1, p2);
            double t3 = t2 + KnotInterval(p2, p3);

            // Remap t from [0,1] into [t1, t2]
            double s = t1 + t * (t2 - t1);

            // Barry-Goldman recursive evaluation
            Vector2 A1 = t1 > t0 ? p0 * ((t1 - s) / (t1 - t0)) + p1 * ((s - t0) / (t1 - t0)) : p1;
            Vector2 A2 = p1 * ((t2 - s) / (t2 - t1)) + p2 * ((s - t1) / (t2 - t1));
            Vector2 A3 = t3 > t2 ? p2 * ((t3 - s) / (t3 - t2)) + p3 * ((s - t2) / (t3 - t2)) : p2;

            Vector2 B1 = t2 > t0 ? A1 * ((t2 - s) / (t2 - t0)) + A2 * ((s - t0) / (t2 - t0)) : A2;
            Vector2 B2 = t3 > t1 ? A2 * ((t3 - s) / (t3 - t1)) + A3 * ((s - t1) / (t3 - t1)) : A2;

            return B1 * ((t2 - s) / (t2 - t1)) + B2 * ((s - t1) / (t2 - t1));
        }

        private double KnotInterval(Vector2 a, Vector2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            // alpha = 0.5: interval = distance^0.5
            return Math.Pow(dx * dx + dy * dy, 0.25); // (dist^2)^0.25 = dist^0.5
        }

        // builds a WPF Path from Catmull-Rom interpolated points
        private Path MakeCatmullRomPath(List<Vector2> controlPoints)
        {
            if (controlPoints.Count < 2) return null;

            // Phantom endpoints: duplicate first and last so every segment has a full 4-point neighborhood
            var pts = new List<Vector2>(controlPoints.Count + 2);
            pts.Add(controlPoints[0]);
            pts.AddRange(controlPoints);
            pts.Add(controlPoints[controlPoints.Count - 1]);

            const int samplesPerSegment = 20;
            var figure = new PathFigure { IsClosed = false };
            var polyline = new PolyLineSegment();

            for (int i = 1; i < pts.Count - 2; i++)
            {
                for (int j = 0; j < samplesPerSegment; j++)
                {
                    double t = (double)j / samplesPerSegment;
                    var pt = CatmullRom(pts[i - 1], pts[i], pts[i + 1], pts[i + 2], t);
                    if (i == 1 && j == 0)
                        figure.StartPoint = new Point(pt.X, pt.Y);
                    else
                        polyline.Points.Add(new Point(pt.X, pt.Y));
                }
            }
            // Add the final endpoint
            var last = controlPoints[controlPoints.Count - 1];
            polyline.Points.Add(new Point(last.X, last.Y));

            figure.Segments.Add(polyline);
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Path { Stroke = this.LineColor, StrokeThickness = 2, Data = geometry };
        }

        private struct CubicBezierSegmentData
        {
            public Vector2 P0;
            public Vector2 P1;
            public Vector2 P2;
            public Vector2 P3;

            public CubicBezierSegmentData(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
            {
                P0 = p0;
                P1 = p1;
                P2 = p2;
                P3 = p3;
            }
        }

        private List<CubicBezierSegmentData> FitSchneiderBezier(List<Vector2> points, double tolerance)
        {
            var result = new List<CubicBezierSegmentData>();
            if (points == null || points.Count < 2)
                return result;

            FitSchneiderBezierRecursive(points, 0, points.Count - 1, tolerance, result);
            return result;
        }

        private void FitSchneiderBezierRecursive(
    List<Vector2> points,
    int first,
    int last,
    double tolerance,
    List<CubicBezierSegmentData> output)
        {
            int count = last - first + 1;
            if (count < 2)
                return;

            if (count == 2)
            {
                Vector2 p0 = points[first];
                Vector2 p3 = points[last];
                Vector2 d = (p3 - p0) * (1.0 / 3.0);
                output.Add(new CubicBezierSegmentData(p0, p0 + d, p3 - d, p3));
                return;
            }

            Vector2 tHat1 = ComputeStartTangent(points, first, last);
            Vector2 tHat2 = ComputeEndTangent(points, first, last);

            var bez = GenerateBezier(points, first, last, tHat1, tHat2);
            int splitPoint = FindMaxErrorPoint(points, first, last, bez, out double maxError);

            if (maxError <= tolerance || splitPoint <= first + 1 || splitPoint >= last - 1)
            {
                output.Add(bez);
                return;
            }

            Vector2 tHatCenter = ComputeCenterTangent(points, splitPoint);

            FitSchneiderBezierRecursive(points, first, splitPoint, tolerance, output);
            FitSchneiderBezierRecursive(points, splitPoint, last, tolerance, output);
        }

        private Vector2 ComputeStartTangent(List<Vector2> points, int first, int last)
        {
            Vector2 t = points[first + 1] - points[first];
            if (t.Magnitude() < 1e-9 && last > first + 1)
                t = points[first + 2] - points[first];
            t.Normalize();
            return t;
        }

        private Vector2 ComputeEndTangent(List<Vector2> points, int first, int last)
        {
            Vector2 t = points[last - 1] - points[last];
            if (t.Magnitude() < 1e-9 && last > first + 1)
                t = points[last - 2] - points[last];
            t.Normalize();
            return t;
        }

        private Vector2 ComputeCenterTangent(List<Vector2> points, int splitPoint)
        {
            Vector2 t = points[splitPoint + 1] - points[splitPoint - 1];
            if (t.Magnitude() < 1e-9)
                return new Vector2(1, 0);
            t.Normalize();
            return t;
        }

        private CubicBezierSegmentData GenerateBezier(
    List<Vector2> points,
    int first,
    int last,
    Vector2 tHat1,
    Vector2 tHat2)
        {
            Vector2 p0 = points[first];
            Vector2 p3 = points[last];

            int nPts = last - first + 1;
            var u = ChordLengthParameterize(points, first, last);

            double c00 = 0, c01 = 0, c11 = 0;
            double x0 = 0, x1 = 0;

            for (int i = 0; i < nPts; i++)
            {
                double ui = u[i];
                double b0 = Bernstein0(ui);
                double b1 = Bernstein1(ui);
                double b2 = Bernstein2(ui);
                double b3 = Bernstein3(ui);

                Vector2 a1 = tHat1 * b1;
                Vector2 a2 = tHat2 * b2;

                Vector2 tmp = points[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));

                c00 += a1 | a1;
                c01 += a1 | a2;
                c11 += a2 | a2;

                x0 += a1 | tmp;
                x1 += a2 | tmp;
            }

            double det = c00 * c11 - c01 * c01;
            double alphaL, alphaR;

            if (Math.Abs(det) > 1e-12)
            {
                alphaL = (x0 * c11 - x1 * c01) / det;
                alphaR = (c00 * x1 - c01 * x0) / det;
            }
            else
            {
                double dist = (p3 - p0).Magnitude() / 3.0;
                alphaL = alphaR = dist;
            }

            double segLength = (p3 - p0).Magnitude();
            double epsilon = segLength * 1e-6;

            if (alphaL < epsilon || alphaR < epsilon)
            {
                double dist = segLength / 3.0;
                alphaL = alphaR = dist;
            }

            Vector2 p1 = p0 + tHat1 * alphaL;
            Vector2 p2 = p3 + tHat2 * alphaR;

            return new CubicBezierSegmentData(p0, p1, p2, p3);
        }

        private int FindMaxErrorPoint(
    List<Vector2> points,
    int first,
    int last,
    CubicBezierSegmentData bez,
    out double maxError)
        {
            maxError = -1;
            int splitPoint = (first + last) / 2;

            int samples = last - first + 1;
            for (int i = 1; i < samples - 1; i++)
            {
                double u = (double)i / (samples - 1);
                Vector2 curvePt = EvaluateCubicBezier(bez, u);
                double err = (points[first + i] - curvePt).Magnitude();

                if (err > maxError)
                {
                    maxError = err;
                    splitPoint = first + i;
                }
            }

            return splitPoint;
        }

        private Vector2 EvaluateCubicBezier(CubicBezierSegmentData bez, double t)
        {
            double mt = 1.0 - t;
            double b0 = mt * mt * mt;
            double b1 = 3 * mt * mt * t;
            double b2 = 3 * mt * t * t;
            double b3 = t * t * t;

            return bez.P0 * b0 + bez.P1 * b1 + bez.P2 * b2 + bez.P3 * b3;
        }

        private double[] ChordLengthParameterize(List<Vector2> points, int first, int last)
        {
            int n = last - first + 1;
            var u = new double[n];
            u[0] = 0;

            double total = 0;
            for (int i = first + 1; i <= last; i++)
                total += (points[i] - points[i - 1]).Magnitude();

            if (total < 1e-12)
            {
                for (int i = 1; i < n; i++)
                    u[i] = (double)i / (n - 1);
                return u;
            }

            double accum = 0;
            for (int i = first + 1; i <= last; i++)
            {
                accum += (points[i] - points[i - 1]).Magnitude();
                u[i - first] = accum / total;
            }

            return u;
        }

        private double Bernstein0(double t) => Math.Pow(1 - t, 3);
        private double Bernstein1(double t) => 3 * t * Math.Pow(1 - t, 2);
        private double Bernstein2(double t) => 3 * t * t * (1 - t);
        private double Bernstein3(double t) => t * t * t;

        private Path MakeSchneiderBezierPath(List<Vector2> controlPoints, double tolerance = 2.0)
        {
            if (controlPoints == null || controlPoints.Count < 2)
                return null;

            var segments = FitSchneiderBezier(controlPoints, tolerance);
            if (segments.Count == 0)
                return null;

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

            return new Path
            {
                Stroke = this.LineColor,
                StrokeThickness = 2,
                Data = geometry
            };
        }

        private List<Vector2> GetSchneiderBezierPoints(List<Vector2> controlPoints, int samplesPerSegment, double tolerance = 2.0)
        {
            var segments = FitSchneiderBezier(controlPoints, tolerance);
            var result = new List<Vector2>();

            foreach (var seg in segments)
            {
                for (int i = 0; i < samplesPerSegment; i++)
                {
                    double t = (double)i / samplesPerSegment;
                    result.Add(EvaluateCubicBezier(seg, t));
                }
            }

            if (segments.Count > 0)
                result.Add(segments[segments.Count - 1].P3);

            return result;
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
                if (IsCircularArcSelected)
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
