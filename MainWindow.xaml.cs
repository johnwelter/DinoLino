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
using System.CodeDom;
using DinoLino.Utilities.Modes;

namespace DinoLino
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        // Current working image in the workspace
        public BitmapImage WorkingImage;

        // Work Modes
        public CurvatureMode CurvatureMode;
        public GetAngleMode GetAngleMode;
        public DrawMode DrawMode;

        // Current active work mode
        public WorkMode CurrentWorkMode;

        // Cursor for mouse in the workspace
        public Ellipse UI_DotCursor;

        #region Initialization
        public MainWindow()
        {
            InitializeComponent();

            // Keyboard shortcuts
            this.KeyDown += MainWindow_KeyDown;
            UI_WorkCanvas.MouseDown += (s, e) =>
            {
                Keyboard.ClearFocus();
                UI_WorkCanvas.Focus();
            };

            // TODO: it feels like work modes should control their own 
            // UI instead of having them predefined and hooked up through here - but that's a later fix. We'll want
            // to double check making separate work modes is even what we want in the first place.
            // for instance, if a work mode has specific tools (like draw will), the button callbacks should maybe be defined in the
            // work mode and not in this main window. 
            // binding functions is luckily not super hard in c#, relative to binding data.  

            // Initiate curvature mode and make appropriate bindings
            CurvatureMode = new();
            CurvatureMode.BindCurvatureResults(UI_CurveAngleOutputValue, UI_AspectRatioOutputValue, UI_TurningAngleOutputValue, UI_ChordArcRatioOutputValue);

            GetAngleMode = new();
            GetAngleMode.BindAngleResults(UI_TriAngleOutputValue1, UI_TriAngleOutputValue2, UI_TriAngleOutputValue3, UI_TriAspectRatioValue, UI_TriAreaRatioValue);

            DrawMode = new();
            DrawMode.BindDrawResults(UI_DrawAspectRatioOutputValue, UI_ShapeAreaOutputValue, UI_LineLengthRatioOutputValue);
            UI_DrawAngleValue.TextChanged += DrawAngleValue_TextChanged;
            UI_WorkCanvas.MouseDown += (s, e) =>
            {
                Keyboard.ClearFocus();
                UI_WorkCanvas.Focus();
            };

            // Set the current work mode to update
            CurrentWorkMode = CurvatureMode;
            BindUndoRedoMenuItems();

            // Initialize image zoom
            UI_WorkImage.InitializeGroupTransform(new Point(0, 0));
            UI_WorkBorder.InitializeGroupTransform(new Point(0, 0));

            // Create cursor for following mouse during angle analytics
            UI_DotCursor = new Ellipse();
            UI_DotCursor.Stroke = Brushes.White;
            UI_DotCursor.StrokeThickness = 2;
            UI_DotCursor.Height = 10;
            UI_DotCursor.Width = 10;
            AddElementToWorkSpace(UI_DotCursor);

            //Reset the current work mode
            CurrentWorkMode.Reset();
        }
        #endregion

        #region Internal Workspace Functions
        private void ClearWorkspace()
        {
            // Clear everything in the workspace, put the cursor back in
            UI_WorkCanvas.Children.Clear();
            AddElementToWorkSpace(UI_DotCursor);
            UI_DotCursor.SetPosition(0, 0);

            //reset the work mode
            CurrentWorkMode.Reset();
        }

        private void AddElementToWorkSpace(UIElement element)
        {
            if (element == null) return;

            // Remove from logical parent first
            if (element is FrameworkElement fe && fe.Parent is Panel logicalPanel)
            {
                logicalPanel.Children.Remove(element);
            }
            else
            {
                // Fall back to visual parent
                var visualParent = VisualTreeHelper.GetParent(element);
                if (visualParent is Panel visualPanel)
                    visualPanel.Children.Remove(element);
            }

            // Add to workspace if not already there
            if (!UI_WorkCanvas.Children.Contains(element))
                UI_WorkCanvas.Children.Add(element);
        }
        


        private void UpdateWorkSpaceZoom(double delta, Point relativeTo)
        {
            UI_WorkImage.ZoomElement(delta, relativeTo);
            UI_WorkBorder.CopyTransforms(UI_WorkImage);
        }

        private void ResetWorkSpaceZoom()
        {
            UI_WorkImage.ResetZoom();
            UI_WorkBorder.CopyTransforms(UI_WorkImage);
        }

        // Keyboard shortcuts
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl + C to reset workspace
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                ClearWorkspace();
            }
            // Ctrl + F to open image
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                Menu_OpenImage(this, new RoutedEventArgs());
                e.Handled = true;
            }
            // Ctrl + Z to undo
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                Menu_Undo(this, new RoutedEventArgs());
                e.Handled = true;
            }
            // Ctrl + R to redo
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                Menu_Redo(null, null);
            }
        }
        #endregion

        #region Menu Bar functions
        private void Menu_OpenImage(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            if (openFileDialog.ShowDialog() == true)
            {
                WorkingImage = new BitmapImage(new Uri(openFileDialog.FileName, UriKind.RelativeOrAbsolute));
                UI_WorkImage.Source = WorkingImage;
            }

            ResetWorkSpaceZoom();
            ClearWorkspace();
        }

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            AboutWindow about = new AboutWindow();
            about.ShowDialog();
        }

        // Added User Guide
        private void Menu_UserGuide(object sender, RoutedEventArgs e)
        {
            UserGuideWindow userguide = new UserGuideWindow();
            userguide.ShowDialog();
        }

        // Undo function
        private void Menu_Undo(object sender, RoutedEventArgs e)
        {
            var result = CurrentWorkMode.Undo();

            if (result == null) return;
            {
                foreach (var el in result.Elements)
                {
                    UI_WorkCanvas.Children.Remove(el);
                }
            }
        }

        // Redo function
        private void Menu_Redo(object sender, RoutedEventArgs e)
        {
            var result = CurrentWorkMode.Redo();

            if (result == null) return;
            {
                foreach (var el in result.Elements)
                {
                    AddElementToWorkSpace(el);
                }
            }
        }

        private void BindUndoRedoMenuItems()
        {
            Binding undoBinding = new Binding(nameof(WorkMode.CanUndo));
            undoBinding.Source = CurrentWorkMode;
            UI_MenuUndo.SetBinding(MenuItem.IsEnabledProperty, undoBinding);

            Binding redoBinding = new Binding(nameof(WorkMode.CanRedo));
            redoBinding.Source = CurrentWorkMode;
            UI_MenuRedo.SetBinding(MenuItem.IsEnabledProperty, redoBinding);
        }

        private void Menu_SeePrevOps(object sender, RoutedEventArgs e)
        {
            bool showPrevious = UI_SeePrevOps.IsChecked;

            if (showPrevious)
            { 
                //placeholder
            }
            else
            {
                //placeholder
            }
        }
        #endregion

        #region Global Toolbar Functions
        private void GlobalTools_Clear(object sender, RoutedEventArgs e)
        {
            ClearWorkspace();
        }

        private void ControlTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UI_ControlTabs.SelectedItem is TabItem tab)
            {
                switch (tab.Header.ToString())
                {
                    case "Curvature":
                        CurrentWorkMode = CurvatureMode;
                        break;

                    case "Triangle":
                        CurrentWorkMode = GetAngleMode;
                        break;

                    case "Draw":
                        CurrentWorkMode = DrawMode; 
                        break;
                }

                CurrentWorkMode?.ResetDrawingState(); // only reset in-progress click history
                BindUndoRedoMenuItems();
            }
        }
        #endregion

            #region Work Space UI Functions

        private void WorkSpace_Click(object sender, MouseButtonEventArgs e)
        {
            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));

            if (e.ClickCount == 2)
            {
                List<UIElement> elementsToAdd = CurrentWorkMode.ProcessDoubleClick(mousePos);
                foreach (UIElement element in elementsToAdd)
                    AddElementToWorkSpace(element);
                return;
            }

            List<UIElement> elementsToAdd2 = CurrentWorkMode.ProcessClick(mousePos);

            foreach (UIElement element in elementsToAdd2)
            {
                AddElementToWorkSpace(element);
            }
        }

        private void WorkSpace_MouseMove(object sender, MouseEventArgs e)
        {
            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));
            Vector2 centeredCursorPos = CurrentWorkMode.ProcessMouseMovement(mousePos) - new Vector2(5, 5);
            UI_DotCursor.SetPosition(centeredCursorPos.X, centeredCursorPos.Y);
        }

        // TODO: encapsulate and make generic someplace else
        private void WorkSpace_ScrollZoom(object sender, MouseWheelEventArgs e)
        {
            UpdateWorkSpaceZoom(e.Delta, e.GetPosition(UI_WorkImage));
        }

        // radio buttons
        // Draw Mode
        private void DrawNone_Checked(object sender, RoutedEventArgs e)
        {
            DrawMode.CurrentShape = DrawMode.DrawShape.None;
            DrawMode.ResetDrawingState();
        }

        private void Rectangle_Checked(object sender, RoutedEventArgs e)
        {
            DrawMode.CurrentShape = DrawMode.DrawShape.Rectangle;
        }
        private void Circle_Checked(object sender, RoutedEventArgs e)
        {
            DrawMode.CurrentShape = DrawMode.DrawShape.Circle;
        }
        private void Ellipse_Checked(object sender, RoutedEventArgs e)
        {
            DrawMode.CurrentShape = DrawMode.DrawShape.Ellipse;
        }
        private void Square_Checked(object sender, RoutedEventArgs e)
        {
            DrawMode.CurrentShape = DrawMode.DrawShape.Square;
        }
        private void DrawAngle_Checked(object sender, RoutedEventArgs e)
        {
            DrawMode.CurrentShape = DrawMode.DrawShape.Angle;
            if (double.TryParse(UI_DrawAngleValue.Text, out double angle))
                DrawMode.LockedAngleDegrees = angle;
        }
        private void DrawAngleValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(UI_DrawAngleValue.Text, out double angle))
                DrawMode.LockedAngleDegrees = angle;
        }
        private void Line_Checked(object sender, RoutedEventArgs e)
        {
            DrawMode.CurrentShape = DrawMode.DrawShape.Line;
        }

        // Curvature Mode
        private void CurvNone_Checked(object sender, RoutedEventArgs e)
        {
            CurvatureMode.CurrentMethod = CurvatureMode.CurvatureMethod.None;
            CurvatureMode.ResetDrawingState();
        }

        private void ThreePointArc_Checked(object sender, RoutedEventArgs e)
        {
            CurvatureMode.CurrentMethod = CurvatureMode.CurvatureMethod.ThreePointArc;
            CurvatureMode.ResetDrawingState();
        }

        private void nPointSpline_Checked(object sender, RoutedEventArgs e)
        {
            CurvatureMode.CurrentMethod = CurvatureMode.CurvatureMethod.NPointSpline;
            CurvatureMode.ResetDrawingState();
        }
        #endregion
    }
}
