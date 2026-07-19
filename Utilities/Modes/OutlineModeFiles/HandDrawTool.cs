using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using DinoLino.DataTypes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// FREE-HAND OUTLINE tool.
    ///
    /// The user holds the left button and drags to lay down a stroke.
    /// Releasing pauses the stroke (a subsequent press continues appending
    /// from the cursor). As soon as the stroke crosses itself, the loop is
    /// closed at the crossing point and any dangling tails before/after the
    /// loop are discarded — so a "p" or a loop with two tails collapses to
    /// just the enclosed object.
    ///
    /// Extracted verbatim from OutlineMode's hand-draw region; the tool sees
    /// the mode only through <see cref="IOutlineToolContext"/>.
    /// </summary>
    public sealed class HandDrawTool : ObservableToolBase
    {
        private readonly IOutlineToolContext _context;

        public HandDrawTool(IOutlineToolContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        // Raw stroke points in CANVAS space, accumulated across press/drag/release
        // until the loop closes. Stored densely; simplified only at closure.
        private readonly List<Point> _stroke = new List<Point>();

        // The live, in-progress (open) preview polyline shown while drawing.
        private Polyline _previewPolyline = null;

        // True between the first press and final closure of a hand-drawn outline.
        private bool _drawingActive = false;

        // MainWindow wires these (via the mode's forwarding events): add/remove
        // the live preview line on the canvas.
        public event Action<Polyline> PreviewReady;     // show/replace the open preview
        public event Action<Polyline> PreviewClear;     // remove the given preview line

        // Minimum canvas distance between consecutive accepted stroke points. Keeps the
        // point list manageable and avoids degenerate zero-length segments that would
        // confuse the self-intersection test.
        private const double MinPointSpacing = 2.0;

        // Dash pattern for the open (not-yet-closed) preview: the shared
        // frozen instance in OutlineVisuals (one copy for mode + tools).

        // True when an outline has been committed by this tool (used by the panel to gate
        // "Generate Metadata"). Distinct from _drawingActive, which means "mid-stroke".
        private bool _outlineCommitted = false;
        public bool HasCommittedOutline => _outlineCommitted;

        /// <summary>True while a stroke is open (started but not yet self-closed).</summary>
        public bool IsStrokeOpen => _drawingActive;

        /// <summary>Called on left-button DOWN while hand-draw is the active tool.</summary>
        public void BeginStroke(Vector2 canvasPos)
        {
            if (!_context.IsHandDrawActive) return;
            if (!_context.HasImage) return;

            // First press of a brand-new outline: start fresh and clear any prior result.
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

        /// <summary>Called on mouse MOVE while the left button is held in hand-draw.</summary>
        public void ProcessDrag(Vector2 canvasPos)
        {
            if (!_context.IsHandDrawActive || !_drawingActive) return;
            AppendPoint(new Point(canvasPos.X, canvasPos.Y));
        }

        /// <summary>
        /// Called on left-button UP while hand-draw is active. Intentionally
        /// does nothing beyond leaving the stroke open: a paused stroke is
        /// resumed by the next BeginStroke, which keeps _drawingActive true
        /// and so does NOT reset the stroke.
        /// </summary>
        public void EndStroke()
        {
        }

        /// <summary>Discards any in-progress stroke (mode switch, reset, escape).</summary>
        public void Cancel()
        {
            _drawingActive = false;
            _stroke.Clear();
            ClearPreview();
        }

        /// <summary>Cancels the stroke and forgets the committed flag (mode Reset).</summary>
        public void Reset()
        {
            Cancel();
            _outlineCommitted = false;
        }

        // Adds a point to the stroke (respecting min spacing), refreshes the live preview,
        // and tests whether the newly added segment closes the loop.
        private void AppendPoint(Point p)
        {
            if (_stroke.Count > 0)
            {
                Point last = _stroke[_stroke.Count - 1];
                double dx = p.X - last.X, dy = p.Y - last.Y;
                if (dx * dx + dy * dy < MinPointSpacing * MinPointSpacing)
                    return; // too close to previous point; skip
            }

            _stroke.Add(p);

            // Check whether the most recent segment crosses any earlier, non-adjacent segment.
            if (TryCloseLoop()) return;

            RefreshPreview();
        }

        // Builds/updates the open preview polyline from the current stroke.
        private void RefreshPreview()
        {
            if (_stroke.Count < 2) { ClearPreview(); return; }

            var line = new Polyline
            {
                Stroke = _context.LineColor,
                StrokeThickness = 2,
                StrokeDashArray = OutlineVisuals.PreviewDashes, // dashed = not yet closed
                FillRule = FillRule.EvenOdd
            };
            foreach (var sp in _stroke)
                line.Points.Add(sp);

            var old = _previewPolyline;
            _previewPolyline = line;
            PreviewReady?.Invoke(line);
            if (old != null) PreviewClear?.Invoke(old);
        }

        private void ClearPreview()
        {
            if (_previewPolyline != null)
            {
                PreviewClear?.Invoke(_previewPolyline);
                _previewPolyline = null;
            }
        }

        // Tests whether the LAST segment of the stroke intersects any earlier non-adjacent
        // segment. If it does, the closed loop is extracted (the polygon between the two
        // crossing segments, joined at the intersection point), tails are discarded, and
        // the outline is committed exactly like an auto-generated one.
        //
        // Returns true if the loop was closed (and the stroke consumed), false otherwise.
        private bool TryCloseLoop()
        {
            int n = _stroke.Count;
            if (n < 4) return false; // need at least a few segments to self-cross

            int lastSeg = n - 2;                     // segment (n-2 -> n-1)
            Point a1 = _stroke[lastSeg];
            Point a2 = _stroke[lastSeg + 1];

            // Compare against every earlier segment except the one directly adjacent
            // (which shares endpoint a1 and can't "cross" in a meaningful way).
            for (int j = 0; j <= lastSeg - 2; j++)
            {
                Point b1 = _stroke[j];
                Point b2 = _stroke[j + 1];

                if (PolylineGeometry.TryGetSegmentIntersection(a1, a2, b1, b2, out Point hit))
                {
                    // The enclosed loop runs from the intersection point, along the stroke
                    // through indices j+1 .. lastSeg, and back to the intersection point.
                    // Everything before segment j (the leading tail) and after the last
                    // point (the trailing tail) is discarded.
                    var loop = new List<Point> { hit };
                    for (int k = j + 1; k <= lastSeg; k++)
                        loop.Add(_stroke[k]);
                    // loop implicitly closes back to 'hit'

                    CommitLoop(loop);
                    return true;
                }
            }

            return false;
        }

        // Finalizes a closed hand-drawn loop: simplify, validate, convert to a committed
        // outline, and route it through the SAME commit path as an auto outline so all
        // metadata / EFD / smooth / erase behavior is shared.
        private void CommitLoop(List<Point> loopCanvas)
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

            if (cleaned.Count < 3) { Cancel(); return; }

            // Light simplification to remove hand-jitter, matching the auto outline feel.
            var simplified = GeometryCalculations.DouglasPeucker(cleaned, _context.SimplifyEpsilon);
            if (simplified.Count < 3) simplified = cleaned;

            // Guard: if simplification somehow self-intersected, fall back to the dense loop.
            if (PolylineGeometry.HasSelfIntersection(simplified))
                simplified = cleaned;

            var poly = new Polyline
            {
                Stroke = _context.LineColor,
                StrokeThickness = 2,
                FillRule = FillRule.EvenOdd
            };
            foreach (var p in simplified)
                poly.Points.Add(p);
            poly.Points.Add(simplified[0]); // explicit closure

            // Tear down the in-progress drawing state and the dashed preview.
            ClearPreview();
            _drawingActive = false;
            _stroke.Clear();
            _outlineCommitted = true;

            // Reuse the existing commit path: sets the active polyline, snapshots for
            // smoothing, commits an OutlineOperation, and fires OutlineReady so
            // MainWindow draws it.
            _context.CommitOutline(poly);
        }
    }
}