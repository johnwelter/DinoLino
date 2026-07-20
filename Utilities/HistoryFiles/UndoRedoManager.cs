using DinoLino.Utilities.Operations;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace DinoLino.Utilities
{
    // A per-specimen snapshot of committed operations. ONE record per specimen for
    // the whole session: created the first time the user moves off that specimen
    // (image-open or arrow navigation) and refreshed in place on every later
    // departure. While a specimen is ACTIVE its record is held OUT of the archive —
    // its operations are the live history instead — so the History window's
    // "archive blocks + live block" enumeration shows every specimen exactly once.
    public class SpecimenRecord
    {
        public string SpecimenName { get; set; }
        public List<WorkOperation> Operations { get; set; } = new();

        // Creation order of the owning specimen. Departure re-inserts the record at
        // the position that keeps the archive sorted by this, so History-window and
        // export block order is stable no matter how the user cycles with the arrows.
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
        // The live history always holds the ACTIVE specimen's working set. Moving off
        // a specimen stashes that set into the specimen's own record; arriving at a
        // specimen restores its record as the live set. The attempt counter, undo/redo,
        // History window, and exports all read the live history / archive, so every one
        // of them reflects the active specimen automatically.

        // Saves the live working set into the departing specimen's record and clears
        // the live lists (the redo stack does not survive leaving a specimen). Called
        // on BOTH departure paths: opening a new image and arrow navigation. Creates
        // the record on first departure even when empty, so every specimen gets a
        // labelled block in the History window, and stamps the specimen's latest name,
        // so renaming a recalled specimen relabels its block on the next departure.
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
            // never shows numbers that belong to this one. (The arrival path below
            // re-applies the arriving specimen's own metadata afterwards.)
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
        // mode panels. Metadata is applied in commit order, so when a mode has several
        // operations its panel ends on that specimen's most recent values — the same
        // rule Undo uses when the top of history changes.
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

        // Clears all live history and redo state — a hard reset used by "Clear All" / Ctrl+C.
        // Under per-specimen contexts this wipes the ACTIVE specimen's working set only.
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