using DinoLino.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

// This one file holds both the specimen data layer (Specimen + SpecimenManager,
// in DinoLino.Utilities) and the Tools > Edit Image Cache window that edits it
// (EditImageCacheWindow, in DinoLino alongside the app's other windows). Two
// namespace blocks in one file is deliberate: the window is a thin view over
// SpecimenManager's cache API, and keeping them together means they evolve as
// a unit.

namespace DinoLino.Utilities
{
    // One opened image together with the name the user gave it. Names are associated
    // with images: each opened image gets its own record, and only the record for the
    // image currently loaded in the workspace is editable (the bound TextBox always
    // shows the current specimen, so a non-loaded image can't be renamed).
    //
    // Image is the only "cache" part of the record: releasing it (Tools > Clear Image
    // Cache / Edit Image Cache) sets Image = null while Name and FileName persist for
    // the whole session. All measurements live in UndoRedoManager (live history +
    // archive), which holds them per specimen via Record below, so releasing images
    // can never touch metadata, the History window, or exports.
    public class Specimen
    {
        public BitmapSource Image { get; set; }   // null = released from the cache
        public string FileName { get; set; }      // shown in the "Loaded: X" label
        public string Name { get; set; }          // null => use the auto "Specimen N"

        // Creation index of this specimen (0-based, never changes: specimen records
        // are never removed). UndoRedoManager uses it to keep the History-window /
        // export block order stable no matter how the user cycles with the arrows.
        public int Ordinal { get; set; }

        // This specimen's operation record, owned by UndoRedoManager's archive
        // machinery. Null until the user first moves OFF this specimen; from then on
        // it is refreshed on every departure and restored as the live history on
        // every arrival, which is what makes the attempt counter, undo/redo, and
        // exports per-specimen.
        public SpecimenRecord Record { get; set; }
    }

    // Manages the ordered list of opened specimens (image + name) shown in the control
    // panel, plus which one is currently loaded. The up/down arrows move the current
    // pointer through this list; MainWindow reloads the pointed-to image into the
    // workspace. Implements INotifyPropertyChanged so the TextBox and the loaded-file
    // label can bind directly.
    public class SpecimenManager : INotifyPropertyChanged
    {
        //----- INotifyPropertyChanged -----//
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        //----- State -----//

        // Opened specimens in the order they were opened. Starts with a single "pending"
        // record (no image yet) so the box shows "Specimen 1" and can be named before the
        // first image is opened; that pending record receives the first opened image.
        private readonly List<Specimen> _specimens = new() { new Specimen() };
        private int _current = 0;

        private Specimen Current => _specimens[_current];

        // The currently loaded specimen. MainWindow needs it to tell UndoRedoManager
        // who is departing when the user opens a new image or cycles with the arrows.
        public Specimen CurrentSpecimen => Current;

        // True once the first image has been opened. MainWindow uses this to decide whether
        // an image-open is a specimen transition (stash the outgoing specimen's operations)
        // or just the initial load (nothing to stash yet).
        private bool _hasOpenedImage = false;
        public bool HasOpenedImage => _hasOpenedImage;

        //----- Display bindings -----//

        public string LoadedFileLabel =>
            Current.FileName == null ? "No file loaded" : $"Loaded: {Current.FileName}";

        // Name of the currently loaded specimen. The setter writes back to that specimen
        // only, so an image that is not loaded cannot be renamed. Because the bound TextBox
        // writes this on every keystroke, the stored name is always the last one typed while
        // the specimen was loaded.
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
        // Returns the specimen that is now current so MainWindow can reload its image,
        // or null (a no-op for the caller) when nothing newer holds an image.
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

        //----- Image cache (Tools > Clear Image Cache / Edit Image Cache) -----//

        // Read-only roster for the Edit Image Cache window: every specimen of the
        // session in open order. The leading record has FileName == null until the
        // first image is opened; display code skips it.
        public IReadOnlyList<Specimen> Specimens => _specimens;

        public bool IsCurrent(Specimen specimen) => ReferenceEquals(specimen, Current);

        // Auto-or-custom label for ANY specimen (the DisplayName property covers only
        // the current one). Auto names are positional, matching what the name box
        // showed while that specimen was loaded.
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
        // workspace itself still references that bitmap). Name, file name, and every
        // measurement remain untouched.
        public void ClearImage(Specimen specimen)
        {
            if (specimen == null || specimen.Image == null) return;
            specimen.Image = null;
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanMovePrevious));
        }

        // Releases every cached bitmap in one sweep (Clear Image Cache). Records and
        // metadata persist exactly as with ClearImage; only the arrows go quiet until
        // new images are opened.
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

        //----- Image open -----//

        // Called by MainWindow whenever an image is registered as a new specimen. The first
        // opened image fills the initial pending record (keeping any name typed beforehand);
        // every image after that appends a new record and makes it current, which freezes the
        // previous specimen's name (whatever the user last typed while it was loaded).
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
    // Tools > Edit Image Cache. Lists every file opened this session (specimen name +
    // file name) with a minus button that releases that image from the specimen cache.
    // Releasing affects ONLY the specimen up/down cycling and memory: the specimen's
    // name stays on its record, and every measurement lives in UndoRedoManager (live
    // history + archive), which this window never touches — the History window and
    // exported tables are unchanged.
    //
    // Built in code rather than XAML so it can live in this file next to the manager
    // it edits. Fonts are inherited from the FontSize/FontFamily the caller sets on
    // the window, matching the app's other dialogs.
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
                    Text = "released",
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