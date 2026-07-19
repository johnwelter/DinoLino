using DinoLino.DataTypes;
using DinoLino.Properties;
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
using ImageSnapshot = DinoLino.Utilities.OutlineProcessor.ImageSnapshot;


namespace DinoLino.Utilities.Modes
{
    public class OutlineMode : WorkMode, IOutlineToolContext
    {
        public override string TabName => "Outline";
        // Cached: recreating the panel on every tab switch discarded panel
        // state and let the EFA window's per-panel single-instance guard leak
        // duplicate windows (each one subscribed to PropertyChanged).
        private OutlineControlPanel _cachedPanel;
        public override UserControl CreateControlPanel() => _cachedPanel ??= new OutlineControlPanel(this);
        public override bool IsStartingNewOperation => true;

        // Erase, smooth, and metadata clicks operate ON the existing outline —
        // they return no new elements for the router to re-add, so the
        // "starting a new operation" workspace clear must not fire for them or
        // a single click wipes the very outline being edited (the erase-mode
        // bug; smooth and metadata clicks had the same latent hazard). Drawing
        // clicks are unaffected: they clear and then re-add the fresh outline.
        // Hand-draw is deliberately NOT included — starting a hand stroke
        // replaces the previous outline via its own clear in MainWindow.Input.
        public override bool IsProbeInteraction =>
            _eraseOutlineMode || _smoothOutlineMode || _outlineMetadataMode;

        #region tools

        // ── Tool objects ──
        // Each tool is self-contained: it owns its parameters (bindable via
        // {Binding Erase.BrushRadius} etc. — the panel's DataContext is this
        // mode) and its drag handlers, and sees the mode only through
        // IOutlineToolContext. Mode-SELECTION flags (which tool is active)
        // stay on the mode below; tool PARAMETERS live on the tools.
        public HandDrawTool HandDraw { get; }
        public EraseTool Erase { get; }
        public SmoothTool Smooth { get; }

        public OutlineMode()
        {
            HandDraw = new HandDrawTool(this);
            Erase = new EraseTool(this);
            Smooth = new SmoothTool(this);

            // The old hidden coupling — erase called RefreshSmoothSnapshot()
            // directly so a later Global smooth builds on the erased shape —
            // is now this one explicit wire.
            Erase.OutlineEdited += Smooth.RefreshSnapshot;

            // BUG FIX (EFA ran on the original outline after an edit): EFA
            // prefers the cached dense contour (_activeDenseContourImage),
            // which is captured ONCE during automatic detection and never
            // updated by erase/smooth. Any tool that mutates the outline now
            // raises OutlineEdited, and the mode invalidates the cache — so
            // GenerateMetadata falls through to the live (edited) polyline.
            // This is the general contract: editing tools raise OutlineEdited;
            // the mode invalidates. A new editing tool wired the same way is
            // covered automatically.
            Erase.OutlineEdited += InvalidateDenseContour;
            Smooth.OutlineEdited += InvalidateDenseContour;
        }

        // ── IOutlineToolContext (explicit: the tools' window into the mode) ──
        Polyline IOutlineToolContext.ActivePolyline => _activePolyline;
        ViewTransform IOutlineToolContext.Transform => _transform;
        double IOutlineToolContext.SimplifyEpsilon => _simplifyEpsilon;
        Brush IOutlineToolContext.LineColor => LineColor;
        bool IOutlineToolContext.HasImage => _cachedPixels != null;
        bool IOutlineToolContext.IsHandDrawActive => _handDrawMode;

        void IOutlineToolContext.OnHandStrokeStarted()
        {
            // First press of a brand-new hand stroke: start an undo operation
            // and clear any prior result (moved from the old BeginHandStroke).
            BeginOperation();
            ClearMetadata();
            ClearEFDPreview();
        }

        void IOutlineToolContext.CommitOutline(Polyline outline) => CommitFinalOutline(outline);

