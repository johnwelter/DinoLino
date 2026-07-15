using System;
using System.Collections.Generic;   // List<int>
using System.Globalization;
using System.IO;
using System.Windows.Media;          // Int32Collection
using System.Windows.Media.Media3D;  // Point3D, Point3DCollection, MeshGeometry3D

namespace DinoLino
{
    /// Minimal Wavefront OBJ mesh reader. Reads 'v' vertex positions and 'f' faces
    /// (fan-triangulated). Texture/normal indices, materials, groups, smoothing, and
    /// free-form geometry are ignored. Face indices may be 1-based positive or negative
    /// (relative to the vertices seen so far), per the OBJ spec.
    public static class ObjLoader
    {
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
                if (hash >= 0) line = line.Substring(0, hash); // strip comment

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

        // Parses the vertex-position index from a face token like "12", "12/3", "12//5",
        // or "12/3/5". OBJ indices are 1-based; negative values count back from the current
        // vertex total. Returns a 0-based index, or -1 if it can't be parsed.
        private static int ParseFaceIndex(string token, int vertexCount)
        {
            int slash = token.IndexOf('/');
            string posPart = slash >= 0 ? token.Substring(0, slash) : token;
            if (!int.TryParse(posPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                return -1;
            if (idx > 0) return idx - 1;           // 1-based -> 0-based
            if (idx < 0) return vertexCount + idx; // negative = relative to end
            return -1;                             // 0 is invalid in OBJ
        }

        private static void AddFace(List<int> face, Int32Collection indices)
        {
            // Fan-triangulate polygons; triangles pass straight through.
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