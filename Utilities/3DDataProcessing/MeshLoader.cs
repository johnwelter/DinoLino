using System;
using System.IO;
using System.Windows.Media.Media3D; 

namespace DinoLino
{
    /// <summary>
    /// Loads 3D models by file extension and simplifies large meshes for interactive WPF display.
    /// </summary>
    public static class MeshLoader
    {
        // Keep the on-screen mesh small enough for responsive WPF 3D interaction.
        private const int MaxDisplayTriangles = 300_000;
        private const int InitialClusterCells = 512;
        private const int MinClusterCells = 64;

        /// <summary>
        /// Loads a mesh from disk and decimates it if needed for display.
        /// </summary>
        public static MeshGeometry3D Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            MeshGeometry3D mesh;

            switch (ext)
            {
                case ".ply":
                    mesh = PlyLoader.Load(path);
                    break;
                case ".stl":
                    mesh = StlLoader.Load(path);
                    break;
                case ".obj":
                    mesh = ObjLoader.Load(path);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported 3D model format '{ext}'. Supported formats are .ply, .stl, and .obj.");
            }

            return DecimateForDisplay(mesh);
        }

        /// <summary>
        /// Reduces triangle count in stages until the mesh is suitable for interactive display.
        /// </summary>
        private static MeshGeometry3D DecimateForDisplay(MeshGeometry3D mesh)
        {
            int cells = InitialClusterCells;

            while (mesh.TriangleIndices.Count / 3 > MaxDisplayTriangles && cells >= MinClusterCells)
            {
                mesh = MeshSimplifier.DecimateByClustering(mesh, cells);
                cells /= 2;
            }

            return mesh;
        }
    }
}