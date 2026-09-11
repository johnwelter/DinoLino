using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Shared pipeline for the outline-editing brushes.
    ///
    /// A stroke rasterizes the active outline into a mask, lets the tool edit the
    /// pixels under the brush, and re-traces the result. The polyline is then
    /// rebuilt from two sources: its own vertices wherever the mask did not
    /// change, and the traced boundary wherever it did. Vertices outside the
    /// brush path are written back exactly as they were, so nothing the cursor
    /// did not reach can move.
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

        /// <summary>
        /// Distance, in image pixels, within which a changed mask pixel marks a
        /// vertex or edge as edited. Rasterizing and tracing each wander by up to a
        /// pixel, so geometry closer than this to an edit shares boundary with it and
        /// is rebuilt from the trace along with it.
        /// </summary>
        private const int ChangeRadius = 2;

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

                // The vertices exactly as the canvas holds them, index-aligned with
                // img. Untouched vertices are copied straight from this list, so they
                // never pass through a coordinate round trip.
                var oldCanvas = new List<Point>(img.Count);
                for (int i = 0; i < img.Count; i++)
                    oldCanvas.Add(srcPoints[i]);

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

                // The outline as the mask sees it: filled, hole-free, single-part.
                // Normalizing this reference the same way the edited mask is
                // normalized below guarantees that every pixel differing between the
                // two was changed by the brush, directly or as a consequence of it.
                bool[] before = PolylineGeometry.RasterizePolygon(local, w, h);
                before = _processor.FillHoles(before, w, h);
                before = _processor.KeepLargestComponent(before, w, h);

                var stroke = new BrushStroke(t, mousePos, ox, oy, w, h, rImgX, rImgY, BrushRadius);

                bool[] after = (bool[])before.Clone();
                if (!ApplyBrush(after, stroke)) return;

                // Close small holes and keep the largest connected region so the
                // outline stays single-part.
                after = _processor.FillHoles(after, w, h);
                after = _processor.KeepLargestComponent(after, w, h);
                if (!_processor.HasMinimumPixels(after, MinimumMaskPixels)) return;

                // Trace the edited mask back into a boundary polyline.
                var boundary = _processor.TraceBoundary(after, w, h);
                if (boundary.Count < MinimumBoundaryPoints) return;

                // Only the edited runs are taken from the trace. When they can't be
                // isolated cleanly the stroke is abandoned and the outline stays put.
                List<Point> newCanvas = SpliceEditedRuns(oldCanvas, local, boundary, before, after, stroke);
                if (newCanvas == null || newCanvas.Count < 3) return;

                srcPoints.Clear();
                foreach (var p in newCanvas)
                    srcPoints.Add(p);
                srcPoints.Add(newCanvas[0]);

                OutlineEdited?.Invoke();
            }
            finally
            {
                EndDrag();
            }
        }

        // ---- Splicing ----
        // The traced boundary is consulted only where the mask changed. Each old
        // vertex is classified as untouched or edited; untouched vertices are kept
        // verbatim, and every maximal run of edited vertices (or a single bitten
        // edge between two untouched vertices) is replaced by the stretch of the
        // traced boundary running between its untouched neighbours. The geometry
        // is symmetric between the tools: the run is what the disc swallowed, and
        // the replacement is a carved bite one way or an outward bulge the other.
        // Several runs can be spliced in one stroke, which covers a brush that
        // touches the outline in two places, a bite that severs an appendage, and a
        // push that bridges a concavity.

        /// <summary>
        /// Rebuilds the outline in canvas space from the untouched vertices and the
        /// traced boundary. Returns null when nothing changed or when the edited
        /// runs can't be spliced cleanly, in which case the outline is left alone.
        /// </summary>
        private List<Point> SpliceEditedRuns(List<Point> oldCanvas, List<Point> oldLocal,
            List<Point> dense, bool[] before, bool[] after, BrushStroke stroke)
        {
            int n = oldLocal.Count;
            if (n < 3 || dense.Count < 3) return null;

            int w = stroke.Width, h = stroke.Height;

            // True when any mask pixel within ChangeRadius of a crop-space point
            // differs between the two masks.
            bool NearChange(double x, double y)
            {
                int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
                for (int yy = cy - ChangeRadius; yy <= cy + ChangeRadius; yy++)
                {
                    if ((uint)yy >= (uint)h) continue;
                    int row = yy * w;
                    for (int xx = cx - ChangeRadius; xx <= cx + ChangeRadius; xx++)
                    {
                        if ((uint)xx >= (uint)w) continue;
                        if (before[row + xx] != after[row + xx]) return true;
                    }
                }
                return false;
            }

            // Walks the open edge between two untouched vertices at one-pixel steps.
            // The windows overlap at that spacing, so no changed pixel along the
            // edge can slip between samples.
            bool EdgeNearChange(Point p, Point q)
            {
                double dx = q.X - p.X, dy = q.Y - p.Y;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy)));
                for (int step = 1; step < steps; step++)
                {
                    double f = (double)step / steps;
                    if (NearChange(p.X + f * dx, p.Y + f * dy)) return true;
                }
                return false;
            }

            // A vertex counts as edited when it sits under the brush (tested slightly
            // wider than the disc so the joins land on stable geometry) or next to a
            // pixel the stroke changed. The second test is what catches vertices the
            // disc never covered but the edit removed anyway, such as those on an
            // appendage the bite cut off or in a pocket a push sealed shut.
            double rTest = BrushRadius + SpliceMargin;
            var kept = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                Point p = oldLocal[i];
                if (stroke.IsWithin(p, rTest) || NearChange(p.X, p.Y)) continue;
                kept.Add(i);
            }

            List<Point> result;
            int m = kept.Count;

            if (m == 0)
            {
                // Every vertex was in the brush path, so the whole outline is
                // legitimately rebuilt from the trace.
                var whole = GeometryCalculations.DouglasPeucker(dense, Context.SimplifyEpsilon);
                if (whole == null) return null;
                result = ToCanvas(whole, stroke);
            }
            else
            {
                // Replacement arcs must run around the traced loop the same way the
                // old outline runs between its vertices.
                bool forward = (SignedArea(oldLocal) >= 0) == (SignedArea(dense) >= 0);

                result = new List<Point>(n + 16);
                bool anyEdited = false;

                for (int g = 0; g < m; g++)
                {
                    int a = kept[g];
                    int b = kept[(g + 1) % m];
                    result.Add(oldCanvas[a]);

                    // An untouched edge between two untouched vertices is kept as is.
                    bool adjacent = m > 1 && (a + 1) % n == b;
                    if (adjacent && !EdgeNearChange(oldLocal[a], oldLocal[b])) continue;

                    anyEdited = true;

                    int ja = PolylineGeometry.NearestIndex(dense, oldLocal[a]);
                    int jb = PolylineGeometry.NearestIndex(dense, oldLocal[b]);
                    if (ja < 0 || jb < 0) return null;

                    List<Point> arc;
                    if (ja != jb)
                        arc = PolylineGeometry.ExtractArc(dense, ja, jb, forward);
                    else if (m == 1)
                        arc = ExtractFullLoop(dense, ja, forward);
                    else
                        continue; // both ends meet the trace at one point: a direct edge suffices

                    var simplified = GeometryCalculations.DouglasPeucker(arc, Context.SimplifyEpsilon);
                    if (simplified == null) return null;

                    // The arc's own endpoints coincide with the untouched vertices on
                    // either side, which are already part of the result.
                    for (int k = 1; k < simplified.Count - 1; k++)
                        result.Add(stroke.Transform.ImageToCanvas(simplified[k], stroke.OriginX, stroke.OriginY));
                }

                if (!anyEdited) return null;
            }

            if (result.Count < 3) return null;
            if (PolylineGeometry.HasSelfIntersection(result)) return null;

            return result;
        }

        /// <summary>Maps crop-space points back to canvas space.</summary>
        private static List<Point> ToCanvas(List<Point> local, BrushStroke stroke)
        {
            var canvas = new List<Point>(local.Count);
            foreach (var p in local)
                canvas.Add(stroke.Transform.ImageToCanvas(p, stroke.OriginX, stroke.OriginY));
            return canvas;
        }

        /// <summary>The whole traced loop, starting and ending at one index.</summary>
        private static List<Point> ExtractFullLoop(List<Point> pts, int start, bool forward)
        {
            int n = pts.Count;
            var loop = new List<Point>(n + 1);
            for (int s = 0; s <= n; s++)
            {
                int idx = forward ? (start + s) % n : ((start - s) % n + n) % n;
                loop.Add(pts[idx]);
            }
            return loop;
        }

        /// <summary>Shoelace area of a closed ring; the sign encodes winding direction.</summary>
        private static double SignedArea(IList<Point> pts)
        {
            double sum = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Point p = pts[i], q = pts[(i + 1) % n];
                sum += p.X * q.Y - q.X * p.Y;
            }
            return 0.5 * sum;
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
            // KeepLargestComponent on a small outline).
            return anyAdded && touchesOutline;
        }
    }
}