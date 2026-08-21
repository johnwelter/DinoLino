using DinoLino.Utilities;
using System;
using System.Windows;

namespace DinoLino
{
    /// Modeless companion to the axis-drawing capture: it stays open while the user
    /// clicks on the workspace, so the main window has to remain interactive.
    public partial class AlignWindow : Window
    {
        /// <summary>Raised when the user accepts the drawn axis.</summary>
        public event Action Accepted;

        /// <summary>Raised when the user wants to discard the line and draw again.</summary>
        public event Action RetryRequested;

        /// The axis the drawn line represents. Read at the moment of acceptance, so
        /// switching X and Y reinterprets a line already on screen.
        public AlignmentAxis SelectedAxis =>
            UI_AxisY.IsChecked == true ? AlignmentAxis.Y : AlignmentAxis.X;

        public AlignWindow() => InitializeComponent();

        /// <summary>Reports whether a usable axis line exists on the workspace.</summary>
        public void SetLineReady(bool ready)
        {
            UI_Accept.IsEnabled = ready;
            UI_Status.Text = ready
                ? "Axis drawn. Accept to set the alignment, or Retry to draw it again."
                : "Click two points on the image to draw the axis.";
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            Accepted?.Invoke();
            Close();
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            RetryRequested?.Invoke();
            SetLineReady(false);
        }

        /// <summary>Abandons the capture. Closing is what tears it down.</summary>
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}