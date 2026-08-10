using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DinoLino
{
    /// <summary>
    /// Main application window and interaction hub for specimen loading, mode switching, and workspace updates.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Shared undo/redo state for the active specimen workflow.
        public UndoRedoManager UndoRedoManager;

        // Tracks opened specimens, the active specimen, and cache-related UI state.
        public SpecimenManager SpecimenManager = new SpecimenManager();

        private void SpecimenCount_Up(object sender, RoutedEventArgs e)
        {
            var departing = SpecimenManager.CurrentSpecimen;
            ReloadSpecimen(departing, SpecimenManager.MoveNext());
        }

        private void SpecimenCount_Down(object sender, RoutedEventArgs e)
        {
            var departing = SpecimenManager.CurrentSpecimen;
            ReloadSpecimen(departing, SpecimenManager.MovePrevious());
        }

        /// <summary>
        /// Loads the selected specimen into the workspace and restores its associated operation history.
        /// </summary>
        private void ReloadSpecimen(Specimen departing, Specimen arriving)
        {
            if (arriving?.Image == null) return; // No eligible specimen was found in that direction.

            SetWorkspaceImage(arriving.Image, arriving.FileName, registerAsNewSpecimen: false);

            // Load the arriving specimen's operation context after the new image is in place.
            UndoRedoManager.SwitchActiveSpecimen(
                departing,
                SpecimenManager.NameOf(departing),
                arriving);

            // Metadata preview/results belong to the specimen that was just left.
            // After the arriving specimen is fully active, return Outline mode to
            // Automated Outline if Generate Metadata had been selected.
            ResetOutlineToolForNewSpecimen();
        }

        /// <summary>
        /// Generate Metadata applies to the outline shown for the prior specimen.
        /// When the active specimen changes, switch an active Outline panel back to
        /// Automated Outline rather than carrying metadata-preview mode forward.
        /// </summary>
        private void ResetOutlineToolForNewSpecimen()
        {
            // The user must actually be viewing the Outline tab. Do not change an
            // Outline-mode setting merely because the OutlineMode object exists.
            if (!ReferenceEquals(CurrentWorkMode, OutlineMode))
                return;

            if (!OutlineMode.OutlineMetadataMode)
                return;

            // Explicitly update both bound properties. This ensures that the backing
            // mode state and the OutlineTool radio-button group agree even before WPF
            // has completed its binding/UI update cycle.
            OutlineMode.OutlineMetadataMode = false;
            OutlineMode.DrawOutlineMode = true;
        }

        // Selected workspace font settings.
        private FontFamily _currentFont = new FontFamily("Arial");
        private double _currentFontSize = 14;

        // Shared image scaling state used by all work modes.
        public ScaleCalibration ScaleCalibration = new ScaleCalibration();

        // The image currently shown in the workspace.
        public BitmapSource WorkingImage { get; private set; }

        // Work modes available in the application.
        public CurvatureMode CurvatureMode;
        public GetAngleMode GetAngleMode;
        public DrawMode DrawMode;
        public OutlineMode OutlineMode;

        // All modes, used for tab switching and shared updates.
        public List<WorkMode> AllWorkModes;

        // Currently selected work mode.
        public WorkMode CurrentWorkMode;

        // Cursor shown over the workspace for certain interactive tools.
        public Ellipse UI_DotCursor;

        public MainWindow()
        {
            InitializeComponent();

            _tipCycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _tipCycleTimer.Tick += TipCycle_Tick;

            _imageAdjuster.OnAdjustmentApplied = bitmap =>
            {
                UI_WorkImage.Source = bitmap;

                // Convert the adjusted image into a frozen BitmapImage so OutlineMode can cache pixels from it.
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

            // Global keyboard shortcuts are handled at the window level.
            this.PreviewKeyDown += MainWindow_KeyDown;

            // Clicking the workspace clears focus so keyboard shortcuts continue to work.
            UI_WorkCanvas.MouseDown += (s, e) =>
            {
                Keyboard.ClearFocus();
                UI_WorkCanvas.Focus();
            };

            CurvatureMode = new();
            GetAngleMode = new();
            DrawMode = new();
            OutlineMode = new();

            // Update the tip text whenever a mode changes its guidance message.
            CurvatureMode.OnTipChanged += UpdateTip;
            GetAngleMode.OnTipChanged += UpdateTip;
            DrawMode.OnTipChanged += UpdateTip;
            OutlineMode.OnTipChanged += UpdateTip;

            // Turning-angle mode draws a temporary oval and hides the dot cursor while active.
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

            // The EFD overlay is no longer displayed in the workspace because it is shown in the Outline tab.
            // The events remain available, but nothing is subscribed here.
            // If the overlay returns, reattach the preview events to the workspace.

            // Busy state is raised from background work, so marshal the UI update to the dispatcher.
            OutlineMode.BusyChanged += busy =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    UI_BusyIndicator.Visibility = busy
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed));
            };

            // Refresh the attempt counter whenever outline metadata is generated.
            OutlineMode.MetadataGenerated += UpdateAttemptCounter;

            // "Commit Outline to History" in the Outline panel.
            OutlineMode.CommitOutlineRequested += OutlineCommit_Requested;

            UndoRedoManager = new UndoRedoManager();
            CurvatureMode.UndoRedoManager = UndoRedoManager;
            GetAngleMode.UndoRedoManager = UndoRedoManager;
            DrawMode.UndoRedoManager = UndoRedoManager;
            OutlineMode.UndoRedoManager = UndoRedoManager;

            // The attempt counter depends on the current undo/redo history.
            UndoRedoManager.PropertyChanged += (s, e) => UpdateAttemptCounter();
            UpdateAttemptCounter();

            CurvatureMode.Scale = ScaleCalibration;
            GetAngleMode.Scale = ScaleCalibration;
            DrawMode.Scale = ScaleCalibration;
            OutlineMode.Scale = ScaleCalibration;

            AllWorkModes = new List<WorkMode> { CurvatureMode, GetAngleMode, DrawMode, OutlineMode };
            CurrentWorkMode = CurvatureMode;

            UI_ModePanel.Content = CurrentWorkMode.CreateControlPanel();

            BindUndoRedoMenuItems();

            // Initialize the workspace transforms before any image interaction begins.
            UI_WorkImage.InitializeGroupTransform(new Point(0, 0));
            UI_WorkBorder.InitializeGroupTransform(new Point(0, 0));

            // Cursor used by tools that need a visible point marker.
            UI_DotCursor = new Ellipse
            {
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Height = 10,
                Width = 10
            };
            AddElementToWorkSpace(UI_DotCursor);

            CurrentWorkMode.Reset();

            // Hook up the workspace scrollbars and the mini-map (MainWindow_Navigation.cs).
            // Must run after the workspace transforms are initialized above.
            InitializeNavigationAids();
        }

        private void ControlTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UI_ControlTabs.SelectedItem is not TabItem tab) return;

            // Match the selected tab header to the corresponding work mode.
            CurrentWorkMode = AllWorkModes.FirstOrDefault(m => m.TabName == tab.Header.ToString())
                              ?? CurrentWorkMode;

            UI_ModePanel.Content = CurrentWorkMode.CreateControlPanel();

            // Reset mode-local drawing state when switching tabs.
            CurrentWorkMode?.ResetDrawingState();
            BindUndoRedoMenuItems();

            UpdateTip();
        }
    }
}