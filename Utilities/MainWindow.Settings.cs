using DinoLino.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino
{
    /// <summary>
    /// What File ▸ Save Settings and File ▸ Restore Default Settings do: reading the
    /// stored View settings, applying them, and writing them back.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Startup
        // =====================

        /// Applies whatever was kept from the user's last session. Called at the end of
        /// the constructor, before the first frame is drawn, so the defaults never
        /// flash past on their way to what the user chose.
        private void ApplyUserSettings()
        {
            var settings = UserSettings.Load();

            UI_MenuSaveSettings.IsChecked = settings.SaveSettings;

            // With the switch off, the window keeps the defaults the XAML has already
            // given it, whatever else the file happens to hold.
            if (!settings.SaveSettings) return;

            ApplySettings(settings);
        }

        // =====================
        // Commands
        // =====================

        /// Switching on records what is on screen straight away; switching off forgets
        /// it there and then, rather than waiting for a close that may never come.
        private void SetSaveSettings(bool keep)
        {
            if (keep)
                SaveUserSettings();
            else
                UserSettings.Delete();
        }

        /// Returns the View menu to how the program first opens, and forgets anything
        /// stored. The Save Settings switch is left as the user set it: with it on, the
        /// defaults are simply what gets kept from here.
        private void RestoreDefaultSettings()
        {
            // An unset line color asks for every mode to be left on the color it
            // already has, which is what an ordinary start wants and what a reset does
            // not: a stale color would outlive the tick that named it. So the default
            // is spelled out here.
            ApplySettings(new UserSettings { LineColor = UserSettings.DefaultLineColor });

            UserSettings.Delete();
        }

        // =====================
        // Applying
        // =====================

        private void ApplySettings(UserSettings settings)
        {
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

        /// Ticks a color's radio button and hands the brush to every work mode, so the
        /// menu and all four tabs agree from the first click. A name the menu no longer
        /// offers is ignored, as is none at all.
        private void ApplyLineColor(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;

            RadioButton chosen = FindLineColorButton(tag);
            if (chosen == null) return;

            chosen.IsChecked = true;

            // Converted from the button's own tag rather than the text handed in, so
            // the color name always comes from the menu and can never be malformed.
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
        // Recording
        // =====================

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // A cancelled close leaves the user still working, so nothing on screen is
            // final yet.
            if (e.Cancel) return;

            if (UI_MenuSaveSettings.IsChecked) SaveUserSettings();
        }

        /// Records what the View menu is showing at this moment.
        private void SaveUserSettings()
        {
            new UserSettings
            {
                SaveSettings = UI_MenuSaveSettings.IsChecked,

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