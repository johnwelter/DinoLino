using System;
using System.Collections.Generic;
using System.Windows;
using DinoLino.DataTypes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Smooths the active outline in either global or local scope.
    /// </summary>
    public sealed class SmoothTool : ObservableToolBase
    {
        private readonly IOutlineToolContext _context;

        public SmoothTool(IOutlineToolContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Raised after smoothing changes the outline geometry.
        /// </summary>
        public event Action OutlineEdited;

        private int _strength = 0;

        /// <summary>
        /// Number of Laplacian passes to apply.
        /// In global scope, changes apply immediately from the current snapshot.
        /// In local scope, this only updates the brush strength.
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

        // =====================
        // Scope selection
        // =====================

        private bool _localScope = false;

        /// <summary>
        /// True when the tool smooths the entire outline.
        /// </summary>
        public bool IsGlobalScope
        {
            get => !_localScope;
            set
            {
                if (value == !_localScope) return;
                _localScope = !value;
                OnScopeChanged();
            }
        }

        /// <summary>
        /// True when the tool smooths only the dragged region.
        /// </summary>
        public bool IsLocalScope
        {
            get => _localScope;
            set
            {
                if (value == _localScope) return;
                _localScope = value;
                OnScopeChanged();
            }
        }

        private void OnScopeChanged()
        {
            OnPropertyChanged(nameof(IsGlobalScope));
            OnPropertyChanged(nameof(IsLocalScope));

            // Rebase the snapshot when entering local scope so the brush starts from the visible shape.
            if (_localScope)
                RefreshSnapshot();
        }

        // =====================
        // Brush settings
        // =====================

        private double _brushRadius = 25;

        /// <summary>
        /// Radius of the local smoothing brush in canvas pixels.
        /// </summary>
        public double BrushRadius
        {
            get => _brushRadius;
            set => SetField(ref _brushRadius, value);
        }

        // =====================
        // Snapshot management
        // =====================

        private List<Point> _preSmoothSnapshot = null;

        /// <summary>
        /// Captures the current outline as the global smoothing baseline.
        /// </summary>
        public void TakeSnapshot()
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null)
            {
                _preSmoothSnapshot = null;
                return;
            }

            _preSmoothSnapshot = new List<Point>(polyline.Points);
        }

        /// <summary>
        /// Replaces the snapshot with the current live outline.
        /// </summary>
        public void RefreshSnapshot()
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;

            _preSmoothSnapshot = new List<Point>(polyline.Points);
        }

        /// <summary>
        /// Sets the snapshot from committed outline points.
        /// </summary>
        public void SetSnapshot(IEnumerable<Point> points)
        {
            _preSmoothSnapshot = points == null ? null : new List<Point>(points);
        }

        /// <summary>
        /// Clears the snapshot when no outline is available.
        /// </summary>
        public void ClearSnapshot() => _preSmoothSnapshot = null;

        // =====================
        // Global smoothing
        // =====================

        private void ApplyGlobal()
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;
            if (_preSmoothSnapshot == null || _preSmoothSnapshot.Count < 3) return;

            // Resample first so smoothing pressure is distributed evenly around the outline.
            var working = PolylineGeometry.ResampleClosedUniformSpacing(
                new List<Point>(_preSmoothSnapshot), targetSpacing: 4.0);

            // Smooth distinct vertices only, then restore the explicit closure point.
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

            var points = polyline.Points;
            points.Clear();
            foreach (var p in working)
                points.Add(p);
            points.Add(working[0]);

            OutlineEdited?.Invoke();
        }

        // =====================
        // Local smoothing
        // =====================

        private bool _localDragInProgress = false;

        /// <summary>
        /// Applies brush-based smoothing during a drag in local scope.
        /// </summary>
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

                // Weight falls off to zero at the brush edge.
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

                var next = new Point[open];
                for (int pass = 0; pass < _strength; pass++)
                {
                    for (int i = 0; i < open; i++)
                    {
                        double w = weight[i];
                        if (w <= 0)
                        {
                            next[i] = work[i];
                            continue;
                        }

                        Point a = work[(i - 1 + open) % open];
                        Point b = work[i];
                        Point c = work[(i + 1) % open];

                        double tx = (a.X + 2 * b.X + c.X) / 4.0;
                        double ty = (a.Y + 2 * b.Y + c.Y) / 4.0;
                        next[i] = new Point(b.X + (tx - b.X) * w, b.Y + (ty - b.Y) * w);
                    }

                    var tmp = work;
                    work = next;
                    next = tmp;
                }

                pts.Clear();
                foreach (var p in work)
                    pts.Add(p);
                if (hasClosure)
                    pts.Add(work[0]);

                // Keep the snapshot aligned with what the user sees.
                RefreshSnapshot();

                OutlineEdited?.Invoke();
            }
            finally
            {
                _localDragInProgress = false;
            }
        }
    }
}