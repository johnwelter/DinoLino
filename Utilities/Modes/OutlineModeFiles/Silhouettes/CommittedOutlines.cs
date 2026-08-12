using DinoLino.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DinoLino.Utilities
{
    /// <summary>
    /// One outline the user deliberately kept as a silhouette. The points are a
    /// snapshot taken at commit time, so undoing the operation, editing it in the
    /// Batch Workshop, or redrawing over it leaves this entry untouched.
    /// </summary>
    public sealed class CommittedOutline
    {
        /// <summary>Specimen the outline was drawn on, as named at commit time.</summary>
        public string SpecimenName;

        /// <summary>File stem the user chose; also what the batch export names it.</summary>
        public string Name;

        /// <summary>Canvas-space points, closure duplicate stripped.</summary>
        public List<Point> Points;
    }

    /// <summary>
    /// Session-scoped store of the outlines the user has committed as silhouettes,
    /// plus the settings remembered when the commit window is switched off. Only
    /// what lives here is written by Batch Workshop ▸ Export 2D Outlines.
    /// </summary>
    public static class CommittedOutlineStore
    {
        public const int MaxNameLength = 60;

        private static readonly List<CommittedOutline> _outlines = new List<CommittedOutline>();

        /// <summary>Committed outlines in the order they were stored.</summary>
        public static IReadOnlyList<CommittedOutline> Outlines => _outlines;

        public static int Count => _outlines.Count;

        // ---- Remembered dialog settings ----
        // Set only by "Don't show window again"; the batch export keeps asking for
        // its own transformations regardless of what is remembered here.

        public static bool SuppressDialog { get; private set; }
        public static bool DefaultScaleToCommonArea { get; private set; } = false;
        public static bool DefaultAlignRotation { get; private set; } = true;
        public static int DefaultCanvasSize { get; private set; } = 512;

        /// Stores the settings the user just used, and whether the window should be
        /// skipped from now on.
        public static void RememberDefaults(OutlineExportOptions options, bool suppress)
        {
            if (options != null)
            {
                DefaultScaleToCommonArea = options.ScaleToCommonArea;
                DefaultAlignRotation = options.AlignRotation;
                DefaultCanvasSize = options.CanvasSize;
            }

            if (suppress) SuppressDialog = true;
        }

        // ---- Store ----

        /// <summary>Trims a typed name and caps its length.</summary>
        public static string CleanName(string name)
        {
            name = (name ?? "").Trim();
            return name.Length <= MaxNameLength ? name : name.Substring(0, MaxNameLength);
        }

        public static bool NameInUse(string name)
        {
            foreach (var outline in _outlines)
                if (string.Equals(outline.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>How many silhouettes are stored for one specimen.</summary>
        public static int CountFor(string specimenName)
        {
            int n = 0;
            foreach (var outline in _outlines)
                if (string.Equals(outline.SpecimenName, specimenName, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>Every silhouette stored for one specimen, in store order.</summary>
        public static List<CommittedOutline> OutlinesFor(string specimenName)
        {
            var found = new List<CommittedOutline>();

            foreach (var outline in _outlines)
                if (string.Equals(outline.SpecimenName, specimenName, StringComparison.Ordinal))
                    found.Add(outline);

            return found;
        }


        /// Suggested name: the specimen's name, an underscore, and the number this
        /// silhouette is for that specimen. Bumped past any name already taken.
        public static string SuggestName(string specimenName)
        {
            string root = string.IsNullOrWhiteSpace(specimenName) ? "specimen" : specimenName.Trim();

            int n = CountFor(specimenName) + 1;
            string candidate = CleanName(root + "_" + n);

            while (NameInUse(candidate))
                candidate = CleanName(root + "_" + (++n));

            return candidate;
        }

        /// Stores a copy of the outline. Returns the entry, or null when the outline
        /// is too small to be a shape.
        public static CommittedOutline Add(string specimenName, string name, IEnumerable<Point> points)
        {
            if (points == null) return null;

            var copy = new List<Point>(points);
            PolylineGeometry.StripClosureDuplicate(copy);
            if (copy.Count < 3) return null;

            string clean = CleanName(name);
            if (clean.Length == 0) clean = SuggestName(specimenName);

            // Two silhouettes sharing a name would overwrite each other on export.
            if (NameInUse(clean))
            {
                string stem = clean;
                for (int n = 2; ; n++)
                {
                    clean = CleanName(stem + "_" + n);
                    if (!NameInUse(clean)) break;
                }
            }

            var entry = new CommittedOutline
            {
                SpecimenName = specimenName ?? "",
                Name = clean,
                Points = copy
            };

            _outlines.Add(entry);
            return entry;
        }

        /// Renames a stored outline. Returns false when the name is blank or already
        /// taken, since the export builds filenames from it and two silhouettes
        /// sharing a name would overwrite each other.
        public static bool Rename(CommittedOutline outline, string newName)
        {
            if (outline == null) return false;

            string clean = CleanName(newName);
            if (clean.Length == 0) return false;

            // Re-entering the same name, or only its casing, is not a collision.
            if (string.Equals(clean, outline.Name, StringComparison.OrdinalIgnoreCase))
            {
                outline.Name = clean;
                return true;
            }

            foreach (var other in _outlines)
            {
                if (ReferenceEquals(other, outline)) continue;
                if (string.Equals(other.Name, clean, StringComparison.OrdinalIgnoreCase)) return false;
            }

            outline.Name = clean;
            return true;
        }

        /// Adds a copy directly after the original: the same shape under the next free
        /// letter. Returns the copy, or null when the outline is not in the store.
        public static CommittedOutline Duplicate(CommittedOutline outline)
        {
            if (outline == null) return null;

            int at = _outlines.IndexOf(outline);
            if (at < 0) return null;

            var copy = new CommittedOutline
            {
                SpecimenName = outline.SpecimenName,
                Name = DuplicateNameFor(outline.Name),
                Points = new List<Point>(outline.Points)
            };

            _outlines.Insert(at + 1, copy);
            return copy;
        }

        /// The name a duplicate takes: the original's with " B" appended, or the next
        /// free letter after that. A name already ending in one of those letters
        /// continues its original's run rather than starting a nested one, so
        /// duplicating "Specimen 1_1 B" gives "Specimen 1_1 C".
        private static string DuplicateNameFor(string name)
        {
            string root = StripDuplicateSuffix(name);

            // The run this name belongs to, then — once its letters are used up — a
            // fresh run off the whole name.
            string candidate = FirstFreeLetter(root)
                            ?? (root == name ? null : FirstFreeLetter(name));

            if (candidate != null) return candidate;

            // Past the alphabet twice over. From here the names only have to stay
            // distinct.
            for (int n = 2; ; n++)
            {
                candidate = WithSuffix(name, " B" + n);
                if (!NameInUse(candidate)) return candidate;
            }
        }

        // First of "<root> B" … "<root> Z" nothing is using, or null when all are taken.
        private static string FirstFreeLetter(string root)
        {
            for (char suffix = 'B'; suffix <= 'Z'; suffix++)
            {
                string candidate = WithSuffix(root, " " + suffix);
                if (!NameInUse(candidate)) return candidate;
            }

            return null;
        }

        // Appends a suffix, trimming the stem rather than the suffix when the result
        // would run past the length cap. Clipping the suffix off instead would make
        // every candidate for a maximum-length name come back identical, and no free
        // name could ever be found.
        private static string WithSuffix(string stem, string suffix)
        {
            int room = MaxNameLength - suffix.Length;
            if (room < 1) return CleanName(suffix);

            stem = (stem ?? "").Trim();
            if (stem.Length > room) stem = stem.Substring(0, room).TrimEnd();

            return stem + suffix;
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

        public static bool Remove(CommittedOutline outline) => _outlines.Remove(outline);

        /// <summary>Drops every silhouette stored for one specimen.</summary>
        public static void RemoveFor(string specimenName) =>
            _outlines.RemoveAll(o => string.Equals(o.SpecimenName, specimenName, StringComparison.Ordinal));

        /// <summary>Empties the store. Called alongside the other session resets.</summary>
        public static void Clear() => _outlines.Clear();
    }
}

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
    /// "Store 2D Outline As Silhouette": names the outline and picks the same
    /// normalization options the batch export offers, for this one silhouette.
    /// Built in code like OutlineExportWindow, whose wording and defaults it follows.
    /// </summary>
    internal class CommitOutlineWindow : Window
    {
        private readonly CheckBox _scaleBox = new CheckBox();
        private readonly CheckBox _alignBox = new CheckBox();
        private readonly ComboBox _sizeBox = new ComboBox { IsEditable = true };
        private readonly TextBox _nameBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly CheckBox _suppressBox = new CheckBox();

        /// <summary>Which button closed the window.</summary>
        public CommitOutlineAction Action { get; private set; } = CommitOutlineAction.Cancel;

        /// <summary>Options chosen; valid once ShowDialog returns true.</summary>
        public OutlineExportOptions Options { get; } = new OutlineExportOptions();

        /// <summary>Name typed for this silhouette.</summary>
        public string OutlineName => CommittedOutlineStore.CleanName(_nameBox.Text);

        /// <summary>True when the user asked not to be shown this window again.</summary>
        public bool SuppressFuture => _suppressBox.IsChecked == true;

        public CommitOutlineWindow(string suggestedName)
        {
            Title = "Store 2D Outline As Silhouette";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "The outline is kept as a black silhouette on a white square canvas. " +
                       "Only stored outlines are written by Batch Workshop ▸ Export 2D Outlines.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 14)
            });

            // ---- Size ----

            _scaleBox.Content = "Scale outline";
            _scaleBox.IsChecked = CommittedOutlineStore.DefaultScaleToCommonArea;
            _scaleBox.Margin = new Thickness(0, 0, 0, 4);
            _scaleBox.ToolTip = "Resize this silhouette to the standard area";
            root.Children.Add(_scaleBox);

            root.Children.Add(new TextBlock
            {
                Text = "Resizes the silhouette to the standard area, so shapes are compared with " +
                       "size taken out of it. Left off, the outline keeps the size it was traced at.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(20, 0, 0, 12)
            });

            // ---- Orientation ----

            _alignBox.Content = "Align orientation";
            _alignBox.IsChecked = CommittedOutlineStore.DefaultAlignRotation;
            _alignBox.Margin = new Thickness(0, 0, 0, 4);
            root.Children.Add(_alignBox);

            root.Children.Add(new TextBlock
            {
                Text = "Rotates outline onto its long axis",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(20, 0, 0, 14)
            });

            // ---- Canvas size ----

            var sizeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var sizeLabel = new TextBlock
            {
                Text = "Canvas size",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            DockPanel.SetDock(sizeLabel, Dock.Left);
            sizeRow.Children.Add(sizeLabel);

            foreach (var preset in new[] { "256", "512", "1024", "2048" })
                _sizeBox.Items.Add(preset);
            _sizeBox.Text = CommittedOutlineStore.DefaultCanvasSize.ToString(CultureInfo.InvariantCulture);
            _sizeBox.ToolTip = "Width and height of the square canvas, in pixels";
            sizeRow.Children.Add(_sizeBox);
            root.Children.Add(sizeRow);

            // ---- Name ----

            root.Children.Add(new TextBlock { Text = "Outline name:", Margin = new Thickness(0, 0, 0, 4) });

            _nameBox.Text = suggestedName ?? "";
            _nameBox.MaxLength = 60;
            _nameBox.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(_nameBox);

            // ---- Remember ----

            _suppressBox.Content = "Don't show window again";
            _suppressBox.Margin = new Thickness(0, 0, 0, 2);
            root.Children.Add(_suppressBox);

            root.Children.Add(new TextBlock
            {
                Text = "(Save these settings for future use, and use auto-generated specimen names.)",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(20, 0, 0, 16)
            });

            // ---- Buttons ----

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var store = new Button { Content = "Store", MinWidth = 84, IsDefault = true };
            store.Click += (s, e) => Finish(CommitOutlineAction.Store);
            buttons.Children.Add(store);

            var export = new Button
            {
                Content = "Export as png",
                MinWidth = 110,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Store the silhouette and write it to the working directory now"
            };
            export.Click += (s, e) => Finish(CommitOutlineAction.ExportPng);
            buttons.Children.Add(export);

            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            });

            root.Children.Add(buttons);
            Content = root;

            Loaded += (s, e) => { _nameBox.SelectAll(); _nameBox.Focus(); };
        }

        // Validates, fills Options, and closes with the pressed action recorded.
        private void Finish(CommitOutlineAction action)
        {
            string name = OutlineName;

            if (name.Length == 0)
            {
                MessageBox.Show(this, "Please enter a name for this outline.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CommittedOutlineStore.NameInUse(name))
            {
                MessageBox.Show(this, $"\"{name}\" is already used by another stored outline.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_sizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)
                || size < 64 || size > 8192)
            {
                MessageBox.Show(this, "Please enter a canvas size between 64 and 8192 pixels.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Options.Format = OutlineImageFormat.Png;
            Options.CanvasSize = size;
            Options.ScaleToCommonArea = _scaleBox.IsChecked == true;
            Options.AlignRotation = _alignBox.IsChecked == true;

            // Margin scales with the canvas, as in OutlineExportWindow.
            Options.Margin = Math.Max(4, size * 0.03);

            Action = action;

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
        }
    }
}