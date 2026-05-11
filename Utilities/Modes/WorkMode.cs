using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino.Utilities.Modes
{
    public abstract class WorkMode : INotifyPropertyChanged
    {
        public abstract UserControl CreateControlPanel();

        // Each mode declares the tab header it corresponds to
        public abstract string TabName { get; }

        // Returns true when the mode is at the start of a new operation.
        public virtual bool IsStartingNewOperation => CurrentStep == 0;

        public UndoRedoManager UndoRedoManager { get; set; }
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
            return UndoRedoManager?.Undo();
        }
        // moving to WorkMode so this is a global tool usable across modes
        public virtual WorkOperation Redo()
        {
            return UndoRedoManager?.Redo();
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

        protected virtual void OnOperationUndone(WorkOperation operation) { }
        protected virtual void OnOperationRedone(WorkOperation operation) { }
        public virtual void ClearMetadata() { }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
