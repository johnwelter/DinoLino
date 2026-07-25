using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;


namespace DinoLino.Utilities
{
    /// <summary>All pixel-level image processing for OutlineMode.</summary>
    internal class OutlineProcessor
    {
        // ---- REUSABLE SCRATCH BUFFERS ----
        private int[] _distBuffer = Array.Empty<int>();
        private int[] _queueBuffer = Array.Empty<int>();
        private int _qHead;
        private int _qTail;

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

        // ---- BINARY MIN-HEAP (watershed frontier) ----
        // Array-based min-heap of packed ((long)gradient << 32 | index) keys: no per-
        // element allocation, single sift-down per pop.
        private long[] _heap = Array.Empty<long>();
        private int _heapCount;

        private void HeapReset(int capacity)
        {
            if (_heap.Length < capacity) _heap = new long[capacity];
            _heapCount = 0;
        }

        private void HeapPush(int grad, int idx)
        {
            if (_heapCount >= _heap.Length)
                Array.Resize(ref _heap, Math.Max(64, _heap.Length * 2));
            long key = ((long)grad << 32) | (uint)idx;
            int i = _heapCount++;
            _heap[i] = key;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_heap[parent] <= _heap[i]) break;
                long t = _heap[parent]; _heap[parent] = _heap[i]; _heap[i] = t;
                i = parent;
            }
        }

        private int HeapPop()
        {
            long top = _heap[0];
            _heap[0] = _heap[--_heapCount];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, smallest = i;
                if (l < _heapCount && _heap[l] < _heap[smallest]) smallest = l;
                if (r < _heapCount && _heap[r] < _heap[smallest]) smallest = r;
                if (smallest == i) break;
                long t = _heap[i]; _heap[i] = _heap[smallest]; _heap[smallest] = t;
                i = smallest;
            }
            return (int)(top & 0xFFFFFFFF);
        }

        // ---- SNAPSHOT TYPE ----
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

        // ---- PIXEL ACCESS ----
        internal (byte r, byte g, byte b) ReadPixel(int x, int y, ImageSnapshot snap)
        {
            int i = y * snap.Stride + x * snap.Bpp;
            if (snap.Bpp == 1) return (snap.Pixels[i], snap.Pixels[i], snap.Pixels[i]);
            return (snap.Pixels[i + 2], snap.Pixels[i + 1], snap.Pixels[i]);
        }

        internal (byte r, byte g, byte b) SampleSeedColor(int cx, int cy, ImageSnapshot snap, int radius)
        {
            int w = snap.Width, h = snap.Height;
            int tr = 0, tg = 0, tb = 0, count = 0;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if ((uint)x >= w || (uint)y >= h) continue;
                    int idx = y * w + x;
                    // Exclude confirmed background pixels from the seed color average
                    if (snap.BgMask != null && snap.BgMask[idx]) continue;
                    var (r, g, b) = ReadPixel(x, y, snap);
                    tr += r; tg += g; tb += b; count++;
                }
            if (count == 0) return ReadPixel(cx, cy, snap);
            return ((byte)(tr / count), (byte)(tg / count), (byte)(tb / count));
        }

        /// Average local texture in a small window around the seed, skipping
        /// confirmed-background pixels — the texture analogue of SampleSeedColor.
        internal float SampleSeedTexture(int cx, int cy, ImageSnapshot snap, float[] textureMap, int radius)
        {
            int w = snap.Width, h = snap.Height;
            double t = 0; int count = 0;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if ((uint)x >= w || (uint)y >= h) continue;
                    int idx = y * w + x;
                    if (snap.BgMask != null && snap.BgMask[idx]) continue;
                    t += textureMap[idx]; count++;
                }
            if (count == 0) return textureMap[cy * w + cx];
            return (float)(t / count);
        }

        internal double PerceptualDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
            => PerceptualDistance((double)r1, g1, b1, r2, g2, b2);

        /// Same weighted-RGB metric over doubles — the ONE copy of the
        /// formula, shared with the background palette estimator below.
        internal static double PerceptualDistance(double r1, double g1, double b1, double r2, double g2, double b2)
        {
            double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
            return Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db);
        }

        // ---- CHROMA / TEXTURE TUNING ----
        // Calibration for FloodFillAdaptive's shading/texture-aware seed metric,
        // kept commensurate with the PerceptualDistance scale so FillSensitivity
        // keeps its meaning across both floods.
        private const double ChromaScale = 440.0;         // pure-hue differences score comparably to PerceptualDistance
        private const double LumaLeash = 0.30;            // residual luma weight inside the chroma metric — keeps same-hue,
                                                          // different-brightness OBJECTS from merging outright
        private const double TexturePenaltyWeight = 1.5;  // cost per unit of texture mismatch beyond the seed's own variation
        private const double TextureToleranceGain = 1.5;  // extra color tolerance per unit of seed texture (self-tuning FillSensitivity)
        private const double TextureToleranceCap = 45.0;
        private const double TextureEdgeGain = 1.5;       // internal texture contrast must not count as an object boundary
        private const double TextureEdgeCap = 60.0;
        private const double TextureLeniencyGain = 0.75;
        private const double TextureLeniencyCap = 30.0;

        /// Shading-tolerant distance for SEED comparisons: weights chroma heavily and
        /// luma lightly (the LumaLeash), since shading moves luma much more than
        /// chromaticity.
        internal double ChromaAwareSeedDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
        {
            double full = PerceptualDistance(r1, g1, b1, r2, g2, b2);

            double s1 = r1 + g1 + b1, s2 = r2 + g2 + b2;
            double minAvg = Math.Min(s1, s2) / 3.0;

            // Darkness guard: unreliable below ~15 average intensity, fully
            // reliable above ~60.
            double darkRel = (minAvg - 15.0) / 45.0;
            if (darkRel <= 0) return full;
            if (darkRel > 1) darkRel = 1;

            double nr1 = r1 / s1, ng1 = g1 / s1, nb1 = b1 / s1;
            double nr2 = r2 / s2, ng2 = g2 / s2, nb2 = b2 / s2;

            // Gray guard: needs at least one of the pair visibly saturated.
            double oneThird = 1.0 / 3.0;
            double dev1 = Math.Sqrt((nr1 - oneThird) * (nr1 - oneThird) + (ng1 - oneThird) * (ng1 - oneThird) + (nb1 - oneThird) * (nb1 - oneThird));
            double dev2 = Math.Sqrt((nr2 - oneThird) * (nr2 - oneThird) + (ng2 - oneThird) * (ng2 - oneThird) + (nb2 - oneThird) * (nb2 - oneThird));
            double grayRel = (Math.Max(dev1, dev2) - 0.03) / 0.10;
            if (grayRel <= 0) return full;
            if (grayRel > 1) grayRel = 1;

            double reliability = darkRel * grayRel;

            double dnr = nr1 - nr2, dng = ng1 - ng2, dnb = nb1 - nb2;
            double chroma = ChromaScale * Math.Sqrt(dnr * dnr + dng * dng + dnb * dnb);

            // A pure-luma step of d scores 3d in PerceptualDistance; match that
            // scale so LumaLeash reads as a fraction of FillSensitivity.
            double luma1 = (2 * r1 + 4 * g1 + 3 * b1) / 9.0;
            double luma2 = (2 * r2 + 4 * g2 + 3 * b2) / 9.0;
            double leash = LumaLeash * 3.0 * Math.Abs(luma1 - luma2);

            double chromaMetric = Math.Sqrt(chroma * chroma + leash * leash);

            return reliability * chromaMetric + (1.0 - reliability) * full;
        }

        // ---- PER-IMAGE ANALYSIS ----

        /// <summary>Squared weighted-Sobel gradient magnitude per pixel.</summary>
        internal int[] ComputeGradient(ImageSnapshot snap)
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

            return gradient;
        }

        /// BFS distance from every pixel to the nearest border/background pixel.
        internal int[] ComputeDistanceToBackground(int w, int h, bool[] bgMask)
        {
            int total = w * h;
            var dist = new int[total];
            var queue = new int[total];
            for (int i = 0; i < total; i++) dist[i] = int.MaxValue / 4;

            int head = 0, tail = 0;

            // Border pixels are hard background
            for (int x = 0; x < w; x++)
            {
                if (dist[x] != 0) { dist[x] = 0; queue[tail++] = x; }
                int bi = (h - 1) * w + x;
                if (dist[bi] != 0) { dist[bi] = 0; queue[tail++] = bi; }
            }
            for (int y = 1; y < h - 1; y++)
            {
                int li = y * w, ri = li + w - 1;
                if (dist[li] != 0) { dist[li] = 0; queue[tail++] = li; }
                if (dist[ri] != 0) { dist[ri] = 0; queue[tail++] = ri; }
            }

            // BgMask pixels are also hard background
            if (bgMask != null)
                for (int i = 0; i < total; i++)
                    if (bgMask[i] && dist[i] != 0) { dist[i] = 0; queue[tail++] = i; }

            // Multi-source BFS: unit edge weights + FIFO order means every pixel
            // settles the first time it is enqueued, so queue[total] suffices.
            while (head < tail)
            {
                int i = queue[head++];
                int d = dist[i] + 1;
                int x = i % w, y = i / w;
                if (x > 0 && dist[i - 1] > d) { dist[i - 1] = d; queue[tail++] = i - 1; }
                if (x < w - 1 && dist[i + 1] > d) { dist[i + 1] = d; queue[tail++] = i + 1; }
                if (y > 0 && dist[i - w] > d) { dist[i - w] = d; queue[tail++] = i - w; }
                if (y < h - 1 && dist[i + w] > d) { dist[i + w] = d; queue[tail++] = i + w; }
            }

            return dist;
        }

        /// Per-pixel local texture: std-dev of weighted luma (2r+4g+3b) over a
        /// (2·radius+1)² window, /3 to sit on the PerceptualDistance scale.
        internal float[] ComputeTextureMap(ImageSnapshot snap, int radius = 3)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            var tex = new float[total];
            if (w < 2 || h < 2) return tex;

            // Pass 1: horizontal box sums of luma and luma² per row.
            var hSum = new int[total];
            var hSq = new int[total];
            var rowLuma = new int[w];

            for (int y = 0; y < h; y++)
            {
                int rowPix = y * snap.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = rowPix + x * snap.Bpp;
                    int r, g, b;
                    if (snap.Bpp == 1) { r = g = b = snap.Pixels[i]; }
                    else { b = snap.Pixels[i]; g = snap.Pixels[i + 1]; r = snap.Pixels[i + 2]; }
                    rowLuma[x] = 2 * r + 4 * g + 3 * b;   // 0..2295
                }

                int row = y * w;
                int sum = 0, sq = 0;
                int x1 = Math.Min(w - 1, radius);
                for (int x = 0; x <= x1; x++) { sum += rowLuma[x]; sq += rowLuma[x] * rowLuma[x]; }
                for (int x = 0; x < w; x++)
                {
                    hSum[row + x] = sum;
                    hSq[row + x] = sq;
                    int add = x + radius + 1, sub = x - radius;
                    if (add < w) { sum += rowLuma[add]; sq += rowLuma[add] * rowLuma[add]; }
                    if (sub >= 0) { sum -= rowLuma[sub]; sq -= rowLuma[sub] * rowLuma[sub]; }
                }
            }

            // Pass 2: vertical sliding sums over the horizontal sums, then
            // σ = sqrt(E[x²] − E[x]²) with the exact per-pixel window count.
            var colSum = new int[w];
            var colSq = new int[w];
            int yInit = Math.Min(h - 1, radius);
            for (int y = 0; y <= yInit; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++) { colSum[x] += hSum[row + x]; colSq[x] += hSq[row + x]; }
            }

            for (int y = 0; y < h; y++)
            {
                int cy0 = Math.Max(0, y - radius), cy1 = Math.Min(h - 1, y + radius);
                int cntY = cy1 - cy0 + 1;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int cx0 = Math.Max(0, x - radius), cx1 = Math.Min(w - 1, x + radius);
                    int n = (cx1 - cx0 + 1) * cntY;
                    double mean = (double)colSum[x] / n;
                    double variance = (double)colSq[x] / n - mean * mean;
                    tex[row + x] = variance > 0 ? (float)(Math.Sqrt(variance) / 3.0) : 0f;
                }
                int addRow = y + radius + 1, subRow = y - radius;
                if (addRow < h)
                {
                    int ar = addRow * w;
                    for (int x = 0; x < w; x++) { colSum[x] += hSum[ar + x]; colSq[x] += hSq[ar + x]; }
                }
                if (subRow >= 0)
                {
                    int sr = subRow * w;
                    for (int x = 0; x < w; x++) { colSum[x] -= hSum[sr + x]; colSq[x] -= hSq[sr + x]; }
                }
            }

            return tex;
        }

        /// Poor-man's retinex: per channel, divide by a large-radius box blur and
        /// rescale by the channel mean, clamping gain to [0.25, 4].
        internal byte[] ComputeIlluminationFlattened(ImageSnapshot snap, int radius)
        {
            int w = snap.Width, h = snap.Height;
            var result = new byte[snap.Pixels.Length];
            Array.Copy(snap.Pixels, result, snap.Pixels.Length); // carries alpha through
            if (w < 4 || h < 4 || radius < 1) return result;

            int channels = snap.Bpp == 1 ? 1 : 3;
            var hSum = new int[w * h];   // horizontal box sums, one channel at a time
            var colSum = new long[w];    // vertical accumulation (window can be large)

            for (int c = 0; c < channels; c++)
            {
                // Global channel mean (the rescale target).
                long globalTotal = 0;
                for (int y = 0; y < h; y++)
                {
                    int rowPix = y * snap.Stride;
                    for (int x = 0; x < w; x++)
                        globalTotal += snap.Pixels[rowPix + x * snap.Bpp + c];
                }
                double globalMean = Math.Max(1.0, (double)globalTotal / ((long)w * h));

                // Pass 1: horizontal sliding sums.
                for (int y = 0; y < h; y++)
                {
                    int rowPix = y * snap.Stride;
                    int row = y * w;
                    int sum = 0;
                    int x1 = Math.Min(w - 1, radius);
                    for (int x = 0; x <= x1; x++) sum += snap.Pixels[rowPix + x * snap.Bpp + c];
                    for (int x = 0; x < w; x++)
                    {
                        hSum[row + x] = sum;
                        int add = x + radius + 1, sub = x - radius;
                        if (add < w) sum += snap.Pixels[rowPix + add * snap.Bpp + c];
                        if (sub >= 0) sum -= snap.Pixels[rowPix + sub * snap.Bpp + c];
                    }
                }

                // Pass 2: vertical sliding sums + normalize each pixel.
                Array.Clear(colSum, 0, w);
                int yInit = Math.Min(h - 1, radius);
                for (int y = 0; y <= yInit; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++) colSum[x] += hSum[row + x];
                }

                for (int y = 0; y < h; y++)
                {
                    int cy0 = Math.Max(0, y - radius), cy1 = Math.Min(h - 1, y + radius);
                    int cntY = cy1 - cy0 + 1;
                    int rowPix = y * snap.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int cx0 = Math.Max(0, x - radius), cx1 = Math.Min(w - 1, x + radius);
                        double blurMean = (double)colSum[x] / ((cx1 - cx0 + 1) * (long)cntY);
                        double gain = globalMean / Math.Max(1.0, blurMean);
                        if (gain < 0.25) gain = 0.25; else if (gain > 4.0) gain = 4.0;
                        int i = rowPix + x * snap.Bpp + c;
                        int v = (int)Math.Round(snap.Pixels[i] * gain);
                        result[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    }
                    int addRow = y + radius + 1, subRow = y - radius;
                    if (addRow < h)
                    {
                        int ar = addRow * w;
                        for (int x = 0; x < w; x++) colSum[x] += hSum[ar + x];
                    }
                    if (subRow >= 0)
                    {
                        int sr = subRow * w;
                        for (int x = 0; x < w; x++) colSum[x] -= hSum[sr + x];
                    }
                }
            }

            return result;
        }

        /// Percentile of the gradient distribution via a coarse sqrt(gradient)
        /// histogram. Computed once per image to calibrate ScoreMask's edge cutoff.
        internal static int GradientPercentileThreshold(int[] gradient, double percentile)
        {
            const int Buckets = 1024;
            const double MaxRoot = 16384.0; // > sqrt of the max possible weighted-Sobel value
            var histo = new int[Buckets];
            for (int i = 0; i < gradient.Length; i++)
            {
                int b = (int)(Math.Sqrt(gradient[i]) * (Buckets - 1) / MaxRoot);
                if (b >= Buckets) b = Buckets - 1;
                histo[b]++;
            }
            long target = (long)(gradient.Length * percentile);
            long cum = 0;
            for (int b = 0; b < Buckets; b++)
            {
                cum += histo[b];
                if (cum >= target)
                {
                    double root = (b + 1) * MaxRoot / (Buckets - 1);
                    return (int)Math.Min(int.MaxValue, root * root);
                }
            }
            return int.MaxValue;
        }

        /// Heuristic quality score in [0,1] for a raw mask: * edge alignment — fraction
        /// of the rim on strong gradient (±2 px tolerance, so masks near but not
        /// exactly on the edge aren't penalized), * border leak — foreground in the
        /// border band is punished, * size sanity — near-full-frame masks are almost
        /// certainly leaks.
        internal double ScoreMask(bool[] mask, int w, int h, int[] gradient, int edgeGradThreshold)
        {
            long rim = 0, rimOnEdge = 0, area = 0, borderFg = 0, borderTotal = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (x < 3 || x >= w - 3 || y < 3 || y >= h - 3)
                    {
                        borderTotal++;
                        if (mask[i]) borderFg++;
                    }
                    if (!mask[i]) continue;
                    area++;

                    bool isRim =
                        (x > 0 && !mask[i - 1]) || (x < w - 1 && !mask[i + 1]) ||
                        (y > 0 && !mask[i - w]) || (y < h - 1 && !mask[i + w]);
                    if (!isRim) continue;
                    rim++;

                    int g = 0;
                    int nx0 = Math.Max(0, x - 2), nx1 = Math.Min(w - 1, x + 2);
                    int ny0 = Math.Max(0, y - 2), ny1 = Math.Min(h - 1, y + 2);
                    for (int yy = ny0; yy <= ny1; yy++)
                    {
                        int rr = yy * w;
                        for (int xx = nx0; xx <= nx1; xx++)
                            if (gradient[rr + xx] > g) g = gradient[rr + xx];
                    }
                    if (g >= edgeGradThreshold) rimOnEdge++;
                }
            }
            if (area == 0 || rim == 0) return 0;

            double edgeAlignment = (double)rimOnEdge / rim;
            double borderLeak = borderTotal > 0 ? (double)borderFg / borderTotal : 0;
            double areaFraction = (double)area / ((long)w * h);
            double sizeFactor = areaFraction > 0.9 ? 0.1 : 1.0;

            return edgeAlignment * (1.0 - Math.Min(1.0, borderLeak * 3.0)) * sizeFactor;
        }

        // ---- BACKGROUND PALETTE ESTIMATION ----
        private const int BgPatchesPerEdge = 5;              // corners shared between edges → 16 patches total
        private const int BgMaxPaletteSize = 6;
        private const double BgPaletteMergeThreshold = 30.0; // perceptual units, same family as the flood threshold

        // Estimates the background as a PALETTE of up to BgMaxPaletteSize colors
        // clustered from 16 median patches around the border, so multi-region
        // backgrounds (mat + ruler + label + shadow) get one entry per region.
        internal ((double r, double g, double b)[] palette, bool[] hardSeedCorners, double threshold)
            EstimateBackgroundPalette(byte[] pixels, int w, int h, int stride, int bpp)
        {
            int patch = Math.Max(4, Math.Min(20, Math.Min(w, h) / 10));
            int inset = Math.Min(5, Math.Min(w, h) / 20);
            int p = patch - 1;
            int lo = inset;
            int hiX = Math.Max(lo, w - patch - inset);
            int hiY = Math.Max(lo, h - patch - inset);

            (double r, double g, double b) SamplePatchMedian(int x0, int y0)
            {
                int x1 = Math.Min(w - 1, x0 + p), y1 = Math.Min(h - 1, y0 + p);
                int cap = Math.Max(1, (x1 - x0 + 1) * (y1 - y0 + 1));
                var rs = new List<double>(cap);
                var gs = new List<double>(cap);
                var bs = new List<double>(cap);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = y * stride + x * bpp;
                        byte pr = bpp == 1 ? pixels[i] : pixels[i + 2];
                        byte pg = bpp == 1 ? pixels[i] : pixels[i + 1];
                        byte pb = bpp == 1 ? pixels[i] : pixels[i];
                        rs.Add(pr); gs.Add(pg); bs.Add(pb);
                    }
                double Med(List<double> v)
                {
                    if (v.Count == 0) return 0;
                    v.Sort();
                    return v[v.Count / 2];
                }
                return (Med(rs), Med(gs), Med(bs));
            }

            static double PDist((double r, double g, double b) a, (double r, double g, double b) b2)
                => PerceptualDistance(a.r, a.g, a.b, b2.r, b2.g, b2.b);

            // 16 patch anchors: BgPatchesPerEdge along top and bottom (their
            // end patches ARE the corners), interior points only on left/right.
            var anchors = new List<(int x, int y)>();
            int idxTL = -1, idxTR = -1, idxBL = -1, idxBR = -1;
            for (int k = 0; k < BgPatchesPerEdge; k++)
            {
                double t = k / (double)(BgPatchesPerEdge - 1);
                int ax = lo + (int)Math.Round(t * (hiX - lo));
                if (k == 0) idxTL = anchors.Count;
                else if (k == BgPatchesPerEdge - 1) idxTR = anchors.Count;
                anchors.Add((ax, lo));
                if (k == 0) idxBL = anchors.Count;
                else if (k == BgPatchesPerEdge - 1) idxBR = anchors.Count;
                anchors.Add((ax, hiY));
            }
            for (int k = 1; k < BgPatchesPerEdge - 1; k++)
            {
                double t = k / (double)(BgPatchesPerEdge - 1);
                int ay = lo + (int)Math.Round(t * (hiY - lo));
                anchors.Add((lo, ay));
                anchors.Add((hiX, ay));
            }

            int m = anchors.Count;
            var med = new (double r, double g, double b)[m];
            for (int i = 0; i < m; i++)
                med[i] = SamplePatchMedian(anchors[i].x, anchors[i].y);

            // Greedy clustering into palette entries, then merge-down to the cap.
            var er = new List<double>(); var eg = new List<double>();
            var eb = new List<double>(); var ec = new List<int>();

            void MergeInto(int k, double r, double g, double b, int count)
            {
                int total = ec[k] + count;
                er[k] = (er[k] * ec[k] + r * count) / total;
                eg[k] = (eg[k] * ec[k] + g * count) / total;
                eb[k] = (eb[k] * ec[k] + b * count) / total;
                ec[k] = total;
            }

            for (int i = 0; i < m; i++)
            {
                int nearest = -1; double nd = double.MaxValue;
                for (int k = 0; k < er.Count; k++)
                {
                    double d = PDist(med[i], (er[k], eg[k], eb[k]));
                    if (d < nd) { nd = d; nearest = k; }
                }
                if (nearest >= 0 && nd <= BgPaletteMergeThreshold)
                    MergeInto(nearest, med[i].r, med[i].g, med[i].b, 1);
                else
                {
                    er.Add(med[i].r); eg.Add(med[i].g); eb.Add(med[i].b); ec.Add(1);
                }
            }

            while (er.Count > BgMaxPaletteSize)
            {
                int bi = 0, bj = 1; double bd = double.MaxValue;
                for (int i2 = 0; i2 < er.Count; i2++)
                    for (int j2 = i2 + 1; j2 < er.Count; j2++)
                    {
                        double d = PDist((er[i2], eg[i2], eb[i2]), (er[j2], eg[j2], eb[j2]));
                        if (d < bd) { bd = d; bi = i2; bj = j2; }
                    }
                MergeInto(bi, er[bj], eg[bj], eb[bj], ec[bj]);
                er.RemoveAt(bj); eg.RemoveAt(bj); eb.RemoveAt(bj); ec.RemoveAt(bj);
            }

            // Drop singleton entries (likely a subject touching the border) —
            // but only when a multi-patch entry remains to stand on.
            bool anyMulti = false;
            for (int k = 0; k < ec.Count; k++) if (ec[k] >= 2) { anyMulti = true; break; }
            if (m >= 8 && anyMulti)
                for (int k = ec.Count - 1; k >= 0; k--)
                    if (ec[k] < 2) { er.RemoveAt(k); eg.RemoveAt(k); eb.RemoveAt(k); ec.RemoveAt(k); }

            var palette = new (double r, double g, double b)[er.Count];
            for (int k = 0; k < er.Count; k++) palette[k] = (er[k], eg[k], eb[k]);

            double MinDistToPalette((double r, double g, double b) c2)
            {
                double best = double.MaxValue;
                for (int k = 0; k < palette.Length; k++)
                {
                    double d = PDist(c2, palette[k]);
                    if (d < best) best = d;
                }
                return best;
            }

            // Adaptive flood threshold from robust patch residuals: sort the
            // per-patch distances to the palette and ignore the top two, which
            // may be subject-contaminated patches whose colors were dropped.
            var residual = new double[m];
            for (int i = 0; i < m; i++) residual[i] = MinDistToPalette(med[i]);
            Array.Sort(residual);
            double robust = residual[Math.Max(0, m - 3)];
            double threshold = Math.Max(35, Math.Min(60, 35 + robust * 1.5));

            var corners = new bool[4];
            corners[0] = idxTL >= 0 && MinDistToPalette(med[idxTL]) <= BgPaletteMergeThreshold * 1.5;
            corners[1] = idxTR >= 0 && MinDistToPalette(med[idxTR]) <= BgPaletteMergeThreshold * 1.5;
            corners[2] = idxBL >= 0 && MinDistToPalette(med[idxBL]) <= BgPaletteMergeThreshold * 1.5;
            corners[3] = idxBR >= 0 && MinDistToPalette(med[idxBR]) <= BgPaletteMergeThreshold * 1.5;

            return (palette, corners, threshold);
        }

        // ---- BACKGROUND MASK (border-palette model) ----
        // Background modeled as a palette of ~6 colors from border median patches
        // (see EstimateBackgroundPalette); each border-flooded pixel matches its
        // NEAREST entry, handling multi-region backgrounds. Corner order: TL, TR, BL, BR.

        private static (byte[] r, byte[] g, byte[] b) ClampPalette((double r, double g, double b)[] pal)
        {
            var pr = new byte[pal.Length];
            var pg = new byte[pal.Length];
            var pb = new byte[pal.Length];
            for (int k = 0; k < pal.Length; k++)
            {
                pr[k] = (byte)Math.Max(0, Math.Min(255, pal[k].r));
                pg[k] = (byte)Math.Max(0, Math.Min(255, pal[k].g));
                pb[k] = (byte)Math.Max(0, Math.Min(255, pal[k].b));
            }
            return (pr, pg, pb);
        }

        private double NearestPaletteDistance(byte r, byte g, byte b,
            byte[] palR, byte[] palG, byte[] palB)
        {
            double best = double.MaxValue;
            for (int k = 0; k < palR.Length; k++)
            {
                double d = PerceptualDistance(r, g, b, palR[k], palG[k], palB[k]);
                if (d < best) best = d;
            }
            return best;
        }

        /// Border-seeded flood over pixels within 'threshold' of the nearest palette
        /// entry. hardSeedCorners (TL, TR, BL, BR): a corner is force-seeded as
        /// background only when its median survived into the palette — insurance
        /// against pixel-vs-median drift without stamping a subject-occupied corner.
        internal bool[] BuildBackgroundMask(
            byte[] pixels, int w, int h, int stride, int bpp,
            (double r, double g, double b)[] bgPalette, bool[] hardSeedCorners,
            double threshold)
        {
            bool[] background = new bool[w * h];
            if (bgPalette == null || bgPalette.Length == 0) return background;
            EnsureQueue(w * h);
            _qHead = 0; _qTail = 0;

            var (palR, palG, palB) = ClampPalette(bgPalette);

            void TryAdd(int x, int y)
            {
                int i = y * w + x;
                if (background[i]) return;
                int pi = y * stride + x * bpp;
                byte pr = bpp == 1 ? pixels[pi] : pixels[pi + 2];
                byte pg = bpp == 1 ? pixels[pi] : pixels[pi + 1];
                byte pb = bpp == 1 ? pixels[pi] : pixels[pi];
                if (NearestPaletteDistance(pr, pg, pb, palR, palG, palB) > threshold) return;
                background[i] = true;
                _queueBuffer[_qTail++] = i;
            }

            // Seed every border pixel that matches the palette.
            for (int x = 0; x < w; x++) { TryAdd(x, 0); TryAdd(x, h - 1); }
            for (int y = 1; y < h - 1; y++) { TryAdd(0, y); TryAdd(w - 1, y); }

            // Hard-seed only the TRUSTED corner patches.
            int patch = Math.Max(4, Math.Min(20, Math.Min(w, h) / 10));
            for (int c = 0; c < 4; c++)
            {
                if (hardSeedCorners == null || c >= hardSeedCorners.Length || !hardSeedCorners[c]) continue;
                int cx0 = (c == 1 || c == 3) ? Math.Max(0, w - patch) : 0;
                int cy0 = (c == 2 || c == 3) ? Math.Max(0, h - patch) : 0;
                for (int py = 0; py < patch && cy0 + py < h; py++)
                    for (int px = 0; px < patch && cx0 + px < w; px++)
                    {
                        int i = (cy0 + py) * w + (cx0 + px);
                        if (background[i]) continue;
                        background[i] = true;
                        _queueBuffer[_qTail++] = i;
                    }
            }

            // Flood inward from all seeded pixels.
            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];
                int cx = ci % w, cy = ci / w;
                if (cx > 0) TryAdd(cx - 1, cy);
                if (cx < w - 1) TryAdd(cx + 1, cy);
                if (cy > 0) TryAdd(cx, cy - 1);
                if (cy < h - 1) TryAdd(cx, cy + 1);
            }

            return background;
        }

        internal bool[] BuildBackgroundMaskProgressive(
            byte[] pixels, int w, int h, int stride, int bpp,
            (double r, double g, double b)[] bgPalette, bool[] hardSeedCorners,
            double tightThreshold, double relaxedThreshold)
        {
            // First pass: tight threshold — only confident background.
            bool[] tight = BuildBackgroundMask(pixels, w, h, stride, bpp,
                bgPalette, hardSeedCorners, tightThreshold);

            // Second pass: relaxed threshold seeded only from confirmed
            // background pixels — extends the mask into gradient regions
            // without starting fresh from the borders.
            bool[] relaxed = (bool[])tight.Clone();
            if (bgPalette == null || bgPalette.Length == 0) return relaxed;
            EnsureQueue(w * h);
            _qHead = 0; _qTail = 0;
            for (int i = 0; i < tight.Length; i++)
                if (tight[i]) _queueBuffer[_qTail++] = i;

            var (palR, palG, palB) = ClampPalette(bgPalette);

            void TryRelax(int nx, int ny)
            {
                int ni = ny * w + nx;
                if (relaxed[ni]) return;
                int pi = ny * stride + nx * bpp;
                byte pr = bpp == 1 ? pixels[pi] : pixels[pi + 2];
                byte pg = bpp == 1 ? pixels[pi] : pixels[pi + 1];
                byte pb = bpp == 1 ? pixels[pi] : pixels[pi];
                if (NearestPaletteDistance(pr, pg, pb, palR, palG, palB) > relaxedThreshold) return;
                relaxed[ni] = true;
                _queueBuffer[_qTail++] = ni;
            }

            while (_qHead < _qTail)
            {
                int ci = _queueBuffer[_qHead++];
                int cx = ci % w, cy = ci / w;
                if (cx > 0) TryRelax(cx - 1, cy);
                if (cx < w - 1) TryRelax(cx + 1, cy);
                if (cy > 0) TryRelax(cx, cy - 1);
                if (cy < h - 1) TryRelax(cx, cy + 1);
            }

            return relaxed;
        }
        // ---- MASK UTILITIES ----
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

        /// Finds the pixel within searchRadius of (seedX, seedY) farthest from any
        /// background/border pixel, using the precomputed distance transform (see
        /// ComputeDistanceToBackground) — a small-window scan.
        internal (int bestX, int bestY) FindDistanceTransformPeak(
            int seedX, int seedY, int w, int h,
            int[] distToBackground, bool[] bgMask, int searchRadius)
        {
            int bestIdx = seedY * w + seedX;
            int bestDist = distToBackground[bestIdx];

            int x0 = Math.Max(0, seedX - searchRadius);
            int x1 = Math.Min(w - 1, seedX + searchRadius);
            int y0 = Math.Max(0, seedY - searchRadius);
            int y1 = Math.Min(h - 1, seedY + searchRadius);

            int r2 = searchRadius * searchRadius;
            for (int y = y0; y <= y1; y++)
            {
                int dy = y - seedY;
                for (int x = x0; x <= x1; x++)
                {
                    int dx = x - seedX;
                    if (dx * dx + dy * dy > r2) continue;
                    int i = y * w + x;
                    if (bgMask != null && bgMask[i]) continue; // never seed on background
                    if (distToBackground[i] > bestDist) { bestDist = distToBackground[i]; bestIdx = i; }
                }
            }

            return (bestIdx % w, bestIdx / w);
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

        // ---- FLOOD FILL ----
        internal bool[] FloodFill(int startX, int startY, byte sr, byte sg, byte sb,
            ImageSnapshot snap, double tolerance, double gradientLeniency, double edgeThreshold,
            CancellationToken token = default)
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
                // Periodic cancellation check so a superseded click actually stops
                // instead of running to completion in the background.
                if ((_qHead & 0x3FFF) == 0) token.ThrowIfCancellationRequested();

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
                    bool gradientMatch = neighborDist <= gradientLeniency && seedDist <= Math.Max(tolerance * 5.0, 150.0);
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

        // ---- ADAPTIVE FLOOD FILL (chroma + texture aware) ----
        /// <summary>FloodFill variant for textured objects and variable lighting.</summary>
        internal bool[] FloodFillAdaptive(int startX, int startY, byte sr, byte sg, byte sb,
            float seedTexture, ImageSnapshot snap, float[] textureMap,
            double tolerance, double gradientLeniency, double edgeThreshold,
            CancellationToken token = default)
        {
            int w = snap.Width, h = snap.Height, total = w * h;
            bool[] inside = new bool[total];
            EnsureQueue(total);
            _qHead = 0; _qTail = 0;
            int seed = startY * w + startX;
            _queueBuffer[_qTail++] = seed;
            inside[seed] = true;

            double texDeadzone = 0.5 * Math.Max(seedTexture, 4f);
            double effTolerance = tolerance + Math.Min(TextureToleranceCap, TextureToleranceGain * seedTexture);
            double effEdge = edgeThreshold + Math.Min(TextureEdgeCap, TextureEdgeGain * seedTexture);
            double effLeniency = gradientLeniency + Math.Min(TextureLeniencyCap, TextureLeniencyGain * seedTexture);
            double seedCap = Math.Max(effTolerance * 5.0, 150.0);

            while (_qHead < _qTail)
            {
                if ((_qHead & 0x3FFF) == 0) token.ThrowIfCancellationRequested();

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

                    double texPenalty = TexturePenaltyWeight *
                        Math.Max(0.0, Math.Abs(textureMap[ni] - seedTexture) - texDeadzone);
                    double seedDist = ChromaAwareSeedDistance(pr, pg, pb, sr, sg, sb) + texPenalty;
                    double neighborDist = PerceptualDistance(pr, pg, pb, cr, cg, cb);

                    bool strongEdge = neighborDist > effEdge;
                    bool seedMatch = seedDist <= effTolerance;
                    bool gradientMatch = neighborDist <= effLeniency && seedDist <= seedCap;
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

        // ---- FILL HOLES ----
        internal bool[] FillHoles(bool[] mask, int w, int h)
        {
            int total = w * h;
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

        // ---- COMPONENT OPERATIONS ----

        internal List<int> CollectComponent(bool[] mask, int w, int h, int seed, bool[] visited = null)
        {
            int total = w * h;
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

        internal bool[] KeepComponentContainingSeed(bool[] inside, int w, int h, int seed)
        {
            int total = w * h;
            bool[] result = new bool[total];
            foreach (int i in CollectComponent(inside, w, h, seed))
                result[i] = true;
            return result;
        }

        internal bool[] KeepLargestComponent(bool[] inside, int w, int h)
        {
            int total = w * h;
            bool[] visited = new bool[total], best = new bool[total];
            List<int> bestComponent = null;
            for (int start = 0; start < total; start++)
            {
                if (!inside[start] || visited[start]) continue;
                var component = CollectComponent(inside, w, h, start, visited);
                if (bestComponent == null || component.Count > bestComponent.Count)
                    bestComponent = component;
            }
            if (bestComponent == null) return best;
            foreach (int i in bestComponent) best[i] = true;
            return best;
        }

        internal bool[] ExpandConnectedComponentToArea(bool[] mask, int w, int h, int seed, int minArea)
        {
            int total = w * h;
            bool[] current = (bool[])mask.Clone();
            if (seed < 0 || seed >= total) return current;
            if (!current[seed]) current = KeepComponentContainingSeed(current, w, h, seed);
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

        // Full per-shape morphology/topology cleanup on one mask, WITHOUT component
        // selection.
        internal bool[] CleanComponentMorphology(bool[] mask, int w, int h,
            int openRadius = 2, int closeRadius = 1, int minCorridorWidth = 3)
        {
            bool[] work = FillHoles(mask, w, h);
            work = MorphOpen(work, w, h, openRadius);
            work = MorphClose(work, w, h, closeRadius);
            EnforceMinimumCorridorWidth(work, w, h, minCorridorWidth);
            SmoothBorderTopology(work, w, h);
            PruneDeadEnds(work, w, h);
            return work;
        }

        // Cleans each connected component in isolation, inside a component-sized crop;
        // components below minComponentArea are dropped.
        internal bool[] CleanEachComponent(bool[] mask, int w, int h,
            int minComponentArea, CancellationToken token = default)
        {
            int total = w * h;
            bool[] result = new bool[total];
            bool[] visited = new bool[total];

            for (int start = 0; start < total; start++)
            {
                if (!mask[start] || visited[start]) continue;
                token.ThrowIfCancellationRequested();

                var members = CollectComponent(mask, w, h, start, visited);

                // Component bounding box
                int minX = w, minY = h, maxX = 0, maxY = 0;
                foreach (int i in members)
                {
                    int x = i % w, y = i / w;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }

                const int closeRadius = 1;
                int margin = closeRadius + 2; // MorphClose can grow into the margin band
                int cx0 = Math.Max(0, minX - margin), cy0 = Math.Max(0, minY - margin);
                int cx1 = Math.Min(w - 1, maxX + margin), cy1 = Math.Min(h - 1, maxY + margin);
                int cw = cx1 - cx0 + 1, ch = cy1 - cy0 + 1;

                bool[] isolated = new bool[cw * ch];
                foreach (int i in members)
                    isolated[(i / w - cy0) * cw + (i % w - cx0)] = true;

                // Radii proportional to component scale (as in
                // OutlineMode.CleanMaskFullSpace), floored at radius 2 so boundary
                // noise doesn't slip through; only tiny components keep radius 1.
                double objScale = Math.Sqrt(members.Count);
                int openRadius = members.Count < 2500
                    ? 1
                    : (int)Math.Min(3, Math.Max(2, objScale / 64.0));
                int corridorWidth = Math.Min(3, openRadius + 1);

                bool[] cleaned = CleanComponentMorphology(isolated, cw, ch,
                    openRadius, closeRadius, corridorWidth);

                if (!HasMinimumPixels(cleaned, minComponentArea)) continue;

                // Union the survivor back into the combined result
                for (int y = 0; y < ch; y++)
                {
                    int row = y * cw;
                    for (int x = 0; x < cw; x++)
                        if (cleaned[row + x]) result[(cy0 + y) * w + (cx0 + x)] = true;
                }
            }

            return result;
        }
        // ---- TOPOLOGY / SHAPE PROCESSING ----
        internal void EnforceMinimumCorridorWidth(bool[] mask, int w, int h, int minWidth = 3)
        {
            int total = w * h;
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

        internal void SmoothBorderTopology(bool[] mask, int w, int h, int passes = 3)
        {
            int total = w * h;
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

        internal void PruneDeadEnds(bool[] mask, int w, int h)
        {
            int total = w * h;
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

        // ---- BOUNDARY TRACING ----
        // Traces a filled region as a SIMPLE closed polygon by walking the cracks
        // between filled/empty cells, not pixel centers: crack following can't self-
        // intersect (it bounds a union of unit cells) whereas Moore tracing can.
        internal List<System.Windows.Point> TraceBoundary(bool[] inside, int w, int h)
        {
            bool Filled(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && inside[y * w + x];

            // Topmost-leftmost filled cell; its north edge is guaranteed on the outer boundary.
            int sx = -1, sy = -1;
            for (int i = 0; i < inside.Length; i++)
                if (inside[i]) { sx = i % w; sy = i / w; break; }
            if (sx < 0) return new List<System.Windows.Point>();

            int cols = w + 1;
            int CornerId(int x, int y) => y * cols + x;

            // Directed boundary edges, filled cell kept on a consistent side.
            // (Verified to form a single closed loop for isolated and adjacent cells.)
            var edges = new Dictionary<int, List<(int ex, int ey)>>();
            int totalEdges = 0;
            void AddEdge(int x0, int y0, int x1, int y1)
            {
                int k = CornerId(x0, y0);
                if (!edges.TryGetValue(k, out var lst)) { lst = new List<(int, int)>(1); edges[k] = lst; }
                lst.Add((x1, y1));
                totalEdges++;
            }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!inside[y * w + x]) continue;
                    if (!Filled(x, y - 1)) AddEdge(x, y, x + 1, y);         // north
                    if (!Filled(x + 1, y)) AddEdge(x + 1, y, x + 1, y + 1); // east
                    if (!Filled(x, y + 1)) AddEdge(x + 1, y + 1, x, y + 1); // south
                    if (!Filled(x - 1, y)) AddEdge(x, y + 1, x, y);         // west
                }

            var boundary = new List<System.Windows.Point>();
            int curX = sx, curY = sy;
            int prevX = sx - 1, prevY = sy;          // incoming heading = +x
            int guard = totalEdges + 4;

            while (guard-- > 0)
            {
                boundary.Add(new System.Windows.Point(curX, curY));
                int curId = CornerId(curX, curY);
                if (!edges.TryGetValue(curId, out var outs) || outs.Count == 0) break;

                (int ex, int ey) next;
                if (outs.Count == 1)
                {
                    next = outs[0];
                }
                else
                {
                    // Pinch point (diagonal touch): take the most-clockwise turn so the
                    // polygon touches at the corner without crossing.
                    int inDx = curX - prevX, inDy = curY - prevY;
                    next = outs[0];
                    double best = double.NegativeInfinity;
                    foreach (var c in outs)
                    {
                        int oDx = c.ex - curX, oDy = c.ey - curY;
                        double cross = inDx * oDy - inDy * oDx;
                        double dot = inDx * oDx + inDy * oDy;
                        double clockwise = -Math.Atan2(cross, dot);
                        if (clockwise > best) { best = clockwise; next = c; }
                    }
                }

                outs.Remove(next);                   // consume so we don't reuse it
                prevX = curX; prevY = curY;
                curX = next.ex; curY = next.ey;
                if (curX == sx && curY == sy) break; // returned to start
            }

            return boundary;
        }

        // ---- BOUNDARY SNAP ----
        private const int BoundarySnapRange = 3;                  // ± pixels searched along the local normal
        private const double BoundarySnapDistancePenalty = 0.12;  // per-px² score decay — prefer NEAR maxima
        private const double BoundarySnapMinGain = 1.15;          // target must beat staying put by 15%
        private const int BoundarySnapMedianWindow = 5;           // consensus filter on offsets (odd)

        /// Post-pass on the dense boundary: moves vertices onto the local gradient
        /// maximum along their normals, CONSERVATIVELY, to fix the mask-trace-stops-
        /// inside-the-soft-edge offset without chasing texture/debris.
        internal List<System.Windows.Point> SnapBoundaryToGradient(
            List<System.Windows.Point> boundary, int offsetX, int offsetY,
            int[] gradient, int imageWidth, int imageHeight, int edgeGradThreshold)
        {
            int n = boundary.Count;
            if (gradient == null || n < 8) return boundary;

            int SampleGrad(double px, double py)
            {
                int ix = (int)Math.Round(px) + offsetX;
                int iy = (int)Math.Round(py) + offsetY;
                if (ix < 0) ix = 0; else if (ix >= imageWidth) ix = imageWidth - 1;
                if (iy < 0) iy = 0; else if (iy >= imageHeight) iy = imageHeight - 1;
                return gradient[iy * imageWidth + ix];
            }

            // Pass 1: per-vertex normal + candidate offset (0 = stay put).
            var nxArr = new double[n];
            var nyArr = new double[n];
            var offset = new double[n];
            for (int i = 0; i < n; i++)
            {
                var pPrev = boundary[(i - 2 + n) % n];
                var pNext = boundary[(i + 2) % n];
                double tx = pNext.X - pPrev.X, ty = pNext.Y - pPrev.Y;
                double len = Math.Sqrt(tx * tx + ty * ty);
                if (len < 1e-6) continue; // offset stays 0
                double nx = -ty / len, ny = tx / len;
                nxArr[i] = nx; nyArr[i] = ny;

                var p = boundary[i];
                int g0 = SampleGrad(p.X, p.Y);
                double bestScore = g0;      // staying put carries weight 1
                int bestT = 0, bestG = g0;
                for (int t = -BoundarySnapRange; t <= BoundarySnapRange; t++)
                {
                    if (t == 0) continue;
                    int g = SampleGrad(p.X + nx * t, p.Y + ny * t);
                    double s = g / (1.0 + BoundarySnapDistancePenalty * t * t);
                    if (s > bestScore) { bestScore = s; bestT = t; bestG = g; }
                }

                if (bestT != 0 && bestG >= edgeGradThreshold && bestG >= g0 * BoundarySnapMinGain)
                    offset[i] = bestT;
            }

            // Pass 2: cyclic median filter on the offsets — isolated
            // disagreements die; coherent runs survive.
            var med = new double[n];
            int half = BoundarySnapMedianWindow / 2;
            var window = new double[BoundarySnapMedianWindow];
            for (int i = 0; i < n; i++)
            {
                for (int k = -half; k <= half; k++)
                    window[k + half] = offset[(i + k + n) % n];
                Array.Sort(window);
                med[i] = window[half];
            }

            // Pass 3: light [1,2,1]/4 smoothing of the offsets, then displace.
            var snapped = new List<System.Windows.Point>(n);
            for (int i = 0; i < n; i++)
            {
                double o = (med[(i - 1 + n) % n] + 2 * med[i] + med[(i + 1) % n]) / 4.0;
                var p = boundary[i];
                snapped.Add(new System.Windows.Point(p.X + nxArr[i] * o, p.Y + nyArr[i] * o));
            }

            // Pass 4: one position pass to settle residual jitter.
            var outPts = new List<System.Windows.Point>(n);
            for (int i = 0; i < n; i++)
            {
                var a = snapped[(i - 1 + n) % n];
                var b = snapped[i];
                var c = snapped[(i + 1) % n];
                outPts.Add(new System.Windows.Point(
                    (a.X + 2 * b.X + c.X) / 4.0,
                    (a.Y + 2 * b.Y + c.Y) / 4.0));
            }
            return outPts;
        }

        // ---- TRACING PREPARATION ----
        internal bool[] PrepareMaskForTracing(bool[] pruned, bool[] raw,
            int w, int h, int seedX, int seedY, int minArea)
        {
            bool[] candidate = (bool[])pruned.Clone();
            int area = CountPixels(candidate);
            if (area >= minArea) return candidate;
            int seed = seedY * w + seedX;
            if (!candidate[seed]) { candidate = KeepComponentContainingSeed(raw, w, h, seed); area = CountPixels(candidate); }
            if (area >= minArea) return candidate;
            candidate = ExpandConnectedComponentToArea(candidate, w, h, seed, minArea);
            if (!HasMinimumPixels(candidate, minArea)) return candidate;
            bool[] fallback = KeepComponentContainingSeed(raw, w, h, seed);
            if (!HasMinimumPixels(fallback, minArea)) return fallback;
            return fallback;
        }

        // ---- WATERSHED ----
        /// Marker-based watershed on the (cached) Sobel gradient.
        ///   cachedGradient   — per-image gradient; used directly at blurLevel 0,
        ///                      else recomputed from the blurred copy.
        ///   distToBackground — per-image cache; pass null to recompute here.
        internal bool[] WatershedSegment(int seedX, int seedY, ImageSnapshot snap,
            int[] cachedGradient, int[] distToBackground, CancellationToken token,
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
                    token.ThrowIfCancellationRequested();
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

            // ── 2. Gradient: reuse the per-image cache when unblurred ────────────
            int[] gradient = (blurLevel == 0 && cachedGradient != null)
                ? cachedGradient
                : ComputeGradient(workSnap);

            token.ThrowIfCancellationRequested();

            // ── 3. Labels and heap ───────────────────────────────────────────────
            int[] labels = new int[total];
            for (int i = 0; i < total; i++) labels[i] = -1;

            HeapReset(total);
            void Push(int i, int label) { labels[i] = label; HeapPush(gradient[i], i); }

            // Border → background
            for (int x = 0; x < w; x++)
            {
                if (labels[x] == -1) Push(x, 0);
                int bi = (h - 1) * w + x;
                if (labels[bi] == -1) Push(bi, 0);
            }
            for (int y = 1; y < h - 1; y++)
            {
                int li = y * w, ri = li + w - 1;
                if (labels[li] == -1) Push(li, 0);
                if (labels[ri] == -1) Push(ri, 0);
            }

            // BgMask → background
            if (snap.BgMask != null)
                for (int i = 0; i < total; i++)
                    if (snap.BgMask[i] && labels[i] == -1) Push(i, 0);

            // Seed foreground from the distance-transform peak near the click (the
            // pixel farthest from background/border within searchRadius), a more
            // stable interior start than a flat circle.
            if (distToBackground == null)
                distToBackground = ComputeDistanceToBackground(w, h, snap.BgMask);
            var (peakX, peakY) = FindDistanceTransformPeak(
                seedX, seedY, w, h, distToBackground, snap.BgMask, searchRadius: 15);

            for (int dy = -seedRadius; dy <= seedRadius; dy++)
                for (int dx = -seedRadius; dx <= seedRadius; dx++)
                {
                    if (dx * dx + dy * dy > seedRadius * seedRadius) continue;
                    int nx = peakX + dx, ny = peakY + dy;
                    if ((uint)nx >= w || (uint)ny >= h) continue;
                    int ni = ny * w + nx;
                    if (snap.BgMask != null && snap.BgMask[ni]) continue;
                    if (labels[ni] == -1) Push(ni, 1);
                }

            // ── 4. Flood ─────────────────────────────────────────────────────────
            int[] ndx4 = { 1, -1, 0, 0 };
            int[] ndy4 = { 0, 0, 1, -1 };
            int steps = 0;

            while (_heapCount > 0)
            {
                if ((++steps & 0x3FFF) == 0) token.ThrowIfCancellationRequested();

                int ci = HeapPop();
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
                        Push(ni, 0);
                        continue;
                    }

                    Push(ni, myLabel);
                }
            }

            // ── 5. Extract mask, enforcing BgMask as hard stop ───────────────────
            bool[] mask = new bool[total];
            for (int i = 0; i < total; i++)
                mask[i] = labels[i] == 1;

            // Hard-remove any foreground pixels that overlap confirmed background.
            if (snap.BgMask != null)
                for (int i = 0; i < total; i++)
                    if (snap.BgMask[i]) mask[i] = false;

            // Check the peak first, fall back to the original click point
            int peakIndex = peakY * w + peakX;
            int checkIndex = (peakIndex >= 0 && peakIndex < total && mask[peakIndex])
                ? peakIndex : seedY * w + seedX;
            if (checkIndex >= 0 && checkIndex < total && mask[checkIndex])
                return KeepComponentContainingSeed(mask, w, h, checkIndex);

            return null;
        }

        // ---- MORPHOLOGY ----
        internal bool[] MorphErode(bool[] mask, int w, int h, int radius)
        {
            int total = w * h;
            EnsureBuffers(total);

            // BFS distance transform from background (false) pixels inward
            var dist = _distBuffer;
            var queue = _queueBuffer;
            for (int i = 0; i < total; i++) dist[i] = mask[i] ? int.MaxValue / 4 : 0;

            int head = 0, tail = 0;
            for (int i = 0; i < total; i++)
                if (dist[i] == 0) queue[tail++] = i;

            while (head < tail)
            {
                int i = queue[head++];
                int d = dist[i] + 1;
                int x = i % w, y = i / w;
                if (x > 0 && dist[i - 1] > d) { dist[i - 1] = d; queue[tail++] = i - 1; }
                if (x < w - 1 && dist[i + 1] > d) { dist[i + 1] = d; queue[tail++] = i + 1; }
                if (y > 0 && dist[i - w] > d) { dist[i - w] = d; queue[tail++] = i - w; }
                if (y < h - 1 && dist[i + w] > d) { dist[i + w] = d; queue[tail++] = i + w; }
            }

            bool[] result = new bool[total];
            for (int i = 0; i < total; i++)
                result[i] = dist[i] > radius;
            return result;
        }

        internal bool[] MorphDilate(bool[] mask, int w, int h, int radius)
        {
            int total = w * h;
            EnsureBuffers(total);

            // BFS distance transform from foreground (true) pixels outward
            var dist = _distBuffer;
            var queue = _queueBuffer;
            for (int i = 0; i < total; i++) dist[i] = mask[i] ? 0 : int.MaxValue / 4;

            int head = 0, tail = 0;
            for (int i = 0; i < total; i++)
                if (dist[i] == 0) queue[tail++] = i;

            while (head < tail)
            {
                int i = queue[head++];
                int d = dist[i] + 1;
                int x = i % w, y = i / w;
                if (x > 0 && dist[i - 1] > d) { dist[i - 1] = d; queue[tail++] = i - 1; }
                if (x < w - 1 && dist[i + 1] > d) { dist[i + 1] = d; queue[tail++] = i + 1; }
                if (y > 0 && dist[i - w] > d) { dist[i - w] = d; queue[tail++] = i - w; }
                if (y < h - 1 && dist[i + w] > d) { dist[i + w] = d; queue[tail++] = i + w; }
            }

            bool[] result = new bool[total];
            for (int i = 0; i < total; i++)
                result[i] = dist[i] <= radius;
            return result;
        }

        internal bool[] MorphOpen(bool[] mask, int w, int h, int radius)
        {
            // Erode then dilate — breaks thin spurious connections to background bleed
            bool[] eroded = MorphErode(mask, w, h, radius);
            return MorphDilate(eroded, w, h, radius);
        }

        internal bool[] MorphClose(bool[] mask, int w, int h, int radius)
        {
            // Dilate then erode — seals small gaps and holes in the subject
            bool[] dilated = MorphDilate(mask, w, h, radius);
            return MorphErode(dilated, w, h, radius);
        }

        /// MorphClose restricted to the mask's bounding box plus a radius margin,
        /// avoiding full-image distance transforms per click during multi-click
        /// merging.
        internal bool[] MorphCloseCropped(bool[] mask, int w, int h, int radius)
        {
            var (x0, y0, x1, y1) = GetMaskBounds(mask, w, h, margin: radius + 2);
            if (x1 <= x0 || y1 <= y0) return mask; // empty or degenerate — nothing to close
            var (cropped, cw, ch) = CropMask(mask, w, x0, y0, x1, y1);
            bool[] closed = MorphClose(cropped, cw, ch, radius);
            bool[] result = new bool[w * h];
            for (int y = 0; y < ch; y++)
            {
                int row = y * cw;
                for (int x = 0; x < cw; x++)
                    if (closed[row + x]) result[(y0 + y) * w + (x0 + x)] = true;
            }
            return result;
        }
        // ---- RESOLUTION ----
        /// Box-downsamples 2× (each output averages a 2×2 block; odd trailing row/col
        /// dropped) into a self-contained Bgra32 snapshot.
        internal ImageSnapshot DownsampleHalf(ImageSnapshot snap)
        {
            int w = snap.Width, h = snap.Height;
            int hw = w / 2, hh = h / 2;
            if (hw < 1 || hh < 1) return snap;

            var pixels = new byte[hw * hh * 4];
            bool[] bg = snap.BgMask != null ? new bool[hw * hh] : null;

            for (int y = 0; y < hh; y++)
            {
                for (int x = 0; x < hw; x++)
                {
                    int sx = x * 2, sy = y * 2;
                    int r = 0, g = 0, b = 0;
                    bool allBg = true;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int px = sx + dx, py = sy + dy;
                            int i = py * snap.Stride + px * snap.Bpp;
                            if (snap.Bpp == 1) { int v = snap.Pixels[i]; r += v; g += v; b += v; }
                            else { b += snap.Pixels[i]; g += snap.Pixels[i + 1]; r += snap.Pixels[i + 2]; }
                            if (snap.BgMask != null && !snap.BgMask[py * w + px]) allBg = false;
                        }
                    int o = (y * hw + x) * 4;
                    pixels[o] = (byte)(b / 4);
                    pixels[o + 1] = (byte)(g / 4);
                    pixels[o + 2] = (byte)(r / 4);
                    pixels[o + 3] = 255;
                    if (bg != null) bg[y * hw + x] = allBg;
                }
            }

            return new ImageSnapshot(pixels, bg, hw, hh, hw * 4, 4);
        }

        /// Nearest-neighbor upsample of a half-resolution mask to full size.
        internal bool[] UpsampleMask2x(bool[] halfMask, int hw, int hh, int w, int h)
        {
            var full = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int hy = Math.Min(hh - 1, y / 2);
                int hrow = hy * hw, row = y * w;
                for (int x = 0; x < w; x++)
                    full[row + x] = halfMask[hrow + Math.Min(hw - 1, x / 2)];
            }
            return full;
        }

        // ---- GRABCUT-STYLE GMM REFINEMENT ----
        // Tuning for GmmRefine.
        private const int GmmMaxComponents = 5;      // per GrabCut convention
        private const int GmmMaxSamples = 10000;     // per model, stride-subsampled
        private const int GmmKMeansIterations = 6;
        private const double GmmVarianceFloor = 4.0; // per-channel σ ≥ 2 — 8-bit sensor noise
        private const int GmmMinSamples = 200;       // below this a model can't be fit
        private const int GmmErodeRadius = 2;        // keeps boundary mixels out of the fg model
        private const int GmmDilateRadius = 3;       // keeps boundary mixels out of the bg model
        private const int GmmHardSeedRadius = 3;     // click anchor — the model can never collapse to all-background

        // Diagonal-covariance Gaussian mixture over RGB. LogLikelihood drops the
        // shared −1.5·log(2π) constant, which cancels in the fg/bg comparison.
        private sealed class Gmm
        {
            public int K;
            public double[] LogConst;  // per component: log w − 0.5·Σ log σ²
            public double[] Mean;      // K×3
            public double[] Inv2Var;   // K×3 : 1 / (2σ²)

            public double LogLikelihood(int r, int g, int b)
            {
                // Streaming logsumexp over ≤ 5 components — single pass, no allocation.
                double m = double.NegativeInfinity, s = 0.0;
                for (int k = 0; k < K; k++)
                {
                    int j = k * 3;
                    double dr = r - Mean[j], dg = g - Mean[j + 1], db = b - Mean[j + 2];
                    double t = LogConst[k]
                        - dr * dr * Inv2Var[j]
                        - dg * dg * Inv2Var[j + 1]
                        - db * db * Inv2Var[j + 2];
                    if (t > m) { s = s * Math.Exp(m - t) + 1.0; m = t; }
                    else s += Math.Exp(t - m);
                }
                return m + Math.Log(s);
            }
        }

        /// Gathers up to GmmMaxSamples packed (r,g,b) triplets from the selected
        /// roi-local pixels into 'colors', stride-subsampled evenly across the
        /// selection so the sample set spans the whole region. Returns the count.
        private int GatherSamples(bool[] sel, int rw, int rh, int rx0, int ry0,
            ImageSnapshot snap, float[] colors)
        {
            int total = 0;
            for (int i = 0; i < sel.Length; i++) if (sel[i]) total++;
            if (total == 0) return 0;
            int stride = (total + GmmMaxSamples - 1) / GmmMaxSamples;

            int n = 0, seen = 0;
            for (int y = 0; y < rh; y++)
            {
                int rowPix = (ry0 + y) * snap.Stride;
                int rRow = y * rw;
                for (int x = 0; x < rw; x++)
                {
                    if (!sel[rRow + x]) continue;
                    if (seen++ % stride != 0) continue;
                    int i = rowPix + (rx0 + x) * snap.Bpp;
                    int r, g, b;
                    if (snap.Bpp == 1) { r = g = b = snap.Pixels[i]; }
                    else { b = snap.Pixels[i]; g = snap.Pixels[i + 1]; r = snap.Pixels[i + 2]; }
                    int j = n * 3;
                    colors[j] = r; colors[j + 1] = g; colors[j + 2] = b;
                    n++;
                    if (n >= GmmMaxSamples) return n;
                }
            }
            return n;
        }

        /// Fits a diagonal-covariance RGB mixture via farthest-point-seeded k-means.
        private Gmm FitGmm(float[] colors, int n)
        {
            if (n < GmmMinSamples) return null;
            int K = Math.Min(GmmMaxComponents, Math.Max(1, n / 100));

            // Farthest-point seeding (k-means++-style spread, deterministic).
            var centers = new double[K * 3];
            centers[0] = colors[0]; centers[1] = colors[1]; centers[2] = colors[2];
            var minDist = new double[n];
            for (int i = 0; i < n; i++)
            {
                int j = i * 3;
                double dr = colors[j] - centers[0], dg = colors[j + 1] - centers[1], db = colors[j + 2] - centers[2];
                minDist[i] = dr * dr + dg * dg + db * db;
            }
            for (int k = 1; k < K; k++)
            {
                int far = 0; double fd = -1;
                for (int i = 0; i < n; i++) if (minDist[i] > fd) { fd = minDist[i]; far = i; }
                int cj = k * 3, fj = far * 3;
                centers[cj] = colors[fj]; centers[cj + 1] = colors[fj + 1]; centers[cj + 2] = colors[fj + 2];
                for (int i = 0; i < n; i++)
                {
                    int j = i * 3;
                    double dr = colors[j] - centers[cj], dg = colors[j + 1] - centers[cj + 1], db = colors[j + 2] - centers[cj + 2];
                    double d = dr * dr + dg * dg + db * db;
                    if (d < minDist[i]) minDist[i] = d;
                }
            }

            var assign = new int[n];
            var count = new int[K];
            var sum = new double[K * 3];

            for (int pass = 0; pass < GmmKMeansIterations; pass++)
            {
                Array.Clear(count, 0, K);
                Array.Clear(sum, 0, K * 3);
                for (int i = 0; i < n; i++)
                {
                    int j = i * 3, bestK = 0; double bd = double.MaxValue;
                    for (int k = 0; k < K; k++)
                    {
                        int cj = k * 3;
                        double dr = colors[j] - centers[cj], dg = colors[j + 1] - centers[cj + 1], db = colors[j + 2] - centers[cj + 2];
                        double d = dr * dr + dg * dg + db * db;
                        if (d < bd) { bd = d; bestK = k; }
                    }
                    assign[i] = bestK; count[bestK]++;
                    int bj = bestK * 3;
                    sum[bj] += colors[j]; sum[bj + 1] += colors[j + 1]; sum[bj + 2] += colors[j + 2];
                }
                for (int k = 0; k < K; k++)
                {
                    int cj = k * 3;
                    if (count[k] > 0)
                    {
                        centers[cj] = sum[cj] / count[k];
                        centers[cj + 1] = sum[cj + 1] / count[k];
                        centers[cj + 2] = sum[cj + 2] / count[k];
                    }
                    else
                    {
                        // Reseed an empty cluster to the sample farthest from its
                        // own centre — the classic fix.
                        int far = 0; double fd = -1;
                        for (int i = 0; i < n; i++)
                        {
                            int j = i * 3, aj = assign[i] * 3;
                            double dr = colors[j] - centers[aj], dg = colors[j + 1] - centers[aj + 1], db = colors[j + 2] - centers[aj + 2];
                            double d = dr * dr + dg * dg + db * db;
                            if (d > fd) { fd = d; far = i; }
                        }
                        int fj = far * 3;
                        centers[cj] = colors[fj]; centers[cj + 1] = colors[fj + 1]; centers[cj + 2] = colors[fj + 2];
                    }
                }
            }

            // Final assignment + per-component mean/variance.
            var sumSq = new double[K * 3];
            Array.Clear(count, 0, K);
            Array.Clear(sum, 0, K * 3);
            for (int i = 0; i < n; i++)
            {
                int j = i * 3, bestK = 0; double bd = double.MaxValue;
                for (int k = 0; k < K; k++)
                {
                    int cj = k * 3;
                    double dr = colors[j] - centers[cj], dg = colors[j + 1] - centers[cj + 1], db = colors[j + 2] - centers[cj + 2];
                    double d = dr * dr + dg * dg + db * db;
                    if (d < bd) { bd = d; bestK = k; }
                }
                count[bestK]++;
                int bj = bestK * 3;
                sum[bj] += colors[j]; sum[bj + 1] += colors[j + 1]; sum[bj + 2] += colors[j + 2];
                sumSq[bj] += (double)colors[j] * colors[j];
                sumSq[bj + 1] += (double)colors[j + 1] * colors[j + 1];
                sumSq[bj + 2] += (double)colors[j + 2] * colors[j + 2];
            }

            var gmm = new Gmm
            {
                K = K,
                LogConst = new double[K],
                Mean = new double[K * 3],
                Inv2Var = new double[K * 3]
            };
            for (int k = 0; k < K; k++)
            {
                int cj = k * 3;
                double wk = Math.Max(count[k], 0.5) / n;   // empty → vanishing, finite
                double logVarSum = 0;
                for (int c = 0; c < 3; c++)
                {
                    double mean = count[k] > 0 ? sum[cj + c] / count[k] : centers[cj + c];
                    double variance = count[k] > 0 ? sumSq[cj + c] / count[k] - mean * mean : GmmVarianceFloor;
                    if (variance < GmmVarianceFloor) variance = GmmVarianceFloor;
                    gmm.Mean[cj + c] = mean;
                    gmm.Inv2Var[cj + c] = 1.0 / (2.0 * variance);
                    logVarSum += Math.Log(variance);
                }
                gmm.LogConst[k] = Math.Log(wk) - 0.5 * logVarSum;
            }
            return gmm;
        }

        /// GrabCut-style refinement of a coarse mask: iterate-classify-cleanup WITHOUT
        /// the graph-cut boundary term — component selection, hole filling, and
        /// downstream morphology stand in for the smoothness prior.
        internal bool[] GmmRefine(bool[] initialMask, ImageSnapshot snap,
            int seedX, int seedY, CancellationToken token, int iterations = 2)
        {
            int w = snap.Width, h = snap.Height;
            if (!HasMinimumPixels(initialMask, GmmMinSamples)) return initialMask;

            var (bx0, by0, bx1, by1) = GetMaskBounds(initialMask, w, h, margin: 0);
            if (bx1 <= bx0 || by1 <= by0) return initialMask;

            // ROI: mask bounds expanded so the band outside the dilated mask
            // supplies enough background context for the model.
            int mw = bx1 - bx0 + 1, mh = by1 - by0 + 1;
            int margin = Math.Max(16, Math.Max(mw, mh) / 4);
            int rx0 = Math.Max(0, bx0 - margin), ry0 = Math.Max(0, by0 - margin);
            int rx1 = Math.Min(w - 1, bx1 + margin), ry1 = Math.Min(h - 1, by1 + margin);
            int rw = rx1 - rx0 + 1, rh = ry1 - ry0 + 1;

            int sxr = seedX - rx0, syr = seedY - ry0;
            if ((uint)sxr >= rw || (uint)syr >= rh) return initialMask; // no valid anchor

            var (roiMask, _, _) = CropMask(initialMask, w, rx0, ry0, rx1, ry1);

            var fgColors = new float[GmmMaxSamples * 3];
            var bgColors = new float[GmmMaxSamples * 3];
            bool[] current = roiMask;

            for (int it = 0; it < iterations; it++)
            {
                token.ThrowIfCancellationRequested();

                // Foreground pool: eroded core; whole mask when erosion leaves
                // too little (thin objects).
                bool[] core = MorphErode(current, rw, rh, GmmErodeRadius);
                if (!HasMinimumPixels(core, GmmMinSamples)) core = current;

                // Background pool: outside the dilated mask, plus confirmed bg.
                bool[] dilated = MorphDilate(current, rw, rh, GmmDilateRadius);
                var bgSel = new bool[rw * rh];
                for (int y = 0; y < rh; y++)
                {
                    int gRow = (ry0 + y) * w;
                    int rRow = y * rw;
                    for (int x = 0; x < rw; x++)
                        bgSel[rRow + x] = !dilated[rRow + x]
                            || (snap.BgMask != null && snap.BgMask[gRow + rx0 + x]);
                }

                Gmm fg = FitGmm(fgColors, GatherSamples(core, rw, rh, rx0, ry0, snap, fgColors));
                Gmm bg = FitGmm(bgColors, GatherSamples(bgSel, rw, rh, rx0, ry0, snap, bgColors));
                if (fg == null || bg == null) break;

                // Reclassify by likelihood ratio.
                var next = new bool[rw * rh];
                for (int y = 0; y < rh; y++)
                {
                    if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                    int gy = ry0 + y;
                    int rowPix = gy * snap.Stride;
                    int gRow = gy * w;
                    int rRow = y * rw;
                    for (int x = 0; x < rw; x++)
                    {
                        int gx = rx0 + x;
                        if (snap.BgMask != null && snap.BgMask[gRow + gx]) continue; // hard background
                        int i = rowPix + gx * snap.Bpp;
                        int r, g, b;
                        if (snap.Bpp == 1) { r = g = b = snap.Pixels[i]; }
                        else { b = snap.Pixels[i]; g = snap.Pixels[i + 1]; r = snap.Pixels[i + 2]; }
                        next[rRow + x] = fg.LogLikelihood(r, g, b) > bg.LogLikelihood(r, g, b);
                    }
                }

                // Hard click anchor.
                for (int dy = -GmmHardSeedRadius; dy <= GmmHardSeedRadius; dy++)
                    for (int dx = -GmmHardSeedRadius; dx <= GmmHardSeedRadius; dx++)
                    {
                        if (dx * dx + dy * dy > GmmHardSeedRadius * GmmHardSeedRadius) continue;
                        int x = sxr + dx, y = syr + dy;
                        if ((uint)x >= rw || (uint)y >= rh) continue;
                        int gIdx = (ry0 + y) * w + (rx0 + x);
                        if (snap.BgMask != null && snap.BgMask[gIdx]) continue;
                        next[y * rw + x] = true;
                    }

                // Per-pixel likelihood has no smoothness prior, so the raw boundary
                // has 1-px fuzz; a radius-1 close-then-open knocks it down (cost: 1-px
                // features here, which the coarse candidates preserve and win when
                // they matter).
                next = MorphClose(next, rw, rh, 1);
                next = MorphOpen(next, rw, rh, 1);

                // The user clicked THE object: keep its component, refill holes.
                int seedIdx = syr * rw + sxr;
                if (!next[seedIdx]) break; // bgMask claims the click — nothing valid to follow
                next = KeepComponentContainingSeed(next, rw, rh, seedIdx);
                next = FillHoles(next, rw, rh);

                if (!HasMinimumPixels(next, GmmMinSamples)) break; // degenerated — keep previous
                current = next;
            }

            if (ReferenceEquals(current, roiMask)) return initialMask;

            // Paste back into full-image space.
            var result = new bool[w * h];
            for (int y = 0; y < rh; y++)
            {
                int rRow = y * rw, fRow = (ry0 + y) * w + rx0;
                for (int x = 0; x < rw; x++)
                    if (current[rRow + x]) result[fRow + x] = true;
            }
            return result;
        }

        // ---- CLICK SEGMENTATION ----
        // Per-image analysis data plus the click pipeline built on the primitives
        // above: choose a mask for the clicked point, clean it, and trace it.

        // ── Per-image analysis, built ONCE per image on a worker thread ──
        // Written during the build then read-only, so it is safe to share across
        // concurrent click operations.
        internal sealed class ImageAnalysis
        {
            public byte[] Pixels;
            public int Width, Height, Stride, Bpp;
            public bool[] BackgroundMask;
            public int[] Gradient;             // squared weighted-Sobel magnitude
            public int[] DistToBackground;     // BFS distance to border/background
            public int GradientEdgeThreshold;  // "counts as an edge" cutoff for mask scoring
            public float[] TextureMap;         // per-pixel local luma std-dev, PerceptualDistance scale (adaptive flood)
            public byte[] FlattenedPixels;     // illumination-normalized copy (poor-man's retinex), same stride/bpp
            public bool[] FlattenedBackgroundMask; // background model REBUILT on the flattened copy
            public SamImageState SamState;         // neural encoder output; null when no model is installed or encoding failed
        }


        /// Tuning values a segmentation click reads from the active outline mode.
        internal sealed class SegmentationSettings
        {
            /// <summary>Runs watershed for every click instead of the candidate portfolio.</summary>
            public bool ForceWatershed;

            /// <summary>Blur level applied to the gradient on the forced-watershed path.</summary>
            public int WatershedBlurLevel;

            /// <summary>Seed colour tolerance shared by the flood candidates.</summary>
            public double FillSensitivity;

            /// <summary>Gradient level at which a flood stops crossing an edge.</summary>
            public double EdgeThreshold;

            /// <summary>Smallest mask, in pixels, that counts as a real object.</summary>
            public int MinAreaPixels;
        }

        // Segments the object at the click. * Rescues clicks landing on background-
        // classified pixels by dropping the (locally wrong) background mask for this
        // operation. * Snaps the flood seed to the local distance-transform peak, like
        // watershed already did, so a click on a highlight or a few pixels from the
        // boundary still samples a representative interior color. * Runs a cheapest-
        // first CANDIDATE PORTFOLIO, stopping at the first acceptable mask: 0. neural
        // (SAM) mask — when Models\*.onnx are installed, the accumulated click(s)
        // prompt a MobileSAM- class decoder against the per-image embedding computed at
        // load; skipped entirely when no model is present; 1. classic flood — seed
        // colour distance with an edge stop; 2. adaptive flood — chroma-weighted seed
        // distance + texture channel, for shaded and textured objects; 3. classic flood
        // on the illumination-flattened copy — cancels vignetting/lighting falloff,
        // with a background model rebuilt on that copy; 4. watershed, no blur (cached
        // gradient); 5. watershed, blur 1 — the portfolio absorbs WatershedBlurLevel;
        // 6. half-resolution classic flood, upsampled — downsampling averages out the
        // noise/texture that fragments the full-resolution flood.
        // A candidate mask scoring at or above this is accepted immediately and the
        // portfolio stops; below it, the next candidate runs and the highest score wins.
        private const double AcceptableMaskScore = 0.5;

        // Masks at or above this already hug the image edges, so GmmRefine is skipped.
        private const double RefinementSkipScore = 0.72;

        internal (bool[] mask, int seedX, int seedY, ImageSnapshot snap, string info) SegmentAtClick(
            int px, int py, ImageAnalysis a, SegmentationSettings settings, CancellationToken token,
            (int x, int y)[] samPrompts = null)
        {
            int clickIdx = py * a.Width + px;
            bool clickOnBg = a.BackgroundMask != null && a.BackgroundMask[clickIdx];

            // Rescue: the click hit a background-classified pixel, so the model is
            // wrong here — run this operation without it.
            bool[] opBgMask = clickOnBg ? null : a.BackgroundMask;
            var snap = new ImageSnapshot(a.Pixels, opBgMask, a.Width, a.Height, a.Stride, a.Bpp);
            int[] opDist = clickOnBg ? null : a.DistToBackground; // null → watershed recomputes vs. border only

            int sx = px, sy = py;
            if (!clickOnBg)
                (sx, sy) = FindDistanceTransformPeak(px, py, a.Width, a.Height,
                    a.DistToBackground, a.BackgroundMask, searchRadius: 8);

            var (sr, sg, sb) = SampleSeedColor(sx, sy, snap, radius: 2);

            if (settings.ForceWatershed)
            {
                // Forced-watershed path, selected by the UseWatershed toggle.
                bool[] forced = WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                    seedRadius: 3, blurLevel: settings.WatershedBlurLevel);
                if (forced != null) return (forced, sx, sy, snap, "");
                return (FloodFill(sx, sy, sr, sg, sb, snap,
                    settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token), sx, sy, snap, "");
            }

            // Size-veto threshold. Cap peak distance so a sparse/failed background
            // mask can only weaken the veto, never reject correct ordinary masks.
            int peakDist = clickOnBg ? 0 : a.DistToBackground[sy * a.Width + sx];
            peakDist = Math.Min(peakDist, Math.Min(a.Width, a.Height) / 8);
            int minPlausibleArea = Math.Max(settings.MinAreaPixels,
                (int)(0.5 * Math.PI * (double)peakDist * peakDist));

            bool[] best = null;
            double bestScore = -1.0;
            string bestName = "none";
            var diag = new System.Text.StringBuilder();

            double Score(bool[] m)
            {
                if (m == null) return 0;
                if (!HasMinimumPixels(m, minPlausibleArea)) return 0; // size veto
                return ScoreMask(m, a.Width, a.Height, a.Gradient, a.GradientEdgeThreshold);
            }

            bool Consider(string name, bool[] m)
            {
                double s = Score(m);
                diag.Append(name).Append('=').Append(s.ToString("F2")).Append(' ');
                if (s > bestScore) { bestScore = s; best = m; bestName = name; }
                return s >= AcceptableMaskScore;
            }

            // GrabCut-style GMM refinement: a mixture fitted to the chosen mask covers
            // multiple color modes a single seed can't.
            (bool[] mask, int seedX, int seedY, ImageSnapshot snap, string info) Finish(bool[] chosen)
            {
                bool refinedWon = false;
                if (bestScore < RefinementSkipScore)
                {
                    bool[] refined = GmmRefine(chosen, snap, sx, sy, token);
                    if (!ReferenceEquals(refined, chosen))
                    {
                        double rs = Score(refined);
                        diag.Append("gmm=").Append(rs.ToString("F2")).Append(' ');
                        if (rs > Math.Max(bestScore, 0.0)) { chosen = refined; refinedWon = true; }
                    }
                }

                // One diagnostic line per click: every candidate's score plus the
                // winner. The caller publishes it through LastSegmentationInfo.
                string info = $"[outline] click({px},{py}) {diag}winner={bestName}{(refinedWon ? "+gmm" : "")}";
                System.Diagnostics.Debug.WriteLine(info);

                return (chosen, sx, sy, snap, info);
            }

            // 0) Neural candidate: click-prompted SAM when a model is installed.
            if (a.SamState != null)
            {
                try
                {
                    var prompts = new List<(int x, int y, bool positive)>();
                    if (samPrompts != null)
                        foreach (var (qx, qy) in samPrompts) prompts.Add((qx, qy, true));
                    if (prompts.Count == 0) prompts.Add((px, py, true));

                    bool[] neural = SamSegmenter.Shared?.Segment(
                        a.SamState, prompts, a.Width, a.Height, token);
                    if (neural != null && Consider("neural", neural)) return Finish(best);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[sam] decode failed: {ex.Message}");
                    diag.Append("neural=err ");
                }
                token.ThrowIfCancellationRequested();
            }
            else
            {
                diag.Append("neural=off ");
            }

            // 1) Classic flood.
            bool[] classicFlood = FloodFill(sx, sy, sr, sg, sb, snap,
                settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
            if (Consider("flood", classicFlood)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 2) Chroma + texture aware flood.
            float seedTexture = SampleSeedTexture(sx, sy, snap, a.TextureMap, radius: 2);
            bool[] adaptive = FloodFillAdaptive(sx, sy, sr, sg, sb, seedTexture, snap,
                a.TextureMap, settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
            if (Consider("adaptive", adaptive)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 3) Classic flood on the illumination-flattened copy (bg model rebuilt on
            // it) for when the BACKGROUND carries the gradient.
            bool clickOnFlatBg = a.FlattenedBackgroundMask != null && a.FlattenedBackgroundMask[clickIdx];
            var flatSnap = new ImageSnapshot(a.FlattenedPixels,
                clickOnFlatBg ? null : a.FlattenedBackgroundMask,
                a.Width, a.Height, a.Stride, a.Bpp);
            var (flr, flg, flb) = SampleSeedColor(sx, sy, flatSnap, radius: 2);
            bool[] flatFlood = FloodFill(sx, sy, flr, flg, flb, flatSnap,
                settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
            if (Consider("flat", flatFlood)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 4) Watershed, no blur (cached gradient).
            bool[] shed0 = WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                seedRadius: 3, blurLevel: 0);
            if (Consider("shed0", shed0)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 5) Watershed, blur level 1 (fresh gradient from the blurred copy).
            bool[] shed1 = WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                seedRadius: 3, blurLevel: 1);
            if (Consider("shed1", shed1)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 6) Half-resolution classic flood, upsampled.
            var half = DownsampleHalf(snap);
            if (half.Width >= 8 && half.Height >= 8 && half.Width < a.Width)
            {
                int hx = Math.Min(half.Width - 1, sx / 2);
                int hy = Math.Min(half.Height - 1, sy / 2);
                if (half.BgMask == null || !half.BgMask[hy * half.Width + hx])
                {
                    var (hr, hg, hb) = SampleSeedColor(hx, hy, half, radius: 2);
                    bool[] halfMask = FloodFill(hx, hy, hr, hg, hb, half,
                        settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
                    Consider("halfres", UpsampleMask2x(halfMask, half.Width, half.Height, a.Width, a.Height));
                }
            }

            // Nothing acceptable: best-scoring candidate wins, or the classic flood
            // if even that was vetoed.
            if (best == null || bestScore <= 0) best = classicFlood;
            return Finish(best);
        }

        // Runs the full cleanup pipeline on a full-image-space mask.
        // Returns the cleaned full-image-space mask, or null if it fails.
        internal bool[] CleanMaskFullSpace(bool[] rawFull, ImageSnapshot snap,
            int seedPxX, int seedPxY, int minAreaPixels, CancellationToken token,
            bool preserveMultipleComponents = false)
        {
            int sw = snap.Width, sh = snap.Height;

            // Strip the 5-px border band only when the mask fills enough of it to
            // look like background bleed, so a subject that genuinely touches the
            // frame in a small arc keeps its edge pixels.
            const int band = 5;
            long bandFg = 0, bandTotal = 0;
            for (int i = 0; i < rawFull.Length; i++)
            {
                int rx = i % sw, ry = i / sw;
                if (rx >= band && rx < sw - band && ry >= band && ry < sh - band) continue;
                bandTotal++;
                if (rawFull[i]) bandFg++;
            }
            if (bandTotal > 0 && bandFg > bandTotal * 0.2)
            {
                for (int i = 0; i < rawFull.Length; i++)
                {
                    if (!rawFull[i]) continue;
                    int rx = i % sw, ry = i / sw;
                    if (rx < band || rx >= sw - band || ry < band || ry >= sh - band) rawFull[i] = false;
                }
            }

            token.ThrowIfCancellationRequested();

            var (bx0, by0, bx1, by1) = GetMaskBounds(rawFull, sw, sh, margin: 4);
            var (cropped, cw, ch) = CropMask(rawFull, sw, bx0, by0, bx1, by1);

            // Seed in crop space, clamped: with the conditional strip the seed can
            // in principle sit just outside the surviving bounds.
            int cpx = Math.Max(0, Math.Min(cw - 1, seedPxX - bx0));
            int cpy = Math.Max(0, Math.Min(ch - 1, seedPxY - by0));

            bool[] traced;
            if (preserveMultipleComponents)
            {
                // Clean each component independently (own crop, scaled radii) and
                // keep all survivors.
                bool[] work = CleanEachComponent(cropped, cw, ch, minAreaPixels, token);
                traced = HasMinimumPixels(work, minAreaPixels) ? work : cropped;
            }
            else
            {
                // Scale cleanup morphology to the object, not fixed radii: fixed
                // radii would delete any feature under ~5 px (an antenna/spine on a
                // small specimen).
                int maskArea = CountPixels(cropped);
                double objScale = Math.Sqrt(Math.Max(1, maskArea));
                // Radius 2 is the floor for ordinary objects; components under
                // 2500 px use radius 1 so they are not erased outright.
                int openRadius = maskArea < 2500
                    ? 1
                    : (int)Math.Min(3, Math.Max(2, objScale / 64.0));
                int corridorWidth = Math.Min(3, openRadius + 1);

                bool[] work = CleanComponentMorphology(cropped, cw, ch,
                    openRadius, closeRadius: 1, minCorridorWidth: corridorWidth);

                token.ThrowIfCancellationRequested();

                // Keep the clicked component if it survived cleanup; fall back to
                // "largest" only if it didn't (MorphOpen can split a mask).
                int cseed = cpy * cw + cpx;
                work = work[cseed]
                    ? KeepComponentContainingSeed(work, cw, ch, cseed)
                    : KeepLargestComponent(work, cw, ch);

                traced = PrepareMaskForTracing(work, cropped, cw, ch, cpx, cpy, minAreaPixels);
            }

            // Paste cropped result back into full-image space
            bool[] full = new bool[sw * sh];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    if (traced[y * cw + x]) full[(by0 + y) * sw + (bx0 + x)] = true;
            return full;
        }


        /// Traces a full-image mask, snaps the boundary onto the image gradient, and
        /// simplifies it.
        internal (List<System.Windows.Point> simplified, List<System.Windows.Point> dense) BuildSimplifiedContour(
            bool[] full, ImageSnapshot snap, int[] gradient, int edgeGradThreshold, double simplifyEpsilon)
        {
            var (bx0, by0, bx1, by1) = GetMaskBounds(full, snap.Width, snap.Height, margin: 2);
            var (cropped, cw, ch) = CropMask(full, snap.Width, bx0, by0, bx1, by1);

            var boundary = TraceBoundary(cropped, cw, ch);
            if (boundary.Count < 8) return (null, null);

            // The snap is conservative (see SnapBoundaryToGradient).
            var rawBoundary = boundary;
            boundary = SnapBoundaryToGradient(boundary, bx0, by0,
                gradient, snap.Width, snap.Height, edgeGradThreshold);

            var simplified = GeometryCalculations.DouglasPeucker(boundary, simplifyEpsilon);
            if (simplified.Count < 3 || PolylineGeometry.HasSelfIntersection(simplified))
            {
                boundary = rawBoundary;
                simplified = GeometryCalculations.DouglasPeucker(boundary, simplifyEpsilon);
                if (simplified.Count < 3) return (null, null);
                if (PolylineGeometry.HasSelfIntersection(simplified))
                    simplified = new List<System.Windows.Point>(boundary);
            }

            // Shift both contours out of crop space into full-image coordinates.
            var simplifiedFull = new List<System.Windows.Point>(simplified.Count);
            foreach (var p in simplified)
                simplifiedFull.Add(new System.Windows.Point(p.X + bx0, p.Y + by0));

            var denseFull = new List<System.Windows.Point>(boundary.Count);
            foreach (var p in boundary)
                denseFull.Add(new System.Windows.Point(p.X + bx0, p.Y + by0));

            return (simplifiedFull, denseFull);
        }


    }
}