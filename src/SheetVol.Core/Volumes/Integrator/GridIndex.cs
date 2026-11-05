// Uniform grid over bounding boxes (6.15 step 2: "index the base triangles on a grid"). Owner: C5. Internal.

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>
/// Static uniform grid of item bounding boxes over a fixed extent (the boundary's local bounding
/// box). Cell size targets about one item per cell. Items reaching past the extent are clamped
/// to the edge cells; callers only query inside the extent. Not thread-safe (query stamps).
/// </summary>
internal sealed class GridIndex
{
    private const int MaxCellsPerAxis = 1024;

    private readonly double _minX, _minY, _cellX, _cellY;
    private readonly int _nx, _ny;
    private readonly int[] _start;   // CSR offsets, length nx*ny + 1
    private readonly int[] _items;
    private readonly int[] _stamp;
    private int _query;

    public GridIndex(IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> boxes,
                     double minX, double minY, double maxX, double maxY)
    {
        int count = boxes.Count;
        double w = Math.Max(maxX - minX, 0), h = Math.Max(maxY - minY, 0);
        double span = Math.Max(Math.Max(w, h), 1e-300);
        double cell = Math.Sqrt(Math.Max(w, span * 1e-6) * Math.Max(h, span * 1e-6) / Math.Max(count, 1));
        _nx = Math.Clamp((int)Math.Ceiling(w / cell), 1, MaxCellsPerAxis);
        _ny = Math.Clamp((int)Math.Ceiling(h / cell), 1, MaxCellsPerAxis);
        _minX = minX; _minY = minY;
        _cellX = w > 0 ? w / _nx : 1.0;
        _cellY = h > 0 ? h / _ny : 1.0;

        var counts = new int[_nx * _ny + 1];
        for (int k = 0; k < count; k++)
        {
            Range(boxes[k], out int ix0, out int iy0, out int ix1, out int iy1);
            for (int iy = iy0; iy <= iy1; iy++)
                for (int ix = ix0; ix <= ix1; ix++)
                    counts[iy * _nx + ix + 1]++;
        }
        for (int i = 1; i < counts.Length; i++) counts[i] += counts[i - 1];
        _start = counts;
        _items = new int[counts[^1]];
        var fill = new int[_nx * _ny];
        for (int k = 0; k < count; k++)
        {
            Range(boxes[k], out int ix0, out int iy0, out int ix1, out int iy1);
            for (int iy = iy0; iy <= iy1; iy++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    int c = iy * _nx + ix;
                    _items[_start[c] + fill[c]++] = k;
                }
        }
        _stamp = new int[count];
    }

    private void Range((double MinX, double MinY, double MaxX, double MaxY) b, out int ix0, out int iy0, out int ix1, out int iy1)
    {
        ix0 = Cell(b.MinX, _minX, _cellX, _nx);
        ix1 = Cell(b.MaxX, _minX, _cellX, _nx);
        iy0 = Cell(b.MinY, _minY, _cellY, _ny);
        iy1 = Cell(b.MaxY, _minY, _cellY, _ny);
    }

    private static int Cell(double v, double min, double size, int n)
    {
        double f = Math.Floor((v - min) / size);
        if (f < 0) return 0;
        if (f >= n) return n - 1;
        return (int)f;
    }

    /// <summary>Appends to <paramref name="result"/> every item whose cells meet the query box, each once.</summary>
    public void Query(double minX, double minY, double maxX, double maxY, List<int> result)
    {
        result.Clear();
        if (++_query == int.MaxValue)
        {
            Array.Clear(_stamp);
            _query = 1;
        }
        Range((minX, minY, maxX, maxY), out int ix0, out int iy0, out int ix1, out int iy1);
        for (int iy = iy0; iy <= iy1; iy++)
            for (int ix = ix0; ix <= ix1; ix++)
            {
                int c = iy * _nx + ix;
                for (int p = _start[c]; p < _start[c + 1]; p++)
                {
                    int k = _items[p];
                    if (_stamp[k] == _query) continue;
                    _stamp[k] = _query;
                    result.Add(k);
                }
            }
    }
}
