using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;    // Dictionary
using System.Windows.Media;          // Int32Collection
using System.Windows.Media.Media3D;  // Point3D, Point3DCollection, MeshGeometry3D

namespace DinoLino
{
    /// Minimal STL mesh reader supporting both binary and ASCII STL.
    ///
    /// STL stores every triangle independently, repeating shared vertices, so a naive
    /// read produces 3 vertices per triangle (a 1M-triangle scan becomes 3M vertices).
    /// This loader WELDS vertices during the read: coordinates that are bit-identical
    /// (the normal case for shared vertices in well-formed STL) collapse to one entry,
    /// typically cutting vertex count ~6x. Degenerate (zero-area) triangles whose
    /// welded corners coincide are dropped. Per-facet normals are ignored (WPF derives
    /// its own, same as PlyLoader).
    public static class StlLoader
    {
        public static MeshGeometry3D Load(string path)
        {
            var mesh = IsBinary(path) ? ReadBinary(path) : ReadAscii(path);
            mesh.Freeze();
            return mesh;
        }

        // Robust binary-vs-ASCII detection: a binary STL's length is exactly
        // 84 + 50*triangleCount (80-byte header + 4-byte count + 50 bytes/triangle).
        // The "solid" prefix is unreliable because binary headers may also contain it.
        private static bool IsBinary(string path)
        {
            long len = new FileInfo(path).Length;
            if (len < 84) return false; // too short to be binary; treat as ASCII

            using var br = new BinaryReader(File.OpenRead(path));
            br.BaseStream.Seek(80, SeekOrigin.Begin);
            uint tris = br.ReadUInt32(); // STL binary is always little-endian
            return len == 84L + 50L * tris;
        }

        private static MeshGeometry3D ReadBinary(string path)
        {
            // BufferedStream: BinaryReader otherwise issues many tiny reads on the raw
            // FileStream, which is slow for multi-hundred-MB scans.
            using var br = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
            br.ReadBytes(80);            // header, ignored
            uint tris = br.ReadUInt32();

            // Capacity hints only (collections grow past them fine). A closed manifold
            // has ~tris/2 unique vertices, so `tris` leaves comfortable headroom.
            int posCap = (int)Math.Min((long)tris, 6_000_000L);
            int idxCap = (int)Math.Min(tris * 3L, 18_000_000L);

            var vertexIndex = new Dictionary<(float, float, float), int>(posCap);
            var positions = new Point3DCollection(posCap);
            var indices = new Int32Collection(idxCap);

            // Weld on the raw float triple: shared vertices in well-formed STL are
            // bit-identical, so exact equality merges them with no tolerance needed.
            int Weld(float x, float y, float z)
            {
                var key = (x, y, z);
                if (!vertexIndex.TryGetValue(key, out int i))
                {
                    i = positions.Count;
                    vertexIndex.Add(key, i);
                    positions.Add(new Point3D(x, y, z));
                }
                return i;
            }

            for (uint t = 0; t < tris; t++)
            {
                // Per-facet normal (3 floats): read-and-discard singles rather than
                // ReadBytes(12), which would allocate a fresh array per triangle.
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle();

                int i0 = Weld(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                int i1 = Weld(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                int i2 = Weld(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                br.ReadUInt16();         // attribute byte count, ignored

                // Skip degenerate slivers whose welded corners coincide.
                if (i0 != i1 && i1 != i2 && i0 != i2)
                {
                    indices.Add(i0);
                    indices.Add(i1);
                    indices.Add(i2);
                }
            }

            return new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        }

        private static MeshGeometry3D ReadAscii(string path)
        {
            var vertexIndex = new Dictionary<(double, double, double), int>();
            var positions = new Point3DCollection();
            var indices = new Int32Collection();

            int Weld(double x, double y, double z)
            {
                var key = (x, y, z);
                if (!vertexIndex.TryGetValue(key, out int i))
                {
                    i = positions.Count;
                    vertexIndex.Add(key, i);
                    positions.Add(new Point3D(x, y, z));
                }
                return i;
            }

            // "vertex" lines arrive in groups of three (one facet loop each); collect
            // three welded indices, then emit the triangle.
            var tri = new int[3];
            int triFill = 0;

            using var sr = new StreamReader(path);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                // Only "vertex x y z" lines matter; solid/facet/loop/endloop are skipped.
                var t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 4 && t[0] == "vertex")
                {
                    tri[triFill++] = Weld(ParseNum(t[1]), ParseNum(t[2]), ParseNum(t[3]));
                    if (triFill == 3)
                    {
                        triFill = 0;
                        if (tri[0] != tri[1] && tri[1] != tri[2] && tri[0] != tri[2])
                        {
                            indices.Add(tri[0]);
                            indices.Add(tri[1]);
                            indices.Add(tri[2]);
                        }
                    }
                }
            }

            return new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        }

        private static double ParseNum(string s) =>
            double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}