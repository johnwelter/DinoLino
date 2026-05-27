using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageSnapshot = DinoLino.Utilities.OutlineProcessor.ImageSnapshot;

namespace DinoLino.Utilities.Modes
{
    public class OutlineMode : WorkMode
    {
        public override string TabName => "Outline";
        public override UserControl CreateControlPanel() => new OutlineControlPanel(this);
        public override bool IsStartingNewOperation => true;

        #region setting outline mode

        private bool _drawOutlineMode = true;
        public bool DrawOutlineMode
        {
            get => _drawOutlineMode;
            set
            {
                _drawOutlineMode = value;
                OnPropertyChanged(nameof(DrawOutlineMode));
            }
        }

        private bool _eraseOutlineMode = false;
        public bool EraseOutlineMode
        {
            get => _eraseOutlineMode;
            set
            {
                _eraseOutlineMode = value;
                OnPropertyChanged(nameof(EraseOutlineMode));
            }
        }

        private bool _smoothOutlineMode = false;
        public bool SmoothOutlineMode
        {
            get => _smoothOutlineMode;
            set
            {
                _smoothOutlineMode = value;
                OnPropertyChanged(nameof(SmoothOutlineMode));
            }
        }

        private bool _outlineMetadataMode = false;
        public bool OutlineMetadataMode
        {
            get => _outlineMetadataMode;
            set
            {
                if (_outlineMetadataMode == value)
                    return;

                _outlineMetadataMode = value;
                OnPropertyChanged(nameof(OutlineMetadataMode));

                // Auto-generate metadata when entering metadata mode
                if (_outlineMetadataMode)
                    GenerateMetadata();
            }
        }
        #endregion

        #region get image data
        private BitmapImage _sourceImage;
        public BitmapImage SourceImage
        {
            get => _sourceImage;
            set
            {
                _sourceImage = value;
                CacheSourcePixels();
            }
        }

        private Polyline _activePolyline = null;
        private byte[] _cachedPixels;
        private int _cachedStride;
        private int _cachedBpp;
        private int _cachedWidth;
        private int _cachedHeight;
        private int _minAreaPixels = 20;
        public int MinAreaPixels
        {
            get => _minAreaPixels;
            set { _minAreaPixels = value; OnPropertyChanged(nameof(MinAreaPixels)); }
        }

        private void CacheSourcePixels()
        {
            if (_sourceImage == null) { _cachedPixels = null; return; }
            var formatted = new FormatConvertedBitmap(_sourceImage, PixelFormats.Bgra32, null, 0);
            _cachedWidth = formatted.PixelWidth;
            _cachedHeight = formatted.PixelHeight;
            _cachedBpp = 4;
            _cachedStride = _cachedWidth * 4;
            _cachedPixels = new byte[_cachedStride * _cachedHeight];
            formatted.CopyPixels(_cachedPixels, _cachedStride, 0);

            var bgColors = EstimateBackgroundColors();
            _bgColors = bgColors;
            _backgroundMask = _processor.BuildBackgroundMask(
                _cachedPixels, _cachedWidth, _cachedHeight, _cachedStride, _cachedBpp, bgColors);
        }


        private (double r, double g, double b)[] _bgColors;
        // Samples one pixel at each corner for use as individual background seeds.
        private (double r, double g, double b)[] EstimateBackgroundColors()
        {
            return new[]
            {
        ToDouble(ReadPixel(0, 0)),
        ToDouble(ReadPixel(_cachedWidth - 1, 0)),
        ToDouble(ReadPixel(0, _cachedHeight - 1)),
        ToDouble(ReadPixel(_cachedWidth - 1, _cachedHeight - 1))
    };
        }

        private (double r, double g, double b) ToDouble((byte r, byte g, byte b) px)
            => (px.r, px.g, px.b);

        private bool[] _backgroundMask;
        

        // Converts a canvas-space point back to image-space (pixel coordinates).
        // Inverse of the transform applied when building the polyline from simplified boundary points.
        private Point CanvasToImage(Point p)
        {
            return new Point(
                (p.X - OffsetX) / ScaleX,
                (p.Y - OffsetY) / ScaleY);
        }
        #endregion

        #region shared functions and variables

