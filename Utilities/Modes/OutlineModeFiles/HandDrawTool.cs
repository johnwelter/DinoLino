using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using DinoLino.DataTypes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>Free-hand outline tool.</summary>
    public sealed class HandDrawTool : ObservableToolBase
    {
        private readonly IOutlineToolContext _context;

        /// <summary>Stores raw stroke points in canvas space until the outline closes.</summary>
        private readonly List<Point> _stroke = new List<Point>();

        /// <summary>Live preview polyline shown while the stroke is open.</summary>
        private Polyline _previewPolyline = null;

        /// True between the first press and the final self-closing intersection.
        private bool _drawingActive = false;

        /// <summary>Raised when the live preview should be shown or replaced.</summary>
        public event Action<Polyline> PreviewReady;

        /// <summary>Raised when the live preview should be removed.</summary>
        public event Action<Polyline> PreviewClear;

        /// <summary>Minimum distance between accepted stroke points, in canvas pixels.</summary>
        private const double MinPointSpacing = 2.0;

        /// <summary>True after this tool has committed a closed outline.</summary>
        private bool _outlineCommitted = false;

        public bool HasCommittedOutline => _outlineCommitted;

        /// <summary>True while a hand-drawn stroke is open.</summary>
        public bool IsStrokeOpen => _drawingActive;

        public HandDrawTool(IOutlineToolContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        // ---- Stroke lifecycle ----

        /// <summary>Starts or continues a stroke on mouse down.</summary>
        public void BeginStroke(Vector2 canvasPos)
        {
            if (!_context.IsHandDrawActive) return;
            if (!_context.HasImage) return;

            // First press starts a new outline session.
            if (!_drawingActive)
            {
                _context.OnHandStrokeStarted();
                _drawingActive = true;
                _outlineCommitted = false;
                _stroke.Clear();
                ClearPreview();
            }

            AppendPoint(new Point(canvasPos.X, canvasPos.Y));
        }

        /// <summary>Appends points while the left button is held down.</summary>
        public void ProcessDrag(Vector2 canvasPos)
        {
            if (!_context.IsHandDrawActive || !_drawingActive) return;
            AppendPoint(new Point(canvasPos.X, canvasPos.Y));
        }

        /// <summary>Leaves the stroke open so drawing can resume on the next press.</summary>
        public void EndStroke()
        {
        }

        /// <summary>Discards the current in-progress stroke.</summary>
        public void Cancel()
        {
            _drawingActive = false;
            _stroke.Clear();
            ClearPreview();
        }

        /// <summary>Resets the tool and clears the committed-outline flag.</summary>
        public void Reset()
        {
            Cancel();
            _outlineCommitted = false;
        }

        // ---- Point handling ----

        /// Adds a point if it is far enough from the previous one, then tests for
        /// closure.
        private void AppendPoint(Point p)
        {
            if (_stroke.Count > 0)
            {
                Point last = _stroke[_stroke.Count - 1];
                double dx = p.X - last.X, dy = p.Y - last.Y;
                if (dx * dx + dy * dy < MinPointSpacing * MinPointSpacing)
                    return;
            }

            _stroke.Add(p);

            // A self-intersection closes the loop immediately.
            if (TryCloseLoop()) return;

            RefreshPreview();
        }

        /// <summary>Rebuilds the dashed preview polyline from the current stroke.</summary>
        private void RefreshPreview()
        {
            if (_stroke.Count < 2)
            {
                ClearPreview();
                return;
            }

            var line = OutlineVisuals.CreateOutlinePolyline(_context.LineColor, dashed: true);

            foreach (var sp in _stroke)
                line.Points.Add(sp);

            var old = _previewPolyline;
            _previewPolyline = line;
            PreviewReady?.Invoke(line);

            if (old != null)
                PreviewClear?.Invoke(old);
        }

        /// <summary>Removes the current preview polyline from the canvas.</summary>
        private void ClearPreview()
        {
            if (_previewPolyline != null)
            {
                PreviewClear?.Invoke(_previewPolyline);
                _previewPolyline = null;
            }
        }

        // ---- Loop closure ----

        /// <summary>Closes the outline when the newest segment crosses an earlier one.</summary>
        private bool TryCloseLoop()
        {
            int n = _stroke.Count;
            if (n < 4) return false;

            int lastSeg = n - 2; // segment (n-2 -> n-1)
            Point a1 = _stroke[lastSeg];
            Point a2 = _stroke[lastSeg + 1];

            // Skip the adjacent segment; it shares an endpoint and cannot form a valid crossing.
            for (int j = 0; j <= lastSeg - 2; j++)
            {
                Point b1 = _stroke[j];
                Point b2 = _stroke[j + 1];

                if (PolylineGeometry.TryGetSegmentIntersection(a1, a2, b1, b2, out Point hit))
                {
                    // Keep only the enclosed loop and discard the tails.
                    var loop = new List<Point> { hit };
                    for (int k = j + 1; k <= lastSeg; k++)
                        loop.Add(_stroke[k]);

                    CommitLoop(loop);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Simplifies and commits a closed hand-drawn loop.</summary>
        private void CommitLoop(List<Point> loopCanvas)
        {
            // Drop near-identical neighbours before simplification.
            var cleaned = PolylineGeometry.RemoveConsecutiveDuplicates(loopCanvas, 0.5);

            if (cleaned.Count < 3)
            {
                Cancel();
                return;
            }

            // Light simplification reduces hand jitter while preserving the loop shape.
            var simplified = GeometryCalculations.DouglasPeucker(cleaned, _context.SimplifyEpsilon);
            if (simplified.Count < 3)
                simplified = cleaned;

            // If simplification creates an invalid outline, fall back to the dense loop.
            if (PolylineGeometry.HasSelfIntersection(simplified))
                simplified = cleaned;

            var poly = OutlineVisuals.CreateOutlinePolyline(_context.LineColor);

            foreach (var p in simplified)
                poly.Points.Add(p);

            // WPF Polyline is open by default, so repeat the first point to close it.
            poly.Points.Add(simplified[0]);

            ClearPreview();
            _drawingActive = false;
            _stroke.Clear();
            _outlineCommitted = true;

            // Reuse the standard outline commit path so all downstream tools see the same result.
            _context.CommitOutline(poly);
        }
    }
}