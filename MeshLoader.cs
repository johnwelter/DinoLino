using System;
using System.IO;
using System.Windows.Media.Media3D; // MeshGeometry3D

namespace DinoLino
{
    /// Dispatches 3D model loading to the right reader based on file extension, then
    /// decimates oversized meshes so WPF's Viewport3D stays interactive.
    /// Supports .ply, .stl, and .obj; all readers return the same frozen
    /// MeshGeometry3D shape (Positions + TriangleIndices).
    public static class MeshLoader
    {
        // WPF's Viewport3D is not a modern GPU-shader renderer; interaction degrades
        // badly past a few hundred thousand triangles. Measurements in DinoLino are
        // taken on the captured 2D projection, so only the SILHOUETTE matters: at 512
        // clustering cells across the model, silhouette error is bounded by one cell
        // (~0.2% of model size), below the outline tracer's pixel resolution. Raise
        // MaxDisplayTriangles / InitialClusterCells on a fast machine if you want more
        // on-screen detail.
        private const int MaxDisplayTriangles = 300_000;
        private const int InitialClusterCells = 512;
        private const int MinClusterCells = 64;

        public static MeshGeometry3D Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            MeshGeometry3D mesh;
            switch (ext)
            {
                case ".ply": mesh = PlyLoader.Load(path); break;
                case ".stl": mesh = StlLoader.Load(path); break;
                case ".obj": mesh = ObjLoader.Load(path); break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported 3D model format '{ext}'. Supported formats are .ply, .stl, and .obj.");
            }
            return DecimateForDisplay(mesh);
        }

        // Clusters at a halving grid resolution until the mesh is small enough for
        // interactive display (or the resolution floor is reached). Small meshes pass
        // straight through untouched.
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