        private bool _useWatershed = false;
        public bool UseWatershed
        {
            get => _useWatershed;
            set { _useWatershed = value; OnPropertyChanged(nameof(UseWatershed)); }
        }
        public override void ClearMetadata()
        {
            AspectRatioResult = 0;
            PerimeterAreaRatioResult = 0;
            CircularityResult = 0;
            SolidityResult = 0;
            SumTurningAnglesResult = 0;
            MeanTurningAngleResult = 0;
            VarianceTurningAnglesResult = 0;
            EFDCoefficientsResult = null;
            MetadataSummary = "";
        }

        protected override void OnOperationUndone(WorkOperation operation)
        {
            if (operation is OutlineOperation)
                ClearMetadata();
        }

        protected override void OnOperationRedone(WorkOperation operation)
        {
            if (operation is OutlineOperation op)
                op.ApplyMetadataToMode();
        }





        #endregion
        #region draw outline

        // =====================
        // USER PARAMETERS
        // =====================

        // 0 = Off, 1 = Low, 2 = High
        private int _watershedBlurLevel = 0;
        public int WatershedBlurLevel
        {
            get => _watershedBlurLevel;
            set { _watershedBlurLevel = value; OnPropertyChanged(nameof(WatershedBlurLevel)); }
        }

        private double _fillSensitivity = 30;
        public double FillSensitivity
        {
            get => _fillSensitivity;
            set { _fillSensitivity = value; OnPropertyChanged(nameof(FillSensitivity)); }
        }

        private double _edgeThreshold = 40;
        public double EdgeThreshold
        {
            get => _edgeThreshold;
            set { _edgeThreshold = value; OnPropertyChanged(nameof(EdgeThreshold)); }
        }

        private double _simplifyEpsilon = 1.5;
        public double SimplifyEpsilon
        {
            get => _simplifyEpsilon;
            set { _simplifyEpsilon = value; OnPropertyChanged(nameof(SimplifyEpsilon)); }
        }

        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public double OffsetX { get; set; } = 0;
        public double OffsetY { get; set; } = 0;

        public override void Reset()
        {
            base.Reset();
            _activePolyline = null;
            _efd.Clear();
            ClearMetadata();
            ClearEFDPreview();
        }

        private readonly OutlineProcessor _processor = new OutlineProcessor();

        // =====================
        // PROCESS CLICK
        // =====================
        public Action<List<UIElement>> OnOutlineReady;

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            BeginOperation();

            if (_eraseOutlineMode || _smoothOutlineMode || _outlineMetadataMode)
                return new List<UIElement>();

            if (_cachedPixels == null) return new List<UIElement>();

            int px = (int)((mousePos.X - OffsetX) / ScaleX);
            int py = (int)((mousePos.Y - OffsetY) / ScaleY);

            if ((uint)px >= _cachedWidth || (uint)py >= _cachedHeight)
                return new List<UIElement>();

            var token = CancellationToken;

            // Snapshot everything the background thread needs
            // so it doesn't touch UI-thread objects
            var snap = new ImageSnapshot(_cachedPixels, _backgroundMask, _cachedWidth, _cachedHeight, _cachedStride, _cachedBpp);
            double scaleX = ScaleX, scaleY = ScaleY;
            double offsetX = OffsetX, offsetY = OffsetY;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    (byte sr, byte sg, byte sb) = _processor.ReadPixel(px, py, snap);
                    bool[] raw;
                    if (_useWatershed)
                    {
                        raw = _processor.WatershedSegment(px, py, snap, seedRadius: 3, _watershedBlurLevel)
                              ?? _processor.FloodFill(px, py, sr, sg, sb, snap, _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold);
                    }
                    else
                    {
                        raw = _processor.FloodFill(px, py, sr, sg, sb, snap, _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold);
                    }

                    if (!_processor.HasMinimumPixels(raw, 1)) return;
                    token.ThrowIfCancellationRequested();

                    // Crop before FillHoles — avoids running hole-filling on the full image.
                    var (bx0, by0, bx1, by1) = _processor.GetMaskBounds(raw, snap.Width, snap.Height, margin: 4);
                    var (croppedRaw, cw, ch) = _processor.CropMask(raw, snap.Width, bx0, by0, bx1, by1);
                    var croppedSnap = new ImageSnapshot(snap.Pixels, snap.BgMask, cw, ch, snap.Stride, snap.Bpp);

