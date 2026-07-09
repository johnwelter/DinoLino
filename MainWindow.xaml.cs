using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Policy;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

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
        private double _currentFontSize = 14;  // image translate at pan start

        // Image scale calibration, shared with all modes
        public ScaleCalibration ScaleCalibration = new ScaleCalibration();

        // Current working image in the workspace
        public BitmapSource WorkingImage { get; private set; }

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

        public MainWindow()
        {
            InitializeComponent();

            _tipCycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _tipCycleTimer.Tick += TipCycle_Tick;

            _imageAdjuster.OnAdjustmentApplied = bitmap =>
            {
                UI_WorkImage.Source = bitmap;

                // Convert the adjusted BitmapSource to BitmapImage so OutlineMode can cache its pixels.
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new System.IO.MemoryStream();
                encoder.Save(stream);
                stream.Position = 0;
                var bmi = new BitmapImage();
                bmi.BeginInit();
                bmi.CacheOption = BitmapCacheOption.OnLoad;
                bmi.StreamSource = stream;
                bmi.EndInit();
                bmi.Freeze();
                OutlineMode.SourceImage = bmi;
            };
            SpecimenManager.BindToTextBox(UI_SpecimenNameBox);

            UI_LoadedFileText.DataContext = SpecimenManager;

            UI_ScaleStatus.DataContext = ScaleCalibration;

            // Keyboard shortcuts
            this.PreviewKeyDown += MainWindow_KeyDown;
            UI_WorkCanvas.MouseDown += (s, e) =>
            {
                Keyboard.ClearFocus();
                UI_WorkCanvas.Focus();
            };

            // Initiate curvature mode and make appropriate bindings
            CurvatureMode = new();
            GetAngleMode = new();
            DrawMode = new();
            OutlineMode = new();

            // Wire tip callbacks for all modes
            CurvatureMode.OnTipChanged += UpdateTip;
            GetAngleMode.OnTipChanged += UpdateTip;
            DrawMode.OnTipChanged += UpdateTip;
            OutlineMode.OnTipChanged += UpdateTip;

            // "Find turning angle": show the section oval (and hide the dot cursor while active).
            CurvatureMode.TurningWindowReady += oval =>
            {
                UI_DotCursor.Visibility = Visibility.Collapsed;
                AddElementToWorkSpace(oval);
            };
            CurvatureMode.TurningWindowClear += oval =>
            {
                if (oval != null) UI_WorkCanvas.Children.Remove(oval);
                UI_DotCursor.Visibility = Visibility.Visible;
            };

            OutlineMode.PendingOutlineReady += (newPending, oldPending) =>
            {
                if (oldPending != null)
                    UI_WorkCanvas.Children.Remove(oldPending);
                AddElementToWorkSpace(newPending);
            };

            OutlineMode.OutlineReady += elements =>
            {
                foreach (var el in elements)
                    AddElementToWorkSpace(el);
            };

            OutlineMode.HandPreviewReady += previewLine =>
            {
                AddElementToWorkSpace(previewLine);
            };

            OutlineMode.HandPreviewClear += previewLine =>
            {
                if (previewLine != null)
                    UI_WorkCanvas.Children.Remove(previewLine);
            };

            OutlineMode.EFDPreviewReady += previewLine =>
            {
                AddElementToWorkSpace(previewLine);
            };

            OutlineMode.EFDPreviewClear += () =>
            {
                // Remove any existing EFD preview polylines from the canvas
                for (int i = UI_WorkCanvas.Children.Count - 1; i >= 0; i--)
                {
                    if (UI_WorkCanvas.Children[i] is System.Windows.Shapes.Polyline pl
                        && pl.Stroke == System.Windows.Media.Brushes.DodgerBlue)
                    {
                        UI_WorkCanvas.Children.RemoveAt(i);
                    }
                }
            };

            // Busy indicator: BusyChanged can fire on a background thread, so
            // marshal to the UI thread before touching the overlay. Only the
            // outline modes raise it today, but any WorkMode could reuse the
            // same pattern.
            OutlineMode.BusyChanged += busy =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    UI_BusyIndicator.Visibility = busy
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed));
            };

            // When an outline is measured (Generate Metadata sets HasMetadata), refresh the
            // on-image counter so n_outline ticks up immediately rather than on the next commit.
            OutlineMode.MetadataGenerated += UpdateAttemptCounter;

            // Initialize the global undo redo manager and link to all modes
            UndoRedoManager = new UndoRedoManager();
            CurvatureMode.UndoRedoManager = UndoRedoManager;
            GetAngleMode.UndoRedoManager = UndoRedoManager;
            DrawMode.UndoRedoManager = UndoRedoManager;
            OutlineMode.UndoRedoManager = UndoRedoManager;

            // Attempt counter: recompute from history whenever it changes (commit/undo/redo/clear).
            UndoRedoManager.PropertyChanged += (s, e) => UpdateAttemptCounter();
            UpdateAttemptCounter();

            CurvatureMode.Scale = ScaleCalibration;
            GetAngleMode.Scale = ScaleCalibration;
            DrawMode.Scale = ScaleCalibration;
            OutlineMode.Scale = ScaleCalibration;

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

        private void ControlTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UI_ControlTabs.SelectedItem is not TabItem tab) return;

            // Find the mode whose TabName matches the selected tab header
            CurrentWorkMode = AllWorkModes.FirstOrDefault(m => m.TabName == tab.Header.ToString())
                              ?? CurrentWorkMode;

            UI_ModePanel.Content = CurrentWorkMode.CreateControlPanel();

            CurrentWorkMode?.ResetDrawingState();
            BindUndoRedoMenuItems();

            UpdateTip();
        }

    }
}