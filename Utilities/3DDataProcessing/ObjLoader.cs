using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DinoLino
{
    /// <summary>
    /// Minimal Wavefront OBJ reader for WPF meshes.
    /// Supports vertex positions and polygon faces; other OBJ features are ignored.
    /// </summary>
    public static class ObjLoader
    {
        /// <summary>
        /// Loads an OBJ file into a frozen MeshGeometry3D.
        /// </summary>
        public static MeshGeometry3D Load(string path)
        {
            var positions = new Point3DCollection();
            var indices = new Int32Collection();
            var face = new List<int>(8);

            using var sr = new StreamReader(path);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);

                var t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;

                if (t[0] == "v" && t.Length >= 4)
                {
                    positions.Add(new Point3D(ParseNum(t[1]), ParseNum(t[2]), ParseNum(t[3])));
                }
                else if (t[0] == "f" && t.Length >= 4)
                {
                    face.Clear();
                    for (int i = 1; i < t.Length; i++)
                    {
                        int vi = ParseFaceIndex(t[i], positions.Count);
                        if (vi >= 0) face.Add(vi);
                    }
                    AddFace(face, indices);
                }
            }

            var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            mesh.Freeze();
            return mesh;
        }

        /// <summary>
        /// Parses the vertex index from an OBJ face token.
        /// </summary>
        private static int ParseFaceIndex(string token, int vertexCount)
        {
            int slash = token.IndexOf('/');
            string posPart = slash >= 0 ? token.Substring(0, slash) : token;

            if (!int.TryParse(posPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                return -1;

            if (idx > 0) return idx - 1;
            if (idx < 0) return vertexCount + idx;
            return -1;
        }

        /// <summary>
        /// Triangulates a polygon face using a fan from the first vertex.
        /// </summary>
        private static void AddFace(List<int> face, Int32Collection indices)
        {
            for (int i = 1; i + 1 < face.Count; i++)
            {
                indices.Add(face[0]);
                indices.Add(face[i]);
                indices.Add(face[i + 1]);
            }
        }

        private static double ParseNum(string s) =>
            double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}