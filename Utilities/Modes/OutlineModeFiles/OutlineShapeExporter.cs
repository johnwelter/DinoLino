using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// System.IO and System.Windows.Shapes both define Path, and this file needs Polyline
// from Shapes, so the file-handling one is aliased.
using IOPath = System.IO.Path;

namespace DinoLino.Utilities
{
    /// <summary>Image formats offered by the 2D outline export.</summary>
    public enum OutlineImageFormat
    {
        Png,
        Tiff,
        Bmp,
        Jpeg,
        Svg
    }

    /// <summary>Settings chosen in the 2D outline export dialog.</summary>
    public class OutlineExportOptions
    {
        public string Folder { get; set; }
        public OutlineImageFormat Format { get; set; } = OutlineImageFormat.Png;

        /// <summary>Width and height, in pixels, of every exported canvas.</summary>
        public int CanvasSize { get; set; } = 512;

        /// When true, every silhouette is resized to the same area, so shapes can be
        /// compared with size taken out of the picture. When false — the default —
        /// the outlines keep the sizes they were traced at, and a larger specimen
        /// exports larger.
        public bool ScaleToCommonArea { get; set; } = false;

        /// Fraction of the canvas each shape should cover WHEN ScaleToCommonArea is
        /// on; ignored otherwise. Standardizing on area means shapes of different
        /// proportions still read as the same visual "size".
        /// 0.25 keeps the area exact for outlines up to roughly 2.5:1; past that the
        /// fit-to-canvas clamp below takes over and the shape comes out smaller.
        /// Raising this uses more of the canvas but starts clamping sooner.
        public double TargetAreaFraction { get; set; } = 0.25;

        /// <summary>Smallest gap kept between the shape and the canvas edge, in pixels.</summary>
        public double Margin { get; set; } = 16;

        /// When true, each shape is rotated onto its principal axis so specimens are
        /// compared in a common orientation. This is a rigid transform of the traced
        /// points: position, size, and rotation change, and no detail is lost.
        public bool AlignRotation { get; set; } = true;
    }

    /// <summary>What the current history can produce, used to set up the export dialog.</summary>
    public class OutlineExportAvailability
    {
        /// <summary>Outlines with usable traced geometry.</summary>
        public int TracedCount { get; set; }

        /// Outlines too close to circular for a principal axis to be meaningful, whose
        /// aligned rotation is therefore arbitrary.
        public int UnstableRotationCount { get; set; }
    }

    /// <summary>
    /// Exports committed outlines as silhouettes: a black filled shape centred on a
    /// white square canvas, standardized to a common area only when asked for.
    /// </summary>
    public static class OutlineShapeExporter
    {
        /// <summary>One outline pulled out of the operation history.</summary>
        private sealed class OutlineShape
        {
            public string SpecimenName;
            public int Attempt;             // 1-based index within that specimen
            public List<Point> Points;      // canvas space, closure point stripped
        }

        /// One outline centred on the origin and optionally rotated onto its principal
        /// axis, still at its traced size. Scaling happens afterwards, because whether
        /// the factor is per-shape or shared across the batch depends on the option.
        private sealed class PreparedShape
        {
            public OutlineShape Source;
            public List<Point> Points;      // centred on the origin, possibly rotated
            public double Area;             // enclosed area; centring and rotation do not change it
            public double HalfExtent;       // furthest reach from the origin on either axis
        }

        /// <summary>One specimen's operations, paired with the name to label it by.</summary>
        private sealed class SpecimenBlock
        {
            public string Name;
            public IReadOnlyList<WorkOperation> Operations;
        }

        /// Every specimen's operations in order: the archived records followed by the
        /// live history, which holds the active specimen. Walked locally rather than
        /// borrowed from GeomOpHistoryWindow so this exporter stands on its own.
        private static IEnumerable<SpecimenBlock> SpecimenBlocks(
            UndoRedoManager undoRedo, string currentSpecimenName)
        {
            if (undoRedo == null) yield break;

            foreach (var record in undoRedo.Archive)
            {
                yield return new SpecimenBlock
                {
                    Name = record.SpecimenName,
                    Operations = record.Operations
                };
            }

            yield return new SpecimenBlock
            {
                Name = currentSpecimenName,
                Operations = undoRedo.History
            };
        }

