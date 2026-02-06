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

        public int pointIndex = 0;
        public Line currentLine = null;
        public double currentSlope;

        public Vector2 Point1;
        public Vector2 Point2;
        public Vector2 Midpoint;
        public Vector2 Orthoganal;
        public Vector2 Point3;
        public Vector2 Intersection;

        public Vector2 Point13Mid;
        public Vector2 Point23Mid;

        public Ellipse DotCursor;
        public double angle;
        public int tries;
        public double averageTotal;
        public double average;
        public double previousAverage;

        public double confidence;

        public List<double> angles;

        public MainWindow()
        {
            InitializeComponent();
            DotCursor = new Ellipse();
            DotCursor.Stroke = Brushes.White;
            DotCursor.StrokeThickness = 2;
            DotCursor.Height = 10;
            DotCursor.Width = 10;
            WorkCanvas.Children.Add(DotCursor);
            DotCursor.SetPosition(0, 0);
            angles = new List<double>();
            for(int i = 0; i < 99; i++)
            {
                ConfidenceSelection.Items.Add($"{i + 1}%");
            }
            ConfidenceSelection.SelectedIndex = 0;
        }


        private void ResetCanvas()
        {

            WorkCanvas.Children.Clear();
            WorkCanvas.Children.Add(DotCursor);
            DotCursor.SetPosition(0, 0);

            if (pointIndex == 3)
            {
                previousAverage = average;
                averageTotal += angle;
                tries++;
                average = averageTotal / tries;
                angles.Add(angle);
                RefreshConfidence();
            }

            pointIndex = 0;

        }

        public void RefreshConfidence()
        {
            double determinant = 0;
            double deviation = 0;
            double onemargin = 0;

            foreach (double F in angles)
            {
                determinant += Math.Pow(F - average, 2);
            }

            deviation = Math.Sqrt(determinant / tries);

            onemargin = Stats.ZTable[ConfidenceSelection.SelectedIndex] * Math.Abs(deviation / Math.Sqrt(tries));

            averageOut.Content = average;
            confidenceOut.Content = average.ToString() + "±" + onemargin.ToString();
        }

        private void ClearCanvas(object sender, RoutedEventArgs e)
        {
            ResetCanvas();
        }

        private void OpenImageButton(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            if (openFileDialog.ShowDialog() == true)
            {
                ImageBrush ib = new ImageBrush();
                ib.ImageSource = new BitmapImage(new Uri(openFileDialog.FileName, UriKind.Relative));
                ib.Stretch = Stretch.Uniform;
                WorkCanvas.Background = ib;
            }

            ResetCanvas();
            angle = 0;
            average = 0;
            previousAverage = 0;
            averageTotal = 0;
            tries = 0;

            angleOut.Content = "0";
            averageOut.Content = "0";
            confidenceOut.Content = "0";
        }

        private void CalculateAngle()
        {
            Vector vA = new Vector(Intersection.X - Point1.X, Intersection.Y - Point1.Y);
            Vector vB = new Vector(Intersection.X - Point2.X, Intersection.Y - Point2.Y);

            angle = Math.Abs(Vector.AngleBetween(vA, vB));
            angleOut.Content = angle;
        }

        private void MouseClick(object sender, RoutedEventArgs e)
        {
            Vector2 mousePos = new Vector2(Mouse.GetPosition(WorkCanvas));
            switch (pointIndex)
            {
                case 0:

                    Point1 = new Vector2(mousePos.X, mousePos.Y);
                    currentLine = AddLine(mousePos, mousePos);
                    pointIndex++;
                    break;

                case 1:

                    currentLine.X2 = mousePos.X;
                    currentLine.Y2 = mousePos.Y;
                    Point2 = new Vector2(mousePos.X, mousePos.Y);
                    Midpoint = (Point1 + Point2) * 0.5;
                    currentLine = AddLine(Midpoint, Midpoint);

                    // make the orthoganal
                    Vector3 p1 = new Vector3(Point1.X, Point1.Y, 1);
                    Vector3 p2 = new Vector3(Point2.X, Point2.Y, 1);
                    Orthoganal = (p1 ^ p2).ToVector2();
                    Orthoganal.Normalize();

                    pointIndex++;
                    break;
                case 2:

                    Point3 = new Vector2(currentLine.X2, currentLine.Y2);

                    AddLine(Point1, Point3);
                    AddLine(Point2, Point3);

                    //midpoints for those lines
                    Point13Mid = (Point1 + Point3) * 0.5;
                    Point23Mid = (Point2 + Point3) * 0.5;

                    //generate the orthogonal lines, and find their intersection point

                    Vector2 Ray13 = (new Vector3(Point1.X, Point1.Y, 1) ^ new Vector3(Point3.X, Point3.Y, 1)).ToVector2();
                    Ray13.Normalize();

                    Vector2 Ray23 = (new Vector3(Point2.X, Point2.Y, 1) ^ new Vector3(Point3.X, Point3.Y, 1)).ToVector2();
                    Ray23.Normalize();

                    Vector2 diff = Point23Mid - Point13Mid;
                    double dx = Point23Mid.X - Point13Mid.X;
                    double dy = Point23Mid.Y - Point13Mid.Y;
                    double det = Ray23 ^ Ray13;

                    if(det <= 0.00001)
                    {
                        //don't allow 0 height, just ignore the click and try again
                        break;
                    }

                    double u = (dy * Ray23.X - dx * Ray23.Y) / det;

                    Vector2 offset = Ray13 * u;

                    Intersection = Point13Mid + offset;
                    
                    currentLine = null;

                    AddLine(Point13Mid, Intersection);
                    AddLine(Point23Mid, Intersection);
                    AddLine(Point1, Intersection);
                    AddLine(Point2, Intersection);

                    CalculateAngle();

                    pointIndex++;

                    break;
            }
        }
        private void MouseFollow(object sender, MouseEventArgs e)
        {

            Point mousePos = Mouse.GetPosition(WorkCanvas);

            switch (pointIndex)
            {
                case 0:
                    DotCursor.SetPosition(mousePos.X - 5, mousePos.Y - 5);

                    break;
                case 1:
                    DotCursor.SetPosition(mousePos.X - 5, mousePos.Y - 5);
                    currentLine.X2 = mousePos.X;
                    currentLine.Y2 = mousePos.Y;
                    break;

                case 2:

                    // we want to lock everything to a given 2D vector
                    // origin at the Midpoint, project down and across
                    // we can do this by making a 2D vector of the mouse position, and dotting it to get the new magnitude
                    // add normalized direction + scale to midpoint to get new point

                    Vector2 toMouse = new Vector2(mousePos.X - Midpoint.X, mousePos.Y - Midpoint.Y);
                    double newMag = Orthoganal | toMouse;

                    Vector2 newDist = Orthoganal * newMag;

                    currentLine.X2 = Midpoint.X + newDist.X;
                    currentLine.Y2 = Midpoint.Y + newDist.Y;
                    DotCursor.SetPosition(currentLine.X2 - 5, currentLine.Y2 - 5);

                    break;
            }

        }

        private void FullresetCanvas_Click(object sender, RoutedEventArgs e)
        {
            WorkCanvas.Children.Clear();
            WorkCanvas.Children.Add(DotCursor);
            Canvas.SetLeft(DotCursor, 0);
            Canvas.SetRight(DotCursor, 1);
            angles.Clear();

            angle = 0;
            average = 0;
            previousAverage = 0;
            averageTotal = 0;
            tries = 0;
            confidence = 0;

            angleOut.Content = "0";
            averageOut.Content = "0";
            confidenceOut.Content = "N/A";

            pointIndex = 0;
        }

        private void Combobox1_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshConfidence();
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
            WorkCanvas.Children.Add(L);
            return L;
        }
    }
}
