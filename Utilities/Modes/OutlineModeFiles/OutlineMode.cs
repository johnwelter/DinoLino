using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageAnalysis = DinoLino.Utilities.OutlineProcessor.ImageAnalysis;
using ImageSnapshot = DinoLino.Utilities.OutlineProcessor.ImageSnapshot;


namespace DinoLino.Utilities.Modes
{
    // Automatic + manual outline detection and measurement.
    public class OutlineMode : WorkMode, IOutlineToolContext
    {
        public override string TabName => "Outline";
        // Cached so tab switches preserve panel state (rebuilding it leaked
        // duplicate EFA windows via PropertyChanged subscriptions).
        private OutlineControlPanel _cachedPanel;
        public override UserControl CreateControlPanel() => _cachedPanel ??= new OutlineControlPanel(this);
        public override bool IsStartingNewOperation => true;

        // Erase/smooth/metadata clicks edit the existing outline and return no new
        // elements, so the "new operation" workspace clear must NOT fire for them or
        // the click wipes the outline being edited.
        public override bool IsProbeInteraction =>
            _eraseOutlineMode || _pushOutlineMode || _smoothOutlineMode || _outlineMetadataMode;

        #region Tools (hand draw, erase, smooth)

        // ── Tool objects ── Each tool owns its parameters and drag handlers and sees
        // the mode only through IOutlineToolContext.
        public HandDrawTool HandDraw { get; }
        public EraseTool Erase { get; }
        public PushTool Push { get; }
        public SmoothTool Smooth { get; }

        public OutlineMode()
        {
            HandDraw = new HandDrawTool(this);
            Erase = new EraseTool(this);
            Push = new PushTool(this);
            Smooth = new SmoothTool(this);

            // Explicit wire so a later Global smooth builds on the erased shape.
            Erase.OutlineEdited += Smooth.RefreshSnapshot;
            Push.OutlineEdited += Smooth.RefreshSnapshot;

            // Contract: editing tools raise OutlineEdited; the mode invalidates the
            // cached dense contour so GenerateMetadata falls through to the live
            // (edited) polyline. A new editing tool wired the same way is covered.
            Erase.OutlineEdited += InvalidateDenseContour;
            Push.OutlineEdited += InvalidateDenseContour;
            Smooth.OutlineEdited += InvalidateDenseContour;
        }

        // ── IOutlineToolContext (explicit: the tools' window into the mode) ──
        Polyline IOutlineToolContext.ActivePolyline => _activePolyline;
        ViewTransform IOutlineToolContext.Transform => _transform;
        double IOutlineToolContext.SimplifyEpsilon => _simplifyEpsilon;
        Brush IOutlineToolContext.LineColor => LineColor;
        bool IOutlineToolContext.HasImage => _cachedPixels != null;
        bool IOutlineToolContext.IsHandDrawActive => _handDrawMode;
        int IOutlineToolContext.ImagePixelWidth => _cachedPixels != null ? _cachedWidth : 0;
        int IOutlineToolContext.ImagePixelHeight => _cachedPixels != null ? _cachedHeight : 0;

        void IOutlineToolContext.OnHandStrokeStarted()
        {
            // First press of a new hand stroke: start an undo op and clear any result.
            BeginOperation();
            ClearMetadata();
            ClearEFDPreview();
            InvalidateDenseContour();
        }

        void IOutlineToolContext.CommitOutline(Polyline outline) => CommitFinalOutline(outline);

        #endregion

        #region Active sub-mode

        private bool _handDrawMode = false;
        public bool HandDrawMode
        {
            get => _handDrawMode;
            set
            {
                if (!SetField(ref _handDrawMode, value)) return;
                OnTipChanged?.Invoke();
                // Leaving hand-draw with an unfinished stroke: discard it.
                if (!_handDrawMode) HandDraw.Cancel();
            }
        }

        // Fired when metadata is requested but the hand-drawn stroke isn't
        // closed yet. As an event only += / -= are possible.
        public event Action HandOutlineUnfinished;

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

