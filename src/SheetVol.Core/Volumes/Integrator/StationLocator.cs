// Station of a point from AlignSeg geometry (6.15: "closed form on lines and arcs, iterative on
// spirals"). Owner: C5. Cross-checked against Alignment.StationOffset in the self-test (A1/S1).
//
// Conventions (HANDOFF.md 6.2): raw stations; offset negative = left of the direction of
// travel, positive = right; AlignSeg directions in radians CCW from +X; Clockwise = the arc
// turns right in the direction of travel. All distances in drawing units.

using System.Diagnostics.CodeAnalysis;

namespace SheetVol.Core.Volumes.Integrator;

/// <summary>Station and offset of a point relative to an alignment.</summary>
/// <param name="Station">Raw station of the nearest alignment point, drawing units. Clamped to the
/// alignment (or to the searched window) for points beyond its ends.</param>
/// <param name="Offset">Signed offset: negative left, positive right, drawing units. For a point
/// beyond an end it is the distance to that end, signed by side.</param>
/// <param name="Distance">Distance from the point to the nearest alignment point, drawing units.</param>
public readonly record struct StationOffset(double Station, double Offset, double Distance);

/// <summary>
/// Projects points onto an alignment built from <see cref="AlignSeg"/> entities. Lines and arcs
/// are closed form; spirals are solved iteratively with <see cref="IAlignmentGeometry.PointAt"/>
/// (bracketed from a 1-unit sample table, then Illinois regula falsi to 1e-11 * length).
/// Points are given in a frame translated by <see cref="Origin"/> (world = local + Origin) so the
/// integrator can stay in its local frame (E1009).
/// </summary>
public sealed class StationLocator
{
    private readonly Model[] _models;

    /// <summary>World coordinates of the frame origin used by <see cref="Locate(Pt)"/>.</summary>
    public Pt Origin { get; }

    /// <summary>First raw station of the alignment.</summary>
    public double StartStation => _models[0].S0;

    /// <summary>Last raw station of the alignment.</summary>
    public double EndStation => _models[^1].S1;

    private StationLocator(Pt origin, Model[] models)
    {
        Origin = origin;
        _models = models;
    }

