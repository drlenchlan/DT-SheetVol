// Triangle-source wrappers and helpers for the C5 integrator tests, prefixed C5. The alignments
// and band polygons that used to live here are the shared fakes in tests/.../Fakes/:
// C5Alignment.S and C are FakeAlignments.S and C (same geometry), C5Alignment.SP is
// FakeAlignments.SP_C5 (R 400, not C1's R 500), and C5Boundaries is FakeBands.

namespace SheetVol.Core.Tests.Integrator;

/// <summary>Wraps a source and moves it by (dx, dy) (state-plane test, E1009).</summary>
internal sealed class C5ShiftedSource(ITriangleSource inner, double dx, double dy) : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        foreach (var t in inner.Triangles(new Pt(windowMin.X - dx, windowMin.Y - dy), new Pt(windowMax.X - dx, windowMax.Y - dy)))
            yield return new Tri3(Move(t.A), Move(t.B), Move(t.C));
    }

    private Pt3 Move(Pt3 p) => new(p.X + dx, p.Y + dy, p.Z);
}

/// <summary>Wraps a source, rotates it by <c>a</c> radians about the origin, then moves it by (dx, dy). Elevations unchanged.</summary>
internal sealed class C5RotatedSource(ITriangleSource inner, double a, double dx, double dy) : ITriangleSource
{
    private readonly double _c = Math.Cos(a), _s = Math.Sin(a);

    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        // Inverse-rotate the window corners and request their bounding box.
        Span<Pt> corners = [new(windowMin.X, windowMin.Y), new(windowMax.X, windowMin.Y), new(windowMax.X, windowMax.Y), new(windowMin.X, windowMax.Y)];
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var p in corners)
        {
            double u = p.X - dx, v = p.Y - dy;
            double x = _c * u + _s * v, y = -_s * u + _c * v;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        return inner.Triangles(new Pt(x0, y0), new Pt(x1, y1)).Select(t => new Tri3(Move(t.A), Move(t.B), Move(t.C)));
    }

    private Pt3 Move(Pt3 p) => new(dx + _c * p.X - _s * p.Y, dy + _s * p.X + _c * p.Y, p.Z);
}

/// <summary>Wraps a source and scales x, y and z by k (unit-agnostic test, E1020).</summary>
internal sealed class C5ScaledSource(ITriangleSource inner, double k) : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        foreach (var t in inner.Triangles(new Pt(windowMin.X / k, windowMin.Y / k), new Pt(windowMax.X / k, windowMax.Y / k)))
            yield return new Tri3(Scale(t.A), Scale(t.B), Scale(t.C));
    }

    private Pt3 Scale(Pt3 p) => new(p.X * k, p.Y * k, p.Z * k);
}

/// <summary>Keeps only the triangles whose centroid satisfies a predicate (a surface with a hole or a partial extent, E1011).</summary>
internal sealed class C5FilteredSource(ITriangleSource inner, Func<double, double, bool> keep) : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
        => inner.Triangles(windowMin, windowMax).Where(t => keep((t.A.X + t.B.X + t.C.X) / 3, (t.A.Y + t.B.Y + t.C.Y) / 3));
}

/// <summary>A source with no triangles (E403).</summary>
internal sealed class C5EmptySource : ITriangleSource
{
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax) => [];
}

/// <summary>Shared constants and helpers.</summary>
internal static class C5
{
    public const double CubicFeetPerCubicYard = 27.0;

    public static SegmentDef Seg(double s0, double s1, double offL = -100, double offR = 100)
        => new(Guid.NewGuid(), $"{s0}-{s1}", s0, s1, offL, offR);

    /// <summary>Asserts |actual - expected| &lt;= rel * max(|expected|, floor).</summary>
    public static void Rel(double expected, double actual, double rel, double floor = 1.0, string? what = null)
    {
        double tol = rel * Math.Max(Math.Abs(expected), floor);
        Assert.True(Math.Abs(actual - expected) <= tol,
            $"{what ?? "value"}: expected {expected:R}, got {actual:R} (diff {actual - expected:E3}, tol {tol:E3})");
    }
}
