using System;
using System.Collections.Generic;   
using System.Windows.Media;          
using System.Windows.Media.Media3D;  

namespace DinoLino
{
    /// <summary>
    /// Simplifies a mesh by clustering vertices into a uniform 3D grid.
    /// </summary>
    public static class MeshSimplifier
    {
        /// <summary>
        /// Decimates a mesh by merging vertices that fall in the same grid cell.
        /// </summary>
        public static MeshGeometry3D DecimateByClustering(MeshGeometry3D src, int cellsLongestAxis)
        {
            if (src == null) return src;

            var srcPos = src.Positions;
            var srcIdx = src.TriangleIndices;
            int vCount = srcPos?.Count ?? 0;
            int iCount = srcIdx?.Count ?? 0;

            if (vCount == 0 || iCount < 3 || cellsLongestAxis < 2) return src;

            // Limit the packed cell key to a safe range.
            if (cellsLongestAxis > 1_000_000) cellsLongestAxis = 1_000_000;

            // Copy WPF collections to arrays for faster access in the hot loops below.
            var pos = new Point3D[vCount];
            srcPos.CopyTo(pos, 0);
            var idx = new int[iCount];
            srcIdx.CopyTo(idx, 0);

            // Compute the mesh bounding box.
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < vCount; i++)
            {
                var p = pos[i];
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            double ext = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            if (ext <= 0) return src;

            double cell = ext / cellsLongestAxis;

            // Build one output vertex per occupied grid cell.
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

                // Clamp boundary cases caused by exact max values or floating-point slop.
                if (ix < 0) ix = 0; else if (ix >= cellsLongestAxis) ix = cellsLongestAxis - 1;
                if (iy < 0) iy = 0; else if (iy >= cellsLongestAxis) iy = cellsLongestAxis - 1;
                if (iz < 0) iz = 0; else if (iz >= cellsLongestAxis) iz = cellsLongestAxis - 1;

                long key = ((long)ix << 42) | ((long)iy << 21) | (long)iz;

                if (!clusterIndex.TryGetValue(key, out int c))
                {
                    c = counts.Count;
                    clusterIndex.Add(key, c);
                    sumX.Add(0);
                    sumY.Add(0);
                    sumZ.Add(0);
                    counts.Add(0);
                }

                clusterOf[v] = c;
                sumX[c] += p.X;
                sumY[c] += p.Y;
                sumZ[c] += p.Z;
                counts[c]++;
            }

            // Average each cluster to produce the simplified vertex list.
            var newPos = new Point3DCollection(counts.Count);
            for (int c = 0; c < counts.Count; c++)
                newPos.Add(new Point3D(sumX[c] / counts[c], sumY[c] / counts[c], sumZ[c] / counts[c]));

            // Remap triangles and drop any that collapsed during clustering.
            var newIdx = new Int32Collection(iCount);
            for (int t = 0; t + 2 < iCount; t += 3)
            {
                int a = clusterOf[idx[t]];
                int b = clusterOf[idx[t + 1]];
                int c = clusterOf[idx[t + 2]];

                if (a == b || b == c || a == c) continue;

                newIdx.Add(a);
                newIdx.Add(b);
                newIdx.Add(c);
            }

            var mesh = new MeshGeometry3D { Positions = newPos, TriangleIndices = newIdx };
            mesh.Freeze();
            return mesh;
        }
    }
}