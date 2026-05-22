using System.Windows;
using System.Windows.Controls;

namespace DinoLino.Utilities.Modes
{
    public partial class OutlineControlPanel : UserControl
    {
        private OutlineMode _mode;
        public OutlineControlPanel(OutlineMode mode)
        {
            InitializeComponent();
            _mode = mode;
            DataContext = mode;
        }

        private void RadioButton_Checked(object sender, System.Windows.RoutedEventArgs e)
        {

        }

        private void GenerateMetadata_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is OutlineMode mode)
                mode.GenerateMetadata();
        }
    }
}