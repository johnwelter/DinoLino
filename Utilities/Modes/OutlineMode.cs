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

        // =====================
        // SOURCE IMAGE + CACHE
        // =====================
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

            _cachedWidth = _sourceImage.PixelWidth;
            _cachedHeight = _sourceImage.PixelHeight;
            _cachedBpp = (_sourceImage.Format.BitsPerPixel + 7) / 8;
            _cachedStride = (_cachedWidth * _sourceImage.Format.BitsPerPixel + 7) / 8;
            _cachedPixels = new byte[_cachedStride * _cachedHeight];
            _sourceImage.CopyPixels(_cachedPixels, _cachedStride, 0);
        }

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

        // =====================
        // PROCESS CLICK
        // =====================
        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            var output = new List<UIElement>();
            if (_cachedPixels == null) return output;

            int px = (int)((mousePos.X - OffsetX) / ScaleX);
            int py = (int)((mousePos.Y - OffsetY) / ScaleY);

            if ((uint)px >= _cachedWidth || (uint)py >= _cachedHeight)
                return output;

            (byte sr, byte sg, byte sb) = ReadPixel(px, py);

            bool[] raw = FloodFill(px, py, sr, sg, sb);
            if (CountPixels(raw) == 0) return output;

            bool[] work = (bool[])raw.Clone();

            MorphologicalErosion(work);
            work = ExtractLargestComponent(work);
            DistancePruneNarrowPassages(work);
            PreventSelfTouchingTopology(work);

            work = KeepLargestComponent(work);

            bool[] tracedMask = PrepareMaskForTracing(work, raw, px, py, MinAreaPixels);

            List<Point> boundary = TraceBoundary(tracedMask);

            if (!HasMinimumBorderLength(boundary, 8))
            {
                bool[] expanded = ExpandConnectedComponentToArea(
                    KeepComponentContainingSeed(raw, py * _cachedWidth + px),
                    py * _cachedWidth + px,
                    MinAreaPixels);

                boundary = TraceBoundary(expanded);
            }

            if (boundary.Count < 8) return output;

            List<Point> simplified = DouglasPeucker(boundary, _simplifyEpsilon);
            if (simplified.Count < 3) return output;

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

            output.Add(polyline);

            CommitOperation(new OutlineOperation
            {
                OperationKind = "Outline",
                SourceMode = this,
                Elements = new List<UIElement>(output)
            });

            return output;
        }

        // =====================
        // PIXEL ACCESS
        // =====================

        // Single consolidated pixel reader — handles grayscale (bpp==1) and color.
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
        private bool[] FloodFill(int startX, int startY, byte sr, byte sg, byte sb)
        {
            int w = _cachedWidth, h = _cachedHeight;
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

                // Read current pixel for neighbor-distance check
                int ci = cy * _cachedStride + cx * _cachedBpp;
                byte cr, cg, cb;
                if (_cachedBpp == 1)
                {
                    cr = cg = cb = _cachedPixels[ci];
                }
                else
                {
                    cr = _cachedPixels[ci + 2];
                    cg = _cachedPixels[ci + 1];
                    cb = _cachedPixels[ci];
                }

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d];
                    int ny = cy + dy[d];

                    if ((uint)nx >= w || (uint)ny >= h)
                        continue;

                    int ni = ny * w + nx;
                    if (inside[ni]) continue;  // already visited

                    // Read neighbor pixel
                    int npi = ny * _cachedStride + nx * _cachedBpp;
                    byte pr, pg, pb;
                    if (_cachedBpp == 1)
                    {
                        pr = pg = pb = _cachedPixels[npi];
                    }
                    else
                    {
                        pr = _cachedPixels[npi + 2];
                        pg = _cachedPixels[npi + 1];
                        pb = _cachedPixels[npi];
                    }

                    // Compute seed distance first; skip immediately if too far
                    double seedDist = PerceptualDistance(pr, pg, pb, sr, sg, sb);
                    if (seedDist > toleranceTimes2) continue;

                    // Edge-stop: if this neighbor is a strong edge AND far from seed, block it
                    double neighborDist = PerceptualDistance(pr, pg, pb, cr, cg, cb);
                    bool strongEdge = neighborDist > _edgeThreshold;
                    if (strongEdge && seedDist > _tolerance) continue;

                    // Final match: direct seed match, or gradient continuation close to seed
                    bool matches =
                        seedDist <= _tolerance ||
                        (neighborDist <= _gradientLeniency && seedDist <= toleranceTimes2);

                    if (!matches) continue;

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
        private void MorphologicalErosion(bool[] inside)
        {
            int w = _cachedWidth, h = _cachedHeight;
            bool[] copy = (bool[])inside.Clone();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!copy[i]) continue;

                    bool borderExposed =
                        x == 0 || x == w - 1 || y == 0 || y == h - 1 ||
                        !copy[i - 1] ||
                        !copy[i + 1] ||
                        !copy[i - w] ||
                        !copy[i + w];

                    if (borderExposed)
                        inside[i] = false;
                }
            }
        }

        // =====================
        // EXTRACT LARGEST COMPONENT
        // =====================
        private bool[] ExtractLargestComponent(bool[] inside)
        {
            int w = _cachedWidth, h = _cachedHeight;
            int total = w * h;
            bool[] labeled = new bool[total];
            bool[] result = new bool[total];

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            int bestSeed = -1;
            int bestCount = 0;

            // Pass 1: find which component is largest, record only its seed index
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
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
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

            // Pass 2: BFS from best seed to populate result cleanly
            var resultQueue = new Queue<int>();
            resultQueue.Enqueue(bestSeed);
            result[bestSeed] = true;

            while (resultQueue.Count > 0)
            {
                int ci = resultQueue.Dequeue();
                int cx = ci % w, cy = ci / w;

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d];
                    int ny = cy + dy[d];
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
        private List<Point> TraceBoundary(bool[] inside)
        {
            int w = _cachedWidth, h = _cachedHeight;

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

        private void DouglasPeuckerRecursive(
    List<Point> points, int start, int end,
    double epsilon, List<Point> result)
        {
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
        private void DistancePruneNarrowPassages(bool[] mask, int minSeparationPixels = 5)
        {
            int w = _cachedWidth, h = _cachedHeight;
            int total = w * h;

            int minRadius = (minSeparationPixels + 1) / 2; // 5 -> 3
            if (minRadius < 1) minRadius = 1;

            bool changed = true;

            int[] dist = new int[total];
            int[] queue = new int[total];

            while (changed)
            {
                changed = false;

                // Initialize distance map:
                // foreground = large, background = 0
                for (int i = 0; i < total; i++)
                    dist[i] = mask[i] ? int.MaxValue / 4 : 0;

                int head = 0, tail = 0;

                // Multi-source BFS from background pixels.
                // This gives a 4-connected city-block distance to the nearest background.
                for (int i = 0; i < total; i++)
                {
                    if (dist[i] == 0)
                        queue[tail++] = i;
                }

                while (head < tail)
                {
                    int i = queue[head++];
                    int d = dist[i] + 1;

                    int x = i % w;
                    int y = i / w;

                    if (x > 0)
                    {
                        int ni = i - 1;
                        if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; }
                    }
                    if (x < w - 1)
                    {
                        int ni = i + 1;
                        if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; }
                    }
                    if (y > 0)
                    {
                        int ni = i - w;
                        if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; }
                    }
                    if (y < h - 1)
                    {
                        int ni = i + w;
                        if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; }
                    }
                }

                // Remove pixels that are too close to the boundary.
                // Any foreground pixel with radius < minRadius is inside a region thinner
                // than the requested minimum separation.
                for (int i = 0; i < total; i++)
                {
                    if (!mask[i]) continue;

                    if (dist[i] < minRadius)
                    {
                        mask[i] = false;
                        changed = true;
                    }
                }

                if (!changed)
                    break;

                // Keep only the largest connected component after pruning,
                // so separate fragments caused by bottlenecks do not remain.
                mask = KeepLargestComponent(mask);
            }
        }

        private bool[] KeepLargestComponent(bool[] inside, int minArea = 0, bool fallbackToLargestIfTooSmall = true)
        {
            int w = _cachedWidth, h = _cachedHeight, total = w * h;
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
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
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
                    foreach (int i in component)
                        best[i] = true;
                }
            }

            if (bestCount == 0)
                return best;

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

        private bool[] PrepareMaskForTracing(bool[] pruned, bool[] raw, int seedX, int seedY, int minArea)
        {
            bool[] candidate = (bool[])pruned.Clone();

            int area = CountPixels(candidate);
            if (area >= minArea)
                return candidate;

            int seed = seedY * _cachedWidth + seedX;

            if (!candidate[seed])
            {
                candidate = KeepComponentContainingSeed(raw, seed);
                area = CountPixels(candidate);
            }

            if (area >= minArea)
                return candidate;

            candidate = ExpandConnectedComponentToArea(candidate, seed, minArea);

            if (CountPixels(candidate) >= minArea)
                return candidate;

            bool[] fallback = KeepComponentContainingSeed(raw, seed);
            if (CountPixels(fallback) >= minArea)
                return fallback;

            return fallback;
        }

        private bool[] ExpandConnectedComponentToArea(bool[] mask, int seed, int minArea)
        {
            int w = _cachedWidth, h = _cachedHeight, total = w * h;

            bool[] current = (bool[])mask.Clone();

            if (seed < 0 || seed >= total)
                return current;

            if (!current[seed])
                current = KeepComponentContainingSeed(current, seed);

            int currentArea = CountPixels(current);
            if (currentArea >= minArea)
                return current;

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            var frontier = new Queue<int>();

            bool[] visited = new bool[total];
            for (int i = 0; i < total; i++)
                if (current[i]) visited[i] = true;

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
                        int nx = cx + dx[d];
                        int ny = cy + dy[d];
                        if ((uint)nx >= w || (uint)ny >= h) continue;

                        int ni = ny * w + nx;
                        if (current[ni]) continue;

                        current[ni] = true;
                        frontier.Enqueue(ni);
                        grew = true;
                        currentArea++;
                        if (currentArea >= minArea)
                            return current;
                    }
                }

                if (!grew)
                    break;
            }

            return current;
        }

        private bool[] KeepComponentContainingSeed(bool[] inside, int seed)
        {
            int w = _cachedWidth, h = _cachedHeight, total = w * h;
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
                    int nx = cx + dx[d];
                    int ny = cy + dy[d];
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
        private void PreventSelfTouchingTopology(bool[] mask)
        {
            int w = _cachedWidth;
            int h = _cachedHeight;

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
    }
}