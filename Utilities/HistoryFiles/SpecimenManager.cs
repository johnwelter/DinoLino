using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace DinoLino.Utilities
{
    // One opened image together with the name the user gave it. Names are associated
    // with images: each opened image gets its own record, and only the record for the
    // image currently loaded in the workspace is editable (the bound TextBox always
    // shows the current specimen, so a non-loaded image can't be renamed).
    public class Specimen
    {
        public BitmapSource Image { get; set; }
        public string FileName { get; set; }   // shown in the "Loaded: X" label
        public string Name { get; set; }        // null => use the auto "Specimen N"
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

        // True once the first image has been opened. MainWindow uses this to decide whether
        // an image-open is a specimen transition (archive the outgoing specimen) or just the
        // initial load (nothing to archive yet).
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

        public bool CanMoveNext => _current < _specimens.Count - 1;
        public bool CanMovePrevious => _current > 0;

        // Up arrow: move to the next (newer) opened image. Returns the specimen that is now
        // current so MainWindow can reload its image, or null if already at the newest.
        public Specimen MoveNext()
        {
            if (!CanMoveNext) return null;
            _current++;
            RaiseCurrentChanged();
            return Current;
        }

        // Down arrow: move to the previous (older) opened image. Returns the specimen that is
        // now current so MainWindow can reload its image, or null if already at the oldest.
        public Specimen MovePrevious()
        {
            if (!CanMovePrevious) return null;
            _current--;
            RaiseCurrentChanged();
            return Current;
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
                _specimens.Add(new Specimen { Image = image, FileName = fileName });
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