        private bool _pushOutlineMode = false;
        public bool PushOutlineMode
        {
            get => _pushOutlineMode;
            set
            {
                if (!SetField(ref _pushOutlineMode, value)) return;
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

        // Cheap idempotent flag; the panel drives GenerateMetadata / ClearEFDPreview
        // from its Checked/Unchecked handlers (see OutlineControlPanel.xaml.cs).
        private bool _outlineMetadataMode = false;
        public bool OutlineMetadataMode
        {
            get => _outlineMetadataMode;
            set
            {
                if (!SetField(ref _outlineMetadataMode, value)) return;
                OnTipChanged?.Invoke();
            }
        }
        #endregion

        #region Source image and per-image analysis
        private BitmapSource _sourceImage;
        public BitmapSource SourceImage
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

        private Task<ImageAnalysis> _analysisTask;
        private int _imageVersion;

        // This CTS
        // supersedes the analysis exactly like _opCts supersedes clicks.
        private CancellationTokenSource _analysisCts;

        // Debounces the analysis rebuild: rapid SourceImage swaps (specimen ▲/▼,
        // picture adjustments) restart the timer so the pixel copy and the 1–3 s SAM
        // encode run only for the image the user lands on.
        private System.Windows.Threading.DispatcherTimer _analysisDebounce;
        private const int AnalysisDebounceMs = 600;

        private void CacheSourcePixels()
        {
            _imageVersion++;
            _opCts?.Cancel();   // supersede any operation still running on the previous image

            var oldAnalysis = _analysisCts;
            oldAnalysis?.Cancel();
            oldAnalysis?.Dispose();  // tokens stay readable after disposal
            _analysisCts = null;

            // A pending multi-click outline belongs to its source image, so any
            // image change clears it (ProcessClick also checks _pendingImageVersion).
            ClearPendingState();

            // Invalidate before the debounce so clicks/tools read "no image" and
            // no-op during the window instead of racing a not-yet-started analysis.
            _cachedPixels = null;
            _analysisTask = null;

            if (_sourceImage == null)
            {
                _analysisDebounce?.Stop();
                return;
            }

            if (_analysisDebounce == null)
            {
                _analysisDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(AnalysisDebounceMs)
                };
                _analysisDebounce.Tick += (s, e) =>
                {
                    _analysisDebounce.Stop();
                    StartImageAnalysis();
                };
            }

            // Restart the window: another swap before it elapses means this
            // image is being skimmed past and should never pay for analysis.
            _analysisDebounce.Stop();
            _analysisDebounce.Start();
        }

        // Copies pixels (fast) then runs the analysis — background mask, Sobel
        // gradient, distance transform, texture, retinex, SAM encode — on a worker
        // so a large image doesn't freeze the UI; the first click awaits the task.
        private void StartImageAnalysis()
        {
            if (_sourceImage == null) return;

            var formatted = new FormatConvertedBitmap(_sourceImage, PixelFormats.Bgra32, null, 0);
            _cachedWidth = formatted.PixelWidth;
            _cachedHeight = formatted.PixelHeight;
            _cachedBpp = 4;
            _cachedStride = _cachedWidth * 4;
            _cachedPixels = new byte[_cachedStride * _cachedHeight];
            formatted.CopyPixels(_cachedPixels, _cachedStride, 0);

            byte[] pixels = _cachedPixels;
            int w = _cachedWidth, h = _cachedHeight, stride = _cachedStride, bpp = _cachedBpp;
            _analysisCts = new CancellationTokenSource();
            CancellationToken analysisToken = _analysisCts.Token;
            // Token also passed to Task.Run so a pre-cancelled start yields a
            // Canceled (not Faulted) task, which callers treat as superseded.
            _analysisTask = Task.Run(() => BuildImageAnalysis(pixels, w, h, stride, bpp, analysisToken), analysisToken);
        }

        private ImageAnalysis BuildImageAnalysis(byte[] pixels, int w, int h, int stride, int bpp, CancellationToken token)
        {
            var proc = new OutlineProcessor();

            // SAM image encoder, run once per image (in parallel with the classical
            // caches) when a model is installed; each click then pays only a fast
            // decoder pass. Null when absent/failed, and downstream falls back.
            Task<SamImageState> samTask = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    return SamSegmenter.Shared?.EncodeImage(pixels, w, h, stride, bpp);
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[sam] encode failed: {ex.Message}");
                    return null;
                }
            }, token);

            // Border-palette background model (see EstimateBackgroundPalette):
            // ~6 clustered border colors + per-corner trust flags + adaptive threshold.
            var (bgPalette, hardSeedCorners, adaptiveThreshold) =
                proc.EstimateBackgroundPalette(pixels, w, h, stride, bpp);
            bool[] bgMask = proc.BuildBackgroundMaskProgressive(
                pixels, w, h, stride, bpp, bgPalette, hardSeedCorners,
                tightThreshold: adaptiveThreshold * 0.6,
                relaxedThreshold: adaptiveThreshold);
            token.ThrowIfCancellationRequested();

            var snap = new ImageSnapshot(pixels, bgMask, w, h, stride, bpp);
            int[] gradient = proc.ComputeGradient(snap);

            // Edge cutoff for mask scoring: 90th-percentile gradient, floored at a
            // ~9-level luma step so a flat background can't make every rim look edgy.
            int edgeThreshold = Math.Max(
                OutlineProcessor.GradientPercentileThreshold(gradient, 0.90), 120_000);

            // Per-pixel local texture (luma std-dev over 7×7). Lets the adaptive
            // flood match on texture and self-tune tolerance from the seed.
            float[] textureMap = proc.ComputeTextureMap(snap);
            token.ThrowIfCancellationRequested();

            int[] distToBackground = proc.ComputeDistanceToBackground(w, h, bgMask);
            token.ThrowIfCancellationRequested();

            // Illumination-flattened copy (retinex, radius ~min(w,h)/8) with its
            // background model rebuilt on it, so a vignetted/side-lit scene becomes
            // uniform for both the flattened flood and its bg mask.
            int flattenRadius = Math.Max(8, Math.Min(w, h) / 8);
            byte[] flattened = proc.ComputeIlluminationFlattened(snap, flattenRadius);
            var (flatPalette, flatCorners, flatThreshold) =
                proc.EstimateBackgroundPalette(flattened, w, h, stride, bpp);
            bool[] flatBgMask = proc.BuildBackgroundMaskProgressive(
                flattened, w, h, stride, bpp, flatPalette, flatCorners,
                tightThreshold: flatThreshold * 0.6,
                relaxedThreshold: flatThreshold);
            token.ThrowIfCancellationRequested();

            SamImageState samState = null;
            try { samState = samTask.Result; } catch { samState = null; }

            System.Diagnostics.Debug.WriteLine(samState != null
                ? $"[sam] embedding ready ({w}x{h}) — neural candidate active"
                : "[sam] embedding unavailable — neural candidate disabled for this image");

