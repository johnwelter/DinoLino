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