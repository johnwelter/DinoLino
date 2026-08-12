using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Runtime.Remoting.Contexts;
using System.Windows;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Shared pipeline for the outline-editing brushes.
    ///
    /// A stroke rasterizes the active outline into a mask, lets the tool edit the
    /// pixels under the brush, then re-traces the mask and splices only the
    /// affected arc back into the polyline, so untouched vertices never move.
    ///
    /// Erase and Push differ in exactly two places: which pixels they write, and
    /// — because Push can write pixels the outline does not already have — how far
    /// the working crop has to reach. Everything else is shared, which is why they
    /// live in one file. The polyline is meant to move the same way outward as it
    /// does inward, and inheritance enforces that rather than leaving it to two
    /// copies of the same code staying in step by hand.
    /// </summary>
    public abstract class MaskBrushTool : ObservableToolBase
    {
        /// <summary>Smallest edited mask still worth tracing, in pixels.</summary>
        private const int MinimumMaskPixels = 9;

        /// <summary>Traced boundaries shorter than this are noise, not a shape.</summary>
        private const int MinimumBoundaryPoints = 8;

        /// <summary>Ceiling on the working crop, so a deep zoom can't allocate a huge mask.</summary>
        private const long MaxCropPixels = 16_000_000;

        /// <summary>How much wider than the brush the splice looks for its join, in canvas pixels.</summary>
        private const double SpliceMargin = 2.0;

        // UI-thread-only processor used for fill and boundary tracing during a stroke.
        private readonly OutlineProcessor _processor = new OutlineProcessor();

        protected MaskBrushTool(IOutlineToolContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>The tool's window into the outline mode.</summary>
        protected IOutlineToolContext Context { get; }

        /// <summary>Raised after the outline geometry has been modified by a drag.</summary>
        public event Action OutlineEdited;

        private double _brushRadius = 20;

        /// <summary>Brush radius in canvas pixels.</summary>
        public double BrushRadius
        {
            get => _brushRadius;
            set => SetField(ref _brushRadius, value);
        }

        // ---- What a subclass supplies ----

        /// The crop always covers the outline. Override to true when the tool can
        /// write pixels the outline does not already have, so the crop widens to
        /// cover the brush disc wherever the cursor happens to be.
        protected virtual bool CropCoversBrushDisc => false;

        /// Edits the rasterized outline mask under the brush. Returning false
        /// abandons the stroke: the polyline is left exactly as it was and
        /// OutlineEdited is not raised.
        protected abstract bool ApplyBrush(bool[] mask, BrushStroke stroke);

        // ---- Stroke geometry ----

        /// <summary>
        /// The crop one stroke works in: where it sits in the full image, how big it
        /// is, and where the brush falls inside it. Handed to ApplyBrush so a
        /// subclass only has to decide which pixels it wants changed.
        /// </summary>
        protected readonly struct BrushStroke
        {
            /// <summary>Canvas-to-image mapping in force for this stroke.</summary>
            public readonly ViewTransform Transform;

            /// <summary>Cursor position, canvas space.</summary>
            public readonly Vector2 MouseCanvas;

            /// <summary>Full-image position of the crop's top-left pixel.</summary>
            public readonly int OriginX;
            public readonly int OriginY;

            /// <summary>Crop dimensions, in image pixels.</summary>
            public readonly int Width;
            public readonly int Height;

            /// <summary>Brush radius in image pixels along each axis (canvas radius ÷ zoom).</summary>
            public readonly double RadiusImageX;
            public readonly double RadiusImageY;

            /// <summary>Brush radius in canvas pixels — what the user actually sees.</summary>
            public readonly double RadiusCanvas;

            // Brush centre in crop space, kept so DiscBounds stays a cheap property.
            private readonly double _centreX;
            private readonly double _centreY;

            internal BrushStroke(ViewTransform transform, Vector2 mouseCanvas,
                int originX, int originY, int width, int height,
                double radiusImageX, double radiusImageY, double radiusCanvas)
            {
                Transform = transform;
                MouseCanvas = mouseCanvas;
                OriginX = originX;
                OriginY = originY;
                Width = width;
                Height = height;
                RadiusImageX = radiusImageX;
                RadiusImageY = radiusImageY;
                RadiusCanvas = radiusCanvas;

                Point centre = transform.CanvasToImage(
                    new Point(mouseCanvas.X, mouseCanvas.Y), originX, originY);
                _centreX = centre.X;
                _centreY = centre.Y;
            }

            /// Crop-space bounding box of the brush disc, clamped to the crop. The
            /// one-pixel slack costs almost nothing and guarantees rounding can
            /// never drop a covered pixel off the edge of the scan.
            public (int x0, int y0, int x1, int y1) DiscBounds =>
            (
                Math.Max(0, (int)Math.Floor(_centreX - RadiusImageX) - 1),
                Math.Max(0, (int)Math.Floor(_centreY - RadiusImageY) - 1),
                Math.Min(Width - 1, (int)Math.Ceiling(_centreX + RadiusImageX) + 1),
                Math.Min(Height - 1, (int)Math.Ceiling(_centreY + RadiusImageY) + 1)
            );

            /// <summary>True when the centre of crop pixel (x, y) falls under the brush.</summary>
            public bool Covers(int x, int y) =>
                IsWithin(new Point(x + 0.5, y + 0.5), RadiusCanvas);

            /// Distance test for an arbitrary crop-space point, measured in CANVAS
            /// space so the brush stays circular on screen whatever the zoom does to
            /// the two image axes. The splice passes a slightly wider radius than
            /// the brush itself.
            public bool IsWithin(Point cropPoint, double radiusCanvas)
            {
                Point canvasPoint = Transform.ImageToCanvas(cropPoint, OriginX, OriginY);
                double dx = canvasPoint.X - MouseCanvas.X;
                double dy = canvasPoint.Y - MouseCanvas.Y;
                return dx * dx + dy * dy <= radiusCanvas * radiusCanvas;
            }
        }

        // ---- Stroke pipeline ----

        /// <summary>Applies one brush stroke at the current mouse position.</summary>
        public void ProcessDrag(Vector2 mousePos)
        {
            var polyline = Context.ActivePolyline;
            if (polyline == null) return;

            ViewTransform t = Context.Transform;
            if (!t.IsValid) return;

            if (!TryBeginDrag()) return;
            try
            {
                var srcPoints = polyline.Points;
                if (srcPoints.Count < 3) return;

                // Work in image space so the mask resolution is independent of zoom.
                var img = new List<Point>(srcPoints.Count);
                foreach (var cp in srcPoints)
                    img.Add(t.CanvasToImage(cp));
                PolylineGeometry.StripClosureDuplicate(img);
                if (img.Count < 3) return;

                double rImgX = BrushRadius / t.ScaleX;
                double rImgY = BrushRadius / t.ScaleY;
                int pad = (int)Math.Ceiling(Math.Max(rImgX, rImgY)) + 2;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var p in img)
                {
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                }

                // Tools that add pixels reach outside the outline, so their crop has
                // to cover the brush disc too. (Erase never needs this: it can only
                // clear pixels the outline already has.)
                if (CropCoversBrushDisc)
                {
                    Point brushImg = t.CanvasToImage(new Point(mousePos.X, mousePos.Y));
                    if (brushImg.X - rImgX < minX) minX = brushImg.X - rImgX;
                    if (brushImg.X + rImgX > maxX) maxX = brushImg.X + rImgX;
                    if (brushImg.Y - rImgY < minY) minY = brushImg.Y - rImgY;
                    if (brushImg.Y + rImgY > maxY) maxY = brushImg.Y + rImgY;
                }

                int ox = (int)Math.Floor(minX) - pad;
                int oy = (int)Math.Floor(minY) - pad;
                int w = (int)Math.Ceiling(maxX) - ox + pad;
                int h = (int)Math.Ceiling(maxY) - oy + pad;
                if (w < 3 || h < 3 || (long)w * h > MaxCropPixels) return;

                var local = new List<Point>(img.Count);
                foreach (var p in img) local.Add(new Point(p.X - ox, p.Y - oy));

                // Rasterize the current outline into a filled mask, then let the tool
                // edit the pixels under the brush.
                bool[] mask = PolylineGeometry.RasterizePolygon(local, w, h);

                var stroke = new BrushStroke(t, mousePos, ox, oy, w, h, rImgX, rImgY, BrushRadius);
                if (!ApplyBrush(mask, stroke)) return;

                // Close small holes and keep the largest connected region so the
                // outline stays single-part.
                mask = _processor.FillHoles(mask, w, h);
                mask = _processor.KeepLargestComponent(mask, w, h);
                if (!_processor.HasMinimumPixels(mask, MinimumMaskPixels)) return;

                // Trace the edited mask back into a boundary polyline.
                var boundary = _processor.TraceBoundary(mask, w, h);
                if (boundary.Count < MinimumBoundaryPoints) return;

                // Splice only the affected arc back in, so untouched vertices stay put.
                List<Point> newLocal = SpliceBrushArc(local, boundary, stroke);
                if (newLocal == null)
                {
                    // Fall back to full simplification only when the edited region
                    // cannot be isolated cleanly.
                    newLocal = GeometryCalculations.DouglasPeucker(boundary, Context.SimplifyEpsilon);
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
        // One implementation covers both directions, because the geometry is
        // symmetric: the old vertices under the brush are the ones the disc
        // swallowed (erase removed them, push made them interior), and the traced
        // boundary's under-brush arc is the replacement — a carved bite one way, an
        // outward bulge the other.

        /// <summary>Replaces only the outline arc affected by the brush.</summary>
        private List<Point> SpliceBrushArc(List<Point> oldLocal, List<Point> dense, BrushStroke stroke)
        {
            int n = oldLocal.Count;
            if (n < 3 || dense.Count < 3) return null;

            // Test slightly wider than the brush so the join lands on stable geometry.
            double rTest = BrushRadius + SpliceMargin;
            bool UnderBrush(Point pLocal) => stroke.IsWithin(pLocal, rTest);

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
                // The brush hit an edge between vertices: anchor the splice to the
                // nearest edge instead.
                int bestEdge = -1;
                double bestDist = double.MaxValue;

                for (int i = 0; i < n; i++)
                {
                    double d = PointToSegmentCanvasDist2(
                        oldLocal[i], oldLocal[(i + 1) % n], stroke);
                    if (d < bestDist) { bestDist = d; bestEdge = i; }
                }

                if (bestEdge < 0) return null;
                aIdx = bestEdge;
                bIdx = (bestEdge + 1) % n;
            }

            Point a = oldLocal[aIdx];
            Point b = oldLocal[bIdx];

            // Extract the two possible arcs on the traced boundary and keep the one
            // actually under the brush.
            int aJ = PolylineGeometry.NearestIndex(dense, a);
            int bJ = PolylineGeometry.NearestIndex(dense, b);
            if (aJ < 0 || bJ < 0 || aJ == bJ) return null;

            List<Point> arc = ChooseUnderBrushArc(
                PolylineGeometry.ExtractArc(dense, aJ, bJ, true),
                PolylineGeometry.ExtractArc(dense, aJ, bJ, false),
                UnderBrush);
            if (arc == null) return null;

            var arcSimpl = GeometryCalculations.DouglasPeucker(arc, Context.SimplifyEpsilon);
            if (arcSimpl.Count < 2) return null;

            // Keep the untouched portion B -> ... -> A, then insert the simplified
            // replacement arc.
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

        /// <summary>Squared canvas-space distance from the brush centre to a segment.</summary>
        private static double PointToSegmentCanvasDist2(Point p0, Point p1, BrushStroke stroke)
        {
            Point a = stroke.Transform.ImageToCanvas(p0, stroke.OriginX, stroke.OriginY);
            Point b = stroke.Transform.ImageToCanvas(p1, stroke.OriginX, stroke.OriginY);

            double ax = a.X, ay = a.Y;
            double vx = b.X - ax, vy = b.Y - ay;
            double wx = stroke.MouseCanvas.X - ax, wy = stroke.MouseCanvas.Y - ay;

            double len2 = vx * vx + vy * vy;
            double tt = len2 > 1e-9 ? (wx * vx + wy * vy) / len2 : 0.0;
            if (tt < 0.0) tt = 0.0; else if (tt > 1.0) tt = 1.0;

            double cx = ax + tt * vx, cy = ay + tt * vy;
            double dx = stroke.MouseCanvas.X - cx, dy = stroke.MouseCanvas.Y - cy;
            return dx * dx + dy * dy;
        }

        /// <summary>Picks the arc whose interior is actually under the brush.</summary>
        private static List<Point> ChooseUnderBrushArc(
            List<Point> a, List<Point> b, Func<Point, bool> underBrush)
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

    /// <summary>
    /// Erases part of the active outline by carving the brush area out of the shape
    /// and re-tracing the result.
    /// </summary>
    public sealed class EraseTool : MaskBrushTool
    {
        public EraseTool(IOutlineToolContext context) : base(context) { }

        // CropCoversBrushDisc stays false: erasing can only clear pixels the
        // outline already has, so the crop around the outline always suffices.

        protected override bool ApplyBrush(bool[] mask, BrushStroke stroke)
        {
            var (x0, y0, x1, y1) = stroke.DiscBounds;
            bool anyCleared = false;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * stroke.Width;
                for (int x = x0; x <= x1; x++)
                {
                    int i = row + x;

                    // Cheap rejection first: only filled pixels are worth the
                    // canvas-space distance test.
                    if (!mask[i]) continue;
                    if (!stroke.Covers(x, y)) continue;

                    mask[i] = false;
                    anyCleared = true;
                }
            }

            return anyCleared;
        }
    }

    /// <summary>
    /// Expands the active outline by adding the brush area to the shape and
    /// re-tracing the result — the exact inverse of EraseTool.
    /// </summary>
    public sealed class PushTool : MaskBrushTool
    {
        public PushTool(IOutlineToolContext context) : base(context) { }

        /// Pushing writes pixels outside the current outline, so the crop must
        /// follow the cursor rather than just bounding the shape.
        protected override bool CropCoversBrushDisc => true;

        protected override bool ApplyBrush(bool[] mask, BrushStroke stroke)
        {
            // Keep the outline on the photo: when the image dimensions are known,
            // pixels past its border are never added.
            int imgW = Context.ImagePixelWidth;
            int imgH = Context.ImagePixelHeight;
            bool clampToImage = imgW > 0 && imgH > 0;

            var (x0, y0, x1, y1) = stroke.DiscBounds;
            bool anyAdded = false;
            bool touchesOutline = false;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * stroke.Width;
                for (int x = x0; x <= x1; x++)
                {
                    if (!stroke.Covers(x, y)) continue;

                    int i = row + x;

                    // Each pixel is visited exactly once, so a true here is original
                    // outline, not something this pass just added.
                    if (mask[i]) { touchesOutline = true; continue; }

                    if (clampToImage)
                    {
                        int fullX = x + stroke.OriginX, fullY = y + stroke.OriginY;
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
            return anyAdded && touchesOutline;
        }
    }
}