    /// <summary>
    /// Builds a locator. Fails, with a plain-words reason, when the segments are missing, out of
    /// order, or internally inconsistent (line length versus station length, arc points off the
    /// radius, Clockwise flag disagreeing with the start direction, spiral ends disagreeing with
    /// PointAt). Inconsistent input is an adapter defect; it is reported, never guessed around.
    /// </summary>
    public static bool TryCreate(IAlignmentGeometry alignment, Pt origin,
                                 [NotNullWhen(true)] out StationLocator? locator, out string? error)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        locator = null; error = null;
        var segs = alignment.Segments;
        if (segs is null || segs.Count == 0) { error = $"alignment '{alignment.Name}' has no segments"; return false; }
        var models = new Model[segs.Count];
        double prevEnd = double.NegativeInfinity;
        for (int i = 0; i < segs.Count; i++)
        {
            var g = segs[i];
            string where = $"alignment '{alignment.Name}' segment {i + 1} ({g.Kind}, stations {g.StartStation:0.###} to {g.EndStation:0.###})";
            if (!double.IsFinite(g.StartStation) || !double.IsFinite(g.EndStation) || !(g.EndStation > g.StartStation))
            { error = $"{where}: end station must be greater than start station"; return false; }
            if (g.StartStation < prevEnd - 1e-6 * Math.Max(1.0, Math.Abs(prevEnd)))
            { error = $"{where}: segments are not in station order"; return false; }
            prevEnd = g.EndStation;
            Model? m = g.Kind switch
            {
                SegKind.Line => LineModel.Create(g, origin, out error),
                SegKind.Arc => ArcModel.Create(g, origin, out error),
                SegKind.Spiral => SpiralModel.Create(g, alignment, origin, out error),
                _ => null,
            };
            if (m is null) { error = $"{where}: {error ?? "unknown segment kind"}"; return false; }
            models[i] = m;
        }
        locator = new StationLocator(origin, models);
        return true;
    }

    /// <summary>Station and offset of a point given in the locator's frame (world minus <see cref="Origin"/>).</summary>
    public StationOffset Locate(Pt point) => Locate(point, double.NegativeInfinity, double.PositiveInfinity);

    /// <summary>
    /// Station and offset of a point (locator frame), searching only the alignment segments that
    /// overlap [fromStation, toStation]. Restricting the search keeps a looping alignment from
    /// returning a station on a far part of itself. If no segment overlaps the window, all are searched.
    /// </summary>
    public StationOffset Locate(Pt point, double fromStation, double toStation)
    {
        StationOffset best = default;
        bool found = false;
        for (int pass = 0; pass < 2 && !found; pass++)
        {
            foreach (var m in _models)
            {
                if (pass == 0 && (m.S1 < fromStation || m.S0 > toStation)) continue;
                var c = m.Project(point.X, point.Y);
                if (!found || c.Distance < best.Distance) { best = c; found = true; }
            }
        }
        return best;
    }

    // ------------------------------------------------------------------------------------------

    private abstract class Model
    {
        public double S0, S1;
        public abstract StationOffset Project(double px, double py);
    }

    private sealed class LineModel : Model
    {
        private double _sx, _sy, _ex, _ey, _ux, _uy, _len;

        public static LineModel? Create(AlignSeg g, Pt o, out string? error)
        {
            error = null;
            double sx = g.Start.X - o.X, sy = g.Start.Y - o.Y, ex = g.End.X - o.X, ey = g.End.Y - o.Y;
            double len = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
            double slen = g.EndStation - g.StartStation;
            if (!(len > 0)) { error = "line has zero length"; return null; }
            if (Math.Abs(len - slen) > 1e-6 * Math.Max(1.0, slen))
            { error = $"line length {len:0.######} differs from its station length {slen:0.######}"; return null; }
            return new LineModel
            {
                S0 = g.StartStation, S1 = g.EndStation,
                _sx = sx, _sy = sy, _ex = ex, _ey = ey,
                _ux = (ex - sx) / len, _uy = (ey - sy) / len, _len = len,
            };
        }

        public override StationOffset Project(double px, double py)
        {
            double vx = px - _sx, vy = py - _sy;
            double t = vx * _ux + vy * _uy;
            double perp = -(_ux * vy - _uy * vx);       // left of travel = negative
            if (t <= 0)
            {
                double d = Math.Sqrt(vx * vx + vy * vy);
                return new StationOffset(S0, perp < 0 ? -d : d, d);
            }
            if (t >= _len)
            {
                double wx = px - _ex, wy = py - _ey;
                double d = Math.Sqrt(wx * wx + wy * wy);
                return new StationOffset(S1, perp < 0 ? -d : d, d);
            }
            return new StationOffset(S0 + t * (S1 - S0) / _len, perp, Math.Abs(perp));
        }
    }

    private sealed class ArcModel : Model
    {
        private double _cx, _cy, _r, _phi0, _sweep, _sx, _sy, _ex, _ey;
        private bool _cw;

        public static ArcModel? Create(AlignSeg g, Pt o, out string? error)
        {
            error = null;
            if (g.Center is not Pt c) { error = "arc has no center"; return null; }
            double r = g.Radius;
            if (!(r > 0) || !double.IsFinite(r)) { error = "arc radius must be positive"; return null; }
            double cx = c.X - o.X, cy = c.Y - o.Y;
            double sx = g.Start.X - o.X, sy = g.Start.Y - o.Y, ex = g.End.X - o.X, ey = g.End.Y - o.Y;
            double tolR = 1e-6 * Math.Max(1.0, r);
            double rs = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
            double re = Math.Sqrt((ex - cx) * (ex - cx) + (ey - cy) * (ey - cy));
            if (Math.Abs(rs - r) > tolR || Math.Abs(re - r) > tolR)
            { error = $"arc end points are not on radius {r:0.######} (start {rs:0.######}, end {re:0.######})"; return null; }
            double sweep = (g.EndStation - g.StartStation) / r;
            if (sweep > 2 * Math.PI + 1e-9) { error = "arc sweeps more than a full circle"; return null; }

            // Turning sense from the start direction: center on the left of travel = counter-clockwise.
            double dx = Math.Cos(g.StartDir), dy = Math.Sin(g.StartDir);
            double side = dx * (cy - sy) - dy * (cx - sx);
            if (Math.Abs(side) < 0.5 * r)
            { error = "arc start direction is not tangent to the arc"; return null; }
            bool cwFromGeometry = side < 0;
            if (cwFromGeometry != g.Clockwise)
            { error = $"Clockwise = {g.Clockwise} disagrees with the start direction and center (the arc turns {(cwFromGeometry ? "right" : "left")})"; return null; }

            double phi0 = Math.Atan2(sy - cy, sx - cx);
            double phiEnd = g.Clockwise ? phi0 - sweep : phi0 + sweep;
            double ex2 = cx + r * Math.Cos(phiEnd), ey2 = cy + r * Math.Sin(phiEnd);
            if (Math.Sqrt((ex2 - ex) * (ex2 - ex) + (ey2 - ey) * (ey2 - ey)) > 1e-6 * Math.Max(1.0, r))
            { error = "arc end point disagrees with its start point, length and turning sense"; return null; }

            return new ArcModel
            {
                S0 = g.StartStation, S1 = g.EndStation,
                _cx = cx, _cy = cy, _r = r, _phi0 = phi0, _sweep = sweep, _cw = g.Clockwise,
                _sx = sx, _sy = sy, _ex = ex, _ey = ey,
            };
        }

        public override StationOffset Project(double px, double py)
        {
            double dx = px - _cx, dy = py - _cy;
            double rho = Math.Sqrt(dx * dx + dy * dy);
            double radial = _cw ? _r - rho : rho - _r;   // right of travel = positive
            if (rho > 0)
            {
                double phi = Math.Atan2(dy, dx);
                double a = _cw ? _phi0 - phi : phi - _phi0;
                a -= 2 * Math.PI * Math.Floor(a / (2 * Math.PI));
                if (a <= _sweep)
                    return new StationOffset(S0 + _r * a, radial, Math.Abs(radial));
            }
            double ds = Math.Sqrt((px - _sx) * (px - _sx) + (py - _sy) * (py - _sy));
            double de = Math.Sqrt((px - _ex) * (px - _ex) + (py - _ey) * (py - _ey));
            return ds <= de
                ? new StationOffset(S0, radial < 0 ? -ds : ds, ds)
                : new StationOffset(S1, radial < 0 ? -de : de, de);
        }
    }

    private sealed class SpiralModel : Model
    {
        private const double SampleSpacing = 1.0;   // drawing units along the spiral
        private IAlignmentGeometry _al = null!;
        private Pt _o;
        private double[] _s = [], _qx = [], _qy = [], _tx = [], _ty = [];

        public static SpiralModel? Create(AlignSeg g, IAlignmentGeometry al, Pt o, out string? error)
        {
            error = null;
            double len = g.EndStation - g.StartStation;
            int k = (int)Math.Clamp(Math.Ceiling(len / SampleSpacing), 8, 100_000);
            var m = new SpiralModel
            {
                S0 = g.StartStation, S1 = g.EndStation, _al = al, _o = o,
                _s = new double[k + 1], _qx = new double[k + 1], _qy = new double[k + 1],
                _tx = new double[k + 1], _ty = new double[k + 1],
            };
            for (int i = 0; i <= k; i++)
            {
                double s = i == k ? g.EndStation : g.StartStation + len * i / k;
                m._s[i] = s;
                m.Frame(s, out m._qx[i], out m._qy[i], out m._tx[i], out m._ty[i]);
            }
            double tol = 1e-6 * Math.Max(1.0, len);
            if (Math.Abs(m._qx[0] - (g.Start.X - o.X)) > tol || Math.Abs(m._qy[0] - (g.Start.Y - o.Y)) > tol ||
                Math.Abs(m._qx[k] - (g.End.X - o.X)) > tol || Math.Abs(m._qy[k] - (g.End.Y - o.Y)) > tol)
            { error = "spiral end points disagree with PointAt at its start and end stations"; return null; }
            return m;
        }

        /// <summary>Centerline point and unit tangent at raw station s, local frame. Tangent = right normal rotated +90 degrees.</summary>
        private void Frame(double s, out double qx, out double qy, out double tx, out double ty)
        {
            var q = _al.PointAt(s, 0.0);
            var r = _al.PointAt(s, 1.0);
            qx = q.X - _o.X; qy = q.Y - _o.Y;
            double nx = r.X - q.X, ny = r.Y - q.Y;
            double nl = Math.Sqrt(nx * nx + ny * ny);
            tx = -ny / nl; ty = nx / nl;
        }

        private double G(double s, double px, double py)
        {
            Frame(s, out double qx, out double qy, out double tx, out double ty);
            return (px - qx) * tx + (py - qy) * ty;
        }

        public override StationOffset Project(double px, double py)
        {
            int n = _s.Length, best = 0;
            double bestD = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                double d = (px - _qx[i]) * (px - _qx[i]) + (py - _qy[i]) * (py - _qy[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
            double gb = (px - _qx[best]) * _tx[best] + (py - _qy[best]) * _ty[best];
            int lo, hi;
            if (gb > 0)
            {
                if (best == n - 1) return End(px, py, S1);
                lo = best; hi = best + 1;
                while (hi < n - 1 && (px - _qx[hi]) * _tx[hi] + (py - _qy[hi]) * _ty[hi] > 0) { lo = hi; hi++; }
                if ((px - _qx[hi]) * _tx[hi] + (py - _qy[hi]) * _ty[hi] > 0) return End(px, py, S1);
            }
            else if (gb < 0)
            {
                if (best == 0) return End(px, py, S0);
                lo = best - 1; hi = best;
                while (lo > 0 && (px - _qx[lo]) * _tx[lo] + (py - _qy[lo]) * _ty[lo] < 0) { hi = lo; lo--; }
                if ((px - _qx[lo]) * _tx[lo] + (py - _qy[lo]) * _ty[lo] < 0) return End(px, py, S0);
            }
            else return At(px, py, _s[best]);

            // g decreases through zero on [a, b]: Illinois regula falsi.
            double a = _s[lo], b = _s[hi];
            double ga = G(a, px, py), gbv = G(b, px, py);
            double tol = 1e-11 * Math.Max(1.0, S1 - S0);
            int side = 0;
            double s = 0.5 * (a + b);
            for (int it = 0; it < 200 && b - a > tol; it++)
            {
                s = (ga - gbv) != 0 ? a + ga * (b - a) / (ga - gbv) : 0.5 * (a + b);
                if (!(s > a && s < b)) s = 0.5 * (a + b);
                double gs = G(s, px, py);
                if (gs == 0) { a = b = s; break; }
                if (gs > 0)
                {
                    a = s; ga = gs;
                    if (side == 1) gbv *= 0.5;
                    side = 1;
                }
                else
                {
                    b = s; gbv = gs;
                    if (side == -1) ga *= 0.5;
                    side = -1;
                }
            }
            return At(px, py, 0.5 * (a + b));
        }

        private StationOffset At(double px, double py, double s)
        {
            Frame(s, out double qx, out double qy, out double tx, out double ty);
            double vx = px - qx, vy = py - qy;
            double off = vx * ty - vy * tx;              // dot with right normal (ty, -tx)
            return new StationOffset(s, off, Math.Sqrt(vx * vx + vy * vy));
        }

        private StationOffset End(double px, double py, double s)
        {
            var c = At(px, py, s);
            return c with { Offset = c.Offset < 0 ? -c.Distance : c.Distance };
        }
    }
}
