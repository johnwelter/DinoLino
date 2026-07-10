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

    public abstract class WorkMode : INotifyPropertyChanged
    {
        public abstract UserControl CreateControlPanel();

        // Each mode declares the tab header it corresponds to
        public abstract string TabName { get; }

        // Returns true when the mode is at the start of a new operation.
        public virtual bool IsStartingNewOperation => CurrentStep == 0;

        // True when the current click is a NON-DESTRUCTIVE probe of an existing
        // operation rather than the start/continuation of a drawing. The input
        // router uses this to skip the "starting a new operation" workspace
        // clear, so probe interactions (e.g. Curvature's Find-Turning-Angle,
        // which returns no new elements to re-add) don't wipe the very shape
        // they're inspecting. Default false; modes with a probe sub-tool
        // override it. MUST stay virtual — CurvatureMode overrides it, and the
        // pair once fell out of sync during a merge, silently disabling the
        // guard.
        public virtual bool IsProbeInteraction => false;

        public UndoRedoManager UndoRedoManager { get; set; }

        // Shared image-scale calibration, injected by MainWindow. Null until set.
        public ScaleCalibration Scale { get; set; }

        // cancellation support
        private CancellationTokenSource _operationCTS;

        public CancellationToken CancellationToken =>
            _operationCTS?.Token ?? CancellationToken.None;

        // begins a new, cancellable operation.
        // automatically cancels any previous operation
        public virtual void BeginOperation()
        {
            CancelCurrentOperation();

            _operationCTS = new CancellationTokenSource();
        }

        // cancels the currently-running operation
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

        // Event for notifying the control panel of tip changes
        public Action OnTipChanged;
        public virtual string[] GetTips() => new[] { string.Empty };

        // Toggling whether or not previous operations are visible
        public bool SeePreviousOperations { get; set; } = false;

        // set default line color
        private Brush _lineColor = Brushes.OrangeRed;

        public Brush LineColor
        {
            get => _lineColor;
            set
            {
                // set safeguard to prevent unnecessary updates
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

        public virtual Vector2 ProcessMouseMovement(Vector2 mousePos) { return mousePos; }
        public virtual List<UIElement> ProcessClick(Vector2 mousePos) { return null; }

        // Resets drawing state only (mid-operation cleanup)
        public virtual void ResetDrawingState() { }

        // full reset
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

        // method to queue elements for removal
        public void AddElementsToRemove(UIElement element)
        {
            _elementsToRemove.Add(element);
        }

        // method to empty the list
        public void ClearElementsToRemove()
        {
            _elementsToRemove.Clear();
        }

        public int CurrentStep { get; set; } = 0;

        private bool _canUndo;
        public bool CanUndo
        {
            get => _canUndo;
            private set
            {
                // set safeguard to prevent unnecessary updates
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
                // set safeguard to prevent unnecessary updates
                if (_canRedo != value)
                {
                    _canRedo = value;
                    OnPropertyChanged();
                }
            }
        }

        protected void UpdateUndoRedoState()
        {
            CanUndo = UndoRedoManager?.CanUndo == true;
            CanRedo = UndoRedoManager?.CanRedo == true;
        }

        public void CommitOperation(WorkOperation operation)
        {
            UndoRedoManager?.Commit(operation);
        }

        // Called by UndoRedoManager after the operation history changes (commit, undo,
        // or redo), so a mode can re-derive any state it computes from that history.
        // Default does nothing; DrawMode uses it to reset its reference-line direction.
        internal virtual void OnHistoryChanged() { }

        // Placeholder for scaled-measurement outputs: "Scale to measure" when the image
        // has no calibration yet, "N/A" once it has been scaled (the user must take a
        // fresh measurement to get a real value).
        protected string ScaledPlaceholder =>
            Scale != null && Scale.IsCalibrated ? "N/A" : "Unscaled";

        // Resets scaled-measurement displays to ScaledPlaceholder. Modes with scaled
        // outputs override this; called whenever the calibration is set or cleared.
        public virtual void RefreshScalePlaceholders() { }

        public virtual void ClearMetadata() { }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}