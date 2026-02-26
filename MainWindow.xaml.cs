using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using System.IO;
using Microsoft.Win32;
using DinoLino.Utilities;
using DinoLino.DataTypes;

namespace DinoLino
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        public BitmapImage WorkingImage;

#region Curvature Mode Variables

        // Curvature Mode

        public Ellipse UI_DotCursor;
        
        public int CurrentPointIndex = 0;
        public Line CurrentUILine = null;
        
        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 Midpoint;
        public Vector2 Orthoganal;
        public Vector2 PointC;
        public Vector2 Intersection;

        public Vector2 ACMid;
        public Vector2 BCMid;

        public double AngleResult;
        public double RatioResult;

#endregion

        public MainWindow()
        {
            InitializeComponent();

            // Initialize image zoom
            UI_WorkImage.InitializeGroupTransform(new Point(0, 0));
            UI_WorkBorder.InitializeGroupTransform(new Point(0, 0));

            // Create cursor for following mouse during angle analytics
            UI_DotCursor = new Ellipse();
            UI_DotCursor.Stroke = Brushes.White;
            UI_DotCursor.StrokeThickness = 2;
            UI_DotCursor.Height = 10;
            UI_DotCursor.Width = 10;

            ResetAnalytics();
        }


        private void ResetAnalytics()
        {
            UI_WorkCanvas.Children.Add(UI_DotCursor);
            UI_DotCursor.SetPosition(0, 0);
            AngleResult = 0;
            UI_CurveAngleOutputValue.Content = "0";
            CurrentPointIndex = 0;
        }

        private void ClearCanvas()
        {
            UI_WorkCanvas.Children.Clear();
            ResetAnalytics();
        }

        private void WorkSpace_Clear(object sender, RoutedEventArgs e)
        {
            ClearCanvas();
        }

        private void Menu_OpenImage(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            if (openFileDialog.ShowDialog() == true)
            {
                WorkingImage = new BitmapImage(new Uri(openFileDialog.FileName, UriKind.RelativeOrAbsolute));
                UI_WorkImage.Source = WorkingImage;
                //ImageBrush ib = new ImageBrush();
                //ib.ImageSource = new BitmapImage(new Uri(openFileDialog.FileName, UriKind.Relative));
                //ib.Stretch = Stretch.Uniform;
                //UI_WorkCanvas.Background = ib;
            }

            ClearCanvas();
        }

        private void CalculateAngle()
        {
            Vector vA = new Vector(Intersection.X - PointA.X, Intersection.Y - PointA.Y);
            Vector vB = new Vector(Intersection.X - PointB.X, Intersection.Y - PointB.Y);

            AngleResult = Math.Abs(Vector.AngleBetween(vA, vB));
            UI_CurveAngleOutputValue.Content = AngleResult;
        }

        // Ideally, the selected tool will change how we handle these. They could possibly be live-swapped, or we could use a switch case?
        private void WorkSpace_Click(object sender, MouseButtonEventArgs e)
        {
            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));
            switch (CurrentPointIndex)
            {
                case 0:

                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = AddLine(mousePos, mousePos);
                    CurrentPointIndex++;
                    break;

                case 1:

                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    PointB = new Vector2(mousePos.X, mousePos.Y);
                    Midpoint = (PointA + PointB) * 0.5;
                    CurrentUILine = AddLine(Midpoint, Midpoint);

                    // make the orthoganal
                    Vector3 p1 = new Vector3(PointA.X, PointA.Y, 1);
                    Vector3 p2 = new Vector3(PointB.X, PointB.Y, 1);
                    Orthoganal = (p1 ^ p2).ToVector2();
                    Orthoganal.Normalize();

                    CurrentPointIndex++;
                    break;
                case 2:

                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    AddLine(PointA, PointC);
                    AddLine(PointB, PointC);

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

                    if(Math.Abs(det) <= 0.00001)
                    {
                        //don't allow 0 height, just ignore the click and try again
                        break;
                    }

                    double u = (dy * Ray23.X - dx * Ray23.Y) / det;

                    Vector2 offset = Ray13 * u;

                    Intersection = ACMid + offset;
                    
                    CurrentUILine = null;

                    AddLine(ACMid, Intersection);
                    AddLine(BCMid, Intersection);
                    AddLine(PointA, Intersection);
                    AddLine(PointB, Intersection);

                    CalculateAngle();

                    CurrentPointIndex++;

                    break;
            }
        }
        private void WorkSpace_MouseMove(object sender, MouseEventArgs e)
        {
            Point mousePos = Mouse.GetPosition(UI_WorkCanvas);

            switch (CurrentPointIndex)
            {
                case 0:
                    UI_DotCursor.SetPosition(mousePos.X - 5, mousePos.Y - 5);

                    break;
                case 1:
                    UI_DotCursor.SetPosition(mousePos.X - 5, mousePos.Y - 5);
                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    break;

                case 2:

                    // we want to lock everything to a given 2D vector
                    // origin at the Midpoint, project down and across
                    // we can do this by making a 2D vector of the mouse position, and dotting it to get the new magnitude
                    // add normalized direction + scale to midpoint to get new point

                    Vector2 toMouse = new Vector2(mousePos.X - Midpoint.X, mousePos.Y - Midpoint.Y);
                    double newMag = Orthoganal | toMouse;

                    Vector2 newDist = Orthoganal * newMag;

                    CurrentUILine.X2 = Midpoint.X + newDist.X;
                    CurrentUILine.Y2 = Midpoint.Y + newDist.Y;
                    UI_DotCursor.SetPosition(CurrentUILine.X2 - 5, CurrentUILine.Y2 - 5);

                    break;
            }

        }

        private Line AddLine(Vector2 a, Vector2 b)
        {
            Line L = new();
            L.Stroke = Brushes.OrangeRed;
            L.StrokeThickness = 2;
            L.X1 = a.X;
            L.Y1 = a.Y;
            L.X2 = b.X;
            L.Y2 = b.Y;
            UI_WorkCanvas.Children.Add(L);
            return L;
        }

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            AboutWindow about = new AboutWindow();
            about.ShowDialog();
        }

        // TODO: encapsulate and make generic someplace else
        private void WorkSpace_ScrollZoom(object sender, MouseWheelEventArgs e)
        {
            UI_WorkImage.ZoomElement(e.Delta, e.GetPosition(UI_WorkImage));
            UI_WorkBorder.CopyTransforms(UI_WorkImage);
        }
    }
}