        // =====================
        // Entry point
        // =====================

        /// <summary>
        /// Writes one image per committed outline into the chosen folder.
        /// Returns the number of files written.
        /// </summary>
        public static int ExportAll(
            UndoRedoManager undoRedo, string currentSpecimenName, OutlineExportOptions options)
        {
            if (undoRedo == null || options == null) return 0;

            var shapes = CollectShapes(undoRedo, currentSpecimenName, options);
            if (shapes.Count == 0) return 0;

            // Centre and orient everything first: an unscaled export has to see the
            // whole batch before it can decide how large the canvas lets it draw.
            var prepared = new List<PreparedShape>(shapes.Count);
            foreach (var shape in shapes)
            {
                PreparedShape p = Prepare(shape, options);
                if (p != null) prepared.Add(p);   // degenerate outlines drop out here
            }

            if (prepared.Count == 0) return 0;

            double batchScale = options.ScaleToCommonArea ? 0 : CommonScale(prepared, options);

            int written = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PreparedShape p in prepared)
            {
                double scale = options.ScaleToCommonArea ? AreaScale(p, options) : batchScale;
                List<Point> placed = PlaceOnCanvas(p, scale, options.CanvasSize);

                string fileName = UniqueFileName(p.Source, options, usedNames);
                string path = IOPath.Combine(options.Folder, fileName);

                if (options.Format == OutlineImageFormat.Svg)
                    WriteSvg(path, placed, options.CanvasSize);
                else
                    WriteRaster(path, placed, options.CanvasSize, options.Format);

                written++;
            }

            return written;
        }

        /// Reports what the current history can export, so the dialog can size its
        /// preview text and warn about shapes whose orientation cannot be pinned down.
        public static OutlineExportAvailability Survey(
            UndoRedoManager undoRedo, string currentSpecimenName)
        {
            var result = new OutlineExportAvailability();
            if (undoRedo == null) return result;

            foreach (SpecimenBlock block in SpecimenBlocks(undoRedo, currentSpecimenName))
            {
                foreach (var op in block.Operations.OfType<OutlineOperation>())
                {
                    var pts = BuildFromTracedPoints(op);
                    if (pts == null) continue;

                    result.TracedCount++;

                    if (!HasStableOrientation(pts))
                        result.UnstableRotationCount++;
                }
            }

            return result;
        }

        // =====================
        // Collection
        // =====================

        /// <summary>Pulls one shape per committed outline from its traced polyline.</summary>
        private static List<OutlineShape> CollectShapes(
            UndoRedoManager undoRedo, string currentSpecimenName, OutlineExportOptions options)
        {
            var shapes = new List<OutlineShape>();

            foreach (SpecimenBlock block in SpecimenBlocks(undoRedo, currentSpecimenName))
            {
                int attempt = 0;
                foreach (var op in block.Operations.OfType<OutlineOperation>())
                {
                    List<Point> pts = BuildFromTracedPoints(op);

                    if (pts == null) continue;

                    attempt++;
                    shapes.Add(new OutlineShape
                    {
                        SpecimenName = block.Name,
                        Attempt = attempt,
                        Points = pts
                    });
                }
            }

            return shapes;
        }

        /// The polyline stored with each operation is the live shape, so erase and
        /// smooth edits are already reflected here.
        private static List<Point> BuildFromTracedPoints(OutlineOperation op)
        {
            var polyline = op.Elements?.OfType<Polyline>().FirstOrDefault();
            if (polyline == null || polyline.Points.Count < 3) return null;

            var pts = new List<Point>(polyline.Points);
            PolylineGeometry.StripClosureDuplicate(pts);
            return pts.Count >= 3 ? pts : null;
        }

        /// True when the outline is elongated enough for its principal axis to be a
        /// meaningful orientation. Near-circular shapes have no dominant axis, so any
        /// rotation applied to them would be arbitrary.
        private static bool HasStableOrientation(List<Point> pts)
        {
            double angle;
            double axisRatio;
            if (!TryGetPrincipalAxis(pts, out angle, out axisRatio)) return false;
            return axisRatio <= OrientationStabilityLimit;
        }

