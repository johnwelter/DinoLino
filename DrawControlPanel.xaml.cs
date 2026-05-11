using DinoLino.Utilities.Modes;
using System.Windows;
using System.Windows.Controls;

namespace DinoLino.Utilities.Modes  
{                                    
    public partial class DrawControlPanel : UserControl
    {
        private DrawMode _mode;

        public DrawControlPanel(DrawMode mode)
        {
            InitializeComponent();
            _mode = mode;
            DataContext = mode;
        }

        private void DrawMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) _mode.SelectDrawMethod(rb.Tag?.ToString());
        }

        private void Shape_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) _mode.SelectShape(rb.Tag?.ToString());
        }

        private void LineConstraint_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) _mode.SelectLineConstraint(rb.Tag?.ToString());
        }

        private void DrawAngleValue_TextChanged(object sender, TextChangedEventArgs e)
            => _mode.UpdateAngle(UI_DrawAngleValue.Text);
    }                                
}                                    