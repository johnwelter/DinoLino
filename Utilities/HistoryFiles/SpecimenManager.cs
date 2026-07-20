using DinoLino.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

// This file contains the specimen cache model and the window used to edit it.
// Keeping the two together makes it easier to keep the UI and data model in sync.

namespace DinoLino.Utilities
{
    /// <summary>
    /// Represents one opened specimen: its image, file name, display name, creation order, and archived history.
    /// </summary>
    public class Specimen
    {
        public BitmapSource Image { get; set; }   // Null when the image has been released from the cache.
        public string FileName { get; set; }      // Shown in the loaded-file label.
        public string Name { get; set; }          // Null means the default "Specimen N" label is used.

        /// <summary>
        /// Stable session-wide order for this specimen.
        /// </summary>
        public int Ordinal { get; set; }

        /// <summary>
        /// This specimen's archived operation record, managed by UndoRedoManager.
        /// </summary>
        public SpecimenRecord Record { get; set; }
    }

    /// <summary>
    /// Tracks opened specimens and the specimen currently loaded in the workspace.
    /// Also provides bindings for the specimen name textbox and cache-related actions.
    /// </summary>
    public class SpecimenManager : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // The first entry is a placeholder so the first specimen can be named before any image is opened.
        private readonly List<Specimen> _specimens = new() { new Specimen() };
        private int _current = 0;

        private Specimen Current => _specimens[_current];

        public Specimen CurrentSpecimen => Current;

        // Becomes true after the first image is loaded into the initial placeholder record.
        private bool _hasOpenedImage = false;
        public bool HasOpenedImage => _hasOpenedImage;

        public string LoadedFileLabel =>
            Current.FileName == null ? "No file loaded" : $"Loaded: {Current.FileName}";

        /// <summary>
        /// Name shown in the textbox for the active specimen.
        /// Updates apply only to the currently loaded specimen.
        /// </summary>
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

        /// <summary>
        /// Returns true when there is a newer specimen with a cached image.
        /// Released specimens are skipped by navigation.
        /// </summary>
        public bool CanMoveNext
        {
            get
            {
                for (int i = _current + 1; i < _specimens.Count; i++)
                    if (_specimens[i].Image != null) return true;
                return false;
            }
        }

        /// <summary>
        /// Returns true when there is an older specimen with a cached image.
        /// Released specimens are skipped by navigation.
        /// </summary>
        public bool CanMovePrevious
        {
            get
            {
                for (int i = _current - 1; i >= 0; i--)
                    if (_specimens[i].Image != null) return true;
                return false;
            }
        }

        /// <summary>
        /// Moves to the next specimen that still has an image cached.
        /// </summary>
        public Specimen MoveNext()
        {
            for (int i = _current + 1; i < _specimens.Count; i++)
            {
                if (_specimens[i].Image == null) continue;
                _current = i;
                RaiseCurrentChanged();
                return Current;
            }
            return null;
        }

        /// <summary>
        /// Moves to the previous specimen that still has an image cached.
        /// </summary>
        public Specimen MovePrevious()
        {
            for (int i = _current - 1; i >= 0; i--)
            {
                if (_specimens[i].Image == null) continue;
                _current = i;
                RaiseCurrentChanged();
                return Current;
            }
            return null;
        }

        /// <summary>
        /// Returns the specimens in session order for the cache editor window.
        /// </summary>
        public IReadOnlyList<Specimen> Specimens => _specimens;

        public bool IsCurrent(Specimen specimen) => ReferenceEquals(specimen, Current);

        /// <summary>
        /// Returns the display name for any specimen, using the stored name when available.
        /// </summary>
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

        /// <summary>
        /// Releases one specimen image from the cache.
        /// The specimen remains in the session list, but it no longer participates in image cycling.
        /// </summary>
        public void ClearImage(Specimen specimen)
        {
            if (specimen == null || specimen.Image == null) return;
            specimen.Image = null;
            OnPropertyChanged(nameof(CanMoveNext));
            OnPropertyChanged(nameof(CanMovePrevious));
        }

        /// <summary>
        /// Releases every cached image in the session.
        /// </summary>
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

        /// <summary>
        /// Registers a newly opened image as the current specimen.
        /// The first image fills the initial placeholder; later images create new specimen records.
        /// </summary>
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
                    Ordinal = _specimens.Count
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

        /// <summary>
        /// Binds specimen name changes to a TextBox in both directions.
        /// </summary>
        public void BindToTextBox(TextBox textBox)
        {
            bool _isSyncing = false;

            textBox.Text = DisplayName;

            textBox.TextChanged += (s, e) =>
            {
                if (_isSyncing) return;
                _isSyncing = true;
                DisplayName = textBox.Text;
                _isSyncing = false;
            };

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
    /// <summary>
    /// Window for reviewing the specimen image cache and releasing cached images.
    /// </summary>
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
                Text = "Releasing an image frees its memory and removes it from specimen cycling. " +
                       "Specimen names and measurements are kept.",
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

            // This view is small, so rebuilding the list after each change keeps the code simple.
            root.Children.Add(new ScrollViewer
            {
                Content = _rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            Content = root;
            RebuildRows();
        }

        /// <summary>
        /// Rebuilds the visible specimen list from the current manager state.
        /// </summary>
        private void RebuildRows()
        {
            _rows.Children.Clear();

            bool any = false;
            foreach (var specimen in _manager.Specimens)
            {
                if (specimen.FileName == null) continue;
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

        /// <summary>
        /// Builds one row for the cache editor list.
        /// </summary>
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
                    Content = "\u2212",
                    MinWidth = 30,
                    Padding = new Thickness(6, 0, 6, 0),
                    ToolTip = "Release this image from the cache."
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