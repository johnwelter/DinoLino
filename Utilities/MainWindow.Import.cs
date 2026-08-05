using DinoLino.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DinoLino
{
    /// <summary>
    /// Import Folder: registers every compatible file in one folder as a specimen.
    /// Reached from File ▸ Import Folder and from the Directory's folder context menu.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Folder inspection
        // =====================

        /// True when a folder holds at least one file DinoLino can open. Stops at the
        /// first match, so it stays cheap enough to call while a menu opens.
        internal static bool CanImportFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (!CanOpenFile(file)) continue;
                    if (IsHiddenEntry(new FileInfo(file).Attributes)) continue;
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            return false;
        }

        /// Every compatible file directly inside a folder, in name order. Subfolders
        /// are not searched: each folder is imported on its own.
        internal static List<string> CompatibleFilesIn(string folder)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(folder)) return files;

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (!CanOpenFile(file)) continue;
                    if (IsHiddenEntry(new FileInfo(file).Attributes)) continue;
                    files.Add(file);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        // =====================
        // Entry points
        // =====================

        /// File ▸ Import Folder. Uses the same shell dialog Open Image uses, switched
        /// into folder-picking mode, so it looks and behaves like File Explorer.
        private void Menu_ImportFolder(object sender, RoutedEventArgs e)
        {
            string startAt = WorkingDirectory;

            while (true)
            {
                string folder;
                var outcome = NativeFolderPicker.Pick(
                    this, "Import Folder", "Import", startAt, out folder);

                if (outcome == FolderPickOutcome.Cancelled) return;

                if (outcome == FolderPickOutcome.Unavailable)
                {
                    // Very old Windows, or the shell dialog refused to start. Fall back
                    // to the in-app picker rather than leaving the menu item dead.
                    folder = FolderImportPickerWindow.Show(this, WorkingDirectory);
                    if (folder == null) return;

                    ImportFolder(folder);
                    return;
                }

                if (CanImportFolder(folder))
                {
                    ImportFolder(folder);
                    return;
                }

                // The shell dialog cannot grey out its own folders, so an empty one is
                // refused here and the dialog reopens where the user left off.
                MessageBox.Show(this,
                    "That folder has nothing DinoLino can import, so it cannot be chosen.\n\n" +
                    "Pick a folder containing images (.png .jpg .jpeg .bmp .gif .tif .tiff) " +
                    "or 3D models (.ply .stl .obj).",
                    "Import Folder", MessageBoxButton.OK, MessageBoxImage.Information);

                startAt = folder;
            }
        }

        // =====================
        // Import
        // =====================

        /// Registers every compatible file in the folder as a specimen, then shows the
        /// first one that has an image.
        internal void ImportFolder(string folder)
        {
            var files = CompatibleFilesIn(folder);

            if (files.Count == 0)
            {
                MessageBox.Show(this,
                    "That folder has no files DinoLino can open, so there is nothing to import.\n\n" +
                    "Images: .png .jpg .jpeg .bmp .gif .tif .tiff\n" +
                    "3D models: .ply .stl .obj",
                    "Import Folder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var imported = new List<Specimen>();
            var failed = new List<string>();
            int models = 0;

            // Decoding a folder of images can take a moment.
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                foreach (var file in files)
                {
                    string name = Path.GetFileName(file);

                    if (DirectoryModelExtensions.Contains(Path.GetExtension(file)))
                    {
                        // A 3D model has no picture until the user positions it, so it
                        // is registered now and loaded when they open it.
                        imported.Add(SpecimenManager.ImportSpecimen(null, name, file));
                        models++;
                        continue;
                    }

                    var bitmap = LoadImportedImage(file);
                    if (bitmap == null)
                    {
                        failed.Add(name);
                        continue;
                    }

                    imported.Add(SpecimenManager.ImportSpecimen(bitmap, name, null));
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (imported.Count == 0)
            {
                MessageBox.Show(this,
                    "None of the files in that folder could be read.",
                    "Import Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Show the first imported specimen that actually has a picture. Only one
            // image is ever displayed, however many were brought in.
            Specimen first = imported.Find(s => s.Image != null);
            if (first != null) LoadSpecimenIntoWorkspace(first);

            RebuildSampleList();
            UpdateAttemptCounter();

            ReportImportResult(imported.Count, models, first == null, failed);
        }

        /// Reads one image file. Returns null when the file cannot be decoded, so one
        /// bad file does not abandon the whole folder.
        private static BitmapImage LoadImportedImage(string path)
        {
            try
            {
                // OnLoad reads the file up front and releases the handle, matching the
                // single-file open path.
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// Makes one already-registered specimen the loaded one, stashing whatever was
        /// loaded before so its measurements travel with it.
        internal bool LoadSpecimenIntoWorkspace(Specimen specimen)
        {
            if (specimen?.Image == null) return false;

            if (SpecimenManager.IsCurrent(specimen))
            {
                // Already the current record (the first import of a fresh session fills
                // the starting placeholder), so only the workspace needs updating.
                SetWorkspaceImage(specimen.Image, specimen.FileName, registerAsNewSpecimen: false);
                return true;
            }

            var departing = SpecimenManager.CurrentSpecimen;
            SpecimenManager.MakeCurrent(specimen);

            SetWorkspaceImage(specimen.Image, specimen.FileName, registerAsNewSpecimen: false);
            UndoRedoManager.SwitchActiveSpecimen(departing, SpecimenManager.NameOf(departing), specimen);

            // An imported image is not a 3D capture.
            _workingImageIsModelCapture = false;
            _activeMesh = null;
            _activeModelName = null;
            UI_MenuReposition3D.IsEnabled = false;

            return true;
        }

        private void ReportImportResult(int count, int models, bool nothingShown, List<string> failed)
        {
            var message = new System.Text.StringBuilder();
            message.Append(count == 1 ? "1 file imported." : $"{count} files imported.");

            if (models > 0)
            {
                message.Append(models == 1
                    ? "\n\n1 of them is a 3D model. Open it from the Sample tab to position it."
                    : $"\n\n{models} of them are 3D models. Open each from the Sample tab to position it.");
            }

            if (nothingShown)
                message.Append("\n\nNothing is shown in the workspace yet, because every imported file is a 3D model.");

            if (failed.Count > 0)
            {
                message.Append(failed.Count == 1
                    ? "\n\n1 file could not be read and was skipped:\n"
                    : $"\n\n{failed.Count} files could not be read and were skipped:\n");
                message.Append(string.Join("\n", failed));
            }

            MessageBox.Show(this, message.ToString(), "Import Folder",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>What the native folder dialog did.</summary>
    internal enum FolderPickOutcome
    {
        Picked,
        Cancelled,

        // The shell dialog could not be created, so the caller should fall back.
        Unavailable
    }

    /// <summary>
    /// The Windows shell folder dialog — the same Explorer-style window the Open
    /// dialogs use, switched into folder-picking mode. WPF on this framework has no
    /// wrapper for it, so it is reached through COM.
    /// </summary>
    internal static class NativeFolderPicker
    {
        // Shell option flags: folders only, real filesystem paths only, must exist.
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;

        // SIGDN_FILESYSPATH: ask a shell item for its plain filesystem path.
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        private const int S_OK = 0;
        private static readonly int ERROR_CANCELLED = unchecked((int)0x800704C7);

        private static readonly Guid CLSID_FileOpenDialog =
            new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

        /// Shows the dialog. okLabel becomes the confirm button's text, so this reads
        /// "Import" rather than "Open".
        internal static FolderPickOutcome Pick(
            Window owner, string title, string okLabel, string initialFolder, out string folder)
        {
            folder = null;
            object dialogObject = null;

            try
            {
                var type = Type.GetTypeFromCLSID(CLSID_FileOpenDialog);
                if (type == null) return FolderPickOutcome.Unavailable;

                dialogObject = Activator.CreateInstance(type);
                var dialog = dialogObject as IFileDialog;
                if (dialog == null) return FolderPickOutcome.Unavailable;

                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(okLabel)) dialog.SetOkButtonLabel(okLabel);

                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    // A folder that will not resolve is not worth losing the dialog
                    // over; it simply opens wherever the shell defaults to.
                    try
                    {
                        IShellItem start;
                        SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, typeof(IShellItem).GUID, out start);
                        if (start != null) dialog.SetFolder(start);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[import] start folder ignored: " + ex.Message);
                    }
                }

                IntPtr hwnd = owner == null
                    ? IntPtr.Zero
                    : new System.Windows.Interop.WindowInteropHelper(owner).Handle;

                int hr = dialog.Show(hwnd);
                if (hr == ERROR_CANCELLED) return FolderPickOutcome.Cancelled;
                if (hr != S_OK) return FolderPickOutcome.Unavailable;

                IShellItem result;
                dialog.GetResult(out result);
                if (result == null) return FolderPickOutcome.Cancelled;

                IntPtr pathPtr;
                result.GetDisplayName(SIGDN_FILESYSPATH, out pathPtr);
                if (pathPtr == IntPtr.Zero) return FolderPickOutcome.Cancelled;

                try
                {
                    folder = Marshal.PtrToStringUni(pathPtr);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }

                return string.IsNullOrEmpty(folder)
                    ? FolderPickOutcome.Cancelled
                    : FolderPickOutcome.Picked;
            }
            catch (Exception ex)
            {
                // Any interop problem falls back rather than failing the command.
                System.Diagnostics.Debug.WriteLine("[import] shell folder dialog unavailable: " + ex.Message);
                return FolderPickOutcome.Unavailable;
            }
            finally
            {
                if (dialogObject != null && Marshal.IsComObject(dialogObject))
                    Marshal.ReleaseComObject(dialogObject);
            }
        }

        // ---- COM declarations ----
        // Method order matters: these mirror the interfaces' vtable layout exactly.

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr parent);

            // IFileDialog
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close([MarshalAs(UnmanagedType.Error)] int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
    }

    /// <summary>
    /// Fallback folder chooser, used only when the shell dialog is unavailable.
    /// Folders holding nothing importable are greyed out and cannot be imported.
    /// </summary>
    internal class FolderImportPickerWindow : Window
    {
        private readonly TreeView _tree = new TreeView
        {
            BorderThickness = new Thickness(0)
        };

        private readonly TextBlock _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.Gray
        };

        private readonly Button _import = new Button
        {
            Content = "Import",
            Width = 90,
            IsEnabled = false
        };

        private const string Placeholder = "\u2026";
        private string _selected;

        /// <summary>Returns the chosen folder, or null when cancelled.</summary>
        internal static string Show(Window owner, string startFolder)
        {
            var dialog = new FolderImportPickerWindow(startFolder)
            {
                Owner = owner,
                FontSize = owner?.FontSize ?? 14,
                FontFamily = owner?.FontFamily
            };

            return dialog.ShowDialog() == true ? dialog._selected : null;
        }

        private FolderImportPickerWindow(string startFolder)
        {
            Title = "Import Folder";
            Width = 520;
            Height = 560;
            MinWidth = 380;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(14) };

            var note = new TextBlock
            {
                Text = "Choose a folder to import. Every image and 3D model inside it becomes " +
                       "a specimen; other files are ignored. Folders with nothing to import are " +
                       "greyed out.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            _import.Click += (s, e) =>
            {
                try { DialogResult = true; }
                catch (InvalidOperationException) { Close(); }
            };
            buttons.Children.Add(_import);

            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                Width = 90,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            });

            var bottom = new StackPanel();
            bottom.Children.Add(_status);
            bottom.Children.Add(buttons);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            _tree.SelectedItemChanged += Tree_SelectedItemChanged;
            root.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Child = new ScrollViewer
                {
                    Content = _tree,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            });

            Content = root;
            BuildRoots(startFolder);
        }

        private void BuildRoots(string startFolder)
        {
            if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
            {
                var item = MakeFolderNode(startFolder, startFolder);
                item.IsExpanded = true;
                _tree.Items.Add(item);
                return;
            }

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (IOException) { drives = new DriveInfo[0]; }

            foreach (var drive in drives)
            {
                try
                {
                    if (!drive.IsReady) continue;
                    _tree.Items.Add(MakeFolderNode(drive.RootDirectory.FullName, drive.Name));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private TreeViewItem MakeFolderNode(string path, string label)
        {
            bool importable = MainWindow.CanImportFolder(path);

            var item = new TreeViewItem
            {
                Header = new TextBlock
                {
                    Text = label,

                    // Dimmed rather than disabled: a folder with nothing to import may
                    // still contain subfolders that do, so it has to stay expandable.
                    Opacity = importable ? 1.0 : 0.45
                },
                Tag = path,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            item.Items.Add(Placeholder);
            item.Expanded += Node_Expanded;
            return item;
        }

        private void Node_Expanded(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, e.OriginalSource)) return;
            if (sender is not TreeViewItem item) return;
            if (item.Items.Count != 1 || item.Items[0] is not string) return;

            item.Items.Clear();
            string path = item.Tag as string;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    var info = new DirectoryInfo(dir);
                    if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                    if ((info.Attributes & FileAttributes.System) != 0) continue;
                    item.Items.Add(MakeFolderNode(info.FullName, info.Name));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            string path = (e.NewValue as TreeViewItem)?.Tag as string;

            if (string.IsNullOrEmpty(path))
            {
                _selected = null;
                _import.IsEnabled = false;
                _status.Text = "";
                return;
            }

            int count = MainWindow.CompatibleFilesIn(path).Count;

            if (count == 0)
            {
                // Selecting it is harmless; importing it is what stays blocked.
                _selected = null;
                _import.IsEnabled = false;
                _status.Text = "This folder has nothing DinoLino can import.";
                return;
            }

            _selected = path;
            _import.IsEnabled = true;
            _status.Text = count == 1
                ? "1 file will be imported from this folder."
                : $"{count} files will be imported from this folder.";
        }
    }
}