            return new ImageAnalysis
            {
                Pixels = pixels,
                Width = w,
                Height = h,
                Stride = stride,
                Bpp = bpp,
                BackgroundMask = bgMask,
                Gradient = gradient,
                DistToBackground = distToBackground,
                GradientEdgeThreshold = edgeThreshold,
                TextureMap = textureMap,
                FlattenedPixels = flattened,
                FlattenedBackgroundMask = flatBgMask,
                SamState = samState
            };
        }

        // Background palette estimation lives in OutlineProcessor
        // (EstimateBackgroundPalette): pure pixel statistics, no mode state.

        // Canvas → image mapping for the click math below.
        private Point CanvasToImage(Point p) => _transform.CanvasToImage(p);
        #endregion

        #region Shared overrides

        // When true, forces watershed for every click.
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
            EFDCoefficientsResult = null;
            MetadataSummary = "";
            _hasScaledMeasurements = false;
            RecomputeScaledValues();
        }

        public override void RefreshScalePlaceholders()
        {
            // Calibration changed: re-derive scaled numbers from the stored canvas
            // measurements and re-raise IsScaleCalibrated / ScaleUnit.
            RecomputeScaledValues();
        }
        #endregion

        #region Click pipeline and outline construction


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

        private int _pendingImageVersion = -1;

        // Click coordinates for the current pending outline, fed to the neural
        // candidate as positive point prompts so multi-click re-prompts the model
        // into one object. UI-thread only; ToArray() before capturing to a task.
        private readonly List<(int x, int y)> _pendingClickPoints = new List<(int x, int y)>();

        // (newPending, oldPending) — MainWindow swaps them on the canvas
        public event Action<Polyline, Polyline> PendingOutlineReady;

        // 0=Off, 1=Low, 2=High. Only honored when UseWatershed==true; auto mode
        // tries blur levels itself.
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

        // ── View transform (zoom + pan) ── ScaleX/Y and OffsetX/Y are views over one
        // immutable ViewTransform value; prefer assigning Transform once per zoom/pan.
        private ViewTransform _transform = ViewTransform.Identity;
        public ViewTransform Transform
        {
            get => _transform;
            set
            {
                if (!SetField(ref _transform, value)) return;
                OnPropertyChanged(nameof(ScaleX));
                OnPropertyChanged(nameof(ScaleY));
                OnPropertyChanged(nameof(OffsetX));
                OnPropertyChanged(nameof(OffsetY));
            }
        }

        public double ScaleX
        {
            get => _transform.ScaleX;
            set => Transform = new ViewTransform(value, _transform.ScaleY, _transform.OffsetX, _transform.OffsetY);
        }
        public double ScaleY
        {
            get => _transform.ScaleY;
            set => Transform = new ViewTransform(_transform.ScaleX, value, _transform.OffsetX, _transform.OffsetY);
        }
        public double OffsetX
        {
            get => _transform.OffsetX;
            set => Transform = new ViewTransform(_transform.ScaleX, _transform.ScaleY, value, _transform.OffsetY);
        }
        public double OffsetY
        {
            get => _transform.OffsetY;
            set => Transform = new ViewTransform(_transform.ScaleX, _transform.ScaleY, _transform.OffsetX, value);
        }

        // Superseding cancellation: a new click cancels the previous computation
        // instead of racing it.
        private CancellationTokenSource _opCts;

        // Busy indicator, raised/lowered around background click operations.
        public event Action<bool> BusyChanged;
        private int _busyCount;

        private void EnterBusy()
        {
            if (System.Threading.Interlocked.Increment(ref _busyCount) == 1)
                BusyChanged?.Invoke(true);
        }

        private void ExitBusy()
        {
            if (System.Threading.Interlocked.Decrement(ref _busyCount) == 0)
                BusyChanged?.Invoke(false);
        }

        // One-line diagnostic of the last click's candidate arbitration, e.g.
        // "[outline] click(412,300) neural=0.81 winner=neural".
        private string _lastSegmentationInfo = "";
        public string LastSegmentationInfo
        {
            get => _lastSegmentationInfo;
            private set => SetField(ref _lastSegmentationInfo, value);
        }

        public override void Reset()
        {
            base.Reset();
            _opCts?.Cancel();
            _activePolyline = null;
            InvalidateDenseContour();
            ClearPendingState();
            HandDraw.Reset();       // discards any open stroke AND the committed flag
            Smooth.ClearSnapshot(); // the outline is gone; never smooth from a ghost
            ClearEFDPreview();
        }

        // CancelCurrentOperation (Esc) also discards an open hand-drawn stroke.
        public override void CancelCurrentOperation()
        {
            base.CancelCurrentOperation();
            HandDraw?.Cancel();
        }

        // Drops all multi-click pending state.
        private void ClearPendingState()
        {
            _pendingMask = null;
            _pendingPolyline = null;
            _hasPending = false;
            _pendingImageVersion = -1;
            _pendingClickPoints.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHARED CLICK-OPERATION SCAFFOLDING
        // ─────────────────────────────────────────────────────────────────────

        // Cooperative cancellation inside the body, so the token is NOT passed
        // to Task.Run — the delegate always runs and busy stays balanced.
        private void RunClickOperation(Func<ImageAnalysis, OutlineProcessor, CancellationToken, Task> body)
        {
            var analysisTask = _analysisTask;
            if (analysisTask == null) return;

            // Newest click wins: supersede any in-flight computation. Per-operation
            // processors remove the buffer race between overlapping clicks.
            var superseded = _opCts;
            superseded?.Cancel();
            _opCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            var token = _opCts.Token;
            superseded?.Dispose();   // tokens stay readable after disposal

            EnterBusy();
            Task.Run(async () =>
            {
                try
                {
                    ImageAnalysis a = await analysisTask.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    // Per-operation processor: scratch buffers are never shared
                    // across concurrent tasks.
                    var proc = new OutlineProcessor();

                    await body(a, proc, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    RaiseOperationFailed($"Outline detection failed:\n{ex.Message}");
                }
                finally { ExitBusy(); }
            });
        }

        // Marshals a computed result back to the UI thread, dropping it when
        // the operation was superseded or the image changed while computing.

        private void PostResultToUi(int version, CancellationToken token, Action apply)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return; // application shutting down
            // BeginInvoke not Invoke: the worker needn't wait and can't deadlock a
            // blocked UI thread; the stale-guard still runs on the UI thread.
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (token.IsCancellationRequested || version != _imageVersion) return;
                apply();
            }));
        }

        public event Action<string> OperationFailed;

        private void RaiseOperationFailed(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[outline] {message}");
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) OperationFailed?.Invoke(message);
            else dispatcher.BeginInvoke(new Action(() => OperationFailed?.Invoke(message)));
        }

        // ---- PROCESS CLICK ----
        public event Action<List<UIElement>> OutlineReady;

        // Current tuning values, read fresh for every click so panel changes apply
        // to the next click without any further wiring.
        private OutlineProcessor.SegmentationSettings CurrentSegmentationSettings =>
            new OutlineProcessor.SegmentationSettings
            {
                ForceWatershed = _useWatershed,
                WatershedBlurLevel = _watershedBlurLevel,
                FillSensitivity = _fillSensitivity,
                EdgeThreshold = _edgeThreshold,
                MinAreaPixels = MinAreaPixels
            };

        // Turns a cleaned mask into the canvas polyline shown to the user, and keeps
        // the dense contour the elliptic Fourier analysis runs on.
        private Polyline BuildPolylineFromFullMask(bool[] full, ImageSnapshot snap, bool dashed,
            OutlineProcessor proc, int[] gradient, int edgeGradThreshold)
        {
            var (simplified, dense) = proc.BuildSimplifiedContour(
                full, snap, gradient, edgeGradThreshold, _simplifyEpsilon);
            if (simplified == null) return null;

            var poly = OutlineVisuals.CreateOutlinePolyline(LineColor, dashed);
            var t = Transform;

            foreach (var p in simplified)
                poly.Points.Add(t.ImageToCanvas(p));
            poly.Points.Add(t.ImageToCanvas(simplified[0]));

            // Ownership stamp: a later commit of a DIFFERENT polyline can't reuse this.
            SetDenseContour(dense, poly);

            if (PolylineGeometry.HasSelfIntersection(poly.Points))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[outline build] final polyline self-intersects with {poly.Points.Count} points.");
            }

            return poly;
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            BeginOperation();

            if (_eraseOutlineMode || _pushOutlineMode || _smoothOutlineMode || _outlineMetadataMode || _handDrawMode)
                return new List<UIElement>();
            if (_cachedPixels == null || _analysisTask == null) return new List<UIElement>();

            // Math.Floor not a cast: truncation-toward-zero would map a click just
            // left/above the image onto row/col 0 instead of failing bounds.
            int px = (int)Math.Floor((mousePos.X - OffsetX) / ScaleX);
            int py = (int)Math.Floor((mousePos.Y - OffsetY) / ScaleY);
            if ((uint)px >= _cachedWidth || (uint)py >= _cachedHeight)
                return new List<UIElement>();

            // Pending state is stamped with its image version (see SwapPending) and
            // dropped on mismatch; belt-and-braces with CacheSourcePixels.
            if (_hasPending && (_pendingMask == null || _pendingImageVersion != _imageVersion))
                ClearPendingState();

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
            if (_analysisTask == null) return;

            int version = _imageVersion;

            // SAM prompt bookkeeping: a NEW outline resets the accumulated
            // click list to just this click; ExpandPending appends instead.
            _pendingClickPoints.Clear();
            _pendingClickPoints.Add((px, py));
            var samPrompts = _pendingClickPoints.ToArray();

            RunClickOperation((a, proc, token) =>
            {
                var (raw, usedX, usedY, snap, info) = proc.SegmentAtClick(
                    px, py, a, CurrentSegmentationSettings, token, samPrompts);
                LastSegmentationInfo = info;
                if (raw == null) return Task.CompletedTask;

                bool[] cleaned = proc.CleanMaskFullSpace(raw, snap, usedX, usedY, MinAreaPixels, token);
                if (cleaned == null || !proc.HasMinimumPixels(cleaned, MinAreaPixels)) return Task.CompletedTask;
                token.ThrowIfCancellationRequested();

                PostResultToUi(version, token, () =>
                {
                    bool dashed = _multiClickOutline;
                    var poly = BuildPolylineFromFullMask(cleaned, snap, dashed, proc, a.Gradient, a.GradientEdgeThreshold);
                    if (poly == null) return;
                    if (dashed) SwapPending(cleaned, poly);   // store mask + show dashed preview
                    else CommitFinalOutline(poly);            // existing commit path
                });
                return Task.CompletedTask;
            });
        }

        private void ExpandPending(int px, int py)
        {
            if (_analysisTask == null || _pendingMask == null) return;

            int version = _imageVersion;
            bool[] accumulated = (bool[])_pendingMask.Clone();

            _pendingClickPoints.Add((px, py));
            var samPrompts = _pendingClickPoints.ToArray();

            RunClickOperation((a, proc, token) =>
            {
                var (newFlood, usedX, usedY, snap, info) = proc.SegmentAtClick(
                    px, py, a, CurrentSegmentationSettings, token, samPrompts);
                LastSegmentationInfo = info;
                if (newFlood == null) return Task.CompletedTask;

                // Union the new seed's region with the accumulated outline
                for (int i = 0; i < accumulated.Length; i++)
                    if (newFlood[i]) accumulated[i] = true;

                const int bridgeRadius = 6; // tune to the largest gap you want to span
                // Cropped close, avoiding two full-image distance transforms per click.
                accumulated = proc.MorphCloseCropped(accumulated, a.Width, a.Height, bridgeRadius);

                // Re-clean the union. Use the new click as the trace seed so
                // PrepareMaskForTracing keeps the component the user just added.
                bool[] cleaned = proc.CleanMaskFullSpace(accumulated, snap, usedX, usedY, MinAreaPixels, token,
                    preserveMultipleComponents: true);
                if (cleaned == null) return Task.CompletedTask;
                token.ThrowIfCancellationRequested();

                PostResultToUi(version, token, () =>
                {
                    var poly = BuildPolylineFromFullMask(cleaned, snap, dashed: true, proc, a.Gradient, a.GradientEdgeThreshold);
                    if (poly == null) return;
                    SwapPending(cleaned, poly);
                });
                return Task.CompletedTask;
            });
        }

        private void SwapPending(bool[] mask, Polyline newPoly)
        {
            var old = _pendingPolyline;
            _pendingMask = mask;
            _pendingPolyline = newPoly;
            _activePolyline = newPoly;   // so smooth/erase/metadata operate on it if confirmed
            _hasPending = true;
            _pendingImageVersion = _imageVersion; // stamp the owning image
            PendingOutlineReady?.Invoke(newPoly, old);
        }

        private void ConfirmPending()
        {
            if (_pendingPolyline == null) return;

            // Recolor from dashed preview to a solid committed outline
            _pendingPolyline.StrokeDashArray = null;
            _pendingPolyline.Stroke = this.LineColor;

            var output = new List<UIElement> { _pendingPolyline };
            _activePolyline = _pendingPolyline;
            Smooth.SetSnapshot(_pendingPolyline.Points); // smoothing baseline = the committed shape

            CommitOperation(new OutlineOperation
            {
                OperationKind = "Outline",
                SourceMode = this,
                Elements = new List<UIElement>(output)
            });

            ResetHarmonicAutoDefault(); // new specimen → re-derive the 99% default
            OutlineReady?.Invoke(output);

            ClearPendingState();
        }

        private void CommitFinalOutline(Polyline poly)
        {
            _activePolyline = poly;
            Smooth.SetSnapshot(poly.Points); // smoothing baseline = the committed shape

            var output = new List<UIElement> { poly };

            CommitOperation(new OutlineOperation
            {
                OperationKind = "Outline",
                SourceMode = this,
                Elements = new List<UIElement>(output)
            });

            ResetHarmonicAutoDefault(); // new specimen → re-derive the 99% default
            OutlineReady?.Invoke(output);
        }
        #endregion

        #region Tool forwarders for MainWindow input routing

        // Mode-level access to the tool objects for callers that hold the mode.

        public event Action<Polyline> HandPreviewReady
        {
            add => HandDraw.PreviewReady += value;
            remove => HandDraw.PreviewReady -= value;
        }
        public event Action<Polyline> HandPreviewClear
        {
            add => HandDraw.PreviewClear += value;
            remove => HandDraw.PreviewClear -= value;
        }

        #endregion

        #region Measurements, elliptic Fourier analysis, and tips

        /// Raised when the user asks to keep the current outline as a silhouette.
        /// MainWindow owns the window, because the specimen name and working
        /// directory live there rather than in the mode.
        public event Action CommitOutlineRequested;

        /// <summary>True when there is an outline worth storing.</summary>
        public bool CanCommitOutline => _activePolyline != null && _activePolyline.Points.Count >= 3;

        /// <summary>Asks the host to open the commit window for this outline.</summary>
        public void RequestCommitOutline() => CommitOutlineRequested?.Invoke();

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

        // Dense traced boundary in full-image space (no closure dup), before Douglas-Peucker.
        private List<Point> _activeDenseContourImage;

        // The polyline the cached dense contour was traced FROM. The cache is valid only
        // while it still describes the ACTIVE outline: hand-draw commits a polyline it
        // traced itself, so _activePolyline changes without BuildPolylineFromFullMask ever
        // running, and EFA would otherwise keep analyzing the previous auto outline.
        private Polyline _denseContourOwner;

        // True only when the cached dense contour belongs to the outline being measured.
        private bool HasDenseContourForActiveOutline =>
            _activePolyline != null
            && ReferenceEquals(_denseContourOwner, _activePolyline)
            && _activeDenseContourImage != null
            && _activeDenseContourImage.Count >= 3;

        private void SetDenseContour(List<Point> dense, Polyline owner)
        {
            _activeDenseContourImage = dense;
            _denseContourOwner = owner;
        }

        // Clears the cached dense EFA contour (via tools' OutlineEdited) so the
        // next GenerateMetadata analyzes the edited polyline. Cheap, idempotent.
        private void InvalidateDenseContour()
        {
            _activeDenseContourImage = null;
            _denseContourOwner = null;
        }

        // How many equally-spaced points the dense contour is resampled to before computing 
        // coefficients. Higher = more faithful, slower.
        private int _contourSampleCount = 128;
        public int ContourSampleCount
        {
            get => _contourSampleCount;
            set { if (SetField(ref _contourSampleCount, Math.Max(16, Math.Min(2048, value)))) UpdateEFDPreview(); }
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

        private string _normalizationWarning = "";
        public string NormalizationWarning
        {
            get => _normalizationWarning;
            set
            {
                if (SetField(ref _normalizationWarning, value))
                    OnPropertyChanged(nameof(HasNormalizationWarning));
            }
        }

        // Visibility companion for NormalizationWarning (BooleanToVisibilityConverter needs a bool).
        public bool HasNormalizationWarning => !string.IsNullOrEmpty(_normalizationWarning);
        private double _lastCanvasPerimeter;
        private double _lastCanvasArea;
        private bool _hasScaledMeasurements;

        public bool IsScaleCalibrated => Scale != null && Scale.IsCalibrated;
        public string ScaleUnit => Scale?.Unit ?? "";

        private double _perimeterScaledValue;
        public double PerimeterScaledValue
        {
            get => _perimeterScaledValue;
            private set => SetField(ref _perimeterScaledValue, value);
        }

        private double _areaScaledValue;
        public double AreaScaledValue
        {
            get => _areaScaledValue;
            private set => SetField(ref _areaScaledValue, value);
        }

        private void RecomputeScaledValues()
        {
            if (IsScaleCalibrated && _hasScaledMeasurements)
            {
                PerimeterScaledValue = Scale.ToUnits(_lastCanvasPerimeter);
                AreaScaledValue = Scale.ToUnitsArea(_lastCanvasArea);
            }
            else
            {
                PerimeterScaledValue = 0;
                AreaScaledValue = 0;
            }
            OnPropertyChanged(nameof(IsScaleCalibrated));
            OnPropertyChanged(nameof(ScaleUnit));
        }

        // Restores scaled Perimeter/Area on undo/redo (called by
        // OutlineOperation.ApplyMetadataToMode) alongside the ratio metrics.
        public void RestoreScaledMeasurements(double canvasPerimeter, double canvasArea)
        {
            _lastCanvasPerimeter = canvasPerimeter;
            _lastCanvasArea = canvasArea;
            _hasScaledMeasurements = true;
            RecomputeScaledValues();
        }

        // Shared frozen dash pattern (OutlineVisuals) for this mode's dashed polylines.

        // Fired after metadata is stamped onto the committed outline.
        public event Action MetadataGenerated;


        // Called from the panel's Generate Metadata handler and by the EFA window
        // when settings change.
        public void GenerateMetadata()
        {
            // Refuse while a hand-drawn stroke is still open.
            if (HandDraw.IsStrokeOpen)
            {
                HandOutlineUnfinished?.Invoke();
                return;
            }

            if (_activePolyline == null || _activePolyline.Points.Count < 3)
            {
                MetadataSummary = "No outline available.";
                _hasScaledMeasurements = false;
                RecomputeScaledValues();
                UpdateEFDPreview();
                return;
            }

            var pts = new List<Point>(_activePolyline.Points);

            // Remove duplicate closing point if present
            PolylineGeometry.StripClosureDuplicate(pts);

            // Convert from canvas space to image space for scale-invariant metric computation.
            // All GeometryCalculations calls below use image-space coordinates.
            var imagePts = pts.Select(CanvasToImage).ToList();

            double perimeter = GeometryCalculations.Perimeter(imagePts);
            double area = GeometryCalculations.PolygonArea(imagePts);

            // Scaled outputs use canvas-space measurements (matching the Scale Image
            // calibration); imagePts stays image-space so ratios are zoom-independent.
            double canvasPerimeter = GeometryCalculations.Perimeter(pts);
            double canvasArea = GeometryCalculations.PolygonArea(pts);
            _lastCanvasPerimeter = canvasPerimeter;
            _lastCanvasArea = canvasArea;
            _hasScaledMeasurements = true;
            RecomputeScaledValues();

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

            // First metadata pass for a new outline: adopt the 99%-power harmonic
            // count as default. Skipped once resolved; never overrides a manual choice.
            ApplyAutoHarmonicDefault();

            int harmonics = EfdHarmonics;

            // Run EFA on the dense contour (resampled to ContourSampleCount), not the
            // Douglas-Peucker polyline which drops detail the higher harmonics capture.
            List<Point> efaSource;
            if (HasDenseContourForActiveOutline)
            {
                var canvasDense = new List<Point>(_activeDenseContourImage.Count);
                foreach (var ip in _activeDenseContourImage)
                    canvasDense.Add(new Point(ip.X * ScaleX + OffsetX, ip.Y * ScaleY + OffsetY));
                efaSource = GeometryCalculations.ResampleClosed(canvasDense, ContourSampleCount);
            }
            else
            {
                // Live (edited) polyline, canvas space, closure dup stripped, resampled
                // like the dense path.
                efaSource = pts.Count >= 3
                    ? GeometryCalculations.ResampleClosed(pts, ContourSampleCount)
                    : pts;
            }
            EFDCoefficientsResult = _efd.ComputeNormalized(efaSource, harmonics);

            // Presentation strings are built by the formatter — the mode
            // computes numbers, the formatter owns the text 
            NormalizationWarning = OutlineMetadataFormatter.BuildNormalizationWarning(
                _efd.NormalizationStatus, _efd.FirstHarmonicAxisRatio);

            MetadataSummary = OutlineMetadataFormatter.BuildSummary(
                AspectRatioResult,
                PerimeterAreaRatioResult,
                CircularityResult,
                SolidityResult,
                TurningAngleLengthResult,
                harmonics,
                EFDCoefficientsResult,
                _efd.NormalizationStatus,
                _efd.FirstHarmonicAxisRatio);

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
                op.MetadataSummary = MetadataSummary;
                op.NormalizationWarning = NormalizationWarning;
                op.HasMetadata = true;

                // HasMetadata flipped on an op already in history; recompute the counter
                // now since an in-place mutation raises no history-changed event.
                MetadataGenerated?.Invoke();
            }

            UpdateEFDPreview();
        }


        // ---- ELLIPTIC FOURIER DESCRIPTORS ----
        private readonly EllipticFourierAnalysis _efd = new EllipticFourierAnalysis();

        // Session accumulator of EFD coefficient tables for multi-specimen CSV
        // export. NOT cleared in Reset(), so it survives new images / workspace clears.
        public EfdCsvCollector EfdCsv { get; } = new EfdCsvCollector();

        // Default harmonic count, replaced per outline by the 99%-power recommendation
        // (see ApplyAutoHarmonicDefault). A manual edit opts that outline out.
        private int efdHarmonics = 10;

        // True once the 99% default is applied or the user sets the count for the
        // current outline. Reset per new outline (see ResetHarmonicAutoDefault).
        private bool _harmonicsResolvedForOutline;

        // Distinguishes a user keystroke/slider change (which should stick) from
        // the internal auto-default write (which should not count as manual).
        private bool _settingHarmonicsInternally;

        public int EfdHarmonics
        {
            get => efdHarmonics;
            set
            {
                int clamped = Math.Max(1, Math.Min(100, value));

                // Any change that isn't our own auto-default write is a manual
                // choice: lock it in for this outline.
                if (!_settingHarmonicsInternally)
                    _harmonicsResolvedForOutline = true;

                if (SetField(ref efdHarmonics, clamped))
                    UpdateEFDPreview();
            }
        }

        // Called when a new outline is committed: forget the previous outline's
        // resolved count so the next GenerateMetadata re-derives the 99% default.
        private void ResetHarmonicAutoDefault()
        {
            _harmonicsResolvedForOutline = false;
        }

        // Sets EfdHarmonics to the 99%-cumulative-power count unless already resolved
        // for this outline.
        private bool ApplyAutoHarmonicDefault()
        {
            if (_harmonicsResolvedForOutline) return false;

            var profile = AnalyzeHarmonicPower(EllipticFourierAnalysis.DefaultPowerThreshold);
            if (profile == null) return false;

            // SelectedHarmonics is the count reaching 99% power (or the ceiling
            // if 99% isn't reached within the analyzed range). Clamp defensively.
            int suggested = Math.Max(1, Math.Min(100, profile.SelectedHarmonics));

            _harmonicsResolvedForOutline = true; // resolved even if unchanged, so we don't re-run every time
            if (suggested == efdHarmonics) return false;

            _settingHarmonicsInternally = true;
            try { EfdHarmonics = suggested; }
            finally { _settingHarmonicsInternally = false; }
            return true;
        }

        // The blue EFD preview polyline shown in the workspace
        public event Action<Polyline> EFDPreviewReady;   // MainWindow wires this up
        public event Action EFDPreviewClear;             // MainWindow wires this up

        // Reconstructs the outline from EFD coefficients and displays it as a
        // blue overlay. Called whenever EfdHarmonics changes or metadata is generated.
        public void UpdateEFDPreview()
        {
            EFDPreviewClear?.Invoke();

            if (_efd.RawCoefficients == null || _efd.RawCoefficients.Length == 0) return;
            if (_activePolyline == null || _activePolyline.Points.Count < 3) return;

            int harmonics = Math.Min(EfdHarmonics, _efd.RawCoefficients.Length / 4);
            if (harmonics < 1) return;

            // Reconstruct about the arc-length centroid (the contour's DC term), not a
            // vertex average, which Douglas-Peucker's uneven spacing would bias.
            var reconstructed = _efd.ReconstructCanonical(harmonics);
            if (reconstructed == null) return;

            var previewLine = new Polyline
            {
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 2.5,
                StrokeDashArray = OutlineVisuals.PreviewDashes,
                FillRule = FillRule.EvenOdd
            };
            foreach (var p in reconstructed)
                previewLine.Points.Add(p);

            EFDPreviewReady?.Invoke(previewLine);
        }

        // ── Accessors for the Elliptic Fourier Analysis window ──
        // Both return canvas-space points so the pair can be rescaled together.
        public List<Point> GetActiveOutlinePoints()
        {
            if (_activePolyline == null || _activePolyline.Points.Count < 3) return null;
            return new List<Point>(_activePolyline.Points);
        }

        public List<Point> GetEfdReconstructionPoints()
        {
            if (_efd.RawCoefficients == null || _efd.RawCoefficients.Length == 0) return null;
            int harmonics = Math.Min(EfdHarmonics, _efd.RawCoefficients.Length / 4);
            if (harmonics < 1) return null;
            return _efd.ReconstructCanonical(harmonics);
        }

        public void ClearEFDPreview()
        {
            EFDPreviewClear?.Invoke();
            _efd.Clear();
        }

        // Harmonic-power analysis on the current outline; threshold in [0,1] (e.g.
        // 0.99). Null when no usable outline. Does NOT change EfdHarmonics or overlays.
        public HarmonicPowerProfile AnalyzeHarmonicPower(double threshold, bool dropFirstHarmonic = true)
        {
            if (_activePolyline == null || _activePolyline.Points.Count < 3) return null;

            var pts = new List<Point>(_activePolyline.Points);

            // Drop a closing duplicate vertex if present (same prep as GenerateMetadata).
            PolylineGeometry.StripClosureDuplicate(pts);
            if (pts.Count < 3) return null;

            return _efd.AnalyzeHarmonicPower(pts, threshold, dropFirstHarmonic);
        }

        // Applies a chosen harmonic count to the EFD display setting (the setter clamps 1..100
        // and refreshes the blue preview).
        public void ApplyHarmonicCount(int harmonics) => EfdHarmonics = harmonics;

        // ── Tips ── Outline-specific lines; the rest are inherited from WorkMode.
        private const string TipDecimate =
            "💡 To increase speed, try decimating pixel count using the Decimate function in the View menu.";
        private const string TipMultiClick =
            "💡 Use multi-click mode to merge multiple regions. To finalize an outline in multi-click mode, click inside the area bounded by a dashed line.";
        private const string TipSolidBackground =
            "💡 Outline mode performs best on unpatterned images with a solid background.";

        // Automated Outline with the watershed toggle enabled.
        private static readonly string[] DrawWatershedTips =
        {
            "💡 Use Watershed to generate more accurate outlines on complex images, at the cost of reduced speed.",
            TipDecimate,
            TipMultiClick,
            TipSolidBackground,
            TipHelp,
            TipUndo,
            TipRedo,
            TipClear,
            TipOpenImage,
            TipZoom,
            TipPan,
            TipToggleTips
        };

        // Push tool tips
        private static readonly string[] PushTips =
        {
            "💡 Click and drag from inside the outline outward to push the boundary out. Adjust brush size for precision.",
            "💡 On a trackpad, hold 'Ctrl' and move the cursor to push without holding a button down.",
            TipSolidBackground, TipHelp, TipClear, TipOpenImage, TipZoom, TipPan, TipToggleTips
        };

        // Automated Outline, default (portfolio) variant.
        private static readonly string[] DrawFloodTips =
        {
            "💡 Set fill sensitivity to maximum values for images on a solid background.",
            TipMultiClick,
            TipDecimate,
            "💡 Having trouble with the outline? Watershed mode may improve accuracy for complex or textured images.",
            TipSolidBackground,
            TipHelp,
            TipUndo,
            TipRedo,
            TipClear,
            TipOpenImage,
            TipZoom,
            TipPan,
            TipToggleTips
        };

        // Erase tool.
        private static readonly string[] EraseTips =
        {
            "💡 Click and drag over the outline to erase. Adjust brush size for precision.",
            TipSolidBackground,
            TipHelp,
            TipClear,
            TipOpenImage,
            TipZoom,
            TipPan,
            TipToggleTips
        };

        // Smooth tool.
        private static readonly string[] SmoothTips =
        {
            "💡 Adjust smooth strength for cleaner outlines. Too high may distort sharp features.",
            "💡 Global smooths the whole perimeter live from the slider; Local turns the cursor into a sanding brush — click and drag along the outline to smooth just that section.",
            TipSolidBackground,
            TipHelp,
            TipClear,
            TipOpenImage,
            TipZoom,
            TipPan,
            TipToggleTips
        };

        // Generate Metadata tool.
        private static readonly string[] MetadataTips =
        {
            "💡 Adjust the number of EFD Harmonics to control Fourier detail. The EF outline is overlaid in a blue, dashed line.",
            "💡 A perfect circle has a circularity value of 1. Circularity, aka roundness, is calculated as ⁠4π × Area ÷ Perimeter squared⁠.",
            "💡 Solidity is the ratio of the outlined area divided by the area of its convex hull. The convex hull is the smallest convex polygon enclosing the outline.",
            "💡 Turn/Length (sum of turning angles divided by outline perimeter) measures how sharply the curve bends, on average, along its length.",
            TipHelp,
            TipClear,
            TipOpenImage,
            TipZoom,
            TipPan,
            TipToggleTips
        };

        // Draw by Hand tool.
        private static readonly string[] HandDrawTips =
        {
            "💡 Hold the left mouse button and drag to draw an outline by hand.",
            "💡 The outline closes automatically as soon as your line crosses itself. Any leftover tails are removed.",
            "💡 Release to pause; press and drag again to continue the same line.",
            "💡 Once closed, switch to Generate Metadata to measure the shape.",
            TipClear,
            TipZoom,
            TipPan,
            TipToggleTips
        };

        private static readonly string[] NoTips = { string.Empty };

        public override string[] GetTips()
        {
            if (DrawOutlineMode) return UseWatershed ? DrawWatershedTips : DrawFloodTips;
            if (EraseOutlineMode) return EraseTips;
            if (PushOutlineMode) return PushTips;
            if (SmoothOutlineMode) return SmoothTips;
            if (OutlineMetadataMode) return MetadataTips;
            if (HandDrawMode) return HandDrawTips;
            return NoTips;
        }
        #endregion
    }
}