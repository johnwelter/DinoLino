using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DinoLino.Utilities
{
    // Everything one click needs to become an outline, in the order it happens:
    //
    //   ImageAnalysis      — the per-image caches, built once on a worker thread.
    //   SegmentationSettings — the tuning values the panel exposes.
    //   ClickSegmentation  — the candidate portfolio, the scoring, and the cleanup.
    //   SamSegmenter       — candidate 0's implementation (ONNX encoder/decoder).
    //
    // The pixel-level operations these compose live in OutlineProcessor. The split
    // is deliberate: OutlineProcessor knows how to make and clean a mask, this file
    // decides WHICH mask a click should get and what happens to the winner.

    /// <summary>
    /// Per-image caches, built ONCE per image on a worker thread and then read-only,
    /// so it is safe to share across concurrent click operations.
    /// </summary>
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

        /// Builds every cache for one image: background model, Sobel gradient,
        /// distance transform, texture, illumination-flattened copy, and the SAM
        /// encode. Costs 1–3 s on a large image, which is why the caller runs it on a
        /// worker and the first click awaits it.
        internal static ImageAnalysis Build(byte[] pixels, int w, int h, int stride, int bpp,
            CancellationToken token)
        {
            var proc = new OutlineProcessor();

            // SAM image encoder, run once per image (in parallel with the classical
            // caches) when a model is installed; each click then pays only a fast
            // decoder pass. Null when absent/failed, and downstream falls back.
            Task<SamImageState> samTask = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    return SamSegmenter.Shared?.EncodeImage(pixels, w, h, stride, bpp);
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[sam] encode failed: {ex.Message}");
                    return null;
                }
            }, token);

            // Border-palette background model (see EstimateBackgroundPalette):
            // ~6 clustered border colors + per-corner trust flags + adaptive threshold.
            var (bgPalette, hardSeedCorners, adaptiveThreshold) =
                proc.EstimateBackgroundPalette(pixels, w, h, stride, bpp);
            bool[] bgMask = proc.BuildBackgroundMaskProgressive(
                pixels, w, h, stride, bpp, bgPalette, hardSeedCorners,
                tightThreshold: adaptiveThreshold * 0.6,
                relaxedThreshold: adaptiveThreshold);
            token.ThrowIfCancellationRequested();

            var snap = new ImageSnapshot(pixels, bgMask, w, h, stride, bpp);
            int[] gradient = proc.ComputeGradient(snap);

            // Edge cutoff for mask scoring: 90th-percentile gradient, floored at a
            // ~9-level luma step so a flat background can't make every rim look edgy.
            int edgeThreshold = Math.Max(
                OutlineProcessor.GradientPercentileThreshold(gradient, 0.90), 120_000);

            // Per-pixel local texture (luma std-dev over 7×7). Lets the adaptive
            // flood match on texture and self-tune tolerance from the seed.
            float[] textureMap = proc.ComputeTextureMap(snap);
            token.ThrowIfCancellationRequested();

            int[] distToBackground = proc.ComputeDistanceToBackground(w, h, bgMask);
            token.ThrowIfCancellationRequested();

            // Illumination-flattened copy (retinex, radius ~min(w,h)/8) with its
            // background model rebuilt on it, so a vignetted/side-lit scene becomes
            // uniform for both the flattened flood and its bg mask.
            int flattenRadius = Math.Max(8, Math.Min(w, h) / 8);
            byte[] flattened = proc.ComputeIlluminationFlattened(snap, flattenRadius);
            var (flatPalette, flatCorners, flatThreshold) =
                proc.EstimateBackgroundPalette(flattened, w, h, stride, bpp);
            bool[] flatBgMask = proc.BuildBackgroundMaskProgressive(
                flattened, w, h, stride, bpp, flatPalette, flatCorners,
                tightThreshold: flatThreshold * 0.6,
                relaxedThreshold: flatThreshold);
            token.ThrowIfCancellationRequested();

            SamImageState samState = null;
            try { samState = samTask.Result; } catch { samState = null; }

            System.Diagnostics.Debug.WriteLine(samState != null
                ? $"[sam] embedding ready ({w}x{h}) — neural candidate active"
                : "[sam] embedding unavailable — neural candidate disabled for this image");

            return new ImageAnalysis
            {
                Pixels = pixels,
                Width = w,
                Height = h,
                Stride = stride,
                Bpp = bpp,
                BackgroundMask = bgMask,
                Gradient = gradient,
                DistToBackground = distToBackground,
                GradientEdgeThreshold = edgeThreshold,
                TextureMap = textureMap,
                FlattenedPixels = flattened,
                FlattenedBackgroundMask = flatBgMask,
                SamState = samState
            };
        }
    }

    /// <summary>Tuning values a segmentation click reads from the active outline mode.</summary>
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

    /// <summary>
    /// One click's journey from pixel coordinates to a polyline: pick a candidate
    /// mask, clean it, trace it.
    /// </summary>
    /// <remarks>
    /// Holds an OutlineProcessor, whose scratch buffers are not thread-safe, so a
    /// ClickSegmentation instance belongs to ONE operation and must not be shared
    /// across overlapping clicks. Create one per operation and let it die with it.
    /// </remarks>
    internal sealed class ClickSegmentation
    {
        // A candidate mask scoring at or above this is accepted immediately and the
        // portfolio stops; below it, the next candidate runs and the highest score wins.
        private const double AcceptableMaskScore = 0.5;

        // Masks at or above this already hug the image edges, so GmmRefine is skipped.
        private const double RefinementSkipScore = 0.72;

        private readonly OutlineProcessor _proc = new OutlineProcessor();

        /// The operation's processor, for the few pixel-level steps the caller drives
        /// itself (multi-click's bridge close, the minimum-area check). Same instance,
        /// so the same one-per-operation, one-thread rule applies.
        internal OutlineProcessor Processor => _proc;

        // ---- MASK SCORING ----

        /// Heuristic quality score in [0,1] for a raw mask: * edge alignment — fraction
        /// of the rim on strong gradient (±2 px tolerance, so masks near but not
        /// exactly on the edge aren't penalized), * border leak — foreground in the
        /// border band is punished, * size sanity — near-full-frame masks are almost
        /// certainly leaks.
        private static double ScoreMask(bool[] mask, int w, int h, int[] gradient, int edgeGradThreshold)
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

        // ---- CANDIDATE PORTFOLIO ----

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
                (sx, sy) = _proc.FindDistanceTransformPeak(px, py, a.Width, a.Height,
                    a.DistToBackground, a.BackgroundMask, searchRadius: 8);

            var (sr, sg, sb) = _proc.SampleSeedColor(sx, sy, snap, radius: 2);

            if (settings.ForceWatershed)
            {
                // Forced-watershed path, selected by the UseWatershed toggle.
                bool[] forced = _proc.WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                    seedRadius: 3, blurLevel: settings.WatershedBlurLevel);
                if (forced != null) return (forced, sx, sy, snap, "");
                return (_proc.FloodFill(sx, sy, sr, sg, sb, snap,
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
                if (!_proc.HasMinimumPixels(m, minPlausibleArea)) return 0; // size veto
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
                    bool[] refined = _proc.GmmRefine(chosen, snap, sx, sy, token);
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
            bool[] classicFlood = _proc.FloodFill(sx, sy, sr, sg, sb, snap,
                settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
            if (Consider("flood", classicFlood)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 2) Chroma + texture aware flood.
            float seedTexture = _proc.SampleSeedTexture(sx, sy, snap, a.TextureMap, radius: 2);
            bool[] adaptive = _proc.FloodFillAdaptive(sx, sy, sr, sg, sb, seedTexture, snap,
                a.TextureMap, settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
            if (Consider("adaptive", adaptive)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 3) Classic flood on the illumination-flattened copy (bg model rebuilt on
            // it) for when the BACKGROUND carries the gradient.
            bool clickOnFlatBg = a.FlattenedBackgroundMask != null && a.FlattenedBackgroundMask[clickIdx];
            var flatSnap = new ImageSnapshot(a.FlattenedPixels,
                clickOnFlatBg ? null : a.FlattenedBackgroundMask,
                a.Width, a.Height, a.Stride, a.Bpp);
            var (flr, flg, flb) = _proc.SampleSeedColor(sx, sy, flatSnap, radius: 2);
            bool[] flatFlood = _proc.FloodFill(sx, sy, flr, flg, flb, flatSnap,
                settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
            if (Consider("flat", flatFlood)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 4) Watershed, no blur (cached gradient).
            bool[] shed0 = _proc.WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                seedRadius: 3, blurLevel: 0);
            if (Consider("shed0", shed0)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 5) Watershed, blur level 1 (fresh gradient from the blurred copy).
            bool[] shed1 = _proc.WatershedSegment(px, py, snap, a.Gradient, opDist, token,
                seedRadius: 3, blurLevel: 1);
            if (Consider("shed1", shed1)) return Finish(best);
            token.ThrowIfCancellationRequested();

            // 6) Half-resolution classic flood, upsampled.
            var half = _proc.DownsampleHalf(snap);
            if (half.Width >= 8 && half.Height >= 8 && half.Width < a.Width)
            {
                int hx = Math.Min(half.Width - 1, sx / 2);
                int hy = Math.Min(half.Height - 1, sy / 2);
                if (half.BgMask == null || !half.BgMask[hy * half.Width + hx])
                {
                    var (hr, hg, hb) = _proc.SampleSeedColor(hx, hy, half, radius: 2);
                    bool[] halfMask = _proc.FloodFill(hx, hy, hr, hg, hb, half,
                        settings.FillSensitivity, settings.FillSensitivity * 0.5, settings.EdgeThreshold, token);
                    Consider("halfres", _proc.UpsampleMask2x(halfMask, half.Width, half.Height, a.Width, a.Height));
                }
            }

            // Nothing acceptable: best-scoring candidate wins, or the classic flood
            // if even that was vetoed.
            if (best == null || bestScore <= 0) best = classicFlood;
            return Finish(best);
        }

        // ---- CLEANUP ----

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

            var (bx0, by0, bx1, by1) = _proc.GetMaskBounds(rawFull, sw, sh, margin: 4);
            var (cropped, cw, ch) = _proc.CropMask(rawFull, sw, bx0, by0, bx1, by1);

            // Seed in crop space, clamped: with the conditional strip the seed can
            // in principle sit just outside the surviving bounds.
            int cpx = Math.Max(0, Math.Min(cw - 1, seedPxX - bx0));
            int cpy = Math.Max(0, Math.Min(ch - 1, seedPxY - by0));

            bool[] traced;
            if (preserveMultipleComponents)
            {
                // Clean each component independently (own crop, scaled radii) and
                // keep all survivors.
                bool[] work = _proc.CleanEachComponent(cropped, cw, ch, minAreaPixels, token);
                traced = _proc.HasMinimumPixels(work, minAreaPixels) ? work : cropped;
            }
            else
            {
                var (openRadius, corridorWidth) =
                    OutlineProcessor.MorphologyRadiiFor(_proc.CountPixels(cropped));

                bool[] work = _proc.CleanComponentMorphology(cropped, cw, ch,
                    openRadius, closeRadius: 1, minCorridorWidth: corridorWidth);

                token.ThrowIfCancellationRequested();

                // Keep the clicked component if it survived cleanup; fall back to
                // "largest" only if it didn't (MorphOpen can split a mask).
                int cseed = cpy * cw + cpx;
                work = work[cseed]
                    ? _proc.KeepComponentContainingSeed(work, cw, ch, cseed)
                    : _proc.KeepLargestComponent(work, cw, ch);

                traced = _proc.PrepareMaskForTracing(work, cropped, cw, ch, cpx, cpy, minAreaPixels);
            }

            // Paste cropped result back into full-image space
            bool[] full = new bool[sw * sh];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    if (traced[y * cw + x]) full[(by0 + y) * sw + (bx0 + x)] = true;
            return full;
        }

        // ---- MASK → POLYLINE ----

        /// Traces a full-image mask, snaps the boundary onto the image gradient, and
        /// simplifies it.
        internal (List<Point> simplified, List<Point> dense) BuildSimplifiedContour(
            bool[] full, ImageSnapshot snap, int[] gradient, int edgeGradThreshold, double simplifyEpsilon)
        {
            var (bx0, by0, bx1, by1) = _proc.GetMaskBounds(full, snap.Width, snap.Height, margin: 2);
            var (cropped, cw, ch) = _proc.CropMask(full, snap.Width, bx0, by0, bx1, by1);

            var boundary = _proc.TraceBoundary(cropped, cw, ch);
            if (boundary.Count < 8) return (null, null);

            // The snap is conservative (see SnapBoundaryToGradient).
            var rawBoundary = boundary;
            boundary = _proc.SnapBoundaryToGradient(boundary, bx0, by0,
                gradient, snap.Width, snap.Height, edgeGradThreshold);

            var simplified = GeometryCalculations.DouglasPeucker(boundary, simplifyEpsilon);
            if (simplified.Count < 3 || PolylineGeometry.HasSelfIntersection(simplified))
            {
                boundary = rawBoundary;
                simplified = GeometryCalculations.DouglasPeucker(boundary, simplifyEpsilon);
                if (simplified.Count < 3) return (null, null);
                if (PolylineGeometry.HasSelfIntersection(simplified))
                    simplified = new List<Point>(boundary);
            }

            // Shift both contours out of crop space into full-image coordinates.
            var simplifiedFull = new List<Point>(simplified.Count);
            foreach (var p in simplified)
                simplifiedFull.Add(new Point(p.X + bx0, p.Y + by0));

            var denseFull = new List<Point>(boundary.Count);
            foreach (var p in boundary)
                denseFull.Add(new Point(p.X + bx0, p.Y + by0));

            return (simplifiedFull, denseFull);
        }
    }

    // =====================================================================
    // CANDIDATE 0: NEURAL SEGMENTATION
    // A SAM-style ONNX encoder/decoder pair. The encoder runs once per image
    // (ImageAnalysis.Build); each click pays only a fast decoder pass.
    // =====================================================================

    /// <summary>
    /// Per-image SAM encoder output and the geometry needed to map clicks into model space.
    /// </summary>
    internal sealed class SamImageState
    {
        public DenseTensor<float> Embedding;
        public int Width;
        public int Height;
        public float Scale;
    }

    /// <summary>
    /// Click-prompted segmentation using a SAM-style ONNX encoder/decoder pair.
    /// </summary>
    internal sealed class SamSegmenter : IDisposable
    {
        private const int InputSize = 1024;
        private const bool NormalizeEncoderInput = false;
        private static readonly float[] PixelMean = { 123.675f, 116.28f, 103.53f };
        private static readonly float[] PixelStd = { 58.395f, 57.12f, 57.375f };
        private const int MaxDecodeSide = 2048;

        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private readonly string _encInputName;
        private readonly int[] _encInputDims;
        private readonly string _decEmbedName;
        private readonly string _decCoordsName;
        private readonly string _decLabelsName;
        private readonly string _decMaskName;
        private readonly string _decHasMaskName;
        private readonly string _decSizeName;
        private readonly object _decodeLock = new object();
        private bool _normalize = NormalizeEncoderInput;

        // =====================
        // Singleton access
        // =====================
        private static readonly object InitLock = new object();
        private static bool _initTried;
        private static SamSegmenter _shared;

        /// <summary>
        /// Loads the first usable encoder/decoder pair from the application folder or Models folder.
        /// </summary>
        internal static SamSegmenter Shared
        {
            get
            {
                lock (InitLock)
                {
                    if (!_initTried)
                    {
                        _initTried = true;
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        _shared = TryCreate(Path.Combine(baseDir, "Models")) ?? TryCreate(baseDir);
                        System.Diagnostics.Debug.WriteLine(_shared != null
                            ? "[sam] models loaded — neural candidate enabled"
                            : "[sam] no usable models found — classical pipeline only");
                    }
                    return _shared;
                }
            }
        }

        /// <summary>
        /// Attempts to load a matching encoder/decoder pair from the given folder.
        /// </summary>
        internal static SamSegmenter TryCreate(string modelDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(modelDirectory) || !Directory.Exists(modelDirectory)) return null;

                var onnx = Directory.GetFiles(modelDirectory, "*.onnx");
                if (onnx.Length < 2) return null;

                string encoderPath = onnx.FirstOrDefault(f =>
                    Path.GetFileName(f).IndexOf("encoder", StringComparison.OrdinalIgnoreCase) >= 0);
                if (encoderPath == null) return null;

                string decoderPath = onnx.FirstOrDefault(f =>
                        !string.Equals(f, encoderPath, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(f).IndexOf("decoder", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? onnx.FirstOrDefault(f =>
                        !string.Equals(f, encoderPath, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(f).IndexOf("encoder", StringComparison.OrdinalIgnoreCase) < 0);

                if (decoderPath == null) return null;

                return new SamSegmenter(encoderPath, decoderPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[sam] load failed: {ex.Message}");
                return null;
            }
        }

        private SamSegmenter(string encoderPath, string decoderPath)
        {
            _encoder = new InferenceSession(encoderPath);
            _decoder = new InferenceSession(decoderPath);

            var encIn = _encoder.InputMetadata.First();
            _encInputName = encIn.Key;
            _encInputDims = encIn.Value.Dimensions ?? Array.Empty<int>();

            // Resolve decoder inputs by substring so common export variants still bind.
            string FindInput(string needle)
            {
                foreach (var k in _decoder.InputMetadata.Keys)
                    if (k.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return k;
                return null;
            }

            _decEmbedName = FindInput("embed");
            _decCoordsName = FindInput("coord");
            _decLabelsName = FindInput("label");
            _decHasMaskName = FindInput("has_mask");
            _decMaskName = _decoder.InputMetadata.Keys.FirstOrDefault(k =>
                k.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 &&
                k.IndexOf("has", StringComparison.OrdinalIgnoreCase) < 0);
            _decSizeName = FindInput("orig") ?? FindInput("size");

            if (_decEmbedName == null || _decCoordsName == null || _decLabelsName == null)
                throw new InvalidOperationException(
                    "decoder inputs not recognized — expected the official SAM ONNX export contract");

            System.Diagnostics.Debug.WriteLine(
                $"[sam] encoder input '{_encInputName}' dims=[{string.Join(",", _encInputDims)}]");
            System.Diagnostics.Debug.WriteLine(
                $"[sam] decoder inputs: {string.Join(", ", _decoder.InputMetadata.Keys)}");
            System.Diagnostics.Debug.WriteLine(
                $"[sam] decoder outputs: {string.Join(", ", _decoder.OutputMetadata.Keys)}");

            try
            {
                CalibrateNormalization();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[sam] calibration failed: {ex.Message} — using default normalize={_normalize}");
            }
        }

        // =====================
        // Encoder calibration
        // =====================

        /// <summary>
        /// Chooses the correct encoder normalization mode by testing both against a synthetic target.
        /// </summary>
        private void CalibrateNormalization()
        {
            const int S = 512;
            const int lo = 160, hi = 352;

            var pixels = new byte[S * S * 4];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int i = (y * S + x) * 4;
                    bool inSquare = x >= lo && x < hi && y >= lo && y < hi;
                    pixels[i] = inSquare ? (byte)60 : (byte)140;
                    pixels[i + 1] = inSquare ? (byte)90 : (byte)120;
                    pixels[i + 2] = inSquare ? (byte)200 : (byte)110;
                    pixels[i + 3] = 255;
                }

            double IoUFor(bool mode)
            {
                _normalize = mode;
                var state = EncodeImage(pixels, S, S, S * 4, 4);
                bool[] mask = Segment(state, new[] { (S / 2, S / 2, true) }, S, S, CancellationToken.None);
                if (mask == null) return 0;

                long inter = 0, union = 0;
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        bool truth = x >= lo && x < hi && y >= lo && y < hi;
                        bool m = mask[y * S + x];
                        if (truth && m) inter++;
                        if (truth || m) union++;
                    }

                return union == 0 ? 0 : (double)inter / union;
            }

            double iouNormalized = IoUFor(true);
            double iouRaw = IoUFor(false);
            _normalize = iouNormalized >= iouRaw;

            string note = Math.Max(iouNormalized, iouRaw) < 0.5
                ? " (LOW CONFIDENCE — check that the encoder/decoder pair matches)"
                : "";

            System.Diagnostics.Debug.WriteLine(
                $"[sam] normalization calibration: normalized={iouNormalized:F2}, raw={iouRaw:F2} → " +
                $"using {(_normalize ? "SAM-normalized" : "raw 0-255")} encoder input{note}");
        }

        // =====================
        // Image encoding
        // =====================

        /// <summary>
        /// Encodes one image into a reusable embedding tensor.
        /// </summary>
        internal SamImageState EncodeImage(byte[] pixels, int w, int h, int stride, int bpp)
        {
            // Keep the aspect ratio and map the long side to the model input square.
            float scale = (float)InputSize / Math.Max(w, h);
            int rw = Math.Max(1, (int)Math.Round(w * scale));
            int rh = Math.Max(1, (int)Math.Round(h * scale));

            // Detect the encoder's expected layout from metadata.
            var d = _encInputDims;
            bool nchw = d.Length == 4 && d[1] == 3;
            bool nhwc = d.Length == 4 && d[3] == 3;
            bool hwc = d.Length == 3 && d[2] == 3;
            if (!nchw && !nhwc && !hwc) nchw = true;

            DenseTensor<float> input =
                nchw ? new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize }) :
                nhwc ? new DenseTensor<float>(new[] { 1, InputSize, InputSize, 3 }) :
                       new DenseTensor<float>(new[] { InputSize, InputSize, 3 });

            // Resize into the model's square input and apply SAM normalization if enabled.
            for (int y = 0; y < rh; y++)
            {
                float sy = (y + 0.5f) / scale - 0.5f;
                for (int x = 0; x < rw; x++)
                {
                    float sx = (x + 0.5f) / scale - 0.5f;
                    for (int c = 0; c < 3; c++)
                    {
                        float v = SampleBilinear(pixels, stride, bpp, w, h, sx, sy, c);
                        if (_normalize) v = (v - PixelMean[c]) / PixelStd[c];
                        if (nchw) input[0, c, y, x] = v;
                        else if (nhwc) input[0, y, x, c] = v;
                        else input[y, x, c] = v;
                    }
                }
            }

            using (var results = _encoder.Run(new[] { NamedOnnxValue.CreateFromTensor(_encInputName, input) }))
            {
                var emb = results.First().AsTensor<float>();
                var copy = new DenseTensor<float>(emb.ToArray(), emb.Dimensions.ToArray());
                return new SamImageState { Embedding = copy, Width = w, Height = h, Scale = scale };
            }
        }

        // =====================
        // Prompt decoding
        // =====================

        /// <summary>
        /// Runs the decoder for the supplied click prompts and returns a full-size mask.
        /// </summary>
        internal bool[] Segment(SamImageState state, IReadOnlyList<(int x, int y, bool positive)> points,
            int imageWidth, int imageHeight, CancellationToken token)
        {
            if (state == null || points == null || points.Count == 0) return null;
            token.ThrowIfCancellationRequested();

            // Add one padding prompt to match the common SAM export contract.
            int n = points.Count;
            var coords = new DenseTensor<float>(new[] { 1, n + 1, 2 });
            var labels = new DenseTensor<float>(new[] { 1, n + 1 });
            for (int i = 0; i < n; i++)
            {
                // Map image-space clicks into the resized model frame.
                coords[0, i, 0] = points[i].x * state.Scale;
                coords[0, i, 1] = points[i].y * state.Scale;
                labels[0, i] = points[i].positive ? 1f : 0f;
            }
            labels[0, n] = -1f;

            // Limit in-graph upsampling for large images to keep memory usage bounded.
            int tw = imageWidth, th = imageHeight;
            if (Math.Max(tw, th) > MaxDecodeSide)
            {
                double s = (double)MaxDecodeSide / Math.Max(tw, th);
                tw = Math.Max(1, (int)Math.Round(tw * s));
                th = Math.Max(1, (int)Math.Round(th * s));
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_decEmbedName, state.Embedding),
                NamedOnnxValue.CreateFromTensor(_decCoordsName, coords),
                NamedOnnxValue.CreateFromTensor(_decLabelsName, labels)
            };

            if (_decMaskName != null)
                inputs.Add(NamedOnnxValue.CreateFromTensor(_decMaskName,
                    new DenseTensor<float>(new[] { 1, 1, 256, 256 })));

            if (_decHasMaskName != null)
                inputs.Add(NamedOnnxValue.CreateFromTensor(_decHasMaskName,
                    new DenseTensor<float>(new[] { 1 })));

            bool usedSizeInput = _decSizeName != null;
            if (usedSizeInput)
            {
                var size = new DenseTensor<float>(new[] { 2 });
                size[0] = th;
                size[1] = tw;
                inputs.Add(NamedOnnxValue.CreateFromTensor(_decSizeName, size));
            }

            float[] maskData;
            int[] maskDims;
            float[] iou = null;

            lock (_decodeLock)
            {
                using (var results = _decoder.Run(inputs))
                {
                    var masksOut = results.FirstOrDefault(r =>
                            r.Name.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            r.Name.IndexOf("low", StringComparison.OrdinalIgnoreCase) < 0)
                        ?? results.First();

                    var mt = masksOut.AsTensor<float>();
                    maskDims = mt.Dimensions.ToArray();
                    maskData = mt.ToArray();

                    var iouOut = results.FirstOrDefault(r =>
                        r.Name.IndexOf("iou", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (iouOut != null) iou = iouOut.AsTensor<float>().ToArray();
                }
            }

            token.ThrowIfCancellationRequested();

            if (maskDims.Length < 2) return null;
            int mCount = maskDims.Length >= 4 ? maskDims[1] : 1;
            int mh = maskDims[maskDims.Length - 2];
            int mw = maskDims[maskDims.Length - 1];
            if (mh <= 1 || mw <= 1) return null;

            int bestMask = 0;
            // Choose the candidate with the highest model-predicted IoU.
            if (iou != null)
                for (int i = 1; i < Math.Min(mCount, iou.Length); i++)
                    if (iou[i] > iou[bestMask]) bestMask = i;

            long offsetL = (long)bestMask * mh * mw;
            if (offsetL + (long)mh * mw > maskData.Length) return null;
            int offset = (int)offsetL;

            // Graph path: the decoder already produced mask logits at the requested output size.
            bool[] resultMask;
            string path;
            if (usedSizeInput && mh == th && mw == tw)
            {
                var atTarget = new bool[tw * th];
                for (int i = 0; i < atTarget.Length; i++)
                    atTarget[i] = maskData[offset + i] > 0f;

                resultMask = (tw == imageWidth && th == imageHeight)
                    ? atTarget
                    : ResizeMaskNearest(atTarget, tw, th, imageWidth, imageHeight);
                path = "graph";
            }
            else
            {
                // Grid path: sample the low-res logits over the full image, then threshold at 0.
                resultMask = MaskFromModelGrid(maskData, offset, mw, mh, state, imageWidth, imageHeight);
                path = "grid";
            }

            int areaPx = 0;
            for (int i = 0; i < resultMask.Length; i++) if (resultMask[i]) areaPx++;

            string iouStr = iou == null ? "n/a"
                : string.Join(",", iou.Take(mCount).Select(v => v.ToString("F2")));

            System.Diagnostics.Debug.WriteLine(
                $"[sam] decode: masks={mCount} pick={bestMask} iou=[{iouStr}] out={mh}x{mw} " +
                $"path={path} area={100.0 * areaPx / resultMask.Length:F1}%");

            return resultMask;
        }

        // =====================
        // Tensor helpers
        // =====================

        private static float ReadChannel(byte[] pixels, int stride, int bpp, int x, int y, int c)
        {
            int i = y * stride + x * bpp;
            if (bpp == 1) return pixels[i];
            return c == 0 ? pixels[i + 2] : c == 1 ? pixels[i + 1] : pixels[i];
        }

        private static float SampleBilinear(byte[] pixels, int stride, int bpp, int w, int h,
            float fx, float fy, int c)
        {
            // Clamp sample coordinates so interpolation stays inside the image bounds.
            if (fx < 0) fx = 0; else if (fx > w - 1) fx = w - 1;
            if (fy < 0) fy = 0; else if (fy > h - 1) fy = h - 1;

            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;

            float v00 = ReadChannel(pixels, stride, bpp, x0, y0, c);
            float v10 = ReadChannel(pixels, stride, bpp, x1, y0, c);
            float v01 = ReadChannel(pixels, stride, bpp, x0, y1, c);
            float v11 = ReadChannel(pixels, stride, bpp, x1, y1, c);

            // Blend the four surrounding pixels using horizontal and vertical weights.
            return v00 * (1 - tx) * (1 - ty) + v10 * tx * (1 - ty)
                 + v01 * (1 - tx) * ty + v11 * tx * ty;
        }

        private static bool[] ResizeMaskNearest(bool[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new bool[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(sh - 1, (int)((long)y * sh / dh));
                int srow = sy * sw, drow = y * dw;
                for (int x = 0; x < dw; x++)
                    dst[drow + x] = src[srow + Math.Min(sw - 1, (int)((long)x * sw / dw))];
            }
            return dst;
        }

        /// <summary>
        /// Samples the model-grid logits over the output image and thresholds at zero.
        /// </summary>
        private static bool[] MaskFromModelGrid(float[] logits, int offset, int mw, int mh,
            SamImageState state, int outW, int outH)
        {
            var result = new bool[outW * outH];
            float gx = state.Scale * mw / InputSize;
            float gy = state.Scale * mh / InputSize;

            for (int y = 0; y < outH; y++)
            {
                float fy = (y + 0.5f) * gy - 0.5f;
                if (fy < 0) fy = 0; else if (fy > mh - 1) fy = mh - 1;

                int y0 = (int)fy, y1 = Math.Min(mh - 1, y0 + 1);
                float ty = fy - y0;
                int row = y * outW;

                for (int x = 0; x < outW; x++)
                {
                    float fx = (x + 0.5f) * gx - 0.5f;
                    if (fx < 0) fx = 0; else if (fx > mw - 1) fx = mw - 1;

                    int x0 = (int)fx, x1 = Math.Min(mw - 1, x0 + 1);
                    float tx = fx - x0;

                    float v00 = logits[offset + y0 * mw + x0];
                    float v10 = logits[offset + y0 * mw + x1];
                    float v01 = logits[offset + y1 * mw + x0];
                    float v11 = logits[offset + y1 * mw + x1];

                    float v = v00 * (1 - tx) * (1 - ty) + v10 * tx * (1 - ty)
                            + v01 * (1 - tx) * ty + v11 * tx * ty;

                    result[row + x] = v > 0f;
                }
            }

            return result;
        }

        public void Dispose()
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
        }
    }
}