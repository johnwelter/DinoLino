using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DinoLino.Utilities
{
    public static class PlotExporter
    {
        private const double ExportDpi = 300.0;

        public static void Export(FrameworkElement plot, string path)
        {
            if (plot == null)
                throw new ArgumentNullException(nameof(plot));

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("An export path is required.", nameof(path));

            string extension = Path.GetExtension(path).ToLowerInvariant();

            switch (extension)
            {
                case ".png":
                    SaveBitmap(RenderPlot(plot), path, new PngBitmapEncoder());
                    break;

                case ".jpg":
                case ".jpeg":
                    SaveBitmap(RenderPlot(plot), path, new JpegBitmapEncoder
                    {
                        QualityLevel = 95
                    });
                    break;

                case ".tif":
                case ".tiff":
                    SaveBitmap(RenderPlot(plot), path, new TiffBitmapEncoder());
                    break;

                case ".pdf":
                    SavePdf(RenderPlot(plot), path);
                    break;

                case ".svg":
                    SaveSvg(RenderPlot(plot), path);
                    break;

                default:
                    throw new NotSupportedException(
                        $"Plot export does not support the '{extension}' file type.");
            }
        }

        private static RenderTargetBitmap RenderPlot(FrameworkElement plot)
        {
            plot.UpdateLayout();

            double width = plot.ActualWidth;
            double height = plot.ActualHeight;

            if (width < 1 || height < 1)
                throw new InvalidOperationException("The plot has no visible size.");

            int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * ExportDpi / 96.0));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * ExportDpi / 96.0));

            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                ExportDpi,
                ExportDpi,
                PixelFormats.Pbgra32);

            bitmap.Render(plot);
            return bitmap;
        }

        private static void SaveBitmap(
            RenderTargetBitmap bitmap,
            string path,
            BitmapEncoder encoder)
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        private static byte[] EncodeJpeg(RenderTargetBitmap bitmap)
        {
            var encoder = new JpegBitmapEncoder
            {
                QualityLevel = 95
            };

            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static void SavePdf(RenderTargetBitmap bitmap, string path)
        {
            byte[] jpeg = EncodeJpeg(bitmap);

            double pageWidth = bitmap.PixelWidth * 72.0 / ExportDpi;
            double pageHeight = bitmap.PixelHeight * 72.0 / ExportDpi;

            string content =
                "q\n" +
                F(pageWidth) + " 0 0 " + F(pageHeight) + " 0 0 cm\n" +
                "/PlotImage Do\n" +
                "Q\n";

            byte[] contentBytes = Encoding.ASCII.GetBytes(content);

            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                var offsets = new long[6];

                WriteAscii(writer, "%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");

                offsets[1] = stream.Position;
                WriteAscii(writer,
                    "1 0 obj\n" +
                    "<< /Type /Catalog /Pages 2 0 R >>\n" +
                    "endobj\n");

                offsets[2] = stream.Position;
                WriteAscii(writer,
                    "2 0 obj\n" +
                    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n" +
                    "endobj\n");

                offsets[3] = stream.Position;
                WriteAscii(writer,
                    "3 0 obj\n" +
                    "<< /Type /Page /Parent 2 0 R " +
                    "/MediaBox [0 0 " + F(pageWidth) + " " + F(pageHeight) + "] " +
                    "/Resources << /XObject << /PlotImage 4 0 R >> >> " +
                    "/Contents 5 0 R >>\n" +
                    "endobj\n");

                offsets[4] = stream.Position;
                WriteAscii(writer,
                    "4 0 obj\n" +
                    "<< /Type /XObject /Subtype /Image " +
                    "/Width " + bitmap.PixelWidth + " " +
                    "/Height " + bitmap.PixelHeight + " " +
                    "/ColorSpace /DeviceRGB " +
                    "/BitsPerComponent 8 " +
                    "/Filter /DCTDecode " +
                    "/Length " + jpeg.Length + " >>\n" +
                    "stream\n");

                writer.Write(jpeg);
                WriteAscii(writer, "\nendstream\nendobj\n");

                offsets[5] = stream.Position;
                WriteAscii(writer,
                    "5 0 obj\n" +
                    "<< /Length " + contentBytes.Length + " >>\n" +
                    "stream\n");

                writer.Write(contentBytes);
                WriteAscii(writer, "endstream\nendobj\n");

                long xrefOffset = stream.Position;

                WriteAscii(writer, "xref\n0 6\n");
                WriteAscii(writer, "0000000000 65535 f \n");

                for (int i = 1; i <= 5; i++)
                    WriteAscii(writer, offsets[i].ToString("D10") + " 00000 n \n");

                WriteAscii(writer,
                    "trailer\n" +
                    "<< /Size 6 /Root 1 0 R >>\n" +
                    "startxref\n" +
                    xrefOffset + "\n" +
                    "%%EOF");
            }
        }

        private static void SaveSvg(RenderTargetBitmap bitmap, string path)
        {
            byte[] png;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                png = stream.ToArray();
            }

            string imageData = Convert.ToBase64String(png);

            string svg =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                "xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                $"width=\"{bitmap.PixelWidth}\" height=\"{bitmap.PixelHeight}\" " +
                $"viewBox=\"0 0 {bitmap.PixelWidth} {bitmap.PixelHeight}\">\n" +
                $"  <image width=\"{bitmap.PixelWidth}\" height=\"{bitmap.PixelHeight}\" " +
                $"xlink:href=\"data:image/png;base64,{imageData}\" />\n" +
                "</svg>\n";

            File.WriteAllText(path, svg, Encoding.UTF8);
        }

        private static string F(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        private static void WriteAscii(BinaryWriter writer, string text) =>
            writer.Write(Encoding.ASCII.GetBytes(text));
    }
}