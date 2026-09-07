using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Gives every pop-up a tool-window frame and an owner.
    /// </summary>
    /// <remarks>
    /// A tool-window frame draws one caption button, close. The window still drags
    /// by its title bar, still resizes from its edges, and still maximizes and
    /// restores by double-clicking the title bar. Removing the minimize button on a
    /// standard frame is not possible without removing maximize with it, because
    /// Windows draws the two together.
    /// </remarks>
    public static class PopupChrome
    {
        /// <summary>
        /// Registers a class handler so every window loaded from now on gets the
        /// pop-up frame. Call once at startup, before any pop-up is shown.
        /// </summary>
        /// <param name="mainWindow">
        /// The window that keeps its normal frame, and the owner adopted by any
        /// pop-up opened without one.
        /// </param>
        public static void ApplyToPopups(Window mainWindow)
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, e) =>
                {
                    if (sender is Window window && !ReferenceEquals(window, mainWindow))
                        Apply(window, mainWindow);
                }));
        }

        private static void Apply(Window popup, Window mainWindow)
        {
            // Without an owner a pop-up is a window in its own right: it keeps
            // sitting over the desktop once the main window is out of the way, and
            // it can end up behind the window it belongs to.
            if (popup.Owner == null && mainWindow != null && mainWindow.IsLoaded)
                popup.Owner = mainWindow;

            // A window built for transparency cannot change frame, and one that
            // already asked for a particular frame keeps it.
            if (popup.AllowsTransparency) return;
            if (popup.WindowStyle != WindowStyle.SingleBorderWindow) return;

            popup.WindowStyle = WindowStyle.ToolWindow;
        }
    }
}