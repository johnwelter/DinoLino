using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DinoLino
{
    /// <summary>
    /// Minimal STL reader for WPF meshes.
    /// Supports ASCII and binary STL.
    /// </summary>
    public static class StlLoader
    {
        /// <summary>
        /// Loads an STL file into a frozen MeshGeometry3D.
        /// </summary>
        public static MeshGeometry3D Load(string path)
        {
            var mesh = IsBinary(path) ? ReadBinary(path) : ReadAscii(path);
            mesh.Freeze();
            return mesh;
        }

        /// <summary>
        /// Detects binary STL by file length.
        /// </summary>
        private static bool IsBinary(string path)
        {
            long len = new FileInfo(path).Length;
            if (len < 84) return false;

            using var br = new BinaryReader(File.OpenRead(path));
            br.BaseStream.Seek(80, SeekOrigin.Begin);
            uint tris = br.ReadUInt32();
            return len == 84L + 50L * tris;
        }

        /// <summary>
        /// Reads a binary STL file and welds identical vertices.
        /// </summary>
        private static MeshGeometry3D ReadBinary(string path)
        {
            using var br = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
            br.ReadBytes(80);
            uint tris = br.ReadUInt32();

            int posCap = (int)Math.Min((long)tris, 6_000_000L);
            int idxCap = (int)Math.Min(tris * 3L, 18_000_000L);

            var vertexIndex = new Dictionary<(float, float, float), int>(posCap);
            var positions = new Point3DCollection(posCap);
            var indices = new Int32Collection(idxCap);

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
                // Normal is stored in STL but not used by this viewer.
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle();

                int i0 = Weld(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                int i1 = Weld(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                int i2 = Weld(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                br.ReadUInt16();

                // Drop triangles that collapsed after welding.
                if (i0 != i1 && i1 != i2 && i0 != i2)
                {
                    indices.Add(i0);
                    indices.Add(i1);
                    indices.Add(i2);
                }
            }

            return new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        }

        /// <summary>
        /// Reads an ASCII STL file and welds identical vertices.
        /// </summary>
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

            var tri = new int[3];
            int triFill = 0;

            using var sr = new StreamReader(path);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                var t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 4 && t[0] == "vertex")
                {
                    tri[triFill++] = Weld(ParseNum(t[1]), ParseNum(t[2]), ParseNum(t[3]));
                    if (triFill == 3)
                    {
                        triFill = 0;

                        // Drop triangles that collapsed after welding.
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