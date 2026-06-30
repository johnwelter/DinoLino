using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace DinoLino.Utilities
{
    // Manages the specimen name and counter displayed in the control panel.
    // Extracted from MainWindow to keep specimen tracking logic self-contained.
    // Implements INotifyPropertyChanged so the TextBox can bind directly.
    public class SpecimenManager : INotifyPropertyChanged
    {
        //----- INotifyPropertyChanged -----//
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        //----- Backing fields -----//
        private int _count = 1;
        private string _customName = null;   // null means "use auto name"
        private string _loadedFileName = null;   // null means "no file loaded"

        public string LoadedFileLabel => _loadedFileName == null ? "No file loaded" : $"Loaded: {_loadedFileName}";

        //----- Public API -----//

        // The count displayed next to the specimen name.
        // Minimum value is 1.
        public int Count
        {
            get => _count;
            private set
            {
                if (_count == value) return;
                _count = value < 1 ? 1 : value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName)); // count change also affects display
            }
        }

        // The name shown in the TextBox.
        // If the user has typed a custom name, that is returned.
        // Otherwise the auto-generated "Specimen N" is returned.
        public string DisplayName
        {
            get => _customName ?? $"Specimen {_count}";
            set
            {
                // If the user clears the box, revert to auto name
                string cleaned = value?.Trim();
                _customName = string.IsNullOrEmpty(cleaned) ? null : cleaned;
                OnPropertyChanged();
            }
        }

        // Increment the counter and clear any custom name so the
        // label reverts to the new "Specimen N" default.
        public void Increment()
        {
            _customName = null;
            Count++;
        }

        // Decrement the counter (floor 1) and clear any custom name.
        public void Decrement()
        {
            _customName = null;
            Count--;
        }

        // Called when a new image is opened. Increments the counter and
        // resets to the auto-generated name.
        private bool _hasOpenedImage = false;

        // True once the first image has been opened. MainWindow uses this to decide whether
        // an image-open is a specimen transition (archive the outgoing specimen) or just the
        // initial load (nothing to archive yet).
        public bool HasOpenedImage => _hasOpenedImage;
        public void OnImageOpened(string fileName)
        {
            _loadedFileName = fileName;
            OnPropertyChanged(nameof(LoadedFileLabel));

            if (_hasOpenedImage)
                Increment();
            else
                _hasOpenedImage = true;
        }

        // Wires the manager to the TextBox so edits flow both ways.
        // Call this once from MainWindow after InitializeComponent().
        public void BindToTextBox(TextBox textBox)
        {
            // flag to prevent the two handlers from triggering each other
            bool _isSyncing = false;

            // Set initial display
            textBox.Text = DisplayName;

            // View → ViewModel: user types in the box
            textBox.TextChanged += (s, e) =>
            {
                if (_isSyncing) return;
                _isSyncing = true;
                DisplayName = textBox.Text;
                _isSyncing = false;
            };

            // ViewModel → View: Increment/Decrement/OnImageOpened changes DisplayName
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