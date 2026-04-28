using System.Collections.Generic;
using UnityEngine;

namespace Terrain.Algorithms
{
    public static class LineRaster
    {
        // Supercover line rasterization — every grid cell the geometric line intersects is
        // included, so the result hugs the actual line with no gaps. Includes both endpoints.
        public static List<Vector2Int> Supercover(Vector2Int p0, Vector2Int p1)
        {
            var result = new List<Vector2Int>();
            int dx = p1.x - p0.x, dy = p1.y - p0.y;
            int absDx = Mathf.Abs(dx), absDy = Mathf.Abs(dy);
            int sx = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int sy = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
            int x = p0.x, y = p0.y;
            result.Add(new Vector2Int(x, y));
            int errX = absDy * 2, errY = absDx * 2, err = absDx - absDy, n = absDx + absDy;
            while (n-- > 0)
            {
                if (err > 0)      { x += sx; err -= errX; }
                else if (err < 0) { y += sy; err += errY; }
                else
                {
                    result.Add(new Vector2Int(x + sx, y));
                    x += sx; y += sy; err += errY - errX; n--;
                }
                result.Add(new Vector2Int(x, y));
            }
            return result;
        }

        // Classic Bresenham line rasterization — one pixel per step in the major axis.
        // Diagonal cells are NOT included (unlike Supercover). Pixel count is always
        // max(|dx|, |dy|) + 1, so two parallel lines (translated by a constant) produce
        // the same number of pixels.
        public static List<Vector2Int> Bresenham(Vector2Int p0, Vector2Int p1)
        {
            var result = new List<Vector2Int>();
            int x0 = p0.x, y0 = p0.y;
            int x1 = p1.x, y1 = p1.y;
            int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                result.Add(new Vector2Int(x0, y0));
                if (x0 == x1 && y0 == y1) break;
                int e2 = err * 2;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 <  dx) { err += dx; y0 += sy; }
            }
            return result;
        }

        public static Vector2Int FindFurthest(List<Vector2Int> pts, Vector2Int from)
        {
            Vector2Int best = pts[0];
            int bestSq = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                int dx = pts[i].x - from.x;
                int dy = pts[i].y - from.y;
                int d = dx * dx + dy * dy;
                if (d > bestSq) { bestSq = d; best = pts[i]; }
            }
            return best;
        }
    }
}
