using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;
using System.Xml.Serialization;
using System.Windows.Data;
using System.Windows.Input;

namespace DinoLino.Utilities.Modes
{

    public class CurvatureMode : WorkMode
    {
        
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


        // Bindable results of curvature calculation

        // private/public pair used to handle propogation of results to UI bindings
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


        // ---------- CURVATURE MODE ---------------- //

        public override void Reset()
        {
            base.Reset();
            AngleResult = 0;
            CurrentStep = 0;
            CurrentUILine = null;
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
            List<UIElement> outputElements = new List<UIElement>();
            switch (CurrentStep)
            {
                case 0: // Start the first chord

                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = MakeLine(mousePos, mousePos);
                    outputElements.Add(CurrentUILine);
                    CurrentStep++;
                    break;

                case 1: // End the first chord, find the midpoint, and start the bisector line 

                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    PointB = new Vector2(mousePos.X, mousePos.Y);
                    Midpoint = (PointA + PointB) * 0.5;
                    CurrentUILine = MakeLine(Midpoint, Midpoint);
                    outputElements.Add(CurrentUILine);

                    // make the orthoganal
                    Vector3 p1 = new Vector3(PointA.X, PointA.Y, 1);
                    Vector3 p2 = new Vector3(PointB.X, PointB.Y, 1);
                    Orthoganal = (p1 ^ p2).ToVector2();
                    Orthoganal.Normalize();

                    CurrentStep++;
                    break;

                case 2: // Send Bisector line, calculate all remaining POIs, and calculate the final results.

                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    outputElements.Add(MakeLine(PointA, PointC));
                    outputElements.Add(MakeLine(PointB, PointC));

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

                    outputElements.Add(MakeLine(ACMid, Intersection));
                    outputElements.Add(MakeLine(BCMid, Intersection));
                    outputElements.Add(MakeLine(PointA, Intersection));
                    outputElements.Add(MakeLine(PointB, Intersection));

                    CalculateAndUpdateResults();

                    CurrentStep++;

                    break;
            }
            return outputElements;
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

        public void CalculateAndUpdateResults()
        {
            Vector2 vA = Intersection - PointA;
            Vector2 vB = Intersection - PointB;

            AngleResult = Math.Abs(Vector2.AngleBetween(vA, vB));
        }

        public void BindCurvatureResults(Label angleOutput)
        {
            Binding angleBind = new Binding(nameof(AngleResult));
            angleOutput.SetBinding(Label.ContentProperty, angleBind);
            angleOutput.DataContext = this;
        }

    }



}
