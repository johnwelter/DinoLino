using DinoLino.Utilities.Operations;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace DinoLino.Utilities
{
    public class UndoRedoManager : INotifyPropertyChanged
    {
        private readonly List<WorkOperation> _history = new();
        private readonly List<WorkOperation> _redoStack = new();
        public IReadOnlyList<WorkOperation> History => _history;
        public IReadOnlyList<WorkOperation> RedoStack => _redoStack;

        public bool CanUndo => _history.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public event PropertyChangedEventHandler PropertyChanged;

        public void Commit(WorkOperation operation)
        {
            if (operation == null) return;

            _redoStack.Clear();
            _history.Add(operation);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));

            // Optionally notify mode to apply metadata
            operation.ApplyMetadataToMode();
        }

        public WorkOperation Undo()
        {
            if (_history.Count == 0) return null;

            var last = _history.Last();
            _history.RemoveAt(_history.Count - 1);
            _redoStack.Add(last);

            // Apply metadata from new top of history
            if (_history.Count > 0)
            {
                var newTop = _history.Last();
                newTop.ApplyMetadataToMode();
            }
            else
            {
                // No history left, clear metadata in all modes
                last.SourceMode?.ClearMetadata();
            }

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            return last;
        }

        public WorkOperation Redo()
        {
            if (_redoStack.Count == 0) return null;

            var op = _redoStack.Last();
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _history.Add(op);

            // Apply metadata from redone operation
            op.ApplyMetadataToMode();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            return op;
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}