        #endregion

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
                if (!_handDrawMode) HandDraw.Cancel();
            }
        }

        // Fired when metadata is requested but the hand-drawn stroke isn't
        // closed yet. WPF FIX: this was a public Action FIELD, so any
        // subscriber could overwrite or null the whole invocation list with
        // '='. As an event only += / -= are possible.
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

        // WPF FIX: the setter used to launch GenerateMetadata() /
        // ClearEFDPreview() as side effects, so a two-way checkbox binding
        // triggered heavyweight work whose ordering depended on binding
        // timing. The setter is now cheap and idempotent; the control panel
        // invokes GenerateMetadata / ClearEFDPreview from its Checked /
        // Unchecked handlers instead (see OutlineControlPanel.xaml.cs).
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

        #region get image data
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

        // ── Per-image analysis, built ONCE per image on a worker thread ──
        // All arrays are written during the build and only read afterwards, so
        // they are safe to share read-only across concurrent click operations.
        private sealed class ImageAnalysis
        {
            public byte[] Pixels;
            public int Width, Height, Stride, Bpp;
            public bool[] BackgroundMask;
            public int[] Gradient;             // squared weighted-Sobel magnitude
            public int[] DistToBackground;     // BFS distance to border/background
            public int GradientEdgeThreshold;  // "counts as an edge" cutoff for mask scoring
            public float[] TextureMap;         // per-pixel local luma std-dev, PerceptualDistance scale (adaptive flood)
            public byte[] FlattenedPixels;     // illumination-normalized copy (poor-man's retinex), same stride/bpp
            public bool[] FlattenedBackgroundMask; // background model REBUILT on the flattened copy
            public SamImageState SamState;         // neural encoder output; null when no model is installed or encoding failed
        }

        private Task<ImageAnalysis> _analysisTask;
        private int _imageVersion;

        // BUG FIX (wasted analysis): BuildImageAnalysis had no token, so
        // loading image B while image A was still analyzing let A's full
        // pipeline — palette, Sobel, distance transform, texture, retinex,
        // and the 1–3 s SAM encode — run to completion for nothing. This CTS
        // supersedes the analysis exactly like _opCts supersedes clicks.
        private CancellationTokenSource _analysisCts;

        private void CacheSourcePixels()
        {
            _imageVersion++;
            _opCts?.Cancel();   // supersede any operation still running against the old image

            var oldAnalysis = _analysisCts;
            oldAnalysis?.Cancel();
            oldAnalysis?.Dispose();  // tokens stay readable after disposal
            _analysisCts = null;

            // BUG FIX (stale pending outline): a pending multi-click outline
            // belongs to the image it was floods from. The old code only
            // dropped it when the pixel-buffer LENGTH changed, so loading a
            // new image with identical dimensions kept a stale pending mask
            // alive. Any image change now clears the pending state outright
            // (and ProcessClick double-checks via _pendingImageVersion).
            ClearPendingState();

            if (_sourceImage == null)
            {
                _cachedPixels = null;
                _analysisTask = null;
                return;
            }

            // Synchronous part: just the pixel copy (fast). The expensive analysis
            // — background mask, Sobel gradient, distance transform — runs on a
            // worker so loading a large photo no longer freezes the UI; the first
            // click simply awaits the task inside its own background work.
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
            // The token is ALSO passed to Task.Run so a pre-cancelled start
            // yields a Canceled (not Faulted) task — awaiting click operations
            // already treat OperationCanceledException as "superseded".
            _analysisTask = Task.Run(() => BuildImageAnalysis(pixels, w, h, stride, bpp, analysisToken), analysisToken);
        }

        private ImageAnalysis BuildImageAnalysis(byte[] pixels, int w, int h, int stride, int bpp, CancellationToken token)
        {
            var proc = new OutlineProcessor();

            // Neural encoder (item 8): when SAM-class models are installed
            // (Models\*.onnx beside the executable — see SamSegmenter), run
            // the image encoder ONCE per image, in PARALLEL with the classical
            // caches below. The 1–3 s CPU cost hides behind this same async
            // load, and each click then pays only a tens-of-milliseconds
            // decoder pass. Null when absent or failed — everything downstream
            // silently falls back to the classical pipeline.
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
            // up to ~6 clustered border colors matched by nearest entry, with
            // per-corner hard-seed trust flags and an adaptive threshold from
            // robust patch residuals.
            var (bgPalette, hardSeedCorners, adaptiveThreshold) =
                proc.EstimateBackgroundPalette(pixels, w, h, stride, bpp);
            bool[] bgMask = proc.BuildBackgroundMaskProgressive(
                pixels, w, h, stride, bpp, bgPalette, hardSeedCorners,
                tightThreshold: adaptiveThreshold * 0.6,
                relaxedThreshold: adaptiveThreshold);
            token.ThrowIfCancellationRequested();

            var snap = new ImageSnapshot(pixels, bgMask, w, h, stride, bpp);
            int[] gradient = proc.ComputeGradient(snap);

            // "Counts as an edge" cutoff for mask scoring: 90th percentile of the
            // image's own gradient distribution, floored at roughly a 9-level
            // luminance step so a perfectly flat background can't drive it to
            // zero and make every rim look edge-aligned.
            int edgeThreshold = Math.Max(
                OutlineProcessor.GradientPercentileThreshold(gradient, 0.90), 120_000);

            // Per-pixel local texture (std-dev of luma over a 7×7 window),
            // ~4 bytes/pixel to keep. Lets the adaptive flood treat "uniformly
            // textured" as a match criterion and self-tune its tolerance from
            // the seed's own texture.
            float[] textureMap = proc.ComputeTextureMap(snap);
            token.ThrowIfCancellationRequested();

            int[] distToBackground = proc.ComputeDistanceToBackground(w, h, bgMask);
            token.ThrowIfCancellationRequested();

            // Illumination-flattened copy (poor-man's retinex, radius
            // ~min(w,h)/8) plus a background model REBUILT on it — that rebuild
            // is the point: a vignetted or side-lit background that defeated
            // the original model becomes uniform here, so both the flattened
            // candidate's flood and its bg mask see a clean scene. Costs one
            // extra pixel buffer + mask (~5 bytes/pixel).
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

        // Background palette estimation moved to OutlineProcessor
        // (EstimateBackgroundPalette): it is pure pixel statistics with no
        // mode state, so it lives beside the background-mask builders and
        // shares the single PerceptualDistance formula.

        // Canvas → image mapping for the click math below. Delegates to the
        // single Transform value (see the Transform property in the draw
        // region) so there is exactly one definition of the mapping.
        private Point CanvasToImage(Point p) => _transform.CanvasToImage(p);
        #endregion

        #region shared functions and variables

        // When true, FORCES watershed for every click (legacy behavior, kept so an
        // existing XAML binding doesn't break). When false — the recommended
        // default — each click runs flood fill first, scores the result against
        // the image gradient, and silently retries with watershed when the flood
        // looks poor, keeping whichever mask scores better (see SegmentAtClick).
        // That makes this toggle, and its checkbox, safe to delete from the UI.
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
            // WPF FIX: the scaled results were sentinel STRINGS ("Error:
            // Unscaled" via ScaledPlaceholder). The mode now exposes numbers
            // plus IsScaleCalibrated and the view owns the wording — see the
            // metadata region.
            _hasScaledMeasurements = false;
            RecomputeScaledValues();
        }

        public override void RefreshScalePlaceholders()
        {
            // Calibration changed (set, updated, or cleared): re-derive the
            // scaled numbers from the stored canvas measurements and re-raise
            // IsScaleCalibrated / ScaleUnit for the bindings.
            RecomputeScaledValues();
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

        // BUG FIX (stale pending): the image version this pending state was
        // computed against. -1 = none. See SwapPending / ProcessClick.
        private int _pendingImageVersion = -1;

        // Accumulated click coordinates for the CURRENT pending outline — fed
        // to the neural candidate as positive point prompts, so a second
        // multi-click doesn't just merge two masks, it RE-PROMPTS the model
        // with both points and lets it produce one coherent object. Mutated
        // only on the UI thread; snapshot with ToArray() before capturing
        // into a background task.
        private readonly List<(int x, int y)> _pendingClickPoints = new List<(int x, int y)>();

        // (newPending, oldPending) — MainWindow swaps them on the canvas
        public event Action<Polyline, Polyline> PendingOutlineReady;

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

        // 0 = Off, 1 = Low, 2 = High. Only honored on the legacy forced-watershed
        // path (UseWatershed == true): in auto mode the candidate portfolio tries
        // blur levels 0 and 1 itself, so this knob — like UseWatershed — can be
        // deleted from the UI.
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

        // ── View transform (zoom + pan) ──
        // WPF FIX: ScaleX/ScaleY/OffsetX/OffsetY were four independent settable
        // auto-properties with no change notification and no consistency
        // guarantee — a reader could observe a new scale paired with an old
        // offset mid-update. They are now views over ONE immutable
        // ViewTransform value with a single PropertyChanged. The four scalar
        // properties remain so existing MainWindow call sites compile
        // unchanged; prefer assigning Transform once per zoom/pan.
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

        // Superseding cancellation for click operations: a new click cancels the
        // previous computation instead of racing it. (Operations also no longer
        // share OutlineProcessor scratch buffers — see RunClickOperation.)
        private CancellationTokenSource _opCts;

        // Busy indicator: raised when a background click operation starts and
        // lowered when it settles (success, cancel, early return, or error).
        // A COUNTER rather than a bool so overlapping clicks behave correctly —
        // a superseded op and its replacement can both be in flight briefly,
        // and the indicator must stay up until the LAST one finishes. Fired on
        // whatever thread the transition happens on; MainWindow marshals to the
        // UI thread. int + Interlocked keeps the raise/lower balanced without a
        // lock.
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

        // A candidate mask whose ScoreMask result reaches this is accepted
        // outright and the portfolio stops; below it, the next (more expensive)
        // candidate runs and the highest score wins. Tunable.
        private const double AcceptableMaskScore = 0.5;

        // Masks scoring at or above this already hug the image edges well;
        // GmmRefine rarely beats them and costs an ROI-sized model fit plus a
        // per-pixel reclassification, so refinement is skipped. Tunable.
        private const double RefinementSkipScore = 0.72;

        // One-line diagnostic of the last click's candidate arbitration, e.g.
        // "[outline] click(412,300) neural=0.81 winner=neural". Written by
        // SegmentAtClick on the worker thread (WPF marshals PropertyChanged
        // for scalar bindings); bind a TextBlock to it for a live readout, or
        // watch the same line in the debugger's Output window.
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
            ClearPendingState();
            HandDraw.Reset();       // discards any open stroke AND the committed flag
            Smooth.ClearSnapshot(); // the outline is gone; never smooth from a ghost
            ClearMetadata();
            ClearEFDPreview();
        }

        // Esc (and any other CancelCurrentOperation caller) also discards an
        // open hand-drawn stroke, as HandDrawTool.Cancel's docs promise — the
        // wiring was missing. Safe against every ProcessClick's
        // BeginOperation: a stroke can only be open while hand-draw is the
        // active tool, whose presses bypass ProcessClick, and
        // OnHandStrokeStarted runs BEFORE the tool marks the stroke active.
        public override void CancelCurrentOperation()
        {
            base.CancelCurrentOperation();
            HandDraw?.Cancel();
        }

        // Drops all multi-click pending state. Called when the image changes
        // (CacheSourcePixels), on Reset, after ConfirmPending, and from the
        // ProcessClick version guard. Deliberately does NOT touch the canvas:
        // the image-load / reset paths in MainWindow clear the workspace
        // elements themselves.
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
        // StartNewOutline and ExpandPending used to duplicate ~40 lines of
        // orchestration each: supersede-CTS, busy raise/lower, Task.Run,
        // dispatcher marshal, error dialog. The scaffolding now lives here once
        // and the two methods contain only their actual difference.
        //
        // BUG FIX (busy-counter leak): the old code called EnterBusy() and then
        // Task.Run(async ..., token). If that token was already canceled when
        // the task was scheduled, the delegate NEVER executed, so the
        // finally { ExitBusy(); } inside it never ran and the busy indicator
        // stuck on forever. Cancellation is cooperative inside the body anyway
        // (ThrowIfCancellationRequested), so the token is deliberately NOT
        // passed to Task.Run — the delegate always runs and busy stays
        // balanced.
        private void RunClickOperation(Func<ImageAnalysis, OutlineProcessor, CancellationToken, Task> body)
        {
            var analysisTask = _analysisTask;
            if (analysisTask == null) return;

            // Newest click wins: supersede any in-flight computation instead of
            // racing it. Combined with per-operation processors, this removes
            // the buffer data race between overlapping clicks and stops
            // superseded work from burning CPU to completion.
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
        // WPF FIX: the old success path used Application.Current.Dispatcher
        // with no null check (NRE if the app is tearing down) while the error
        // path checked; both now share this null-safe access.
        private void PostResultToUi(int version, CancellationToken token, Action apply)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return; // application shutting down
            // BeginInvoke, not Invoke: the worker never needs to wait for the
            // apply, and an async post can never deadlock against a blocked
            // UI thread. The stale-guard runs ON the UI thread either way.
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (token.IsCancellationRequested || version != _imageVersion) return;
                apply();
            }));
        }

        // WPF FIX: a mode object should not summon MessageBoxes — that is a
        // view decision. The mode RAISES this event (on the UI thread) and the
        // view presents it; MainWindow subscribes and shows the dialog (see
        // the migration notes). The Debug line keeps failures visible even if
        // nothing is subscribed.
        public event Action<string> OperationFailed;

        private void RaiseOperationFailed(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[outline] {message}");
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) OperationFailed?.Invoke(message);
            else dispatcher.BeginInvoke(new Action(() => OperationFailed?.Invoke(message)));
        }

        // =====================
        // PROCESS CLICK
        // =====================
        public event Action<List<UIElement>> OutlineReady;

        // Runs the full cleanup pipeline on a full-image-space mask.
        // Returns the cleaned full-image-space mask, or null if it fails.
        // Runs on a background thread; 'proc' is that operation's own processor.
        private bool[] CleanMaskFullSpace(bool[] rawFull, ImageSnapshot snap,
            int seedPxX, int seedPxY, OutlineProcessor proc, CancellationToken token,
            bool preserveMultipleComponents = false)
        {
            int sw = snap.Width, sh = snap.Height;

            // Strip the 5-px border band ONLY when the mask occupies enough of it
            // to look like background bleed. A subject that genuinely touches the
            // frame in a small arc keeps its edge pixels instead of losing a slice
            // (or being cut in two, after which "largest component" could keep the
            // wrong half). The crack tracer stays in-bounds either way.
            const int band = 5;
            long bandFg = 0, bandTotal = 0;
            for (int i = 0; i < rawFull.Length; i++)
            {
                int rx = i % sw, ry = i / sw;
                if (rx >= band && rx < sw - band && ry >= band && ry < sh - band) continue;
                bandTotal++;
                if (rawFull[i]) bandFg++;
            }
            if (bandTotal > 0 && bandFg > bandTotal * 0.2)
            {
                for (int i = 0; i < rawFull.Length; i++)
                {
                    if (!rawFull[i]) continue;
                    int rx = i % sw, ry = i / sw;
                    if (rx < band || rx >= sw - band || ry < band || ry >= sh - band) rawFull[i] = false;
                }
            }

            token.ThrowIfCancellationRequested();

            var (bx0, by0, bx1, by1) = proc.GetMaskBounds(rawFull, sw, sh, margin: 4);
            var (cropped, cw, ch) = proc.CropMask(rawFull, sw, bx0, by0, bx1, by1);

            // Seed in crop space, clamped: with the conditional strip the seed can
            // in principle sit just outside the surviving bounds.
            int cpx = Math.Max(0, Math.Min(cw - 1, seedPxX - bx0));
            int cpy = Math.Max(0, Math.Min(ch - 1, seedPxY - by0));

            bool[] traced;
            if (preserveMultipleComponents)
            {
                // Clean every component independently (each in its own crop, with
                // per-component scaled radii) and keep all that survive.
                // No single-component collapse, no trace-seed dependence.
                bool[] work = proc.CleanEachComponent(cropped, cw, ch, MinAreaPixels, token);
                traced = proc.HasMinimumPixels(work, MinAreaPixels) ? work : cropped;
            }
            else
            {
                // Scale the cleanup morphology to the object instead of fixed radii:
                // a fixed radius-2 opening plus corridor-3 thinning deterministically
                // deletes any feature under ~5 px wide — exactly an antenna or spine
                // on a small specimen. Small blobs still get cleaned aggressively.
                int maskArea = proc.CountPixels(cropped);
                double objScale = Math.Sqrt(Math.Max(1, maskArea));
                // Floor restored to the ORIGINAL radius 2 (see the matching
                // note in CleanEachComponent); tiny objects keep radius 1.
                int openRadius = maskArea < 2500
                    ? 1
                    : (int)Math.Min(3, Math.Max(2, objScale / 64.0));
                int corridorWidth = Math.Min(3, openRadius + 1);

                bool[] work = proc.CleanComponentMorphology(cropped, cw, ch,
                    openRadius, closeRadius: 1, minCorridorWidth: corridorWidth);

                token.ThrowIfCancellationRequested();

                // Honor the click: if the component the user pointed at survived
                // cleanup, keep THAT one. Fall back to "largest" only when it
                // didn't — MorphOpen can split a mask, and the biggest fragment
                // is not necessarily the one under the cursor.
                int cseed = cpy * cw + cpx;
                work = work[cseed]
                    ? proc.KeepComponentContainingSeed(work, cw, ch, cseed)
                    : proc.KeepLargestComponent(work, cw, ch);

                traced = proc.PrepareMaskForTracing(work, cropped, cw, ch, cpx, cpy, MinAreaPixels);
            }

            // Paste cropped result back into full-image space
            bool[] full = new bool[sw * sh];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    if (traced[y * cw + x]) full[(by0 + y) * sw + (bx0 + x)] = true;
            return full;
        }

        private Polyline BuildPolylineFromFullMask(bool[] full, ImageSnapshot snap, bool dashed, OutlineProcessor proc,
            int[] gradient, int edgeGradThreshold)
        {
            var (bx0, by0, bx1, by1) = proc.GetMaskBounds(full, snap.Width, snap.Height, margin: 2);
            var (cropped, cw, ch) = proc.CropMask(full, snap.Width, bx0, by0, bx1, by1);

            var boundary = proc.TraceBoundary(cropped, cw, ch);
            if (boundary.Count < 8) return null;

            // Boundary snap post-pass (conservative — see SnapBoundaryToGradient).
            // Keep the unsnapped trace: if simplification of the snapped
            // contour self-intersects, retry from the raw trace — which is
            // simple by construction — effectively disabling the snap for that
            // outline. The old fallback dumped the ENTIRE dense contour into
            // the display polyline, which is exactly the "every pixel of
            // jitter becomes a vertex" jaggedness this replaces.
            // NOTE: in multi-click mode the stored pending MASK is not
            // re-rasterized from the snapped contour — the ≤3 px divergence is
            // immaterial for its two uses (click-inside test, merge base).
            var rawBoundary = boundary;
            boundary = proc.SnapBoundaryToGradient(boundary, bx0, by0,
                gradient, snap.Width, snap.Height, edgeGradThreshold);

            var simplified = GeometryCalculations.DouglasPeucker(boundary, _simplifyEpsilon);
            bool simAfterDP = simplified.Count >= 3 && !PolylineGeometry.HasSelfIntersection(simplified);
            if (!simAfterDP)
            {
                boundary = rawBoundary;
                simplified = GeometryCalculations.DouglasPeucker(boundary, _simplifyEpsilon);
                if (simplified.Count < 3) return null;
                if (PolylineGeometry.HasSelfIntersection(simplified))
                    simplified = new List<Point>(boundary);
            }

            // Keep the DENSE boundary (pre-simplification) in full-image space for EFA.
            _activeDenseContourImage = new List<Point>(boundary.Count);
            foreach (var p in boundary)
                _activeDenseContourImage.Add(new Point(p.X + bx0, p.Y + by0));

            // The destructive per-point border clamp that used to live here was
            // REMOVED. The crack tracer only ever emits vertices at grid corners
            // inside [0..w] x [0..h], so every traced point is in-bounds by
            // construction — including now that CleanMaskFullSpace strips the
            // border band only when it detects bleed, so masks CAN legitimately
            // touch the edge. Clamping each point onto a box could collapse a
            // near-edge concavity onto the box line and self-intersect the
            // polygon — that was the source of the corrupted metadata.

            var poly = new Polyline
            {
                // LineColor for BOTH states: the pending preview hardcoded
                // OrangeRed and stopped matching once the user changed the
                // line color — the dashes alone mark it as pending.
                Stroke = this.LineColor,
                StrokeThickness = 2,
                FillRule = FillRule.EvenOdd
            };
            if (dashed) poly.StrokeDashArray = OutlineVisuals.PreviewDashes;

            // (p.X + bx0, p.Y + by0) maps cropped coords back to full-image coords — the
            // clamp loop used to apply this offset, so it must stay now that it's gone.
            foreach (var p in simplified)
                poly.Points.Add(new Point((p.X + bx0) * ScaleX + OffsetX, (p.Y + by0) * ScaleY + OffsetY));
            poly.Points.Add(new Point((simplified[0].X + bx0) * ScaleX + OffsetX, (simplified[0].Y + by0) * ScaleY + OffsetY));

            // Confirmation: with the simple tracer, the DP fallback, and no clamp, the
            // finished polyline should always be simple. Log only if it somehow isn't.
            if (PolylineGeometry.HasSelfIntersection(poly.Points))
            {
                bool simRaw = !PolylineGeometry.HasSelfIntersection(boundary);
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
            if (_cachedPixels == null || _analysisTask == null) return new List<UIElement>();

            // Math.Floor, not a bare cast: truncation toward zero mapped a
            // click up to one canvas pixel LEFT/ABOVE the image onto column/
            // row 0 instead of failing the bounds check below.
            int px = (int)Math.Floor((mousePos.X - OffsetX) / ScaleX);
            int py = (int)Math.Floor((mousePos.Y - OffsetY) / ScaleY);
            if ((uint)px >= _cachedWidth || (uint)py >= _cachedHeight)
                return new List<UIElement>();

            // BUG FIX (stale pending): pending state is stamped with the image
            // version it was computed against (see SwapPending) and dropped on
            // any mismatch. The old length-only test missed a NEW image with
            // IDENTICAL dimensions, leaving a stale mask live. CacheSourcePixels
            // also clears pending outright on image change, so this guard is
            // belt-and-braces.
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

        // Segments the object at the click.
        //  * Rescues clicks landing on background-classified pixels by dropping
        //    the (locally wrong) background mask for this operation.
        //  * Snaps the flood seed to the local distance-transform peak, like
        //    watershed already did, so a click on a highlight or a few pixels
        //    from the boundary still samples a representative interior color.
        //  * Runs a cheapest-first CANDIDATE PORTFOLIO, stopping at the first
        //    acceptable mask:
        //      0. neural (SAM) mask — when Models\*.onnx are installed, the
        //                             accumulated click(s) prompt a MobileSAM-
        //                             class decoder against the per-image
        //                             embedding computed at load; skipped
        //                             entirely when no model is present;
        //      1. classic flood     — identical to the original pipeline, so
        //                             images that already worked don't change;
        //      2. adaptive flood    — chroma-weighted seed distance + texture
        //                             channel, for shaded and textured objects;
        //      3. classic flood on the illumination-flattened copy — cancels
        //                             vignetting/lighting falloff, with a
        //                             background model rebuilt on that copy;
        //      4. watershed, no blur (cached gradient);
        //      5. watershed, blur 1  — the portfolio absorbs WatershedBlurLevel;
        //      6. half-resolution classic flood, upsampled — downsampling
        //                             averages out the noise/texture that
        //                             fragments the full-resolution flood.
        //    If nothing is acceptable, the best-scoring candidate wins; if even
        //    that failed the size veto, the classic flood is returned so this
        //    can never do worse than the original pipeline.
        //  * SIZE VETO: DistToBackground at the seed peak is the radius of a
        //    disc around the peak containing no background at all, so a correct
        //    mask must contain at least (roughly) that disc — any candidate
        //    smaller than HALF its area has under-segmented no matter how well
        //    its rim scores. This closes ScoreMask's blind spot on textured
        //    objects, where a fragment's internal texture edges look like a
        //    perfectly edge-aligned rim.
        //  * REFINEMENT: unless the winner already hugs edges well
        //    (>= RefinementSkipScore), it is passed through GmmRefine —
        //    GrabCut-style foreground/background mixture models — and the
        //    refined mask replaces it only when it scores strictly better.
        private (bool[] mask, int seedX, int seedY, ImageSnapshot snap) SegmentAtClick(
            int px, int py, ImageAnalysis a, OutlineProcessor proc, CancellationToken token,
            (int x, int y)[] samPrompts = null)
        {
            int clickIdx = py * a.Width + px;
            bool clickOnBg = a.BackgroundMask != null && a.BackgroundMask[clickIdx];

            // Rescue path: the click landed on a pixel the background model
            // claimed, so the model is evidently wrong here. Run this operation
            // without it rather than letting every flood/watershed step silently
            // refuse to grow anywhere near the click.
            bool[] opBgMask = clickOnBg ? null : a.BackgroundMask;
            var snap = new ImageSnapshot(a.Pixels, opBgMask, a.Width, a.Height, a.Stride, a.Bpp);
            int[] opDist = clickOnBg ? null : a.DistToBackground; // null → watershed recomputes vs. border only

            int sx = px, sy = py;
            if (!clickOnBg)
                (sx, sy) = proc.FindDistanceTransformPeak(px, py, a.Width, a.Height,
                    a.DistToBackground, a.BackgroundMask, searchRadius: 8);

            var (sr, sg, sb) = proc.SampleSeedColor(sx, sy, snap, radius: 2);

            if (_useWatershed)
            {
                // Legacy "force watershed" path (see UseWatershed remarks).
                bool[] forced = proc.WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                    seedRadius: 3, blurLevel: _watershedBlurLevel);
                if (forced != null) return (forced, sx, sy, snap);
                return (proc.FloodFill(sx, sy, sr, sg, sb, snap,
                    _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold, token), sx, sy, snap);
            }

            // Size veto threshold. Cap the peak distance so a sparse or failed
            // background mask (large distances everywhere) can only WEAKEN the
            // veto toward legacy behavior — never reject correct masks of
            // ordinary size.
            int peakDist = clickOnBg ? 0 : a.DistToBackground[sy * a.Width + sx];
            peakDist = Math.Min(peakDist, Math.Min(a.Width, a.Height) / 8);
            int minPlausibleArea = Math.Max(MinAreaPixels,
                (int)(0.5 * Math.PI * (double)peakDist * peakDist));

            bool[] best = null;
            double bestScore = -1.0;
            string bestName = "none";
            var diag = new System.Text.StringBuilder();

            double Score(bool[] m)
            {
                if (m == null) return 0;
                if (!proc.HasMinimumPixels(m, minPlausibleArea)) return 0; // size veto
                return proc.ScoreMask(m, a.Width, a.Height, a.Gradient, a.GradientEdgeThreshold);
            }

            bool Consider(string name, bool[] m)
            {
                double s = Score(m);
                diag.Append(name).Append('=').Append(s.ToString("F2")).Append(' ');
                if (s > bestScore) { bestScore = s; best = m; bestName = name; }
                return s >= AcceptableMaskScore;
            }

            // Item 7: GrabCut-style GMM refinement. A mixture fitted to the
            // chosen mask covers what no single seed color can — a textured
            // object's several color modes, or the lit and shadowed halves of
            // one surface — so it recovers regions the coarse candidates missed
            // and sheds background they grabbed. The refined mask must WIN on
            // score (size veto included) to replace the coarse one, so this
            // stage can only improve the result.
            (bool[] mask, int seedX, int seedY, ImageSnapshot snap) Finish(bool[] chosen)
            {
                bool refinedWon = false;
                if (bestScore < RefinementSkipScore)
                {
                    bool[] refined = proc.GmmRefine(chosen, snap, sx, sy, token);
                    if (!ReferenceEquals(refined, chosen))
                    {
                        double rs = Score(refined);
                        diag.Append("gmm=").Append(rs.ToString("F2")).Append(' ');
                        if (rs > Math.Max(bestScore, 0.0)) { chosen = refined; refinedWon = true; }
                    }
                }

                // One line per click answering "which engine produced this
                // outline, and why": every candidate's score plus the winner.
                // Visible in the debugger's Output window; also exposed via
                // LastSegmentationInfo for an optional UI binding.
                string info = $"[outline] click({px},{py}) {diag}winner={bestName}{(refinedWon ? "+gmm" : "")}";
                System.Diagnostics.Debug.WriteLine(info);
                LastSegmentationInfo = info;

                return (chosen, sx, sy, snap);
            }

            // 0) Neural candidate: click-prompted SAM (MobileSAM-class) when a
            //    model is installed. Tried FIRST because the encoder already
            //    ran at image load and a decoder pass costs tens of
            //    milliseconds — and it handles texture, clutter, and variable
            //    lighting at a level the color heuristics below can't reach.
            //    Deliberately NOT clamped by the background mask: the neural
            //    mask's whole value is its independence from the color
            //    background model, and the shared score, size veto, and
            //    downstream cleanup arbitrate it like every other candidate.
            //    The classical portfolio below remains the complete fallback.
            if (a.SamState != null)
            {
                try
                {
                    var prompts = new List<(int x, int y, bool positive)>();
                    if (samPrompts != null)
                        foreach (var (qx, qy) in samPrompts) prompts.Add((qx, qy, true));
                    if (prompts.Count == 0) prompts.Add((px, py, true));

                    bool[] neural = SamSegmenter.Shared?.Segment(
                        a.SamState, prompts, a.Width, a.Height, token);
                    if (neural != null && Consider("neural", neural)) return Finish(best);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[sam] decode failed: {ex.Message}");
                    diag.Append("neural=err ");
                }
                token.ThrowIfCancellationRequested();
            }
            else
            {
                diag.Append("neural=off ");
            }

            // 1) Classic flood — unchanged legacy behavior.
            bool[] classicFlood = proc.FloodFill(sx, sy, sr, sg, sb, snap,
                _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold, token);
            if (Consider("flood", classicFlood)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 2) Chroma + texture aware flood.
            float seedTexture = proc.SampleSeedTexture(sx, sy, snap, a.TextureMap, radius: 2);
            bool[] adaptive = proc.FloodFillAdaptive(sx, sy, sr, sg, sb, seedTexture, snap,
                a.TextureMap, _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold, token);
            if (Consider("adaptive", adaptive)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 3) Classic flood on the illumination-flattened copy, with the
            //    background model rebuilt on that copy. Cancels vignetting and
            //    lighting falloff that the chroma metric alone can't fix when
            //    the BACKGROUND (not the object) carries the gradient. Scored
            //    against the ORIGINAL image's edges like every other candidate,
            //    and the winning mask feeds the normal downstream pipeline —
            //    only the segmentation itself looks at flattened pixels.
            bool clickOnFlatBg = a.FlattenedBackgroundMask != null && a.FlattenedBackgroundMask[clickIdx];
            var flatSnap = new ImageSnapshot(a.FlattenedPixels,
                clickOnFlatBg ? null : a.FlattenedBackgroundMask,
                a.Width, a.Height, a.Stride, a.Bpp);
            var (flr, flg, flb) = proc.SampleSeedColor(sx, sy, flatSnap, radius: 2);
            bool[] flatFlood = proc.FloodFill(sx, sy, flr, flg, flb, flatSnap,
                _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold, token);
            if (Consider("flat", flatFlood)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 4) Watershed, no blur (cached gradient).
            bool[] shed0 = proc.WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                seedRadius: 3, blurLevel: 0);
            if (Consider("shed0", shed0)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 5) Watershed, blur level 1 (fresh gradient from the blurred copy).
            bool[] shed1 = proc.WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                seedRadius: 3, blurLevel: 1);
            if (Consider("shed1", shed1)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 6) Half-resolution classic flood, upsampled.
            var half = proc.DownsampleHalf(snap);
            if (half.Width >= 8 && half.Height >= 8 && half.Width < a.Width)
            {
                int hx = Math.Min(half.Width - 1, sx / 2);
                int hy = Math.Min(half.Height - 1, sy / 2);
                if (half.BgMask == null || !half.BgMask[hy * half.Width + hx])
                {
                    var (hr, hg, hb) = proc.SampleSeedColor(hx, hy, half, radius: 2);
                    bool[] halfMask = proc.FloodFill(hx, hy, hr, hg, hb, half,
                        _fillSensitivity, _fillSensitivity * 0.5, _edgeThreshold, token);
                    Consider("halfres", proc.UpsampleMask2x(halfMask, half.Width, half.Height, a.Width, a.Height));
                }
            }

            // Nothing acceptable: best-scoring candidate wins; if even that was
            // vetoed away, fall back to the classic flood so this never does
            // worse than the original pipeline.
            if (best == null || bestScore <= 0) best = classicFlood;
            return Finish(best);
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
                var (raw, usedX, usedY, snap) = SegmentAtClick(px, py, a, proc, token, samPrompts);
                if (raw == null) return Task.CompletedTask;

                bool[] cleaned = CleanMaskFullSpace(raw, snap, usedX, usedY, proc, token);
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
                var (newFlood, usedX, usedY, snap) = SegmentAtClick(px, py, a, proc, token, samPrompts);
                if (newFlood == null) return Task.CompletedTask;

                // Union the new seed's region with the accumulated outline
                for (int i = 0; i < accumulated.Length; i++)
                    if (newFlood[i]) accumulated[i] = true;

                const int bridgeRadius = 6; // tune to the largest gap you want to span
                // Cropped close: the old full-frame MorphClose ran two
                // full-image distance transforms per added click.
                accumulated = proc.MorphCloseCropped(accumulated, a.Width, a.Height, bridgeRadius);

                // Re-clean the union. Use the new click as the trace seed so
                // PrepareMaskForTracing keeps the component the user just added.
                bool[] cleaned = CleanMaskFullSpace(accumulated, snap, usedX, usedY, proc, token,
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
            _pendingImageVersion = _imageVersion; // BUG FIX: stamp the owning image
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

        #region MainWindow compatibility forwarders

        // The erase / smooth / hand-draw implementations moved to the tool
        // objects (Erase, Smooth, HandDraw above). These thin forwarders keep
        // every existing MainWindow.Input call site compiling unchanged; new
        // code should talk to the tools directly, and once MainWindow is
        // updated these can simply be deleted.
        public void BeginHandStroke(Vector2 canvasPos) => HandDraw.BeginStroke(canvasPos);
        public void ProcessHandDrawDrag(Vector2 canvasPos) => HandDraw.ProcessDrag(canvasPos);
        public void EndHandStroke() => HandDraw.EndStroke();
        public void CancelHandDraw() => HandDraw.Cancel();
        public bool HasFinishedHandOutline => HandDraw.HasCommittedOutline;

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

        public void ProcessEraseDrag(Vector2 mousePos) => Erase.ProcessDrag(mousePos);
        public void TakePreSmoothSnapshot() => Smooth.TakeSnapshot();
        public void ProcessLocalSmoothDrag(Vector2 mousePos) => Smooth.ProcessLocalDrag(mousePos);

        // Tool-PARAMETER forwarders (same rationale as the method forwarders
        // above): MainWindow.Input routes local-smooth drags via
        // IsLocalSmoothSelected, and other partials may touch the rest. The
        // updated panel binds the tool paths (Smooth.Strength, Erase.BrushRadius,
        // ...) directly. NOTE: these wrappers do not raise MODE-level
        // PropertyChanged — bind through the tool properties, not these names.
        public double EraseBrushRadius
        {
            get => Erase.BrushRadius;
            set => Erase.BrushRadius = value;
        }
        public int SmoothStrength
        {
            get => Smooth.Strength;
            set => Smooth.Strength = value;
        }
        public double SmoothBrushRadius
        {
            get => Smooth.BrushRadius;
            set => Smooth.BrushRadius = value;
        }
        public bool IsGlobalSmoothSelected
        {
            get => Smooth.IsGlobalScope;
            set => Smooth.IsGlobalScope = value;
        }
        public bool IsLocalSmoothSelected
        {
            get => Smooth.IsLocalScope;
            set => Smooth.IsLocalScope = value;
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

        // Dense traced boundary of the current outline, in FULL-IMAGE space (no closure
        // dup), captured before Douglas-Peucker simplification. EFA runs on this — resampled
        // to ContourSampleCount points — so the harmonic spectrum reflects the true outline
        // detail rather than the thinned display polyline. 
        private List<Point> _activeDenseContourImage;

        // Clears the cached dense EFA contour. Called (via the tools'
        // OutlineEdited events) whenever erase or smooth changes the outline,
        // so the NEXT GenerateMetadata analyzes the live edited polyline
        // instead of the original auto-detected boundary. Cheap and
        // idempotent; safe to fire on every drag event.
        private void InvalidateDenseContour() => _activeDenseContourImage = null;

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

        // ── Scaled measurements ──
        // WPF FIX: PerimeterScaledResult / AreaScaledResult were STRINGS with
        // "Error: Unscaled" baked in as a sentinel VALUE — presentation mixed
        // into the model. The mode now exposes the numbers plus a calibration
        // flag; the panel formats them ("{0:F2} {1}" via MultiBinding) and owns
        // the uncalibrated wording. Canvas-space measurements are stored so a
        // calibration change re-derives the values without regenerating.
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

        // Called by OutlineOperation.ApplyMetadataToMode on undo/redo so the
        // scaled Perimeter/Area rows restore along with the ratio metrics —
        // they were stamped onto the operation but never pushed back, leaving
        // the panel at 0 / showing the previous outline's summary.
        public void RestoreScaledMeasurements(double canvasPerimeter, double canvasArea)
        {
            _lastCanvasPerimeter = canvasPerimeter;
            _lastCanvasArea = canvasArea;
            _hasScaledMeasurements = true;
            RecomputeScaledValues();
        }

        // Dash pattern for the dashed polylines this mode creates (pending
        // multi-click preview, blue EFD reconstruction): the shared frozen
        // instance in OutlineVisuals.

        // Fired after metadata is generated and stamped onto the committed outline.
        // MainWindow wires this to refresh the on-image operation counter: setting
        // HasMetadata mutates the operation in place, which does NOT raise a history
        // change, so the counter would otherwise not update until the next commit.
        public event Action MetadataGenerated;


        // Called when the user selects Generate Metadata (the control panel's
        // Checked handler — no longer a property-setter side effect) and by the
        // EFA window when settings change.
        public void GenerateMetadata()
        {
            // Refuse while a hand-drawn stroke is still open (not yet
            // self-closed). Checked WITHOUT _handDrawMode: WPF unchecks the
            // hand-draw radio (flipping the flag and auto-cancelling) BEFORE a
            // sibling's Checked handler can run, and the EFA window can call
            // in from any tool — the open stroke is the only reliable signal.
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

            // Scaled outputs use canvas-space measurements, matching the canvas-space
            // calibration captured by the Scale Image tool (imagePts above stays image-space
            // so the ratio metrics remain zoom-independent).
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

            // First metadata pass for a freshly committed outline: adopt the
            // harmonic count the 99% power analysis recommends as the default.
            // Skipped once resolved, and never overrides a manual choice.
            ApplyAutoHarmonicDefault();

            int harmonics = EfdHarmonics;

            // Run EFA on the dense contour resampled to ContourSampleCount equally-spaced points,
            // not the Douglas-Peucker display polyline (which drops the low-amplitude detail
            // the higher harmonics are meant to capture).
            //
            // The dense contour is captured during AUTOMATIC detection and is
            // cleared by InvalidateDenseContour the moment erase or smooth edits
            // the outline (see the tool wiring in the constructor). So after an
            // edit — and for hand-drawn outlines, which never populate it — this
            // falls through to the live polyline, which IS the edited shape.
            // Both branches resample to ContourSampleCount so harmonic precision
            // is the same either way.
            List<Point> efaSource;
            if (_activeDenseContourImage != null && _activeDenseContourImage.Count >= 3)
            {
                var canvasDense = new List<Point>(_activeDenseContourImage.Count);
                foreach (var ip in _activeDenseContourImage)
                    canvasDense.Add(new Point(ip.X * ScaleX + OffsetX, ip.Y * ScaleY + OffsetY));
                efaSource = GeometryCalculations.ResampleClosed(canvasDense, ContourSampleCount);
            }
            else
            {
                // Live (possibly edited) polyline, in canvas space with the
                // closure duplicate already stripped above — resampled the same
                // way the dense path is, rather than fed in raw.
                efaSource = pts.Count >= 3
                    ? GeometryCalculations.ResampleClosed(pts, ContourSampleCount)
                    : pts;
            }
            EFDCoefficientsResult = _efd.ComputeNormalized(efaSource, harmonics);

            // Presentation strings are built by the formatter — the mode
            // computes numbers, the formatter owns the text (WPF FIX: the
            // StringBuilder with alignment spaces and glyphs was presentation
            // logic living inside the model).
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

                // HasMetadata just flipped on an operation already sitting in history.
                // Recompute the on-image counter now — mutating the op in place doesn't
                // raise a history-changed event, so nothing else will trigger the refresh.
                MetadataGenerated?.Invoke();
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

        // Default harmonic count. Once an outline exists it is replaced, per
        // outline, by the count the 99% Harmonic Power analysis recommends (see
        // ApplyAutoHarmonicDefault) — so the default adapts to each specimen's
        // complexity instead of a fixed guess. A manual edit to EfdHarmonics
        // opts that outline out of further auto-setting.
        private const double AutoHarmonicPowerThreshold = 0.99;
        private int efdHarmonics = 10;

        // True once the 99% default has been applied for the CURRENT outline, or
        // once the user has manually set the count for it. Reset when a new
        // outline is committed (see ResetHarmonicAutoDefault), so the next
        // specimen gets its own recommendation.
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

        // Sets EfdHarmonics to the harmonic count the 99% cumulative-power
        // threshold recommends for the current outline, unless the count has
        // already been resolved (auto-set once, or manually chosen) for this
        // outline. Returns true if it applied a new default. Runs AFTER the EFD
        // coefficients exist for the outline, so the power analysis has data.
        private bool ApplyAutoHarmonicDefault()
        {
            if (_harmonicsResolvedForOutline) return false;

            var profile = AnalyzeHarmonicPower(AutoHarmonicPowerThreshold);
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
        private Polyline _efdPreviewPolyline = null;
        public event Action<Polyline> EFDPreviewReady;   // MainWindow wires this up
        public event Action EFDPreviewClear;             // MainWindow wires this up

        // Reconstructs the outline from EFD coefficients and displays it as a
        // blue overlay. Called whenever EfdHarmonics changes or metadata is generated.
        public void UpdateEFDPreview()
        {
            EFDPreviewClear?.Invoke();
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
                StrokeDashArray = OutlineVisuals.PreviewDashes,
                FillRule = FillRule.EvenOdd
            };
            foreach (var p in reconstructed)
                previewLine.Points.Add(p);

            _efdPreviewPolyline = previewLine;
            EFDPreviewReady?.Invoke(previewLine);
        }

        // ── Accessors for the consolidated Elliptic Fourier Analysis window ──
        // Both return CANVAS-space points (the frame the workspace draws in),
        // so the pair can be rescaled together for the image-free Outline tab.
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
            PolylineGeometry.StripClosureDuplicate(pts);
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

        // ── Tips ──
        // The five near-identical arrays used to be rebuilt inline on every
        // GetTips call, duplicating each shared line up to six times. The
        // shared lines are now single constants, the per-tool arrays are
        // built once, and GetTips just returns the cached array.
        private const string TipDecimate =
            "💡 To increase speed, try decimating pixel count using the Decimate function in the View menu.";
        private const string TipMultiClick =
            "💡 Use multi-click mode to merge multiple regions. To finalize an outline in multi-click mode, click inside the area bounded by a dashed line.";
        private const string TipSolidBackground =
            "💡 Outline mode performs best on unpatterned images with a solid background.";
        private const string TipHelp =
            "💡 The user guide and software information can be found in the Help menu.";
        private const string TipUndo =
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.";
        private const string TipRedo =
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.";
        private const string TipClear =
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.";
        private const string TipOpenImage =
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.";
        private const string TipZoom =
            "💡 Zoom in or out using the scroll wheel.";
        private const string TipPan =
            "💡 Press 'Ctrl' and left click to drag the image.";
        private const string TipToggleTips =
            "💡 Toggle tip visibility in the View menu.";

        // Automated Outline, legacy forced-watershed variant.
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
            if (SmoothOutlineMode) return SmoothTips;
            if (OutlineMetadataMode) return MetadataTips;
            if (HandDrawMode) return HandDrawTips;
            return NoTips;
        }
        #endregion
    }
}