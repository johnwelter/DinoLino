using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Serialization;

namespace DinoLino.Utilities.Modes
{
    public class CurvatureMode : WorkMode
    {
        // enum for toggling between curvature methods
        public enum CurvatureMethod
        {
            ThreePointArc,
            NPointSpline
        }
        public CurvatureMethod CurrentMethod { get; set; } = CurvatureMethod.ThreePointArc;

        // Tracking 3-click line groups 
        private List<UIElement> CurrentOperation = new List<UIElement>();
        
        // Spline mode fields
        private List<Vector2> _splinePoints = new List<Vector2>();
        private List<UIElement> _splineDots = new List<UIElement>();
        private Polyline _splinePreview = null;
        private List<UIElement> _splineCurrentOperation = new List<UIElement>();

        // Current state of the curvature drawing mode
        public int CurrentStep = 0;

        // Current UI line to modify during mouse move
        public Line CurrentUILine = null;

        // All major POIs in generating curvature
        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 Midpoint;
        public Vector2 Orthoganal;
        public Vector2 PointC;
        public Vector2 Intersection;

        public Vector2 ACMid;
        public Vector2 BCMid;


        // Bindable results of curvature calculations

        // private/public pair used to handle propagation of results to UI bindings
        private double _turningAngleResult;
        public double TurningAngleResult
        {
            get => _turningAngleResult;
            set
            {
                _turningAngleResult = value;
                OnPropertyChanged(nameof(TurningAngleResult));
            }
        }

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

