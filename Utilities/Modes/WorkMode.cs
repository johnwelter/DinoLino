using DinoLino.DataTypes;
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

        public event PropertyChangedEventHandler PropertyChanged;

        public virtual Vector2 ProcessMouseMovement(Vector2 mousePos) { return mousePos; }
        public virtual List<UIElement> ProcessClick(Vector2 mousePos) { return null; }
        public virtual void Reset() 
        {
            DrawnElements.Clear();
        }
        // moving to WorkMode so this is a global tool usable across modes
        public virtual UndoResult Undo()
        {
            return null;
        }
        // moving to WorkMode so this is a global tool usable across modes
        public virtual UndoResult Redo()
        {
            return null;
        }

        protected virtual void OnPropertyChanged(string name) 
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
