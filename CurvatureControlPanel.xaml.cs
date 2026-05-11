using System.Windows;
using System.Windows.Controls;

namespace DinoLino.Utilities.Modes
{
    public partial class CurvatureControlPanel : UserControl
    {
        private CurvatureMode _mode;

        public CurvatureControlPanel(CurvatureMode mode)
        {
            InitializeComponent();
            _mode = mode;
            DataContext = mode; // bindings in XAML resolve against the mode directly
        }

        private void CircularArc_Checked(object sender, RoutedEventArgs e)
            => _mode.SelectCircularArc();

        private void ParabolicArc_Checked(object sender, RoutedEventArgs e)
            => _mode.SelectParabolicArc();

        private void NPointSpline_Checked(object sender, RoutedEventArgs e)
            => _mode.SelectNPointSpline();
    }
}