        private double _angleResult;
        public double AngleResult
        {
            get { return _angleResult; }
            set
            {
                _angleResult = value;
                OnPropertyChanged(nameof(AngleResult));
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

        protected override void OnOperationUndone(WorkOperation operation)
        {
            if (operation is CurvatureOperation)
            {
                // Restore previous values, or reset if history is now empty
                if (History.Count > 0 && History.Last() is CurvatureOperation prev)
                {
                    AngleResult = prev.Angle;
                    AspectRatioResult = prev.AspectRatio;
                }
                else
                {
                    AngleResult = 0;
                    AspectRatioResult = 0;
                }
            }
            else if (operation is SplineOperation)
            {
                if (History.Count > 0 && History.Last() is SplineOperation prev)
                {
                    TurningAngleResult = prev.TurningAngle;
                    ChordArcRatioResult = prev.ChordArcRatio;
                }
                else
                {
                    TurningAngleResult = 0;
                    ChordArcRatioResult = 0;
                }
            }
        }

        protected override void OnOperationRedone(WorkOperation operation)
        {
            if (operation is CurvatureOperation op)
            {
                AngleResult = op.Angle;
                AspectRatioResult = op.AspectRatio;
            }
            else if (operation is SplineOperation sop)
            {
                TurningAngleResult = sop.TurningAngle;
                ChordArcRatioResult = sop.ChordArcRatio;
            }
        }

        // ---------- CURVATURE MODE ---------------- //

        public override void Reset()
        {
            base.Reset();
            AngleResult = 0;
            AspectRatioResult = 0;
            TurningAngleResult = 0;
            ChordArcRatioResult = 0;
            CurrentStep = 0;
            CurrentUILine = null;
            _splinePoints.Clear();
            _splineDots.Clear();
            _splinePreview = null;
            _splineCurrentOperation.Clear();
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

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
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
                    double newMag = Orthoganal | toMouse;

                    Vector2 newDist = Orthoganal * newMag;

                    CurrentUILine.X2 = Midpoint.X + newDist.X;
                    CurrentUILine.Y2 = Midpoint.Y + newDist.Y;
                    modifiedPos = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);
                    break;

            }
            return modifiedPos;
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            if (CurrentMethod == CurvatureMethod.NPointSpline)
                return ProcessSplineClick(mousePos);

            List<UIElement> outputElements = new List<UIElement>();
            switch (CurrentStep)
            {
                case 0: // Start the first chord

                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = MakeLine(mousePos, mousePos);
                    outputElements.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep++;
                    break;

                case 1: // End the first chord, find the midpoint, and start the bisector line 

                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    PointB = new Vector2(mousePos.X, mousePos.Y);
                    Midpoint = (PointA + PointB) * 0.5;
                    CurrentUILine = MakeLine(Midpoint, Midpoint);
                    outputElements.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);

                    // make the orthoganal
                    Vector3 p1 = new Vector3(PointA.X, PointA.Y, 1);
                    Vector3 p2 = new Vector3(PointB.X, PointB.Y, 1);
                    Orthoganal = (p1 ^ p2).ToVector2();
                    Orthoganal.Normalize();

                    CurrentStep++;
                    break;

                case 2: // Send Bisector line, calculate all remaining POIs, and calculate the final results.

                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    var line1 = MakeLine(PointA, PointC);
                    var line2 = MakeLine(PointB, PointC);

                    outputElements.Add(line1);
                    outputElements.Add(line2);

                    CurrentOperation.Add(line1);
                    CurrentOperation.Add(line2);

                    //midpoints for those lines
                    ACMid = (PointA + PointC) * 0.5;
                    BCMid = (PointB + PointC) * 0.5;

                    //generate the orthogonal lines, and find their intersection point

                    Vector2 Ray13 = (new Vector3(PointA.X, PointA.Y, 1) ^ new Vector3(PointC.X, PointC.Y, 1)).ToVector2();
                    Ray13.Normalize();

                    Vector2 Ray23 = (new Vector3(PointB.X, PointB.Y, 1) ^ new Vector3(PointC.X, PointC.Y, 1)).ToVector2();
                    Ray23.Normalize();

                    Vector2 diff = BCMid - ACMid;
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

                    CurrentUILine = null;

                    var line3 = MakeLine(ACMid, Intersection);
                    var line4 = MakeLine(BCMid, Intersection);
                    var line5 = MakeLine(PointA, Intersection);
                    var line6 = MakeLine(PointB, Intersection);

                    outputElements.Add(line3);
                    outputElements.Add(line4);
                    outputElements.Add(line5);
                    outputElements.Add(line6);

                    CurrentOperation.Add(line3);
                    CurrentOperation.Add(line4);
                    CurrentOperation.Add(line5);
                    CurrentOperation.Add(line6);

                    CalculateAndUpdateResults();

                    CurrentStep++;

                    // Clear redo history when drawing new shape
                    RedoStack.Clear();
                    History.Add(new CurvatureOperation
                    {
                        Elements = new List<UIElement>(CurrentOperation),
                        Angle = AngleResult,
                        AspectRatio = AspectRatioResult
                    });

                    UpdateUndoRedoState();

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

        private List<UIElement> ProcessSplineClick(Vector2 mousePos)
        {
            List<UIElement> output = new List<UIElement>();

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
                    _splineCurrentOperation.Remove(_splinePreview);

                // generate new spline through all current points
                _splinePreview = MakeCatmullRomPolyline(_splinePoints);
                _splineCurrentOperation.Add(_splinePreview);
                output.Add(_splinePreview);
            }

            return output;
        }

