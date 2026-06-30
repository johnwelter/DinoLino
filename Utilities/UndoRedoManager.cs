using DinoLino.Utilities.Operations;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace DinoLino.Utilities
{
    // A frozen, per-specimen snapshot of the operations that were committed while that
    // specimen was active. Created on image-open and never mutated again — archived
    // specimens are read-only history.
    public class SpecimenRecord
    {
        public string SpecimenName { get; set; }
        public List<WorkOperation> Operations { get; set; } = new();
    }

    public class UndoRedoManager : INotifyPropertyChanged
    {
        private readonly List<WorkOperation> _history = new();
        private readonly List<WorkOperation> _redoStack = new();

        // Permanent, ordered archive of past specimens. Survives Clear()/ArchiveAndReset
        // so the History window can show specimens whose live operations are long gone.
        private readonly List<SpecimenRecord> _archive = new();
        public IReadOnlyList<SpecimenRecord> Archive => _archive;

        public IReadOnlyList<WorkOperation> History => _history;
        public IReadOnlyList<WorkOperation> RedoStack => _redoStack;
        public WorkOperation CurrentOperation => _history.Count > 0 ? _history[_history.Count - 1] : null;

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
            operation.SourceMode?.OnHistoryChanged();
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

            last.SourceMode?.OnHistoryChanged();
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
            op.SourceMode?.OnHistoryChanged();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            return op;
        }

        // Freezes the current live operations into a permanent SpecimenRecord under
        // `outgoingSpecimenName`, then clears live history and redo. Called on image-open,
        // BEFORE the specimen counter is incremented, so the record carries the name of the
        // specimen that produced the data. Archives even when there are no operations, so
        // every specimen gets a labelled block in the History window. After this, the
        // operation counter reads zero (next attempt is #1) and undo can no longer reach the
        // now-frozen specimen.
        public void ArchiveAndReset(string outgoingSpecimenName)
        {
            _archive.Add(new SpecimenRecord
            {
                SpecimenName = outgoingSpecimenName,
                Operations = new List<WorkOperation>(_history)
            });

            var affectedModes = _history.Concat(_redoStack)
                .Select(o => o.SourceMode)
                .Where(m => m != null)
                .Distinct()
                .ToList();

            _history.Clear();
            _redoStack.Clear();

            foreach (var mode in affectedModes)
                mode.OnHistoryChanged();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        // Clears all live history and redo state — a hard reset used by "Clear All" / Ctrl+C.
        // Does NOT touch the specimen archive: clearing the current working set shouldn't
        // erase previously recorded specimens. Raises CanUndo/CanRedo so bound UI refreshes.
        public void Clear()
        {
            var affectedModes = _history.Concat(_redoStack)
                .Select(o => o.SourceMode)
                .Where(m => m != null)
                .Distinct()
                .ToList();

            _history.Clear();
            _redoStack.Clear();

            foreach (var mode in affectedModes)
                mode.OnHistoryChanged();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}