                    // FillHoles and ExtractLargestComponent now operate only on the small cropped region.
                    bool[] work = _processor.FillHoles(croppedRaw, croppedSnap);
                    work = _processor.ExtractLargestComponent(work, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    _processor.EnforceMinimumCorridorWidth(work, croppedSnap, minWidth: 3);
                    token.ThrowIfCancellationRequested();

                    _processor.SmoothBorderTopology(work, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    _processor.PruneDeadEnds(work, croppedSnap);
                    work = _processor.KeepLargestComponent(work, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    int cpx = px - bx0, cpy = py - by0;
                    bool[] tracedMask = _processor.PrepareMaskForTracing(work, croppedRaw, cpx, cpy, MinAreaPixels, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    List<Point> boundary = _processor.TraceBoundary(tracedMask, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    if (!HasMinimumBorderLength(boundary, 8))
                    {
                        bool[] expanded = _processor.ExpandConnectedComponentToArea(
                            _processor.KeepComponentContainingSeed(croppedRaw, cpy * cw + cpx, croppedSnap),
                            cpy * cw + cpx, MinAreaPixels, croppedSnap);
                        token.ThrowIfCancellationRequested();
                        boundary = _processor.TraceBoundary(expanded, croppedSnap);
                    }

                    if (boundary.Count < 8) return;
                    token.ThrowIfCancellationRequested();

                    List<Point> simplified = GeometryCalculations.DouglasPeucker(boundary, _simplifyEpsilon);
                    if (simplified.Count < 3) return;
                    token.ThrowIfCancellationRequested();

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        var polyline = new Polyline
                        {
                            Stroke = this.LineColor,
                            StrokeThickness = 2,
                            FillRule = FillRule.EvenOdd
                        };

                        // Boundary points are in cropped coordinates — translate back to canvas
                        foreach (var p in simplified)
                            polyline.Points.Add(new Point(
                                (p.X + bx0) * ScaleX + OffsetX,
                                (p.Y + by0) * ScaleY + OffsetY));

                        polyline.Points.Add(new Point(
                            (simplified[0].X + bx0) * ScaleX + OffsetX,
                            (simplified[0].Y + by0) * ScaleY + OffsetY));

                        _activePolyline = polyline;
                        _preSmoothSnapshot = new List<Point>(polyline.Points);

                        var output = new List<UIElement> { polyline };

                        CommitOperation(new OutlineOperation
                        {
                            OperationKind = "Outline",
                            SourceMode = this,
                            Elements = new List<UIElement>(output)
                        });

                        OnOutlineReady?.Invoke(output);
                    });
                }
                catch (OperationCanceledException) { }
            }, token);

            // Return empty immediately; results arrive via OnOutlineReady
            return new List<UIElement>();
        }

        private (byte r, byte g, byte b) ReadPixel(int x, int y)
        {
            int i = y * _cachedStride + x * _cachedBpp;
            if (_cachedBpp == 1) return (_cachedPixels[i], _cachedPixels[i], _cachedPixels[i]);
            return (_cachedPixels[i + 2], _cachedPixels[i + 1], _cachedPixels[i]);
        }

        private bool HasMinimumBorderLength(List<Point> boundary, int minBorderLength)
        {
            return boundary != null && boundary.Count >= minBorderLength;
        }

        #endregion

        #region erase function   
        private double _eraseBrushRadius = 20;
        public double EraseBrushRadius
        {
            get => _eraseBrushRadius;
            set { _eraseBrushRadius = value; OnPropertyChanged(nameof(EraseBrushRadius)); }
        }

        // =====================
        // ERASE / SHRINK OUTLINE
        // =====================
        // Called on mouse-drag when EraseOutlineMode is active.
        // Finds all polyline vertices within EraseBrushRadius of the cursor
        // and moves them toward the centroid of the full polyline,
        // shrinking that portion of the outline inward.
        public void ProcessEraseDrag(Vector2 mousePos)
        {
            if (_activePolyline == null) return;

            double mx = mousePos.X;
            double my = mousePos.Y;
            double r2 = EraseBrushRadius * EraseBrushRadius;

            var points = _activePolyline.Points;
            if (points.Count < 3) return;

            int n = points.Count;
            bool[] inside = new bool[n];
            bool anyInside = false;

            for (int i = 0; i < n; i++)
            {
                double dx = points[i].X - mx;
                double dy = points[i].Y - my;
                if (dx * dx + dy * dy <= r2)
                {
                    inside[i] = true;
                    anyInside = true;
                }
            }

            if (!anyInside) return;

            // Check whether the erased run wraps around the seam (last->first).
            // If so, rotate the point list so the seam falls inside a kept region,
            // preventing the bridging logic from splitting across the wrap point.
            if (inside[0] || inside[n - 1])
            {
                // Find the first index that is NOT inside the brush
                int rotateStart = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!inside[i]) { rotateStart = i; break; }
                }

