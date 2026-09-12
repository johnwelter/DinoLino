using DinoLino.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

// This one file holds both the specimen data layer (Specimen + SpecimenManager, in
// DinoLino.Utilities) and the Tools > Edit Image Cache window that edits it
// (EditImageCacheWindow, in DinoLino alongside the app's other windows).

namespace DinoLino.Utilities
{
    // One opened image together with the name the user gave it.
    public class Specimen
    {
        public BitmapSource Image { get; set; }   // null = released from the cache
        public string FileName { get; set; }      // shown in the "Loaded: X" label
        public string Name { get; set; }          // null => use the auto "Specimen N"

        // Creation index of this specimen (0-based, never changes: specimen records are
        // never removed).
        public int Ordinal { get; set; }

        // This specimen's operation record, owned by UndoRedoManager's archive
        // machinery.
        public SpecimenRecord Record { get; set; }

        // Set for a 3D model brought in by Import Folder: the file is registered as a
        // specimen straight away, but it has no image until the user opens it and
        // positions it. Cleared once a view is captured.
        public string PendingModelPath { get; set; }

        // Mesh file this specimen was captured from, kept for the whole session — unlike
        // PendingModelPath, which is cleared as soon as a view is captured. This is what
        // lets a later reposition, its own or a batch one, find the model again.
        public string ModelPath { get; set; }

        // Orientation this specimen was last captured at
        public System.Windows.Media.Media3D.Quaternion ModelOrientation { get; set; }
               = System.Windows.Media.Media3D.Quaternion.Identity;

        // Orientation of the specimen within its image, set by Tools ▸ Align Image.
        public AlignmentState Alignment { get; set; } = AlignmentState.None;

        // Scale calibration for this specimen's image, set by Tools ▸ Set Scale.
        public ScaleState Calibration { get; set; } = ScaleState.None;

        // Contrast, brightness and saturation for this specimen's image, set by
        // Tools ▸ Picture Adjustment. A correction that suits one specimen's lighting
        // rarely suits the next, so each keeps its own and starts uncorrected.
        public CorrectionState Corrections { get; set; } = CorrectionState.None;

        // True while this specimen is a 3D model that still needs positioning.
        public bool NeedsPositioning => Image == null && PendingModelPath != null;

        // True once the user deletes the specimen from the Sample tab. The record is
        // kept in the list rather than removed so the auto "Specimen N" numbering of
        // the surviving specimens never shifts, but it is hidden everywhere and
        // skipped by navigation.
        public bool Deleted { get; set; }
    }

    // Manages the ordered list of opened specimens (image + name) shown in the control
    // panel, plus which one is currently loaded.
    public class SpecimenManager : INotifyPropertyChanged
    {
        //----- INotifyPropertyChanged -----//
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        //----- State -----//

        // Opened specimens in the order they were opened.
        private readonly List<Specimen> _specimens = new() { new Specimen() };
        private int _current = 0;

        private Specimen Current => _specimens[_current];

        // The currently loaded specimen. MainWindow needs it to tell UndoRedoManager
        // who is departing when the user opens a new image or cycles with the arrows.
        public Specimen CurrentSpecimen => Current;

        // True once the first image has been opened.
        private bool _hasOpenedImage = false;
        public bool HasOpenedImage => _hasOpenedImage;

        //----- Display bindings -----//

        public string LoadedFileLabel =>
            Current.FileName == null ? "No file loaded" : $"Loaded: {Current.FileName}";

        // Name of the currently loaded specimen.
        public string DisplayName
        {
            get => Current.Name ?? $"Specimen {_current + 1}";
            set
            {
                string cleaned = value?.Trim();
                Current.Name = string.IsNullOrEmpty(cleaned) ? null : cleaned;
                OnPropertyChanged();
            }
        }

        //----- Navigation (up/down arrows) -----//
        // Specimens whose image has been released (Image == null) are invisible to the
        // arrows: navigation skips them in both directions, and when nothing with an
        // image remains on a side, that side's arrow reports disabled.

        public bool CanMoveNext
        {
            get
            {
                for (int i = _current + 1; i < _specimens.Count; i++)
                    if (_specimens[i].Image != null) return true;
                return false;
            }
        }

        public bool CanMovePrevious
        {
            get
            {
                for (int i = _current - 1; i >= 0; i--)
                    if (_specimens[i].Image != null) return true;
                return false;
            }
        }

        // Up arrow: move to the NEAREST newer specimen that still has a cached image.
        public Specimen MoveNext()
        {
            for (int i = _current + 1; i < _specimens.Count; i++)
            {
                if (_specimens[i].Image == null) continue;   // released: skip
                _current = i;
                RaiseCurrentChanged();
                return Current;
            }
            return null;
        }

