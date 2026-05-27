using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Windows;


namespace DinoLino.Utilities
{
    /// <summary>
    /// All pixel-level image processing for OutlineMode.
    /// Stateless except for reusable buffers to avoid repeated allocations.
    /// OutlineMode owns one instance and passes it parameters; no UI types here.
    /// </summary>
    internal class OutlineProcessor
    {
        // =====================
        // REUSABLE BUFFERS
        // =====================
        private int[] _distBuffer = Array.Empty<int>();
        private int[] _queueBuffer = Array.Empty<int>();
        private int _qHead;
        private int _qTail;

        private int[] _cachedGradient = null;
        private int _cachedGradientWidth = 0;
        private int _cachedGradientHeight = 0;

        private void EnsureBuffers(int size)
        {
            if (_distBuffer.Length < size)
            {
                _distBuffer = new int[size];
                _queueBuffer = new int[size * 2];
            }
        }

        private void EnsureQueue(int capacity)
        {
            if (_queueBuffer == null || _queueBuffer.Length < capacity)
                _queueBuffer = new int[capacity];
        }

        internal void BuildGradientCache(ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            var gradient = new int[total];

            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    (byte r00, byte g00, byte b00) = ReadPixel(x - 1, y - 1, snap);
                    (byte r10, byte g10, byte b10) = ReadPixel(x, y - 1, snap);
                    (byte r20, byte g20, byte b20) = ReadPixel(x + 1, y - 1, snap);
                    (byte r01, byte g01, byte b01) = ReadPixel(x - 1, y, snap);
                    (byte r21, byte g21, byte b21) = ReadPixel(x + 1, y, snap);
                    (byte r02, byte g02, byte b02) = ReadPixel(x - 1, y + 1, snap);
                    (byte r12, byte g12, byte b12) = ReadPixel(x, y + 1, snap);
                    (byte r22, byte g22, byte b22) = ReadPixel(x + 1, y + 1, snap);

                    int gxR = -r00 + r20 - 2 * r01 + 2 * r21 - r02 + r22;
                    int gyR = -r00 - 2 * r10 - r20 + r02 + 2 * r12 + r22;
                    int gxG = -g00 + g20 - 2 * g01 + 2 * g21 - g02 + g22;
                    int gyG = -g00 - 2 * g10 - g20 + g02 + 2 * g12 + g22;
                    int gxB = -b00 + b20 - 2 * b01 + 2 * b21 - b02 + b22;
                    int gyB = -b00 - 2 * b10 - b20 + b02 + 2 * b12 + b22;

                    int gx = 2 * gxR + 4 * gxG + 3 * gxB;
                    int gy = 2 * gyR + 4 * gyG + 3 * gyB;
                    gradient[y * w + x] = gx * gx + gy * gy;
                }
            }

