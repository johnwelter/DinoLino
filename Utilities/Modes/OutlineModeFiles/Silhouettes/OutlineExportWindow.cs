using DinoLino.Utilities;
using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DinoLino
{
    /// Workshop ▸ 2D Outlines. Collects the folder, file type, and canvas size for the
    /// silhouette export. Built in code like EditImageCacheWindow.
    public class OutlineExportWindow : Window
    {
        private readonly TextBox _folderBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ComboBox _formatBox = new ComboBox();
        private readonly ComboBox _sizeBox = new ComboBox { IsEditable = true };
        private readonly CheckBox _scaleBox = new CheckBox();
        private readonly CheckBox _alignBox = new CheckBox();
        private readonly TextBlock _countText = new TextBlock { FontWeight = FontWeights.Bold };
        private readonly TextBlock _warningText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DarkOrange,
            Visibility = Visibility.Collapsed
        };

        private readonly OutlineExportAvailability _availability;

        /// <summary>Options chosen by the user; valid once ShowDialog returns true.</summary>
        public OutlineExportOptions Options { get; } = new OutlineExportOptions();

        public OutlineExportWindow(OutlineExportAvailability availability)
        {
            _availability = availability ?? new OutlineExportAvailability();

            Title = "Export 2D Outlines";
            Width = 470;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(16) };

            _countText.Margin = new Thickness(0, 0, 0, 4);
            root.Children.Add(_countText);

            root.Children.Add(new TextBlock
            {
                Text = "Each outline is saved as a black silhouette, centred on a white square canvas.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 14)
            });

            // ---- Size ----
            // Off by default: the traced sizes are real data, and standardizing them
            // away is a choice the user makes rather than one made for them.

            _scaleBox.Content = "Scale";
            _scaleBox.IsChecked = false;
            _scaleBox.Margin = new Thickness(0, 0, 0, 4);
            _scaleBox.ToolTip = "Give every exported silhouette the same area";
            root.Children.Add(_scaleBox);

            root.Children.Add(new TextBlock
            {
                Text = "Resizes every silhouette to the same area, so the shapes are compared " +
                       "with size taken out of it. Left off, each outline keeps the size it was " +
                       "traced at and a larger one exports larger.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(20, 0, 0, 12)
            });

            // ---- Orientation ----

            _alignBox.Content = "Align orientation";
            _alignBox.IsChecked = true;
            _alignBox.Margin = new Thickness(0, 0, 0, 4);
            _alignBox.Checked += (s2, e2) => RefreshAlignState();
            _alignBox.Unchecked += (s2, e2) => RefreshAlignState();
            root.Children.Add(_alignBox);

            root.Children.Add(new TextBlock
            {
                Text = "Rotates each outline onto its long axis.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(20, 0, 0, 8)
            });

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

            root.Children.Add(new TextBlock { Text = "Canvas size (pixels, square):", Margin = new Thickness(0, 0, 0, 4) });

            foreach (var preset in new[] { "256", "512", "1024", "2048" })
                _sizeBox.Items.Add(preset);
            _sizeBox.Text = "512";
            _sizeBox.Margin = new Thickness(0, 0, 0, 16);
            root.Children.Add(_sizeBox);

            // ---- Buttons ----

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var ok = new Button { Content = "Export", Width = 84, IsDefault = true };
            ok.Click += Ok_Click;
            buttons.Children.Add(ok);

            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                Width = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            });

            root.Children.Add(buttons);
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
            if (_alignBox.IsChecked == true && _availability.UnstableRotationCount > 0)
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
                MessageBox.Show(this, "Please choose an existing destination folder.",
                    "Export 2D Outlines", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_sizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)
                || size < 64 || size > 8192)
            {
                MessageBox.Show(this, "Please enter a canvas size between 64 and 8192 pixels.",
                    "Export 2D Outlines", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Options.Folder = folder;
            Options.CanvasSize = size;
            Options.ScaleToCommonArea = _scaleBox.IsChecked == true;
            Options.AlignRotation = _alignBox.IsChecked == true;
            Options.Format = (_formatBox.SelectedItem as ComboBoxItem)?.Tag is OutlineImageFormat f
                ? f
                : OutlineImageFormat.Png;

            // Margin scales with the canvas so the framing looks the same at any size.
            Options.Margin = Math.Max(4, size * 0.03);

            // DialogResult may only be set while running modally; guard it the same way
            // ScaleWindow does.
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