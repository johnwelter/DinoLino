using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Base class for interactive work modes.
    /// </summary>
    public abstract class WorkMode : INotifyPropertyChanged
    {
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

        public UndoRedoManager UndoRedoManager { get; set; }

        /// <summary>
        /// Shared scale calibration supplied by the main window.
        /// </summary>
        public ScaleCalibration Scale { get; set; }

        // Cancellation support for long-running mode actions.
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

        /// <summary>
        /// Raised when the mode's tip text changes.
        /// </summary>
        public Action OnTipChanged;
        public virtual string[] GetTips() => new[] { string.Empty };

        /// <summary>
        /// Controls whether previously drawn operations remain visible.
        /// </summary>
        public bool SeePreviousOperations { get; set; } = false;

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

        private readonly ObservableCollection<UIElement> _elementsToRemove = new();
        public ReadOnlyObservableCollection<UIElement> ElementsToRemove { get; }

        public WorkMode()
        {
            ElementsToRemove = new ReadOnlyObservableCollection<UIElement>(_elementsToRemove);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Processes mouse movement in mode-specific coordinates.
        /// </summary>
        public virtual Vector2 ProcessMouseMovement(Vector2 mousePos) { return mousePos; }

        /// <summary>
        /// Processes a click and returns any UI elements created by that click.
        /// </summary>
        public virtual List<UIElement> ProcessClick(Vector2 mousePos) { return null; }

        /// <summary>
        /// Clears transient state used while an operation is in progress.
        /// </summary>
        public virtual void ResetDrawingState() { }

        /// <summary>
        /// Resets mode state for a fresh workspace context.
        /// </summary>
        public virtual void Reset()
        {
            UpdateUndoRedoState();
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

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

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

        /// <summary>
        /// Index of the current step within the active operation.
        /// </summary>
        public int CurrentStep { get; set; } = 0;

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

        public void CommitOperation(WorkOperation operation)
        {
            UndoRedoManager?.Commit(operation);
        }

        /// <summary>
        /// Called after undo/redo changes the active history so the mode can refresh
        /// any state derived from that history.
        /// </summary>
        internal virtual void OnHistoryChanged() { }

        /// <summary>
        /// Placeholder text for scaled values before calibration exists.
        /// </summary>
        protected string ScaledPlaceholder =>
            Scale != null && Scale.IsCalibrated ? "N/A" : "Unscaled";

        /// <summary>
        /// Refreshes any scaled-measurement placeholders shown by the mode.
        /// </summary>
        public virtual void RefreshScalePlaceholders() { }

        public virtual void ClearMetadata() { }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}