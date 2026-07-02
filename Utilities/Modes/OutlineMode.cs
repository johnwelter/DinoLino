using DinoLino.DataTypes;
using DinoLino.Properties;
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

        private bool _handDrawMode = false;
        public bool HandDrawMode
        {
            get => _handDrawMode;
            set
            {
                if (!SetField(ref _handDrawMode, value)) return;
                OnTipChanged?.Invoke();
                // Leaving hand-draw with an unfinished stroke: discard it.
                if (!_handDrawMode) CancelHandDraw();
            }
        }

        // Fired when metadata is requested but the hand-drawn stroke isn't closed yet.
        public Action HandOutlineUnfinished;

        private bool _drawOutlineMode = true;
        public bool DrawOutlineMode
        {
            get => _drawOutlineMode;
            set
            {
                if (!SetField(ref _drawOutlineMode, value)) return;
                OnTipChanged?.Invoke();
            }
        }

        private bool _eraseOutlineMode = false;
        public bool EraseOutlineMode
        {
            get => _eraseOutlineMode;
            set
            {
                if (!SetField(ref _eraseOutlineMode, value)) return;
                OnTipChanged?.Invoke();
            }
        }

        private bool _smoothOutlineMode = false;
        public bool SmoothOutlineMode
        {
            get => _smoothOutlineMode;
            set
            {
                if (!SetField(ref _smoothOutlineMode, value)) return;
                OnTipChanged?.Invoke();
            }
        }

        private bool _outlineMetadataMode = false;
        public bool OutlineMetadataMode
        {
            get => _outlineMetadataMode;
            set
            {
                if (!SetField(ref _outlineMetadataMode, value)) return;
                OnTipChanged?.Invoke();
                if (_outlineMetadataMode) GenerateMetadata();
                else ClearEFDPreview();
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
            set => SetField(ref _minAreaPixels, value);
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
                if (!SetField(ref _useWatershed, value)) return;
                OnTipChanged?.Invoke();
            }
        }
        public override void ClearMetadata()
        {
            AspectRatioResult = 0;
            PerimeterAreaRatioResult = 0;
            CircularityResult = 0;
            SolidityResult = 0;
            TurningAngleLengthResult = 0;
            SumTurningAnglesResult = 0;
            EFDCoefficientsResult = null;
            MetadataSummary = "";
            PerimeterScaledResult = ScaledPlaceholder;
            AreaScaledResult = ScaledPlaceholder;
        }

        public override void RefreshScalePlaceholders()
        {
            PerimeterScaledResult = ScaledPlaceholder;
            AreaScaledResult = ScaledPlaceholder;
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
            set => SetField(ref _multiClickOutline, value);
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
                if (!SetField(ref _useActiveContour, value)) return;
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
            set => SetField(ref _fillSensitivity, value);
        }

        private double _edgeThreshold = 40;
        public double EdgeThreshold
        {
            get => _edgeThreshold;
            set => SetField(ref _edgeThreshold, value);
        }

        private double _simplifyEpsilon = 1.5;
        public double SimplifyEpsilon
        {
            get => _simplifyEpsilon;
            set => SetField(ref _simplifyEpsilon, value);
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
            CancelHandDraw();         
            _handOutlineCommitted = false;
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
    int seedPxX, int seedPxY, CancellationToken token, bool preserveMultipleComponents = false)
        {
            int sw = snap.Width, sh = snap.Height;

            // Strip the border band so flood bleed at the image edge can't anchor a component
            for (int i = 0; i < rawFull.Length; i++)
            {
                if (!rawFull[i]) continue;
                int rx = i % sw, ry = i / sw;
                if (rx <= 4 || rx >= sw - 5 || ry <= 4 || ry >= sh - 5) rawFull[i] = false;
            }

            var (bx0, by0, bx1, by1) = _processor.GetMaskBounds(rawFull, sw, sh, margin: 4);
            var (cropped, cw, ch) = _processor.CropMask(rawFull, sw, bx0, by0, bx1, by1);
            var croppedSnap = new ImageSnapshot(snap.Pixels, snap.BgMask, cw, ch, snap.Stride, snap.Bpp);

            bool[] traced;
            if (preserveMultipleComponents)
            {
                // Clean every component independently and keep all that survive.
                // No single-component collapse, no trace-seed dependence.
                bool[] work = _processor.CleanEachComponent(cropped, croppedSnap, MinAreaPixels);
                traced = _processor.HasMinimumPixels(work, MinAreaPixels) ? work : cropped;
            }
            else
            {
                // Single-region path: clean the whole mask as one shape, then
                // explicitly collapse to the largest component as a separate step.
                bool[] work = _processor.CleanComponentMorphology(cropped, croppedSnap);
                work = _processor.KeepLargestComponent(work, croppedSnap);

                int cpx = seedPxX - bx0, cpy = seedPxY - by0;
                traced = _processor.PrepareMaskForTracing(work, cropped, cpx, cpy, MinAreaPixels, croppedSnap);
            }

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

            // The crack-traced boundary is simple, but Douglas-Peucker can occasionally
            // cross a concave bay. Validate and fall back to the dense boundary if so.
            bool simAfterDP = !PolylineHasSelfIntersection(simplified);
            if (!simAfterDP)
                simplified = new List<Point>(boundary);

            // The destructive per-point border clamp that used to live here was REMOVED.
            // CleanMaskFullSpace already clears everything within 5 px of the edge, so
            // every traced point is in-bounds. Clamping each point onto a box could
            // collapse a near-edge concavity onto the box line and self-intersect the
            // polygon — that was the source of the corrupted metadata, and it only bit
            // specimens close to the image border (hence the intermittency).

            var poly = new Polyline
            {
                Stroke = dashed ? Brushes.OrangeRed : this.LineColor,
                StrokeThickness = 2,
                FillRule = FillRule.EvenOdd
            };
            if (dashed) poly.StrokeDashArray = new DoubleCollection { 4, 2 };

            // (p.X + bx0, p.Y + by0) maps cropped coords back to full-image coords — the
            // clamp loop used to apply this offset, so it must stay now that it's gone.
            foreach (var p in simplified)
                poly.Points.Add(new Point((p.X + bx0) * ScaleX + OffsetX, (p.Y + by0) * ScaleY + OffsetY));
            poly.Points.Add(new Point((simplified[0].X + bx0) * ScaleX + OffsetX, (simplified[0].Y + by0) * ScaleY + OffsetY));

            // Confirmation: with the simple tracer, the DP fallback, and no clamp, the
            // finished polyline should always be simple. Log only if it somehow isn't.
            if (PolylineHasSelfIntersection(poly.Points))
            {
                bool simRaw = !PolylineHasSelfIntersection(boundary);
                System.Diagnostics.Debug.WriteLine(
                    $"[outline build] FINAL self-intersects! simRaw={simRaw} simAfterDP={simAfterDP} pts={boundary.Count}");
            }

            return poly;
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            BeginOperation();

            if (_eraseOutlineMode || _smoothOutlineMode || _outlineMetadataMode || _handDrawMode)
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

                    int bridgeRadius = 6; // tune to the largest gap you want to span
                    accumulated = _processor.MorphClose(accumulated, _cachedWidth, _cachedHeight, bridgeRadius);

                    // Re-clean the union. Use the new click as the trace seed so
                    // PrepareMaskForTracing keeps the component the user just added.
                    bool[] cleaned = CleanMaskFullSpace(accumulated, snap, px, py, token, preserveMultipleComponents: true);
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
        #endregion

        #region hand draw

        // =====================
        // FREE-HAND OUTLINE
        // =====================
        // The user holds the left button and drags to lay down a stroke. Releasing pauses
        // the stroke (a subsequent press continues appending from the cursor). As soon as
        // the stroke crosses itself, the loop is closed at the crossing point and any
        // dangling tails before/after the loop are discarded — so a "p" or a loop with
        // two tails collapses to just the enclosed object.

        // Raw stroke points in CANVAS space, accumulated across press/drag/release until
        // the loop closes. Stored densely; simplified only at closure.
        private readonly List<Point> _handStroke = new List<Point>();

        // The live, in-progress (open) preview polyline shown while drawing.
        private Polyline _handPreviewPolyline = null;

        // True between the first press and final closure of a hand-drawn outline.
        private bool _handDrawingActive = false;

        // MainWindow wires these: add/remove the live preview line on the canvas.
        public Action<Polyline> OnHandPreviewReady;     // show/replace the open preview
        public Action<Polyline> OnHandPreviewClear;     // remove the given preview line

        // Minimum canvas distance between consecutive accepted stroke points. Keeps the
        // point list manageable and avoids degenerate zero-length segments that would
        // confuse the self-intersection test.
        private const double HandMinPointSpacing = 2.0;

        // True when an outline has been committed in this mode (used by the panel to gate
        // "Generate Metadata"). Distinct from _handDrawingActive, which means "mid-stroke".
        private bool _handOutlineCommitted = false;
        public bool HasFinishedHandOutline => _handOutlineCommitted;

        // Called from MainWindow on left-button DOWN while HandDrawMode is active.
        public void BeginHandStroke(Vector2 canvasPos)
        {
            if (!_handDrawMode) return;
            if (_cachedPixels == null) return;

            // First press of a brand-new outline: start fresh and clear any prior result.
            if (!_handDrawingActive)
            {
                BeginOperation();
                _handDrawingActive = true;
                _handOutlineCommitted = false;
                _handStroke.Clear();
                ClearHandPreview();
                ClearMetadata();
                ClearEFDPreview();
            }

            AppendHandPoint(new Point(canvasPos.X, canvasPos.Y));
        }

        // Called from MainWindow on mouse MOVE while the left button is held in HandDrawMode.
        public void ProcessHandDrawDrag(Vector2 canvasPos)
        {
            if (!_handDrawMode || !_handDrawingActive) return;
            AppendHandPoint(new Point(canvasPos.X, canvasPos.Y));
        }

        // Called from MainWindow on left-button UP while HandDrawMode is active.
        // Releasing simply pauses — the stroke stays open and can be continued.
        public void EndHandStroke()
        {
            // Intentionally does nothing beyond leaving the stroke open: a paused stroke
            // is resumed by the next BeginHandStroke, which keeps _handDrawingActive true
            // and so does NOT reset _handStroke.
        }

        // Discards any in-progress stroke (mode switch, reset, escape).
        public void CancelHandDraw()
        {
            _handDrawingActive = false;
            _handStroke.Clear();
            ClearHandPreview();
        }

        // Adds a point to the stroke (respecting min spacing), refreshes the live preview,
        // and tests whether the newly added segment closes the loop.
        private void AppendHandPoint(Point p)
        {
            if (_handStroke.Count > 0)
            {
                Point last = _handStroke[_handStroke.Count - 1];
                double dx = p.X - last.X, dy = p.Y - last.Y;
                if (dx * dx + dy * dy < HandMinPointSpacing * HandMinPointSpacing)
                    return; // too close to previous point; skip
            }

            _handStroke.Add(p);

            // Check whether the most recent segment crosses any earlier, non-adjacent segment.
            if (TryCloseHandLoop()) return;

            RefreshHandPreview();
        }

        // Builds/updates the open preview polyline from the current stroke.
        private void RefreshHandPreview()
        {
            if (_handStroke.Count < 2) { ClearHandPreview(); return; }

            var line = new Polyline
            {
                Stroke = this.LineColor,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 }, // dashed = not yet closed
                FillRule = FillRule.EvenOdd
            };
            foreach (var sp in _handStroke)
                line.Points.Add(sp);

            var old = _handPreviewPolyline;
            _handPreviewPolyline = line;
            OnHandPreviewReady?.Invoke(line);
            if (old != null) OnHandPreviewClear?.Invoke(old);
        }

        private void ClearHandPreview()
        {
            if (_handPreviewPolyline != null)
            {
                OnHandPreviewClear?.Invoke(_handPreviewPolyline);
                _handPreviewPolyline = null;
            }
        }

        // Tests whether the LAST segment of the stroke intersects any earlier non-adjacent
        // segment. If it does, the closed loop is extracted (the polygon between the two
        // crossing segments, joined at the intersection point), tails are discarded, and
        // the outline is committed exactly like an auto-generated one.
        //
        // Returns true if the loop was closed (and the stroke consumed), false otherwise.
        private bool TryCloseHandLoop()
        {
            int n = _handStroke.Count;
            if (n < 4) return false; // need at least a few segments to self-cross

            int lastSeg = n - 2;                     // segment (n-2 -> n-1)
            Point a1 = _handStroke[lastSeg];
            Point a2 = _handStroke[lastSeg + 1];

            // Compare against every earlier segment except the one directly adjacent
            // (which shares endpoint a1 and can't "cross" in a meaningful way).
            for (int j = 0; j <= lastSeg - 2; j++)
            {
                Point b1 = _handStroke[j];
                Point b2 = _handStroke[j + 1];

                if (TryGetSegmentIntersection(a1, a2, b1, b2, out Point hit))
                {
                    // The enclosed loop runs from the intersection point, along the stroke
                    // through indices j+1 .. lastSeg, and back to the intersection point.
                    // Everything before segment j (the leading tail) and after the last
                    // point (the trailing tail) is discarded.
                    var loop = new List<Point> { hit };
                    for (int k = j + 1; k <= lastSeg; k++)
                        loop.Add(_handStroke[k]);
                    // loop implicitly closes back to 'hit'

                    CommitHandLoop(loop);
                    return true;
                }
            }

            return false;
        }

        // Finalizes a closed hand-drawn loop: simplify, validate, convert to a committed
        // outline, and route it through the SAME commit path as an auto outline so all
        // metadata / EFD / smooth / erase behavior is shared.
        private void CommitHandLoop(List<Point> loopCanvas)
        {
            // De-dup consecutive coincident points.
            var cleaned = new List<Point>(loopCanvas.Count);
            foreach (var p in loopCanvas)
            {
                if (cleaned.Count == 0) { cleaned.Add(p); continue; }
                Point l = cleaned[cleaned.Count - 1];
                double dx = p.X - l.X, dy = p.Y - l.Y;
                if (dx * dx + dy * dy >= 0.25) cleaned.Add(p);
            }

            if (cleaned.Count < 3) { CancelHandDraw(); return; }

            // Light simplification to remove hand-jitter, matching the auto outline feel.
            var simplified = GeometryCalculations.DouglasPeucker(cleaned, _simplifyEpsilon);
            if (simplified.Count < 3) simplified = cleaned;

            // Guard: if simplification somehow self-intersected, fall back to the dense loop.
            if (PolylineHasSelfIntersection(simplified))
                simplified = cleaned;

            var poly = new Polyline
            {
                Stroke = this.LineColor,
                StrokeThickness = 2,
                FillRule = FillRule.EvenOdd
            };
            foreach (var p in simplified)
                poly.Points.Add(p);
            poly.Points.Add(simplified[0]); // explicit closure

            // Tear down the in-progress drawing state and the dashed preview.
            ClearHandPreview();
            _handDrawingActive = false;
            _handStroke.Clear();
            _handOutlineCommitted = true;

            // Reuse the existing commit path: sets _activePolyline, snapshots for smoothing,
            // commits an OutlineOperation, and fires OnOutlineReady so MainWindow draws it.
            CommitFinalOutline(poly);
        }

        // Segment/segment intersection returning the crossing point. Treats proper crossings
        // only (no collinear-overlap handling needed for a freehand stroke). Mirrors the sign
        // logic in SegmentsIntersect but also computes the intersection coordinate.
        private static bool TryGetSegmentIntersection(Point p1, Point p2, Point p3, Point p4, out Point hit)
        {
            hit = default;

            double d1x = p2.X - p1.X, d1y = p2.Y - p1.Y;
            double d2x = p4.X - p3.X, d2y = p4.Y - p3.Y;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-9) return false; // parallel / degenerate

            double t = ((p3.X - p1.X) * d2y - (p3.Y - p1.Y) * d2x) / denom;
            double u = ((p3.X - p1.X) * d1y - (p3.Y - p1.Y) * d1x) / denom;

            if (t < 0.0 || t > 1.0 || u < 0.0 || u > 1.0) return false;

            hit = new Point(p1.X + t * d1x, p1.Y + t * d1y);
            return true;
        }

        #endregion

        #region erase function   
        private double _eraseBrushRadius = 20;
        public double EraseBrushRadius
        {
            get => _eraseBrushRadius;
            set => SetField(ref _eraseBrushRadius, value);
        }

        // =====================
        // ERASE / SHRINK OUTLINE
        // =====================
        // Called on mouse-drag when EraseOutlineMode is active.
        // Finds all polyline vertices within EraseBrushRadius of the cursor
        // and moves them toward the centroid of the full polyline,
        // shrinking that portion of the outline inward.
        private bool _eraseInProgress = false;
        public void ProcessEraseDrag(Vector2 mousePos)
        {
            if (_activePolyline == null) return;
            if (_eraseInProgress) return;
            if (ScaleX <= 0 || ScaleY <= 0) return;
            _eraseInProgress = true;
            try
            {
                var srcPoints = _activePolyline.Points;
                if (srcPoints.Count < 3) return;

                // Work in IMAGE space so mask resolution is independent of zoom.
                var img = new List<Point>(srcPoints.Count);
                foreach (var cp in srcPoints)
                    img.Add(new Point((cp.X - OffsetX) / ScaleX, (cp.Y - OffsetY) / ScaleY));
                if (img.Count >= 2)
                {
                    Point f = img[0], l = img[img.Count - 1];
                    if ((f.X - l.X) * (f.X - l.X) + (f.Y - l.Y) * (f.Y - l.Y) < 1.0)
                        img.RemoveAt(img.Count - 1);
                }
                if (img.Count < 3) return;

                double rImgX = EraseBrushRadius / ScaleX;
                double rImgY = EraseBrushRadius / ScaleY;
                int pad = (int)Math.Ceiling(Math.Max(rImgX, rImgY)) + 2;

                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var p in img)
                {
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                }

                int ox = (int)Math.Floor(minX) - pad;
                int oy = (int)Math.Floor(minY) - pad;
                int w = (int)Math.Ceiling(maxX) - ox + pad;
                int h = (int)Math.Ceiling(maxY) - oy + pad;
                if (w < 3 || h < 3 || (long)w * h > 16_000_000) return;

                var local = new List<Point>(img.Count);
                foreach (var p in img) local.Add(new Point(p.X - ox, p.Y - oy));

                // 1) Rasterize the outline to a filled mask (even-odd fill).
                bool[] mask = RasterizePolygon(local, w, h);

                // 2) Carve the brush disc. Test each pixel's CANVAS distance so the brush
                //    stays a true on-screen circle even under non-uniform scale. Clearing
                //    pixels can only ever REMOVE area — no cursor-side dependence.
                double rCanvas2 = EraseBrushRadius * EraseBrushRadius;
                double cImgX = (mousePos.X - OffsetX) / ScaleX - ox;
                double cImgY = (mousePos.Y - OffsetY) / ScaleY - oy;
                int bx0 = Math.Max(0, (int)Math.Floor(cImgX - rImgX) - 1);
                int bx1 = Math.Min(w - 1, (int)Math.Ceiling(cImgX + rImgX) + 1);
                int by0 = Math.Max(0, (int)Math.Floor(cImgY - rImgY) - 1);
                int by1 = Math.Min(h - 1, (int)Math.Ceiling(cImgY + rImgY) + 1);
                bool anyCleared = false;
                for (int y = by0; y <= by1; y++)
                    for (int x = bx0; x <= bx1; x++)
                    {
                        if (!mask[y * w + x]) continue;
                        double canX = (x + ox + 0.5) * ScaleX + OffsetX;
                        double canY = (y + oy + 0.5) * ScaleY + OffsetY;
                        double dx = canX - mousePos.X, dy = canY - mousePos.Y;
                        if (dx * dx + dy * dy <= rCanvas2) { mask[y * w + x] = false; anyCleared = true; }
                    }
                if (!anyCleared) return;

                // 3) Re-fill enclosed holes (so an interior-only stroke does nothing rather
                //    than punching a hole), then keep the single largest blob.
                var snap = new ImageSnapshot(null, null, w, h, 0, 0);
                mask = _processor.FillHoles(mask, snap);
                mask = _processor.KeepLargestComponent(mask, snap);
                if (!_processor.HasMinimumPixels(mask, 9)) return;

                // 4) Re-trace the carved mask as a guaranteed-simple dense boundary.
                var boundary = _processor.TraceBoundary(mask, snap);
                if (boundary.Count < 8) return;

                // 5) Update ONLY the stretch of outline the brush actually overlapped,
                //    leaving every other vertex exactly where it was. This removes the
                //    "ripple": Douglas-Peucker reselects its vertices globally, so
                //    re-simplifying the WHOLE boundary on every drag shifts vertices in
                //    regions the brush never reached. Splicing just the affected arc keeps
                //    untouched vertices pinned (they re-project to the same canvas coords).
                List<Point> newLocal = SpliceErasedArc(local, boundary, mousePos, ox, oy);
                if (newLocal == null)
                {
                    // Couldn't isolate a single affected arc (e.g. the stroke pinched the
                    // shape into separate pieces). Fall back to re-simplifying the whole
                    // boundary — this can ripple, but only in these rare cases.
                    newLocal = GeometryCalculations.DouglasPeucker(boundary, _simplifyEpsilon);
                }
                if (newLocal == null || newLocal.Count < 3) return;

                srcPoints.Clear();
                foreach (var p in newLocal)
                    srcPoints.Add(new Point((p.X + ox) * ScaleX + OffsetX, (p.Y + oy) * ScaleY + OffsetY));
                srcPoints.Add(new Point((newLocal[0].X + ox) * ScaleX + OffsetX, (newLocal[0].Y + oy) * ScaleY + OffsetY));

                RefreshSmoothSnapshot();
            }
            finally { _eraseInProgress = false; }
        }

        // Even-odd scanline fill. pts are in local mask coordinates, closure dup removed.
        private static bool[] RasterizePolygon(List<Point> pts, int w, int h)
        {
            var mask = new bool[w * h];
            int n = pts.Count;
            if (n < 3) return mask;
            var xs = new List<double>(8);
            for (int y = 0; y < h; y++)
            {
                double scanY = y + 0.5;
                xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    Point a = pts[i], b = pts[(i + 1) % n];
                    double ay = a.Y, by = b.Y;
                    if ((ay <= scanY && by > scanY) || (by <= scanY && ay > scanY))
                    {
                        double t = (scanY - ay) / (by - ay);
                        xs.Add(a.X + t * (b.X - a.X));
                    }
                }
                if (xs.Count < 2) continue;
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int xStart = (int)Math.Ceiling(xs[k] - 0.5);
                    int xEnd = (int)Math.Floor(xs[k + 1] - 0.5);
                    if (xStart < 0) xStart = 0;
                    if (xEnd > w - 1) xEnd = w - 1;
                    int row = y * w;
                    for (int x = xStart; x <= xEnd; x++) mask[row + x] = true;
                }
            }
            return mask;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // ERASE: LOCAL SPLICE (anti-ripple)
        // ─────────────────────────────────────────────────────────────────────────
        // Replaces ONLY the contiguous arc the brush overlapped. Every other vertex is
        // reused as-is, so untouched regions re-project to the exact same canvas
        // coordinate and cannot drift.
        //
        //   oldLocal    : current outline vertices, local mask frame, no closure dup
        //   dense       : freshly traced boundary of the carved mask, same frame
        //   mouseCanvas : brush centre, canvas space
        //   ox, oy      : local-frame origin (local = image - (ox, oy))
        //
        // Returns the new vertex list (local frame, no closure dup), or null when the
        // affected region can't be isolated as a single arc (caller falls back).
        private List<Point> SpliceErasedArc(List<Point> oldLocal, List<Point> dense,
            Vector2 mouseCanvas, int ox, int oy)
        {
            int n = oldLocal.Count;
            if (n < 3 || dense.Count < 3) return null;

            // Seam slightly outside the brush so the join lands on unmodified geometry.
            double rTest = EraseBrushRadius + 2.0;
            double rTest2 = rTest * rTest;

            bool UnderBrush(Point pLocal)
            {
                double canX = (pLocal.X + ox) * ScaleX + OffsetX;
                double canY = (pLocal.Y + oy) * ScaleY + OffsetY;
                double dx = canX - mouseCanvas.X, dy = canY - mouseCanvas.Y;
                return dx * dx + dy * dy <= rTest2;
            }

            // 1) Find the vertices A and B flanking the affected span on oldLocal.
            var under = new bool[n];
            int underCount = 0;
            for (int i = 0; i < n; i++)
            {
                under[i] = UnderBrush(oldLocal[i]);
                if (under[i]) underCount++;
            }
            if (underCount == n) return null; // nothing stable left to anchor to

            int aIdx, bIdx;
            if (underCount > 0)
            {
                // Require the under-brush vertices to form exactly ONE contiguous run on
                // the cyclic outline; more than one means the brush touched the shape in
                // separate places and a single splice isn't valid.
                int runs = 0, runStart = -1;
                for (int i = 0; i < n; i++)
                    if (under[i] && !under[(i - 1 + n) % n]) { runs++; runStart = i; }
                if (runs != 1) return null;

                int runEnd = runStart;
                while (under[(runEnd + 1) % n]) runEnd = (runEnd + 1) % n;

                aIdx = (runStart - 1 + n) % n; // last kept vertex before the run
                bIdx = (runEnd + 1) % n;       // first kept vertex after the run
            }
            else
            {
                // Brush bit into an edge without covering a vertex: anchor to the edge
                // whose segment passes closest to the brush centre.
                int bestEdge = -1;
                double bestDist = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    double d = PointToSegmentCanvasDist2(oldLocal[i], oldLocal[(i + 1) % n],
                                                         mouseCanvas, ox, oy);
                    if (d < bestDist) { bestDist = d; bestEdge = i; }
                }
                if (bestEdge < 0) return null;
                aIdx = bestEdge;
                bIdx = (bestEdge + 1) % n;
            }

            Point A = oldLocal[aIdx];
            Point B = oldLocal[bIdx];

            // 2) Extract the carved arc of the dense boundary from (near A) to (near B)
            //    and simplify just that arc.
            int aJ = NearestIndex(dense, A);
            int bJ = NearestIndex(dense, B);
            if (aJ < 0 || bJ < 0 || aJ == bJ) return null;

            List<Point> arc = ChooseUnderBrushArc(
                ExtractArc(dense, aJ, bJ, true),
                ExtractArc(dense, aJ, bJ, false),
                UnderBrush);
            if (arc == null) return null;

            var arcSimpl = GeometryCalculations.DouglasPeucker(arc, _simplifyEpsilon);
            if (arcSimpl.Count < 2) return null;

            // 3) Kept vertices (walk B → … → A), then the new arc interior (A → B).
            var result = new List<Point>(n + arcSimpl.Count);
            for (int i = bIdx; ; i = (i + 1) % n)
            {
                result.Add(oldLocal[i]);
                if (i == aIdx) break;
            }
            for (int k = 1; k < arcSimpl.Count - 1; k++)
                result.Add(arcSimpl[k]);

            if (result.Count < 3) return null;
            if (PolylineHasSelfIntersection(result)) return null; // never return a tangled outline

            return result;
        }

        // Squared canvas-space distance from the brush centre to segment p0–p1 (local frame).
        private double PointToSegmentCanvasDist2(Point p0, Point p1, Vector2 mouseCanvas, int ox, int oy)
        {
            double ax = (p0.X + ox) * ScaleX + OffsetX, ay = (p0.Y + oy) * ScaleY + OffsetY;
            double bx = (p1.X + ox) * ScaleX + OffsetX, by = (p1.Y + oy) * ScaleY + OffsetY;
            double vx = bx - ax, vy = by - ay;
            double wx = mouseCanvas.X - ax, wy = mouseCanvas.Y - ay;
            double len2 = vx * vx + vy * vy;
            double t = len2 > 1e-9 ? (wx * vx + wy * vy) / len2 : 0.0;
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            double cx = ax + t * vx, cy = ay + t * vy;
            double dx = mouseCanvas.X - cx, dy = mouseCanvas.Y - cy;
            return dx * dx + dy * dy;
        }

        // Index of the point in pts closest to target.
        private static int NearestIndex(List<Point> pts, Point target)
        {
            int best = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i].X - target.X, dy = pts[i].Y - target.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // Sub-path of the cyclic list from index i to index j (endpoints included).
        // forward = true walks i → i+1 → … → j; forward = false walks i → i-1 → … → j.
        private static List<Point> ExtractArc(List<Point> pts, int i, int j, bool forward)
        {
            int n = pts.Count;
            var arc = new List<Point> { pts[i] };
            int idx = i, guard = n + 1;
            while (idx != j && guard-- > 0)
            {
                idx = forward ? (idx + 1) % n : (idx - 1 + n) % n;
                arc.Add(pts[idx]);
            }
            return arc;
        }

        // Of the two candidate arcs, returns the one whose interior lies under the brush
        // (the carved notch), or null if neither does.
        private static List<Point> ChooseUnderBrushArc(List<Point> a, List<Point> b, Func<Point, bool> underBrush)
        {
            double FracUnder(List<Point> arc)
            {
                int under = 0, total = 0;
                for (int k = 1; k < arc.Count - 1; k++) { total++; if (underBrush(arc[k])) under++; }
                return total == 0 ? 0.0 : (double)under / total;
            }
            double fa = FracUnder(a), fb = FracUnder(b);
            if (Math.Max(fa, fb) <= 0.0) return null;
            return fa >= fb ? a : b;
        }

        private static bool PolylineHasSelfIntersection(IList<Point> pts)
        {
            int n = pts.Count;
            if (n < 4) return false;
            for (int i = 0; i < n; i++)
            {
                Point a1 = pts[i], a2 = pts[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i) continue;
                    if ((i + 1) % n == j || (j + 1) % n == i) continue;
                    Point b1 = pts[j], b2 = pts[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(Point p1, Point p2, Point p3, Point p4)
        {
            double d1 = Cross(p3, p4, p1);
            double d2 = Cross(p3, p4, p2);
            double d3 = Cross(p1, p2, p3);
            double d4 = Cross(p1, p2, p4);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(Point a, Point b, Point c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        #endregion

        #region smooth mode
        private int _smoothStrength = 0;
        public int SmoothStrength
        {
            get => _smoothStrength;
            set
            {
                if (SetField(ref _smoothStrength, value))
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
            set => SetField(ref _aspectRatioResult, value);
        }

        private double _perimeterAreaRatioResult;
        public double PerimeterAreaRatioResult
        {
            get => _perimeterAreaRatioResult;
            set => SetField(ref _perimeterAreaRatioResult, value);
        }

        private double _circularityResult;
        public double CircularityResult
        {
            get => _circularityResult;
            set => SetField(ref _circularityResult, value);
        }

        private double[] _efdCoefficientsResult;
        public double[] EFDCoefficientsResult
        {
            get => _efdCoefficientsResult;
            set => SetField(ref _efdCoefficientsResult, value);
        }

        private double _solidityResult;
        public double SolidityResult
        {
            get => _solidityResult;
            set => SetField(ref _solidityResult, value);
        }

        private double _sumTurningAnglesResult;
        public double SumTurningAnglesResult
        {
            get => _sumTurningAnglesResult;
            set => SetField(ref _sumTurningAnglesResult, value);
        }

        private double _turningAngleLengthResult;
        public double TurningAngleLengthResult
        {
            get => _turningAngleLengthResult;
            set => SetField(ref _turningAngleLengthResult, value);
        }

        // Formatted string for display in the control panel
        private string _metadataSummary = "";
        public string MetadataSummary
        {
            get => _metadataSummary;
            set => SetField(ref _metadataSummary, value);
        }

        private string _perimeterScaledResult = "Scale to measure";
        public string PerimeterScaledResult
        {
            get => _perimeterScaledResult;
            set => SetField(ref _perimeterScaledResult, value);
        }

        private string _areaScaledResult = "Scale to measure";
        public string AreaScaledResult
        {
            get => _areaScaledResult;
            set => SetField(ref _areaScaledResult, value);
        }

        // Fired after metadata is generated and stamped onto the committed outline.
        // MainWindow wires this to refresh the on-image operation counter: setting
        // HasMetadata mutates the operation in place, which does NOT raise a history
        // change, so the counter would otherwise not update until the next commit.
        public Action OnMetadataGenerated;


        // Called when the user clicks Generate Metadata
        public void GenerateMetadata()
        {
            // Refuse while a hand-drawn stroke is still open (not yet self-closed).
            if (_handDrawMode && _handDrawingActive)
            {
                HandOutlineUnfinished?.Invoke();
                return;
            }

            if (_activePolyline == null || _activePolyline.Points.Count < 3)
            {
                MetadataSummary = "No outline available.";
                PerimeterScaledResult = ScaledPlaceholder;
                AreaScaledResult = ScaledPlaceholder;
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

            // Scaled outputs use canvas-space measurements, matching the canvas-space
            // calibration captured by the Scale Image tool (imagePts above stays image-space
            // so the ratio metrics remain zoom-independent).
            double canvasPerimeter = GeometryCalculations.Perimeter(pts);
            double canvasArea = GeometryCalculations.PolygonArea(pts);
            PerimeterScaledResult = Scale != null && Scale.IsCalibrated
                ? $"{Scale.ToUnits(canvasPerimeter):F2} {Scale.Unit}"
                : "Scale to measure";
            AreaScaledResult = Scale != null && Scale.IsCalibrated
                ? $"{Scale.ToUnitsArea(canvasArea):F2} {Scale.Unit}²"
                : "Scale to measure";

            double[] bbox = GeometryCalculations.BoundingBox(imagePts);
            double bboxW = bbox[2] - bbox[0];
            double bboxH = bbox[3] - bbox[1];
            AspectRatioResult = GeometryCalculations.BoundingBoxAspectRatio(bboxW, bboxH);
            PerimeterAreaRatioResult = GeometryCalculations.PerimeterAreaRatio(perimeter, area);
            CircularityResult = GeometryCalculations.Circularity(perimeter, area);

            double convexHullArea = GeometryCalculations.ConvexHullArea(imagePts);
            SolidityResult = GeometryCalculations.Solidity(area, convexHullArea);
            SumTurningAnglesResult = GeometryCalculations.SumTurningAngles(imagePts);
            TurningAngleLengthResult = GeometryCalculations.TurningAnglePerLength(SumTurningAnglesResult, perimeter);

            int harmonics = EfdHarmonics;
            EFDCoefficientsResult = _efd.ComputeNormalized(pts, harmonics);

            // Build display string
            var sb = new System.Text.StringBuilder();

            if (_efd.NormalizationStatus != EfdNormalizationStatus.Ok)
                sb.AppendLine($"  ⚠ Orientation ambiguous (1st-harmonic axis ratio {_efd.FirstHarmonicAxisRatio:F2}); normalized rotation may be unstable.");

            sb.AppendLine($"Aspect Ratio:       {AspectRatioResult:F3}");
            sb.AppendLine($"Perim / Area:       {PerimeterAreaRatioResult:F4}");
            sb.AppendLine($"Circularity:        {CircularityResult:F4}");
            sb.AppendLine($"Solidity:           {SolidityResult:F4}");
            sb.AppendLine($"Sum Turning Angles: {SumTurningAnglesResult:F4}");
            sb.AppendLine($"Turn. Angles / Length: {TurningAngleLengthResult:F4}");
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
                op.TurningAngleLength = TurningAngleLengthResult;
                op.Perimeter = canvasPerimeter;
                op.Area = canvasArea;
                op.HasMetadata = true;

                // HasMetadata just flipped on an operation already sitting in history.
                // Recompute the on-image counter now — mutating the op in place doesn't
                // raise a history-changed event, so nothing else will trigger the refresh.
                OnMetadataGenerated?.Invoke();
            }

            UpdateEFDPreview();
        }

        // =====================
        // ELLIPTIC FOURIER DESCRIPTORS
        // =====================
        private readonly EllipticFourierAnalysis _efd = new EllipticFourierAnalysis();

        // Session-level accumulator of EFD coefficient tables for multi-specimen CSV export.
        // Deliberately NOT cleared in Reset(), so the batch survives opening new images and
        // clearing the workspace within a session.
        public EfdCsvCollector EfdCsv { get; } = new EfdCsvCollector();

        private int efdHarmonics = 10;
        public int EfdHarmonics
        {
            get => efdHarmonics;
            set
            {
                int clamped = Math.Max(1, Math.Min(100, value));
                if (SetField(ref efdHarmonics, clamped))
                    UpdateEFDPreview();
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

            // Reconstruct using the contour's own DC term (arc-length centroid) rather than a
            // vertex average. Douglas-Peucker leaves vertices unevenly spaced, so the vertex
            // average drifts toward densely-sampled regions; the arc-length centroid does not.
            var reconstructed = _efd.ReconstructCanonical(harmonics);
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

        // Number of harmonics computed when analyzing harmonic power. Independent of the display
        // harmonic count (EfdHarmonics) so the cumulative-power curve has room to converge even
        // when the user is viewing only a few harmonics.
        private const int HarmonicPowerAnalysisCeiling = 50;

        // Runs a harmonic-power analysis on the current outline. threshold is a fraction in [0,1]
        // (e.g. 0.99). Returns null when there is no usable outline. Does NOT change EfdHarmonics,
        // the cached display coefficients, or the blue overlay.
        public HarmonicPowerProfile AnalyzeHarmonicPower(double threshold, bool dropFirstHarmonic = true)
        {
            if (_activePolyline == null || _activePolyline.Points.Count < 3) return null;

            var pts = new List<Point>(_activePolyline.Points);

            // Drop a closing duplicate vertex if present (same prep as GenerateMetadata).
            if (pts.Count > 1)
            {
                Point f = pts[0], l = pts[pts.Count - 1];
                if ((f.X - l.X) * (f.X - l.X) + (f.Y - l.Y) * (f.Y - l.Y) < 1.0)
                    pts.RemoveAt(pts.Count - 1);
            }
            if (pts.Count < 3) return null;

            // Never request more harmonics than the vertex count can support (~Nyquist): a coarse,
            // heavily-simplified outline can't meaningfully express dozens of harmonics, so the
            // suggested count may be lower than EfdHarmonics' 100 ceiling for such outlines.
            int ceiling = Math.Min(HarmonicPowerAnalysisCeiling, Math.Max(1, pts.Count / 2));
            return _efd.AnalyzeHarmonicPower(pts, ceiling, threshold, dropFirstHarmonic);
        }

        // Applies a chosen harmonic count to the EFD display setting (the setter clamps 1..100
        // and refreshes the blue preview).
        public void ApplyHarmonicCount(int harmonics) => EfdHarmonics = harmonics;

        public override string[] GetTips()
        {
            if (DrawOutlineMode)
                return UseWatershed
                    ? new[] 
                    { 
                        "💡 Use Watershed to generate more accurate outlines on complex images, at the cost of reduced speed.",
                        "💡 To increase speed, try decimating pixel count using the Decimate function in the View menu.",
                        "💡 Use multi-click mode to merge multiple regions. To finalize an outline in multi-click mode, click inside the area bounded by a dashed line.",
                        "💡 Outline mode performs best on unpatterned images with a solid background.",
                        "💡 The user guide and software information can be found in the Help menu.",
                        "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
                        "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
                        "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                        "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                        "💡 Zoom in or out using the scroll wheel.",
                        "💡 Press 'Ctrl' and left click to drag the image.",
                        "💡 Toggle tip visibility in the View menu."
                    }
                    : new[] 
                    { 
                        "💡 Set fill sensitivity to maximum values for images on a solid background.",
                        "💡 Use multi-click mode to merge multiple regions. To finalize an outline in multi-click mode, click inside the area bounded by a dashed line.",
                        "💡 To increase speed, try decimating pixel count using the Decimate function in the View menu.",
                        "💡 Having trouble with the outline? Watershed mode may improve accuracy for complex or textured images.",
                        "💡 Outline mode performs best on unpatterned images with a solid background.",
                        "💡 The user guide and software information can be found in the Help menu.",
                        "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.",
                        "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.",
                        "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                        "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                        "💡 Zoom in or out using the scroll wheel.",
                        "💡 Press 'Ctrl' and left click to drag the image.",
                        "💡 Toggle tip visibility in the View menu."
                    };
            if (EraseOutlineMode)
                return new[] 
                { 
                    "💡 Click and drag over the outline to erase. Adjust brush size for precision.",
                    "💡 Outline mode performs best on unpatterned images with a solid background.",
                    "💡 The user guide and software information can be found in the Help menu.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Press 'Ctrl' and left click to drag the image.",
                    "💡 Toggle tip visibility in the View menu."
                };
            if (SmoothOutlineMode)
                return new[] 
                { 
                    "💡 Adjust smooth strength for cleaner outlines. Too high may distort sharp features.",
                    "💡 Outline mode performs best on unpatterned images with a solid background.",
                    "💡 The user guide and software information can be found in the Help menu.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Press 'Ctrl' and left click to drag the image.",
                    "💡 Toggle tip visibility in the View menu."
                };
            if (OutlineMetadataMode)
                return new[] 
                { 
                    "💡 Adjust the number of EFD Harmonics to control Fourier detail. The EF outline is overlaid in a blue, dashed line.",
                    "💡 A perfect circle has a circularity value of 1. Circularity, aka roundness, is calculated as ⁠4π × Area ÷ Perimeter squared⁠.",
                    "💡 Solidity is the ratio of the outlined area divided by the area of its convex hull. The convex hull is the smallest convex polygon enclosing the outline.",
                    "💡 Sum of turning angles is the sum of all angular changes between consecutive edges, representing the total amount of turning around the outline.",
                    "💡 The user guide and software information can be found in the Help menu.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Press 'Ctrl' and left click to drag the image.",
                    "💡 Toggle tip visibility in the View menu."
                };
            if (HandDrawMode)
                return new[]
                {
                    "💡 Hold the left mouse button and drag to draw an outline by hand.",
                    "💡 The outline closes automatically as soon as your line crosses itself. Any leftover tails are removed.",
                    "💡 Release to pause; press and drag again to continue the same line.",
                    "💡 Once closed, switch to Generate Metadata to measure the shape.",
                    "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.",
                    "💡 Zoom in or out using the scroll wheel.",
                    "💡 Press 'Ctrl' and left click to drag the image.",
                    "💡 Toggle tip visibility in the View menu."
                };
            return new[] { string.Empty };
        }
        #endregion
    }
}