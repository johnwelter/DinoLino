using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DinoLino.Utilities.Modes
{
    public class WorkMode : INotifyPropertyChanged
    {
        protected List<UIElement> DrawnElements = new List<UIElement>();

        //  storing undo/redo history
        protected List<WorkOperation> History = new List<WorkOperation>();
        protected List<WorkOperation> RedoStack = new List<WorkOperation>();

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
            if (History.Count == 0) return null;

            var last = History.Last();
            History.RemoveAt(History.Count - 1);
            RedoStack.Add(last);

            OnOperationUndone(last);
            UpdateUndoRedoState();
            return last;
        }
        // moving to WorkMode so this is a global tool usable across modes
        public virtual WorkOperation Redo()
        {
            if (RedoStack.Count == 0) return null;

            var operation = RedoStack.Last();
            RedoStack.RemoveAt(RedoStack.Count - 1);
            History.Add(operation);

            OnOperationRedone(operation); // hook for subclasses to update their display values
            UpdateUndoRedoState();
            return operation;
        }
        private bool _canUndo;
        public bool CanUndo
        {
            get => _canUndo;
            private set
            {
                _canUndo = value;
                OnPropertyChanged(nameof(CanUndo));
            }
        }

        private bool _canRedo;
        public bool CanRedo
        {
            get => _canRedo;
            private set
            {
                _canRedo = value;
                OnPropertyChanged(nameof(CanRedo));
            }
        }

        protected void UpdateUndoRedoState()
        {
            CanUndo = History.Count > 0;
            CanRedo = RedoStack.Count > 0;
        }

        protected virtual void OnOperationUndone(WorkOperation operation) { }
        protected virtual void OnOperationRedone(WorkOperation operation) { }

        protected virtual void OnPropertyChanged(string name) 
        {
            if (PropertyChanged != null)
            {
                this.PropertyChanged(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