        /// Ratio of the two principal moments above which orientation is treated as
        /// unreliable. At 1.0 the shape is rotationally symmetric.
        private const double OrientationStabilityLimit = 0.90;

        /// Finds the angle of the shape's principal (long) axis using exact
        /// area-weighted polygon moments.
        ///
        /// Moments are integrated over the enclosed area with Green's theorem rather
        /// than averaged over the vertices, because outline simplification leaves
        /// vertices unevenly spaced: a densely sampled stretch of boundary would
        /// otherwise pull the axis toward itself.
        private static bool TryGetPrincipalAxis(List<Point> pts, out double angle, out double axisRatio)
        {
            angle = 0;
            axisRatio = 1.0;

            int n = pts.Count;
            if (n < 3) return false;

            double a2 = 0, cx = 0, cy = 0, sxx = 0, syy = 0, sxy = 0;

            for (int i = 0; i < n; i++)
            {
                Point p0 = pts[i];
                Point p1 = pts[(i + 1) % n];
                double cross = p0.X * p1.Y - p1.X * p0.Y;

                a2 += cross;
                cx += (p0.X + p1.X) * cross;
                cy += (p0.Y + p1.Y) * cross;

                syy += (p0.Y * p0.Y + p0.Y * p1.Y + p1.Y * p1.Y) * cross;
                sxx += (p0.X * p0.X + p0.X * p1.X + p1.X * p1.X) * cross;
                sxy += (p0.X * p1.Y + 2 * p0.X * p0.Y + 2 * p1.X * p1.Y + p1.X * p0.Y) * cross;
            }

            double area = a2 / 2.0;
            if (Math.Abs(area) < 1e-9) return false;

            cx /= 6 * area;
            cy /= 6 * area;

            // Shift the second moments onto the centroid.
            double mxx = sxx / 12.0 - area * cx * cx;   // integral of x^2 over the area
            double myy = syy / 12.0 - area * cy * cy;   // integral of y^2
            double mxy = sxy / 24.0 - area * cx * cy;   // integral of x*y

            angle = 0.5 * Math.Atan2(2 * mxy, mxx - myy);

            // Eigenvalues of the moment matrix give the spread along each principal
            // axis; their ratio says how elongated the shape is.
            double trace = mxx + myy;
            double det = mxx * myy - mxy * mxy;
            double disc = Math.Sqrt(Math.Max(0.0, trace * trace / 4.0 - det));
            double major = trace / 2.0 + disc;
            double minor = trace / 2.0 - disc;

            axisRatio = major > 1e-12 ? Math.Max(0.0, minor) / major : 1.0;
            return true;
        }

        /// Rotates the shape (already centred on the origin) so its long axis lies on
        /// the X axis.
        private static void ApplyRotation(List<Point> centred, double angle)
        {
            double ct = Math.Cos(-angle), st = Math.Sin(-angle);

            for (int i = 0; i < centred.Count; i++)
            {
                Point p = centred[i];
                centred[i] = new Point(p.X * ct - p.Y * st, p.X * st + p.Y * ct);
            }
        }

        /// Resolves the 180-degree ambiguity left by the principal axis: the axis is a
        /// line, so two opposite orientations fit it equally well and otherwise
        /// identical specimens could come out upside down relative to each other.
        ///
        /// The choice is made on the sign of a third moment, which is smooth and
        /// deterministic, unlike picking the farthest vertex, which flickers between
        /// near-equal candidates. The boundary is resampled to even spacing first so
        /// vertex density cannot influence the sign.
        private static void ResolveFlip(List<Point> centred)
        {
            var uniform = PolylineGeometry.ResampleClosedUniformSpacing(
                new List<Point>(centred), targetSpacing: EstimateFlipSampleSpacing(centred));

            var basis = (uniform != null && uniform.Count >= 3) ? uniform : centred;

            double sx = 0, sy = 0;
            foreach (var p in basis)
            {
                sx += p.X * p.X * p.X;
                sy += p.Y * p.Y * p.Y;
            }

            // Use whichever axis carries the stronger asymmetry; a shape symmetric on
            // one axis gives no usable sign there.
            double key = Math.Abs(sx) >= Math.Abs(sy) ? sx : sy;
            if (key >= 0) return;

            for (int i = 0; i < centred.Count; i++)
                centred[i] = new Point(-centred[i].X, -centred[i].Y);
        }

