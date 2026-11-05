// A1 adapters. Owner: A1.

using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Adapters;

/// <summary>
/// <see cref="ITriangleSource"/> over one TIN surface (HANDOFF.md 6.15, probe P23).
/// <para>Reads <c>GetTriangles(false)</c>: visible triangles only, so hide and outer boundaries
/// and the slivers Civil 3D hides by itself are left out exactly as Civil's own volume
/// calculation leaves them out (E1013); masks hide nothing (P23). Civil 3D has no windowed
/// triangle query, so the surface is read once when the adapter is built (about 1 M triangles
/// per 1.1 to 1.5 s, P23), optionally keeping only triangles that overlap a read window (the
/// run's whole band), and held as <see cref="Tri3"/> in drawing units. Every
/// <see cref="Triangles"/> call then answers from a grid index over the cached triangles: the
/// integrator asks for one segment window at a time.</para>
/// <para>The cache is a snapshot: build a new adapter for every regen, after any rebuild. The
/// window query reuses one scratch array, so one adapter serves one thread at a time.</para>
/// <para><b>Reference and corridor surfaces (E1021).</b> The read is the same API on the TIN
/// surface object (a reference surface is a <c>TinSurface</c>; a corridor surface's
/// <c>SurfaceId</c> is one). Probe P23 could not build either headless, so the adapter reads
/// them and attaches an SV-E1021 warning; the integrator's band totals are cross-checked against
/// <c>GetBoundedVolumes</c> on every regen (E1012), so a wrong read fails the run rather than
/// reporting a wrong number. Stop condition 12.9 stays open until Dan's drawing shows it.</para>
/// </summary>
public sealed class TriangleSourceAdapter : ITriangleSource
{
    private readonly Tri3[] _tris;
    private readonly double[] _minX, _minY, _maxX, _maxY;
    private readonly double _gx0, _gy0, _cell;
    private readonly int _nx, _ny;
    private readonly int[] _cellStart = [], _cellItems = [], _oversize = [];
    private readonly int[] _stamp;
    private int _stampValue;

