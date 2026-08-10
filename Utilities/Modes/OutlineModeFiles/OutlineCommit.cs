using DinoLino.Utilities;
using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;

namespace DinoLino
{
    /// <summary>
    /// Commits one outline to the session's silhouette store, optionally writing it
    /// out straight away. Everything geometric is handled by OutlineShapeExporter,
    /// the same code the Batch Workshop export uses.
    /// </summary>
    public partial class MainWindow
    {
        private const string CommitTitle = "Store 2D Outline As Silhouette";

        /// Raised by the Outline panel's "Commit Outline to History" link.
        private void OutlineCommit_Requested()
        {
            var points = OutlineMode.GetActiveOutlinePoints();
            PolylineGeometry.StripClosureDuplicate(points ?? new System.Collections.Generic.List<Point>());

            if (points == null || points.Count < 3)
            {
                MessageBox.Show(this,
                    "There is no outline to store.\n\n" +
                    "Draw an outline and generate its metadata first.",
                    CommitTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string specimen = SpecimenManager.DisplayName;

            // "Don't show window again": store silently with the remembered settings
            // and an auto-generated name.
            if (CommittedOutlineStore.SuppressDialog)
            {
                CommittedOutlineStore.Add(specimen, CommittedOutlineStore.SuggestName(specimen), points);
                return;
            }

            var dialog = new CommitOutlineWindow(CommittedOutlineStore.SuggestName(specimen))
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            if (dialog.ShowDialog() != true || dialog.Action == CommitOutlineAction.Cancel) return;

            CommittedOutlineStore.RememberDefaults(dialog.Options, dialog.SuppressFuture);

            var committed = CommittedOutlineStore.Add(specimen, dialog.OutlineName, points);
            if (committed == null)
            {
                MessageBox.Show(this,
                    "That outline is too small to store as a silhouette.",
                    CommitTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (dialog.Action == CommitOutlineAction.ExportPng)
                ExportCommittedOutline(committed, dialog.Options);
            else
                MessageBox.Show(this,
                    $"\"{committed.Name}\" stored.\n\n" +
                    "It will be written by Batch Workshop ▸ Export 2D Outlines.",
                    CommitTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// Writes one stored silhouette as a PNG. The working directory is the
        /// destination; without one set, the user picks a file instead.
        private void ExportCommittedOutline(CommittedOutline outline, OutlineExportOptions options)
        {
            try
            {
                if (!string.IsNullOrEmpty(WorkingDirectory) && Directory.Exists(WorkingDirectory))
                {
                    options.Folder = WorkingDirectory;
                    string written = OutlineShapeExporter.ExportOne(outline, options);

                    MessageBox.Show(this,
                        written == null
                            ? "The outline was stored, but it could not be rendered."
                            : $"\"{outline.Name}\" stored and exported to:\n{written}",
                        CommitTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dlg = new SaveFileDialog
                {
                    Title = "Export outline",
                    Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
                    DefaultExt = ".png",
                    FileName = outline.Name + ".png",
                    AddExtension = true
                };

                if (dlg.ShowDialog(this) != true)
                {
                    // The outline is stored either way; only the file was declined.
                    return;
                }

                OutlineShapeExporter.ExportToPath(outline, options, dlg.FileName);

                MessageBox.Show(this,
                    $"\"{outline.Name}\" stored and exported to:\n{dlg.FileName}",
                    CommitTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"The outline was stored, but the file could not be written:\n{ex.Message}",
                    "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}