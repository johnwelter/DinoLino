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
        public virtual void Reset() 
        {
            DrawnElements.Clear();
        }
        // moving to WorkMode so this is a global tool usable across modes
        public virtual WorkOperation Undo()
        {
            if (History.Count == 0) return null;

            var last = History.Last();
            History.RemoveAt(History.Count - 1);
            RedoStack.Add(last);

            OnOperationUndone(last);
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
            return operation;
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
