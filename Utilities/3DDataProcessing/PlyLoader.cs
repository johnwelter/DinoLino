using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DinoLino   // adjust to your namespace
{
    /// Minimal PLY mesh reader: ASCII, binary_little_endian, and binary_big_endian.
    /// Reads vertex x/y/z and face index lists; all other properties (normals,
    /// colors, extra elements) are read past and ignored.
    public static class PlyLoader
    {
        private class PlyProperty
        {
            public string Name;
            public string Type;        // scalar type, or list item type
            public bool IsList;
            public string CountType;   // list count type
        }

        private class PlyElement
        {
            public string Name;
            public int Count;
            public readonly List<PlyProperty> Props = new List<PlyProperty>();
        }

        public static MeshGeometry3D Load(string path)
        {
            using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);

            // ---------- header ----------
            if (ReadHeaderLine(stream)?.Trim() != "ply")
                throw new InvalidDataException("Not a PLY file (missing 'ply' signature).");

            bool binary = false, bigEndian = false;
            var elements = new List<PlyElement>();
            PlyElement cur = null;

            while (true)
            {
                string line = ReadHeaderLine(stream)
                    ?? throw new InvalidDataException("PLY header ended unexpectedly.");
                var t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                if (t[0] == "end_header") break;

                switch (t[0])
                {
                    case "format":
                        binary = t[1] != "ascii";
                        bigEndian = t[1] == "binary_big_endian";
                        break;
                    case "element":
                        cur = new PlyElement { Name = t[1], Count = int.Parse(t[2], CultureInfo.InvariantCulture) };
                        elements.Add(cur);
                        break;
                    case "property":
                        if (cur == null) throw new InvalidDataException("PLY: 'property' before any 'element'.");
                        cur.Props.Add(t[1] == "list"
                            ? new PlyProperty { IsList = true, CountType = t[2], Type = t[3], Name = t[4] }
                            : new PlyProperty { IsList = false, Type = t[1], Name = t[2] });
                        break;
                        // "comment" / "obj_info": ignored
                }
            }

            // ---------- body ----------
            int vCount = elements.Find(el => el.Name == "vertex")?.Count ?? 0;
            int fCount = elements.Find(el => el.Name == "face")?.Count ?? 0;
            var positions = new Point3DCollection(vCount);
            var indices = new Int32Collection(fCount * 3);

            if (binary)
            {
                using var br = new BinaryReader(stream);
                foreach (var el in elements)
                    ReadElementBinary(br, el, bigEndian, positions, indices);
            }
            else
            {
                using var sr = new StreamReader(stream);
                var tok = sr.ReadToEnd().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                int p = 0;
                foreach (var el in elements)
                    ReadElementAscii(tok, ref p, el, positions, indices);
            }

            var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            mesh.Freeze();   // big performance win; we never mutate the geometry, only transforms
            return mesh;
        }

        private static void ReadElementBinary(BinaryReader br, PlyElement el, bool big,
            Point3DCollection positions, Int32Collection indices)
        {
            bool isVertex = el.Name == "vertex";
            bool isFace = el.Name == "face";
            var face = new List<int>(8);

            for (int i = 0; i < el.Count; i++)
            {
                double x = 0, y = 0, z = 0;
                foreach (var prop in el.Props)
                {
                    if (prop.IsList)
                    {
                        int n = (int)ReadScalarBinary(br, prop.CountType, big);
                        bool wantIdx = isFace && prop.Name.StartsWith("vertex_ind"); // vertex_index / vertex_indices
                        if (wantIdx) face.Clear();
                        for (int k = 0; k < n; k++)
                        {
                            double v = ReadScalarBinary(br, prop.Type, big);
                            if (wantIdx) face.Add((int)v);
                        }
                        if (wantIdx) AddFace(face, indices);
                    }
                    else
                    {
                        double v = ReadScalarBinary(br, prop.Type, big);
                        if (isVertex)
                        {
                            if (prop.Name == "x") x = v;
                            else if (prop.Name == "y") y = v;
                            else if (prop.Name == "z") z = v;
                        }
                    }
                }
                if (isVertex) positions.Add(new Point3D(x, y, z));
            }
        }

        private static void ReadElementAscii(string[] tok, ref int p, PlyElement el,
            Point3DCollection positions, Int32Collection indices)
        {
            bool isVertex = el.Name == "vertex";
            bool isFace = el.Name == "face";
            var face = new List<int>(8);

            for (int i = 0; i < el.Count; i++)
            {
                double x = 0, y = 0, z = 0;
                foreach (var prop in el.Props)
                {
                    if (prop.IsList)
                    {
                        int n = (int)ParseNum(tok[p++]);
                        bool wantIdx = isFace && prop.Name.StartsWith("vertex_ind");
                        if (wantIdx) face.Clear();
                        for (int k = 0; k < n; k++)
                        {
                            double v = ParseNum(tok[p++]);
                            if (wantIdx) face.Add((int)v);
                        }
                        if (wantIdx) AddFace(face, indices);
                    }
                    else
                    {
                        double v = ParseNum(tok[p++]);
                        if (isVertex)
                        {
                            if (prop.Name == "x") x = v;
                            else if (prop.Name == "y") y = v;
                            else if (prop.Name == "z") z = v;
                        }
                    }
                }
                if (isVertex) positions.Add(new Point3D(x, y, z));
            }
        }

        private static void AddFace(List<int> face, Int32Collection indices)
        {
            // Fan-triangulate quads/ngons; triangles pass straight through.
            for (int i = 1; i + 1 < face.Count; i++)
            {
                indices.Add(face[0]);
                indices.Add(face[i]);
                indices.Add(face[i + 1]);
            }
        }

        private static double ParseNum(string s) =>
            double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static double ReadScalarBinary(BinaryReader br, string type, bool big)
        {
            switch (type)
            {
                case "char": case "int8": return unchecked((sbyte)br.ReadByte());
                case "uchar": case "uint8": return br.ReadByte();
                case "short": case "int16": return BitConverter.ToInt16(Bytes(br, 2, big), 0);
                case "ushort": case "uint16": return BitConverter.ToUInt16(Bytes(br, 2, big), 0);
                case "int": case "int32": return BitConverter.ToInt32(Bytes(br, 4, big), 0);
                case "uint": case "uint32": return BitConverter.ToUInt32(Bytes(br, 4, big), 0);
                case "float": case "float32": return BitConverter.ToSingle(Bytes(br, 4, big), 0);
                case "double": case "float64": return BitConverter.ToDouble(Bytes(br, 8, big), 0);
                default: throw new InvalidDataException($"PLY: unknown property type '{type}'.");
            }
        }

        private static byte[] Bytes(BinaryReader br, int n, bool big)
        {
            var b = br.ReadBytes(n);
            if (b.Length != n) throw new EndOfStreamException("PLY: file is truncated.");
            if (big) Array.Reverse(b);
            return b;
        }

        // Reads header lines byte-by-byte so we never over-read into a binary body.
        private static string ReadHeaderLine(Stream s)
        {
            var sb = new StringBuilder(64);
            int b;
            while ((b = s.ReadByte()) != -1)
            {
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}