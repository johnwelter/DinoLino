using System.Windows.Controls;

namespace DinoLino.Utilities.Modes
{
    public partial class OutlineControlPanel : UserControl
    {
        public OutlineControlPanel(OutlineMode mode)
        {
            InitializeComponent();
            DataContext = mode;
        }

        private void RadioButton_Checked(object sender, System.Windows.RoutedEventArgs e)
        {

        }
    }
}