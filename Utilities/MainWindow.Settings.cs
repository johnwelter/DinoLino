using DinoLino.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino
{
    /// <summary>
    /// Restores the user's View menu preferences when the window opens, and writes
    /// them back when it closes.
    /// </summary>
    /// <remarks>
    /// A toggle is restored by ticking its menu item and then running that item's own
    /// handler, and is saved by reading the same item back. What a toggle actually
    /// does therefore stays described in exactly one place, and no handler needs to
    /// know that preferences are stored at all: a new View menu toggle costs one line
    /// in each of the two methods below and nothing anywhere else.
    /// </remarks>
    public partial class MainWindow
    {
        // =====================
        // Restore
        // =====================

        /// Applies the stored preferences to the window. Called at the end of the
        /// constructor, before the first frame is drawn, so the defaults never flash
        /// past on their way to what the user chose.
        private void ApplyUserSettings()
        {
            var settings = UserSettings.Load();

            UI_SeeTips.IsChecked = settings.SeeTips;
            Menu_SeeTips(UI_SeeTips, new RoutedEventArgs());

            UI_SeePrevOps.IsChecked = settings.SeePreviousOperations;
            Menu_SeePrevOps(UI_SeePrevOps, new RoutedEventArgs());

            UI_SeeAttempts.IsChecked = settings.SeeOperationCount;
            Menu_SeeAttempts(UI_SeeAttempts, new RoutedEventArgs());

            UI_SeeImageAxes.IsChecked = settings.SeeImageAxes;
            Menu_SeeImageAxes(UI_SeeImageAxes, new RoutedEventArgs());

            UI_SeeMiniMap.IsChecked = settings.SeeNavigationWindow;
            Menu_SeeMiniMap(UI_SeeMiniMap, new RoutedEventArgs());

            UI_SeeWorkshop.IsChecked = settings.SeeBatchWorkshop;
            Menu_SeeWorkshop(UI_SeeWorkshop, new RoutedEventArgs());

            UI_SeeDirectory.IsChecked = settings.SeeDirectory;
            Menu_SeeDirectory(UI_SeeDirectory, new RoutedEventArgs());

            UI_SeeRex.IsChecked = settings.SeeRex;
            Menu_SeeRex(UI_SeeRex, new RoutedEventArgs());

            ApplyLineColor(settings.LineColor);

            ApplyFontSize(settings.FontSize);
            ApplyFontFamily(new FontFamily(settings.FontFamily));
        }

        /// Ticks the stored color's radio button and hands the brush to every work
        /// mode, so the menu and all four tabs agree on the color from the first
        /// click. A name the menu no longer offers is ignored, leaving each mode on
        /// its own default.
        private void ApplyLineColor(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;

            RadioButton chosen = FindLineColorButton(tag);
            if (chosen == null) return;

            chosen.IsChecked = true;

            // Converted from the button's own tag rather than the stored text, so the
            // color name always comes from the menu and can never be malformed.
            var brush = (Brush)new BrushConverter().ConvertFromString(chosen.Tag.ToString());

            foreach (var mode in AllWorkModes)
                mode.LineColor = brush;
        }

        private RadioButton FindLineColorButton(string tag)
        {
            foreach (object item in UI_LineColor.Items)
            {
                if (item is RadioButton button &&
                    string.Equals(button.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return button;
                }
            }

            return null;
        }

        // =====================
        // Save
        // =====================

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // A cancelled close leaves the user still working, so nothing they have on
            // screen is final yet.
            if (!e.Cancel) SaveUserSettings();
        }

        /// Records what the View menu is showing at the moment the window closes.
        private void SaveUserSettings()
        {
            new UserSettings
            {
                SeeTips = UI_SeeTips.IsChecked,
                SeePreviousOperations = UI_SeePrevOps.IsChecked,
                SeeOperationCount = UI_SeeAttempts.IsChecked,
                SeeImageAxes = UI_SeeImageAxes.IsChecked,
                SeeNavigationWindow = UI_SeeMiniMap.IsChecked,
                SeeBatchWorkshop = UI_SeeWorkshop.IsChecked,
                SeeDirectory = UI_SeeDirectory.IsChecked,
                SeeRex = UI_SeeRex.IsChecked,

                LineColor = CheckedLineColorTag(),

                FontFamily = _currentFont?.Source,
                FontSize = _currentFontSize
            }
            .Save();
        }

        private string CheckedLineColorTag()
        {
            foreach (object item in UI_LineColor.Items)
            {
                if (item is RadioButton button && button.IsChecked == true)
                    return button.Tag as string;
            }

            return null;
        }
    }
}