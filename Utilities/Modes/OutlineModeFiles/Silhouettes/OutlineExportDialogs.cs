using DinoLino.Utilities;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino
{
    /// <summary>What the user pressed in the commit window.</summary>
    public enum CommitOutlineAction
    {
        Cancel,
        Store,
        ExportPng
    }

    /// <summary>
    /// The parts both silhouette dialogs are made of. "Store 2D Outline As
    /// Silhouette" and "Export 2D Outlines" ask the same three questions — scale to
    /// a common area, align orientation, canvas size — validate them the same way,
    /// and close the same way; they differ only in what surrounds those questions.
    /// Keeping the shared half here means the wording and the accepted size range
    /// can't drift apart between one silhouette and a batch of them.
    /// </summary>
    public abstract class OutlineOptionsWindow : Window
    {
        private const int MinCanvasSize = 64;
        private const int MaxCanvasSize = 8192;
        private static readonly string[] CanvasPresets = { "256", "512", "1024", "2048" };

        protected readonly CheckBox ScaleBox = new CheckBox();
        protected readonly CheckBox AlignBox = new CheckBox();
        protected readonly ComboBox SizeBox = new ComboBox { IsEditable = true };

        /// <summary>Options chosen by the user; valid once ShowDialog returns true.</summary>
        public OutlineExportOptions Options { get; } = new OutlineExportOptions();

        protected OutlineOptionsWindow(string title, double width)
        {
            Title = title;
            Width = width;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
        }

        // ---- Shared controls ----

        /// <summary>Grey explanatory paragraph, optionally indented under the control it describes.</summary>
        protected static TextBlock Note(string text, double indent = 0, double bottomMargin = 12) =>
            new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(indent, 0, 0, bottomMargin)
            };

        /// The size-standardization checkbox and its explanation. Off by default in
        /// both dialogs: the traced sizes are real data, and standardizing them away
        /// is a choice the user makes rather than one made for them.
        protected void AddScaleOption(Panel root, string label, string tooltip, string description, bool initial)
        {
            ScaleBox.Content = label;
            ScaleBox.IsChecked = initial;
            ScaleBox.ToolTip = tooltip;
            ScaleBox.Margin = new Thickness(0, 0, 0, 4);
            root.Children.Add(ScaleBox);
            root.Children.Add(Note(description, indent: 20));
        }

        /// <summary>The orientation checkbox and its explanation.</summary>
        protected void AddAlignOption(Panel root, string description, bool initial, double bottomMargin = 14)
        {
            AlignBox.Content = "Align orientation";
            AlignBox.IsChecked = initial;
            AlignBox.Margin = new Thickness(0, 0, 0, 4);
            root.Children.Add(AlignBox);
            root.Children.Add(Note(description, indent: 20, bottomMargin: bottomMargin));
        }

        /// The canvas-size combo. `labelInline` puts the label beside the box (the
        /// commit window's compact layout) rather than above it.
        protected void AddCanvasSizeRow(Panel root, string label, int initialSize,
            bool labelInline, string tooltip = null)
        {
            foreach (string preset in CanvasPresets)
                SizeBox.Items.Add(preset);

            SizeBox.Text = initialSize.ToString(CultureInfo.InvariantCulture);
            SizeBox.ToolTip = tooltip;

            if (labelInline)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
                var text = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                DockPanel.SetDock(text, Dock.Left);
                row.Children.Add(text);
                row.Children.Add(SizeBox);
                root.Children.Add(row);
                return;
            }

            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            SizeBox.Margin = new Thickness(0, 0, 0, 16);
            root.Children.Add(SizeBox);
        }

        /// <summary>Right-aligned row of dialog buttons, in the order given.</summary>
        protected static StackPanel ButtonRow(params Button[] buttons)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            foreach (Button button in buttons)
                row.Children.Add(button);

            return row;
        }

        // ---- Shared validation and closing ----

        /// Reads the canvas size and the two normalization checkboxes into Options.
        /// Returns false — having already told the user why — when the size is not
        /// usable, so a caller can simply bail out.
        protected bool TryApplySharedOptions()
        {
            if (!int.TryParse(SizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)
                || size < MinCanvasSize || size > MaxCanvasSize)
            {
                Warn($"Please enter a canvas size between {MinCanvasSize} and {MaxCanvasSize} pixels.");
                return false;
            }

            Options.CanvasSize = size;
            Options.ScaleToCommonArea = ScaleBox.IsChecked == true;
            Options.AlignRotation = AlignBox.IsChecked == true;

            // Margin scales with the canvas so the framing looks the same at any size.
            Options.Margin = Math.Max(4, size * 0.03);
            return true;
        }

        protected void Warn(string message) =>
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

        /// Closes with a positive result. DialogResult may only be set while running
        /// modally, so it is guarded the same way ScaleWindow does.
        protected void CloseAsAccepted()
        {
            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }
    }

    /// <summary>
    /// "Store 2D Outline As Silhouette": names one outline and picks the same
    /// normalization options the batch export offers, for that one silhouette.
    /// </summary>
    internal class CommitOutlineWindow : OutlineOptionsWindow
    {
        private readonly TextBox _nameBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly CheckBox _suppressBox = new CheckBox();

        /// <summary>Which button closed the window.</summary>
        public CommitOutlineAction Action { get; private set; } = CommitOutlineAction.Cancel;

        /// <summary>Name typed for this silhouette.</summary>
        public string OutlineName => CommittedOutlineStore.CleanName(_nameBox.Text);

        /// <summary>True when the user asked not to be shown this window again.</summary>
        public bool SuppressFuture => _suppressBox.IsChecked == true;

        public CommitOutlineWindow(string suggestedName)
            : base("Store 2D Outline As Silhouette", 440)
        {
            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(Note(
                "The outline is kept as a black silhouette on a white square canvas. " +
                "Only stored outlines are written by Batch Workshop ▸ Export 2D Outlines.",
                bottomMargin: 14));

            AddScaleOption(root,
                label: "Scale outline",
                tooltip: "Resize this silhouette to the standard area",
                description: "Resizes the silhouette to the standard area, so shapes are compared with " +
                             "size taken out of it. Left off, the outline keeps the size it was traced at.",
                initial: CommittedOutlineStore.DefaultScaleToCommonArea);

            AddAlignOption(root,
                description: "Rotates outline onto its long axis",
                initial: CommittedOutlineStore.DefaultAlignRotation);

            AddCanvasSizeRow(root,
                label: "Canvas size",
                initialSize: CommittedOutlineStore.DefaultCanvasSize,
                labelInline: true,
                tooltip: "Width and height of the square canvas, in pixels");

            // ---- Name ----

            root.Children.Add(new TextBlock { Text = "Outline name:", Margin = new Thickness(0, 0, 0, 4) });

            _nameBox.Text = suggestedName ?? "";
            _nameBox.MaxLength = CommittedOutlineStore.MaxNameLength;
            _nameBox.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(_nameBox);

            // ---- Remember ----

            _suppressBox.Content = "Don't show window again";
            _suppressBox.Margin = new Thickness(0, 0, 0, 2);
            root.Children.Add(_suppressBox);

            root.Children.Add(Note(
                "(Save these settings for future use, and use auto-generated specimen names.)",
                indent: 20, bottomMargin: 16));

            // ---- Buttons ----

            var store = new Button { Content = "Store", MinWidth = 84, IsDefault = true };
            store.Click += (s, e) => Finish(CommitOutlineAction.Store);

            var export = new Button
            {
                Content = "Export as png",
                MinWidth = 110,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Store the silhouette and write it to the working directory now"
            };
            export.Click += (s, e) => Finish(CommitOutlineAction.ExportPng);

            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            root.Children.Add(ButtonRow(store, export, cancel));
            Content = root;

            Loaded += (s, e) => { _nameBox.SelectAll(); _nameBox.Focus(); };
        }

        // Validates, fills Options, and closes with the pressed action recorded.
        private void Finish(CommitOutlineAction action)
        {
            string name = OutlineName;

            if (name.Length == 0)
            {
                Warn("Please enter a name for this outline.");
                return;
            }

            // A typed name is refused rather than numbered off, so the user can see
            // the clash and pick something else (see CommittedOutlineStore ▸ Names).
            if (CommittedOutlineStore.NameInUse(name))
            {
                Warn($"\"{name}\" is already used by another stored outline.");
                return;
            }

            if (!TryApplySharedOptions()) return;

            Options.Format = OutlineImageFormat.Png;
            Action = action;
            CloseAsAccepted();
        }
    }

    /// Workshop ▸ 2D Outlines. Collects the folder, file type, and canvas size for
    /// the whole-batch silhouette export.
    public class OutlineExportWindow : OutlineOptionsWindow
    {
        private readonly TextBox _folderBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox _formatBox = new ComboBox();
        private readonly TextBlock _countText = new TextBlock { FontWeight = FontWeights.Bold };
        private readonly TextBlock _warningText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DarkOrange,
            Visibility = Visibility.Collapsed
        };

        private readonly OutlineExportAvailability _availability;

        public OutlineExportWindow(OutlineExportAvailability availability)
            : base("Export 2D Outlines", 470)
        {
            _availability = availability ?? new OutlineExportAvailability();

            var root = new StackPanel { Margin = new Thickness(16) };

            _countText.Margin = new Thickness(0, 0, 0, 4);
            root.Children.Add(_countText);

            root.Children.Add(Note(
                "Each outline is saved as a black silhouette, centred on a white square canvas.",
                bottomMargin: 14));

            AddScaleOption(root,
                label: "Scale",
                tooltip: "Give every exported silhouette the same area",
                description: "Resizes every silhouette to the same area, so the shapes are compared " +
                             "with size taken out of it. Left off, each outline keeps the size it was " +
                             "traced at and a larger one exports larger.",
                initial: false);

            AddAlignOption(root,
                description: "Rotates each outline onto its long axis.",
                initial: true,
                bottomMargin: 8);

            // Only the batch warns about unrotatable shapes: it is the one that can
            // have several, and the count has to follow the checkbox.
            AlignBox.Checked += (s, e) => RefreshAlignState();
            AlignBox.Unchecked += (s, e) => RefreshAlignState();

            _warningText.Margin = new Thickness(20, 0, 0, 12);
            root.Children.Add(_warningText);

            // ---- Folder ----

            root.Children.Add(new TextBlock { Text = "Destination folder:", Margin = new Thickness(0, 0, 0, 4) });

            var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var browse = new Button
            {
                Content = "Browse…",
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 2, 6, 2)
            };
            browse.Click += Browse_Click;
            DockPanel.SetDock(browse, Dock.Right);
            folderRow.Children.Add(browse);
            folderRow.Children.Add(_folderBox);
            root.Children.Add(folderRow);

            // ---- File type ----

            root.Children.Add(new TextBlock { Text = "File type:", Margin = new Thickness(0, 0, 0, 4) });

            // Tag carries the format so the selection maps back without string parsing.
            _formatBox.Items.Add(new ComboBoxItem { Content = "PNG (.png)", Tag = OutlineImageFormat.Png });
            _formatBox.Items.Add(new ComboBoxItem { Content = "TIFF (.tif)", Tag = OutlineImageFormat.Tiff });
            _formatBox.Items.Add(new ComboBoxItem { Content = "BMP (.bmp)", Tag = OutlineImageFormat.Bmp });
            _formatBox.Items.Add(new ComboBoxItem { Content = "JPEG (.jpg)", Tag = OutlineImageFormat.Jpeg });
            _formatBox.Items.Add(new ComboBoxItem { Content = "SVG (.svg) — vector", Tag = OutlineImageFormat.Svg });
            _formatBox.SelectedIndex = 0;
            _formatBox.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(_formatBox);

            // ---- Canvas size ----

            AddCanvasSizeRow(root,
                label: "Canvas size (pixels, square):",
                initialSize: 512,
                labelInline: false);

            // ---- Buttons ----

            var ok = new Button { Content = "Export", Width = 84, IsDefault = true };
            ok.Click += Ok_Click;

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            root.Children.Add(ButtonRow(ok, cancel));
            Content = root;

            RefreshAlignState();
        }

        /// Keeps the warning note in step with the orientation checkbox.
        private void RefreshAlignState()
        {
            int count = _availability.TracedCount;
            _countText.Text = count == 1
                ? "1 outline will be exported."
                : $"{count} outlines will be exported.";

            // Near-circular outlines have no dominant axis, so they are exported as
            // traced rather than given an arbitrary rotation.
            if (AlignBox.IsChecked == true && _availability.UnstableRotationCount > 0)
            {
                _warningText.Text = _availability.UnstableRotationCount == 1
                    ? "\u26a0 1 outline is too close to circular to have a reliable orientation; it will be exported unrotated."
                    : $"\u26a0 {_availability.UnstableRotationCount} outlines are too close to circular to have a reliable orientation; they will be exported unrotated.";
                _warningText.Visibility = Visibility.Visible;
            }
            else
            {
                _warningText.Visibility = Visibility.Collapsed;
            }
        }

        /// WPF has no folder picker on this framework, so a save dialog stands in: the
        /// user opens the destination folder and confirms, and only the directory is used.
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Open the destination folder, then click Save",
                FileName = "Save here",
                Filter = "Folder|*.none",
                CheckPathExists = true,
                OverwritePrompt = false
            };

            if (!string.IsNullOrWhiteSpace(_folderBox.Text) && Directory.Exists(_folderBox.Text))
                dlg.InitialDirectory = _folderBox.Text;

            if (dlg.ShowDialog(this) != true) return;

            string folder = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(folder))
                _folderBox.Text = folder;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string folder = _folderBox.Text?.Trim();

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Warn("Please choose an existing destination folder.");
                return;
            }

            if (!TryApplySharedOptions()) return;

            Options.Folder = folder;
            Options.Format = (_formatBox.SelectedItem as ComboBoxItem)?.Tag is OutlineImageFormat f
                ? f
                : OutlineImageFormat.Png;

            CloseAsAccepted();
        }
    }

    /// <summary>
    /// Runs "Commit Outline to History" end to end: check there is something to
    /// store, ask for a name and options, put it in the store, and — if the user
    /// asked — write it out straight away.
    ///
    /// This used to be a partial of MainWindow purely to reach three of its fields.
    /// Taking those as arguments instead leaves MainWindow with one line, and lets
    /// the flow be read without hunting through the main window.
    /// </summary>
    internal static class CommitOutlineFlow
    {
        private const string Title = "Store 2D Outline As Silhouette";

        /// <param name="owner">Window the dialogs and message boxes belong to.</param>
        /// <param name="specimenName">Specimen the outline was drawn on, as named now.</param>
        /// <param name="outlinePoints">Canvas-space outline; a closure duplicate is fine.</param>
        /// <param name="workingDirectory">Where "Export as png" writes, or null/empty to ask.</param>
        public static void Run(Window owner, string specimenName,
            IReadOnlyList<Point> outlinePoints, string workingDirectory)
        {
            var points = outlinePoints == null ? new List<Point>() : new List<Point>(outlinePoints);
            PolylineGeometry.StripClosureDuplicate(points);

            if (points.Count < 3)
            {
                Inform(owner,
                    "There is no outline to store.\n\n" +
                    "Draw an outline and generate its metadata first.");
                return;
            }

            // "Don't show window again": store silently with the remembered settings
            // and an auto-generated name.
            if (CommittedOutlineStore.SuppressDialog)
            {
                CommittedOutlineStore.Add(specimenName, CommittedOutlineStore.SuggestName(specimenName), points);
                return;
            }

            var dialog = new CommitOutlineWindow(CommittedOutlineStore.SuggestName(specimenName)) { Owner = owner };
            InheritTypography(dialog, owner);

            if (dialog.ShowDialog() != true || dialog.Action == CommitOutlineAction.Cancel) return;

            CommittedOutlineStore.RememberDefaults(dialog.Options, dialog.SuppressFuture);

            CommittedOutline committed = CommittedOutlineStore.Add(specimenName, dialog.OutlineName, points);
            if (committed == null)
            {
                MessageBox.Show(owner, "That outline is too small to store as a silhouette.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (dialog.Action != CommitOutlineAction.ExportPng)
            {
                Inform(owner,
                    $"\"{committed.Name}\" stored.\n\n" +
                    "It will be written by Batch Workshop ▸ Export 2D Outlines.");
                return;
            }

            ExportNow(owner, committed, dialog.Options, workingDirectory);
        }

        /// Writes the silhouette the user just stored. The working directory is the
        /// destination; without one set, they pick a file instead. The outline is
        /// stored either way — only the file can be declined.
        private static void ExportNow(Window owner, CommittedOutline outline,
            OutlineExportOptions options, string workingDirectory)
        {
            OutlineWriteResult result =
                OutlineShapeExporter.WriteIntoFolder(outline, options, workingDirectory);

            if (result.Outcome == OutlineWriteOutcome.NoDestination)
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Export outline",
                    Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
                    DefaultExt = ".png",
                    FileName = OutlineShapeExporter.SuggestFileName(outline, options.Format),
                    AddExtension = true
                };

                if (dlg.ShowDialog(owner) != true) return;

                result = OutlineShapeExporter.WriteToFile(outline, options, dlg.FileName);
            }

            switch (result.Outcome)
            {
                case OutlineWriteOutcome.Written:
                    Inform(owner, $"\"{outline.Name}\" stored and exported to:\n{result.Path}");
                    break;

                case OutlineWriteOutcome.NotRendered:
                    Inform(owner, "The outline was stored, but it could not be rendered.");
                    break;

                case OutlineWriteOutcome.Failed:
                    MessageBox.Show(owner,
                        $"The outline was stored, but the file could not be written:\n{result.Error}",
                        "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }

        /// A dialog is its own top-level window, so WPF does not pass the owner's
        /// font down to it. Copy it across so the dialog matches the rest of the app.
        private static void InheritTypography(Window dialog, Window owner)
        {
            if (owner == null) return;

            dialog.FontFamily = owner.FontFamily;
            dialog.FontSize = owner.FontSize;
        }

        private static void Inform(Window owner, string message) =>
            MessageBox.Show(owner, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}