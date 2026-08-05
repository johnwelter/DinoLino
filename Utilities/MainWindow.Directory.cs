using DinoLino.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DinoLino
{
    /// <summary>
    /// Directory browser in the right sidebar: sets the working directory and opens
    /// images and 3D models straight from disk.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Openable file types
        // =====================

        // Raster formats WPF's BitmapImage decodes.
        private static readonly HashSet<string> DirectoryImageExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

        // Mesh formats MeshLoader dispatches on.
        private static readonly HashSet<string> DirectoryModelExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".ply", ".stl", ".obj" };

        /// <summary>True when double-clicking this file would open it in DinoLino.</summary>
        internal static bool CanOpenFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string ext = Path.GetExtension(path);
            return DirectoryImageExtensions.Contains(ext) || DirectoryModelExtensions.Contains(ext);
        }

        // =====================
        // State
        // =====================

        /// Folder chosen with "Set Working Directory", or typed into the path box.
        /// Used as the starting folder for the Open dialogs; null until one is set.
        public string WorkingDirectory { get; private set; }

        // Folder the tree is rooted at. Null lists every ready drive instead.
        private string _directoryRoot;

        // Stand-in child that gives an unread folder its expander arrow.
        private const string DirectoryPlaceholder = "\u2026";

        /// Starting folder for the Open dialogs; empty string leaves the dialog's own
        /// default in place.
        private string DialogInitialDirectory =>
            !string.IsNullOrEmpty(WorkingDirectory) && Directory.Exists(WorkingDirectory)
                ? WorkingDirectory
                : "";

        // =====================
        // Roots
        // =====================

        /// Rebuilds the top level of the tree: the working directory when one is set,
        /// otherwise every ready drive.
        private void RebuildDirectoryRoots()
        {
            UI_DirectoryTree.Items.Clear();

            if (!string.IsNullOrEmpty(_directoryRoot) && Directory.Exists(_directoryRoot))
            {
                var root = CreateFolderItem(new DirectoryInfo(_directoryRoot), _directoryRoot);

                // Open the chosen folder straight away so its contents are visible.
                root.IsExpanded = true;
                UI_DirectoryTree.Items.Add(root);
                return;
            }

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (IOException)
            {
                drives = new DriveInfo[0];
            }

            foreach (var drive in drives)
            {
                // A drive can fail on any of these properties (empty card readers,
                // disconnected network shares), so each one is guarded separately.
                try
                {
                    if (!drive.IsReady) continue;

                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.Name
                        : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

                    UI_DirectoryTree.Items.Add(CreateFolderItem(drive.RootDirectory, label));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            if (UI_DirectoryTree.Items.Count == 0)
                UI_DirectoryTree.Items.Add(MakeDirectoryNote("(no drives available)"));
        }

        /// <summary>Fills the tree the first time the panel appears.</summary>
        private void DirectoryTree_Loaded(object sender, RoutedEventArgs e)
        {
            if (UI_DirectoryTree.Items.Count == 0)
                RebuildDirectoryRoots();

            // The Sample list watches the same specimen manager the ▲/▼ arrows use.
            HookSampleList();
        }

        /// <summary>Returns the tree to the drive listing, keeping the working directory.</summary>
        private void Directory_ShowDrives(object sender, RoutedEventArgs e)
        {
            _directoryRoot = null;
            RebuildDirectoryRoots();
        }

        // =====================
        // Typed or pasted paths
        // =====================

        private void Directory_PathBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            ApplyTypedPath();
            e.Handled = true;
        }

        private void Directory_ApplyTypedPath(object sender, RoutedEventArgs e) => ApplyTypedPath();

        /// Sets the working directory from whatever is in the path box. A pasted file
        /// path is accepted too and resolves to the folder containing that file, since
        /// copying a file path is the easier thing to do in Explorer.
        private void ApplyTypedPath()
        {
            string typed = UI_WorkingDirectoryBox.Text?.Trim();
            if (string.IsNullOrEmpty(typed)) return;

            // Explorer's "Copy as path" wraps the path in quotes.
            typed = typed.Trim('"');

            string resolved;
            try
            {
                if (Directory.Exists(typed))
                {
                    resolved = Path.GetFullPath(typed);
                }
                else if (File.Exists(typed))
                {
                    resolved = Path.GetDirectoryName(Path.GetFullPath(typed));
                }
                else
                {
                    MessageBox.Show(this,
                        "That folder could not be found:\n" + typed,
                        "Set Working Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Rejects malformed paths (bad characters, too long) without crashing.
                MessageBox.Show(this,
                    "That path could not be read:\n" + ex.Message,
                    "Set Working Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(resolved) || !Directory.Exists(resolved))
            {
                MessageBox.Show(this,
                    "That folder could not be found:\n" + typed,
                    "Set Working Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetWorkingDirectory(resolved);
        }

        // =====================
        // Items
        // =====================

        private TreeViewItem CreateFolderItem(DirectoryInfo dir, string label)
        {
            var item = new TreeViewItem
            {
                Header = MakeDirectoryHeader("\uD83D\uDCC1", label, dimmed: false),
                Tag = dir.FullName,

                // Left alignment lets long names run past the panel so the horizontal
                // scrollbar can reach them.
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // The menu needs the item itself, so a folder added inside it can be shown
            // without rebuilding the whole tree.
            item.ContextMenu = MakeFolderContextMenu(item);

            // The placeholder gives the folder an expander arrow; it is swapped for the
            // real contents the first time the folder opens.
            item.Items.Add(DirectoryPlaceholder);
            item.Expanded += Folder_Expanded;

            return item;
        }

        private TreeViewItem CreateFileItem(FileInfo file)
        {
            bool supported = CanOpenFile(file.FullName);

            var item = new TreeViewItem
            {
                Header = MakeDirectoryHeader("\uD83D\uDCC4", file.Name, dimmed: !supported),
                Tag = file.FullName,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = supported
                    ? "Double-click to open in DinoLino"
                    : "DinoLino cannot open this file type"
            };

            item.MouseDoubleClick += File_DoubleClick;
            return item;
        }

        // Glyph plus name, dimmed for files DinoLino cannot open.
        private static StackPanel MakeDirectoryHeader(string glyph, string text, bool dimmed)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            panel.Children.Add(new TextBlock
            {
                Text = glyph,

                // Set explicitly because the window's Arial has no emoji coverage.
                FontFamily = new FontFamily("Segoe UI Emoji"),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = dimmed ? 0.45 : 1.0
            });

            panel.Children.Add(new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = dimmed ? 0.45 : 1.0
            });

            return panel;
        }

        private static TreeViewItem MakeDirectoryNote(string text) => new TreeViewItem
        {
            Header = new TextBlock
            {
                Text = text,
                Opacity = 0.55,
                FontStyle = FontStyles.Italic
            },
            Focusable = false
        };

        // =====================
        // Expansion
        // =====================

        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            // Expanded bubbles up the tree, so ignore it unless this is the folder that
            // actually opened.
            if (!ReferenceEquals(sender, e.OriginalSource)) return;
            if (sender is not TreeViewItem item) return;

            // Anything other than the lone placeholder means it is already populated.
            if (item.Items.Count != 1 || item.Items[0] is not string) return;

            PopulateFolder(item);
        }

        /// Reads one folder into the tree: subfolders first, then files, each in name
        /// order.
        private void PopulateFolder(TreeViewItem item)
        {
            string path = item.Tag as string;
            item.Items.Clear();

            if (string.IsNullOrEmpty(path)) return;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path)
                                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new DirectoryInfo(dir);
                    if (IsHiddenEntry(info.Attributes)) continue;
                    item.Items.Add(CreateFolderItem(info, info.Name));
                }

                foreach (var file in Directory.EnumerateFiles(path)
                                              .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(file);
                    if (IsHiddenEntry(info.Attributes)) continue;
                    item.Items.Add(CreateFileItem(info));
                }
            }
            catch (UnauthorizedAccessException)
            {
                item.Items.Add(MakeDirectoryNote("(access denied)"));
                return;
            }
            catch (IOException)
            {
                item.Items.Add(MakeDirectoryNote("(unavailable)"));
                return;
            }

            if (item.Items.Count == 0)
                item.Items.Add(MakeDirectoryNote("(empty)"));
        }

        // Hidden and system entries are skipped, matching File Explorer's default view.
        private static bool IsHiddenEntry(FileAttributes attributes) =>
            (attributes & FileAttributes.Hidden) != 0 ||
            (attributes & FileAttributes.System) != 0;

        // =====================
        // Opening files
        // =====================

        private void File_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeViewItem item) return;

            // Keep the double-click from reaching the folder above.
            e.Handled = true;

            OpenFromDirectory(item.Tag as string);
        }

        /// Opens a file the tree was double-clicked on, if DinoLino handles that type.
        private async void OpenFromDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string ext = Path.GetExtension(path);

            if (DirectoryImageExtensions.Contains(ext))
                OpenImageFromPath(path);
            else if (DirectoryModelExtensions.Contains(ext))
                await Open3DModelFromPath(path);

            // Anything else is dimmed in the tree and does nothing here.
        }

        // =====================
        // Sample tab
        // =====================

        // Rebuilding on every SpecimenManager change would fire on each keystroke in
        // the name box, so the list is only refreshed while its tab is on screen.
        private bool _sampleTabSelected;

        /// Subscribes the Sample list to specimen changes. Called once from the
        /// Directory tree's Loaded handler, which runs after the panel is built.
        private void HookSampleList()
        {
            if (_sampleHooked) return;
            _sampleHooked = true;

            SpecimenManager.PropertyChanged += (s, e) =>
            {
                if (_sampleTabSelected) RebuildSampleList();
            };
        }
        private bool _sampleHooked;

        private void DirectoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Read the state off the control rather than the event: SelectionChanged
            // also bubbles up from any nested selector, and recomputing is harmless
            // where an early return would leave the flag stale.
            _sampleTabSelected =
                UI_DirectoryTabs.SelectedItem is TabItem tab &&
                (tab.Header as string) == "Sample";

            if (_sampleTabSelected) RebuildSampleList();
        }

        /// Redraws the specimen roster. Rebuilt wholesale rather than patched, so the
        /// list is always truthful after opens, renames, and cache releases.
        private void RebuildSampleList()
        {
            UI_SampleList.Children.Clear();

            bool any = false;
            foreach (var specimen in SpecimenManager.Specimens)
            {
                // The session starts with one empty placeholder record; it is not a
                // real specimen until a file has been opened into it.
                if (specimen.FileName == null) continue;

                // Deleted specimens keep their slot so the auto numbering of the others
                // never shifts, but they are gone from the roster.
                if (specimen.Deleted) continue;

                any = true;
                UI_SampleList.Children.Add(BuildSampleRow(specimen));
            }

            if (!any)
            {
                UI_SampleList.Children.Add(new TextBlock
                {
                    Text = "No specimens loaded yet.",
                    Opacity = 0.6,
                    Margin = new Thickness(2)
                });
            }
        }

        private UIElement BuildSampleRow(Specimen specimen)
        {
            bool loaded = SpecimenManager.IsCurrent(specimen);

            // An imported 3D model has no image yet but is still openable: opening it
            // is how the user positions it.
            bool pendingModel = specimen.NeedsPositioning;
            bool released = specimen.Image == null && !pendingModel;

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Name on top, file name beneath, so both are readable in a narrow panel.
            var text = new StackPanel { Margin = new Thickness(2, 0, 6, 0) };

            var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
            nameLine.Children.Add(new TextBlock
            {
                Text = SpecimenManager.NameOf(specimen),
                FontWeight = loaded ? FontWeights.Bold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (loaded)
            {
                nameLine.Children.Add(new TextBlock
                {
                    Text = "  (loaded)",
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            text.Children.Add(nameLine);

            text.Children.Add(new TextBlock
            {
                Text = released ? specimen.FileName + "  (image released)"
                     : pendingModel ? specimen.FileName + "  (3D \u2014 not positioned)"
                     : specimen.FileName,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // A released specimen has no image to show, so it cannot be opened.
            if (released) text.Opacity = 0.55;

            // Transparent background so the whole row, not just the glyphs, is a
            // double-click target.
            var hit = new Border
            {
                Background = Brushes.Transparent,
                Child = text,
                ToolTip = released
                    ? "This specimen's image was released; its measurements are kept"
                    : pendingModel
                        ? "Double-click to position this 3D model and capture its view"
                        : "Double-click to open this specimen in the workspace"
            };

            var captured = specimen;
            hit.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    OpenSpecimenFromSample(captured);
                }
            };

            Grid.SetColumn(hit, 0);
            grid.Children.Add(hit);

            // Offered even for a released specimen, whose measurements still exist.
            var remove = new Button
            {
                Content = "\u2715",
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Delete this specimen and all of its measurements"
            };

            remove.Click += (s, e) => DeleteSpecimenFromSample(captured);
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);

            return grid;
        }

        /// Loads a specimen picked from the Sample list, reusing the same swap the
        /// ▲/▼ arrows perform so its operation history travels with it.
        private async void OpenSpecimenFromSample(Specimen specimen)
        {
            if (specimen == null) return;

            // An imported 3D model is positioned the first time it is opened; the
            // captured view then becomes this specimen's image.
            if (specimen.NeedsPositioning)
            {
                await OpenImportedModel(specimen);
                return;
            }

            if (specimen.Image == null)
            {
                MessageBox.Show(this,
                    "This specimen's image was released from the cache, so it cannot be reopened.\n\n" +
                    "Its name and measurements are still kept. Open the file again to restore the image.",
                    "Open Specimen", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Already on screen: nothing to swap, and reloading would clear the
            // workspace for no reason.
            if (SpecimenManager.IsCurrent(specimen)) return;

            var departing = SpecimenManager.CurrentSpecimen;
            ReloadSpecimen(departing, SpecimenManager.MoveTo(specimen));

            RebuildSampleList();
        }

        /// Deletes a specimen and everything recorded for it: the cached image, and
        /// every measurement and outline in its operation history.
        private void DeleteSpecimenFromSample(Specimen specimen)
        {
            if (specimen == null || specimen.Deleted) return;

            if (!_suppressDeleteSpecimenPrompt)
            {
                bool dontAskAgain;
                bool confirmed = ConfirmPromptWindow.Show(
                    this,
                    "Delete Specimen",
                    "This will delete this image from the cache and remove all of its " +
                    "associated measurements and outlines. Are you sure?",
                    out dontAskAgain);

                // The preference is remembered even when the user cancels, matching how
                // "don't ask again" behaves elsewhere.
                if (dontAskAgain) _suppressDeleteSpecimenPrompt = true;
                if (!confirmed) return;
            }

            bool wasCurrent = SpecimenManager.IsCurrent(specimen);

            // Drop the measurements first, while the specimen still knows where they
            // live. The active specimen's operations are the live history; an inactive
            // one keeps them in its archived record.
            if (wasCurrent)
            {
                UndoRedoManager.RemoveActiveSpecimenOperations();

                // Those operations drew on the canvas, so clear what is left of them.
                ClearWorkspaceVisualsOnly();
            }
            else if (specimen.Record != null)
            {
                UndoRedoManager.RemoveArchivedSpecimen(specimen.Record);
            }

            SpecimenManager.DeleteSpecimen(specimen);

            // Deleting the specimen on screen leaves the workspace showing something
            // that no longer exists, so move to a surviving specimen or empty it.
            if (wasCurrent)
            {
                var survivor = SpecimenManager.MovePrevious() ?? SpecimenManager.MoveNext();

                if (survivor != null)
                {
                    // departing is null on purpose: the deleted specimen must not be
                    // stashed back into the archive on the way out.
                    ReloadSpecimen(null, survivor);
                }
                else
                {
                    ClearWorkspaceImage();
                }
            }

            RebuildSampleList();
            UpdateAttemptCounter();
        }

        // Remembers "Don't show this message again" for the rest of the session.
        private bool _suppressDeleteSpecimenPrompt;

        // =====================
        // Working directory
        // =====================

        private ContextMenu MakeFolderContextMenu(TreeViewItem item)
        {
            var menu = new ContextMenu();

            var setItem = new MenuItem { Header = "Set Working Directory" };
            setItem.Click += (s, e) => SetWorkingDirectory(item.Tag as string);
            menu.Items.Add(setItem);

            var addItem = new MenuItem { Header = "Add Folder" };
            addItem.Click += (s, e) => AddFolderInside(item);
            menu.Items.Add(addItem);

            var importItem = new MenuItem { Header = "Import Folder" };
            importItem.Click += (s, e) => ImportFolder(item.Tag as string);
            menu.Items.Add(importItem);

            // Scanning every folder as the tree is built would be wasteful, so whether
            // this folder holds anything importable is decided as the menu opens. An
            // empty folder, or one of unsupported types, leaves the item greyed out.
            menu.Opened += (s, e) =>
            {
                bool importable = CanImportFolder(item.Tag as string);
                importItem.IsEnabled = importable;
                importItem.ToolTip = importable
                    ? "Import every image and 3D model in this folder"
                    : "This folder has nothing DinoLino can import";
            };

            return menu;
        }

        /// Creates a folder inside the right-clicked one and reveals it, so the new
        /// folder is visible without collapsing and reopening the parent.
        private void AddFolderInside(TreeViewItem parentItem)
        {
            string parentPath = parentItem?.Tag as string;
            if (string.IsNullOrEmpty(parentPath) || !Directory.Exists(parentPath)) return;

            string name = PromptForFolderName(parentPath);
            if (name == null) return;   // cancelled

            string newPath = Path.Combine(parentPath, name);

            try
            {
                Directory.CreateDirectory(newPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not create the folder:\n{ex.Message}",
                    "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Re-read the parent so the new folder appears in the correct sort position,
            // then open the parent and select what was just created.
            PopulateFolder(parentItem);
            parentItem.IsExpanded = true;

            foreach (var child in parentItem.Items)
            {
                if (child is TreeViewItem childItem
                    && string.Equals(childItem.Tag as string, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    childItem.IsSelected = true;
                    childItem.BringIntoView();
                    break;
                }
            }
        }

        /// Asks for a folder name, defaulting to an unused "New folder" like Explorer
        /// does. Returns null when cancelled, and re-asks on an invalid name.
        private string PromptForFolderName(string parentPath)
        {
            string suggestion = SuggestFolderName(parentPath);

            while (true)
            {
                string entered = TextPromptWindow.Show(
                    this, "Add Folder", "Name of the new folder:", suggestion);

                if (entered == null) return null;

                entered = entered.Trim();

                if (entered.Length == 0)
                {
                    MessageBox.Show(this, "Please enter a folder name.",
                        "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                if (entered.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show(this,
                        "A folder name cannot contain any of these characters:\n" +
                        "  \\ / : * ? \" < > |",
                        "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    suggestion = entered;
                    continue;
                }

                if (Directory.Exists(Path.Combine(parentPath, entered)) ||
                    File.Exists(Path.Combine(parentPath, entered)))
                {
                    MessageBox.Show(this,
                        $"\"{entered}\" already exists in that folder.",
                        "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    suggestion = entered;
                    continue;
                }

                return entered;
            }
        }

        // "New folder", then "New folder (2)" and up, so the default never collides.
        private static string SuggestFolderName(string parentPath)
        {
            const string baseName = "New folder";

            try
            {
                if (!Directory.Exists(Path.Combine(parentPath, baseName)))
                    return baseName;

                for (int n = 2; n < 1000; n++)
                {
                    string candidate = $"{baseName} ({n})";
                    if (!Directory.Exists(Path.Combine(parentPath, candidate)))
                        return candidate;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return baseName;
        }

        /// Makes a folder the working directory and re-roots the tree there.
        private void SetWorkingDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            WorkingDirectory = path;
            _directoryRoot = path;

            UI_WorkingDirectoryText.Text = "Working directory: " + path;
            UI_WorkingDirectoryText.ToolTip = path;
            UI_WorkingDirectoryBox.Text = path;

            RebuildDirectoryRoots();
        }
    }

    /// <summary>
    /// Yes/Cancel confirmation with a "Don't show this message again" option.
    /// </summary>
    internal class ConfirmPromptWindow : Window
    {
        private readonly CheckBox _suppress = new CheckBox
        {
            Content = "Don't show this message again",
            Margin = new Thickness(0, 14, 0, 0)
        };

        /// Shows the prompt. Returns true when the user confirms; dontShowAgain
        /// reports the checkbox either way, so the choice sticks even on Cancel.
        internal static bool Show(Window owner, string title, string message, out bool dontShowAgain)
        {
            var dialog = new ConfirmPromptWindow(title, message)
            {
                Owner = owner,
                FontSize = owner?.FontSize ?? 14,
                FontFamily = owner?.FontFamily
            };

            bool confirmed = dialog.ShowDialog() == true;
            dontShowAgain = dialog._suppress.IsChecked == true;
            return confirmed;
        }

        private ConfirmPromptWindow(string title, string message)
        {
            Title = title;
            Width = 430;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            });

            root.Children.Add(_suppress);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var yes = new Button { Content = "Yes", Width = 84 };
            yes.Click += (s, e) =>
            {
                // DialogResult may only be set while running modally; guard it the same
                // way ScaleWindow does.
                try
                {
                    DialogResult = true;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            };
            buttons.Children.Add(yes);

            // Cancel is the default so a stray Enter or Esc does not delete anything.
            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                Width = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true,
                IsCancel = true
            });

            root.Children.Add(buttons);
            Content = root;
        }
    }

    /// <summary>
    /// Small modal prompt for a single line of text, used by Add Folder.
    /// </summary>
    internal class TextPromptWindow : Window
    {
        private readonly TextBox _box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };

        /// <summary>Returns the entered text, or null when cancelled.</summary>
        internal static string Show(Window owner, string title, string prompt, string initial)
        {
            var dialog = new TextPromptWindow(title, prompt, initial)
            {
                Owner = owner,
                FontSize = owner?.FontSize ?? 14,
                FontFamily = owner?.FontFamily
            };

            return dialog.ShowDialog() == true ? dialog._box.Text : null;
        }

        private TextPromptWindow(string title, string prompt, string initial)
        {
            Title = title;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            });

            _box.Text = initial ?? "";
            root.Children.Add(_box);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
            ok.Click += (s, e) =>
            {
                // DialogResult may only be set while running modally; guard it the same
                // way ScaleWindow does.
                try
                {
                    DialogResult = true;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            };
            buttons.Children.Add(ok);

            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                Width = 80,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            });

            root.Children.Add(buttons);
            Content = root;

            // Open with the suggested name selected so typing replaces it.
            Loaded += (s, e) =>
            {
                _box.SelectAll();
                _box.Focus();
            };
        }
    }
}