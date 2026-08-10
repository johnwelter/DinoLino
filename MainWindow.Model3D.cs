using DinoLino.Utilities;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace DinoLino
{
    /// <summary>
    /// Handles loading, posing, capturing, and re-posing 3D models.
    /// </summary>
    /// <remarks>
    /// Each specimen owns its orientation (Specimen.ModelOrientation). Nothing here
    /// keeps a single "last orientation" of its own: one existed, and it meant that
    /// repositioning any specimen opened at whichever model was captured most
    /// recently rather than at that specimen's own view.
    /// </remarks>
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

        // Orientation the overlay opened at. Capture measures the session's turn
        // against it, which is what the other objects are then turned by.
        private Quaternion _poseStartQuaternion = Quaternion.Identity;

        // How much wider than the model's own diagonal the camera sits by default.
        // Also the yardstick the wheel zoom is measured against when one turn is
        // carried across several models.
        private const double DefaultFramingFactor = 1.2;

        // Fine-tune overlay controls for exact-angle rotation and preset views.
        private ModelPoseController _poseController;
        private System.Windows.Controls.TextBox _poseStepBox;
        private System.Windows.Controls.Border _poseFineControls;

        // Captured model state kept so the same specimen can be re-posed later.
        private MeshGeometry3D _activeMesh;
        private string _activeModelName;

        // File the mesh in memory came from. Checked against the loaded specimen's
        // own path, so repositioning cannot re-pose whichever model happens to still
        // be in memory after the user has moved to a different specimen.
        private string _activeModelPath;

        // True when the workspace image is a captured 3D view rather than a native 2D image.
        private bool _workingImageIsModelCapture;

        // True while the pose overlay is being used to re-capture an existing specimen.
        private bool _isRepositioning;

        // True while every other 3D object is being re-captured, so the overlay's own
        // controls cannot start a second run on top of the first.
        private bool _batchPosing;

        // Set while positioning a 3D model that Import Folder registered earlier. The
        // captured view attaches to that specimen instead of creating a new one.
        private Specimen _pendingImportSpecimen;

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
                         "OBJ mesh (*.obj)|*.obj",

                // Start in the Directory panel's working folder when one is set.
                InitialDirectory = DialogInitialDirectory
            };

            if (dlg.ShowDialog() != true) return;

            await Open3DModelFromPath(dlg.FileName);
        }

        /// Loads a mesh file and opens the pose overlay for it. Shared by the File menu
        /// and the Directory panel.
        internal async Task Open3DModelFromPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;

            MeshGeometry3D mesh = await LoadMeshOrWarn(fileName);
            if (mesh == null) return;

            _activeModelPath = fileName;

            // Fresh open starts at the default front view and counts as a new specimen if captured.
            ShowModelPoseOverlay(mesh, System.IO.Path.GetFileNameWithoutExtension(fileName),
                                 Quaternion.Identity, isReposition: false);
        }

        /// Loads an imported 3D model and opens the pose overlay for it. The specimen
        /// already exists, so capturing attaches the view to it rather than adding one.
        internal async Task OpenImportedModel(Specimen specimen)
        {
            string path = specimen?.PendingModelPath;

            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show(this,
                    "This model's file could not be found:\n" + path,
                    "Open 3D Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MeshGeometry3D mesh = await LoadMeshOrWarn(path);
            if (mesh == null) return;

            _activeModelPath = path;
            _pendingImportSpecimen = specimen;

            // An imported model has never been posed, so it starts where its file puts
            // it — unless a batch turn has already moved it, which its own orientation
            // records.
            ShowModelPoseOverlay(mesh, System.IO.Path.GetFileNameWithoutExtension(path),
                                 specimen.ModelOrientation, isReposition: false);
        }

        /// Reads one mesh off the UI thread, reporting anything that goes wrong and
        /// returning null rather than a mesh nothing can be drawn from.
        private async Task<MeshGeometry3D> LoadMeshOrWarn(string path)
        {
            MeshGeometry3D mesh;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // Load and simplify off the UI thread; MeshGeometry3D is frozen before return.
                mesh = await Task.Run(() => MeshLoader.Load(path));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not read the 3D model:\n{ex.Message}", "Open 3D Model",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (mesh == null || mesh.Positions.Count == 0 || mesh.TriangleIndices.Count == 0)
            {
                MessageBox.Show(this, "The 3D model contains no triangle mesh (point clouds can't be rendered).",
                    "Open 3D Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            return mesh;
        }

        private async void Menu_Reposition3DModel(object sender, RoutedEventArgs e)
            => await Reposition3DModel(SpecimenManager.CurrentSpecimen);

        /// Opens the pose overlay on one specimen's model, resuming from the view it
        /// was last captured at. A capture lands on whichever specimen is loaded, so
        /// the specimen given here must be that one — the Sample list loads it first.
        internal async Task Reposition3DModel(Specimen specimen)
        {
            // A second overlay would replace the model the first one is posing.
            if (UI_ModelPoseOverlay.Visibility == Visibility.Visible) return;

            string path = specimen?.ModelPath;

            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show(this,
                    _activeMesh == null
                        ? "Open and capture a 3D model first, then you can reposition it."
                        : "The specimen on screen is not a 3D capture, so there is nothing to reposition.",
                    "No 3D Model", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // The ▲/▼ arrows move between specimens without touching the mesh held in
            // memory, so it may belong to a different object than the one on screen.
            // Re-read the file whenever the two disagree.
            bool meshMatchesSpecimen =
                _activeMesh != null
                && !string.IsNullOrEmpty(_activeModelPath)
                && string.Equals(_activeModelPath, path, StringComparison.OrdinalIgnoreCase);

            if (!meshMatchesSpecimen)
            {
                if (!System.IO.File.Exists(path))
                {
                    MessageBox.Show(this,
                        "This specimen's model file could not be found:\n" + path,
                        "Reposition 3D Object", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MeshGeometry3D reloaded = await LoadMeshOrWarn(path);
                if (reloaded == null) return;

                _activeMesh = reloaded;
                _activeModelPath = path;
                _activeModelName = System.IO.Path.GetFileNameWithoutExtension(path);
            }

            // Resume from THIS specimen's captured orientation, so repositioning picks
            // up exactly where its own last capture left off.
            ShowModelPoseOverlay(_activeMesh, _activeModelName, specimen.ModelOrientation, isReposition: true);
        }

        // =====================
        // Pose overlay
        // =====================

        private void ShowModelPoseOverlay(MeshGeometry3D mesh, string name,
            Quaternion initialRotation, bool isReposition)
        {
            _isRepositioning = isReposition;
            _poseStartQuaternion = initialRotation;

            _modelRotation = new QuaternionRotation3D(initialRotation);
            _poseController = new ModelPoseController(_modelRotation);
            BuildPoseFineTuneControls();

            MountModel(mesh, _modelRotation, zoomFactor: 1.0);

            _pendingModelName = name;
            UI_ModelPoseOverlay.Visibility = Visibility.Visible;
            RefreshApplyToAllBox(isReposition);

            PreviewKeyDown -= ModelPose_PreviewKeyDown; // Prevent double-subscription when reopening the overlay.
            PreviewKeyDown += ModelPose_PreviewKeyDown;
        }

        /// Puts one mesh in the viewport, centred on itself and framed by its own size.
        /// zoomFactor carries the user's wheel zoom across models, so a view zoomed in
        /// on one object is zoomed in on the next by the same amount.
        private void MountModel(MeshGeometry3D mesh, QuaternionRotation3D rotation, double zoomFactor)
        {
            if (_poseModel != null)
                UI_ModelGroup.Children.Remove(_poseModel);

            var b = mesh.Bounds;
            var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            _modelDiag = Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
            if (_modelDiag <= 0) _modelDiag = 1;

            var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(205, 205, 210)));
            mat.Freeze();

            var tg = new Transform3DGroup();
            tg.Children.Add(new TranslateTransform3D(-center.X, -center.Y, -center.Z)); // Rotate around model center.
            tg.Children.Add(new RotateTransform3D(rotation));

            // Light both sides because mesh winding can vary across file formats.
            _poseModel = new GeometryModel3D(mesh, mat) { BackMaterial = mat, Transform = tg };
            UI_ModelGroup.Children.Add(_poseModel);

            UI_ModelCamera.Position = new Point3D(0, 0, _modelDiag * 2);
            UI_ModelCamera.NearPlaneDistance = _modelDiag * 0.1;
            UI_ModelCamera.FarPlaneDistance = _modelDiag * 4;
            UI_ModelCamera.Width = _modelDiag * DefaultFramingFactor * zoomFactor;
        }

        // =====================
        // Mouse input
        // =====================

        private void ModelPose_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_batchPosing) return;
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
            if (_batchPosing) return;

            // Smaller camera width means a closer orthographic view.
            double f = e.Delta > 0 ? 1 / 1.1 : 1.1;
            double w = UI_ModelCamera.Width * f;
            UI_ModelCamera.Width = Math.Max(_modelDiag * 0.02, Math.Min(_modelDiag * 10, w));
            e.Handled = true;
        }

        // =====================
        // Capture workflow
        // =====================

        private async void ModelPose_Capture(object sender, RoutedEventArgs e)
        {
            if (_batchPosing || _poseModel == null) return;

            BitmapSource bmp = RenderPosedModel();
            if (bmp == null) return;

            // Orthographic width maps model units directly to image pixels.
            double pxPerModelUnit = bmp.PixelWidth / UI_ModelCamera.Width;

            Quaternion finalOrientation = _modelRotation.Quaternion;
            finalOrientation.Normalize();

            // What this session actually did to the model, in world terms. The other
            // objects are turned by this rather than snapped to finalOrientation, so
            // each keeps whatever alignment it already had and the change lands on top:
            // "turn everything 90° clockwise from where it sits" rather than "make
            // everything face exactly this way".
            Quaternion turn = WorldTurn(_poseStartQuaternion, finalOrientation);

            _activeMesh = _poseModel.Geometry as MeshGeometry3D;
            _activeModelName = _pendingModelName;
            bool wasReposition = _isRepositioning;

            // How far the user zoomed away from the default framing, so the other
            // objects are captured at the same relative distance rather than at
            // whatever width this particular model happened to need.
            double zoomFactor = _modelDiag > 0
                ? UI_ModelCamera.Width / (_modelDiag * DefaultFramingFactor)
                : 1.0;

            // Captured for a specimen Import Folder registered earlier: attach the view
            // to that record rather than creating another one.
            var importTarget = _pendingImportSpecimen;
            _pendingImportSpecimen = null;

            // The rest of the session is re-captured BEFORE the overlay comes down,
            // because the viewport those captures render through lives inside it.
            if (UI_ApplyPoseToAll.IsEnabled && UI_ApplyPoseToAll.IsChecked == true)
            {
                Specimen posed = importTarget
                    ?? (wasReposition ? SpecimenManager.CurrentSpecimen : null);

                await ApplyTurnToOtherModels(turn, zoomFactor, posed);
            }

            ModelPose_Cancel(sender, e);

            if (importTarget != null)
            {
                AttachCaptureToImportedSpecimen(importTarget, bmp);
            }
            else
            {
                // Repositioning re-captures the same specimen, so it should not advance specimen history.
                SetWorkspaceImage(bmp, _activeModelName, registerAsNewSpecimen: !wasReposition);
            }

            // Remember the file and the orientation on the specimen itself. The path is
            // what lets a later reposition find the mesh again; the orientation is what
            // lets it open on this same view.
            var current = SpecimenManager.CurrentSpecimen;
            if (current != null && !string.IsNullOrEmpty(_activeModelPath))
            {
                current.ModelPath = _activeModelPath;
                current.ModelOrientation = finalOrientation;
            }

            _workingImageIsModelCapture = true;
            UI_MenuReposition3D.IsEnabled = true;

            RebuildSampleList();
        }

        /// The world-space rotation that takes one orientation to another.
        /// Left-multiplied, matching how ModelPoseController applies its nudges.
        private static Quaternion WorldTurn(Quaternion from, Quaternion to)
        {
            // Quaternion is a struct, so inverting this copy leaves the caller's alone.
            Quaternion inverse = from;
            inverse.Invert();

            Quaternion turn = to * inverse;
            turn.Normalize();
            return turn;
        }

        /// Renders whatever is currently in the pose viewport to a frozen bitmap.
        private BitmapSource RenderPosedModel()
        {
            double w = UI_ModelViewportHost.ActualWidth, h = UI_ModelViewportHost.ActualHeight;
            if (w <= 0 || h <= 0) return null;

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
            return bmp;
        }

        /// Gives an imported 3D specimen the view just captured and makes it the loaded
        /// specimen, carrying its (so far empty) operation history across.
        private void AttachCaptureToImportedSpecimen(Specimen target, BitmapSource bmp)
        {
            var departing = SpecimenManager.CurrentSpecimen;
            bool switching = !ReferenceEquals(departing, target);

            SpecimenManager.AttachCapturedImage(target, bmp);
            SpecimenManager.MakeCurrent(target);

            SetWorkspaceImage(bmp, target.FileName, registerAsNewSpecimen: false);

            if (switching)
                UndoRedoManager.SwitchActiveSpecimen(departing, SpecimenManager.NameOf(departing), target);

            RebuildSampleList();
        }

        private void ModelPose_Cancel(object sender, RoutedEventArgs e)
        {
            if (_batchPosing) return;

            // Abandoning the overlay leaves an imported model unpositioned; it must not
            // capture into that specimen later.
            _pendingImportSpecimen = null;

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
        // Apply to all 3D objects
        // =====================

        /// Every 3D object of the session except the one being posed. A specimen counts
        /// as 3D once it has a ModelPath, which it keeps for the whole session — an
        /// imported model that has never been positioned is included, so a folder of
        /// scans can be turned from one pose.
        private List<Specimen> OtherModelSpecimens(Specimen posed)
        {
            var others = new List<Specimen>();

            foreach (var specimen in SpecimenManager.Specimens)
            {
                if (specimen.Deleted) continue;
                if (string.IsNullOrEmpty(specimen.ModelPath)) continue;
                if (ReferenceEquals(specimen, posed)) continue;

                others.Add(specimen);
            }

            return others;
        }

        /// Sets the checkbox up for a freshly opened overlay: always unticked, and off
        /// entirely when this is the session's only 3D object.
        private void RefreshApplyToAllBox(bool isReposition)
        {
            Specimen posed = _pendingImportSpecimen
                ?? (isReposition ? SpecimenManager.CurrentSpecimen : null);

            int others = OtherModelSpecimens(posed).Count;

            // Unticked every time: re-capturing every object in the session is not a
            // choice to inherit from the last time the overlay happened to be open.
            UI_ApplyPoseToAll.IsChecked = false;
            UI_ApplyPoseToAll.IsEnabled = others > 0;

            // The theme greys a disabled checkbox from its template, which the explicit
            // white foreground needed on the dark bar would win against, so the dimming
            // is done here instead.
            UI_ApplyPoseToAll.Opacity = others > 0 ? 1.0 : 0.45;

            UI_ApplyPoseToAll.ToolTip =
                others == 0 ? "Only one 3D object has been opened this session"
                : others == 1 ? "Turn the other 3D object in this session by the same amount"
                : $"Turn the other {others} 3D objects in this session by the same amount";
        }

        /// Turns every other 3D object by the rotation just made here: each mesh is read
        /// from disk, posed at its own orientation plus that turn, and its view handed
        /// to its specimen. Which specimen is loaded does not change.
        private async Task ApplyTurnToOtherModels(Quaternion turn, double zoomFactor, Specimen posed)
        {
            List<Specimen> targets = OtherModelSpecimens(posed);
            if (targets.Count == 0) return;

            if (!ConfirmBatchRepose(targets)) return;

            _batchPosing = true;
            string hint = UI_ModelPoseHint.Text;
            if (_poseFineControls != null) _poseFineControls.IsEnabled = false;

            var failed = new List<string>();
            int done = 0;

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Specimen specimen = targets[i];
                    string label = specimen.FileName ?? SpecimenManager.NameOf(specimen);

                    UI_ModelPoseHint.Text = $"Repositioning {i + 1} of {targets.Count}:  {label}";

                    if (!System.IO.File.Exists(specimen.ModelPath))
                    {
                        failed.Add(label + "  (file not found)");
                        continue;
                    }

                    MeshGeometry3D mesh;
                    try
                    {
                        // Awaiting also lets the label above repaint before the read
                        // blocks, which for a large mesh is most of the wait.
                        mesh = await Task.Run(() => MeshLoader.Load(specimen.ModelPath));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(label + "  (" + ex.Message + ")");
                        continue;
                    }

                    if (mesh == null || mesh.Positions.Count == 0 || mesh.TriangleIndices.Count == 0)
                    {
                        failed.Add(label + "  (no triangle mesh)");
                        continue;
                    }

                    // From where this object already sits, not from where the posed one
                    // ended up: its own alignment survives and the turn lands on top.
                    Quaternion orientation = turn * specimen.ModelOrientation;
                    orientation.Normalize();

                    MountModel(mesh, new QuaternionRotation3D(orientation), zoomFactor);
                    UI_ModelViewportHost.UpdateLayout();

                    // RenderTargetBitmap reads the visual tree, and 3D content swapped
                    // in during this same tick may not have been drawn yet; waiting for
                    // a render pass keeps the capture from coming back empty.
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                    BitmapSource bmp = RenderPosedModel();
                    if (bmp == null)
                    {
                        failed.Add(label + "  (nothing to capture)");
                        continue;
                    }

                    SpecimenManager.AttachCapturedImage(specimen, bmp);
                    specimen.ModelOrientation = orientation;
                    done++;
                }
            }
            finally
            {
                UI_ModelPoseHint.Text = hint;
                if (_poseFineControls != null) _poseFineControls.IsEnabled = true;
                _batchPosing = false;
            }

            RebuildSampleList();
            ReportBatchRepose(done, failed);
        }

        /// A batch turn re-reads every mesh and replaces every view, so it asks first —
        /// and says plainly both what it turns from and what it does to work already
        /// recorded.
        private bool ConfirmBatchRepose(List<Specimen> targets)
        {
            int withMeasurements = 0;
            foreach (Specimen specimen in targets)
                if (specimen.Record != null && specimen.Record.Operations.Count > 0)
                    withMeasurements++;

            string message = targets.Count == 1
                ? "Turn the other 3D object by the same amount?"
                : $"Turn the other {targets.Count} 3D objects by the same amount?";

            message += "\n\nEach one is rotated from its own last captured view by the change made " +
                       "here, then captured again. Every mesh is read from disk, which can take a " +
                       "while for large files.";

            if (withMeasurements == 1)
                message += "\n\nOne of them already has measurements. Those were taken on its previous " +
                           "view and will no longer line up with the new one.";
            else if (withMeasurements > 1)
                message += $"\n\n{withMeasurements} of them already have measurements. Those were taken on " +
                           "their previous views and will no longer line up with the new ones.";

            return MessageBox.Show(this, message, "Apply to all 3D objects",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        private void ReportBatchRepose(int done, List<string> failed)
        {
            if (done == 0 && failed.Count == 0) return;

            string message = done == 1
                ? "1 other 3D object was repositioned."
                : $"{done} other 3D objects were repositioned.";

            if (failed.Count > 0)
            {
                message += failed.Count == 1
                    ? "\n\n1 could not be read and was left as it was:\n"
                    : $"\n\n{failed.Count} could not be read and were left as they were:\n";
                message += string.Join("\n", failed);
            }

            MessageBox.Show(this, message, "Apply to all 3D objects", MessageBoxButton.OK,
                failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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
            if (_batchPosing) return;
            if (_modelRotation == null || screenAxis.LengthSquared == 0 || angleDegrees == 0) return;

            var q = _modelRotation.Quaternion * new Quaternion(screenAxis, angleDegrees);
            q.Normalize();
            _modelRotation.Quaternion = q;
        }

        private const double ArrowRotateStep = 2.0;

        private void ModelPose_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (UI_ModelPoseOverlay.Visibility != Visibility.Visible) return;
            if (_batchPosing) return;

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