using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
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

        // Undo Redo Manager fields 
        public UndoRedoManager UndoRedoManager;

        // Specimen Manager fields
        public SpecimenManager SpecimenManager = new SpecimenManager();
        private void SpecimenCount_Up(object sender, RoutedEventArgs e) => SpecimenManager.Increment();
        private void SpecimenCount_Down(object sender, RoutedEventArgs e) => SpecimenManager.Decrement();

        // store selected font type
        private FontFamily _currentFont = new FontFamily("Arial");

        // store selected font size
        private double _currentFontSize = 14;

        // image adjuster fields
        private ImageAdjuster _imageAdjuster = new ImageAdjuster();

        // Picture adjustment state
        private double _currentContrast = 0;
        private double _currentBrightness = 0;
        private double _currentSaturation = 0;

        // Current working image in the workspace
        public BitmapImage WorkingImage;

        // Work Modes
        public CurvatureMode CurvatureMode;
        public GetAngleMode GetAngleMode;
        public DrawMode DrawMode;
        public OutlineMode OutlineMode;

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
            SpecimenManager.BindToTextBox(UI_SpecimenNameBox);

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
            GetAngleMode = new();    
            DrawMode = new();
            OutlineMode = new();


            // Initialize the global undo redo manager and link to all modes
            UndoRedoManager = new UndoRedoManager();
            CurvatureMode.UndoRedoManager = UndoRedoManager;
            GetAngleMode.UndoRedoManager = UndoRedoManager;
            DrawMode.UndoRedoManager = UndoRedoManager;
            OutlineMode.UndoRedoManager = UndoRedoManager;

            // Initialize list of all modes
            AllWorkModes = new List<WorkMode> { CurvatureMode, GetAngleMode, DrawMode, OutlineMode };

            // Set the current work mode to update
            CurrentWorkMode = CurvatureMode;

            UI_ModePanel.Content = CurrentWorkMode.CreateControlPanel();

            BindUndoRedoMenuItems();

            // Initialize image zoom
            UI_WorkImage.InitializeGroupTransform(new Point(0, 0));
            UI_WorkBorder.InitializeGroupTransform(new Point(0, 0));

            // Create cursor for following mouse during angle analytics
            UI_DotCursor = new Ellipse
            {
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Height = 10,
                Width = 10
            };
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

                WorkingImage = new BitmapImage(new Uri(openFileDialog.FileName, UriKind.RelativeOrAbsolute));
                UI_WorkImage.Source = WorkingImage;
                SpecimenManager.OnImageOpened();
                ResetWorkSpaceZoom();
                ClearWorkspace();
                _imageAdjuster.CacheImage(WorkingImage);
                OutlineMode.SourceImage = WorkingImage;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(SyncOutlineImageTransform));
            }
        }

        private void SyncOutlineImageTransform()
        {
            if (WorkingImage == null) return;
            double displayW = UI_WorkImage.ActualWidth;
            double displayH = UI_WorkImage.ActualHeight;
            if (displayW <= 0 || displayH <= 0) return;

            OutlineMode.ScaleX = displayW / WorkingImage.PixelWidth;
            OutlineMode.ScaleY = displayH / WorkingImage.PixelHeight;

            // Offset of the image within the canvas
            var imagePos = UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkCanvas);
            OutlineMode.OffsetX = imagePos.X;
            OutlineMode.OffsetY = imagePos.Y;
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

        private void Menu_Font(object sender, RoutedEventArgs e)
        {
            FontWindow fontWindow = new FontWindow(_currentFontSize, _currentFont);
            fontWindow.FontSize = _currentFontSize;
            fontWindow.FontFamily = _currentFont;

            fontWindow.OnFontSizeChanged = size =>
            {
                _currentFontSize = size;
                TextElement.SetFontSize(UI_ControlPanel, size);
                fontWindow.FontSize = size;
            };

            fontWindow.OnFontFamilyChanged = family =>
            {
                _currentFont = family;
                TextElement.SetFontFamily(UI_ControlPanel, family);
                fontWindow.FontFamily = family;
            };

            fontWindow.Show(); // use Show() instead of ShowDialog() so the user can adjust font while seeing the main window update live
        }
        private void Menu_PictureAdjustment(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PictureAdjustmentWindow adjustWindow = new PictureAdjustmentWindow(_currentContrast, _currentBrightness, _currentSaturation);
            adjustWindow.FontSize = _currentFontSize;
            adjustWindow.FontFamily = _currentFont;

            adjustWindow.OnAdjustmentChanged = (contrast, brightness, saturation) =>
            {
                _currentContrast = contrast;
                _currentBrightness = brightness;
                _currentSaturation = saturation;
                _imageAdjuster.RequestAdjustment(
                    contrast / 100.0,
                    brightness / 100.0,
                    saturation / 100.0);
            };

            adjustWindow.Show();
        }

        private void Menu_DownSample(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long originalPixelCount = (long)WorkingImage.PixelWidth * WorkingImage.PixelHeight;

            DownSampleWindow sampleWindow = new DownSampleWindow(originalPixelCount);
            sampleWindow.FontSize = _currentFontSize;
            sampleWindow.FontFamily = _currentFont;

            sampleWindow.OnPixelsChanged = targetPixels =>
            {
                var result = _imageAdjuster.DownSample(targetPixels);
                if (result != null)
                    UI_WorkImage.Source = result;
                else
                    UI_WorkImage.Source = WorkingImage;
            };

            sampleWindow.Show();
        }
        #endregion

        #region Global Toolbar Functions
        private void GlobalTools_Clear(object sender, RoutedEventArgs e)
        {
            ClearWorkspace();
        }

        private void ControlTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UI_ControlTabs.SelectedItem is not TabItem tab) return;

            // Find the mode whose TabName matches the selected tab header
            CurrentWorkMode = AllWorkModes.FirstOrDefault(m => m.TabName == tab.Header.ToString())
                              ?? CurrentWorkMode;

            UI_ModePanel.Content = CurrentWorkMode.CreateControlPanel();

            CurrentWorkMode?.ResetDrawingState();
            BindUndoRedoMenuItems();
        }
        #endregion

        #region Work Space UI Functions

        private void WorkSpace_Click(object sender, MouseButtonEventArgs e)
        {
            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));

            if (CurrentWorkMode is OutlineMode)
                SyncOutlineImageTransform();

            if (!CurrentWorkMode.SeePreviousOperations && CurrentWorkMode.IsStartingNewOperation)
            {
                // don't use Children.Clear() because we want to keep the DotCursor
                ClearWorkspaceVisualsOnly();
            }

            if (e.ClickCount == 2)
            {
                RemovePendingElements();

                foreach (UIElement element in CurrentWorkMode.ProcessDoubleClick(mousePos))
                    AddElementToWorkSpace(element);
                return;
            }

            RemovePendingElements();

            foreach (UIElement element in CurrentWorkMode.ProcessClick(mousePos))
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
        #endregion
    }
}
