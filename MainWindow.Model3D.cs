using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DinoLino
{
    /// <summary>
    /// Handles loading, posing, capturing, and re-posing 3D models.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // 3D pose state
        // =====================

        private GeometryModel3D _poseModel;
        private QuaternionRotation3D _modelRotation;
        private double _modelDiag = 1;
        private string _pendingModelName;
        private Point _lastPosePoint;
        private bool _rotatingModel;

        // Fine-tune overlay controls for exact-angle rotation and preset views.
        private ModelPoseController _poseController;
        private System.Windows.Controls.TextBox _poseStepBox;
        private System.Windows.Controls.Border _poseFineControls;

        // Captured model state kept so the same specimen can be re-posed later.
        private MeshGeometry3D _activeMesh;
        private string _activeModelName;
        private Quaternion _lastModelQuaternion = Quaternion.Identity;

        // True when the workspace image is a captured 3D view rather than a native 2D image.
        private bool _workingImageIsModelCapture;

        // True while the pose overlay is being used to re-capture an existing specimen.
        private bool _isRepositioning;

        // =====================
        // Model loading
        // =====================

        private async void Menu_Open3DModel(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open 3D Model",
                Filter = "3D models (*.ply;*.stl;*.obj)|*.ply;*.stl;*.obj|" +
                         "PLY mesh (*.ply)|*.ply|" +
                         "STL mesh (*.stl)|*.stl|" +
                         "OBJ mesh (*.obj)|*.obj"
            };

            if (dlg.ShowDialog() != true) return;

            string fileName = dlg.FileName;
            MeshGeometry3D mesh = null;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // Load and simplify off the UI thread; MeshGeometry3D is frozen before return.
                mesh = await System.Threading.Tasks.Task.Run(() => MeshLoader.Load(fileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read the 3D model:\n{ex.Message}", "Open 3D Model",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (mesh == null || mesh.Positions.Count == 0 || mesh.TriangleIndices.Count == 0)
            {
                MessageBox.Show("The 3D model contains no triangle mesh (point clouds can't be rendered).",
                    "Open 3D Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Fresh open starts at the default front view and counts as a new specimen if captured.
            ShowModelPoseOverlay(mesh, System.IO.Path.GetFileNameWithoutExtension(fileName),
                                 Quaternion.Identity, isReposition: false);
        }

        private void Menu_Reposition3DModel(object sender, RoutedEventArgs e)
        {
            if (_activeMesh == null)
            {
                MessageBox.Show("Open and capture a 3D model first, then you can reposition it.",
                    "No 3D Model", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Resume from the last captured orientation so the model does not snap back to front.
            ShowModelPoseOverlay(_activeMesh, _activeModelName, _lastModelQuaternion, isReposition: true);
        }

        // =====================
        // Pose overlay
        // =====================

        private void ShowModelPoseOverlay(MeshGeometry3D mesh, string name,
            Quaternion initialRotation, bool isReposition)
        {
            _isRepositioning = isReposition;

            if (_poseModel != null)
                UI_ModelGroup.Children.Remove(_poseModel);

            var b = mesh.Bounds;
            var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            _modelDiag = Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
            if (_modelDiag <= 0) _modelDiag = 1;

            var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(205, 205, 210)));
            mat.Freeze();

            _modelRotation = new QuaternionRotation3D(initialRotation);
            _poseController = new ModelPoseController(_modelRotation);
            BuildPoseFineTuneControls();

            var tg = new Transform3DGroup();
            tg.Children.Add(new TranslateTransform3D(-center.X, -center.Y, -center.Z)); // Rotate around model center.
            tg.Children.Add(new RotateTransform3D(_modelRotation));

            // Light both sides because mesh winding can vary across file formats.
            _poseModel = new GeometryModel3D(mesh, mat) { BackMaterial = mat, Transform = tg };
            UI_ModelGroup.Children.Add(_poseModel);

            UI_ModelCamera.Position = new Point3D(0, 0, _modelDiag * 2);
            UI_ModelCamera.NearPlaneDistance = _modelDiag * 0.1;
            UI_ModelCamera.FarPlaneDistance = _modelDiag * 4;
            UI_ModelCamera.Width = _modelDiag * 1.2;

            _pendingModelName = name;
            UI_ModelPoseOverlay.Visibility = Visibility.Visible;

            PreviewKeyDown -= ModelPose_PreviewKeyDown; // Prevent double-subscription when reopening the overlay.
            PreviewKeyDown += ModelPose_PreviewKeyDown;
        }

        // =====================
        // Mouse input
        // =====================

        private void ModelPose_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            _rotatingModel = true;
            _lastPosePoint = e.GetPosition(UI_ModelPoseOverlay);
            UI_ModelPoseOverlay.CaptureMouse();
            e.Handled = true;
        }

        private void ModelPose_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_rotatingModel) return;

            var p = e.GetPosition(UI_ModelPoseOverlay);
            double dx = p.X - _lastPosePoint.X;
            double dy = p.Y - _lastPosePoint.Y;
            _lastPosePoint = p;

            if (dx == 0 && dy == 0) return;

            // Drag direction maps to a screen-space axis for quick free rotation.
            var axis = new Vector3D(dy, dx, 0);
            RotateModel(axis, axis.Length * 0.4);
            e.Handled = true;
        }

        private void ModelPose_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            _rotatingModel = false;
            UI_ModelPoseOverlay.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ModelPose_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Smaller camera width means a closer orthographic view.
            double f = e.Delta > 0 ? 1 / 1.1 : 1.1;
            double w = UI_ModelCamera.Width * f;
            UI_ModelCamera.Width = Math.Max(_modelDiag * 0.02, Math.Min(_modelDiag * 10, w));
            e.Handled = true;
        }

        // =====================
        // Capture workflow
        // =====================

        private void ModelPose_Capture(object sender, RoutedEventArgs e)
        {
            double w = UI_ModelViewportHost.ActualWidth, h = UI_ModelViewportHost.ActualHeight;
            if (w <= 0 || h <= 0 || _poseModel == null) return;

            const double ss = 2.0; // Supersample for a sharper 2D capture.
            var rtb = new RenderTargetBitmap((int)(w * ss), (int)(h * ss), 96 * ss, 96 * ss, PixelFormats.Pbgra32);
            rtb.Render(UI_ModelViewportHost);

            // Rebuild at 96 DPI so the captured bitmap behaves like a normal layout-sized image.
            int stride = rtb.PixelWidth * 4;
            var px = new byte[stride * rtb.PixelHeight];
            rtb.CopyPixels(px, stride, 0);
            var bmp = BitmapSource.Create(rtb.PixelWidth, rtb.PixelHeight, 96, 96,
                                          PixelFormats.Pbgra32, null, px, stride);
            bmp.Freeze();

            // Orthographic width maps model units directly to image pixels.
            double pxPerModelUnit = bmp.PixelWidth / UI_ModelCamera.Width;

            _activeMesh = _poseModel.Geometry as MeshGeometry3D;
            _activeModelName = _pendingModelName;
            _lastModelQuaternion = _modelRotation.Quaternion;
            bool wasReposition = _isRepositioning;

            ModelPose_Cancel(sender, e);

            // Repositioning re-captures the same specimen, so it should not advance specimen history.
            SetWorkspaceImage(bmp, _activeModelName, registerAsNewSpecimen: !wasReposition);

            _workingImageIsModelCapture = true;
            UI_MenuReposition3D.IsEnabled = true;

            // Optional scale calibration can be derived from pxPerModelUnit if needed by the app.
        }

        private void ModelPose_Cancel(object sender, RoutedEventArgs e)
        {
            PreviewKeyDown -= ModelPose_PreviewKeyDown;

            UI_ModelPoseOverlay.Visibility = Visibility.Collapsed;
            _rotatingModel = false;

            if (_poseModel != null)
            {
                UI_ModelGroup.Children.Remove(_poseModel);
                _poseModel = null;
            }
        }

        // =====================
        // Fine rotation UI
        // =====================

        private double ReadPoseStepDegrees()
        {
            double step = 15;

            if (_poseStepBox != null &&
                double.TryParse(_poseStepBox.Text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double s))
                step = s;

            if (step <= 0) step = 15;
            if (step > 180) step = 180;

            return step;
        }

        private void BuildPoseFineTuneControls()
        {
            if (_poseFineControls != null) return;

            var panel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical
            };

            System.Windows.Controls.Button MakeBtn(string text, string tip,
                System.Windows.RoutedEventHandler onClick)
            {
                var b = new System.Windows.Controls.Button
                {
                    Content = text,
                    ToolTip = tip,
                    MinWidth = 36,
                    Margin = new System.Windows.Thickness(2),
                    Padding = new System.Windows.Thickness(4, 2, 4, 2),
                    FontSize = 12
                };
                b.Click += onClick;
                return b;
            }

            System.Windows.Controls.TextBlock Label(string t)
            {
                return new System.Windows.Controls.TextBlock
                {
                    Text = t,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 11,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Margin = new System.Windows.Thickness(2, 6, 2, 1)
                };
            }

            System.Windows.Controls.TextBlock RowLabel(string t)
            {
                return new System.Windows.Controls.TextBlock
                {
                    Text = t,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 11,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
            }

            // Preset views.
            panel.Children.Add(Label("Preset views"));
            var presets = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            presets.Children.Add(MakeBtn("Front", "View the model's front", (s, e) => _poseController.ViewFront()));
            presets.Children.Add(MakeBtn("Back", "View the back", (s, e) => _poseController.ViewBack()));
            presets.Children.Add(MakeBtn("Left", "View the left side", (s, e) => _poseController.ViewLeft()));
            presets.Children.Add(MakeBtn("Right", "View the right side", (s, e) => _poseController.ViewRight()));
            presets.Children.Add(MakeBtn("Top", "View from above", (s, e) => _poseController.ViewTop()));
            presets.Children.Add(MakeBtn("Bottom", "View from below", (s, e) => _poseController.ViewBottom()));
            panel.Children.Add(presets);

            // Step size.
            panel.Children.Add(Label("Rotate by (degrees)"));
            _poseStepBox = new System.Windows.Controls.TextBox
            {
                Text = "15",
                Width = 56,
                Margin = new System.Windows.Thickness(2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center
            };
            panel.Children.Add(_poseStepBox);

            var chips = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            foreach (var deg in new[] { "1", "5", "15", "90" })
                chips.Children.Add(MakeBtn(deg + "\u00B0", "Set step to " + deg + "\u00B0",
                    (s, e) => _poseStepBox.Text = deg));
            panel.Children.Add(chips);

            // Exact-angle nudges in camera/world space.
            panel.Children.Add(Label("Fine rotate"));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };

            grid.Children.Add(RowLabel("Pitch"));
            grid.Children.Add(MakeBtn("\u25B2", "Tilt up (Up arrow)",
                (s, e) => _poseController.Nudge(ModelPoseController.Axis.Pitch, +ReadPoseStepDegrees())));
            grid.Children.Add(MakeBtn("\u25BC", "Tilt down (Down arrow)",
                (s, e) => _poseController.Nudge(ModelPoseController.Axis.Pitch, -ReadPoseStepDegrees())));

            grid.Children.Add(RowLabel("Yaw"));
            grid.Children.Add(MakeBtn("\u25C0", "Turn left (Left arrow)",
                (s, e) => _poseController.Nudge(ModelPoseController.Axis.Yaw, +ReadPoseStepDegrees())));
            grid.Children.Add(MakeBtn("\u25B6", "Turn right (Right arrow)",
                (s, e) => _poseController.Nudge(ModelPoseController.Axis.Yaw, -ReadPoseStepDegrees())));

            grid.Children.Add(RowLabel("Roll"));
            grid.Children.Add(MakeBtn("\u21BA", "Roll counter-clockwise ( , )",
                (s, e) => _poseController.Nudge(ModelPoseController.Axis.Roll, +ReadPoseStepDegrees())));
            grid.Children.Add(MakeBtn("\u21BB", "Roll clockwise ( . )",
                (s, e) => _poseController.Nudge(ModelPoseController.Axis.Roll, -ReadPoseStepDegrees())));
            panel.Children.Add(grid);

            _poseFineControls = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x20)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = panel
            };

            UI_ModelPoseOverlay.Children.Add(_poseFineControls);
        }

        // =====================
        // Rotation helper
        // =====================

        private void RotateModel(Vector3D screenAxis, double angleDegrees)
        {
            if (_modelRotation == null || screenAxis.LengthSquared == 0 || angleDegrees == 0) return;

            var q = _modelRotation.Quaternion * new Quaternion(screenAxis, angleDegrees);
            q.Normalize();
            _modelRotation.Quaternion = q;
        }

        private const double ArrowRotateStep = 2.0;

        private void ModelPose_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (UI_ModelPoseOverlay.Visibility != Visibility.Visible) return;

            switch (e.Key)
            {
                case Key.Down:
                    RotateModel(new Vector3D(1, 0, 0), ArrowRotateStep);
                    break;
                case Key.Up:
                    RotateModel(new Vector3D(1, 0, 0), -ArrowRotateStep);
                    break;
                case Key.Right:
                    RotateModel(new Vector3D(0, 1, 0), ArrowRotateStep);
                    break;
                case Key.Left:
                    RotateModel(new Vector3D(0, 1, 0), -ArrowRotateStep);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }
    }
}