        /// Spacing that yields a few hundred samples around the outline, enough for a
        /// stable sign without doing unnecessary work on large shapes.
        private static double EstimateFlipSampleSpacing(List<Point> pts)
        {
            double perimeter = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % pts.Count];
                perimeter += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            }

            return perimeter > 0 ? Math.Max(0.5, perimeter / 512.0) : 1.0;
        }

        // =====================
        // Normalization
        // =====================

        /// Centres one outline on its centroid and, when asked, rotates it onto its
        /// principal axis — every step a rigid transform of the traced points, so no
        /// shape detail is altered. Size is left alone here. Returns null for a
        /// degenerate outline.
        private static PreparedShape Prepare(OutlineShape shape, OutlineExportOptions options)
        {
            // One shoelace pass yields both the enclosed area and the centroid.
            double signedArea;
            Point centroid = GetAreaAndCentroid(shape.Points, out signedArea);
            double area = Math.Abs(signedArea);
            if (area < 1e-6) return null;

            // Work on a centroid-origin copy so rotation and scaling are about the
            // point the shape will be centred on.
            var work = new List<Point>(shape.Points.Count);
            foreach (var p in shape.Points)
                work.Add(new Point(p.X - centroid.X, p.Y - centroid.Y));

            if (options.AlignRotation)
            {
                double angle;
                double axisRatio;
                if (TryGetPrincipalAxis(work, out angle, out axisRatio)
                    && axisRatio <= OrientationStabilityLimit)
                {
                    ApplyRotation(work, angle);
                    ResolveFlip(work);
                }

                // A near-circular outline is left as traced: it has no dominant axis,
                // so rotating it would impose an arbitrary orientation.
            }

            // Measured after rotation, since turning the shape changes how far it
            // reaches along each axis. From the centroid rather than the bounding-box
            // centre, because the centroid is the point the shape will be centred on:
            // a concave shape reaches further on one side, and measuring the box
            // instead would let it overflow.
            double halfExtent = 0;
            foreach (var p in work)
            {
                double dx = Math.Abs(p.X);
                double dy = Math.Abs(p.Y);
                if (dx > halfExtent) halfExtent = dx;
                if (dy > halfExtent) halfExtent = dy;
            }

            return new PreparedShape
            {
                Source = shape,
                Points = work,
                Area = area,
                HalfExtent = halfExtent
            };
        }

        /// Factor for one shape when every silhouette is standardized to the same
        /// area. Area scales with the square of a length, so the linear factor is the
        /// root; rotation does not change area, so this is unaffected by alignment.
        private static double AreaScale(PreparedShape prepared, OutlineExportOptions options)
        {
            double size = options.CanvasSize;
            double targetArea = size * size * options.TargetAreaFraction;
            double scale = Math.Sqrt(targetArea / prepared.Area);

            // Area standardization alone can push an elongated shape off the canvas.
            // A clamped shape ends up under the target area, which is the intended
            // trade: the whole outline stays visible.
            double usableHalf = UsableHalf(options);
            if (usableHalf > 0.5 && prepared.HalfExtent * scale > usableHalf)
                scale = usableHalf / prepared.HalfExtent;

            return scale;
        }

        /// ONE factor for the whole batch when the shapes are exported at their traced
        /// sizes. Normally 1, leaving them untouched; when the largest outline would
        /// run off the canvas every outline shrinks by the same amount, so nothing is
        /// cut off and their sizes stay comparable with each other.
        private static double CommonScale(List<PreparedShape> prepared, OutlineExportOptions options)
        {
            double largest = 0;
            foreach (PreparedShape p in prepared)
                if (p.HalfExtent > largest) largest = p.HalfExtent;

            double usableHalf = UsableHalf(options);
            if (largest <= 1e-9 || usableHalf <= 0.5) return 1.0;

            return largest > usableHalf ? usableHalf / largest : 1.0;
        }

        /// <summary>Half-width of the canvas once the margins are taken off.</summary>
        private static double UsableHalf(OutlineExportOptions options) =>
            (options.CanvasSize - 2 * options.Margin) / 2.0;

        /// <summary>Scales a centred shape and moves it onto the middle of the canvas.</summary>
        private static List<Point> PlaceOnCanvas(PreparedShape prepared, double scale, int canvasSize)
        {
            double half = canvasSize / 2.0;

            var result = new List<Point>(prepared.Points.Count);
            foreach (var p in prepared.Points)
                result.Add(new Point(p.X * scale + half, p.Y * scale + half));

            return result;
        }

        /// Shoelace pass returning the polygon's area-weighted centroid, with the signed
        /// area as an out parameter. A near-zero area means the outline is degenerate.
        private static Point GetAreaAndCentroid(List<Point> pts, out double signedArea)
        {
            double twiceArea = 0, cx = 0, cy = 0;
            int n = pts.Count;

            for (int i = 0; i < n; i++)
            {
                Point a = pts[i];
                Point b = pts[(i + 1) % n];
                double cross = a.X * b.Y - b.X * a.Y;
                twiceArea += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }

            signedArea = twiceArea / 2.0;

            if (Math.Abs(twiceArea) < 1e-9)
                return new Point();

            double factor = 1.0 / (3.0 * twiceArea);
            return new Point(cx * factor, cy * factor);
        }

        // =====================
        // Raster output
        // =====================

        private static void WriteRaster(string path, List<Point> pts, int size, OutlineImageFormat format)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // White background fills the whole canvas, including under the shape.
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
                dc.DrawGeometry(Brushes.Black, null, BuildGeometry(pts));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            BitmapEncoder encoder = format switch
            {
                OutlineImageFormat.Tiff => new TiffBitmapEncoder(),
                OutlineImageFormat.Bmp => new BmpBitmapEncoder(),
                OutlineImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = 95 },
                _ => new PngBitmapEncoder()
            };

            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(stream);
        }

        /// <summary>Builds a filled closed geometry from the normalized points.</summary>
        private static Geometry BuildGeometry(List<Point> pts)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };

            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(pts[0], isFilled: true, isClosed: true);
                ctx.PolyLineTo(pts.Skip(1).ToList(), isStroked: false, isSmoothJoin: false);
            }

            geometry.Freeze();
            return geometry;
        }

        // =====================
        // Vector output
        // =====================

        /// Writes the same normalized shape as SVG, so it stays editable and
        /// resolution-independent downstream.
        private static void WriteSvg(string path, List<Point> pts, int size)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" " +
                          $"viewBox=\"0 0 {size} {size}\">");
            sb.AppendLine($"  <rect width=\"{size}\" height=\"{size}\" fill=\"#FFFFFF\"/>");

            sb.Append("  <polygon fill=\"#000000\" points=\"");
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(pts[i].X.ToString("0.###", ci)).Append(',').Append(pts[i].Y.ToString("0.###", ci));
            }
            sb.AppendLine("\"/>");

            sb.AppendLine("</svg>");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // =====================
        // File naming
        // =====================

        private static string Extension(OutlineImageFormat format) => format switch
        {
            OutlineImageFormat.Tiff => ".tif",
            OutlineImageFormat.Bmp => ".bmp",
            OutlineImageFormat.Jpeg => ".jpg",
            OutlineImageFormat.Svg => ".svg",
            _ => ".png"
        };

        /// Builds a filesystem-safe name, suffixing the attempt number when a specimen
        /// holds more than one outline and disambiguating any remaining collisions.
        /// EFA-aligned exports are tagged so they can sit beside traced ones.
        private static string UniqueFileName(
            OutlineShape shape, OutlineExportOptions options, HashSet<string> used)
        {
            string baseName = Sanitize(shape.SpecimenName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "specimen";

            string kind = options.AlignRotation ? "_aligned" : "";
            string stem = shape.Attempt > 1
                ? $"{baseName}_outline{shape.Attempt}{kind}"
                : $"{baseName}_outline{kind}";

            string ext = Extension(options.Format);

            string candidate = stem + ext;
            int suffix = 2;
            while (used.Contains(candidate))
                candidate = $"{stem}_{suffix++}{ext}";

            used.Add(candidate);
            return candidate;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            var invalid = IOPath.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);

            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            return sb.ToString().Trim();
        }
    }
}