using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Controls;
using static DinoLino.Utilities.Modes.CurvatureMode;

namespace DinoLino.Utilities.Modes
{
    public class GetAngleMode : WorkMode
    { // Temporary to make sure things compile

        // Stores lines and angles
        public class GetAngleOperation
        {
            public List<UIElement> Elements { get; set; }
            public double Angle1 { get; set; }
            public double Angle2 { get; set; }
            public double Angle3 { get; set; }
        }

        //  storing undo/redo history
        private List<GetAngleOperation> History = new List<GetAngleOperation>();
        private List<GetAngleOperation> RedoStack = new List<GetAngleOperation>();

        // Tracking 3-click line groups 
        private List<UIElement> CurrentOperation = new ();

        // Current state of the triangle drawing mode
        public int CurrentStep = 0;

        // Current UI line to modify during mouse move
        public Line CurrentUILine = null;

        // All major POIs in generating angle
        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 PointC;

        // Bindable results of angle calculation

        // private/public pairs used to handle propagation of results to UI bindings
        private double _angle1Result;
        private double _angle2Result;
        private double _angle3Result;

        public double Angle1Result
        {
            get => _angle1Result;
            set
            {
                _angle1Result = value;
                OnPropertyChanged(nameof(Angle1Result));
            }
        }

        public double Angle2Result
        {
            get => _angle2Result;
            set
            {
                _angle2Result = value;
                OnPropertyChanged(nameof(Angle2Result));
            }
        }

        public double Angle3Result
        {
            get => _angle3Result;
            set
            {
                _angle3Result = value;
                OnPropertyChanged(nameof(Angle3Result));
            }
        }

        // ---------- TRIANGLE MODE ---------------- //

        public override void Reset()
        {
            base.Reset();
            Angle1Result = Angle2Result = Angle3Result = 0;
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
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

                    CurrentOperation.Add(ab);
                    CurrentOperation.Add(bc);
                    CurrentOperation.Add(ca);

                    // calculate the three angles

                    CalculateAndUpdateResults();

                    History.Add(new GetAngleOperation
                    {
                        Elements = new List<UIElement>(CurrentOperation),
                        Angle1 = Angle1Result,
                        Angle2 = Angle2Result,
                        Angle3 = Angle3Result
                    });

                    CurrentOperation.Clear();
                    CurrentStep++;
                    break;

                case 3:
                    Reset();
                    // reuse this click as the first step
                    PointA = mousePos;
                    CurrentUILine = MakeLine(PointA, PointA);
                    output.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep = 1;
                    break;
            }
            return output;
        }

        public Line MakeLine(Vector2 a, Vector2 b)
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
            Vector2 AB = PointB - PointA;
            Vector2 AC = PointC - PointA;

            Vector2 BA = PointA - PointB;
            Vector2 BC = PointC - PointB;

            Vector2 CA = PointA - PointC;
            Vector2 CB = PointB - PointC;

            // reject near-collinear points (not a triangle)
            double cross = (AB.X * AC.Y - AB.Y * AC.X);
            if (Math.Abs(cross) < 0.0001)
                return;

            Angle1Result = Math.Round(Math.Abs(Vector2.AngleBetween(AB, AC)), 2); // angle A
            Angle2Result = Math.Round(Math.Abs(Vector2.AngleBetween(BA, BC)), 2); // angle B
            Angle3Result = Math.Round(Math.Abs(Vector2.AngleBetween(CA, CB)), 2); // angle C
        }

        public void BindAngleResults(
            System.Windows.Controls.Label angle1Output,
            System.Windows.Controls.Label angle2Output,
            System.Windows.Controls.Label angle3Output)
        {
            angle1Output.DataContext = this;
            angle2Output.DataContext = this;
            angle3Output.DataContext = this;

            angle1Output.SetBinding(Label.ContentProperty, new Binding(nameof(Angle1Result)));
            angle2Output.SetBinding(Label.ContentProperty, new Binding(nameof(Angle2Result)));
            angle3Output.SetBinding(Label.ContentProperty, new Binding(nameof(Angle3Result)));
        }
    }
}