        // Down arrow: move to the NEAREST older specimen that still has a cached image.
        // Returns it, or null (a no-op for the caller) when nothing older holds an image.
        public Specimen MovePrevious()
        {
            for (int i = _current - 1; i >= 0; i--)
            {
                if (_specimens[i].Image == null) continue;   // released: skip
                _current = i;
                RaiseCurrentChanged();
                return Current;
            }
            return null;
        }

        // Direct jump used by the Directory panel's Sample tab, where the user picks a
        // specimen rather than stepping through them. Returns null when the specimen
        // is unknown, already loaded, or has no cached image to show.
        public Specimen MoveTo(Specimen specimen)
        {
            if (specimen == null || specimen.Image == null) return null;

            int i = _specimens.IndexOf(specimen);
            if (i < 0 || i == _current) return null;

            _current = i;
            RaiseCurrentChanged();
            return Current;
        }

        //----- Image cache (Tools > Clear Image Cache / Edit Image Cache) -----//

        // Read-only roster for the Edit Image Cache window: every specimen of the
        // session in open order.
        public IReadOnlyList<Specimen> Specimens => _specimens;

        public bool IsCurrent(Specimen specimen) => ReferenceEquals(specimen, Current);

        // Auto-or-custom label for ANY specimen (the DisplayName property covers only
        // the current one).
        public string NameOf(Specimen specimen)
        {
            if (specimen?.Name != null) return specimen.Name;
            int i = _specimens.IndexOf(specimen);
            return i < 0 ? "Specimen ?" : $"Specimen {i + 1}";
        }

        public int CachedImageCount
        {
            get
            {
                int n = 0;
                foreach (var s in _specimens)
                    if (s.Image != null) n++;
                return n;
            }
        }

