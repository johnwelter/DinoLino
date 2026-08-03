using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace DinoLino.Utilities
{
    // A per-specimen snapshot of committed operations.
    public class SpecimenRecord
    {
        public string SpecimenName { get; set; }
        public List<WorkOperation> Operations { get; set; } = new();

        // Creation order of the owning specimen.
        public int Ordinal { get; set; }
    }

    public class UndoRedoManager : INotifyPropertyChanged
    {
        private readonly List<WorkOperation> _history = new();
        private readonly List<WorkOperation> _redoStack = new();

        // Ordered records of every INACTIVE specimen (the active specimen's operations
        // are the live _history; its record — if it has ever departed — is parked on
        // its Specimen and re-inserted here at the next departure).
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

        // ---- Per-specimen history contexts ----
        // The live history always holds the ACTIVE specimen's working set.

        // Saves the live working set into the departing specimen's record and clears
        // the live lists (the redo stack does not survive leaving a specimen).
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

            // Re-insert in specimen-creation order (the record was taken out of the
            // archive when this specimen became active).
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

            // Blank the departing specimen's on-panel results so the next specimen
            // never shows numbers that belong to this one.
            foreach (var mode in affectedModes)
            {
                mode.ClearMetadata();
                mode.OnHistoryChanged();
            }

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        // Arrow navigation: stash the departing specimen, then restore the arriving
        // specimen's record as the live working set and re-apply its metadata to the
        // mode panels.
        public void SwitchActiveSpecimen(Specimen departing, string departingName, Specimen arriving)
        {
            if (arriving == null || ReferenceEquals(departing, arriving)) return;

            StashActiveSpecimen(departing, departingName);

            var record = arriving.Record;
            if (record != null)
            {
                _archive.Remove(record);   // active specimen lives in _history, not the archive
                _history.AddRange(record.Operations);
            }

            foreach (var op in _history)
                op.ApplyMetadataToMode();

            foreach (var mode in _history.Select(o => o.SourceMode).Where(m => m != null).Distinct().ToList())
                mode.OnHistoryChanged();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        // Clears all live history and redo state — a hard reset used by "Clear All" /
        // Ctrl+C.
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

        // ---- Editing (Batch Workshop) ----
        // Unlike Undo, these deletions are permanent: the operation is dropped from
        // the redo stack too, so it cannot be brought back.

        /// Removes one operation wherever it lives: the live working set, the redo
        /// stack, or an archived specimen's record. Returns true when it was found.
        public bool RemoveOperation(WorkOperation operation)
        {
            if (operation == null) return false;

            var mode = operation.SourceMode;
            bool removed = _history.Remove(operation);

            // A deleted operation must not survive on the redo stack.
            if (_redoStack.Remove(operation)) removed = true;

            if (!removed)
            {
                foreach (var record in _archive)
                {
                    if (record.Operations.Remove(operation))
                    {
                        removed = true;
                        break;
                    }
                }
            }

            if (!removed) return false;

            RefreshAfterEdit(mode);
            return true;
        }

        /// Removes every operation belonging to an archived specimen, and the record
        /// itself, so the specimen disappears from history and exports.
        public void RemoveArchivedSpecimen(SpecimenRecord record)
        {
            if (record == null) return;

            var affectedModes = record.Operations
                .Select(o => o.SourceMode)
                .Where(m => m != null)
                .Distinct()
                .ToList();

            record.Operations.Clear();
            _archive.Remove(record);

            foreach (var mode in affectedModes)
            {
                mode.ClearMetadata();
                mode.OnHistoryChanged();
            }

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        /// Removes every operation of the ACTIVE specimen. Its live working set is the
        /// same list Clear() empties, so this is that same hard reset.
        public void RemoveActiveSpecimenOperations() => Clear();

        // After a deletion the panels may be showing a value that no longer exists, so
        // re-apply the new top of history or blank them.
        private void RefreshAfterEdit(WorkMode mode)
        {
            if (_history.Count > 0)
                _history[_history.Count - 1].ApplyMetadataToMode();
            else
                mode?.ClearMetadata();

            mode?.OnHistoryChanged();

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}