            _cachedGradient = gradient;
            _cachedGradientWidth = w;
            _cachedGradientHeight = h;
        }
        // =====================
        // SNAPSHOT TYPE
        // =====================
        internal readonly struct ImageSnapshot
        {
            public readonly byte[] Pixels;
            public readonly bool[] BgMask;
            public readonly int Width;
            public readonly int Height;
            public readonly int Stride;
            public readonly int Bpp;

            public ImageSnapshot(byte[] pixels, bool[] bgMask, int w, int h, int stride, int bpp)
            {
                Pixels = pixels; BgMask = bgMask;
                Width = w; Height = h; Stride = stride; Bpp = bpp;
            }
        }

        // =====================
        // PIXEL ACCESS
        // =====================
        internal (byte r, byte g, byte b) ReadPixel(int x, int y, ImageSnapshot snap)
        {
            int i = y * snap.Stride + x * snap.Bpp;
            if (snap.Bpp == 1) return (snap.Pixels[i], snap.Pixels[i], snap.Pixels[i]);
            return (snap.Pixels[i + 2], snap.Pixels[i + 1], snap.Pixels[i]);
        }

        internal double PerceptualDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
        {
            double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
            return Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db);
        }

        // =====================
        // BACKGROUND MASK
        // =====================
        internal bool[] BuildBackgroundMask(
            byte[] pixels, int w, int h, int stride, int bpp,
            (double r, double g, double b)[] bgColors,
            double threshold = 25)
        {
            bool[] background = new bool[w * h];
            EnsureQueue(w * h);
            _qHead = 0; _qTail = 0;

            void TryAdd(int x, int y, int colorIndex)
            {
                int i = y * w + x;
                if (background[i]) return;
                int pi = y * stride + x * bpp;
                byte pr = bpp == 1 ? pixels[pi] : pixels[pi + 2];
                byte pg = bpp == 1 ? pixels[pi] : pixels[pi + 1];
                byte pb = bpp == 1 ? pixels[pi] : pixels[pi];
                var (br, bg, bb) = bgColors[colorIndex];
                double dist = PerceptualDistance(pr, pg, pb,
                    (byte)Math.Max(0, Math.Min(255, br)),
                    (byte)Math.Max(0, Math.Min(255, bg)),
                    (byte)Math.Max(0, Math.Min(255, bb)));
                if (dist > threshold) return;
                background[i] = true;
                _queueBuffer[_qTail++] = y * w + x;
            }

            for (int x = 0; x < w; x++)
            {
                TryAdd(x, 0, x < w / 2 ? 0 : 1);
                TryAdd(x, h - 1, x < w / 2 ? 2 : 3);
            }
            for (int y = 1; y < h - 1; y++)
            {
                TryAdd(0, y, y < h / 2 ? 0 : 2);
                TryAdd(w - 1, y, y < h / 2 ? 1 : 3);
            }

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];
                int cx = ci % w, cy = ci / w;
                int colorIndex = (cx < w / 2 ? 0 : 1) + (cy < h / 2 ? 0 : 2);
                if (cx > 0) TryAdd(cx - 1, cy, (cx - 1 < w / 2 ? 0 : 1) + (cy < h / 2 ? 0 : 2));
                if (cx < w - 1) TryAdd(cx + 1, cy, (cx + 1 < w / 2 ? 0 : 1) + (cy < h / 2 ? 0 : 2));
                if (cy > 0) TryAdd(cx, cy - 1, (cx < w / 2 ? 0 : 1) + (cy - 1 < h / 2 ? 0 : 2));
                if (cy < h - 1) TryAdd(cx, cy + 1, (cx < w / 2 ? 0 : 1) + (cy + 1 < h / 2 ? 0 : 2));
            }

            return background;
        }

        // =====================
        // MASK UTILITIES
        // =====================
        internal (int x0, int y0, int x1, int y1) GetMaskBounds(bool[] mask, int w, int h, int margin = 2)
        {
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (!mask[row + x]) continue;
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            }
            if (x0 > x1) return (0, 0, 0, 0);
            return (Math.Max(0, x0 - margin), Math.Max(0, y0 - margin),
                    Math.Min(w - 1, x1 + margin), Math.Min(h - 1, y1 + margin));
        }

        internal (bool[] cropped, int cw, int ch) CropMask(bool[] mask, int w, int x0, int y0, int x1, int y1)
        {
            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
            var cropped = new bool[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    cropped[y * cw + x] = mask[(y0 + y) * w + (x0 + x)];
            return (cropped, cw, ch);
        }

        internal bool HasMinimumPixels(bool[] mask, int minimum)
        {
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i] && ++count >= minimum) return true;
            return false;
        }

        internal int CountPixels(bool[] mask)
        {
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) count++;
            return count;
        }

        private int CountCardinalNeighbors(bool[] mask, int x, int y, int w, int h)
        {
            int count = 0, i = y * w + x;
            if (x > 0 && mask[i - 1]) count++;
            if (x < w - 1 && mask[i + 1]) count++;
            if (y > 0 && mask[i - w]) count++;
            if (y < h - 1 && mask[i + w]) count++;
            return count;
        }

        // =====================
        // FLOOD FILL
        // =====================
        internal bool[] FloodFill(int startX, int startY, byte sr, byte sg, byte sb,
            ImageSnapshot snap, double tolerance, double gradientLeniency, double edgeThreshold)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] inside = new bool[total];
            EnsureQueue(total);
            _qHead = 0; _qTail = 0;
            int seed = startY * w + startX;
            _queueBuffer[_qTail++] = seed;
            inside[seed] = true;

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];
                int cx = ci % w, cy = ci / w;
                int pi = cy * snap.Stride + cx * snap.Bpp;
                byte cr = snap.Bpp == 1 ? snap.Pixels[pi] : snap.Pixels[pi + 2];
                byte cg = snap.Bpp == 1 ? snap.Pixels[pi] : snap.Pixels[pi + 1];
                byte cb = snap.Bpp == 1 ? snap.Pixels[pi] : snap.Pixels[pi];

                void TryAdd(int nx, int ny)
                {
                    int ni = ny * w + nx;
                    if (inside[ni]) return;
                    if (snap.BgMask != null && snap.BgMask[ni]) return;
                    int npi = ny * snap.Stride + nx * snap.Bpp;
                    byte pr = snap.Bpp == 1 ? snap.Pixels[npi] : snap.Pixels[npi + 2];
                    byte pg = snap.Bpp == 1 ? snap.Pixels[npi] : snap.Pixels[npi + 1];
                    byte pb = snap.Bpp == 1 ? snap.Pixels[npi] : snap.Pixels[npi];
                    double seedDist = PerceptualDistance(pr, pg, pb, sr, sg, sb);
                    double neighborDist = PerceptualDistance(pr, pg, pb, cr, cg, cb);
                    bool strongEdge = neighborDist > edgeThreshold;
                    bool seedMatch = seedDist <= tolerance;
                    bool gradientMatch = neighborDist <= gradientLeniency && seedDist <= tolerance * 6.0;
                    if ((!strongEdge || seedMatch) && (seedMatch || gradientMatch))
                    { inside[ni] = true; _queueBuffer[_qTail++] = ni; }
                }

                if (cx > 0) TryAdd(cx - 1, cy);
                if (cx < w - 1) TryAdd(cx + 1, cy);
                if (cy > 0) TryAdd(cx, cy - 1);
                if (cy < h - 1) TryAdd(cx, cy + 1);
            }
            return inside;
        }

        // =====================
        // FILL HOLES
        // =====================
        internal bool[] FillHoles(bool[] mask, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] exterior = new bool[total];
            EnsureQueue(total);
            _qHead = 0; _qTail = 0;

            void TrySeed(int x, int y)
            {
                int i = y * w + x;
                if (mask[i] || exterior[i]) return;
                exterior[i] = true; _queueBuffer[_qTail++] = i;
            }

            for (int x = 0; x < w; x++) { TrySeed(x, 0); TrySeed(x, h - 1); }
            for (int y = 1; y < h - 1; y++) { TrySeed(0, y); TrySeed(w - 1, y); }

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];
                int cx = ci % w, cy = ci / w;
                if (cx > 0) { int ni = ci - 1; if (!mask[ni] && !exterior[ni]) { exterior[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cx < w - 1) { int ni = ci + 1; if (!mask[ni] && !exterior[ni]) { exterior[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cy > 0) { int ni = ci - w; if (!mask[ni] && !exterior[ni]) { exterior[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cy < h - 1) { int ni = ci + w; if (!mask[ni] && !exterior[ni]) { exterior[ni] = true; _queueBuffer[_qTail++] = ni; } }
            }

            bool[] filled = (bool[])mask.Clone();
            for (int i = 0; i < total; i++)
                if (!mask[i] && !exterior[i]) filled[i] = true;
            return filled;
        }

        // =====================
        // COMPONENT OPERATIONS
        // =====================
        internal bool[] ExtractLargestComponent(bool[] inside, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] visited = new bool[total], result = new bool[total];
            int bestSeed = -1, bestCount = 0;

            for (int start = 0; start < total; start++)
            {
                if (!inside[start] || visited[start]) continue;
                EnsureQueue(total);
                _qHead = 0; _qTail = 0;
                _queueBuffer[_qTail++] = start;
                visited[start] = true;
                int count = 0;
                while (_qHead < _qTail)
                {
                    int ci = _queueBuffer[_qHead++]; count++;
                    int cx = ci % w, cy = ci / w;
                    if (cx > 0) { int ni = ci - 1; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                    if (cx < w - 1) { int ni = ci + 1; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                    if (cy > 0) { int ni = ci - w; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                    if (cy < h - 1) { int ni = ci + w; if (inside[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                }
                if (count > bestCount) { bestCount = count; bestSeed = start; }
            }

            if (bestSeed < 0) return result;
            EnsureQueue(total);
            _qHead = 0; _qTail = 0;
            _queueBuffer[_qTail++] = bestSeed; result[bestSeed] = true;
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

        internal List<int> CollectComponent(bool[] mask, int seed, ImageSnapshot snap, bool[] visited = null)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            var component = new List<int>();
            if (seed < 0 || seed >= total || !mask[seed]) return component;
            if (visited == null) visited = new bool[total];
            EnsureQueue(total);
            _qHead = 0; _qTail = 0;
            _queueBuffer[_qTail++] = seed; visited[seed] = true;
            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++]; component.Add(ci);
                int cx = ci % w, cy = ci / w;
                if (cx > 0) { int ni = ci - 1; if (mask[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cx < w - 1) { int ni = ci + 1; if (mask[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cy > 0) { int ni = ci - w; if (mask[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
                if (cy < h - 1) { int ni = ci + w; if (mask[ni] && !visited[ni]) { visited[ni] = true; _queueBuffer[_qTail++] = ni; } }
            }
            return component;
        }

        internal bool[] KeepComponentContainingSeed(bool[] inside, int seed, ImageSnapshot snap)
        {
            int total = snap.Width * snap.Height;
            bool[] result = new bool[total];
            foreach (int i in CollectComponent(inside, seed, snap))
                result[i] = true;
            return result;
        }

        internal bool[] KeepLargestComponent(bool[] inside, ImageSnapshot snap)
        {
            int total = snap.Width * snap.Height;
            bool[] visited = new bool[total], best = new bool[total];
            List<int> bestComponent = null;
            for (int start = 0; start < total; start++)
            {
                if (!inside[start] || visited[start]) continue;
                var component = CollectComponent(inside, start, snap, visited);
                if (bestComponent == null || component.Count > bestComponent.Count)
                    bestComponent = component;
            }
            if (bestComponent == null) return best;
            foreach (int i in bestComponent) best[i] = true;
            return best;
        }

        internal bool[] ExpandConnectedComponentToArea(bool[] mask, int seed, int minArea, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] current = (bool[])mask.Clone();
            if (seed < 0 || seed >= total) return current;
            if (!current[seed]) current = KeepComponentContainingSeed(current, seed, snap);
            int currentArea = CountPixels(current);
            if (currentArea >= minArea) return current;
            EnsureQueue(total);
            _qHead = 0; _qTail = 0;
            for (int i = 0; i < total; i++) if (current[i]) _queueBuffer[_qTail++] = i;
            while (_qHead < _qTail && currentArea < minArea)
            {
                int ci = _queueBuffer[_qHead++];
                int cx = ci % w, cy = ci / w;
                void TryAdd(int ni) { if (current[ni]) return; current[ni] = true; _queueBuffer[_qTail++] = ni; currentArea++; }
                if (cx > 0) TryAdd(ci - 1);
                if (cx < w - 1) TryAdd(ci + 1);
                if (cy > 0) TryAdd(ci - w);
                if (cy < h - 1) TryAdd(ci + w);
            }
            return current;
        }

        // =====================
        // TOPOLOGY / SHAPE PROCESSING
        // =====================
        internal void EnforceMinimumCorridorWidth(bool[] mask, ImageSnapshot snap, int minWidth = 3)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            if (mask.Length != total) return;
            int minRadius = Math.Max(1, (minWidth + 1) / 2);
            EnsureBuffers(total);
            var dist = _distBuffer;
            var queue = _queueBuffer;
            for (int i = 0; i < total; i++) dist[i] = mask[i] ? int.MaxValue / 4 : 0;
            int head = 0, tail = 0;
            for (int i = 0; i < total; i++) if (dist[i] == 0) queue[tail++] = i;
            while (head < tail)
            {
                int i = queue[head++]; int d = dist[i] + 1;
                int x = i % w, y = i / w;
                void Relax(int ni) { if (dist[ni] > d) { dist[ni] = d; queue[tail++] = ni; } }
                if (x > 0) Relax(i - 1); if (x < w - 1) Relax(i + 1);
                if (y > 0) Relax(i - w); if (y < h - 1) Relax(i + w);
            }
            bool[] copy = (bool[])mask.Clone();
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (!copy[i]) continue;
                    bool thin =
                        (x > 0 && copy[i - 1] && dist[i - 1] < minRadius) ||
                        (x < w - 1 && copy[i + 1] && dist[i + 1] < minRadius) ||
                        (y > 0 && copy[i - w] && dist[i - w] < minRadius) ||
                        (y < h - 1 && copy[i + w] && dist[i + w] < minRadius);
                    if (thin) mask[i] = false;
                }
            }
        }

        internal void SmoothBorderTopology(bool[] mask, ImageSnapshot snap, int passes = 3)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] copy = new bool[total];
            for (int pass = 0; pass < passes; pass++)
            {
                Array.Copy(mask, copy, total);
                for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        if (!copy[i]) continue;
                        bool n = copy[i - w], s = copy[i + w], e = copy[i + 1], wv = copy[i - 1];
                        bool ne = copy[i + 1 - w], nw = copy[i - 1 - w], se = copy[i + 1 + w], sw = copy[i - 1 + w];
                        bool diagonalBridge = (ne && sw && !n && !e && !s && !wv) || (nw && se && !n && !e && !s && !wv);
                        bool cornerTouch = (n && e && !ne) || (e && s && !se) || (s && wv && !sw) || (wv && n && !nw);
                        bool junction = CountCardinalNeighbors(copy, x, y, w, h) >= 3;
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
                                else if (!se) mask[i + 1 + w] = true; else if (!sw) mask[i - 1 + w] = true;
                            }
                        }
                    }
            }
        }

        internal void PruneDeadEnds(bool[] mask, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            EnsureQueue(total * 2);
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
                    if (cx > 0) { int ni = ci - 1; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                    if (cx < w - 1) { int ni = ci + 1; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                    if (cy > 0) { int ni = ci - w; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                    if (cy < h - 1) { int ni = ci + w; if (mask[ni]) _queueBuffer[_qTail++] = ni; }
                }
            }
        }

        // =====================
        // BOUNDARY TRACING
        // =====================
        internal List<System.Windows.Point> TraceBoundary(bool[] inside, ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height;
            int startX = -1, startY = -1;
            for (int i = 0; i < inside.Length && startX < 0; i++)
                if (inside[i]) { startX = i % w; startY = i / w; }
            if (startX < 0) return new List<System.Windows.Point>();

            var boundary = new List<System.Windows.Point>();
            int[] ndx = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] ndy = { 0, -1, -1, -1, 0, 1, 1, 1 };
            int cx = startX, cy = startY, dir = 4;

            for (int iterations = 0; iterations < w * h * 2; iterations++)
            {
                boundary.Add(new System.Windows.Point(cx, cy));
                int checkDir = (dir + 6) % 8;
                bool found = false;
                for (int i = 0; i < 8; i++)
                {
                    int d = (checkDir + i) % 8;
                    int bx = cx + ndx[d], by = cy + ndy[d];
                    if ((uint)bx >= w || (uint)by >= h) continue;
                    if (!inside[by * w + bx]) continue;
                    dir = d; cx = bx; cy = by; found = true; break;
                }
                if (!found) break;
                if (cx == startX && cy == startY) break;
            }
            return boundary;
        }

        // =====================
        // TRACING PREPARATION
        // =====================
        internal bool[] PrepareMaskForTracing(bool[] pruned, bool[] raw,
            int seedX, int seedY, int minArea, ImageSnapshot snap)
        {
            bool[] candidate = (bool[])pruned.Clone();
            int area = CountPixels(candidate);
            if (area >= minArea) return candidate;
            int seed = seedY * snap.Width + seedX;
            if (!candidate[seed]) { candidate = KeepComponentContainingSeed(raw, seed, snap); area = CountPixels(candidate); }
            if (area >= minArea) return candidate;
            candidate = ExpandConnectedComponentToArea(candidate, seed, minArea, snap);
            if (!HasMinimumPixels(candidate, minArea)) return candidate;
            bool[] fallback = KeepComponentContainingSeed(raw, seed, snap);
            if (!HasMinimumPixels(fallback, minArea)) return fallback;
            return fallback;
        }

        internal bool[]? WatershedSegment(int seedX, int seedY, ImageSnapshot snap,
    int seedRadius = 3, int blurLevel = 0)
        {
            int w = snap.Width, h = snap.Height, total = w * h;

            // ── 1. Optional pre-blur ──────────────────────────────────────────────
            // blurLevel 0 = none, 1 = 1 pass box blur, 2 = 2 passes box blur
            ImageSnapshot workSnap = snap;
            if (blurLevel > 0)
            {
                byte[] blurred = (byte[])snap.Pixels.Clone();
                for (int pass = 0; pass < blurLevel; pass++)
                {
                    byte[] src = blurred;
                    byte[] dst = new byte[src.Length];
                    for (int y = 1; y < h - 1; y++)
                    {
                        for (int x = 1; x < w - 1; x++)
                        {
                            int tr = 0, tg = 0, tb = 0;
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int si = (y + dy) * snap.Stride + (x + dx) * snap.Bpp;
                                    tb += src[si];
                                    tg += src[si + 1];
                                    tr += src[si + 2];
                                }
                            int di = y * snap.Stride + x * snap.Bpp;
                            dst[di] = (byte)(tb / 9);
                            dst[di + 1] = (byte)(tg / 9);
                            dst[di + 2] = (byte)(tr / 9);
                            if (snap.Bpp == 4) dst[di + 3] = src[di + 3];
                        }
                    }
                    blurred = dst;
                }
                workSnap = new ImageSnapshot(blurred, snap.BgMask, w, h, snap.Stride, snap.Bpp);
            }

            // ── 2. Compute Sobel gradient ─────────────────────────────────────────
            // Use cached gradient when no blur is requested.
            // When blur is active, compute fresh from the blurred workSnap.
            int[] gradient;
            if (blurLevel == 0 &&
                _cachedGradient != null &&
                _cachedGradientWidth == w &&
                _cachedGradientHeight == h)
            {
                gradient = _cachedGradient;
            }
            else
            {
                gradient = new int[total];
                for (int y = 1; y < h - 1; y++)
                {
                    for (int x = 1; x < w - 1; x++)
                    {
                        (byte r00, byte g00, byte b00) = ReadPixel(x - 1, y - 1, workSnap);
                        (byte r10, byte g10, byte b10) = ReadPixel(x, y - 1, workSnap);
                        (byte r20, byte g20, byte b20) = ReadPixel(x + 1, y - 1, workSnap);
                        (byte r01, byte g01, byte b01) = ReadPixel(x - 1, y, workSnap);
                        (byte r21, byte g21, byte b21) = ReadPixel(x + 1, y, workSnap);
                        (byte r02, byte g02, byte b02) = ReadPixel(x - 1, y + 1, workSnap);
                        (byte r12, byte g12, byte b12) = ReadPixel(x, y + 1, workSnap);
                        (byte r22, byte g22, byte b22) = ReadPixel(x + 1, y + 1, workSnap);

                        int gxR = -r00 + r20 - 2 * r01 + 2 * r21 - r02 + r22;
                        int gyR = -r00 - 2 * r10 - r20 + r02 + 2 * r12 + r22;
                        int gxG = -g00 + g20 - 2 * g01 + 2 * g21 - g02 + g22;
                        int gyG = -g00 - 2 * g10 - g20 + g02 + 2 * g12 + g22;
                        int gxB = -b00 + b20 - 2 * b01 + 2 * b21 - b02 + b22;
                        int gyB = -b00 - 2 * b10 - b20 + b02 + 2 * b12 + b22;

                        int gx = 2 * gxR + 4 * gxG + 3 * gxB;
                        int gy = 2 * gyR + 4 * gyG + 3 * gyB;
                        gradient[y * w + x] = gx * gx + gy * gy;
                    }
                }
            }

            // ── 3. Labels and heap ───────────────────────────────────────────────
            int[] labels = new int[total];
            for (int i = 0; i < total; i++) labels[i] = -1;

            var heap = new SortedSet<(int grad, int idx)>(
                Comparer<(int grad, int idx)>.Create((a, b) =>
                    a.grad != b.grad ? a.grad.CompareTo(b.grad) : a.idx.CompareTo(b.idx)));

            // Border → background
            for (int x = 0; x < w; x++)
            {
                int ti = x, bi = (h - 1) * w + x;
                if (labels[ti] == -1) { labels[ti] = 0; heap.Add((gradient[ti], ti)); }
                if (labels[bi] == -1) { labels[bi] = 0; heap.Add((gradient[bi], bi)); }
            }
            for (int y = 1; y < h - 1; y++)
            {
                int li = y * w, ri = y * w + w - 1;
                if (labels[li] == -1) { labels[li] = 0; heap.Add((gradient[li], li)); }
                if (labels[ri] == -1) { labels[ri] = 0; heap.Add((gradient[ri], ri)); }
            }

            // BgMask → background
            if (snap.BgMask != null)
                for (int i = 0; i < total; i++)
                    if (snap.BgMask[i] && labels[i] == -1)
                    { labels[i] = 0; heap.Add((gradient[i], i)); }

            // Seed circle → foreground (uses seedRadius parameter)
            for (int dy = -seedRadius; dy <= seedRadius; dy++)
                for (int dx = -seedRadius; dx <= seedRadius; dx++)
                {
                    if (dx * dx + dy * dy > seedRadius * seedRadius) continue;
                    int nx = seedX + dx, ny = seedY + dy;
                    if ((uint)nx >= w || (uint)ny >= h) continue;
                    int ni = ny * w + nx;
                    if (labels[ni] == -1) { labels[ni] = 1; heap.Add((gradient[ni], ni)); }
                }

            // ── 4. Flood ─────────────────────────────────────────────────────────
            int[] ndx4 = { 1, -1, 0, 0 };
            int[] ndy4 = { 0, 0, 1, -1 };

            while (heap.Count > 0)
            {
                var (_, ci) = heap.Min;
                heap.Remove(heap.Min);
                int cx = ci % w, cy = ci / w;
                int myLabel = labels[ci];
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + ndx4[d], ny = cy + ndy4[d];
                    if ((uint)nx >= w || (uint)ny >= h) continue;
                    int ni = ny * w + nx;
                    if (labels[ni] != -1) continue;

                    // Hard stop: BgMask pixels can never be claimed as foreground.
                    // Force them to background regardless of which label is flooding them.
                    if (snap.BgMask != null && snap.BgMask[ni])
                    {
                        labels[ni] = 0;
                        heap.Add((gradient[ni], ni));
                        continue;
                    }

                    labels[ni] = myLabel;
                    heap.Add((gradient[ni], ni));
                }
            }

            // ── 5. Extract mask, enforcing BgMask as hard stop ───────────────────
            bool[] mask = new bool[total];
            for (int i = 0; i < total; i++)
                mask[i] = labels[i] == 1;

            // Hard-remove any foreground pixels that overlap confirmed background.
            // This prevents the foreground basin from claiming background regions
            // when no strong gradient ridge exists between them.
            if (snap.BgMask != null)
                for (int i = 0; i < total; i++)
                    if (snap.BgMask[i]) mask[i] = false;

            int seedIndex = seedY * w + seedX;
            if (seedIndex >= 0 && seedIndex < total && mask[seedIndex])
                return KeepComponentContainingSeed(mask, seedIndex,
                       new ImageSnapshot(snap.Pixels, snap.BgMask, w, h, snap.Stride, snap.Bpp));

            return null;
        }
    }
}