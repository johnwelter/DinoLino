using System;
using System.IO;
using System.Windows;

namespace DinoLino
{
    public partial class UserGuideWindow : Window
    {
        public UserGuideWindow()
        {
            InitializeComponent();

            string path = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "User Guide.txt");

            if (File.Exists(path))
                GuideText.Text = File.ReadAllText(path);
            else
                GuideText.Text = "User Guide not found.";
        }
    }
}