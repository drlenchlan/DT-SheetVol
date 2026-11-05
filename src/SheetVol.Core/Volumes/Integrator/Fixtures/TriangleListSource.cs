// In-memory ITriangleSource for code-built fixtures. Owner: C5.

namespace SheetVol.Core.Volumes.Integrator.Fixtures;

/// <summary>
/// A fixed list of triangles exposed as an <see cref="ITriangleSource"/>. World coordinates,
/// drawing units. <see cref="Triangles"/> returns the triangles whose bounding box meets the
/// window (inclusive), in list order.
/// </summary>
public sealed class TriangleListSource : ITriangleSource
{
    private readonly Tri3[] _tris;
    private readonly double[] _minX, _minY, _maxX, _maxY;

    public TriangleListSource(string name, IEnumerable<Tri3> triangles)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(triangles);
        Name = name;
        _tris = [.. triangles];
        _minX = new double[_tris.Length]; _minY = new double[_tris.Length];
        _maxX = new double[_tris.Length]; _maxY = new double[_tris.Length];
        for (int i = 0; i < _tris.Length; i++)
        {
            var t = _tris[i];
            _minX[i] = Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)); _maxX[i] = Math.Max(t.A.X, Math.Max(t.B.X, t.C.X));
            _minY[i] = Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)); _maxY[i] = Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y));
        }
    }

    /// <summary>Surface name, for messages and the Civil 3D fixture builder.</summary>
    public string Name { get; }

    /// <summary>Every triangle, in generation order (counter-clockwise vertices).</summary>
    public IReadOnlyList<Tri3> All => _tris;

    /// <inheritdoc />
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        for (int i = 0; i < _tris.Length; i++)
        {
            if (_maxX[i] < windowMin.X || _minX[i] > windowMax.X || _maxY[i] < windowMin.Y || _minY[i] > windowMax.Y) continue;
            yield return _tris[i];
        }
    }
}