                // If every point is inside the brush, the whole outline would be erased — bail out
                if (rotateStart < 0) return;

                // Rotate points and inside flags so rotateStart becomes index 0
                var rotatedPoints = new List<Point>(n);
                var rotatedInside = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    int src = (rotateStart + i) % n;
                    rotatedPoints.Add(points[src]);
                    rotatedInside[i] = inside[src];
                }

                points.Clear();
                foreach (var p in rotatedPoints)
                    points.Add(p);

                inside = rotatedInside;
                n = points.Count;
            }

            // Build new point list, replacing each erased run with one midpoint bridge
            var newPoints = new List<Point>();
            int i2 = 0;
            while (i2 < n)
            {
                if (!inside[i2])
                {
                    newPoints.Add(points[i2]);
                    i2++;
                }
                else
                {
                    int runStart = i2;
                    while (i2 < n && inside[i2]) i2++;

                    Point before = points[(runStart - 1 + n) % n];
                    Point after = points[i2 % n];

                    newPoints.Add(new Point(
                        (before.X + after.X) * 0.5,
                        (before.Y + after.Y) * 0.5));
                }
            }

            if (newPoints.Count < 3) return;

            // =====================
            // CLOSURE FAILSAFE
            // =====================
            // Guarantee the polyline is always explicitly closed:
            // the last point must equal the first point.
            // If they differ by more than 1 pixel, append a copy of the first point.

            points.Clear();
            foreach (var p in newPoints)
                points.Add(p);

            EnforcePolylineClosure();

            RefreshSmoothSnapshot();
        }
        private void EnforcePolylineClosure()
        {
            if (_activePolyline == null) return;
            var points = _activePolyline.Points;
            if (points.Count < 2) return;

            Point first = points[0];
            Point last = points[points.Count - 1];
            double dx = first.X - last.X;
            double dy = first.Y - last.Y;

            if (dx * dx + dy * dy >= 1.0)
                points.Add(first);
        }

        public void ClearActivePolyline()
        {
            _activePolyline = null;
        }
        #endregion

        #region smooth mode
        private int _smoothStrength = 0;
        public int SmoothStrength
        {
            get => _smoothStrength;
            set
            {
                _smoothStrength = value;
                OnPropertyChanged(nameof(SmoothStrength));
                ApplyGlobalSmooth();
            }
        }

        // Snapshot of the polyline points taken when smooth mode is entered,
        // so that smoothing always applies to the original shape rather than
        // compounding on each slider change.
        private List<Point> _preSmoothSnapshot = null;

        public void TakePreSmoothSnapshot()
        {
            if (_activePolyline == null) { _preSmoothSnapshot = null; return; }
            _preSmoothSnapshot = new List<Point>(_activePolyline.Points);
        }

        private void RefreshSmoothSnapshot()
        {
            if (_activePolyline == null) return;
            _preSmoothSnapshot = new List<Point>(_activePolyline.Points);
        }

        // Applies Laplacian smoothing to the entire polyline.
        // Runs SmoothStrength passes of neighbor-averaging over all points.
        // Always works from the pre-smooth snapshot so slider changes are
        // non-destructive and the original shape is recoverable by setting
        // the slider back to 0.
        private void ApplyGlobalSmooth()
        {
            if (_activePolyline == null) return;
            if (_preSmoothSnapshot == null || _preSmoothSnapshot.Count < 3) return;

            // Start from the original snapshot each time
            var working = new List<Point>(_preSmoothSnapshot);

            for (int pass = 0; pass < _smoothStrength; pass++)
            {
                int n = working.Count;
                var smoothed = new Point[n];

                for (int i = 0; i < n; i++)
                {
                    int prev = (i - 1 + n) % n;
                    int next = (i + 1) % n;

                    smoothed[i] = new Point(
                        (working[prev].X + working[i].X * 2 + working[next].X) / 4.0,
                        (working[prev].Y + working[i].Y * 2 + working[next].Y) / 4.0);
                }

                for (int i = 0; i < n; i++)
                    working[i] = smoothed[i];
            }

            // Write result back into the live polyline
            var points = _activePolyline.Points;
            points.Clear();
            foreach (var p in working)
                points.Add(p);

            // Ensure closed
            if (points.Count >= 2)
            {
                Point first = points[0];
                Point last = points[points.Count - 1];
                double dx = first.X - last.X;
                double dy = first.Y - last.Y;
                if (dx * dx + dy * dy >= 1.0)
                    points.Add(first);
            }
        }
        #endregion
        #region calculate metadata

        // =====================
        // RESULT PROPERTIES
        // =====================
        private double _aspectRatioResult;
        public double AspectRatioResult
        {
            get => _aspectRatioResult;
            set { _aspectRatioResult = value; OnPropertyChanged(nameof(AspectRatioResult)); }
        }

        private double _perimeterAreaRatioResult;
        public double PerimeterAreaRatioResult
        {
            get => _perimeterAreaRatioResult;
            set { _perimeterAreaRatioResult = value; OnPropertyChanged(nameof(PerimeterAreaRatioResult)); }
        }

        private double _circularityResult;
        public double CircularityResult
        {
            get => _circularityResult;
            set { _circularityResult = value; OnPropertyChanged(nameof(CircularityResult)); }
        }

        private double[] _efdCoefficientsResult;
        public double[] EFDCoefficientsResult
        {
            get => _efdCoefficientsResult;
            set { _efdCoefficientsResult = value; OnPropertyChanged(nameof(EFDCoefficientsResult)); }
        }

        private double _solidityResult;
        public double SolidityResult
        {
            get => _solidityResult;
            set { _solidityResult = value; OnPropertyChanged(nameof(SolidityResult)); }
        }

        private double _sumTurningAnglesResult;
        public double SumTurningAnglesResult
        {
            get => _sumTurningAnglesResult;
            set { _sumTurningAnglesResult = value; OnPropertyChanged(nameof(SumTurningAnglesResult)); }
        }

        private double _meanTurningAngleResult;
        public double MeanTurningAngleResult
        {
            get => _meanTurningAngleResult;
            set { _meanTurningAngleResult = value; OnPropertyChanged(nameof(MeanTurningAngleResult)); }
        }

        private double _varianceTurningAnglesResult;
        public double VarianceTurningAnglesResult
        {
            get => _varianceTurningAnglesResult;
            set { _varianceTurningAnglesResult = value; OnPropertyChanged(nameof(VarianceTurningAnglesResult)); }
        }

        // Formatted string for display in the control panel
        private string _metadataSummary = "";
        public string MetadataSummary
        {
            get => _metadataSummary;
            set { _metadataSummary = value; OnPropertyChanged(nameof(MetadataSummary)); }
        }

        // Called when the user clicks Generate Metadata
        public void GenerateMetadata()
        {
            if (_activePolyline == null || _activePolyline.Points.Count < 3)
            {
                MetadataSummary = "No outline available.";
                UpdateEFDPreview();
                return;
            }

            var pts = new List<Point>(_activePolyline.Points);

            // Remove duplicate closing point if present
            if (pts.Count > 1)
            {
                Point f = pts[0], l = pts[pts.Count - 1];
                if ((f.X - l.X) * (f.X - l.X) + (f.Y - l.Y) * (f.Y - l.Y) < 1.0)
                    pts.RemoveAt(pts.Count - 1);
            }

            // Convert from canvas space to image space for scale-invariant metric computation.
            // All GeometryCalculations calls below use image-space coordinates.
            var imagePts = pts.Select(CanvasToImage).ToList();

            double perimeter = GeometryCalculations.Perimeter(imagePts);
            double area = GeometryCalculations.PolygonArea(imagePts);
            double[] bbox = GeometryCalculations.BoundingBox(imagePts);
            double bboxW = bbox[2] - bbox[0];
            double bboxH = bbox[3] - bbox[1];
            AspectRatioResult = GeometryCalculations.BoundingBoxAspectRatio(bboxW, bboxH);
            PerimeterAreaRatioResult = GeometryCalculations.PerimeterAreaRatio(perimeter, area);
            CircularityResult = GeometryCalculations.Circularity(perimeter, area);

            double convexHullArea = GeometryCalculations.ConvexHullArea(imagePts);
            SolidityResult = GeometryCalculations.Solidity(area, convexHullArea);
            SumTurningAnglesResult = GeometryCalculations.SumTurningAngles(imagePts);
            MeanTurningAngleResult = GeometryCalculations.MeanTurningAngle(imagePts);
            VarianceTurningAnglesResult = GeometryCalculations.VarianceTurningAngles(imagePts);

            int harmonics = EfdHarmonics;
            EFDCoefficientsResult = _efd.ComputeNormalized(pts, harmonics);

            // Build display string
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Aspect Ratio:       {AspectRatioResult:F3}");
            sb.AppendLine($"Perim / Area:       {PerimeterAreaRatioResult:F4}");
            sb.AppendLine($"Circularity:        {CircularityResult:F4}");
            sb.AppendLine($"Solidity:           {SolidityResult:F4}");
            sb.AppendLine($"Sum Turning Angles: {SumTurningAnglesResult:F4}");
            sb.AppendLine($"Mean Turning Angle: {MeanTurningAngleResult:F4}");
            sb.AppendLine($"Variance Turning Angles: {VarianceTurningAnglesResult:F4}");
            sb.AppendLine($"EFD harmonics ({harmonics}):");
            for (int h = 0; h < harmonics; h++)
            {
                int k = h * 4;
                sb.AppendLine($"  n={h + 1}: a={EFDCoefficientsResult[k]:F4} b={EFDCoefficientsResult[k + 1]:F4} c={EFDCoefficientsResult[k + 2]:F4} d={EFDCoefficientsResult[k + 3]:F4}");
            }
            MetadataSummary = sb.ToString();

            // Stamp the result onto the committed operation so it persists with undo/redo
            if (UndoRedoManager?.CurrentOperation is OutlineOperation op)
            {
                op.AspectRatio = AspectRatioResult;
                op.PerimeterAreaRatio = PerimeterAreaRatioResult;
                op.Circularity = CircularityResult;
                op.EFDCoefficients = EFDCoefficientsResult;
                op.Solidity = SolidityResult;
                op.SumTurningAngles = SumTurningAnglesResult;
                op.MeanTurningAngle = MeanTurningAngleResult;
                op.VarianceTurningAngles = VarianceTurningAnglesResult;
            }

            UpdateEFDPreview();
        }

        // =====================
        // ELLIPTIC FOURIER DESCRIPTORS
        // =====================
        private readonly EllipticFourierAnalysis _efd = new EllipticFourierAnalysis();

        private int efdHarmonics = 10;
        public int EfdHarmonics
        {
            get => efdHarmonics;
            set
            {
                int clamped = Math.Max(1, Math.Min(100, value));
                if (efdHarmonics == clamped) return;
                efdHarmonics = clamped;
                OnPropertyChanged(nameof(EfdHarmonics));
                UpdateEFDPreview(); // ← add this
            }
        }

        // The blue EFD preview polyline shown in the workspace
        private Polyline _efdPreviewPolyline = null;
        public Action<Polyline> OnEFDPreviewReady;   // MainWindow wires this up
        public Action OnEFDPreviewClear;             // MainWindow wires this up

        // Reconstructs the outline from EFD coefficients and displays it as a
        // blue overlay. Called whenever EfdHarmonics changes or metadata is generated.
        public void UpdateEFDPreview()
        {
            OnEFDPreviewClear?.Invoke();
            _efdPreviewPolyline = null;

            if (_efd.RawCoefficients == null || _efd.RawCoefficients.Length == 0) return;
            if (_activePolyline == null || _activePolyline.Points.Count < 3) return;

            int harmonics = Math.Min(EfdHarmonics, _efd.RawCoefficients.Length / 4);
            if (harmonics < 1) return;

            double dcX = 0, dcY = 0;
            int ptCount = _activePolyline.Points.Count;
            foreach (var p in _activePolyline.Points) { dcX += p.X; dcY += p.Y; }
            dcX /= ptCount;
            dcY /= ptCount;

            var reconstructed = _efd.Reconstruct(harmonics, dcX, dcY);
            if (reconstructed == null) return;

            var previewLine = new Polyline
            {
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                FillRule = FillRule.EvenOdd
            };

            foreach (var p in reconstructed)
                previewLine.Points.Add(p);

            _efdPreviewPolyline = previewLine;
            OnEFDPreviewReady?.Invoke(previewLine);
        }

        public void ClearEFDPreview()
        {
            OnEFDPreviewClear?.Invoke();
            _efdPreviewPolyline = null;
            _efd.Clear();
        }
        #endregion
    }
}