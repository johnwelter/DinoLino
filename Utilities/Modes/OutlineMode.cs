using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    public class OutlineMode : WorkMode
    {
        public override string TabName => "Outline";
        public override UserControl CreateControlPanel() => new OutlineControlPanel(this);
        public override bool IsStartingNewOperation => true;

        #region setting outline mode

        private bool _drawOutlineMode = true;
        public bool DrawOutlineMode
        {
            get => _drawOutlineMode;
            set
            {
                _drawOutlineMode = value;
                OnPropertyChanged(nameof(DrawOutlineMode));
            }
        }

        private bool _eraseOutlineMode = false;
        public bool EraseOutlineMode
        {
            get => _eraseOutlineMode;
            set
            {
                _eraseOutlineMode = value;
                OnPropertyChanged(nameof(EraseOutlineMode));
            }
        }

        private bool _smoothOutlineMode = false;
        public bool SmoothOutlineMode
        {
            get => _smoothOutlineMode;
            set
            {
                _smoothOutlineMode = value;
                OnPropertyChanged(nameof(SmoothOutlineMode));
            }
        }

        private bool _outlineMetadataMode = false;
        public bool OutlineMetadataMode
        {
            get => _outlineMetadataMode;
            set
            {
                if (_outlineMetadataMode == value)
                    return;

                _outlineMetadataMode = value;
                OnPropertyChanged(nameof(OutlineMetadataMode));

                // Auto-generate metadata when entering metadata mode
                if (_outlineMetadataMode)
                    GenerateMetadata();
            }
        }
        #endregion

        #region get image data
        private BitmapImage _sourceImage;
        public BitmapImage SourceImage
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
            set { _minAreaPixels = value; OnPropertyChanged(nameof(MinAreaPixels)); }
        }

        private void CacheSourcePixels()
        {
            if (_sourceImage == null) { _cachedPixels = null; return; }

            var formatted = new FormatConvertedBitmap(
                _sourceImage,
                PixelFormats.Bgra32,
                null,
                0);

            _cachedWidth = formatted.PixelWidth;
            _cachedHeight = formatted.PixelHeight;

            _cachedBpp = 4;
            _cachedStride = _cachedWidth * 4;

            _cachedPixels = new byte[_cachedStride * _cachedHeight];
            formatted.CopyPixels(_cachedPixels, _cachedStride, 0);

            _bgColors = EstimateBackgroundColors();
            _backgroundMask = BuildBackgroundMask();
        }


        private (double r, double g, double b)[] _bgColors;
        // Samples one pixel at each corner for use as individual background seeds.
        private (double r, double g, double b)[] EstimateBackgroundColors()
        {
            return new[]
            {
        ToDouble(ReadPixel(0, 0)),
        ToDouble(ReadPixel(_cachedWidth - 1, 0)),
        ToDouble(ReadPixel(0, _cachedHeight - 1)),
        ToDouble(ReadPixel(_cachedWidth - 1, _cachedHeight - 1))
    };
        }

        private (double r, double g, double b) ToDouble((byte r, byte g, byte b) px)
            => (px.r, px.g, px.b);

        private bool[] _backgroundMask;
        private bool[] BuildBackgroundMask(double threshold = 25)
        {
            int w = _cachedWidth;
            int h = _cachedHeight;

            bool[] background = new bool[w * h];
            var queue = new Queue<(int x, int y)>();


            void TryAdd(int x, int y, int colorIndex)
            {
                int i = y * w + x;
                if (background[i]) return;

                var (r, g, b) = ReadPixel(x, y);
                var (br, bg, bb) = _bgColors[colorIndex];

                double dist = PerceptualDistance(
                    r, g, b,
                    (byte)Math.Max(0, Math.Min(255, br)),
                    (byte)Math.Max(0, Math.Min(255, bg)),
                    (byte)Math.Max(0, Math.Min(255, bb)));

                if (dist > threshold)
                    return;

                background[i] = true;
                queue.Enqueue((x, y));
            }

            // Seed from all four corners using their respective reference colors.
            // Each border edge uses the color of its nearest corner.
            for (int x = 0; x < w; x++)
            {
                // Top edge: left half uses corner 0, right half uses corner 1
                int topColor = x < w / 2 ? 0 : 1;
                TryAdd(x, 0, topColor);

                // Bottom edge: left half uses corner 2, right half uses corner 3
                int botColor = x < w / 2 ? 2 : 3;
                TryAdd(x, h - 1, botColor);
            }
            for (int y = 1; y < h - 1; y++)
            {
                // Left edge: top half uses corner 0, bottom half uses corner 2
                int leftColor = y < h / 2 ? 0 : 2;
                TryAdd(0, y, leftColor);

                // Right edge: top half uses corner 1, bottom half uses corner 3
                int rightColor = y < h / 2 ? 1 : 3;
                TryAdd(w - 1, y, rightColor);
            }

            // BFS flood fill — each queued pixel carries the color index it was seeded with.
            // We track which color index each background pixel was accepted under so the
            // flood respects the correct reference as it spreads inward.
            // Since background[] prevents revisiting, color bleed between zones is harmless.
            int iterations = 0;
            while (queue.Count > 0)
            {
                if ((queue.Count & 1023) == 0)
                    ThrowIfCancelled();

                var (cx, cy) = queue.Dequeue();

                if ((iterations++ & 1023) == 0)
                    ThrowIfCancelled();

                // Determine which corner color governs this pixel by quadrant.
                int colorIndex = (cx < w / 2 ? 0 : 1) + (cy < h / 2 ? 0 : 2);

                // Left
                if (cx > 0)
                {
                    int nx = cx - 1;
                    int ny = cy;
                    int neighborColor = (nx < w / 2 ? 0 : 1) + (ny < h / 2 ? 0 : 2);
                    TryAdd(nx, ny, neighborColor);
                }

                // Right
                if (cx < w - 1)
                {
                    int nx = cx + 1;
                    int ny = cy;
                    int neighborColor = (nx < w / 2 ? 0 : 1) + (ny < h / 2 ? 0 : 2);
                    TryAdd(nx, ny, neighborColor);
                }

                // Up
                if (cy > 0)
                {
                    int nx = cx;
                    int ny = cy - 1;
                    int neighborColor = (nx < w / 2 ? 0 : 1) + (ny < h / 2 ? 0 : 2);
                    TryAdd(nx, ny, neighborColor);
                }

                // Down
                if (cy < h - 1)
                {
                    int nx = cx;
                    int ny = cy + 1;
                    int neighborColor = (nx < w / 2 ? 0 : 1) + (ny < h / 2 ? 0 : 2);
                    TryAdd(nx, ny, neighborColor);
                }
            }

            return background;
        }

        private readonly struct ImageSnapshot
        {
            public readonly byte[] Pixels;
            public readonly bool[] BgMask;
            public readonly int Width;
            public readonly int Height;
            public readonly int Stride;
            public readonly int Bpp;

            public ImageSnapshot(
                byte[] pixels,
                bool[] bgMask,
                int w,
                int h,
                int stride,
                int bpp)
            {
                Pixels = pixels;
                BgMask = bgMask;
                Width = w;
                Height = h;
                Stride = stride;
                Bpp = bpp;
            }
        }

        //-----bounding box crop-----//
        private (int x0, int y0, int x1, int y1) GetMaskBounds(
    bool[] mask, int w, int h, int margin = 2)
        {
            int x0 = w, y0 = h, x1 = 0, y1 = 0;

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (!mask[row + x]) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }

            if (x0 > x1) return (0, 0, 0, 0); // empty mask

            x0 = Math.Max(0, x0 - margin);
            y0 = Math.Max(0, y0 - margin);
            x1 = Math.Min(w - 1, x1 + margin);
            y1 = Math.Min(h - 1, y1 + margin);

            return (x0, y0, x1, y1);
        }

        // Crops a flat mask to a bounding box sub-region,
        // returning the cropped mask and its new width/height.
        private (bool[] croppedMask, int cw, int ch) CropMask(
            bool[] mask, int w, int x0, int y0, int x1, int y1)
        {
            int cw = x1 - x0 + 1;
            int ch = y1 - y0 + 1;
            var cropped = new bool[cw * ch];

            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    cropped[y * cw + x] = mask[(y0 + y) * w + (x0 + x)];

            return (cropped, cw, ch);
        }

        // Converts a canvas-space point back to image-space (pixel coordinates).
        // Inverse of the transform applied when building the polyline from simplified boundary points.
        private Point CanvasToImage(Point p)
        {
            return new Point(
                (p.X - OffsetX) / ScaleX,
                (p.Y - OffsetY) / ScaleY);
        }
        #endregion

        #region shared functions and variables
        public override void ClearMetadata()
        {
            AspectRatioResult = 0;
            PerimeterAreaRatioResult = 0;
            CircularityResult = 0;
            SolidityResult = 0;
            SumTurningAnglesResult = 0;
            MeanTurningAngleResult = 0;
            VarianceTurningAnglesResult = 0;
            EFDCoefficientsResult = null;
            MetadataSummary = "";
        }

        protected override void OnOperationUndone(WorkOperation operation)
        {
            if (operation is OutlineOperation)
                ClearMetadata();
        }

        protected override void OnOperationRedone(WorkOperation operation)
        {
            if (operation is OutlineOperation op)
                op.ApplyMetadataToMode();
        }

        //-----Reusable buffers-----//
        private int[] _distBuffer = Array.Empty<int>();

        private void EnsureBuffers(int size)
        {
            if (_distBuffer.Length < size)
            {
                _distBuffer = new int[size];
                _queueBuffer = new int[size * 2]; // queue can exceed total in BFS
            }
        }

        private int CountCardinalNeighbors(bool[] mask, int x, int y, int width, int height)
        {
            int count = 0;
            int i = y * width + x;

            if (x > 0 && mask[i - 1]) count++;
            if (x < width - 1 && mask[i + 1]) count++;
            if (y > 0 && mask[i - width]) count++;
            if (y < height - 1 && mask[i + width]) count++;

            return count;
        }

        private int[] _queueBuffer;
        private int _qHead;
        private int _qTail;

        private void EnsureQueue(int capacity)
        {
            if (_queueBuffer == null || _queueBuffer.Length < capacity)
                _queueBuffer = new int[capacity];
        }
        #endregion
        #region draw outline

        // =====================
        // USER PARAMETERS
        // =====================
        private double _tolerance = 30;
        public double Tolerance
        {
            get => _tolerance;
            set { _tolerance = value; OnPropertyChanged(nameof(Tolerance)); }
        }

        private double _gradientLeniency = 15;
        public double GradientLeniency
        {
            get => _gradientLeniency;
            set { _gradientLeniency = value; OnPropertyChanged(nameof(GradientLeniency)); }
        }

        private double _edgeThreshold = 40;
        public double EdgeThreshold
        {
            get => _edgeThreshold;
            set { _edgeThreshold = value; OnPropertyChanged(nameof(EdgeThreshold)); }
        }

        private double _simplifyEpsilon = 1.5;
        public double SimplifyEpsilon
        {
            get => _simplifyEpsilon;
            set { _simplifyEpsilon = value; OnPropertyChanged(nameof(SimplifyEpsilon)); }
        }

        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public double OffsetX { get; set; } = 0;
        public double OffsetY { get; set; } = 0;

        public override void Reset()
        {
            base.Reset();
            _activePolyline = null;
            _rawEFDCoefficients = null;
            ClearMetadata();
            ClearEFDPreview();
        }


        // =====================
        // PROCESS CLICK
        // =====================
        public Action<List<UIElement>> OnOutlineReady;

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            BeginOperation();

            if (_eraseOutlineMode || _smoothOutlineMode || _outlineMetadataMode)
                return new List<UIElement>();

            if (_cachedPixels == null) return new List<UIElement>();

            int px = (int)((mousePos.X - OffsetX) / ScaleX);
            int py = (int)((mousePos.Y - OffsetY) / ScaleY);

            if ((uint)px >= _cachedWidth || (uint)py >= _cachedHeight)
                return new List<UIElement>();

            var token = CancellationToken;

            // Snapshot everything the background thread needs
            // so it doesn't touch UI-thread objects
            var snap = new ImageSnapshot(_cachedPixels, _backgroundMask, _cachedWidth, _cachedHeight, _cachedStride, _cachedBpp);
            double scaleX = ScaleX, scaleY = ScaleY;
            double offsetX = OffsetX, offsetY = OffsetY;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    (byte sr, byte sg, byte sb) = ReadPixel(px, py, snap);
                    bool[] raw = FloodFill(px, py, sr, sg, sb, snap);

                    if (!HasMinimumPixels(raw, 1)) return;
                    token.ThrowIfCancellationRequested();

                    // Crop before FillHoles — avoids running hole-filling on the full image.
                    var (bx0, by0, bx1, by1) = GetMaskBounds(raw, snap.Width, snap.Height, margin: 4);
                    var (croppedRaw, cw, ch) = CropMask(raw, snap.Width, bx0, by0, bx1, by1);
                    var croppedSnap = new ImageSnapshot(snap.Pixels, snap.BgMask, cw, ch, snap.Stride, snap.Bpp);

                    // FillHoles and ExtractLargestComponent now operate only on the small cropped region.
                    bool[] work = FillHoles(croppedRaw, croppedSnap);
                    work = ExtractLargestComponent(work, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    EnforceMinimumCorridorWidth(work, croppedSnap, minWidth: 3);
                    token.ThrowIfCancellationRequested();

                    SmoothBorderTopology(work, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    PruneDeadEnds(work, croppedSnap);
                    work = KeepLargestComponent(work, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    int cpx = px - bx0, cpy = py - by0;
                    bool[] tracedMask = PrepareMaskForTracing(work, croppedRaw, cpx, cpy, MinAreaPixels, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    List<Point> boundary = TraceBoundary(tracedMask, croppedSnap);
                    token.ThrowIfCancellationRequested();

                    if (!HasMinimumBorderLength(boundary, 8))
                    {
                        bool[] expanded = ExpandConnectedComponentToArea(
                            KeepComponentContainingSeed(croppedRaw, cpy * cw + cpx, croppedSnap),
                            cpy * cw + cpx, MinAreaPixels, croppedSnap);
                        token.ThrowIfCancellationRequested();
                        boundary = TraceBoundary(expanded, croppedSnap);
                    }

                    if (boundary.Count < 8) return;
                    token.ThrowIfCancellationRequested();

                    List<Point> simplified = GeometryCalculations.DouglasPeucker(boundary, _simplifyEpsilon);
                    if (simplified.Count < 3) return;
                    token.ThrowIfCancellationRequested();

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        var polyline = new Polyline
                        {
                            Stroke = this.LineColor,
                            StrokeThickness = 2,
                            FillRule = FillRule.EvenOdd
                        };

                        // Boundary points are in cropped coordinates — translate back to canvas
                        foreach (var p in simplified)
                            polyline.Points.Add(new Point(
                                (p.X + bx0) * ScaleX + OffsetX,
                                (p.Y + by0) * ScaleY + OffsetY));

                        polyline.Points.Add(new Point(
                            (simplified[0].X + bx0) * ScaleX + OffsetX,
                            (simplified[0].Y + by0) * ScaleY + OffsetY));

                        _activePolyline = polyline;
                        _preSmoothSnapshot = new List<Point>(polyline.Points);

                        var output = new List<UIElement> { polyline };

                        CommitOperation(new OutlineOperation
                        {
                            OperationKind = "Outline",
                            SourceMode = this,
                            Elements = new List<UIElement>(output)
                        });

                        OnOutlineReady?.Invoke(output);
                    });
                }
                catch (OperationCanceledException) { }
            }, token);

            // Return empty immediately; results arrive via OnOutlineReady
            return new List<UIElement>();
        }


        // =====================
        // PIXEL ACCESS
        // =====================

        // Single consolidated pixel reader — handles grayscale (bpp==1) and color.
        private (byte r, byte g, byte b) ReadPixel(int x, int y, ImageSnapshot snap)
        {
            int i = y * snap.Stride + x * snap.Bpp;
            if (snap.Bpp == 1)
                return (snap.Pixels[i], snap.Pixels[i], snap.Pixels[i]);
            return (snap.Pixels[i + 2], snap.Pixels[i + 1], snap.Pixels[i]);
        }

        private (byte r, byte g, byte b) ReadPixel(int x, int y)
        {
            int i = y * _cachedStride + x * _cachedBpp;
            if (_cachedBpp == 1)
                return (_cachedPixels[i], _cachedPixels[i], _cachedPixels[i]);
            return (_cachedPixels[i + 2], _cachedPixels[i + 1], _cachedPixels[i]);
        }

        private double PerceptualDistance(
            byte r1, byte g1, byte b1,
            byte r2, byte g2, byte b2)
        {
            double dr = r1 - r2;
            double dg = g1 - g2;
            double db = b1 - b2;
            return Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db);
        }

        // =====================
        // FLOOD FILL
        // =====================
        private bool[] FloodFill(int startX, int startY, byte sr, byte sg, byte sb,
                         ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height;
            int total = w * h;

            bool[] inside = new bool[total];

            EnsureQueue(total);

            _qHead = 0;
            _qTail = 0;

            int seed = startY * w + startX;

            _queueBuffer[_qTail++] = seed;
            inside[seed] = true;

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];

                int cx = ci % w;
                int cy = ci / w;

                int pi = cy * snap.Stride + cx * snap.Bpp;

                byte cr, cg, cb;
                if (snap.Bpp == 1)
                {
                    cr = cg = cb = snap.Pixels[pi];
                }
                else
                {
                    cr = snap.Pixels[pi + 2];
                    cg = snap.Pixels[pi + 1];
                    cb = snap.Pixels[pi];
                }

                // helper local function to avoid repeating code
                void TryAdd(int nx, int ny)
                {
                    int ni = ny * w + nx;
                    if (inside[ni]) return;
                    if (snap.BgMask != null && snap.BgMask[ni]) return;

                    int npi = ny * snap.Stride + nx * snap.Bpp;

                    byte pr, pg, pb;
                    if (snap.Bpp == 1)
                    {
                        pr = pg = pb = snap.Pixels[npi];
                    }
                    else
                    {
                        pr = snap.Pixels[npi + 2];
                        pg = snap.Pixels[npi + 1];
                        pb = snap.Pixels[npi];
                    }

                    double seedDist = PerceptualDistance(pr, pg, pb, sr, sg, sb);
                    double neighborDist = PerceptualDistance(pr, pg, pb, cr, cg, cb);

                    bool strongEdge = neighborDist > _edgeThreshold;
                    bool seedMatch = seedDist <= _tolerance;
                    bool gradientMatch =
                        neighborDist <= _gradientLeniency &&
                        seedDist <= _tolerance * 6.0;

                    if ((!strongEdge || seedMatch) && (seedMatch || gradientMatch))
                    {
                        inside[ni] = true;
                        _queueBuffer[_qTail++] = ni;
                    }
                }

                // LEFT
                if (cx > 0) TryAdd(cx - 1, cy);

                // RIGHT
                if (cx < w - 1) TryAdd(cx + 1, cy);

                // UP
                if (cy > 0) TryAdd(cx, cy - 1);

                // DOWN
                if (cy < h - 1) TryAdd(cx, cy + 1);
            }

            return inside;
        }

        // =====================
        // FILL HOLES
        // =====================
        // Fills enclosed voids inside the segmented object.
        // Any empty region not connected to the image border
        // is considered a hole and converted to filled pixels.
        private bool[] FillHoles(bool[] mask, ImageSnapshot snap)
        {
            int w = snap.Width;
            int h = snap.Height;
            int total = w * h;

            bool[] exterior = new bool[total];

            EnsureQueue(total);
            _qHead = 0;
            _qTail = 0;

            // Helper: push into buffer
            void Enqueue(int i)
            {
                _queueBuffer[_qTail++] = i;
            }

            // Helper: pop from buffer
            int Dequeue()
            {
                return _queueBuffer[_qHead++];
            }

            // Seed flood fill from border background pixels
            void TrySeed(int x, int y)
            {
                int i = y * w + x;

                if (mask[i] || exterior[i])
                    return;

                exterior[i] = true;
                Enqueue(i);
            }

            // Top + bottom
            for (int x = 0; x < w; x++)
            {
                TrySeed(x, 0);
                TrySeed(x, h - 1);
            }

            // Left + right
            for (int y = 1; y < h - 1; y++)
            {
                TrySeed(0, y);
                TrySeed(w - 1, y);
            }

            // Flood exterior background
            while (_qHead < _qTail)
            {
                int ci = Dequeue();

                int cx = ci % w;
                int cy = ci / w;

                int row = cy * w;

                // LEFT
                if (cx > 0)
                {
                    int ni = row + (cx - 1);

                    if (!mask[ni] && !exterior[ni])
                    {
                        exterior[ni] = true;
                        Enqueue(ni);
                    }
                }

                // RIGHT
                if (cx < w - 1)
                {
                    int ni = row + (cx + 1);

                    if (!mask[ni] && !exterior[ni])
                    {
                        exterior[ni] = true;
                        Enqueue(ni);
                    }
                }

                // UP
                if (cy > 0)
                {
                    int ni = (cy - 1) * w + cx;

                    if (!mask[ni] && !exterior[ni])
                    {
                        exterior[ni] = true;
                        Enqueue(ni);
                    }
                }

                // DOWN
                if (cy < h - 1)
                {
                    int ni = (cy + 1) * w + cx;

                    if (!mask[ni] && !exterior[ni])
                    {
                        exterior[ni] = true;
                        Enqueue(ni);
                    }
                }
            }

            // Fill holes
            bool[] filled = (bool[])mask.Clone();

            for (int i = 0; i < total; i++)
            {
                if (!mask[i] && !exterior[i])
                    filled[i] = true;
            }

            return filled;
        }


        // =====================
        // EXTRACT LARGEST COMPONENT
        // =====================
        private bool[] ExtractLargestComponent(bool[] inside, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] visited = new bool[total];
            bool[] result = new bool[total];

            int bestSeed = -1, bestCount = 0;

            for (int start = 0; start < total; start++)
            {
                if (!inside[start] || visited[start]) continue;

                // BFS to count only, mark visited
                EnsureQueue(total);
                _qHead = 0; _qTail = 0;
                _queueBuffer[_qTail++] = start;
                visited[start] = true;
                int count = 0;

                while (_qHead < _qTail)
                {
                    int ci = _queueBuffer[_qHead++];
                    count++;
                    int cx = ci % w, cy = ci / w;
                    if (cx > 0) { int ni = ci - 1; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                    if (cx < w - 1) { int ni = ci + 1; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                    if (cy > 0) { int ni = ci - w; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                    if (cy < h - 1) { int ni = ci + w; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                }

                if (count > bestCount) { bestCount = count; bestSeed = start; }
            }

            if (bestSeed < 0) return result;

            // Second pass: flood from best seed into result
            EnsureQueue(total);
            _qHead = 0; _qTail = 0;
            _queueBuffer[_qTail++] = bestSeed;
            result[bestSeed] = true;

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];
                int cx = ci % w, cy = ci / w;
                if (cx > 0) { int ni = ci - 1; if (inside[ni] && !result[ni]) { result[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cx < w - 1) { int ni = ci + 1; if (inside[ni] && !result[ni]) { result[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cy > 0) { int ni = ci - w; if (inside[ni] && !result[ni]) { result[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cy < h - 1) { int ni = ci + w; if (inside[ni] && !result[ni]) { result[ni] = true; _queueBuffer[_qTail++] = ni; } }
            }

            return result;
        }

        // =====================
        // BOUNDARY TRACING
        // =====================
        private List<Point> TraceBoundary(bool[] inside, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height;

            int startX = -1, startY = -1;
            for (int i = 0; i < inside.Length && startX < 0; i++)
                if (inside[i]) { startX = i % w; startY = i / w; }

            if (startX < 0) return new List<Point>();

            var boundary = new List<Point>();
            int[] ndx = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] ndy = { 0, -1, -1, -1, 0, 1, 1, 1 };

            int cx = startX, cy = startY, dir = 4;

            for (int iterations = 0; iterations < w * h * 2; iterations++)
            {
                if ((iterations & 255) == 0) ThrowIfCancelled();

                boundary.Add(new Point(cx, cy));

                int checkDir = (dir + 6) % 8;
                bool found = false;
                for (int i = 0; i < 8; i++)
                {
                    int d = (checkDir + i) % 8;
                    int bx = cx + ndx[d];
                    int by = cy + ndy[d];
                    if ((uint)bx >= w || (uint)by >= h) continue;
                    if (!inside[by * w + bx]) continue;
                    dir = d; cx = bx; cy = by;
                    found = true;
                    break;
                }

                if (!found) break;
                if (cx == startX && cy == startY) break;
            }

            return boundary;
        }
        // =====================
        // NECK REMOVAL
        // =====================
        // Uses the crossing-number (connectivity number) test from digital topology.
        // For each filled pixel, counts how many 0->1 transitions occur when walking
        // clockwise around its 8 neighbors. A value of 1 means the pixel is interior
        // or a simple endpoint. A value >= 2 means it is a junction/cut vertex —
        // removing it would disconnect the region. These are removed iteratively
        // until the mask is stable (simply connected, no necks or figure-8 topology).
        private void EnforceMinimumCorridorWidth(bool[] mask, ImageSnapshot snap, int minWidth = 5)
        {
            int w = snap.Width;
            int h = snap.Height;
            int total = w * h;
            if (mask.Length != total) return;

            int minRadius = Math.Max(1, (minWidth + 1) / 2);

            EnsureBuffers(total);
            var dist = _distBuffer;
            var queue = _queueBuffer;

            for (int i = 0; i < total; i++)
                dist[i] = mask[i] ? int.MaxValue / 4 : 0;

            int head = 0, tail = 0;
            for (int i = 0; i < total; i++)
                if (dist[i] == 0)
                    queue[tail++] = i;

            while (head < tail)
            {
                int i = queue[head++];
                int d = dist[i] + 1;
                int x = i % w;
                int y = i / w;

                if (x > 0) Relax(i - 1, d);
                if (x < w - 1) Relax(i + 1, d);
                if (y > 0) Relax(i - w, d);
                if (y < h - 1) Relax(i + w, d);
            }

            bool[] copy = (bool[])mask.Clone();

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (!copy[i]) continue;

                    bool thin = false;

                    if (x > 0 && copy[i - 1] && dist[i - 1] < minRadius) thin = true;
                    if (!thin && x < w - 1 && copy[i + 1] && dist[i + 1] < minRadius) thin = true;
                    if (!thin && y > 0 && copy[i - w] && dist[i - w] < minRadius) thin = true;
                    if (!thin && y < h - 1 && copy[i + w] && dist[i + w] < minRadius) thin = true;

                    if (thin) mask[i] = false;
                }
            }

            void Relax(int ni, int nd)
            {
                if (dist[ni] <= nd) return;
                dist[ni] = nd;
                queue[tail++] = ni;
            }
        }


        private bool[] KeepLargestComponent(bool[] inside, ImageSnapshot snap,
                             int minArea = 0,
                             bool fallbackToLargestIfTooSmall = true)
        {
            int total = snap.Width * snap.Height;

            bool[] visited = new bool[total];
            bool[] best = new bool[total];

            List<int> bestComponent = null;

            for (int start = 0; start < total; start++)
            {
                if (!inside[start] || visited[start])
                    continue;

                var component = CollectComponent(inside, start, snap, visited);

                if (bestComponent == null || component.Count > bestComponent.Count)
                    bestComponent = component;
            }

            if (bestComponent == null)
                return best;

            if (minArea > 0 &&
                bestComponent.Count < minArea &&
                !fallbackToLargestIfTooSmall)
            {
                return new bool[total];
            }

            foreach (int i in bestComponent)
                best[i] = true;

            return best;
        }

        private int CountPixels(bool[] mask)
        {
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) count++;
            return count;
        }

        private bool HasMinimumPixels(bool[] mask, int minimum)
        {
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] && ++count >= minimum) return true;
            }
            return false;
        }


        private List<int> CollectComponent(
     bool[] mask,
     int seed,
     ImageSnapshot snap,
     bool[] visited = null)
        {
            int w = snap.Width, h = snap.Height;
            int total = w * h;

            var component = new List<int>();

            if (seed < 0 || seed >= total || !mask[seed])
                return component;

            if (visited == null)
                visited = new bool[total];

            EnsureQueue(total);
            _qHead = 0;
            _qTail = 0;

            // enqueue seed
            _queueBuffer[_qTail++] = seed;
            visited[seed] = true;

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];

                component.Add(ci);

                int cx = ci % w;
                int cy = ci / w;

                int row = cy * w;

                // LEFT
                if (cx > 0)
                {
                    int ni = ci - 1;

                    if (mask[ni] && !visited[ni])
                    {
                        visited[ni] = true;
                        _queueBuffer[_qTail++] = ni;
                    }
                }

                // RIGHT
                if (cx < w - 1)
                {
                    int ni = ci + 1;

                    if (mask[ni] && !visited[ni])
                    {
                        visited[ni] = true;
                        _queueBuffer[_qTail++] = ni;
                    }
                }

                // UP
                if (cy > 0)
                {
                    int ni = ci - w;

                    if (mask[ni] && !visited[ni])
                    {
                        visited[ni] = true;
                        _queueBuffer[_qTail++] = ni;
                    }
                }

                // DOWN
                if (cy < h - 1)
                {
                    int ni = ci + w;

                    if (mask[ni] && !visited[ni])
                    {
                        visited[ni] = true;
                        _queueBuffer[_qTail++] = ni;
                    }
                }
            }

            return component;
        }

        private bool[] PrepareMaskForTracing(bool[] pruned, bool[] raw,
                                      int seedX, int seedY, int minArea,
                                      ImageSnapshot snap)
        {
            bool[] candidate = (bool[])pruned.Clone();
            int area = CountPixels(candidate);
            if (area >= minArea) return candidate;

            int seed = seedY * snap.Width + seedX;

            if (!candidate[seed])
            {
                candidate = KeepComponentContainingSeed(raw, seed, snap);
                area = CountPixels(candidate);
            }

            if (area >= minArea) return candidate;

            candidate = ExpandConnectedComponentToArea(candidate, seed, minArea, snap);
            if (!HasMinimumPixels(candidate, minArea)) return candidate;

            bool[] fallback = KeepComponentContainingSeed(raw, seed, snap);
            if (!HasMinimumPixels(fallback, minArea)) return fallback;

            return fallback;
        }

        private bool[] ExpandConnectedComponentToArea(
    bool[] mask,
    int seed,
    int minArea,
    ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;

            bool[] current = (bool[])mask.Clone();

            if (seed < 0 || seed >= total)
                return current;

            if (!current[seed])
                current = KeepComponentContainingSeed(current, seed, snap);

            int currentArea = CountPixels(current);
            if (currentArea >= minArea)
                return current;

            EnsureQueue(total);
            _qHead = 0;
            _qTail = 0;

            // Seed BFS with all current pixels
            for (int i = 0; i < total; i++)
            {
                if (current[i])
                    _queueBuffer[_qTail++] = i;
            }

            while (_qHead < _qTail && currentArea < minArea)
            {
                int ci = _queueBuffer[_qHead++];

                int cx = ci % w;
                int cy = ci / w;

                int row = cy * w;

                void TryAdd(int ni)
                {
                    if (current[ni]) return;

                    current[ni] = true;
                    _queueBuffer[_qTail++] = ni;
                    currentArea++;
                }

                // LEFT
                if (cx > 0)
                    TryAdd(row + (cx - 1));

                // RIGHT
                if (cx < w - 1)
                    TryAdd(row + (cx + 1));

                // UP
                if (cy > 0)
                    TryAdd(row - w + cx);

                // DOWN
                if (cy < h - 1)
                    TryAdd(row + w + cx);
            }

            return current;
        }


        private bool[] KeepComponentContainingSeed(bool[] inside, int seed, ImageSnapshot snap)
        {
            int total = snap.Width * snap.Height;

            bool[] result = new bool[total];

            var component = CollectComponent(inside, seed, snap);

            foreach (int i in component)
                result[i] = true;

            return result;
        }

        private bool HasMinimumBorderLength(List<Point> boundary, int minBorderLength)
        {
            return boundary != null && boundary.Count >= minBorderLength;
        }

        // =====================
        // PREVENT SELF-TOUCHING TOPOLOGY
        // =====================
        // Removes any pixel that causes:
        // - corner touching
        // - diagonal kissing
        // - figure-8 topology
        // - narrow reconnections
        // - self-contacting outlines
        //
        // This guarantees the traced contour remains a simple curve.
        private void SmoothBorderTopology(bool[] mask, ImageSnapshot snap, int passes = 3)
        {
            int w = snap.Width, h = snap.Height;
            int total = w * h;
            bool[] copy = new bool[total];

            for (int pass = 0; pass < passes; pass++)
            {
                Array.Copy(mask, copy, total);

                for (int y = 1; y < h - 1; y++)
                {
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        if (!copy[i]) continue;

                        bool n = copy[i - w];
                        bool s = copy[i + w];
                        bool e = copy[i + 1];
                        bool wv = copy[i - 1];
                        bool ne = copy[i + 1 - w];
                        bool nw = copy[i - 1 - w];
                        bool se = copy[i + 1 + w];
                        bool sw = copy[i - 1 + w];

                        bool diagonalBridge =
                            (ne && sw && !n && !e && !s && !wv) ||
                            (nw && se && !n && !e && !s && !wv);

                        bool cornerTouch =
                            (n && e && !ne) ||
                            (e && s && !se) ||
                            (s && wv && !sw) ||
                            (wv && n && !nw);

                        int neighborCount = CountCardinalNeighbors(copy, x, y, w, h);
                        bool junction = neighborCount >= 3;

                        if (diagonalBridge || cornerTouch || junction)
                        {
                            if (n && e && !ne) mask[i + 1 - w] = true;
                            else if (e && s && !se) mask[i + 1 + w] = true;
                            else if (s && wv && !sw) mask[i - 1 + w] = true;
                            else if (wv && n && !nw) mask[i - 1 - w] = true;
                            else if (ne && sw && !n && !e && !s && !wv) mask[i - w] = true;
                            else if (nw && se && !n && !e && !s && !wv) mask[i - w] = true;
                            else if (junction)
                            {
                                if (!ne) mask[i + 1 - w] = true;
                                else if (!nw) mask[i - 1 - w] = true;
                                else if (!se) mask[i + 1 + w] = true;
                                else if (!sw) mask[i - 1 + w] = true;
                            }
                        }
                    }
                }
            }
        }


        // =====================
        // PRUNE DEAD ENDS
        // =====================
        // Removes any pixel that is not part of a closed loop by repeatedly
        // eliminating pixels with only one filled 4-neighbor (dead ends).
        // Iterates until stable — guarantees every remaining pixel has at least
        // two neighbors, meaning it lies on a cycle rather than a dangling branch.
        private void PruneDeadEnds(bool[] mask, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            EnsureQueue(total * 2);

            // Seed worklist with all current border pixels (those with ≤1 neighbor)
            _qHead = 0; _qTail = 0;
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    if (mask[i] && CountCardinalNeighbors(mask, x, y, w, h) <= 1)
                        _queueBuffer[_qTail++] = i;
                }
            }

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];
                if (!mask[ci]) continue;

                int cx = ci % w, cy = ci / w;
                if (cx < 1 || cx >= w - 1 || cy < 1 || cy >= h - 1) continue;

                if (CountCardinalNeighbors(mask, cx, cy, w, h) <= 1)
                {
                    mask[ci] = false;
                    // Recheck all 4 cardinal neighbors
                    if (cx > 0) { int ni = ci - 1; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                    if (cx < w - 1) { int ni = ci + 1; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                    if (cy > 0) { int ni = ci - w; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                    if (cy < h - 1) { int ni = ci + w; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                }
            }
        }
        #endregion

        #region erase function   
        private double _eraseBrushRadius = 20;
        public double EraseBrushRadius
        {
            get => _eraseBrushRadius;
            set { _eraseBrushRadius = value; OnPropertyChanged(nameof(EraseBrushRadius)); }
        }

        // =====================
        // ERASE / SHRINK OUTLINE
        // =====================
        // Called on mouse-drag when EraseOutlineMode is active.
        // Finds all polyline vertices within EraseBrushRadius of the cursor
        // and moves them toward the centroid of the full polyline,
        // shrinking that portion of the outline inward.
        public void ProcessEraseDrag(Vector2 mousePos)
        {
            if (_activePolyline == null) return;

            double mx = mousePos.X;
            double my = mousePos.Y;
            double r2 = EraseBrushRadius * EraseBrushRadius;

            var points = _activePolyline.Points;
            if (points.Count < 3) return;

            int n = points.Count;
            bool[] inside = new bool[n];
            bool anyInside = false;

            for (int i = 0; i < n; i++)
            {
                double dx = points[i].X - mx;
                double dy = points[i].Y - my;
                if (dx * dx + dy * dy <= r2)
                {
                    inside[i] = true;
                    anyInside = true;
                }
            }

            if (!anyInside) return;

            // Check whether the erased run wraps around the seam (last->first).
            // If so, rotate the point list so the seam falls inside a kept region,
            // preventing the bridging logic from splitting across the wrap point.
            if (inside[0] || inside[n - 1])
            {
                // Find the first index that is NOT inside the brush
                int rotateStart = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!inside[i]) { rotateStart = i; break; }
                }

                // If every point is inside the brush, the whole outline would be erased — bail out
                if (rotateStart < 0) return;

                // Rotate points and inside flags so rotateStart becomes index 0
                var rotatedPoints = new List<Point>(n);
                var rotatedInside = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    int src = (rotateStart + i) % n;
                    rotatedPoints.Add(points[src]);
                    rotatedInside[i] = inside[src];
                }

                points.Clear();
                foreach (var p in rotatedPoints)
                    points.Add(p);

                inside = rotatedInside;
                n = points.Count;
            }

            // Build new point list, replacing each erased run with one midpoint bridge
            var newPoints = new List<Point>();
            int i2 = 0;
            while (i2 < n)
            {
                if (!inside[i2])
                {
                    newPoints.Add(points[i2]);
                    i2++;
                }
                else
                {
                    int runStart = i2;
                    while (i2 < n && inside[i2]) i2++;

                    Point before = points[(runStart - 1 + n) % n];
                    Point after = points[i2 % n];

                    newPoints.Add(new Point(
                        (before.X + after.X) * 0.5,
                        (before.Y + after.Y) * 0.5));
                }
            }

            if (newPoints.Count < 3) return;

            // =====================
            // CLOSURE FAILSAFE
            // =====================
            // Guarantee the polyline is always explicitly closed:
            // the last point must equal the first point.
            // If they differ by more than 1 pixel, append a copy of the first point.

            points.Clear();
            foreach (var p in newPoints)
                points.Add(p);

            EnforcePolylineClosure();

            RefreshSmoothSnapshot();
        }
        private void EnforcePolylineClosure()
        {
            if (_activePolyline == null) return;
            var points = _activePolyline.Points;
            if (points.Count < 2) return;

            Point first = points[0];
            Point last = points[points.Count - 1];
            double dx = first.X - last.X;
            double dy = first.Y - last.Y;

            if (dx * dx + dy * dy >= 1.0)
                points.Add(first);
        }

        public void ClearActivePolyline()
        {
            _activePolyline = null;
        }
        #endregion

        #region smooth mode
        private int _smoothStrength = 0;
        public int SmoothStrength
        {
            get => _smoothStrength;
            set
            {
                _smoothStrength = value;
                OnPropertyChanged(nameof(SmoothStrength));
                ApplyGlobalSmooth();
            }
        }

        // Snapshot of the polyline points taken when smooth mode is entered,
        // so that smoothing always applies to the original shape rather than
        // compounding on each slider change.
        private List<Point> _preSmoothSnapshot = null;

        public void TakePreSmoothSnapshot()
        {
            if (_activePolyline == null) { _preSmoothSnapshot = null; return; }
            _preSmoothSnapshot = new List<Point>(_activePolyline.Points);
        }

        private void RefreshSmoothSnapshot()
        {
            if (_activePolyline == null) return;
            _preSmoothSnapshot = new List<Point>(_activePolyline.Points);
        }

        // Applies Laplacian smoothing to the entire polyline.
        // Runs SmoothStrength passes of neighbor-averaging over all points.
        // Always works from the pre-smooth snapshot so slider changes are
        // non-destructive and the original shape is recoverable by setting
        // the slider back to 0.
        private void ApplyGlobalSmooth()
        {
            if (_activePolyline == null) return;
            if (_preSmoothSnapshot == null || _preSmoothSnapshot.Count < 3) return;

            // Start from the original snapshot each time
            var working = new List<Point>(_preSmoothSnapshot);

            for (int pass = 0; pass < _smoothStrength; pass++)
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

            // Write result back into the live polyline
            var points = _activePolyline.Points;
            points.Clear();
            foreach (var p in working)
                points.Add(p);

            // Ensure closed
            if (points.Count >= 2)
            {
                Point first = points[0];
                Point last = points[points.Count - 1];
                double dx = first.X - last.X;
                double dy = first.Y - last.Y;
                if (dx * dx + dy * dy >= 1.0)
                    points.Add(first);
            }
        }
        #endregion
        #region calculate metadata

        // =====================
        // RESULT PROPERTIES
        // =====================
        private double _aspectRatioResult;
        public double AspectRatioResult
        {
            get => _aspectRatioResult;
            set { _aspectRatioResult = value; OnPropertyChanged(nameof(AspectRatioResult)); }
        }

        private double _perimeterAreaRatioResult;
        public double PerimeterAreaRatioResult
        {
            get => _perimeterAreaRatioResult;
            set { _perimeterAreaRatioResult = value; OnPropertyChanged(nameof(PerimeterAreaRatioResult)); }
        }

        private double _circularityResult;
        public double CircularityResult
        {
            get => _circularityResult;
            set { _circularityResult = value; OnPropertyChanged(nameof(CircularityResult)); }
        }

        private double[] _efdCoefficientsResult;
        public double[] EFDCoefficientsResult
        {
            get => _efdCoefficientsResult;
            set { _efdCoefficientsResult = value; OnPropertyChanged(nameof(EFDCoefficientsResult)); }
        }

        private double _solidityResult;
        public double SolidityResult
        {
            get => _solidityResult;
            set { _solidityResult = value; OnPropertyChanged(nameof(SolidityResult)); }
        }

        private double _sumTurningAnglesResult;
        public double SumTurningAnglesResult
        {
            get => _sumTurningAnglesResult;
            set { _sumTurningAnglesResult = value; OnPropertyChanged(nameof(SumTurningAnglesResult)); }
        }

        private double _meanTurningAngleResult;
        public double MeanTurningAngleResult
        {
            get => _meanTurningAngleResult;
            set { _meanTurningAngleResult = value; OnPropertyChanged(nameof(MeanTurningAngleResult)); }
        }

        private double _varianceTurningAnglesResult;
        public double VarianceTurningAnglesResult
        {
            get => _varianceTurningAnglesResult;
            set { _varianceTurningAnglesResult = value; OnPropertyChanged(nameof(VarianceTurningAnglesResult)); }
        }

        // Formatted string for display in the control panel
        private string _metadataSummary = "";
        public string MetadataSummary
        {
            get => _metadataSummary;
            set { _metadataSummary = value; OnPropertyChanged(nameof(MetadataSummary)); }
        }

        // Called when the user clicks Generate Metadata
        public void GenerateMetadata()
        {
            if (_activePolyline == null || _activePolyline.Points.Count < 3)
            {
                MetadataSummary = "No outline available.";
                UpdateEFDPreview();
                return;
            }

            var pts = new List<Point>(_activePolyline.Points);

            // Remove duplicate closing point if present
            if (pts.Count > 1)
            {
                Point f = pts[0], l = pts[pts.Count - 1];
                if ((f.X - l.X) * (f.X - l.X) + (f.Y - l.Y) * (f.Y - l.Y) < 1.0)
                    pts.RemoveAt(pts.Count - 1);
            }

            // Convert from canvas space to image space for scale-invariant metric computation.
            // All GeometryCalculations calls below use image-space coordinates.
            var imagePts = pts.Select(CanvasToImage).ToList();

            double perimeter = GeometryCalculations.Perimeter(imagePts);
            double area = GeometryCalculations.PolygonArea(imagePts);
            double[] bbox = GeometryCalculations.BoundingBox(imagePts);
            double bboxW = bbox[2] - bbox[0];
            double bboxH = bbox[3] - bbox[1];
            AspectRatioResult = GeometryCalculations.BoundingBoxAspectRatio(bboxW, bboxH);
            PerimeterAreaRatioResult = GeometryCalculations.PerimeterAreaRatio(perimeter, area);
            CircularityResult = GeometryCalculations.Circularity(perimeter, area);

            double convexHullArea = GeometryCalculations.ConvexHullArea(imagePts);
            SolidityResult = GeometryCalculations.Solidity(area, convexHullArea);
            SumTurningAnglesResult = GeometryCalculations.SumTurningAngles(imagePts);
            MeanTurningAngleResult = GeometryCalculations.MeanTurningAngle(imagePts);
            VarianceTurningAnglesResult = GeometryCalculations.VarianceTurningAngles(imagePts);

            int harmonics = EfdHarmonics;
            EFDCoefficientsResult = ComputeNormalizedEFD(pts, harmonics);

            // Build display string
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Aspect Ratio:       {AspectRatioResult:F3}");
            sb.AppendLine($"Perim / Area:       {PerimeterAreaRatioResult:F4}");
            sb.AppendLine($"Circularity:        {CircularityResult:F4}");
            sb.AppendLine($"Solidity:           {SolidityResult:F4}");
            sb.AppendLine($"Sum Turning Angles: {SumTurningAnglesResult:F4}");
            sb.AppendLine($"Mean Turning Angle: {MeanTurningAngleResult:F4}");
            sb.AppendLine($"Variance Turning Angles: {VarianceTurningAnglesResult:F4}");
            sb.AppendLine($"EFD harmonics ({harmonics}):");
            for (int h = 0; h < harmonics; h++)
            {
                int k = h * 4;
                sb.AppendLine($"  n={h + 1}: a={EFDCoefficientsResult[k]:F4} b={EFDCoefficientsResult[k + 1]:F4} c={EFDCoefficientsResult[k + 2]:F4} d={EFDCoefficientsResult[k + 3]:F4}");
            }
            MetadataSummary = sb.ToString();

            // Stamp the result onto the committed operation so it persists with undo/redo
            if (UndoRedoManager?.CurrentOperation is OutlineOperation op)
            {
                op.AspectRatio = AspectRatioResult;
                op.PerimeterAreaRatio = PerimeterAreaRatioResult;
                op.Circularity = CircularityResult;
                op.EFDCoefficients = EFDCoefficientsResult;
                op.Solidity = SolidityResult;
                op.SumTurningAngles = SumTurningAnglesResult;
                op.MeanTurningAngle = MeanTurningAngleResult;
                op.VarianceTurningAngles = VarianceTurningAnglesResult;
            }

            UpdateEFDPreview();
        }

        // =====================
        // ELLIPTIC FOURIER DESCRIPTORS
        // =====================

        // Store raw coefficients separately for preview reconstruction
        private double[] _rawEFDCoefficients = null;

        // Computes normalized EFDs (Kuhl & Giardina 1982).
        // Normalization makes descriptors invariant to size, rotation, and starting point.
        // Returns array of length harmonics*4: [a1,b1,c1,d1, a2,b2,c2,d2, ...]
        private double[] ComputeNormalizedEFD(List<Point> pts, int harmonics)
        {
            int n = pts.Count;

            // Arc-length parametrization
            double[] dt = new double[n];       // segment lengths
            double[] T = new double[n + 1];   // cumulative arc length
            T[0] = 0;
            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                dt[i] = Math.Sqrt(dx * dx + dy * dy);
                T[i + 1] = T[i] + dt[i];
            }
            double totalLen = T[n];
            if (totalLen < 1e-10) return new double[harmonics * 4];

            double[] coeffs = new double[harmonics * 4];

            for (int h = 1; h <= harmonics; h++)
            {
                double an = 0, bn = 0, cn = 0, dn = 0;
                double twoPI_n_T = 2.0 * Math.PI * h / totalLen;
                double coeff = totalLen / (2.0 * h * h * Math.PI * Math.PI);

                for (int i = 0; i < n; i++)
                {
                    Point a = pts[i], b = pts[(i + 1) % n];
                    double dxi = b.X - a.X;
                    double dyi = b.Y - a.Y;
                    if (dt[i] < 1e-10) continue;

                    double ti0 = T[i];
                    double ti1 = T[i + 1];

                    double cos1 = Math.Cos(twoPI_n_T * ti1) - Math.Cos(twoPI_n_T * ti0);
                    double sin1 = Math.Sin(twoPI_n_T * ti1) - Math.Sin(twoPI_n_T * ti0);

                    an += (dxi / dt[i]) * cos1;
                    bn += (dxi / dt[i]) * sin1;
                    cn += (dyi / dt[i]) * cos1;
                    dn += (dyi / dt[i]) * sin1;
                }

                int k = (h - 1) * 4;
                coeffs[k] = coeff * an;
                coeffs[k + 1] = coeff * bn;
                coeffs[k + 2] = coeff * cn;
                coeffs[k + 3] = coeff * dn;
            }

            // Save raw coefficients for preview reconstruction before normalizing
            _rawEFDCoefficients = (double[])coeffs.Clone();

            // Normalize: make invariant to size and rotation using the first harmonic
            // Rotation angle from first harmonic ellipse
            double a1 = coeffs[0], b1 = coeffs[1], c1 = coeffs[2], d1 = coeffs[3];
            double theta1 = 0.5 * Math.Atan2(2 * (a1 * b1 + c1 * d1),
                                              a1 * a1 - b1 * b1 + c1 * c1 - d1 * d1);

            // Semi-major axis length for scale normalization
            double cosT = Math.Cos(theta1), sinT = Math.Sin(theta1);
            double scaleA = a1 * cosT + b1 * sinT;
            double scaleC = c1 * cosT + d1 * sinT;
            double scale = Math.Sqrt(scaleA * scaleA + scaleC * scaleC);

            if (scale < 1e-10) return coeffs;

            // Apply rotation and scale normalization to all harmonics
            var normalized = new double[harmonics * 4];
            for (int h = 1; h <= harmonics; h++)
            {
                int k = (h - 1) * 4;
                double ah = coeffs[k], bh = coeffs[k + 1], ch = coeffs[k + 2], dh = coeffs[k + 3];

                // Rotate by -theta1 * h
                double angle = h * theta1;
                double cosA = Math.Cos(angle), sinA = Math.Sin(angle);

                double ahr = ah * cosA + bh * sinA;
                double bhr = -ah * sinA + bh * cosA;
                double chr = ch * cosA + dh * sinA;
                double dhr = -ch * sinA + dh * cosA;

                // Then rotate out the tilt of the first harmonic ellipse
                double psi = Math.Atan2(scaleC, scaleA);
                double cosP = Math.Cos(psi), sinP = Math.Sin(psi);

                normalized[k] = (ahr * cosP + chr * sinP) / scale;
                normalized[k + 1] = (bhr * cosP + dhr * sinP) / scale;
                normalized[k + 2] = (-ahr * sinP + chr * cosP) / scale;
                normalized[k + 3] = (-bhr * sinP + dhr * cosP) / scale;
            }

            return normalized;
        }

        private int efdHarmonics = 10;
        public int EfdHarmonics
        {
            get => efdHarmonics;
            set
            {
                int clamped = Math.Max(1, Math.Min(100, value));
                if (efdHarmonics == clamped) return;
                efdHarmonics = clamped;
                OnPropertyChanged(nameof(EfdHarmonics));
                UpdateEFDPreview(); // ← add this
            }
        }

        // The blue EFD preview polyline shown in the workspace
        private Polyline _efdPreviewPolyline = null;
        public Action<Polyline> OnEFDPreviewReady;   // MainWindow wires this up
        public Action OnEFDPreviewClear;             // MainWindow wires this up

        // Reconstructs the outline from EFD coefficients and displays it as a
        // blue overlay. Called whenever EfdHarmonics changes or metadata is generated.
        public void UpdateEFDPreview()
        {
            OnEFDPreviewClear?.Invoke();
            _efdPreviewPolyline = null;

            if (_rawEFDCoefficients == null || _rawEFDCoefficients.Length == 0) return;
            if (_activePolyline == null || _activePolyline.Points.Count < 3) return;

            int maxHarmonics = _rawEFDCoefficients.Length / 4;
            int harmonics = Math.Min(EfdHarmonics, maxHarmonics);
            if (harmonics < 1) return;

            int sampleCount = Math.Max(100, harmonics * 20);

            // Raw EFD coefficients are in canvas space — no scaling needed.
            // DC offset is the mean position of the outline points.
            double dcX = 0, dcY = 0;
            int ptCount = _activePolyline.Points.Count;
            foreach (var p in _activePolyline.Points) { dcX += p.X; dcY += p.Y; }
            dcX /= ptCount;
            dcY /= ptCount;

            var previewLine = new Polyline
            {
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                FillRule = FillRule.EvenOdd
            };

            for (int s = 0; s <= sampleCount; s++)
            {
                double baseAngle = 2.0 * Math.PI * (double)s / sampleCount;
                double x = 0, y = 0;

                double cosBase = Math.Cos(baseAngle);
                double sinBase = Math.Sin(baseAngle);
                // Running cos/sin via angle addition: cos(h*θ), sin(h*θ)
                double cosH = cosBase, sinH = sinBase;  // h=1
                double cos2 = 2 * cosBase * cosBase - 1; // cos(2θ) = 2cos²θ - 1

                for (int h = 1; h <= harmonics; h++)
                {
                    int k = (h - 1) * 4;
                    x += _rawEFDCoefficients[k] * cosH + _rawEFDCoefficients[k + 1] * sinH;
                    y += _rawEFDCoefficients[k + 2] * cosH + _rawEFDCoefficients[k + 3] * sinH;

                    // Advance to next harmonic using angle addition
                    double newCos = cosBase * cosH - sinBase * sinH;
                    double newSin = sinBase * cosH + cosBase * sinH;
                    cosH = newCos;
                    sinH = newSin;
                }

                previewLine.Points.Add(new Point(dcX + x, dcY + y));
            }

            _efdPreviewPolyline = previewLine;
            OnEFDPreviewReady?.Invoke(previewLine);
        }

        public void ClearEFDPreview()
        {
            OnEFDPreviewClear?.Invoke();
            _efdPreviewPolyline = null;
        }
        #endregion
    }
}