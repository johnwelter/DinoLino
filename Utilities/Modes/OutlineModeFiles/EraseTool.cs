using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Shapes;
using DinoLino.DataTypes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// ERASE / SHRINK OUTLINE tool.
    ///
    /// On mouse-drag, rasterizes the outline to a mask, carves the brush disc
    /// out of it, re-traces the carved mask, and splices ONLY the affected arc
    /// back into the outline so untouched vertices never ripple.
    ///
    /// Extracted verbatim from OutlineMode's erase region. The tool owns its
    /// own OutlineProcessor: erase runs synchronously on the UI thread, so the
    /// instance is never shared across threads (see OutlineProcessor's
    /// threading model), and OutlineMode no longer needs a UI-thread processor
    /// field at all.
    /// </summary>
    public sealed class EraseTool : ObservableToolBase
    {
        private readonly IOutlineToolContext _context;

        // UI-THREAD-ONLY processor for the synchronous re-trace.
        private readonly OutlineProcessor _processor = new OutlineProcessor();

        public EraseTool(IOutlineToolContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Raised after the outline's geometry was actually modified by a drag.
        /// OutlineMode wires this to SmoothTool.RefreshSnapshot() — the
        /// explicit version of the old direct RefreshSmoothSnapshot() call, so
        /// a later Global smoothing pass builds on the erased shape instead of
        /// a stale snapshot.
        /// </summary>
        public event Action OutlineEdited;

        private double _brushRadius = 20;
        /// <summary>Brush radius in CANVAS pixels (screen-true at any zoom).</summary>
        public double BrushRadius
        {
            get => _brushRadius;
            set => SetField(ref _brushRadius, value);
        }

        private bool _dragInProgress = false;

        /// <summary>
        /// Called on mouse-drag when the Erase tool is active. Finds the
        /// stretch of outline under the brush and replaces just that arc with
        /// the freshly carved boundary.
        /// </summary>
        public void ProcessDrag(Vector2 mousePos)
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;
            if (_dragInProgress) return;

            var t = _context.Transform;
            if (!t.IsValid) return;

            _dragInProgress = true;
            try
            {
                var srcPoints = polyline.Points;
                if (srcPoints.Count < 3) return;

                // Work in IMAGE space so mask resolution is independent of zoom.
                var img = new List<Point>(srcPoints.Count);
                foreach (var cp in srcPoints)
                    img.Add(t.CanvasToImage(cp));
                PolylineGeometry.StripClosureDuplicate(img);
                if (img.Count < 3) return;

                double rImgX = BrushRadius / t.ScaleX;
                double rImgY = BrushRadius / t.ScaleY;
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
                bool[] mask = PolylineGeometry.RasterizePolygon(local, w, h);

                // 2) Carve the brush disc. Test each pixel's CANVAS distance so the brush
                //    stays a true on-screen circle even under non-uniform scale. Clearing
                //    pixels can only ever REMOVE area — no cursor-side dependence.
                double rCanvas2 = BrushRadius * BrushRadius;
                double cImgX = (mousePos.X - t.OffsetX) / t.ScaleX - ox;
                double cImgY = (mousePos.Y - t.OffsetY) / t.ScaleY - oy;
                int bx0 = Math.Max(0, (int)Math.Floor(cImgX - rImgX) - 1);
                int bx1 = Math.Min(w - 1, (int)Math.Ceiling(cImgX + rImgX) + 1);
                int by0 = Math.Max(0, (int)Math.Floor(cImgY - rImgY) - 1);
                int by1 = Math.Min(h - 1, (int)Math.Ceiling(cImgY + rImgY) + 1);
                bool anyCleared = false;
                for (int y = by0; y <= by1; y++)
                    for (int x = bx0; x <= bx1; x++)
                    {
                        if (!mask[y * w + x]) continue;
                        double canX = (x + ox + 0.5) * t.ScaleX + t.OffsetX;
                        double canY = (y + oy + 0.5) * t.ScaleY + t.OffsetY;
                        double dx = canX - mousePos.X, dy = canY - mousePos.Y;
                        if (dx * dx + dy * dy <= rCanvas2) { mask[y * w + x] = false; anyCleared = true; }
                    }
                if (!anyCleared) return;

                // 3) Re-fill enclosed holes (so an interior-only stroke does nothing rather
                //    than punching a hole), then keep the single largest blob.
                mask = _processor.FillHoles(mask, w, h);
                mask = _processor.KeepLargestComponent(mask, w, h);
                if (!_processor.HasMinimumPixels(mask, 9)) return;

                // 4) Re-trace the carved mask as a guaranteed-simple dense boundary.
                var boundary = _processor.TraceBoundary(mask, w, h);
                if (boundary.Count < 8) return;

                // 5) Update ONLY the stretch of outline the brush actually overlapped,
                //    leaving every other vertex exactly where it was. This removes the
                //    "ripple": Douglas-Peucker reselects its vertices globally, so
                //    re-simplifying the WHOLE boundary on every drag shifts vertices in
                //    regions the brush never reached. Splicing just the affected arc keeps
                //    untouched vertices pinned (they re-project to the same canvas coords).
                List<Point> newLocal = SpliceErasedArc(local, boundary, mousePos, ox, oy, t);
                if (newLocal == null)
                {
                    // Couldn't isolate a single affected arc (e.g. the stroke pinched the
                    // shape into separate pieces). Fall back to re-simplifying the whole
                    // boundary — this can ripple, but only in these rare cases.
                    newLocal = GeometryCalculations.DouglasPeucker(boundary, _context.SimplifyEpsilon);
                }
                if (newLocal == null || newLocal.Count < 3) return;

                srcPoints.Clear();
                foreach (var p in newLocal)
                    srcPoints.Add(new Point((p.X + ox) * t.ScaleX + t.OffsetX, (p.Y + oy) * t.ScaleY + t.OffsetY));
                srcPoints.Add(new Point((newLocal[0].X + ox) * t.ScaleX + t.OffsetX, (newLocal[0].Y + oy) * t.ScaleY + t.OffsetY));

                OutlineEdited?.Invoke();
            }
            finally { _dragInProgress = false; }
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
        //   t           : the canvas ↔ image transform in effect for this drag
        //
        // Returns the new vertex list (local frame, no closure dup), or null when the
        // affected region can't be isolated as a single arc (caller falls back).
        private List<Point> SpliceErasedArc(List<Point> oldLocal, List<Point> dense,
            Vector2 mouseCanvas, int ox, int oy, ViewTransform t)
        {
            int n = oldLocal.Count;
            if (n < 3 || dense.Count < 3) return null;

            // Seam slightly outside the brush so the join lands on unmodified geometry.
            double rTest = BrushRadius + 2.0;
            double rTest2 = rTest * rTest;

            bool UnderBrush(Point pLocal)
            {
                double canX = (pLocal.X + ox) * t.ScaleX + t.OffsetX;
                double canY = (pLocal.Y + oy) * t.ScaleY + t.OffsetY;
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
                                                         mouseCanvas, ox, oy, t);
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
            int aJ = PolylineGeometry.NearestIndex(dense, A);
            int bJ = PolylineGeometry.NearestIndex(dense, B);
            if (aJ < 0 || bJ < 0 || aJ == bJ) return null;

            List<Point> arc = ChooseUnderBrushArc(
                PolylineGeometry.ExtractArc(dense, aJ, bJ, true),
                PolylineGeometry.ExtractArc(dense, aJ, bJ, false),
                UnderBrush);
            if (arc == null) return null;

            var arcSimpl = GeometryCalculations.DouglasPeucker(arc, _context.SimplifyEpsilon);
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
            if (PolylineGeometry.HasSelfIntersection(result)) return null; // never return a tangled outline

            return result;
        }

        // Squared canvas-space distance from the brush centre to segment p0–p1 (local frame).
        private static double PointToSegmentCanvasDist2(Point p0, Point p1, Vector2 mouseCanvas,
            int ox, int oy, ViewTransform t)
        {
            double ax = (p0.X + ox) * t.ScaleX + t.OffsetX, ay = (p0.Y + oy) * t.ScaleY + t.OffsetY;
            double bx = (p1.X + ox) * t.ScaleX + t.OffsetX, by = (p1.Y + oy) * t.ScaleY + t.OffsetY;
            double vx = bx - ax, vy = by - ay;
            double wx = mouseCanvas.X - ax, wy = mouseCanvas.Y - ay;
            double len2 = vx * vx + vy * vy;
            double tt = len2 > 1e-9 ? (wx * vx + wy * vy) / len2 : 0.0;
            if (tt < 0.0) tt = 0.0; else if (tt > 1.0) tt = 1.0;
            double cx = ax + tt * vx, cy = ay + tt * vy;
            double dx = mouseCanvas.X - cx, dy = mouseCanvas.Y - cy;
            return dx * dx + dy * dy;
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
    }
}