        // Releases one specimen's bitmap: it disappears from arrow cycling and its
        // memory can be reclaimed (immediately for past specimens; for the currently
        // loaded one only after the workspace moves to another image, because the
        // workspace itself still references that bitmap).
        public void ClearImage(Specimen specimen)
        {
            if (specimen == null || specimen.Image == null) return;
            specimen.Image = null;
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanMovePrevious));
        }

        // Releases every cached bitmap in one sweep (Clear Image Cache).
        public void ClearAllImages()
        {
            bool changed = false;
            foreach (var s in _specimens)
            {
                if (s.Image == null) continue;
                s.Image = null;
                changed = true;
            }
            if (!changed) return;
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanMovePrevious));
        }

        // Deletes a specimen outright (Sample tab ✕): the image is released and the
        // record is marked so it disappears from the rosters and from ▲/▼ cycling.
        // Its measurements are removed separately by the caller, which owns the
        // undo/redo history.
        public void DeleteSpecimen(Specimen specimen)
        {
            if (specimen == null || specimen.Deleted) return;

            specimen.Deleted = true;
            specimen.Image = null;
            specimen.Record = null;

            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(LoadedFileLabel));
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanMovePrevious));
        }

        //----- Image open -----//

        // Called by MainWindow whenever an image is registered as a new specimen.
        public void OnImageOpened(BitmapSource image, string fileName)
        {
            if (!_hasOpenedImage)
            {
                _hasOpenedImage = true;
                Current.Image = image;
                Current.FileName = fileName;
            }
            else
            {
                _specimens.Add(new Specimen
                {
                    Image = image,
                    FileName = fileName,
                    Ordinal = _specimens.Count   // creation index, stable for the session
                });
                _current = _specimens.Count - 1;
            }
            RaiseCurrentChanged();
        }

        //----- Batch import -----//

        // Adds one specimen without changing which one is loaded, so a whole folder can
        // be registered before the workspace switches to the first of them. A 3D model
        // arrives with a null image and its path in modelPath.
        public Specimen ImportSpecimen(BitmapSource image, string fileName, string modelPath)
        {
            Specimen target;

            if (!_hasOpenedImage)
            {
                // The session begins with one empty placeholder record; fill it rather
                // than leaving a phantom entry ahead of the imported files.
                _hasOpenedImage = true;
                target = Current;
                target.Image = image;
                target.FileName = fileName;
                target.PendingModelPath = modelPath;
                target.ModelPath = modelPath;
            }
            else
            {
                target = new Specimen
                {
                    Image = image,
                    FileName = fileName,
                    PendingModelPath = modelPath,
                    ModelPath = modelPath,
                    Ordinal = _specimens.Count   // creation index, stable for the session
                };
                _specimens.Add(target);
            }

            RaiseCurrentChanged();
            return target;
        }

        // Makes a specimen the loaded one. Unlike MoveTo this allows a specimen with no
        // image yet, which is what an imported 3D model is until it has been positioned.
        public bool MakeCurrent(Specimen specimen)
        {
            if (specimen == null || specimen.Deleted) return false;

            int i = _specimens.IndexOf(specimen);
            if (i < 0) return false;

            if (i != _current)
            {
                _current = i;
                RaiseCurrentChanged();
            }

            return true;
        }

        // Attaches the bitmap captured from the pose overlay to an imported 3D
        // specimen, which then behaves like any other specimen.
        public void AttachCapturedImage(Specimen specimen, BitmapSource image)
        {
            if (specimen == null || image == null) return;

            specimen.Image = image;
            specimen.PendingModelPath = null;

            RaiseCurrentChanged();
        }

        // Swaps in a re-oriented bitmap for a specimen that already holds one, so a
        // flip or rotation is what the specimen comes back as. A specimen whose image
        // has been released keeps none: the user asked for that memory back, and
        // turning the workspace copy is no reason to hand them a new one. Says nothing
        // about a pending model, unlike AttachCapturedImage, because turning an image
        // does not finish a 3D capture.
        public void ReplaceImage(Specimen specimen, BitmapSource image)
        {
            if (specimen == null || image == null) return;
            if (specimen.Image == null) return;

            specimen.Image = image;
        }

        //----- Duplication -----//

        /// Adds a copy of one specimen directly after it in the list: the same image
        /// and file under a new name, carrying none of the original's measurements,
        /// which is what opening the file a second time would give. Returns the copy,
        /// or null when the specimen is not one the list holds.
        public Specimen DuplicateSpecimen(Specimen original)
        {
            if (original == null || original.Deleted) return null;

            int at = _specimens.IndexOf(original);
            if (at < 0) return null;

            // Read before anything moves: the name comes from what the list shows now.
            string name = DuplicateNameFor(original);

            // Everything below the insertion point moves down one place, and two
            // things are read from that place. An auto "Specimen N" label is one, so
            // it is written down first: the numbering of a specimen the user did not
            // touch never shifts, exactly as deleting one promises. Ordinal is the
            // other, and it orders the archive the data tables are built from, so it
            // shifts along — a record holds its own copy of the number.
            for (int i = at + 1; i < _specimens.Count; i++)
            {
                var below = _specimens[i];

                if (below.Name == null) below.Name = NameOf(below);

                below.Ordinal++;
                if (below.Record != null) below.Record.Ordinal++;
            }

            var copy = new Specimen
            {
                // The bitmap is shared rather than decoded again: it never changes
                // once loaded, so a duplicate costs a reference and nothing more.
                Image = original.Image,
                FileName = original.FileName,
                Name = name,
                Ordinal = at + 1,

                // A 3D duplicate reopens from the same mesh at the same pose, and one
                // that was never positioned still needs positioning of its own.
                ModelPath = original.ModelPath,
                ModelOrientation = original.ModelOrientation,
                Alignment = original.Alignment,
                Calibration = original.Calibration,
                PendingModelPath = original.PendingModelPath

                // Record stays null: the copy starts with no measurements.
            };

            _specimens.Insert(at + 1, copy);

            // The loaded specimen keeps its place in the list, which is one further
            // down when the insertion happened above it.
            if (_current > at) _current++;

            RaiseCurrentChanged();
            return copy;
        }

        /// The name a duplicate takes: its original's with " B" appended, or the next
        /// free letter after that. A name already ending in one of those letters
        /// continues its original's run rather than starting a nested one, so
        /// duplicating "Specimen 1 B" gives "Specimen 1 C".
        private string DuplicateNameFor(Specimen original)
        {
            string full = NameOf(original);
            string root = StripDuplicateSuffix(full);

            // The run this name belongs to, then — once its letters are used up — a
            // fresh run off the whole name, so "Specimen 1 Z" is followed by
            // "Specimen 1 Z B" and the alphabet starts again from there.
            string name = FirstFreeLetter(root)
                       ?? (root == full ? null : FirstFreeLetter(full));

            if (name != null) return name;

            // Past the alphabet twice over, which takes fifty-odd copies of one
            // specimen. From here the names only have to stay distinct: a repeat
            // would make two specimens indistinguishable in every data table.
            for (int n = 2; ; n++)
            {
                string candidate = full + " B" + n;
                if (!NameInUse(candidate)) return candidate;
            }
        }

        // First of "<root> B" … "<root> Z" that no specimen is using, or null when
        // every one of them is taken.
        private string FirstFreeLetter(string root)
        {
            for (char suffix = 'B'; suffix <= 'Z'; suffix++)
            {
                string candidate = root + " " + suffix;
                if (!NameInUse(candidate)) return candidate;
            }

            return null;
        }

        // Drops a trailing " B" … " Z", the suffix duplication itself adds. " A" is
        // left alone: nothing here produces one, so it belongs to whoever typed it.
        private static string StripDuplicateSuffix(string name)
        {
            if (name == null || name.Length < 3) return name;

            char last = name[name.Length - 1];
            if (name[name.Length - 2] != ' ' || last < 'B' || last > 'Z') return name;

            return name.Substring(0, name.Length - 2);
        }

        // Checked against what each specimen displays rather than its stored name, so
        // a collision with an auto "Specimen N" is caught too.
        private bool NameInUse(string name)
        {
            foreach (var specimen in _specimens)
            {
                if (specimen.Deleted) continue;
                if (NameOf(specimen) == name) return true;
            }

            return false;
        }

        //----- Session reset -----//

        /// Empties the roster back to how the session began: one placeholder record,
        /// nothing loaded, no cached images. Measurements live in UndoRedoManager and
        /// group assignments in SpecimenGroups, so the caller clears those too.
        public void ResetSession()
        {
            _specimens.Clear();
            _specimens.Add(new Specimen());
            _current = 0;
            _hasOpenedImage = false;

            RaiseCurrentChanged();
        }

        private void RaiseCurrentChanged()
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(LoadedFileLabel));
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanMovePrevious));
        }

        //----- TextBox wiring -----//

        // Wires the manager to the TextBox so edits flow both ways.
        // Call this once from MainWindow after InitializeComponent().
        public void BindToTextBox(TextBox textBox)
        {
            // flag to prevent the two handlers from triggering each other
            bool _isSyncing = false;

            // Set initial display
            textBox.Text = DisplayName;

            // View -> ViewModel: user types in the box (writes to the current specimen)
            textBox.TextChanged += (s, e) =>
            {
                if (_isSyncing) return;
                _isSyncing = true;
                DisplayName = textBox.Text;
                _isSyncing = false;
            };

            // ViewModel -> View: navigation / open changes DisplayName
            PropertyChanged += (s, e) =>
            {
                if (_isSyncing) return;
                if (e.PropertyName != nameof(DisplayName)) return;
                _isSyncing = true;
                textBox.Text = DisplayName;
                _isSyncing = false;
            };
        }
    }
}

