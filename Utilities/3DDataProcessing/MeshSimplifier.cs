using System;
using System.Collections.Generic;    // Dictionary, List
using System.Windows.Media;          // Int32Collection
using System.Windows.Media.Media3D;  // Point3D, Point3DCollection, MeshGeometry3D

namespace DinoLino
{
    /// Grid vertex-clustering decimation (Rossignac–Borrel style).
    ///
    /// Overlays a uniform 3D grid on the mesh, merges every vertex that falls in the
    /// same cell into one output vertex (placed at their average), and drops triangles
    /// that collapse to a point or edge. O(n), no topology bookkeeping, and it preserves
    /// the SILHOUETTE to within one cell — which is what matters here, since all
    /// measurements are taken on the captured 2D projection, not the mesh itself.
    public static class MeshSimplifier
    {
        public static MeshGeometry3D DecimateByClustering(MeshGeometry3D src, int cellsLongestAxis)
        {
            if (src == null) return src;
            var srcPos = src.Positions;
            var srcIdx = src.TriangleIndices;
            int vCount = srcPos?.Count ?? 0;
            int iCount = srcIdx?.Count ?? 0;
            if (vCount == 0 || iCount < 3 || cellsLongestAxis < 2) return src;

            // 21 bits per axis in the packed cell key below.
            if (cellsLongestAxis > 1_000_000) cellsLongestAxis = 1_000_000;

            // Copy out of the (frozen) WPF collections once; array access is much
            // faster than the collection indexers in these hot loops.
            var pos = new Point3D[vCount];
            srcPos.CopyTo(pos, 0);
            var idx = new int[iCount];
            srcIdx.CopyTo(idx, 0);

            // Bounding box
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < vCount; i++)
            {
                var p = pos[i];
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }

            double ext = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            if (ext <= 0) return src;                 // degenerate (all points coincide)
            double cell = ext / cellsLongestAxis;

            // Assign each vertex to a cell; each occupied cell becomes one output vertex.
            var clusterOf = new int[vCount];
            var clusterIndex = new Dictionary<long, int>(Math.Max(16, vCount / 4));
            var sumX = new List<double>();
            var sumY = new List<double>();
            var sumZ = new List<double>();
            var counts = new List<int>();

            for (int v = 0; v < vCount; v++)
            {
                var p = pos[v];
                int ix = (int)((p.X - minX) / cell);
                int iy = (int)((p.Y - minY) / cell);
                int iz = (int)((p.Z - minZ) / cell);
                // Clamp: the max-coordinate vertex lands exactly on the upper boundary,
                // and float slop can nudge just outside on either end.
                if (ix < 0) ix = 0; else if (ix >= cellsLongestAxis) ix = cellsLongestAxis - 1;
                if (iy < 0) iy = 0; else if (iy >= cellsLongestAxis) iy = cellsLongestAxis - 1;
                if (iz < 0) iz = 0; else if (iz >= cellsLongestAxis) iz = cellsLongestAxis - 1;

                long key = ((long)ix << 42) | ((long)iy << 21) | (long)iz;
                if (!clusterIndex.TryGetValue(key, out int c))
                {
                    c = counts.Count;
                    clusterIndex.Add(key, c);
                    sumX.Add(0); sumY.Add(0); sumZ.Add(0); counts.Add(0);
                }
                clusterOf[v] = c;
                sumX[c] += p.X; sumY[c] += p.Y; sumZ[c] += p.Z; counts[c]++;
            }

            // Output vertices: the average of each cell's members (smoother than
            // picking an arbitrary representative).
            var newPos = new Point3DCollection(counts.Count);
            for (int c = 0; c < counts.Count; c++)
                newPos.Add(new Point3D(sumX[c] / counts[c], sumY[c] / counts[c], sumZ[c] / counts[c]));

            // Remap triangles; drop the ones whose corners merged (zero area).
            var newIdx = new Int32Collection(iCount);
            for (int t = 0; t + 2 < iCount; t += 3)
            {
                int a = clusterOf[idx[t]];
                int b = clusterOf[idx[t + 1]];
                int c = clusterOf[idx[t + 2]];
                if (a == b || b == c || a == c) continue;
                newIdx.Add(a); newIdx.Add(b); newIdx.Add(c);
            }

            var mesh = new MeshGeometry3D { Positions = newPos, TriangleIndices = newIdx };
            mesh.Freeze();
            return mesh;
        }
    }
}