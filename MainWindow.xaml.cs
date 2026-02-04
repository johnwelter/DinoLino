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

        public Point A;
        public Point B;
        public Point C;
        public Point D;
        public Point X;
        public Point E;

        public Point AX;
        public Point BX;

        public Ellipse El;
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
            El = new Ellipse();
            El.Stroke = Brushes.White;
            El.StrokeThickness = 2;
            El.Height = 10;
            El.Width = 10;
            LineHold.Children.Add(El);
            Canvas.SetLeft(El, 0);
            Canvas.SetRight(El, 1);
            angles = new List<double>();
            for(int i = 0; i < 99; i++)
            {
                combobox1.Items.Add($"{i + 1}%");
            }
            combobox1.SelectedIndex = 0;
        }


        private void ResetCanvas()
        {

            LineHold.Children.Clear();
            LineHold.Children.Add(El);
            Canvas.SetLeft(El, 0);
            Canvas.SetRight(El, 1);

            if (pointIndex == 4)
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

            onemargin = Stats.ZTable[combobox1.SelectedIndex] * Math.Abs(deviation / Math.Sqrt(tries));

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
                LineHold.Background = ib;
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
            Vector vA = new Vector(E.X - A.X, E.Y - A.Y);
            Vector vB = new Vector(E.X - B.X, E.Y - B.Y);

            angle = Math.Abs(Vector.AngleBetween(vA, vB));
            angleOut.Content = angle;
        }

        private void MouseClick(object sender, RoutedEventArgs e)
        {
            Point P;
            Point Mid;
            Line L;
            switch (pointIndex)
            {
                case 0:

                    P = Mouse.GetPosition(LineHold);
                    L = new Line();
                    L.X1 = P.X;
                    L.Y1 = P.Y;
                    L.X2 = P.X;
                    L.Y2 = P.Y;
                    A = new Point(P.X, P.Y);
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    LineHold.Children.Add(L);
                    currentLine = L;
                    pointIndex++;
                    break;

                case 1:
                    P = Mouse.GetPosition(LineHold);
                    currentLine.X2 = P.X;
                    currentLine.Y2 = P.Y;
                    B = new Point(P.X, P.Y);
                    L = new Line();
                    Mid = new Point((currentLine.X1 + currentLine.X2) / 2, (currentLine.Y1 + currentLine.Y2) / 2);
                    L.X1 = Mid.X;
                    L.Y1 = Mid.Y;
                    L.X2 = Mid.X;
                    L.Y2 = Mid.Y;
                    C = new Point(Mid.X, Mid.Y);
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    LineHold.Children.Add(L);
                    currentSlope = ((currentLine.Y2 - currentLine.Y1) / (currentLine.X2 - currentLine.X1));
                    currentLine = L;
                    pointIndex++;
                    break;
                case 2:
                    D = new Point(currentLine.X2, currentLine.Y2);
                    currentLine = null;
                    pointIndex++;

                    L = new Line();
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    L.X1 = A.X;
                    L.Y1 = A.Y;
                    L.X2 = X.X;
                    L.Y2 = X.Y;
                    LineHold.Children.Add(L);

                    L = new Line();
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    L.X1 = B.X;
                    L.Y1 = B.Y;
                    L.X2 = X.X;
                    L.Y2 = X.Y;
                    LineHold.Children.Add(L);

                    AX = new Point((A.X + X.X) / 2, (A.Y + X.Y) / 2);
                    BX = new Point((B.X + X.X) / 2, (B.Y + X.Y) / 2);

                    double mA = (X.Y - C.Y) / (X.X - C.X);
                    double mB = (X.Y - AX.Y) / (X.X - AX.X);
                    mB = -1 / mB;

                    double bA = X.Y - (mA * X.X);
                    double bB = AX.Y - (mB * AX.X);

                    E.X = (bB - bA) / (mA - mB);
                    E.Y = mB * (E.X) + bB;

                    L = new Line();
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    L.X1 = AX.X;
                    L.Y1 = AX.Y;
                    L.X2 = E.X;
                    L.Y2 = E.Y;
                    LineHold.Children.Add(L);

                    L = new Line();
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    L.X1 = BX.X;
                    L.Y1 = BX.Y;
                    L.X2 = E.X;
                    L.Y2 = E.Y;
                    LineHold.Children.Add(L);

                    L = new Line();
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    L.X1 = A.X;
                    L.Y1 = A.Y;
                    L.X2 = E.X;
                    L.Y2 = E.Y;
                    LineHold.Children.Add(L);
                    L = new Line();
                    L.Stroke = Brushes.OrangeRed;
                    L.StrokeThickness = 2;
                    L.X1 = B.X;
                    L.Y1 = B.Y;
                    L.X2 = E.X;
                    L.Y2 = E.Y;
                    LineHold.Children.Add(L);

                    CalculateAngle();

                    pointIndex++;

                    break;
            }
        }
        private void MouseFollow(object sender, MouseEventArgs e)
        {

            Point P = Mouse.GetPosition(LineHold);

            switch (pointIndex)
            {
                case 0:
                    Canvas.SetLeft(El, P.X - 5);
                    Canvas.SetTop(El, P.Y - 5);
                    break;
                case 1:
                    Canvas.SetLeft(El, P.X - 5);
                    Canvas.SetTop(El, P.Y - 5);
                    currentLine.X2 = P.X;
                    currentLine.Y2 = P.Y;
                    break;

                case 2:

                    if (Math.Abs(currentSlope) == 0)
                    {
                        currentLine.Y2 = P.Y;
                        Canvas.SetTop(El, P.Y - 5);

                    }

                    else
                    {
                        //have rise, need run
                        //need inverse slope

                        currentLine.X2 = currentLine.X1 + (-currentSlope * (P.Y - currentLine.Y1));
                        currentLine.Y2 = P.Y;
                        Canvas.SetLeft(El, currentLine.X2 - 5);
                        Canvas.SetTop(El, P.Y - 5);
                        X.X = (C.X + (-currentSlope * (P.Y - currentLine.Y1)));
                        X.Y = P.Y;

                    }

                    break;
            }

        }

        private void FullresetCanvas_Click(object sender, RoutedEventArgs e)
        {
            LineHold.Children.Clear();
            LineHold.Children.Add(El);
            Canvas.SetLeft(El, 0);
            Canvas.SetRight(El, 1);
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
    }
}
