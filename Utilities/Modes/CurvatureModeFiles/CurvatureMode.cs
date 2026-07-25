using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
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

        // A probe click measures the existing spline and adds nothing, so the router
        // must not clear the workspace on it.
        public override bool IsProbeInteraction =>
            CurrentMethod == CurvatureMethod.NPointSpline && FindTurningAngleMode;

        public enum CurvatureMethod
        {
            None,
            CircularArc,
            ParabolicArc,
            NPointSpline
        }

        public enum SplineAlgorithm { CatmullRom, Bezier }

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

        private Line CurrentUILine = null;

        public void SelectCurvature(string option)
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

        public void SelectNone()
        {
            ExitFindTurningAngle();
            CurrentMethod = CurvatureMethod.None;
            ResetDrawingState();
        }

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            // Snap the cursor to the nearest point on the last spline while probing.
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
            _canvasSplineLength = 0;
            _hasCanvasSplineLength = false;
            RecomputeScaledResults();
        }

        public override void RefreshScalePlaceholders()
        {
            RecomputeScaledResults();
            OnPropertyChanged(nameof(AvgSplineLengthScaledResult));
        }

        // Canvas-space length of the displayed spline, kept so the scaled row can be
        // re-derived whenever the calibration changes or an undo restores a different
        // spline.
        private double _canvasSplineLength;
        private bool _hasCanvasSplineLength;

        private void RecomputeScaledResults()
        {
            SplineLengthScaledResult = FormatScaledLength(_canvasSplineLength, _hasCanvasSplineLength);
        }

        /// <summary>Restores the canvas-space length behind the scaled row.</summary>
        public void RestoreScaledMeasurements(double canvasLength)
        {
            _canvasSplineLength = canvasLength;
            _hasCanvasSplineLength = true;
            RecomputeScaledResults();
        }

        // Resets the in-progress drawing only. Does NOT exit FindTurningAngleMode:
        // that is toggled explicitly (see ExitFindTurningAngle).
        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            _splinePoints.Clear();
            _splinePreview = null;
        }

        // Leaves probe mode (removes the oval, clears the readout).
        private void ExitFindTurningAngle()
        {
            if (FindTurningAngleMode) FindTurningAngleMode = false;
        }

        public override void Reset()
        {
            base.Reset();
            _lastSplineDense = null;
            FindTurningAngleDisplay = "";
        }
        #endregion

        #region 3-Point Arc Section
        //-----THREE-POINT ARC SECTION-----//

        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 Midpoint;
        public Vector2 Orthogonal;
        public Vector2 PointC;
        public Vector2 Intersection;
        public Vector2 ACMid;
        public Vector2 BCMid;

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

            Vector3 p1 = new Vector3(PointA.X, PointA.Y, 1);
            Vector3 p2 = new Vector3(PointB.X, PointB.Y, 1);
            Orthogonal = (p1 ^ p2).ToVector2();
            Orthogonal.Normalize();

            CurrentStep++;
        }


        #region Circular Arc Section
        //-----CIRCULAR ARCS-----//

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

        public void SelectCircularArc()
        {
            ExitFindTurningAngle();
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

                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    ACMid = (PointA + PointC) * 0.5;
                    BCMid = (PointB + PointC) * 0.5;


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

                    var thetaLabel = MakeLabel("\u03B8", Intersection, 22, -7, -30);

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

                    CommitCurrentOperation(new CircularArcOperation
                    {
                        OperationKind = "Circular Arc",
                        CentralAngle = CentralAngleResult,
                        AspectRatio = AspectRatioResult,
                        ChordArcRatio = ChordArcRatioResult
                    });

                    break;
                case 3:
                    ResetDrawingState();
                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = MakeLine(mousePos, mousePos);
                    outputElements.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep = 1;
                    break;
            }

            return outputElements;
        }

        private Path MakeCircularArc(Vector2 center, Vector2 start, Vector2 end, double radius)
        {
            double crossProduct = (PointC.X - start.X) * (end.Y - start.Y) - (PointC.Y - start.Y) * (end.X - start.X);
            SweepDirection direction = crossProduct > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

            // Arc exceeds 180° when the centre lies inside triangle ABC.
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

        public void SelectParabolicArc()
        {
            ExitFindTurningAngle();
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

                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    var parabola = MakeParabolicArc(PointA, PointB, PointC);

                    outputElements.Add(parabola);
                    CurrentOperation.Add(parabola);

                    CalculateParabolicArcResults();

                    CurrentStep++;

                    CommitCurrentOperation(new ParabolaOperation
                    {
                        OperationKind = "Parabolic Arc",
                        XYFunction = XYFunctionResult,
                        RiseSpanRatio = RiseSpanRatioResult,
                        PChordArcRatio = PChordArcRatioResult,
                        VertexCurvature = VertexCurvatureResult
                    });

                    break;
                case 3:
                    ResetDrawingState();
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
        private List<Vector2> _splinePoints = new List<Vector2>();
        private UIElement _splinePreview = null;

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

        private string _splineLengthScaledResult = "Unscaled";
        public string SplineLengthScaledResult
        {
            get => _splineLengthScaledResult;
            set => SetField(ref _splineLengthScaledResult, value);
        }

        private List<UIElement> ProcessSplineClick(Vector2 mousePos)
        {
            List<UIElement> output = new List<UIElement>();
            ClearElementsToRemove();

            // New spline: drop the retained probe target (committed visuals stay).
            if (_splinePoints.Count == 0)
                _lastSplineDense = null;

            _splinePoints.Add(mousePos);

            var dot = MakeDot(mousePos);
            CurrentOperation.Add(dot);
            output.Add(dot);

            if (_splinePoints.Count >= 2)
            {
                if (_splinePreview != null)
                {
                    AddElementsToRemove(_splinePreview);
                    CurrentOperation.Remove(_splinePreview);
                }

                _splinePreview = _splineAlgorithm == SplineAlgorithm.Bezier
                    ? MakeSchneiderBezierPath(_splinePoints)
                    : MakeCatmullRomPath(_splinePoints);
                CurrentOperation.Add(_splinePreview);
                output.Add(_splinePreview);
            }

            return output;
        }

        public void SelectNPointSpline()
        {
            // Don't reset if a probe-ready spline exists: reaching the
            // Find-Turning-Angle control re-invokes this and must not discard it.
            bool alreadySplineWithFinished =
                CurrentMethod == CurvatureMethod.NPointSpline
                && _lastSplineDense != null && _lastSplineDense.Count >= 3;

            CurrentMethod = CurvatureMethod.NPointSpline;
            _splineAlgorithm = SplineAlgorithm.CatmullRom;
            OnPropertyChanged(nameof(IsCatmullRomSelected));
            OnPropertyChanged(nameof(IsBezierSelected));

            if (!alreadySplineWithFinished)
                ResetDrawingState();
        }

        // True when there's an in-progress spline ready to finalize with Enter.
        public bool CanFinalizeSpline =>
            CurrentMethod == CurvatureMethod.NPointSpline
            && !FindTurningAngleMode
            && _splinePoints.Count >= 3;

        // Commits the spline and returns its elements (Enter key, wired in MainWindow).
        public List<UIElement> FinalizeSpline()
        {
            if (CurrentMethod != CurvatureMethod.NPointSpline)
                return new List<UIElement>();

            if (FindTurningAngleMode)
                return new List<UIElement>();   // Enter is a no-op while probing

            if (_splinePoints.Count < 3)
                return new List<UIElement>();   // not enough points yet; keep what's there

            List<Vector2> splinePointsDense = _splineAlgorithm == SplineAlgorithm.Bezier
                    ? SplineFitting.GetSchneiderBezierPoints(_splinePoints, 50)
                    : SplineFitting.GetCatmullRomPoints(_splinePoints, 50);
            _lastSplineDense = splinePointsDense;   // keep for the Find-turning-angle tool
            double splineLength = GeometryCalculations.ArcLength(splinePointsDense);
            _canvasSplineLength = splineLength;
            _hasCanvasSplineLength = true;
            RecomputeScaledResults();
            TurningAngleArcRatioResult = Math.Round(GeometryCalculations.TurningAnglePerUnitLength(splinePointsDense), 1);
            SChordArcRatioResult = Math.Round(CalculateSChordArcRatio(splinePointsDense, _splinePoints), 1);

            // Captured before the commit, which empties the accumulator.
            var output = new List<UIElement>(CurrentOperation);

            CommitCurrentOperation(new SplineOperation
            {
                OperationKind = _splineAlgorithm == SplineAlgorithm.Bezier
                    ? "n-Point Bezier Spline"
                    : "n-Point Catmull-Rom Spline",
                TurningAngleArcRatio = TurningAngleArcRatioResult,
                SChordArcRatio = SChordArcRatioResult,
                SplineLengthPixels = splineLength
            });

            _splinePoints.Clear();
            _splinePreview = null;

            return output;
        }

        // ---- Find turning angle (probe the most recent spline) ----

        // Dense samples of the last spline, retained for the probe tool.
        private List<Vector2> _lastSplineDense = null;

        // Index on _lastSplineDense the oval is currently centred on.
        private int _turningIndex = 0;

        // Half-width of the probed section, in dense-polyline samples.
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
                        FormatTurningReadout(_lastSplineDense, _turningIndex, _turningAngleWindow);
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
                // No spline to probe: refuse and snap the CheckBox back.
                if (value && (_lastSplineDense == null || _lastSplineDense.Count < 3))
                {
                    OnPropertyChanged(nameof(FindTurningAngleMode));
                    return;
                }

                if (!SetField(ref _findTurningAngleMode, value)) return;
                if (value) BeginFindTurningAngle();
                else TurningWindowClear?.Invoke(_windowOval);
                OnTipChanged?.Invoke();
            }
        }

        // MainWindow wires these to add/remove the oval on the canvas.
        public event Action<UIElement> TurningWindowReady;
        public event Action<UIElement> TurningWindowClear;

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
            TurningWindowReady?.Invoke(EnsureWindowOval());
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
                FormatTurningReadout(_lastSplineDense, idx, _turningAngleWindow);
            return new List<UIElement>();
        }

        // Returns the closest point on the spline and its nearest dense-vertex index.
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

        // Clamped sample span [i0, i1] centred on index, half-width `window`.
        private static (int i0, int i1) TurningWindowBounds(int count, int index, int window)
        {
            int i0 = Math.Max(0, index - window);
            int i1 = Math.Min(count - 1, index + window);
            if (i0 == index) i0 = Math.Max(0, index - 1);
            if (i1 == index) i1 = Math.Min(count - 1, index + 1);
            return (i0, i1);
        }

        // Turning angle and arc length over the probed span; matches the committed
        // Turn/Length metric restricted to that span (caller divides).
        private static (double angleDeg, double arcLenPx) LocalTurningAngleArcLength(
            List<Vector2> pts, int index, int window)
        {
            int n = pts.Count;
            if (n < 3) return (0, 0);

            var (i0, i1) = TurningWindowBounds(n, index, window);
            if (i1 - i0 < 2) return (0, 0); // need at least one interior vertex

            double arc = 0;
            for (int k = i0 + 1; k <= i1; k++)
                arc += (pts[k] - pts[k - 1]).Magnitude();

            double totalTurning = 0;
            for (int k = i0 + 1; k <= i1 - 1; k++)
            {
                Vector2 seg1 = pts[k] - pts[k - 1];
                Vector2 seg2 = pts[k + 1] - pts[k];
                if (seg1.Magnitude() < 1e-5 || seg2.Magnitude() < 1e-5) continue;
                totalTurning += Math.Abs(Vector2.AngleBetween(seg1, seg2));
            }

            return (totalTurning, arc);
        }

        // Hover readout in °/px, matching the committed Turn/Length column's units.
        private string FormatTurningReadout(List<Vector2> pts, int index, int window)
        {
            var (angleDeg, arcLenPx) = LocalTurningAngleArcLength(pts, index, window);
            if (arcLenPx < 1e-9) return "";
            return $"{angleDeg / arcLenPx:F2}\u00B0/px";
        }

        // Sizes and rotates the oval to enclose the probed span.
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

        #region Operation averages
        // Live per-type averages from history. The parabola formula is excluded
        // (not a single number to average).

        private IEnumerable<CircularArcOperation> CircularArcOps => OperationsOfKind<CircularArcOperation>();
        private IEnumerable<ParabolaOperation> ParabolaOps => OperationsOfKind<ParabolaOperation>();
        private IEnumerable<SplineOperation> SplineOps => OperationsOfKind<SplineOperation>();

        // Circular arc
        public string AvgCentralAngleResult => FormatAverage(CircularArcOps.Select(o => o.CentralAngle));
        public string AvgChordArcRatioResult => FormatAverage(CircularArcOps.Select(o => o.ChordArcRatio));
        public string AvgAspectRatioResult => FormatAverage(CircularArcOps.Select(o => o.AspectRatio));

        // Parabolic arc (formula excluded)
        public string AvgPChordArcRatioResult => FormatAverage(ParabolaOps.Select(o => o.PChordArcRatio));
        public string AvgRiseSpanRatioResult => FormatAverage(ParabolaOps.Select(o => o.RiseSpanRatio));
        public string AvgVertexCurvatureResult => FormatAverage(ParabolaOps.Select(o => o.VertexCurvature));

        // n-point spline (Catmull-Rom and Bézier combined, matching n_spline)
        public string AvgTurningAngleArcRatioResult => FormatAverage(SplineOps.Select(o => o.TurningAngleArcRatio));
        public string AvgSChordArcRatioResult => FormatAverage(SplineOps.Select(o => o.SChordArcRatio));
        public string AvgSplineLengthScaledResult => FormatScaledLengthAverage(SplineOps.Select(o => o.SplineLengthPixels));

        protected override void RecomputeAverages()
        {
            OnPropertyChanged(nameof(AvgCentralAngleResult));
            OnPropertyChanged(nameof(AvgChordArcRatioResult));
            OnPropertyChanged(nameof(AvgAspectRatioResult));
            OnPropertyChanged(nameof(AvgPChordArcRatioResult));
            OnPropertyChanged(nameof(AvgRiseSpanRatioResult));
            OnPropertyChanged(nameof(AvgVertexCurvatureResult));
            OnPropertyChanged(nameof(AvgTurningAngleArcRatioResult));
            OnPropertyChanged(nameof(AvgSChordArcRatioResult));
            OnPropertyChanged(nameof(AvgSplineLengthScaledResult));
        }
        #endregion

        #region results and tips
        private double CalculateSChordArcRatio(List<Vector2> densePoints, List<Vector2> controlPoints)
        {
            double arcLength = GeometryCalculations.ArcLength(densePoints);
            double chordLength = (controlPoints[controlPoints.Count - 1] - controlPoints[0]).Magnitude();
            return GeometryCalculations.ArcChordRatio(arcLength, chordLength);
        }

        private static readonly string[] CircularArcTips = BuildTips(
            "💡 Approximate a curve as the arc of a circle. First click each endpoint of the arc, then click its midpoint.",
            "💡 Central angle measures the angle between the radii that define the circular arc. Higher angles correspond to larger arcs.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Rise/span ratio measures how tall an arc is relative to its width.");

        private static readonly string[] ParabolicArcTips = BuildTips(
            "💡 Approximate a curve as a parabolic arc. First click each endpoint of the arc, then click its midpoint.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Rise/span ratio measures how tall an arc is relative to its width.",
            "💡 Vertex curvature describes sharpness of the curve at its peak. This is the 'm' of 'y=mx^2'.");

        private static readonly string[] CatmullRomTips = BuildTips(
            "💡 Draw a curve of any shape using any number of points. Press 'Enter' to finish drawing.",
            "💡 Catmull-Rom splines use local smoothing and must pass through every clicked point. This operation draws a centripetal Catmull-Rom spline.",
            "💡 Bézier splines use global smoothing and may not pass through every clicked point. Points are used to approximate a smooth curve.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Turn/Length (Turning angle - spline length ratio) measures how sharply the curve bends, on average, along its length.");

        private static readonly string[] BezierTips = BuildTips(
            "💡 Bézier splines use global smoothing and may not pass through every clicked point. Points are used to approximate a smooth curve.",
            "💡 This operation uses Schneider's Bézier fitting to convert points into one or more smooth cubic Bézier segments.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Turn/Length (Turning angle - spline length ratio) measures how sharply the curve bends, on average, along its length.");

        private static readonly string[] NoMethodTips = BuildTips(
            "💡 Select a curvature method to begin.");

        public override string[] GetTips()
        {
            if (IsCircularArcSelected) return CircularArcTips;
            if (IsParabolicArcSelected) return ParabolicArcTips;
            if (IsNPointSplineSelected) return IsCatmullRomSelected ? CatmullRomTips : BezierTips;
            return NoMethodTips;
        }

        #endregion

    }
}