using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Security.Policy;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DinoLino
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public UndoRedoManager UndoRedoManager;

        // Tracks how many images have been opened
        private int _specimenCount = 0;

        // store selected font type
        private FontFamily _currentFont = new FontFamily("Arial");

        // store selected font size
        private double _currentFontSize = 14;

        // image adjuster field
        private ImageAdjuster _imageAdjuster = new ImageAdjuster();

        // Current working image in the workspace
        public BitmapImage WorkingImage;

        // Work Modes
        public CurvatureMode CurvatureMode;
        public GetAngleMode GetAngleMode;
        public DrawMode DrawMode;

        // A list to hold all modes for global settings
        public List<WorkMode> AllWorkModes;

        // Current active work mode
        public WorkMode CurrentWorkMode;

        // Cursor for mouse in the workspace
        public Ellipse UI_DotCursor;

        #region Initialization
        public MainWindow()
        {
            InitializeComponent();

            _imageAdjuster.OnAdjustmentApplied = bitmap => UI_WorkImage.Source = bitmap;

            // Keyboard shortcuts
            this.PreviewKeyDown += MainWindow_KeyDown;
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
            CurvatureMode.BindCurvatureResults(
                UI_CurveAngleOutputValue, UI_ChordArcRatioOutputValue, UI_AspectRatioOutputValue, // circular arc metadata
                UI_XYFunctionOutputValue, UI_PChordArcRatioOutputValue, UI_RiseSpanRatioOutputValue, UI_VertexCurvatureOutputValue, // parabolic arc metadata
                UI_TurningAngleOutputValue, UI_SChordArcRatioOutputValue); // spline metadata

            GetAngleMode = new();
            GetAngleMode.BindAngleResults(UI_TriAngleOutputValue1, UI_TriAngleOutputValue2, UI_TriAngleOutputValue3, UI_TriAspectRatioValue, UI_TriAreaRatioValue);

            DrawMode = new();
            DrawMode.BindDrawResults(UI_DrawAspectRatioOutputValue, UI_ShapeAreaOutputValue, UI_LineLengthRatioOutputValue);

            UI_DrawAngleValue.TextChanged += DrawAngleValue_TextChanged;

            // Initialize list of all modes
            AllWorkModes = new List<WorkMode> { CurvatureMode, GetAngleMode, DrawMode };

            // Set the current work mode to update
            CurrentWorkMode = CurvatureMode;
            BindUndoRedoMenuItems();

            // Initialize the global undo redo manager and link to all modes
            UndoRedoManager = new UndoRedoManager();
            CurvatureMode.UndoRedoManager = UndoRedoManager;
            GetAngleMode.UndoRedoManager = UndoRedoManager;
            DrawMode.UndoRedoManager = UndoRedoManager;

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
            // Ctrl + Y to redo
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                Menu_Redo(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        #endregion

        #region Menu Bar functions
        private void Menu_OpenImage(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            if (openFileDialog.ShowDialog() == true)
            {
                _specimenCount++;

                WorkingImage = new BitmapImage(new Uri(openFileDialog.FileName, UriKind.RelativeOrAbsolute));
                UI_WorkImage.Source = WorkingImage;
                UI_SpecimenNameBox.Text = $"Specimen {_specimenCount}";
            }

            ResetWorkSpaceZoom();
            ClearWorkspace();
            _imageAdjuster.CacheImage(WorkingImage);
        }

        private void SpecimenCount_Up(object sender, RoutedEventArgs e)
        {
            _specimenCount++;
            UI_SpecimenNameBox.Text = $"Specimen {_specimenCount}";
        }

        private void SpecimenCount_Down(object sender, RoutedEventArgs e)
        {
            if (_specimenCount > 1)
            {
                _specimenCount--;
                UI_SpecimenNameBox.Text = $"Specimen {_specimenCount}";
            }
        }

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            AboutWindow about = new AboutWindow();
            about.FontSize = _currentFontSize;
            about.FontFamily = _currentFont;
            about.ShowDialog();
        }

        // Added User Guide
        private void Menu_UserGuide(object sender, RoutedEventArgs e)
        {
            UserGuideWindow userguide = new UserGuideWindow();
            userguide.FontFamily = _currentFont;
            userguide.FontSize = _currentFontSize;
            userguide.ShowDialog();
        }

        // Undo function
        private void Menu_Undo(object sender, RoutedEventArgs e)
        {
            var result = UndoRedoManager.Undo();

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
            var result = UndoRedoManager.Redo();

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
            undoBinding.Source = UndoRedoManager;
            UI_MenuUndo.SetBinding(MenuItem.IsEnabledProperty, undoBinding);

            Binding redoBinding = new Binding(nameof(WorkMode.CanRedo));
            redoBinding.Source = UndoRedoManager;
            UI_MenuRedo.SetBinding(MenuItem.IsEnabledProperty, redoBinding);
        }

        private void Menu_SeePrevOps(object sender, RoutedEventArgs e)
        {
            bool isChecked = UI_SeePrevOps.IsChecked;

            // Update the global preference 
            foreach (var mode in AllWorkModes)
            {
                mode.SeePreviousOperations = isChecked;
            }

            // Refresh screen
            if (!isChecked)
            {
                ClearWorkspaceVisualsOnly();
            }
            else
            {
                ClearWorkspaceVisualsOnly();

                foreach (var operation in UndoRedoManager.History)
                {
                    foreach (var el in operation.Elements)
                        AddElementToWorkSpace(el);
                }
            }
        }

        private void Menu_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                menuItem.IsSubmenuOpen = false;
            }
        }

        private void Menu_Color_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null)
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(rb.Tag.ToString());
                CurrentWorkMode.LineColor = brush;
            }
        }

        private void UI_FontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (UI_ControlPanel == null || UI_FontSizeeText == null)
                return;

            double size = e.NewValue;

            UI_FontSizeeText.Text = size.ToString("0");

            TextElement.SetFontSize(UI_ControlPanel, size);
            TextElement.SetFontSize(UI_WorkSpace, size);
            _currentFontSize = size;
        }

        private void Menu_FontType_Click(object sender, RoutedEventArgs e)
        {
            RadioButton rb = sender as RadioButton;

            if (rb != null)
            {
                string fontName = rb.Tag.ToString();
                _currentFont = new FontFamily(fontName);
                FontFamily newFont = new FontFamily(fontName);
                TextElement.SetFontFamily(UI_ControlPanel, newFont);
            }
        }

        private void UI_ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (UI_ContrastValueText != null)
                UI_ContrastValueText.Text = e.NewValue.ToString("0");
            _imageAdjuster.RequestAdjustment(
                UI_ContrastSlider.Value / 100.0,
                UI_BrightnessSlider.Value / 100.0,
                UI_SaturationSlider.Value / 100.0);
        }

        private void UI_BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (UI_BrightnessValueText != null)
                UI_BrightnessValueText.Text = e.NewValue.ToString("0");
            _imageAdjuster.RequestAdjustment(
                UI_ContrastSlider.Value / 100.0,
                UI_BrightnessSlider.Value / 100.0,
                UI_SaturationSlider.Value / 100.0);
        }

        private void UI_SaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (UI_SaturationValueText != null)
                UI_SaturationValueText.Text = e.NewValue.ToString("0");
            _imageAdjuster.RequestAdjustment(
                UI_ContrastSlider.Value / 100.0,
                UI_BrightnessSlider.Value / 100.0,
                UI_SaturationSlider.Value / 100.0);
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

            bool isStartingNewOperation = CurrentWorkMode.CurrentStep == 0 || CurrentWorkMode.CurrentStep == 3;

            if (!CurrentWorkMode.SeePreviousOperations && isStartingNewOperation)
            {
                // don't use Children.Clear() because we want to keep the DotCursor
                ClearWorkspaceVisualsOnly();
            }

            if (e.ClickCount == 2)
            {
                RemovePendingElements();

                List<UIElement> elementsToAdd = CurrentWorkMode.ProcessDoubleClick(mousePos);
                foreach (UIElement element in elementsToAdd)
                    AddElementToWorkSpace(element);
                return;
            }

            RemovePendingElements();

            List<UIElement> elementsToAdd2 = CurrentWorkMode.ProcessClick(mousePos);

            foreach (UIElement element in elementsToAdd2)
            {
                AddElementToWorkSpace(element);
            }
        }

        // Helper method for single click and double click finishing
        private void RemovePendingElements()
        {
            foreach (UIElement element in CurrentWorkMode.ElementsToRemove)
            {
                UI_WorkCanvas.Children.Remove(element);
            }
            CurrentWorkMode.ClearElementsToRemove();
        }

        // Helper method to clear the drawings but keep the cursor
        private void ClearWorkspaceVisualsOnly()
        {
            // Loop backwards to safely remove children while keeping the UI_DotCursor
            for (int i = UI_WorkCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (UI_WorkCanvas.Children[i] != UI_DotCursor)
                {
                    UI_WorkCanvas.Children.RemoveAt(i);
                }
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

        private void DrawMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && Enum.TryParse(rb.Tag.ToString(), out DrawMode.DrawMethod selectedMethod))
            {
                DrawMode.CurrentMethod = selectedMethod;
                DrawMode.ResetDrawingState();
            }
        }

        private void Shape_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && Enum.TryParse(rb.Tag.ToString(), out DrawMode.ShapeConstraint selectedShape))
            {
                DrawMode.CurrentShape = selectedShape;
                DrawMode.ResetDrawingState();
            }
        }

        private void DrawAngleValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            DrawMode.UpdateAngle(UI_DrawAngleValue.Text);
        }

        private void LineConstraint_Checked(object sender, RoutedEventArgs e)
        {
            if (DrawMode == null) return; // guard for initialization ordering

            if (sender is RadioButton rb && Enum.TryParse(rb.Tag.ToString(), out DrawMode.LineConstraint selectedConstraint))
            {
                DrawMode.CurrentLineType = selectedConstraint;
                DrawMode.ResetDrawingState();
            }
        }

        // Curvature Mode
        private void CurvNone_Checked(object sender, RoutedEventArgs e)
        {
            CurvatureMode.CurrentMethod = CurvatureMode.CurvatureMethod.None;
            CurvatureMode.ResetDrawingState();
        }

        private void CircularArc_Checked(object sender, RoutedEventArgs e)
        {
            CurvatureMode.CurrentMethod = CurvatureMode.CurvatureMethod.CircularArc;
            CurvatureMode.CurrentStep = 0;
            CurvatureMode.ResetDrawingState();
        }

        private void ParabolicArc_Checked(object sender, RoutedEventArgs e)
        {
            CurvatureMode.CurrentMethod = CurvatureMode.CurvatureMethod.ParabolicArc;
            CurvatureMode.CurrentStep = 0;
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