namespace DinoLino
{
    // Tools > Edit Image Cache.
    public class EditImageCacheWindow : Window
    {
        private readonly SpecimenManager _manager;
        private readonly StackPanel _rows = new StackPanel();

        public EditImageCacheWindow(SpecimenManager manager)
        {
            _manager = manager;

            Title = "Edit Image Cache";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };

            var header = new TextBlock
            {
                Text = "Releasing an image frees its memory and removes it from the " +
                       "specimen \u25b2/\u25bc cycling. Specimen names and all " +
                       "measurements are kept \u2014 the History window and exported " +
                       "tables are unaffected.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var closeBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true };
            close.Click += (s, e) => Close();
            closeBar.Children.Add(close);
            DockPanel.SetDock(closeBar, Dock.Bottom);
            root.Children.Add(closeBar);

            // Last child fills the remaining space between header and close bar.
            root.Children.Add(new ScrollViewer
            {
                Content = _rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            Content = root;
            RebuildRows();
        }

        // The list is small (one row per opened file), so rebuilding it wholesale after
        // every release keeps the code trivial and the display always truthful.
        private void RebuildRows()
        {
            _rows.Children.Clear();

            bool any = false;
            foreach (var specimen in _manager.Specimens)
            {
                if (specimen.FileName == null) continue;   // pre-first-open placeholder record
                if (specimen.Deleted) continue;            // deleted from the Sample tab
                any = true;
                _rows.Children.Add(BuildRow(specimen));
            }

            if (!any)
            {
                _rows.Children.Add(new TextBlock
                {
                    Text = "No images have been opened this session.",
                    Opacity = 0.6
                });
            }
        }

        private UIElement BuildRow(Specimen specimen)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string label = $"{_manager.NameOf(specimen)} \u2014 {specimen.FileName}";
            if (_manager.IsCurrent(specimen)) label += "   (loaded)";

            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0)
            };
            if (specimen.Image == null) text.Opacity = 0.55;
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            if (specimen.Image != null)
            {
                var minus = new Button
                {
                    Content = "\u2212",   // minus sign
                    MinWidth = 30,
                    Padding = new Thickness(6, 0, 6, 0),
                    ToolTip = "Release this image from the cache (specimen name and measurements are kept)"
                };
                minus.Click += (s, e) =>
                {
                    _manager.ClearImage(specimen);
                    RebuildRows();
                };
                Grid.SetColumn(minus, 1);
                grid.Children.Add(minus);
            }
            else
            {
                var released = new TextBlock
                {
                    Text = specimen.NeedsPositioning ? "not positioned" : "released",
                    FontStyle = FontStyles.Italic,
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(released, 1);
                grid.Children.Add(released);
            }

            return grid;
        }
    }
}