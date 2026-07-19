using System;
using System.Collections.Generic;
using System.Windows;
using DinoLino.DataTypes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// SMOOTH OUTLINE tool: Global (whole perimeter, live from the slider)
    /// vs Local (a draggable sanding brush, progressive like Erase).
    ///
    /// Extracted verbatim from OutlineMode's smooth region. The tool OWNS the
    /// pre-smooth snapshot; the old hidden coupling (erase refreshed the
    /// snapshot directly) is now an explicit wiring in OutlineMode:
    /// Erase.OutlineEdited → Smooth.RefreshSnapshot().
    /// </summary>
    public sealed class SmoothTool : ObservableToolBase
    {
        private readonly IOutlineToolContext _context;

        public SmoothTool(IOutlineToolContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Raised after this tool changes the outline's geometry (a global pass
        /// or a local-smooth drag). OutlineMode subscribes to invalidate the
        /// cached dense EFA contour, so Elliptic Fourier Analysis runs on the
        /// smoothed shape rather than the stale auto-detected boundary. Mirrors
        /// EraseTool.OutlineEdited.
        /// </summary>
        public event Action OutlineEdited;

        private int _strength = 0;
        /// <summary>
        /// Number of Laplacian passes. In GLOBAL scope changing the slider
        /// re-applies live from the snapshot (deliberate live-apply UX); in
        /// LOCAL scope it only sets the sanding strength for the drag brush
        /// and must not trigger a whole-perimeter pass.
        /// </summary>
        public int Strength
        {
            get => _strength;
            set
            {
                if (!SetField(ref _strength, value)) return;
                if (!_localScope)
                    ApplyGlobal();
            }
        }

        // ── Smoothing scope ──
        private bool _localScope = false;

        public bool IsGlobalScope
        {
            get => !_localScope;
            set { if (value == !_localScope) return; _localScope = !value; OnScopeChanged(); }
        }

        public bool IsLocalScope
        {
            get => _localScope;
            set { if (value == _localScope) return; _localScope = value; OnScopeChanged(); }
        }

        private void OnScopeChanged()
        {
            OnPropertyChanged(nameof(IsGlobalScope));
            OnPropertyChanged(nameof(IsLocalScope));
            // Entering LOCAL bakes whatever the global slider currently shows
            // into the snapshot, so sanding starts from the shape on screen and
            // a later return to Global doesn't stack passes on a stale base.
            if (_localScope) RefreshSnapshot();
        }

        // Brush size for local smoothing (canvas pixels), mirroring the erase
        // brush so the tool stays screen-true at any zoom.
        private double _brushRadius = 25;
        public double BrushRadius
        {
            get => _brushRadius;
            set => SetField(ref _brushRadius, value);
        }

        // Snapshot of the polyline points taken when smooth mode is entered,
        // so that smoothing always applies to the original shape rather than
        // compounding on each slider change.
        private List<Point> _preSmoothSnapshot = null;

        /// <summary>Public snapshot capture — MainWindow calls this when smooth mode is entered.</summary>
        public void TakeSnapshot()
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) { _preSmoothSnapshot = null; return; }
            _preSmoothSnapshot = new List<Point>(polyline.Points);
        }

        /// <summary>
        /// Re-bakes the snapshot from the live polyline. Wired to
        /// EraseTool.OutlineEdited, and called after each local-smooth drag,
        /// so a later Global pass builds on what is on screen.
        /// </summary>
        public void RefreshSnapshot()
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;
            _preSmoothSnapshot = new List<Point>(polyline.Points);
        }

        /// <summary>
        /// Seeds the snapshot from a freshly committed outline (the commit
        /// paths used to write _preSmoothSnapshot directly).
        /// </summary>
        public void SetSnapshot(IEnumerable<Point> points)
        {
            _preSmoothSnapshot = points == null ? null : new List<Point>(points);
        }

        /// <summary>Forgets the snapshot (mode Reset — the outline is gone).</summary>
        public void ClearSnapshot() => _preSmoothSnapshot = null;

        // Applies Laplacian smoothing to the entire polyline.
        // Runs Strength passes of neighbor-averaging over all points.
        // Always works from the pre-smooth snapshot so slider changes are
        // non-destructive; setting the slider back to 0 restores the
        // 4-px-uniform RESAMPLE of the snapshot (visually identical to the
        // original — the vertex set differs, which is fine because every
        // metadata consumer resamples again anyway).
        private void ApplyGlobal()
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;
            if (_preSmoothSnapshot == null || _preSmoothSnapshot.Count < 3) return;

            // Resample to uniform arc-length spacing before smoothing so that
            // Laplacian pressure is even around the whole outline. Without this,
            // dense regions (tight curves) smooth faster than sparse ones (straight runs),
            // causing corners to drift unpredictably.
            var working = PolylineGeometry.ResampleClosedUniformSpacing(
                new List<Point>(_preSmoothSnapshot), targetSpacing: 4.0);

            // BUG FIX (seam kink): the resampler preserves the trailing
            // closure duplicate, and the cyclic kernel below then saw the seam
            // vertex TWICE as adjacent ring members, kinking the start point a
            // little more with every pass. Smooth the DISTINCT vertices only
            // and re-add the explicit closure afterwards — exactly what
            // ProcessLocalDrag already does.
            PolylineGeometry.StripClosureDuplicate(working);
            if (working.Count < 3) return;

            for (int pass = 0; pass < _strength; pass++)
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

            // Write result back into the live polyline, with an explicit
            // closure point (the outline polylines' convention).
            var points = polyline.Points;
            points.Clear();
            foreach (var p in working)
                points.Add(p);
            points.Add(working[0]);

            // Fire AFTER the write so subscribers (the mode's dense-contour
            // invalidation) observe the smoothed geometry.
            OutlineEdited?.Invoke();
        }

        // =====================
        // LOCAL SMOOTHING (draggable sanding brush)
        // =====================
        // Called on mouse-drag when Smooth mode + Local scope are active.
        // Applies Strength weighted-Laplacian passes to the vertices under
        // the brush, with a quartic falloff to zero at the brush rim so the
        // treated section blends into its surroundings without kinks. Vertices
        // outside the brush are pinned exactly — no global ripple (the lesson
        // the erase tool taught). Progressive like sanding: keep dragging to
        // keep smoothing. Each event refreshes the smooth snapshot so a later
        // Global pass builds on what is on screen, matching erase semantics.
        private bool _localDragInProgress = false;

        public void ProcessLocalDrag(Vector2 mousePos)
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;
            if (!_localScope || _strength <= 0) return;
            if (_localDragInProgress) return;
            _localDragInProgress = true;
            try
            {
                var pts = polyline.Points;
                if (pts.Count < 4) return;

                // Distinct vertices; remember whether a closure duplicate exists.
                int n = pts.Count;
                bool hasClosure;
                {
                    Point f = pts[0], l = pts[n - 1];
                    double dx = f.X - l.X, dy = f.Y - l.Y;
                    hasClosure = dx * dx + dy * dy < 1.0;
                }
                int open = hasClosure ? n - 1 : n;
                if (open < 3) return;

                var work = new Point[open];
                for (int i = 0; i < open; i++) work[i] = pts[i];

                // Brush weights: canvas-space distance with a quartic falloff —
                // w = (1 - (d/R)^2)^2 — 1 at the centre, 0 at the rim, smooth.
                double r = Math.Max(2.0, _brushRadius);
                double r2 = r * r;
                var weight = new double[open];
                int touched = 0;
                for (int i = 0; i < open; i++)
                {
                    double dx = work[i].X - mousePos.X;
                    double dy = work[i].Y - mousePos.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 >= r2) continue;
                    double t = 1.0 - d2 / r2;
                    weight[i] = t * t;
                    touched++;
                }
                if (touched == 0) return;

                // Strength weighted passes of the same [1,2,1]/4 kernel the
                // global tool uses, scaled per-vertex by the brush weight.
                var next = new Point[open];
                for (int pass = 0; pass < _strength; pass++)
                {
                    for (int i = 0; i < open; i++)
                    {
                        double w = weight[i];
                        if (w <= 0) { next[i] = work[i]; continue; }
                        Point a = work[(i - 1 + open) % open];
                        Point b = work[i];
                        Point c = work[(i + 1) % open];
                        double tx = (a.X + 2 * b.X + c.X) / 4.0;
                        double ty = (a.Y + 2 * b.Y + c.Y) / 4.0;
                        next[i] = new Point(b.X + (tx - b.X) * w, b.Y + (ty - b.Y) * w);
                    }
                    var tmp = work; work = next; next = tmp;
                }

                pts.Clear();
                foreach (var p in work) pts.Add(p);
                if (hasClosure) pts.Add(work[0]);

                // Bake the edit so a later Global pass starts from this shape
                // (identical to how erase keeps the snapshot current).
                RefreshSnapshot();

                // Invalidate the cached dense EFA contour: this drag changed
                // the geometry, so EFA must re-run on the smoothed polyline.
                OutlineEdited?.Invoke();
            }
            finally { _localDragInProgress = false; }
        }
    }
}