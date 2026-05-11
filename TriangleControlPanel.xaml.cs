using DinoLino.Utilities.Modes;
using System.Windows.Controls;

namespace DinoLino.Utilities.Modes
{
    public partial class TriangleControlPanel : UserControl
    {
        public TriangleControlPanel(GetAngleMode mode)
        {
            InitializeComponent();
            DataContext = mode;
        }
    }
}