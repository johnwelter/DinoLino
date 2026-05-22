using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
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
                _outlineMetadataMode = value;
                OnPropertyChanged(nameof(OutlineMetadataMode));
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

            _bgColor = EstimateBackgroundColor();
            _backgroundMask = BuildBackgroundMask();
        }


        private (double r, double g, double b) _bgColor;
        private (double r, double g, double b) EstimateBackgroundColor()
        {
            long r = 0, g = 0, b = 0;
            long count = 0;

            int stepX = Math.Max(1, _cachedWidth / 50);
            int stepY = Math.Max(1, _cachedHeight / 50);

            for (int y = 0; y < _cachedHeight; y += stepY)
            {
                for (int x = 0; x < _cachedWidth; x += stepX)
                {
                    var (pr, pg, pb) = ReadPixel(x, y);
                    r += pr;
                    g += pg;
                    b += pb;
                    count++;
                }
            }

            return (
                r / (double)count,
                g / (double)count,
                b / (double)count
            );
        }

        private bool[] _backgroundMask; 
        private bool[] BuildBackgroundMask(double threshold = 25)
        {
            int w = _cachedWidth;
            int h = _cachedHeight;

            bool[] background = new bool[w * h];
            var queue = new Queue<(int x, int y)>();

            void TryAdd(int x, int y)
            {
                int i = y * w + x;
                if (background[i]) return;

                var (r, g, b) = ReadPixel(x, y);

                double dist = PerceptualDistance(
                    r, g, b,
                    (byte)Math.Max(255, Math.Min(0, _bgColor.r)),
                    (byte)Math.Max(255, Math.Min(0, _bgColor.g)),
                    (byte)Math.Max(255, Math.Min(0, _bgColor.b)));

                if (dist > threshold)
                    return;

                background[i] = true;
                queue.Enqueue((x, y));
            }

            // Seed from borders
            for (int x = 0; x < w; x++)
            {
                TryAdd(x, 0);
                TryAdd(x, h - 1);
            }

            for (int y = 1; y < h - 1; y++)
            {
                TryAdd(0, y);
                TryAdd(w - 1, y);
            }

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            int iterations = 0;

            while (queue.Count > 0)
            {
                if ((queue.Count & 1023) == 0)
                    ThrowIfCancelled();

                var (cx, cy) = queue.Dequeue();

                if ((iterations++ & 1023) == 0)
                    ThrowIfCancelled();

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d];
                    int ny = cy + dy[d];

                    if ((uint)nx >= w || (uint)ny >= h)
                        continue;

                    TryAdd(nx, ny);
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
            public readonly (double r, double g, double b) BgColor;

            public ImageSnapshot(
                byte[] pixels,
                bool[] bgMask,
                int w,
                int h,
                int stride,
                int bpp,
                (double r, double g, double b) bgColor)
            {
                Pixels = pixels;
                BgMask = bgMask;
                Width = w;
                Height = h;
                Stride = stride;
                Bpp = bpp;
                BgColor = bgColor;
            }
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
            var snap = new ImageSnapshot(_cachedPixels, _backgroundMask, _cachedWidth, _cachedHeight, _cachedStride, _cachedBpp, _bgColor);
            double scaleX = ScaleX, scaleY = ScaleY;
            double offsetX = OffsetX, offsetY = OffsetY;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    (byte sr, byte sg, byte sb) = ReadPixel(px, py, snap);
                    bool[] raw = FloodFill(px, py, sr, sg, sb, snap);
                    if (CountPixels(raw) == 0) return;

                    token.ThrowIfCancellationRequested();

                    bool[] work = (bool[])raw.Clone();
                    MorphologicalErosion(work, snap);
                    token.ThrowIfCancellationRequested();

                    work = ExtractLargestComponent(work, snap);
                    token.ThrowIfCancellationRequested();

                    DistancePruneNarrowPassages(work, snap);
                    token.ThrowIfCancellationRequested();

                    PreventSelfTouchingTopology(work, snap);
                    token.ThrowIfCancellationRequested();

                    PruneDeadEnds(work, snap);
                    work = KeepLargestComponent(work, snap);
                    token.ThrowIfCancellationRequested();

                    bool[] tracedMask = PrepareMaskForTracing(work, raw, px, py, MinAreaPixels, snap);
                    token.ThrowIfCancellationRequested();

                    List<Point> boundary = TraceBoundary(tracedMask, snap);
                    token.ThrowIfCancellationRequested();

                    if (!HasMinimumBorderLength(boundary, 8))
                    {
                        bool[] expanded = ExpandConnectedComponentToArea(
                            KeepComponentContainingSeed(raw, py * snap.Width + px, snap),
                            py * snap.Width + px,
                            MinAreaPixels,
                            snap);
                        token.ThrowIfCancellationRequested();
                        boundary = TraceBoundary(expanded, snap);
                    }

                    if (boundary.Count < 8) return;
                    token.ThrowIfCancellationRequested();

                    List<Point> simplified = DouglasPeucker(boundary, _simplifyEpsilon);
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

                        foreach (var p in simplified)
                            polyline.Points.Add(new Point(
                                p.X * ScaleX + OffsetX,
                                p.Y * ScaleY + OffsetY));

                        polyline.Points.Add(new Point(
                            simplified[0].X * ScaleX + OffsetX,
                            simplified[0].Y * ScaleY + OffsetY));

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
            bool[] inside = new bool[w * h];

            var queue = new Queue<(int x, int y)>();
            queue.Enqueue((startX, startY));
            inside[startY * w + startX] = true;

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            double toleranceTimes2 = _tolerance * 2.0;

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                int ci = cy * snap.Stride + cx * snap.Bpp;
                byte cr, cg, cb;
                if (snap.Bpp == 1)
                {
                    cr = cg = cb = snap.Pixels[ci];
                }
                else
                {
                    cr = snap.Pixels[ci + 2];
                    cg = snap.Pixels[ci + 1];
                    cb = snap.Pixels[ci];
                }

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d];
                    int ny = cy + dy[d];

                    if ((uint)nx >= w || (uint)ny >= h) continue;

                    int ni = ny * w + nx;
                    if (inside[ni]) continue;

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
                    double maxSeedDistance = _tolerance * 6.0;
                    bool seedMatch = seedDist <= _tolerance;
                    bool gradientMatch = neighborDist <= _gradientLeniency &&
                                         seedDist <= maxSeedDistance;

                    if (strongEdge && !seedMatch) continue;

                    if (snap.BgMask != null && snap.BgMask[ni]) continue;

                    if (!(seedMatch || gradientMatch)) continue;

                    inside[ni] = true;
                    queue.Enqueue((nx, ny));
                }
            }

            return inside;
        }

        // =====================
        // MORPHOLOGICAL EROSION
        // =====================
        // Removes isolated edge pixels from the fill mask (1-pixel shrink),
        // producing a cleaner boundary for tracing.
        private void MorphologicalErosion(bool[] inside, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height;
            bool[] copy = (bool[])inside.Clone();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!copy[i]) continue;

                    bool borderExposed =
                        x == 0 || x == w - 1 || y == 0 || y == h - 1 ||

                        (x > 0 && !copy[i - 1]) ||
                        (x < w - 1 && !copy[i + 1]) ||
                        (y > 0 && !copy[i - w]) ||
                        (y < h - 1 && !copy[i + w]);

                    if (borderExposed)
                        inside[i] = false;
                }
            }
        }

        // =====================
        // EXTRACT LARGEST COMPONENT
        // =====================
        private bool[] ExtractLargestComponent(bool[] inside, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height;
            int total = w * h;
            bool[] labeled = new bool[total];
            bool[] result = new bool[total];

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            int bestSeed = -1, bestCount = 0;

            for (int start = 0; start < total; start++)
            {
                if (!inside[start] || labeled[start]) continue;

                var queue = new Queue<int>();
                queue.Enqueue(start);
                labeled[start] = true;
                int count = 1;

                while (queue.Count > 0)
                {
                    int ci = queue.Dequeue();
                    int cx = ci % w, cy = ci / w;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + dx[d], ny = cy + dy[d];
                        if ((uint)nx >= w || (uint)ny >= h) continue;
                        int ni = ny * w + nx;
                        if (!inside[ni] || labeled[ni]) continue;
                        labeled[ni] = true;
                        count++;
                        queue.Enqueue(ni);
                    }
                }

                if (count > bestCount) { bestCount = count; bestSeed = start; }
            }

            if (bestSeed < 0) return result;

            var resultQueue = new Queue<int>();
            resultQueue.Enqueue(bestSeed);
            result[bestSeed] = true;

            while (resultQueue.Count > 0)
            {
                if ((resultQueue.Count & 1023) == 0) ThrowIfCancelled();

                int ci = resultQueue.Dequeue();
                int cx = ci % w, cy = ci / w;

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d], ny = cy + dy[d];
                    if ((uint)nx >= w || (uint)ny >= h) continue;
                    int ni = ny * w + nx;
                    if (!inside[ni] || result[ni]) continue;
                    result[ni] = true;
                    resultQueue.Enqueue(ni);
                }
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
        // DOUGLAS-PEUCKER
        // =====================
        // Index-based: no List.GetRange() allocations during recursion.
        private List<Point> DouglasPeucker(List<Point> points, double epsilon)
        {
            var result = new List<Point>();
            DouglasPeuckerRecursive(points, 0, points.Count - 1, epsilon, result);
            result.Add(points[points.Count - 1]);
            return result;
        }

        private void DouglasPeuckerRecursive(List<Point> points, int start, int end,double epsilon, List<Point> result)
        {
            ThrowIfCancelled();

            if (end <= start + 1)
            {
                result.Add(points[start]);
                return;
            }

            double maxDist = 0;
            int maxIndex = start;
            Point a = points[start], b = points[end];

            for (int i = start + 1; i < end; i++)
            {
                if ((i & 63) == 0)   // frequent enough because this can be O(n²)
                    ThrowIfCancelled();

                double d = PerpendicularDistance(points[i], a, b);
                if (d > maxDist) { maxDist = d; maxIndex = i; }
            }

            if (maxDist > epsilon)
            {
                DouglasPeuckerRecursive(points, start, maxIndex, epsilon, result);
                DouglasPeuckerRecursive(points, maxIndex, end, epsilon, result);
            }
            else
            {
                // Emit start; end will be emitted as start of next segment or by the caller
                result.Add(points[start]);
            }
        }

        private double PerpendicularDistance(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (dx == 0 && dy == 0)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            double t = Math.Max(0, Math.Min(1,
                ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy)));
            double projX = a.X + t * dx, projY = a.Y + t * dy;
            return Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
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
        private void DistancePruneNarrowPassages(bool[] mask, ImageSnapshot snap,
                                          int minSeparationPixels = 5)
        {
            int w = snap.Width, h = snap.Height;
            int total = w * h;

            if (mask.Length != total) return;

            int minRadius = (minSeparationPixels + 1) / 2;
            if (minRadius < 1) minRadius = 1;

            bool changed = true;
            int[] dist = new int[total];
            int[] queue = new int[total];

            while (changed)
            {
                changed = false;

                for (int i = 0; i < total; i++)
                    dist[i] = mask[i] ? int.MaxValue / 4 : 0;

                int head = 0, tail = 0;

                for (int i = 0; i < total; i++)
                    if (dist[i] == 0) queue[tail++] = i;

                while (head < tail)
                {
                    if ((head & 1023) == 0) ThrowIfCancelled();

                    int i = queue[head++];
                    int d = dist[i] + 1;
                    int x = i % w, y = i / w;

                    if (x > 0) { int ni = i - 1; if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; } }
                    if (x < w - 1) { int ni = i + 1; if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; } }
                    if (y > 0) { int ni = i - w; if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; } }
                    if (y < h - 1) { int ni = i + w; if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; } }
                }

                for (int i = 0; i < total; i++)
                {
                    if (!mask[i]) continue;
                    if (dist[i] < minRadius) { mask[i] = false; changed = true; }
                }

                if (!changed) break;

                mask = KeepLargestComponent(mask, snap);
            }
        }

        private bool[] KeepLargestComponent(bool[] inside, ImageSnapshot snap,
                                     int minArea = 0,
                                     bool fallbackToLargestIfTooSmall = true)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] visited = new bool[total];
            bool[] best = new bool[total];

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            int bestCount = 0;
            var q = new Queue<int>();

            for (int start = 0; start < total; start++)
            {
                if (!inside[start] || visited[start]) continue;

                var component = new List<int>();
                q.Clear();
                q.Enqueue(start);
                visited[start] = true;

                while (q.Count > 0)
                {
                    int ci = q.Dequeue();
                    component.Add(ci);
                    int cx = ci % w, cy = ci / w;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + dx[d], ny = cy + dy[d];
                        if ((uint)nx >= w || (uint)ny >= h) continue;
                        int ni = ny * w + nx;
                        if (!inside[ni] || visited[ni]) continue;
                        visited[ni] = true;
                        q.Enqueue(ni);
                    }
                }

                if (component.Count > bestCount)
                {
                    bestCount = component.Count;
                    Array.Clear(best, 0, best.Length);
                    foreach (int i in component) best[i] = true;
                }
            }

            if (bestCount == 0) return best;

            if (minArea > 0 && bestCount < minArea)
                return fallbackToLargestIfTooSmall ? best : new bool[total];

            return best;
        }

        private int CountPixels(bool[] mask)
        {
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) count++;
            return count;
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
            if (CountPixels(candidate) >= minArea) return candidate;

            bool[] fallback = KeepComponentContainingSeed(raw, seed, snap);
            if (CountPixels(fallback) >= minArea) return fallback;

            return fallback;
        }

        private bool[] ExpandConnectedComponentToArea(bool[] mask, int seed, int minArea,
                                               ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] current = (bool[])mask.Clone();

            if (seed < 0 || seed >= total) return current;

            if (!current[seed])
                current = KeepComponentContainingSeed(current, seed, snap);

            int currentArea = CountPixels(current);
            if (currentArea >= minArea) return current;

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            var frontier = new Queue<int>();

            for (int i = 0; i < total; i++)
                if (current[i]) frontier.Enqueue(i);

            while (currentArea < minArea && frontier.Count > 0)
            {
                int levelCount = frontier.Count;
                bool grew = false;

                for (int n = 0; n < levelCount; n++)
                {
                    int ci = frontier.Dequeue();
                    int cx = ci % w, cy = ci / w;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + dx[d], ny = cy + dy[d];
                        if ((uint)nx >= w || (uint)ny >= h) continue;
                        int ni = ny * w + nx;
                        if (current[ni]) continue;

                        current[ni] = true;
                        frontier.Enqueue(ni);
                        grew = true;
                        currentArea++;
                        if (currentArea >= minArea) return current;
                    }
                }

                if (!grew) break;
            }

            return current;
        }


        private bool[] KeepComponentContainingSeed(bool[] inside, int seed, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] result = new bool[total];
            if (seed < 0 || seed >= total || !inside[seed]) return result;

            var q = new Queue<int>();
            q.Enqueue(seed);
            result[seed] = true;

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            while (q.Count > 0)
            {
                int ci = q.Dequeue();
                int cx = ci % w, cy = ci / w;

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d], ny = cy + dy[d];
                    if ((uint)nx >= w || (uint)ny >= h) continue;
                    int ni = ny * w + nx;
                    if (!inside[ni] || result[ni]) continue;
                    result[ni] = true;
                    q.Enqueue(ni);
                }
            }

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
        private void PreventSelfTouchingTopology(bool[] mask, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height;

            bool changed = true;

            while (changed)
            {
                changed = false;

                bool[] copy = (bool[])mask.Clone();

                for (int y = 1; y < h - 1; y++)
                {
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;

                        if (!copy[i])
                            continue;

                        // Cardinal neighbors
                        bool n = copy[i - w];
                        bool s = copy[i + w];
                        bool e = copy[i + 1];
                        bool wv = copy[i - 1];

                        // Diagonal neighbors
                        bool ne = copy[i + 1 - w];
                        bool nw = copy[i - 1 - w];
                        bool se = copy[i + 1 + w];
                        bool sw = copy[i - 1 + w];

                        // =====================
                        // DIAGONAL SELF-TOUCH TESTS
                        // =====================

                        bool diagonalBridge =
                            (ne && sw && !n && !e && !s && !wv) ||
                            (nw && se && !n && !e && !s && !wv);

                        // =====================
                        // CORNER KISSING TESTS
                        // =====================

                        bool cornerTouch =
                            (n && e && !ne) ||
                            (e && s && !se) ||
                            (s && wv && !sw) ||
                            (wv && n && !nw);

                        // =====================
                        // LOCAL CONNECTIVITY TEST
                        // =====================

                        int neighborCount =
                            (n ? 1 : 0) +
                            (s ? 1 : 0) +
                            (e ? 1 : 0) +
                            (wv ? 1 : 0);

                        // Remove junctions
                        bool junction = neighborCount >= 3;

                        // =====================
                        // REMOVE BAD PIXELS
                        // =====================

                        if (diagonalBridge || cornerTouch || junction)
                        {
                            mask[i] = false;
                            changed = true;
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
            int w = snap.Width, h = snap.Height;
            bool changed = true;

            while (changed)
            {
                changed = false;

                for (int y = 1; y < h - 1; y++)
                {
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        if (!mask[i]) continue;

                        int filledNeighbors =
                            (mask[i - 1] ? 1 : 0) +
                            (mask[i + 1] ? 1 : 0) +
                            (mask[i - w] ? 1 : 0) +
                            (mask[i + w] ? 1 : 0);

                        if (filledNeighbors <= 1)
                        {
                            mask[i] = false;
                            changed = true;
                        }
                    }
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
            EnforcePolylineClosure();

            points.Clear();
            foreach (var p in newPoints)
                points.Add(p);

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

            double perimeter = ComputePerimeter(pts);
            double area = Math.Abs(ComputeSignedArea(pts));
            double[] bbox = ComputeBoundingBox(pts);
            double bboxW = bbox[2] - bbox[0];
            double bboxH = bbox[3] - bbox[1];

            AspectRatioResult = bboxH > 0 ? bboxW / bboxH : 0;
            PerimeterAreaRatioResult = area > 0 ? perimeter / area : 0;
            // Circularity = 4π·A / P²  (1.0 = perfect circle, approaches 0 for elongated shapes)
            CircularityResult = perimeter > 0 ? (4.0 * Math.PI * area) / (perimeter * perimeter) : 0;

            int harmonics = 10;
            EFDCoefficientsResult = ComputeNormalizedEFD(pts, harmonics);

            // Build display string
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Aspect Ratio:       {AspectRatioResult:F3}");
            sb.AppendLine($"Perim / Area:       {PerimeterAreaRatioResult:F4}");
            sb.AppendLine($"Circularity:        {CircularityResult:F4}");
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
            }
        }

        // =====================
        // GEOMETRY HELPERS
        // =====================

        private double ComputePerimeter(List<Point> pts)
        {
            double p = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                p += Math.Sqrt(dx * dx + dy * dy);
            }
            return p;
        }

        // Shoelace formula — returns signed area (positive = CCW)
        private double ComputeSignedArea(List<Point> pts)
        {
            double area = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2.0;
        }

        private double[] ComputeBoundingBox(List<Point> pts)
        {
            double minX = pts[0].X, maxX = pts[0].X;
            double minY = pts[0].Y, maxY = pts[0].Y;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new double[] { minX, minY, maxX, maxY };
        }

        // =====================
        // ELLIPTIC FOURIER DESCRIPTORS
        // =====================
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
        #endregion
    }
}