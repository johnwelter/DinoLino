using DinoLino.Utilities.Operations;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace DinoLino.Utilities
{
    /// <summary>
    /// A per-specimen snapshot of committed operations.
    /// Each specimen owns one record for the session, and the record stores the specimen's
    /// display name, creation order, and committed operations.
    /// </summary>
    public class SpecimenRecord
    {
        public string SpecimenName { get; set; }
        public List<WorkOperation> Operations { get; set; } = new();

        /// <summary>
        /// The specimen's creation order in the session.
        /// </summary>
        public int Ordinal { get; set; }
    }

    public class UndoRedoManager : INotifyPropertyChanged
    {
        private readonly List<WorkOperation> _history = new();
        private readonly List<WorkOperation> _redoStack = new();

        /// <summary>
        /// Archived records for specimens that are not currently active.
        /// The active specimen's operations live in <see cref="_history"/>.
        /// </summary>
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

            // The newest committed operation defines the active metadata state.
            operation.ApplyMetadataToMode();
            operation.SourceMode?.OnHistoryChanged();
        }

        public WorkOperation Undo()
        {
            if (_history.Count == 0) return null;

            var last = _history.Last();
            _history.RemoveAt(_history.Count - 1);
            _redoStack.Add(last);

            // Restore metadata from the new top of history, or clear it if history is empty.
            if (_history.Count > 0)
            {
                var newTop = _history.Last();
                newTop.ApplyMetadataToMode();
            }
            else
            {
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

            // Redo restores the operation to the active history and re-applies its metadata.
            op.ApplyMetadataToMode();
            op.SourceMode?.OnHistoryChanged();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            return op;
        }

        /// <summary>
        /// Stashes the departing specimen's live history into its record and clears the active stacks.
        /// The record is created on first departure and then updated in place on later departures.
        /// </summary>
        public void StashActiveSpecimen(Specimen departing, string departingName)
        {
            if (departing == null) return;

            var record = departing.Record;
            if (record == null)
            {
                record = new SpecimenRecord { Ordinal = departing.Ordinal };
                departing.Record = record;
            }

            record.SpecimenName = departingName;
            record.Operations = new List<WorkOperation>(_history);

            // Keep archived records in specimen-creation order.
            if (!_archive.Contains(record))
            {
                int at = 0;
                while (at < _archive.Count && _archive[at].Ordinal < record.Ordinal) at++;
                _archive.Insert(at, record);
            }

            var affectedModes = _history.Concat(_redoStack)
                .Select(o => o.SourceMode)
                .Where(m => m != null)
                .Distinct()
                .ToList();

            _history.Clear();
            _redoStack.Clear();

            // Clear the departing specimen's results so the next specimen starts cleanly.
            foreach (var mode in affectedModes)
            {
                mode.ClearMetadata();
                mode.OnHistoryChanged();
            }

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        /// <summary>
        /// Switches the active specimen by stashing the departing one and restoring the arriving one.
        /// </summary>
        public void SwitchActiveSpecimen(Specimen departing, string departingName, Specimen arriving)
        {
            if (arriving == null || ReferenceEquals(departing, arriving)) return;

            StashActiveSpecimen(departing, departingName);

            var record = arriving.Record;
            if (record != null)
            {
                _archive.Remove(record); // Active specimens are represented by _history, not the archive.
                _history.AddRange(record.Operations);
            }

            foreach (var op in _history)
                op.ApplyMetadataToMode();

            foreach (var mode in _history.Select(o => o.SourceMode).Where(m => m != null).Distinct().ToList())
                mode.OnHistoryChanged();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        /// <summary>
        /// Clears the active specimen's live history and redo stack without changing archived specimens.
        /// </summary>
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