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

#region Angle Analytics Variables
        
        // Angle Analytics 
        
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

#endregion

        public MainWindow()
        {
            InitializeComponent();

            // Initialize image zoom
            TransformGroup group = new TransformGroup();
            ScaleTransform scaleTransform = new ScaleTransform();
            group.Children.Add(scaleTransform);
            TranslateTransform translateTransform = new TranslateTransform();
            group.Children.Add(translateTransform);
            UI_WorkImage.RenderTransform = group;
            UI_WorkImage.RenderTransformOrigin = new Point(0, 0);

            TransformGroup canvGroup = new TransformGroup();
            ScaleTransform canvScaleTransform = new ScaleTransform();
            TranslateTransform canvTranslateTransform = new TranslateTransform();
            canvGroup.Children.Add(canvScaleTransform);
            canvGroup.Children.Add(canvTranslateTransform);
            UI_WorkBorder.RenderTransform = canvGroup;
            UI_WorkBorder.RenderTransformOrigin = new Point(0, 0);

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
            UI_AAAngleOutputValue.Content = "0";
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
            UI_AAAngleOutputValue.Content = AngleResult;
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

                    if(det <= 0.00001)
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

        /*
        

        // Source - https://stackoverflow.com/a/6782715
        // Posted by Wiesław Šoltés, modified by community. See post 'Timeline' for change history
        // Retrieved 2026-02-23, License - CC BY-SA 4.0
        // various stuff from that one SO thread to pick apart and reuse - might actually not hurt to use the whole zoom border class... baby steps. we have the canvas overlay to worry about as well
        // we'll want to start making a toolbar with icons shortcuts, and cursors
        // general tool bar for image/canvas manip (move, zoom, reset view)
        // tabable tool bar for different "work spaces"
        // different canvas overlays for each one? toggeable visuals?

        // used for panning
        private Point origin;
        private Point start;

        public void Reset()
        {
          if (child != null)
          {
            // reset zoom
            var st = GetScaleTransform(child);
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;

            // reset pan
            var tt = GetTranslateTransform(child);
            tt.X = 0.0;
            tt.Y = 0.0;
          }
        }

        private void child_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (child != null)
            {
                var tt = GetTranslateTransform(child);
                start = e.GetPosition(this);
                origin = new Point(tt.X, tt.Y);
                this.Cursor = Cursors.Hand;
                child.CaptureMouse();
            }
        }

        private void child_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (child != null)
            {
                child.ReleaseMouseCapture();
                this.Cursor = Cursors.Arrow;
            }
        }

        void child_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Reset();
        }

        private void child_MouseMove(object sender, MouseEventArgs e)
        {
            if (child != null)
            {
                if (child.IsMouseCaptured)
                {
                    var tt = GetTranslateTransform(child);
                    Vector v = start - e.GetPosition(this);
                    tt.X = origin.X - v.X;
                    tt.Y = origin.Y - v.Y;
                }
            }
        }

 
         */

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
            var st = CanvasUtils.GetScaleTransform(UI_WorkImage);
            var tt = CanvasUtils.GetTranslateTransform(UI_WorkImage);

            var cst = CanvasUtils.GetScaleTransform(UI_WorkBorder);
            var ctt = CanvasUtils.GetTranslateTransform(UI_WorkBorder);

            double zoom = e.Delta > 0 ? .2 : -.2;
            if (!(e.Delta > 0) && (st.ScaleX < .4 || st.ScaleY < .4))
                return;

            Point relative = e.GetPosition(UI_WorkImage);
            double absoluteX;
            double absoluteY;

            absoluteX = relative.X * st.ScaleX + tt.X;
            absoluteY = relative.Y * st.ScaleY + tt.Y;

            st.ScaleX += zoom;
            st.ScaleY += zoom;

            tt.X = absoluteX - relative.X * st.ScaleX;
            tt.Y = absoluteY - relative.Y * st.ScaleY;

            cst.ScaleX = st.ScaleX;
            cst.ScaleY = st.ScaleY;

            ctt.X = tt.X;
            ctt.Y = tt.Y;
        }
    }
}