    private TriangleSourceAdapter(string name, ObjectId id, SurfaceOrigin origin, Tri3[] tris, int visibleTotal, double seconds, IReadOnlyList<Problem> problems, int maxCells, int oversizeCells)
    {
        Name = name;
        Id = id;
        Origin = origin;
        _tris = tris;
        VisibleTotal = visibleTotal;
        ReadSeconds = seconds;
        Problems = problems;
        int n = tris.Length;
        _minX = new double[n]; _minY = new double[n]; _maxX = new double[n]; _maxY = new double[n];
        _stamp = new int[n];
        if (n == 0) { _cell = 1; _nx = _ny = 0; return; }

        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            var t = tris[i];
            _minX[i] = Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)); _maxX[i] = Math.Max(t.A.X, Math.Max(t.B.X, t.C.X));
            _minY[i] = Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)); _maxY[i] = Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y));
            x0 = Math.Min(x0, _minX[i]); x1 = Math.Max(x1, _maxX[i]); y0 = Math.Min(y0, _minY[i]); y1 = Math.Max(y1, _maxY[i]);
        }
        double w = Math.Max(x1 - x0, 1e-9), h = Math.Max(y1 - y0, 1e-9);
        int target = Math.Clamp(n, 1, Math.Max(1, maxCells));
        _cell = Math.Sqrt(w * h / target);
        if (!(_cell > 0) || !double.IsFinite(_cell)) _cell = Math.Max(w, h);
        _nx = Math.Max(1, (int)Math.Min(Math.Ceiling(w / _cell), maxCells));
        _ny = Math.Max(1, (int)Math.Min(Math.Ceiling(h / _cell), Math.Max(1, maxCells / _nx)));
        _cell = Math.Max(_cell, Math.Max(w / _nx, h / _ny));
        _gx0 = x0; _gy0 = y0;

        // A triangle whose box covers more than oversizeCells cells (a long hull sliver on a
        // surface with no outer boundary) goes to a list scanned on every query instead of into
        // every cell it covers, so the index stays near one entry per triangle.
        var counts = new int[_nx * _ny + 1];
        var oversize = new List<int>();
        var indexed = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var (ix0, ix1, iy0, iy1) = Cells(_minX[i], _minY[i], _maxX[i], _maxY[i]);
            if ((long)(ix1 - ix0 + 1) * (iy1 - iy0 + 1) > oversizeCells) { oversize.Add(i); continue; }
            indexed[i] = true;
            for (int iy = iy0; iy <= iy1; iy++)
                for (int ix = ix0; ix <= ix1; ix++) counts[iy * _nx + ix + 1]++;
        }
        for (int c = 1; c < counts.Length; c++) counts[c] = checked(counts[c] + counts[c - 1]);
        _cellStart = counts;
        _cellItems = new int[counts[^1]];
        _oversize = oversize.ToArray();
        var fill = new int[_nx * _ny];
        for (int i = 0; i < n; i++)
        {
            if (!indexed[i]) continue;
            var (ix0, ix1, iy0, iy1) = Cells(_minX[i], _minY[i], _maxX[i], _maxY[i]);
            for (int iy = iy0; iy <= iy1; iy++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    int c = iy * _nx + ix;
                    _cellItems[_cellStart[c] + fill[c]++] = i;
                }
        }
    }

    /// <summary>The surface's name.</summary>
    public string Name { get; }

    /// <summary>The TIN surface read.</summary>
    public ObjectId Id { get; }

    /// <summary>Native, reference or corridor.</summary>
    public SurfaceOrigin Origin { get; }

    /// <summary>Triangles kept (inside the read window).</summary>
    public int Count => _tris.Length;

    /// <summary>Visible triangles the surface returned before the read window was applied.</summary>
    public int VisibleTotal { get; }

    /// <summary>Seconds spent reading the surface.</summary>
    public double ReadSeconds { get; }

    /// <summary>Warnings raised while reading (SV-E1021).</summary>
    public IReadOnlyList<Problem> Problems { get; }

    /// <summary>
    /// Reads the visible triangles of a TIN surface. SV-E402 when the object is not a TIN surface;
    /// SV-E403 when the surface has no visible triangles at all.
    /// </summary>
    /// <param name="tr">The caller's transaction; nothing is held after the call.</param>
    /// <param name="surfaceId">A TIN surface: native, data-shortcut reference, or a corridor surface's <c>SurfaceId</c>.</param>
    /// <param name="cfg">Adapter settings.</param>
    /// <param name="readWindowMin">Lower-left of the read window, or null to keep every triangle.</param>
    /// <param name="readWindowMax">Upper-right of the read window, or null.</param>
    public static Outcome<TriangleSourceAdapter> Read(Transaction tr, ObjectId surfaceId, AdapterConfig cfg, Pt? readWindowMin = null, Pt? readWindowMax = null)
    {
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(cfg);
        if (surfaceId.IsNull || surfaceId.IsErased)
            return Outcome<TriangleSourceAdapter>.Fail(new Problem(AdapterCodes.SourceNotFound, Severity.Error, "The surface to read triangles from has been erased."));
        var obj = tr.GetObject(surfaceId, OpenMode.ForRead);
        if (obj is not TinSurface tin)
            return Outcome<TriangleSourceAdapter>.Fail(new Problem(AdapterCodes.NotTinSurface, Severity.Error,
                $"'{(obj is Autodesk.Civil.DatabaseServices.Surface s ? s.Name : obj.GetType().Name)}' is not a TIN surface; triangles are read from the base and comparison TIN surfaces (P23)."));

        var origin = SurfaceOrigin.Native;
        if (tin.IsReferenceObject) origin = SurfaceOrigin.Reference;
        else if (CivilDocument.GetCivilDocument(tin.Database) is { } civ)
        {
            foreach (ObjectId cid in civ.CorridorCollection)
            {
                if (cid.IsErased || tr.GetObject(cid, OpenMode.ForRead) is not Corridor corridor) continue;
                foreach (CorridorSurface cs in corridor.CorridorSurfaces)
                    if (cs.SurfaceId == surfaceId) origin = SurfaceOrigin.Corridor;
            }
        }

        bool windowed = readWindowMin is not null && readWindowMax is not null;
        double wx0 = 0, wy0 = 0, wx1 = 0, wy1 = 0;
        if (windowed)
        {
            wx0 = Math.Min(readWindowMin!.Value.X, readWindowMax!.Value.X); wx1 = Math.Max(readWindowMin.Value.X, readWindowMax.Value.X);
            wy0 = Math.Min(readWindowMin.Value.Y, readWindowMax.Value.Y); wy1 = Math.Max(readWindowMin.Value.Y, readWindowMax.Value.Y);
        }

        var sw = Stopwatch.StartNew();
        List<Tri3> list;
        int total = 0;
        using (var tris = tin.GetTriangles(false))
        {
            list = new List<Tri3>(windowed ? 0 : tris.Count);
            foreach (TinSurfaceTriangle t in tris)
            {
                total++;
                Point3d a, b, c;
                using (var v1 = t.Vertex1) a = v1.Location;
                using (var v2 = t.Vertex2) b = v2.Location;
                using (var v3 = t.Vertex3) c = v3.Location;
                t.Dispose();
                if (windowed && (Math.Max(a.X, Math.Max(b.X, c.X)) < wx0 || Math.Min(a.X, Math.Min(b.X, c.X)) > wx1
                              || Math.Max(a.Y, Math.Max(b.Y, c.Y)) < wy0 || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > wy1)) continue;
                list.Add(new Tri3(new Pt3(a.X, a.Y, a.Z), new Pt3(b.X, b.Y, b.Z), new Pt3(c.X, c.Y, c.Z)));
            }
        }
        double seconds = sw.Elapsed.TotalSeconds;
        if (total == 0)
            return Outcome<TriangleSourceAdapter>.Fail(new Problem(AdapterCodes.NoTriangles, Severity.Error,
                $"Surface '{tin.Name}' has no visible triangles.", Fix: "Add data to the surface or pick another one."));

        var problems = new List<Problem>();
        if (origin != SurfaceOrigin.Native)
            problems.Add(new Problem(AdapterCodes.TriangleAccessUnverified, Severity.Warning,
                $"Surface '{tin.Name}' is a {(origin == SurfaceOrigin.Reference ? "data-shortcut reference" : "corridor")} surface. Reading its triangles was not verified headless (probe P23); the integrator is checked against Civil 3D's bounded volume on every regen, so a bad read fails the run.",
                Fix: "Nothing to do unless the integrator check fails; then report the drawing."));
        return Outcome<TriangleSourceAdapter>.Ok(new TriangleSourceAdapter(tin.Name, surfaceId, origin, list.ToArray(), total, seconds, problems,
            cfg.TriangleIndexMaxCells, cfg.TriangleIndexOversizeCells), problems);
    }

    /// <inheritdoc />
    public IEnumerable<Tri3> Triangles(Pt windowMin, Pt windowMax)
    {
        if (_tris.Length == 0) return [];
        double x0 = Math.Min(windowMin.X, windowMax.X), x1 = Math.Max(windowMin.X, windowMax.X);
        double y0 = Math.Min(windowMin.Y, windowMax.Y), y1 = Math.Max(windowMin.Y, windowMax.Y);
        if (x1 < _gx0 || y1 < _gy0 || x0 > _gx0 + _nx * _cell || y0 > _gy0 + _ny * _cell) return [];
        if (++_stampValue == int.MaxValue) { Array.Clear(_stamp); _stampValue = 1; }
        int stamp = _stampValue;
        var result = new List<Tri3>();
        var (ix0, ix1, iy0, iy1) = Cells(x0, y0, x1, y1);
        for (int iy = iy0; iy <= iy1; iy++)
        {
            for (int ix = ix0; ix <= ix1; ix++)
            {
                int c = iy * _nx + ix;
                for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    int i = _cellItems[k];
                    if (_stamp[i] == stamp) continue;
                    _stamp[i] = stamp;
                    if (_maxX[i] >= x0 && _minX[i] <= x1 && _maxY[i] >= y0 && _minY[i] <= y1) result.Add(_tris[i]);
                }
            }
        }
        foreach (int i in _oversize)
            if (_maxX[i] >= x0 && _minX[i] <= x1 && _maxY[i] >= y0 && _minY[i] <= y1) result.Add(_tris[i]);
        return result;
    }

    /// <summary>Triangles kept out of the grid because their box covers too many cells; scanned on every query.</summary>
    public int OversizeCount => _oversize.Length;

    /// <summary>Every cached triangle, for reports and tests.</summary>
    public IReadOnlyList<Tri3> All => _tris;

    private (int X0, int X1, int Y0, int Y1) Cells(double minX, double minY, double maxX, double maxY) => (
        Math.Clamp((int)Math.Floor((minX - _gx0) / _cell), 0, _nx - 1),
        Math.Clamp((int)Math.Floor((maxX - _gx0) / _cell), 0, _nx - 1),
        Math.Clamp((int)Math.Floor((minY - _gy0) / _cell), 0, _ny - 1),
        Math.Clamp((int)Math.Floor((maxY - _gy0) / _cell), 0, _ny - 1));
}
