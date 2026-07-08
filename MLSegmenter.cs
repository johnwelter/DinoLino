using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Per-image output of the SAM image encoder, plus the geometry needed to
    /// map click coordinates into the model frame. Immutable after creation —
    /// safe to share read-only across concurrent click operations, exactly
    /// like the other per-image caches in OutlineMode.ImageAnalysis.
    /// </summary>
    internal sealed class SamImageState
    {
        public DenseTensor<float> Embedding;
        public int Width;    // original image size
        public int Height;
        public float Scale;  // original → model frame (long side = SamSegmenter input size)
    }

    /// <summary>
    /// Click-prompted neural segmentation via ONNX Runtime (MobileSAM /
    /// EfficientSAM / SAM-class models).
    ///
    /// DEPLOYMENT: drop TWO .onnx files into a "Models" folder beside the
    /// executable — the image encoder (filename containing "encoder", e.g.
    /// mobile_sam_encoder.onnx) and the prompt/mask decoder (any other .onnx,
    /// e.g. mobile_sam.onnx). No settings, no UI: if the files are present and
    /// load, the neural candidate runs; if not, <see cref="Shared"/> is null
    /// and the classical pipeline runs exactly as before. Loading never throws
    /// past this class.
    ///
    /// MODEL CONTRACT: targets the standard segment-anything / MobileSAM ONNX
    /// exports —
    ///  * decoder from scripts/export_onnx_model.py, inputs image_embeddings,
    ///    point_coords, point_labels, mask_input, has_mask_input, orig_im_size;
    ///    outputs masks (logits, resized in-graph to orig_im_size) and
    ///    iou_predictions. Input/output names are resolved by substring so
    ///    minor export variations still bind; mask_input / has_mask_input /
    ///    orig_im_size are optional (a trimmed export without orig_im_size
    ///    falls back to the 256×256 low-res grid path below).
    ///  * encoder taking the longest-side-1024 resized image. NCHW
    ///    [1,3,1024,1024] is the primary layout; NHWC and HWC are detected
    ///    from the input metadata and handled. SAM-standard normalization
    ///    (mean/std below) is applied HERE — set NormalizeEncoderInput to
    ///    false if your particular export embeds normalization in the graph
    ///    (symptom: garbage masks everywhere with a correctly loading model).
    ///
    /// THREADING: EncodeImage runs once per image on the analysis worker;
    /// Segment runs on click workers and is serialized by an internal lock —
    /// decodes are tens of milliseconds, so serialization is cheaper than
    /// reasoning about concurrent Run() calls sharing one embedding tensor.
    /// </summary>
    internal sealed class SamSegmenter : IDisposable
    {
        private const int InputSize = 1024;
        // SEED value only: the constructor CALIBRATES the correct mode against
        // a synthetic test image and overrides this (see CalibrateNormalization),
        // so exports that embed preprocessing in the graph and exports that
        // expect SAM-normalized input both work without editing code. Kept as
        // the fallback when calibration itself fails.
        private const bool NormalizeEncoderInput = false;
        private static readonly float[] PixelMean = { 123.675f, 116.28f, 103.53f }; // RGB, SAM standard
        private static readonly float[] PixelStd = { 58.395f, 57.12f, 57.375f };

        // Cap for the in-graph mask resize: beyond this the graph would
        // materialize several full-resolution float mask planes (12 MP × 4
        // masks × 4 bytes ≈ 190 MB per click); we resize the remainder here.
        private const int MaxDecodeSide = 2048;

        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private readonly string _encInputName;
        private readonly int[] _encInputDims;
        private readonly string _decEmbedName;
        private readonly string _decCoordsName;
        private readonly string _decLabelsName;
        private readonly string _decMaskName;     // optional
        private readonly string _decHasMaskName;  // optional
        private readonly string _decSizeName;     // optional
        private readonly object _decodeLock = new object();
        private bool _normalize = NormalizeEncoderInput; // resolved by CalibrateNormalization

        // =====================
        // SINGLETON
        // =====================
        private static readonly object InitLock = new object();
        private static bool _initTried;
        private static SamSegmenter _shared;

        /// <summary>
        /// Lazily loads models from "&lt;exe dir&gt;\Models" (falling back to the
        /// exe directory itself). Null when no usable models are found — the
        /// caller treats that as "feature not installed" and runs the
        /// classical pipeline alone.
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
        /// Attempts to load an encoder/decoder pair from the directory.
        /// Encoder = first *.onnx whose name contains "encoder"; decoder = the
        /// first other *.onnx (preferring names containing "decoder").
        /// Returns null — never throws — on any failure.
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
                    "decoder inputs not recognized — expected the official SAM ONNX export contract " +
                    "(image_embeddings / point_coords / point_labels)");

            // Contract dump: one glance at the Output window now answers every
            // "which export flavor is this?" question.
            System.Diagnostics.Debug.WriteLine(
                $"[sam] encoder input '{_encInputName}' dims=[{string.Join(",", _encInputDims)}]");
            System.Diagnostics.Debug.WriteLine(
                $"[sam] decoder inputs: {string.Join(", ", _decoder.InputMetadata.Keys)}");
            System.Diagnostics.Debug.WriteLine(
                $"[sam] decoder outputs: {string.Join(", ", _decoder.OutputMetadata.Keys)}");

            try { CalibrateNormalization(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[sam] calibration failed: {ex.Message} — using default normalize={_normalize}");
            }
        }

        /// <summary>
        /// Settles the encoder-normalization question empirically instead of by
        /// configuration: encodes a synthetic scene (a colored square on a
        /// contrasting background) once with SAM normalization and once with
        /// raw 0–255 input, decodes a center-click for each, and keeps the mode
        /// whose mask best matches the known square. Runs once at model load
        /// (~2 extra encoder passes, hidden inside the first image's async
        /// analysis). Deterministic — no randomness.
        /// </summary>
        private void CalibrateNormalization()
        {
            const int S = 512;
            const int lo = 160, hi = 352; // square occupies [lo, hi) in both axes

            var pixels = new byte[S * S * 4];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int i = (y * S + x) * 4;
                    bool inSquare = x >= lo && x < hi && y >= lo && y < hi;
                    // BGRA: bluish-gray background, reddish square — differs in
                    // both luminance and chroma so either failure mode shows.
                    pixels[i] = inSquare ? (byte)60 : (byte)140;      // B
                    pixels[i + 1] = inSquare ? (byte)90 : (byte)120;  // G
                    pixels[i + 2] = inSquare ? (byte)200 : (byte)110; // R
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
        // ENCODE (once per image)
        // =====================
        internal SamImageState EncodeImage(byte[] pixels, int w, int h, int stride, int bpp)
        {
            float scale = (float)InputSize / Math.Max(w, h);
            int rw = Math.Max(1, (int)Math.Round(w * scale));
            int rh = Math.Max(1, (int)Math.Round(h * scale));

            // Resolve layout from the encoder's input metadata; dynamic (-1)
            // dims fall through to the official NCHW layout.
            var d = _encInputDims;
            bool nchw = d.Length == 4 && d[1] == 3;
            bool nhwc = d.Length == 4 && d[3] == 3;
            bool hwc = d.Length == 3 && d[2] == 3;
            if (!nchw && !nhwc && !hwc) nchw = true;

            DenseTensor<float> input =
                nchw ? new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize }) :
                nhwc ? new DenseTensor<float>(new[] { 1, InputSize, InputSize, 3 }) :
                       new DenseTensor<float>(new[] { InputSize, InputSize, 3 });

            // Bilinear resize into RGB float with SAM normalization. Padding
            // beyond (rw, rh) stays 0, matching SAM's preprocess, which pads
            // with zeros AFTER normalization.
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
                // Copy out — the result collection is disposed with this scope,
                // but the embedding must live for the image's whole lifetime.
                var copy = new DenseTensor<float>(emb.ToArray(), emb.Dimensions.ToArray());
                return new SamImageState { Embedding = copy, Width = w, Height = h, Scale = scale };
            }
        }

        // =====================
        // DECODE (per click)
        // =====================
        /// <summary>
        /// Runs the prompt decoder with the given click points (positive =
        /// "this is the object", negative = "this is not") and returns a
        /// full-image boolean mask, or null when it cannot produce one. The
        /// best of the model's candidate masks is chosen by its own predicted
        /// IoU; the caller's ScoreMask/size-veto arbitration then judges it
        /// against the classical candidates like any other.
        /// </summary>
        internal bool[] Segment(SamImageState state, IReadOnlyList<(int x, int y, bool positive)> points,
            int imageWidth, int imageHeight, CancellationToken token)
        {
            if (state == null || points == null || points.Count == 0) return null;
            token.ThrowIfCancellationRequested();

            // Point prompts in the model frame, plus the (0,0) / label -1
            // padding point the official export was traced with.
            int n = points.Count;
            var coords = new DenseTensor<float>(new[] { 1, n + 1, 2 });
            var labels = new DenseTensor<float>(new[] { 1, n + 1 });
            for (int i = 0; i < n; i++)
            {
                coords[0, i, 0] = points[i].x * state.Scale;
                coords[0, i, 1] = points[i].y * state.Scale;
                labels[0, i] = points[i].positive ? 1f : 0f;
            }
            labels[0, n] = -1f; // padding point; coords default to (0,0)

            // Cap the in-graph resize target for very large images; the
            // remaining upsample happens here.
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
            if (iou != null)
                for (int i = 1; i < Math.Min(mCount, iou.Length); i++)
                    if (iou[i] > iou[bestMask]) bestMask = i;
            long offsetL = (long)bestMask * mh * mw;
            if (offsetL + (long)mh * mw > maskData.Length) return null;
            int offset = (int)offsetL;

            // Two post-map paths:
            //  (a) the export took orig_im_size — logits arrive at (th, tw);
            //      threshold > 0, then nearest-upsample any capped remainder;
            //  (b) no size input — logits are the low-res grid over the padded
            //      model square; bilinear-sample the LOGITS per output pixel
            //      (far cleaner edges than nearest on a 256 grid), then
            //      threshold.
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
                resultMask = MaskFromModelGrid(maskData, offset, mw, mh, state, imageWidth, imageHeight);
                path = "grid";
            }

            // Per-decode telemetry: the model's own confidence for EVERY
            // candidate mask, which one was taken, the raw output geometry,
            // and the final area — enough to tell a confident-but-mispicked
            // part from a genuinely degraded embedding at a glance.
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
        // HELPERS
        // =====================
        private static float ReadChannel(byte[] pixels, int stride, int bpp, int x, int y, int c)
        {
            int i = y * stride + x * bpp;
            if (bpp == 1) return pixels[i];
            return c == 0 ? pixels[i + 2] : c == 1 ? pixels[i + 1] : pixels[i]; // BGRA source, RGB out
        }

        private static float SampleBilinear(byte[] pixels, int stride, int bpp, int w, int h,
            float fx, float fy, int c)
        {
            if (fx < 0) fx = 0; else if (fx > w - 1) fx = w - 1;
            if (fy < 0) fy = 0; else if (fy > h - 1) fy = h - 1;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            float v00 = ReadChannel(pixels, stride, bpp, x0, y0, c);
            float v10 = ReadChannel(pixels, stride, bpp, x1, y0, c);
            float v01 = ReadChannel(pixels, stride, bpp, x0, y1, c);
            float v11 = ReadChannel(pixels, stride, bpp, x1, y1, c);
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

        // The low-res mask grid covers the PADDED model square (InputSize²)
        // at (mw × mh); map each full-resolution pixel into that grid and
        // bilinear-sample the logits, thresholding at 0.
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