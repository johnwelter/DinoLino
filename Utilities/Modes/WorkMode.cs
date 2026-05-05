using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DinoLino.Utilities.Modes
{
    public class WorkMode : INotifyPropertyChanged
    {
        // Toggling whether or not previous operations are visible
        public bool SeePreviousOperations { get; set; } = true;

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

        protected ObservableCollection<UIElement> DrawnElements {get;} = new();
        private readonly ObservableCollection<UIElement> _elementsToRemove = new();

        public ReadOnlyObservableCollection<UIElement> ElementsToRemove { get; }

        public WorkMode()
        {
            ElementsToRemove = new ReadOnlyObservableCollection<UIElement>(_elementsToRemove);
        }


        //  storing undo/redo history
        protected List<WorkOperation> _history = new List<WorkOperation>();
        public IReadOnlyList<WorkOperation> History => _history;
        protected List<WorkOperation> _redoStack = new List<WorkOperation>();

        public event PropertyChangedEventHandler PropertyChanged;

        public virtual Vector2 ProcessMouseMovement(Vector2 mousePos) { return mousePos; }
        public virtual List<UIElement> ProcessClick(Vector2 mousePos) { return null; }
        // double-click for finalizing splines (or anything else)
        public virtual List<UIElement> ProcessDoubleClick(Vector2 mousePos) { return new List<UIElement>(); }

        // Resets drawing state only (mid-operation cleanup)
        public virtual void ResetDrawingState() { }
        
        // full reset
        public virtual void Reset() 
        {
            DrawnElements.Clear();
            UpdateUndoRedoState();
        }
        
        // moving to WorkMode so this is a global tool usable across modes
        public virtual WorkOperation Undo()
        {
            if (_history.Count == 0) return null;

            var last = _history.Last();
            _history.RemoveAt(_history.Count - 1);
            _redoStack.Add(last);

            OnOperationUndone(last);
            UpdateUndoRedoState();
            return last;
        }
        // moving to WorkMode so this is a global tool usable across modes
        public virtual WorkOperation Redo()
        {
            if (_redoStack.Count == 0) return null;

            var operation = _redoStack.Last();
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _history.Add(operation);

            OnOperationRedone(operation); // hook for subclasses to update their display values
            UpdateUndoRedoState();
            return operation;
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
            CanUndo = _history.Count > 0;
            CanRedo = _redoStack.Count > 0;
        }

        protected void CommitOperation(WorkOperation operation)
        {
            _redoStack.Clear();
            _history.Add(operation);
            UpdateUndoRedoState();
        }

        protected virtual void OnOperationUndone(WorkOperation operation) { }
        protected virtual void OnOperationRedone(WorkOperation operation) { }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
