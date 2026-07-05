using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DinoLino
{
    // The 3D model workflow: async loading, the pose overlay, drag/wheel/keyboard
    // rotation, fine-tune controls, capture to a 2D working image, and repositioning.
    // Split from MainWindow.xaml.cs; no logic changes.
    public partial class MainWindow
    {


        private GeometryModel3D _poseModel;
        private QuaternionRotation3D _modelRotation;
        private double _modelDiag = 1;
        private string _pendingModelName;
        private Point _lastPosePoint;
        private bool _rotatingModel;

        // 3D pose fine-tuning: exact-degree rotation + preset views over the live transform.
        private ModelPoseController _poseController;
        private System.Windows.Controls.TextBox _poseStepBox;
        private System.Windows.Controls.Border _poseFineControls;

        // Mesh + orientation retained after a capture so the same model can be re-posed later
        // via "Reposition 3D Object". Committed only on a successful capture, so cancelling a
        // freshly opened model doesn't overwrite the model behind the current captured view.
        private MeshGeometry3D _activeMesh;
        private string _activeModelName;
        private Quaternion _lastModelQuaternion = Quaternion.Identity;

        // True when the current working image is a captured view of a 3D model (so it can be
        // re-posed); false for ordinary 2D images, which have nothing to rotate.
        private bool _workingImageIsModelCapture;

        // True while the pose overlay is open as a reposition (vs. a fresh open). A reposition
        // re-captures the SAME specimen from a new angle, so it must not advance the specimen.
        private bool _isRepositioning;

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
                // MeshGeometry3D is Freezable: the loaders freeze it before returning,
                // so it can be built on a worker thread and then used from the UI
                // thread. Parsing + welding + decimation all happen off-thread; the UI
                // stays responsive with a wait cursor instead of freezing.
                mesh = await System.Threading.Tasks.Task.Run(() => MeshLoader.Load(fileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read the 3D model:\n{ex.Message}", "Open 3D Model",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;   // finally still restores the cursor
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

            // Fresh model: start at the front (identity) and treat a resulting capture as a new
            // specimen. The committed reposition state isn't touched until an actual capture.
            ShowModelPoseOverlay(mesh, System.IO.Path.GetFileNameWithoutExtension(fileName),
                                 Quaternion.Identity, isReposition: false);
        }

        private void Menu_Reposition3DModel(object sender, RoutedEventArgs e)
        {
            if (_activeMesh == null)
            {
                MessageBox.Show("Open and capture a 3D model (.ply) first, then you can reposition it.",
                    "No 3D Model", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Re-open the posing overlay on the same mesh, resuming from the orientation the last
            // capture was taken at so the object doesn't snap back to the front. isReposition:true
            // keeps a re-capture on the same specimen.
            ShowModelPoseOverlay(_activeMesh, _activeModelName, _lastModelQuaternion, isReposition: true);
        }

        private void ShowModelPoseOverlay(MeshGeometry3D mesh, string name,
            Quaternion initialRotation, bool isReposition)
        {
            _isRepositioning = isReposition;

            if (_poseModel != null) UI_ModelGroup.Children.Remove(_poseModel);

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
            tg.Children.Add(new TranslateTransform3D(-center.X, -center.Y, -center.Z)); // spin about its own center
            tg.Children.Add(new RotateTransform3D(_modelRotation));

            // BackMaterial: PLY winding conventions vary, so light both sides of every face.
            _poseModel = new GeometryModel3D(mesh, mat) { BackMaterial = mat, Transform = tg };
            UI_ModelGroup.Children.Add(_poseModel);

            UI_ModelCamera.Position = new Point3D(0, 0, _modelDiag * 2);
            UI_ModelCamera.NearPlaneDistance = _modelDiag * 0.1;
            UI_ModelCamera.FarPlaneDistance = _modelDiag * 4;
            UI_ModelCamera.Width = _modelDiag * 1.2;

            _pendingModelName = name;
            UI_ModelPoseOverlay.Visibility = Visibility.Visible;

            PreviewKeyDown -= ModelPose_PreviewKeyDown;   // guard against double-subscribe
            PreviewKeyDown += ModelPose_PreviewKeyDown;
        }

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
            double f = e.Delta > 0 ? 1 / 1.1 : 1.1;   // smaller camera width = zoomed in
            double w = UI_ModelCamera.Width * f;
            UI_ModelCamera.Width = Math.Max(_modelDiag * 0.02, Math.Min(_modelDiag * 10, w));
            e.Handled = true;
        }

        private void ModelPose_Capture(object sender, RoutedEventArgs e)
        {
            double w = UI_ModelViewportHost.ActualWidth, h = UI_ModelViewportHost.ActualHeight;
            if (w <= 0 || h <= 0 || _poseModel == null) return;

            const double ss = 2.0;   // supersample, same idea as the screenshot exporter
            var rtb = new RenderTargetBitmap((int)(w * ss), (int)(h * ss), 96 * ss, 96 * ss, PixelFormats.Pbgra32);
            rtb.Render(UI_ModelViewportHost);

            // Re-tag at 96 DPI so downstream code sees an ordinary bitmap whose layout size
            // equals its pixel size — no DPI surprises in the 2D pipeline.
            int stride = rtb.PixelWidth * 4;
            var px = new byte[stride * rtb.PixelHeight];
            rtb.CopyPixels(px, stride, 0);
            var bmp = BitmapSource.Create(rtb.PixelWidth, rtb.PixelHeight, 96, 96,
                                          PixelFormats.Pbgra32, null, px, stride);
            bmp.Freeze();

            // Exact projection scale: the ortho camera maps Width model units across the
            // full bitmap width, uniformly, regardless of depth.
            double pxPerModelUnit = bmp.PixelWidth / UI_ModelCamera.Width;

            // Retain the mesh, orientation, and name so the view can be re-posed later. Grab the
            // mesh from the live model BEFORE ModelPose_Cancel tears it down. Committing here (not
            // when the overlay opens) means cancelling a freshly opened model leaves the model
            // behind the current captured view intact.
            _activeMesh = _poseModel.Geometry as MeshGeometry3D;
            _activeModelName = _pendingModelName;
            _lastModelQuaternion = _modelRotation.Quaternion;
            bool wasReposition = _isRepositioning;

            ModelPose_Cancel(sender, e);                 // hide overlay, remove the live model

            // A reposition re-captures the same specimen from a new angle, so don't register it
            // as a new specimen (which would advance the counter and start a new history block).
            SetWorkspaceImage(bmp, _activeModelName, registerAsNewSpecimen: !wasReposition);

            // This working image is a captured 3D view, so it can be re-posed.
            _workingImageIsModelCapture = true;
            UI_MenuReposition3D.IsEnabled = true;

            // Optional auto-calibration: if the PLY is in real units (scanners typically export mm),
            // 1 model unit == pxPerModelUnit pixels in this image. Hook into ScaleCalibration here,
            // e.g. Scale.SetPixelsPerUnit(pxPerModelUnit) — adapt to your API.
        }

        private void ModelPose_Cancel(object sender, RoutedEventArgs e)
        {
            PreviewKeyDown -= ModelPose_PreviewKeyDown;

            UI_ModelPoseOverlay.Visibility = Visibility.Collapsed;
            _rotatingModel = false;
            if (_poseModel != null)
            {
                UI_ModelGroup.Children.Remove(_poseModel);
                _poseModel = null;   // releases the (potentially large) mesh
            }
        }

        // Reads the step size (degrees) from the box; falls back to 15 and clamps to
        // (0, 180] so a stray value can't spin wildly or do nothing.
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
            if (_poseFineControls != null) return; // built once; survives repositioning

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

            // Preset views
            panel.Children.Add(Label("Preset views"));
            var presets = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            presets.Children.Add(MakeBtn("Front", "View the model's front", (s, e) => _poseController.ViewFront()));
            presets.Children.Add(MakeBtn("Back", "View the back", (s, e) => _poseController.ViewBack()));
            presets.Children.Add(MakeBtn("Left", "View the left side", (s, e) => _poseController.ViewLeft()));
            presets.Children.Add(MakeBtn("Right", "View the right side", (s, e) => _poseController.ViewRight()));
            presets.Children.Add(MakeBtn("Top", "View from above", (s, e) => _poseController.ViewTop()));
            presets.Children.Add(MakeBtn("Bottom", "View from below", (s, e) => _poseController.ViewBottom()));
            panel.Children.Add(presets);

            // Step size
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

            // Exact-degree axis nudges (world/camera frame)
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
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0xB0, 0x20, 0x20, 0x20)),
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(6),
                Padding = new System.Windows.Thickness(6),
                Margin = new System.Windows.Thickness(8),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Child = panel
            };

            UI_ModelPoseOverlay.Children.Add(_poseFineControls);
        }

        // Rotates the model about a screen-space axis (camera is axis-aligned, so world
        // X = screen right, world Y = screen up). Used by both middle-drag and arrow keys.
        private void RotateModel(Vector3D screenAxis, double angleDegrees)
        {
            if (_modelRotation == null || screenAxis.LengthSquared == 0 || angleDegrees == 0) return;
            var q = _modelRotation.Quaternion * new Quaternion(screenAxis, angleDegrees);
            q.Normalize();
            _modelRotation.Quaternion = q;
        }

        private const double ArrowRotateStep = 2.0;   // degrees per key event; holding a key auto-repeats

        private void ModelPose_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (UI_ModelPoseOverlay.Visibility != Visibility.Visible) return;

            switch (e.Key)
            {
                case Key.Down: RotateModel(new Vector3D(1, 0, 0), ArrowRotateStep); break; // top tips forward & down
                case Key.Up: RotateModel(new Vector3D(1, 0, 0), -ArrowRotateStep); break; // bottom tips forward & up
                case Key.Right: RotateModel(new Vector3D(0, 1, 0), ArrowRotateStep); break; // left margin swings forward & right
                case Key.Left: RotateModel(new Vector3D(0, 1, 0), -ArrowRotateStep); break; // right margin swings forward & left
                default: return;
            }
            e.Handled = true;   // stop arrows from moving focus to the Capture/Cancel buttons
        }
    }
}