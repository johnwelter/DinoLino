using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Shapes;
using DinoLino.DataTypes;

namespace DinoLino.Utilities.Modes
{
    /// Expands the active outline by adding the brush area to the shape and
    /// re-tracing the result — the exact inverse of EraseTool. The pipeline
    /// (rasterize → edit mask → fill holes → keep largest → trace → splice) is
    /// deliberately identical so the polyline moves the same way it does when
    /// erasing, just outward instead of inward. EraseTool itself is untouched.
    public sealed class PushTool : ObservableToolBase
    {
        private readonly IOutlineToolContext _context;

        // UI-thread-only processor used for raster, fill, and boundary tracing during a push.
        private readonly OutlineProcessor _processor = new OutlineProcessor();

        public PushTool(IOutlineToolContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>Raised after the outline geometry has been modified by a drag.</summary>
        public event Action OutlineEdited;

        private double _brushRadius = 20;

        /// <summary>Brush radius in canvas pixels.</summary>
        public double BrushRadius
        {
            get => _brushRadius;
            set => SetField(ref _brushRadius, value);
        }

        /// <summary>Applies a push stroke at the current mouse position.</summary>
        public void ProcessDrag(Vector2 mousePos)
        {
            var polyline = _context.ActivePolyline;
            if (polyline == null) return;

            var t = _context.Transform;
            if (!t.IsValid) return;

            if (!TryBeginDrag()) return;
            try
            {
                var srcPoints = polyline.Points;
                if (srcPoints.Count < 3) return;

                // Convert the outline to image space so the mask resolution is independent of zoom.
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

                // Unlike erasing, pushing ADDS pixels outside the current outline, so
                // the crop must also cover the brush disc wherever the cursor is.
                // (Erase never needs this: it only clears pixels the outline already has.)
                Point brushImg = t.CanvasToImage(new Point(mousePos.X, mousePos.Y));
                if (brushImg.X - rImgX < minX) minX = brushImg.X - rImgX;
                if (brushImg.X + rImgX > maxX) maxX = brushImg.X + rImgX;
                if (brushImg.Y - rImgY < minY) minY = brushImg.Y - rImgY;
                if (brushImg.Y + rImgY > maxY) maxY = brushImg.Y + rImgY;

                int ox = (int)Math.Floor(minX) - pad;
                int oy = (int)Math.Floor(minY) - pad;
                int w = (int)Math.Ceiling(maxX) - ox + pad;
                int h = (int)Math.Ceiling(maxY) - oy + pad;
                if (w < 3 || h < 3 || (long)w * h > 16_000_000) return;

                var local = new List<Point>(img.Count);
                foreach (var p in img) local.Add(new Point(p.X - ox, p.Y - oy));

                // Rasterize the current outline into a filled mask.
                bool[] mask = PolylineGeometry.RasterizePolygon(local, w, h);

                // Add pixels inside the brush disc using canvas-space distance so the
                // brush stays circular on screen.
                double rCanvas2 = BrushRadius * BrushRadius;
                Point brushCrop = t.CanvasToImage(new Point(mousePos.X, mousePos.Y), ox, oy);
                double cImgX = brushCrop.X;
                double cImgY = brushCrop.Y;
                int bx0 = Math.Max(0, (int)Math.Floor(cImgX - rImgX) - 1);
                int bx1 = Math.Min(w - 1, (int)Math.Ceiling(cImgX + rImgX) + 1);
                int by0 = Math.Max(0, (int)Math.Floor(cImgY - rImgY) - 1);
                int by1 = Math.Min(h - 1, (int)Math.Ceiling(cImgY + rImgY) + 1);

                // Keep the outline on the photo: when the image dimensions are
                // known, pixels past its border are never added.
                int imgW = _context.ImagePixelWidth;
                int imgH = _context.ImagePixelHeight;
                bool clampToImage = imgW > 0 && imgH > 0;

                bool anyAdded = false;
                bool touchesOutline = false;

                for (int y = by0; y <= by1; y++)
                {
                    for (int x = bx0; x <= bx1; x++)
                    {
                        Point canvasPixel = t.ImageToCanvas(new Point(x + 0.5, y + 0.5), ox, oy);
                        double dx = canvasPixel.X - mousePos.X;
                        double dy = canvasPixel.Y - mousePos.Y;
                        if (dx * dx + dy * dy > rCanvas2) continue;

                        int i = y * w + x;

                        // Each pixel is visited exactly once, so a true here is
                        // original outline, not something this pass just added.
                        if (mask[i]) { touchesOutline = true; continue; }

                        if (clampToImage)
                        {
                            int fullX = x + ox, fullY = y + oy;
                            if ((uint)fullX >= (uint)imgW || (uint)fullY >= (uint)imgH) continue;
                        }

                        mask[i] = true;
                        anyAdded = true;
                    }
                }

                // Nothing changed, or the brush isn't touching the shape at all: a
                // detached brush disc must never replace the outline (it could win
                // KeepLargestComponent on a small outline) or trigger the full
                // re-simplification fallback on untouched geometry.
                if (!anyAdded || !touchesOutline) return;

                // Close small holes (a push across a narrow bay can seal it shut) and
                // keep the largest connected region so the outline stays single-part.
                mask = _processor.FillHoles(mask, w, h);
                mask = _processor.KeepLargestComponent(mask, w, h);
                if (!_processor.HasMinimumPixels(mask, 9)) return;

                // Trace the expanded mask back into a boundary polyline.
                var boundary = _processor.TraceBoundary(mask, w, h);
                if (boundary.Count < 8) return;

                // Splice only the affected arc back into the outline to avoid shifting untouched vertices.
                List<Point> newLocal = SplicePushedArc(local, boundary, mousePos, ox, oy, t);
                if (newLocal == null)
                {
                    // Fall back to full simplification only when the pushed region cannot be isolated cleanly.
                    newLocal = GeometryCalculations.DouglasPeucker(boundary, _context.SimplifyEpsilon);
                }

                if (newLocal == null || newLocal.Count < 3) return;

                // Rebuild the live polyline in canvas space and restore closure.
                srcPoints.Clear();
                foreach (var p in newLocal)
                    srcPoints.Add(t.ImageToCanvas(p, ox, oy));
                srcPoints.Add(t.ImageToCanvas(newLocal[0], ox, oy));

                OutlineEdited?.Invoke();
            }
            finally
            {
                EndDrag();
            }
        }

        // ---- Arc splicing ----
        // Identical logic to EraseTool's splice, because the geometry is symmetric:
        // old vertices under the brush are the ones the disc swallowed (they are now
        // interior points of the expanded mask), and the traced boundary's
        // under-brush arc is the new outward bulge that replaces them.

        /// <summary>Replaces only the outline arc affected by the brush.</summary>
        private List<Point> SplicePushedArc(List<Point> oldLocal, List<Point> dense,
            Vector2 mouseCanvas, int ox, int oy, ViewTransform t)
        {
            int n = oldLocal.Count;
            if (n < 3 || dense.Count < 3) return null;

            // Test slightly larger than the brush so the join lands on stable geometry.
            double rTest = BrushRadius + 2.0;
            double rTest2 = rTest * rTest;

            bool UnderBrush(Point pLocal)
            {
                Point canvasPoint = t.ImageToCanvas(pLocal, ox, oy);
                double dx = canvasPoint.X - mouseCanvas.X;
                double dy = canvasPoint.Y - mouseCanvas.Y;
                return dx * dx + dy * dy <= rTest2;
            }

            // Identify which old vertices were touched by the brush.
            var under = new bool[n];
            int underCount = 0;
            for (int i = 0; i < n; i++)
            {
                under[i] = UnderBrush(oldLocal[i]);
                if (under[i]) underCount++;
            }

            if (underCount == n) return null;

            int aIdx, bIdx;
            if (underCount > 0)
            {
                // Require one contiguous hit run on the cyclic outline.
                int runs = 0, runStart = -1;
                for (int i = 0; i < n; i++)
                    if (under[i] && !under[(i - 1 + n) % n]) { runs++; runStart = i; }

                if (runs != 1) return null;

                int runEnd = runStart;
                while (under[(runEnd + 1) % n]) runEnd = (runEnd + 1) % n;

                aIdx = (runStart - 1 + n) % n;
                bIdx = (runEnd + 1) % n;
            }
            else
            {
                // If the brush hit an edge between vertices, anchor the splice to the nearest edge.
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

            // Extract the two possible arcs on the traced boundary and keep the one actually under the brush.
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

            // Keep the untouched portion B -> ... -> A, then insert the simplified replacement arc.
            var result = new List<Point>(n + arcSimpl.Count);
            for (int i = bIdx; ; i = (i + 1) % n)
            {
                result.Add(oldLocal[i]);
                if (i == aIdx) break;
            }

            for (int k = 1; k < arcSimpl.Count - 1; k++)
                result.Add(arcSimpl[k]);

            if (result.Count < 3) return null;
            if (PolylineGeometry.HasSelfIntersection(result)) return null;

            return result;
        }

        /// <summary>Squared canvas-space distance from the brush center to a segment.</summary>
        private static double PointToSegmentCanvasDist2(Point p0, Point p1, Vector2 mouseCanvas,
            int ox, int oy, ViewTransform t)
        {
            Point a = t.ImageToCanvas(p0, ox, oy);
            Point b = t.ImageToCanvas(p1, ox, oy);
            double ax = a.X, ay = a.Y;
            double bx = b.X, by = b.Y;
            double vx = bx - ax, vy = by - ay;
            double wx = mouseCanvas.X - ax, wy = mouseCanvas.Y - ay;
            double len2 = vx * vx + vy * vy;
            double tt = len2 > 1e-9 ? (wx * vx + wy * vy) / len2 : 0.0;
            if (tt < 0.0) tt = 0.0; else if (tt > 1.0) tt = 1.0;
            double cx = ax + tt * vx, cy = ay + tt * vy;
            double dx = mouseCanvas.X - cx, dy = mouseCanvas.Y - cy;
            return dx * dx + dy * dy;
        }

        /// <summary>Picks the arc whose interior is actually under the brush.</summary>
        private static List<Point> ChooseUnderBrushArc(List<Point> a, List<Point> b, Func<Point, bool> underBrush)
        {
            double FracUnder(List<Point> arc)
            {
                int under = 0, total = 0;
                for (int k = 1; k < arc.Count - 1; k++)
                {
                    total++;
                    if (underBrush(arc[k])) under++;
                }
                return total == 0 ? 0.0 : (double)under / total;
            }

            double fa = FracUnder(a), fb = FracUnder(b);
            if (Math.Max(fa, fb) <= 0.0) return null;
            return fa >= fb ? a : b;
        }
    }
}