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
            public double AngleA { get; set; }
            public double AngleB { get; set; }
            public double AngleC { get; set; }
        }

        public TextBlock MakeLabel(string text, Vector2 pos)
        {
            TextBlock label = new TextBlock();
            label.Text = text;
            label.Foreground = Brushes.Red;
            label.FontSize = 28;
            label.FontWeight = FontWeights.Bold;

            // offset so it doesn't sit exactly on the point
            Canvas.SetLeft(label, pos.X + 5);
            Canvas.SetTop(label, pos.Y + 5);

            return label;
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
        private double _angleAResult;
        private double _angleBResult;
        private double _angleCResult;

        public double AngleAResult
        {
            get => _angleAResult;
            set
            {
                _angleAResult = value;
                OnPropertyChanged(nameof(AngleAResult));
            }
        }

        public double AngleBResult
        {
            get => _angleBResult;
            set
            {
                _angleBResult = value;
                OnPropertyChanged(nameof(AngleBResult));
            }
        }

        public double AngleCResult
        {
            get => _angleCResult;
            set
            {
                _angleCResult = value;
                OnPropertyChanged(nameof(AngleCResult));
            }
        }

        // ---------- TRIANGLE MODE ---------------- //

        public override void Reset()
        {
            base.Reset();
            AngleAResult = AngleBResult = AngleCResult = 0;
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            PointA = PointB = PointC = default;
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

                    output.Add(MakeLabel("A", PointA));
                    output.Add(MakeLabel("B", PointB));
                    output.Add(MakeLabel("C", PointC));

                    CurrentOperation.Add(ab);
                    CurrentOperation.Add(bc);
                    CurrentOperation.Add(ca);

                    // calculate the three angles

                    CalculateAndUpdateResults();

                    History.Add(new GetAngleOperation
                    {
                        Elements = new List<UIElement>(CurrentOperation),
                        AngleA = AngleAResult,
                        AngleB = AngleBResult,
                        AngleC = AngleCResult
                    });

                    CurrentOperation.Clear();
                    CurrentUILine = null;
                    CurrentStep++;
                    break;

                case 3:
                    Reset();
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

            AngleAResult = Math.Round(Math.Abs(Vector2.AngleBetween(AB, AC)), 2); // angle A
            AngleBResult = Math.Round(Math.Abs(Vector2.AngleBetween(BA, BC)), 2); // angle B
            AngleCResult = Math.Round(Math.Abs(Vector2.AngleBetween(CA, CB)), 2); // angle C
        }

        public void BindAngleResults(
            System.Windows.Controls.Label angleAOutput,
            System.Windows.Controls.Label angleBOutput,
            System.Windows.Controls.Label angleCOutput)
        {
            angleAOutput.DataContext = this;
            angleBOutput.DataContext = this;
            angleCOutput.DataContext = this;

            angleAOutput.SetBinding(Label.ContentProperty, new Binding(nameof(AngleAResult)));
            angleBOutput.SetBinding(Label.ContentProperty, new Binding(nameof(AngleBResult)));
            angleCOutput.SetBinding(Label.ContentProperty, new Binding(nameof(AngleCResult)));
        }
    }
}
