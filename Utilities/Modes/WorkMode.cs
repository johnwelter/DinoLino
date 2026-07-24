using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Base class for interactive work modes. Owns the click/step lifecycle, the
    /// element accumulator for the operation being drawn, the canvas element
    /// factories, scaled-measurement formatting, history-driven averages, and the
    /// tip lines shared by every mode.
    /// </summary>
    public abstract class WorkMode : INotifyPropertyChanged
    {
        #region Identity and lifecycle

        public abstract UserControl CreateControlPanel();

        /// <summary>
        /// The tab header used to match this mode to its UI tab.
        /// </summary>
        public abstract string TabName { get; }

        /// <summary>
        /// True when the mode is ready to begin a new operation.
        /// </summary>
        public virtual bool IsStartingNewOperation => CurrentStep == 0;

        /// <summary>
        /// True for probe-style interactions that inspect or adjust an existing operation
        /// instead of starting a new one.
        /// </summary>
        public virtual bool IsProbeInteraction => false;

        /// <summary>
        /// Index of the current step within the active operation.
        /// </summary>
        public int CurrentStep { get; set; } = 0;

        public UndoRedoManager UndoRedoManager { get; set; }

        /// <summary>
        /// Shared scale calibration supplied by the main window.
        /// </summary>
        public ScaleCalibration Scale { get; set; }

        /// <summary>
        /// Controls whether previously drawn operations remain visible.
        /// </summary>
        public bool SeePreviousOperations { get; set; } = false;

        public WorkMode()
        {
            ElementsToRemove = new ReadOnlyObservableCollection<UIElement>(_elementsToRemove);
        }

        /// <summary>
        /// Processes mouse movement in mode-specific coordinates.
        /// </summary>
        public virtual Vector2 ProcessMouseMovement(Vector2 mousePos) { return mousePos; }

        /// <summary>
        /// Processes a click and returns any UI elements created by that click.
        /// </summary>
        public virtual List<UIElement> ProcessClick(Vector2 mousePos) { return null; }

        /// <summary>
        /// Clears transient state used while an operation is in progress. Called on
        /// tab switch, so it must leave committed results and any retained probe
        /// target alone.
        /// </summary>
        public virtual void ResetDrawingState() { }

        /// <summary>
        /// Returns the mode to a fresh workspace context: refreshes undo/redo state,
        /// clears displayed results, and drops any in-progress drawing. Overrides
        /// should call base and then clear state specific to the mode.
        /// </summary>
        public virtual void Reset()
        {
            UpdateUndoRedoState();
            ClearMetadata();
            ResetDrawingState();
        }

        /// <summary>
        /// Clears the results the control panel displays.
        /// </summary>
        public virtual void ClearMetadata() { }

        #endregion

        #region Cancellable operations

        private CancellationTokenSource _operationCTS;

        public CancellationToken CancellationToken =>
            _operationCTS?.Token ?? CancellationToken.None;

        /// <summary>
        /// Starts a new cancellable operation and cancels any previous one.
        /// </summary>
        public virtual void BeginOperation()
        {
            CancelCurrentOperation();
            _operationCTS = new CancellationTokenSource();
        }

        /// <summary>
        /// Cancels the current operation, if one is active.
        /// </summary>
        public virtual void CancelCurrentOperation()
        {
            if (_operationCTS != null)
            {
                if (!_operationCTS.IsCancellationRequested)
                    _operationCTS.Cancel();

                _operationCTS.Dispose();
                _operationCTS = null;
            }
        }

        #endregion

        #region Workspace elements

        private Brush _lineColor = Brushes.OrangeRed;

        /// <summary>
        /// Current drawing color for newly created elements.
        /// </summary>
        public Brush LineColor
        {
            get => _lineColor;
            set
            {
                if (_lineColor != value)
                {
                    _lineColor = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Elements drawn by the operation in progress. CommitCurrentOperation moves
        /// them onto the committed WorkOperation and empties the list.
        /// </summary>
        protected readonly List<UIElement> CurrentOperation = new();

        private readonly ObservableCollection<UIElement> _elementsToRemove = new();
        public ReadOnlyObservableCollection<UIElement> ElementsToRemove { get; }

        /// <summary>
        /// Queues an element to be removed from the workspace.
        /// </summary>
        public void AddElementsToRemove(UIElement element)
        {
            _elementsToRemove.Add(element);
        }

        /// <summary>
        /// Clears the pending removal list.
        /// </summary>
        public void ClearElementsToRemove()
        {
            _elementsToRemove.Clear();
        }

        protected Line MakeLine(Vector2 a, Vector2 b, double thickness = 2) => new()
        {
            Stroke = LineColor,
            StrokeThickness = thickness,
            X1 = a.X,
            Y1 = a.Y,
            X2 = b.X,
            Y2 = b.Y,
        };

        /// <summary>
        /// Bold canvas label in the current line color, offset from the given point.
        /// </summary>
        protected TextBlock MakeLabel(string text, Vector2 pos, double fontSize = 28,
            double offsetX = 5, double offsetY = 5)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = LineColor,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center
            };

            Canvas.SetLeft(label, pos.X + offsetX);
            Canvas.SetTop(label, pos.Y + offsetY);
            return label;
        }

        /// <summary>
        /// Filled marker dot in the current line color, centred on the given point.
        /// </summary>
        protected Ellipse MakeDot(Vector2 pos, double diameter = 8)
        {
            var dot = new Ellipse
            {
                Fill = LineColor,
                Width = diameter,
                Height = diameter
            };

            Canvas.SetLeft(dot, pos.X - diameter / 2);
            Canvas.SetTop(dot, pos.Y - diameter / 2);
            return dot;
        }

        #endregion

        #region History

        public void CommitOperation(WorkOperation operation)
        {
            UndoRedoManager?.Commit(operation);
        }

        /// <summary>
        /// Stamps the operation with this mode and the accumulated elements, commits
        /// it, and empties the accumulator ready for the next operation.
        /// </summary>
        protected void CommitCurrentOperation(WorkOperation operation)
        {
            if (operation == null) return;

            operation.SourceMode = this;
            operation.Elements = new List<UIElement>(CurrentOperation);
            CommitOperation(operation);
            CurrentOperation.Clear();
        }

        private bool _canUndo;
        public bool CanUndo
        {
            get => _canUndo;
            private set
            {
                if (_canUndo != value)
                {
                    _canUndo = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _canRedo;
        public bool CanRedo
        {
            get => _canRedo;
            private set
            {
                if (_canRedo != value)
                {
                    _canRedo = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Synchronizes the mode's undo/redo state with the shared manager.
        /// </summary>
        protected void UpdateUndoRedoState()
        {
            CanUndo = UndoRedoManager?.CanUndo == true;
            CanRedo = UndoRedoManager?.CanRedo == true;
        }

        /// <summary>
        /// Called after undo/redo changes the active history so the mode can refresh
        /// any state derived from that history. Overrides must call base to keep the
        /// averages in sync.
        /// </summary>
        internal virtual void OnHistoryChanged()
        {
            RecomputeAverages();
        }

        /// <summary>
        /// Raises PropertyChanged for the mode's Avg* properties. Modes that display
        /// averages override this; the base call from OnHistoryChanged keeps them
        /// current through commit, undo, redo, and clear.
        /// </summary>
        protected virtual void RecomputeAverages() { }

        #endregion

        #region Scaled measurements

        /// <summary>
        /// Placeholder shown in place of a scaled value when the mode holds no
        /// measurement, or holds one the image calibration cannot convert.
        /// </summary>
        protected string ScaledPlaceholder =>
            Scale != null && Scale.IsCalibrated ? "N/A" : "Unscaled";

        /// <summary>
        /// True when the shared calibration can convert canvas measurements.
        /// </summary>
        protected bool IsScaleUsable => Scale != null && Scale.IsCalibrated;

        /// <summary>
        /// Formats a canvas-space length in calibrated units. hasMeasurement is false
        /// before anything has been measured, which yields the placeholder instead.
        /// </summary>
        protected string FormatScaledLength(double canvasLength, bool hasMeasurement = true) =>
            hasMeasurement && IsScaleUsable
                ? $"{Scale.ToUnits(canvasLength):F2} {Scale.Unit}"
                : ScaledPlaceholder;

        /// <summary>
        /// Formats a canvas-space area in calibrated square units. hasMeasurement is
        /// false before anything has been measured, which yields the placeholder.
        /// </summary>
        protected string FormatScaledArea(double canvasArea, bool hasMeasurement = true) =>
            hasMeasurement && IsScaleUsable
                ? $"{Scale.ToUnitsArea(canvasArea):F2} {Scale.Unit}\u00B2"
                : ScaledPlaceholder;

        /// <summary>
        /// Re-derives every scaled value the mode displays from the canvas-space
        /// measurements it has stored. Called when the calibration is set, changed,
        /// or cleared, so existing measurements convert to the new units rather than
        /// reverting to the placeholder.
        /// </summary>
        public virtual void RefreshScalePlaceholders() { }

        #endregion

        #region Averages

        /// <summary>
        /// Mean of a value series to one decimal place, or "N/A" with no attempts.
        /// </summary>
        protected static string FormatAverage(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count == 0) return "N/A";
            return Math.Round(list.Average(), 1).ToString();
        }

        /// <summary>
        /// Mean of a canvas-space length series in calibrated units, or "N/A" with no
        /// attempts or no calibration.
        /// </summary>
        protected string FormatScaledLengthAverage(IEnumerable<double> canvasLengths)
        {
            var list = canvasLengths.ToList();
            if (list.Count == 0 || !IsScaleUsable) return "N/A";
            return $"{Scale.ToUnits(list.Average()):F2} {Scale.Unit}";
        }

        /// <summary>
        /// Mean of a canvas-space area series in calibrated square units, or "N/A"
        /// with no attempts or no calibration.
        /// </summary>
        protected string FormatScaledAreaAverage(IEnumerable<double> canvasAreas)
        {
            var list = canvasAreas.ToList();
            if (list.Count == 0 || !IsScaleUsable) return "N/A";
            return $"{Scale.ToUnitsArea(list.Average()):F2} {Scale.Unit}\u00B2";
        }

        /// <summary>
        /// Committed operations of one kind from the live history, empty when no
        /// history is attached.
        /// </summary>
        protected IEnumerable<TOperation> OperationsOfKind<TOperation>() where TOperation : WorkOperation =>
            UndoRedoManager?.History.OfType<TOperation>() ?? Enumerable.Empty<TOperation>();

        #endregion

        #region Tips

        public Action OnTipChanged;

        protected const string TipUndo =
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.";
        protected const string TipRedo =
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.";
        protected const string TipClear =
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.";
        protected const string TipOpenImage =
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.";
        protected const string TipZoom =
            "💡 Zoom in or out using the scroll wheel.";
        protected const string TipPan =
            "💡 Press 'Ctrl' and left click to drag the image.";
        protected const string TipHelp =
            "💡 The user guide and software information can be found in the Help menu.";
        protected const string TipToggleTips =
            "💡 Toggle tip visibility in the View menu.";

        /// <summary>
        /// Trailer appended by BuildTips. Modes needing a different trailer compose
        /// their arrays from the Tip constants directly.
        /// </summary>
        private static readonly string[] CommonTipTail =
        {
            TipUndo, TipRedo, TipClear, TipOpenImage, TipZoom, TipPan, TipHelp, TipToggleTips
        };

        /// <summary>
        /// Builds a tip list from the mode's own lines followed by the shared trailer.
        /// </summary>
        protected static string[] BuildTips(params string[] modeTips) =>
            modeTips.Concat(CommonTipTail).ToArray();

        public virtual string[] GetTips() => new[] { string.Empty };

        #endregion

        #region Property change notification

        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}