        public override List<UIElement> ProcessDoubleClick(Vector2 mousePos)
        {
            if (CurrentMethod != CurvatureMethod.NPointSpline)
                return new List<UIElement>();

            if (_splinePoints.Count < 3)
            {
                // not enough points, reset and try again
                ResetDrawingState();
                return new List<UIElement>();
            }

            // calculate results
            List<Vector2> splinePointsDense = GetCatmullRomPoints(_splinePoints, 50);
            TurningAngleResult = Math.Round(CalculateTurningAngle(splinePointsDense), 2);
            ChordArcRatioResult = Math.Round(CalculateChordArcRatio(splinePointsDense, _splinePoints), 2);

            // store in history
            RedoStack.Clear();
            History.Add(new SplineOperation
            {
                Elements = new List<UIElement>(_splineCurrentOperation),
                TurningAngle = TurningAngleResult,
                ChordArcRatio = ChordArcRatioResult
            });
            UpdateUndoRedoState();

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
        private Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;

            double x = 0.5 * ((2 * p1.X) +
                       (-p0.X + p2.X) * t +
                       (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                       (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);

            double y = 0.5 * ((2 * p1.Y) +
                       (-p0.Y + p2.Y) * t +
                       (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                       (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);

            return new Vector2(x, y);
        }

        // builds a WPF Polyline from Catmull-Rom interpolated points
        private Polyline MakeCatmullRomPolyline(List<Vector2> controlPoints)
        {
            List<Vector2> pts = GetCatmullRomPoints(controlPoints, 50);
            Polyline polyline = new Polyline();
            polyline.Stroke = Brushes.OrangeRed;
            polyline.StrokeThickness = 2;
            foreach (var pt in pts)
                polyline.Points.Add(new Point(pt.X, pt.Y));
            return polyline;
        }

        public Line MakeLine(Vector2 a,  Vector2 b)
        {
            Line L = new();
            L.Stroke = Brushes.OrangeRed;
            L.StrokeThickness = 2;
            L.X1 = a.X;
            L.Y1 = a.Y;
            L.X2 = b.X;
            L.Y2 = b.Y;
            return L;
        }

        public Ellipse MakeDot(Vector2 pos)
        {
            Ellipse dot = new Ellipse();
            dot.Fill = Brushes.OrangeRed;
            dot.Width = 8;
            dot.Height = 8;
            Canvas.SetLeft(dot, pos.X - 4);
            Canvas.SetTop(dot, pos.Y - 4);
            return dot;
        }

        private double CalculateTurningAngle(List<Vector2> points)
        {
            if (points.Count < 3) return 0;

            double totalTurning = 0;
            double totalLength = 0;

            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector2 seg1 = points[i] - points[i - 1];
                Vector2 seg2 = points[i + 1] - points[i];

                double len1 = seg1.Magnitude();
                double len2 = seg2.Magnitude();
                totalLength += len1;

                if (len1 < 0.00001 || len2 < 0.00001) continue;

                double angle = Math.Abs(Vector2.AngleBetween(seg1, seg2));
                totalTurning += angle;
            }

            // add last segment length
            if (points.Count >= 2)
            {
                Vector2 last = points[points.Count - 1] - points[points.Count - 2];
                totalLength += last.Magnitude();
            }

            return totalLength > 0.00001 ? totalTurning / totalLength : 0;
        }

        private double CalculateChordArcRatio(List<Vector2> densePoints, List<Vector2> controlPoints)
        {
            // arc length = sum of distances between dense interpolated points
            double arcLength = 0;
            for (int i = 1; i < densePoints.Count; i++)
            {
                Vector2 seg = densePoints[i] - densePoints[i - 1];
                arcLength += seg.Magnitude();
            }

            // chord length = straight line from first to last control point
            Vector2 chord = controlPoints[controlPoints.Count - 1] - controlPoints[0];
            double chordLength = chord.Magnitude();

            return chordLength > 0.00001 ? Math.Round(arcLength / chordLength, 2) : 0;
        }

        public void CalculateAndUpdateResults()
        {
            Vector2 vA = Intersection - PointA;
            Vector2 vB = Intersection - PointB;

            AngleResult = Math.Round(Math.Abs(Vector2.AngleBetween(vA, vB)), 2);

                    // -----------------------------
                    // Aspect Ratio calculation
                    // -----------------------------

                    double chordLength = (PointB - PointA).Magnitude();

                    // bisector = midpoint → C
                    double bisectorLength = (PointC - Midpoint).Magnitude();

                    AspectRatioResult = bisectorLength > 0.00001
                        ? Math.Round(chordLength / bisectorLength, 2)
                        : 0;

                }

                public void BindCurvatureResults(Label angleOutput, Label aspectRatioOutput, Label turningAngleOutput, Label chordArcRatioOutput)
                {
                    Binding angleBind = new Binding(nameof(AngleResult));
                    angleOutput.SetBinding(Label.ContentProperty, angleBind);
                    angleOutput.DataContext = this;

                    Binding ratioBind = new Binding(nameof(AspectRatioResult));
                    aspectRatioOutput.SetBinding(Label.ContentProperty, ratioBind);
                    aspectRatioOutput.DataContext = this;

                    Binding turningAngleBind = new Binding(nameof(TurningAngleResult));
                    turningAngleOutput.SetBinding(Label.ContentProperty, turningAngleBind);
                    turningAngleOutput.DataContext = this;

                    Binding chordArcRatioBind = new Binding(nameof(ChordArcRatioResult));
                    chordArcRatioOutput.SetBinding(Label.ContentProperty, chordArcRatioBind);
                    chordArcRatioOutput.DataContext = this;
                }

            }

        }
