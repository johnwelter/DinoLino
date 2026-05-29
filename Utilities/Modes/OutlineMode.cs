using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
                OnTipChanged?.Invoke();
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
                OnTipChanged?.Invoke();
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
                OnTipChanged?.Invoke();
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
                OnTipChanged?.Invoke();

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
            // Compute variance across the four corner patch averages to estimate
            // how uniform the background is. High variance = noisy/gradient background
            // = use a higher threshold. Low variance = clean background = use lower threshold.
            double adaptiveThreshold = ComputeAdaptiveBackgroundThreshold(bgColors);
            _backgroundMask = _processor.BuildBackgroundMaskProgressive(
                _cachedPixels, _cachedWidth, _cachedHeight, _cachedStride, _cachedBpp,
                bgColors,
                tightThreshold: adaptiveThreshold * 0.6,
                relaxedThreshold: adaptiveThreshold);
        }

        private double ComputeAdaptiveBackgroundThreshold((double r, double g, double b)[] bgColors)
        {
            // Compute mean color across all four corners
            double mr = 0, mg = 0, mb = 0;
            foreach (var (r, g, b) in bgColors) { mr += r; mg += g; mb += b; }
            mr /= 4; mg /= 4; mb /= 4;

            // Compute max perceptual distance between any corner and the mean
            double maxDist = 0;
            foreach (var (r, g, b) in bgColors)
            {
                double dr = r - mr, dg = g - mg, db = b - mb;
                double dist = Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db);
                if (dist > maxDist) maxDist = dist;
            }

            // Map corner spread to threshold: uniform background → 35, noisy → 60
            return Math.Max(35, Math.Min(60, 35 + maxDist * 0.8));
        }


        private (double r, double g, double b)[] _bgColors;
        // Samples one pixel at each corner for use as individual background seeds.
        private (double r, double g, double b)[] EstimateBackgroundColors()
        {
            int w = _cachedWidth, h = _cachedHeight;
            int patch = Math.Max(4, Math.Min(20, Math.Min(w, h) / 10));

            (double r, double g, double b) SamplePatch(int x0, int y0, int x1, int y1)
            {
                double tr = 0, tg = 0, tb = 0; int count = 0;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var (r, g, b) = ReadPixel(x, y);
                        tr += r; tg += g; tb += b; count++;
                    }
                return count > 0 ? (tr / count, tg / count, tb / count) : (0, 0, 0);
            }

            int inset = Math.Min(5, Math.Min(w, h) / 20); // skip the outermost pixels
            int p = patch - 1;
            return new[]
            {
                SamplePatch(inset,         inset,         inset + p,         inset + p),         // top-left
                SamplePatch(w - patch - inset, inset,     w - 1 - inset,     inset + p),         // top-right
                SamplePatch(inset,         h - patch - inset, inset + p,     h - 1 - inset),     // bottom-left
                SamplePatch(w - patch - inset, h - patch - inset, w - 1 - inset, h - 1 - inset), // bottom-right
            };
        }

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
            set
            {
                _useWatershed = value;
                OnPropertyChanged(nameof(UseWatershed));
                OnTipChanged?.Invoke();
            }
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

        // Multi-click toggle (bound in XAML)
        private bool _multiClickOutline = false;
        public bool MultiClickOutline
        {
            get => _multiClickOutline;
            set { _multiClickOutline = value; OnPropertyChanged(nameof(MultiClickOutline)); OnTipChanged?.Invoke(); }
        }

        // Pending (uncommitted) outline state for multi-click
        private bool[] _pendingMask;          // full-image-space accumulated foreground mask
        private Polyline _pendingPolyline;    // the dashed preview currently shown
        private bool _hasPending = false;

        // (newPending, oldPending) — MainWindow swaps them on the canvas
        public Action<Polyline, Polyline> OnPendingOutlineReady;

        private bool _useActiveContour = false;
        public bool UseActiveContour
        {
            get => _useActiveContour;
            set
            {
                _useActiveContour = value;
                OnPropertyChanged(nameof(UseActiveContour));
                OnTipChanged?.Invoke();
            }
        }

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
            _pendingMask = null;
            _pendingPolyline = null;
            _hasPending = false;
            _efd.Clear();
            ClearMetadata();
            ClearEFDPreview();
        }

        private readonly OutlineProcessor _processor = new OutlineProcessor();

        // =====================
        // PROCESS CLICK
        // =====================
        public Action<List<UIElement>> OnOutlineReady;

        // Runs the full cleanup pipeline on a full-image-space mask.
        // Returns the cleaned full-image-space mask, or null if it fails.
        private bool[] CleanMaskFullSpace(bool[] rawFull, ImageSnapshot snap,
            int seedPxX, int seedPxY, CancellationToken token)
        {
            // border strip
            int sw = snap.Width, sh = snap.Height;
            for (int i = 0; i < rawFull.Length; i++)
            {
                if (!rawFull[i]) continue;
                int rx = i % sw, ry = i / sw;
                if (rx <= 4 || rx >= sw - 5 || ry <= 4 || ry >= sh - 5) rawFull[i] = false;
            }

            var (bx0, by0, bx1, by1) = _processor.GetMaskBounds(rawFull, sw, sh, margin: 4);
            var (cropped, cw, ch) = _processor.CropMask(rawFull, sw, bx0, by0, bx1, by1);
            var croppedSnap = new ImageSnapshot(snap.Pixels, snap.BgMask, cw, ch, snap.Stride, snap.Bpp);

            bool[] work = _processor.FillHoles(cropped, croppedSnap);
            work = _processor.ExtractLargestComponent(work, croppedSnap);
            work = _processor.MorphOpen(work, cw, ch, radius: 2);
            work = _processor.ExtractLargestComponent(work, croppedSnap);
            work = _processor.MorphClose(work, cw, ch, radius: 1);
            _processor.EnforceMinimumCorridorWidth(work, croppedSnap, minWidth: 3);
            _processor.SmoothBorderTopology(work, croppedSnap);
            _processor.PruneDeadEnds(work, croppedSnap);
            work = _processor.KeepLargestComponent(work, croppedSnap);

            int cpx = seedPxX - bx0, cpy = seedPxY - by0;
            bool[] traced = _processor.PrepareMaskForTracing(work, cropped, cpx, cpy, MinAreaPixels, croppedSnap);

            // Paste cropped result back into full-image space
            bool[] full = new bool[sw * sh];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    if (traced[y * cw + x]) full[(by0 + y) * sw + (bx0 + x)] = true;
            return full;
        }

        private Polyline BuildPolylineFromFullMask(bool[] full, ImageSnapshot snap, bool dashed)
        {
            var (bx0, by0, bx1, by1) = _processor.GetMaskBounds(full, snap.Width, snap.Height, margin: 2);
            var (cropped, cw, ch) = _processor.CropMask(full, snap.Width, bx0, by0, bx1, by1);
            var croppedSnap = new ImageSnapshot(snap.Pixels, snap.BgMask, cw, ch, snap.Stride, snap.Bpp);

            var boundary = _processor.TraceBoundary(cropped, croppedSnap);
            if (boundary.Count < 8) return null;

            var simplified = GeometryCalculations.DouglasPeucker(boundary, _simplifyEpsilon);
            if (simplified.Count < 3) return null;

            const int borderMargin = 5;
            for (int i = 0; i < simplified.Count; i++)
            {
                double cx = Math.Max(borderMargin, Math.Min(snap.Width - borderMargin - 1, simplified[i].X + bx0));
                double cy = Math.Max(borderMargin, Math.Min(snap.Height - borderMargin - 1, simplified[i].Y + by0));
                simplified[i] = new Point(cx, cy);
            }

            var poly = new Polyline
            {
                Stroke = dashed ? Brushes.OrangeRed : this.LineColor,
                StrokeThickness = 2,
                FillRule = FillRule.EvenOdd
            };
            if (dashed) poly.StrokeDashArray = new DoubleCollection { 4, 2 };

            foreach (var p in simplified)
                poly.Points.Add(new Point(p.X * ScaleX + OffsetX, p.Y * ScaleY + OffsetY));
            poly.Points.Add(new Point(simplified[0].X * ScaleX + OffsetX, simplified[0].Y * ScaleY + OffsetY));
            return poly;
        }

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

            // ── Multi-click: a pending outline already exists ──
            if (_multiClickOutline && _hasPending && _pendingMask != null)
            {
                int idx = py * _cachedWidth + px;
                bool clickInside = _pendingMask[idx];

                if (clickInside)
                {
                    ConfirmPending();          // commit + clear pending state
                    return new List<UIElement>();
                }
                else
                {
                    ExpandPending(px, py);     // flood new seed, merge, re-preview
                    return new List<UIElement>();
                }
            }

            // ── First click (single-click mode OR first click of multi-click) ──
            StartNewOutline(px, py);
            return new List<UIElement>();
        }

        private void StartNewOutline(int px, int py)
        {
            var token = CancellationToken;
            var snap = new ImageSnapshot(_cachedPixels, _backgroundMask, _cachedWidth, _cachedHeight, _cachedStride, _cachedBpp);

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    bool[] full = FloodFullSpace(px, py, snap);
                    if (full == null) return;

                    bool[] cleaned = CleanMaskFullSpace(full, snap, px, py, token);
                    if (cleaned == null || !_processor.HasMinimumPixels(cleaned, MinAreaPixels)) return;
                    token.ThrowIfCancellationRequested();

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        if (_multiClickOutline)
                        {
                            var poly = BuildPolylineFromFullMask(cleaned, snap, dashed: true);
                            if (poly == null) return;
                            SwapPending(cleaned, poly);   // store mask + show dashed preview
                        }
                        else
                        {
                            var poly = BuildPolylineFromFullMask(cleaned, snap, dashed: false);
                            if (poly == null) return;
                            CommitFinalOutline(poly);     // your existing commit path
                        }
                    });
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private bool[] FloodFullSpace(int px, int py, ImageSnapshot snap)
        {
            var (sr, sg, sb) = _processor.SampleSeedColor(px, py, snap, radius: 2);
            bool[] raw = _useWatershed
                ? (_processor.WatershedSegment(px, py, snap, seedRadius: 3, _watershedBlurLevel)
                   ?? _processor.FloodFill(px, py, sr, sg, sb, snap, _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold))
                : _processor.FloodFill(px, py, sr, sg, sb, snap, _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold);
            return _processor.HasMinimumPixels(raw, 1) ? raw : null;
        }

        private void ExpandPending(int px, int py)
        {
            var token = CancellationToken;
            var snap = new ImageSnapshot(_cachedPixels, _backgroundMask, _cachedWidth, _cachedHeight, _cachedStride, _cachedBpp);
            bool[] accumulated = (bool[])_pendingMask.Clone();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    bool[] newFlood = FloodFullSpace(px, py, snap);
                    if (newFlood == null) return;

                    // Union the new seed's region with the accumulated outline
                    for (int i = 0; i < accumulated.Length; i++)
                        if (newFlood[i]) accumulated[i] = true;

                    // Re-clean the union. Use the new click as the trace seed so
                    // PrepareMaskForTracing keeps the component the user just added.
                    bool[] cleaned = CleanMaskFullSpace(accumulated, snap, px, py, token);
                    if (cleaned == null) return;
                    token.ThrowIfCancellationRequested();

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        var poly = BuildPolylineFromFullMask(cleaned, snap, dashed: true);
                        if (poly == null) return;
                        SwapPending(cleaned, poly);
                    });
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private void SwapPending(bool[] mask, Polyline newPoly)
        {
            var old = _pendingPolyline;
            _pendingMask = mask;
            _pendingPolyline = newPoly;
            _activePolyline = newPoly;   // so smooth/erase/metadata operate on it if confirmed
            _hasPending = true;
            OnPendingOutlineReady?.Invoke(newPoly, old);
        }

        private void ConfirmPending()
        {
            if (_pendingPolyline == null) return;

            // Recolor from dashed preview to a solid committed outline
            _pendingPolyline.StrokeDashArray = null;
            _pendingPolyline.Stroke = this.LineColor;

            var output = new List<UIElement> { _pendingPolyline };
            _activePolyline = _pendingPolyline;
            _preSmoothSnapshot = new List<Point>(_pendingPolyline.Points);

            CommitOperation(new OutlineOperation
            {
                OperationKind = "Outline",
                SourceMode = this,
                Elements = new List<UIElement>(output)
            });

            OnOutlineReady?.Invoke(output);

            _hasPending = false;
            _pendingMask = null;
            _pendingPolyline = null;
        }

        private void CommitFinalOutline(Polyline poly)
        {
            _activePolyline = poly;
            _preSmoothSnapshot = new List<Point>(poly.Points);

            var output = new List<UIElement> { poly };

            CommitOperation(new OutlineOperation
            {
                OperationKind = "Outline",
                SourceMode = this,
                Elements = new List<UIElement>(output)
            });

            OnOutlineReady?.Invoke(output);
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

            // Resample to uniform arc-length spacing before smoothing so that
            // Laplacian pressure is even around the whole outline. Without this,
            // dense regions (tight curves) smooth faster than sparse ones (straight runs),
            // causing corners to drift unpredictably.
            var working = ResampleUniform(new List<Point>(_preSmoothSnapshot), targetSpacing: 4.0);

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

        // Resamples a closed polyline to approximately uniform arc-length spacing.
        // This ensures Laplacian smoothing applies equal pressure at every vertex,
        // preventing corners from drifting based on local point density.
        // The last point is assumed to be a closure duplicate of the first and is
        // preserved as such after resampling.
        private List<Point> ResampleUniform(List<Point> points, double targetSpacing)
        {
            if (points == null || points.Count < 3) return points;

            // Build cumulative arc-length table (excluding the closure duplicate)
            int n = points.Count;
            bool hasClosure = false;
            {
                Point f = points[0], l = points[n - 1];
                double dx = f.X - l.X, dy = f.Y - l.Y;
                hasClosure = (dx * dx + dy * dy) < 1.0;
            }
            int open = hasClosure ? n - 1 : n; // number of distinct vertices

            var lengths = new double[open];
            lengths[0] = 0;
            for (int i = 1; i < open; i++)
            {
                double dx = points[i].X - points[i - 1].X;
                double dy = points[i].Y - points[i - 1].Y;
                lengths[i] = lengths[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            // Close the loop: distance from last distinct vertex back to first
            {
                double dx = points[0].X - points[open - 1].X;
                double dy = points[0].Y - points[open - 1].Y;
                double totalLength = lengths[open - 1] + Math.Sqrt(dx * dx + dy * dy);

                if (totalLength < 1e-6) return points;

                // How many evenly-spaced samples fit around the perimeter?
                int count = Math.Max(3, (int)Math.Round(totalLength / targetSpacing));
                double step = totalLength / count;

                var result = new List<Point>(count + 1);
                int seg = 0;
                for (int k = 0; k < count; k++)
                {
                    double target = k * step;
                    // Advance segment pointer
                    while (seg < open - 1 && lengths[seg + 1] < target) seg++;

                    // Interpolate within the current segment (wraps: last->first)
                    double segStart = lengths[seg];
                    double segEnd = seg < open - 1 ? lengths[seg + 1]
                                                   : lengths[open - 1] + Math.Sqrt(
                                                       (points[0].X - points[open - 1].X) * (points[0].X - points[open - 1].X) +
                                                       (points[0].Y - points[open - 1].Y) * (points[0].Y - points[open - 1].Y));
                    double t = (segEnd > segStart) ? (target - segStart) / (segEnd - segStart) : 0;

                    Point a = points[seg];
                    Point b = seg < open - 1 ? points[seg + 1] : points[0];
                    result.Add(new Point(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
                }

                // Re-add closure point
                if (hasClosure) result.Add(result[0]);
                return result;
            }
        }
        #endregion

        #region metadata

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

        public override string[] GetTips()
        {
            if (DrawOutlineMode)
                return UseWatershed
                    ? new[] 
                    { 
                        "💡 Use Watershed to generate more accurate outlines on complex images, at the cost of reduced speed.",
                        "💡 Use multi-click mode to merge multiple regions. To finalize an outline in multi-click mode, click inside the area bounded by a dashed line.",
                        "💡 To increase speed, try decimating pixel count using the Decimate function in the View menu.",
                        "💡 Outline mode performs best on unpatterned images with a solid background.",
                        "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
                        "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
                        "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                        "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                        "💡 Zoom in or out using the scroll wheel.",
                        "💡 Toggle tip visibility in the View menu."
                    }
                    : new[] 
                    { 
                        "💡 Set fill sensitivity to maximum values for images on a solid background.",
                        "💡 To increase speed, try decimating pixel count using the Decimate function in the View menu.",
                        "💡 Having trouble with the outline? Watershed mode may improve accuracy for complex or textured images.",
                        "💡 Outline mode performs best on unpatterned images with a solid background.",
                        "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
                        "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
                        "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                        "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                        "💡 Zoom in or out using the scroll wheel.",
                        "💡 Toggle tip visibility in the View menu."
                    };
            if (EraseOutlineMode)
                return new[] 
                { 
                    "💡 Click and drag over the outline to erase. Adjust brush size for precision.",
                    "💡 Outline mode performs best on unpatterned images with a solid background.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Toggle tip visibility in the View menu."
                };
            if (SmoothOutlineMode)
                return new[] 
                { 
                    "💡 Adjust smooth strength for cleaner outlines. Too high may distort sharp features.",
                    "💡 Outline mode performs best on unpatterned images with a solid background.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Toggle tip visibility in the View menu."
                };
            if (OutlineMetadataMode)
                return new[] 
                { 
                    "💡 Adjust the number of EFD Harmonics to control Fourier detail. The EF outline is overlaid in a blue, dashed line.",
                    "💡 A perfect circle has a circularity value of 1. Circularity, aka roundness, is calculated as ⁠4π × Area ÷ Perimeter squared⁠.",
                    "💡 Solidity is the ratio of the outlined area divided by the area of its convex hull. The convex hull is the smallest convex polygon enclosing the outline.",
                    "💡 Sum of turning angles is the sum of all angular changes between consecutive edges, representing the total amount of turning around the outline.",
                    "💡 Mean of turning angles is the average turning angle per vertex, representing the typical magnitude of directional change around the outline.",
                    "💡 Variance of turning angles indicates how consistent or uneven curvature is around the outline.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Toggle tip visibility in the View menu."
                };
            return new[] { string.Empty };
        }
        #endregion
    }
}