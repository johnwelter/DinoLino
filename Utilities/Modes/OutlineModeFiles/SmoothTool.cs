using System;
using System.Collections.Generic;
using System.Windows;
using DinoLino.DataTypes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>Smooths the active outline in either global or local scope.</summary>
    public sealed class SmoothTool : ObservableToolBase
    {
        private readonly IOutlineToolContext _context;

        public SmoothTool(IOutlineToolContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>Raised after smoothing changes the outline geometry.</summary>
        public event Action OutlineEdited;

        private int _strength = 0;

        /// <summary>Number of Laplacian passes to apply.</summary>
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

        // ---- Scope selection ----

        private bool _localScope = false;

        /// <summary>True when the tool smooths the entire outline.</summary>
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

        /// <summary>True when the tool smooths only the dragged region.</summary>
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

        // ---- Brush settings ----

        private double _brushRadius = 25;

        /// <summary>Radius of the local smoothing brush in canvas pixels.</summary>
        public double BrushRadius
        {
            get => _brushRadius;
            set => SetField(ref _brushRadius, value);
        }

        // ---- Snapshot management ----

        private List<Point> _preSmoothSnapshot = null;

        /// <summary>Captures the current outline as the global smoothing baseline.</summary>
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

        /// <summary>Replaces the snapshot with the current live outline.</summary>
        public void RefreshSnapshot()
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;

            _preSmoothSnapshot = new List<Point>(polyline.Points);
        }

        /// <summary>Sets the snapshot from committed outline points.</summary>
        public void SetSnapshot(IEnumerable<Point> points)
        {
            _preSmoothSnapshot = points == null ? null : new List<Point>(points);
        }

        /// <summary>Clears the snapshot when no outline is available.</summary>
        public void ClearSnapshot() => _preSmoothSnapshot = null;

        // ---- Global smoothing ----

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

            PolylineGeometry.SmoothClosedRing(working, _strength);

            var points = polyline.Points;
            points.Clear();
            foreach (var p in working)
                points.Add(p);
            points.Add(working[0]);

            OutlineEdited?.Invoke();
        }

        // ---- Local smoothing ----

        /// <summary>Applies brush-based smoothing during a drag in local scope.</summary>
        public void ProcessLocalDrag(Vector2 mousePos)
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;
            if (!_localScope || _strength <= 0) return;
            if (!TryBeginDrag()) return;

            try
            {
                var pts = polyline.Points;
                if (pts.Count < 4) return;

                int n = pts.Count;
                bool hasClosure = PolylineGeometry.HasClosureDuplicate(pts);
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

                PolylineGeometry.SmoothClosedRing(work, _strength, weight);

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
                EndDrag();
            }
        }
    }
}