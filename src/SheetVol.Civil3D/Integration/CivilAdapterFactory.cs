// Wave 2 integration. A4's IAdapterFactory / IDrawingSources over A1's adapters, a thin shim:
// every quirk (P1-P23) stays in Adapters/. What the shim adds is the transaction-lifetime
// guard A4 adopted (TransactionLifetime: A1's own IsDisposed guard does not fire after a
// commit, MEASURED by A4) and the reading of a manual segment's polyline, which no A1 entry
// point covers.

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using SheetVol.Civil3D.Adapters;
using SheetVol.Civil3D.Session;
using SheetVol.Core;
using SheetVol.Core.Planning;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivTinVolumeSurface = Autodesk.Civil.DatabaseServices.TinVolumeSurface;

namespace SheetVol.Civil3D.Integration;

/// <summary>The installed A1 adapters.</summary>
public sealed class CivilAdapterFactory : IAdapterFactory
{
    /// <inheritdoc />
    public IDrawingSources Open(Database db, Transaction tr, SheetVolSettings settings) => new CivilSources(db, tr, settings);

    /// <inheritdoc />
    public IVolumeSurfaceSources OpenVolumeSurfaces(Database db, Transaction tr, SheetVolSettings settings) => new VolumeSurfaceSources(db, tr, settings);
}

/// <summary>One transaction's view of the drawing through A1.</summary>
internal sealed class CivilSources(Database db, Transaction tr, SheetVolSettings settings) : IDrawingSources
{
    private readonly TransactionLifetime _life = new(tr);
    private readonly Dictionary<string, ResolvedSource> _resolved = new(StringComparer.Ordinal);
    private Outcome<DrawingUnitInfo>? _units;

    private Outcome<DrawingUnitInfo> UnitInfo() => _units ??= DrawingUnitsReader.Read(db, settings.Report.Volumes.Constants);

    /// <inheritdoc />
    public Outcome<DrawingUnits> Units()
    {
        var u = UnitInfo();
        return u.Succeeded ? Outcome<DrawingUnits>.Ok(u.Value.VolumeUnits, u.Problems) : Outcome<DrawingUnits>.Fail(u.Problems);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AlignmentNames()
    {
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return [];
        var names = new List<string>();
        foreach (ObjectId id in civ.GetAlignmentIds())
            if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is CivAlignment a) names.Add(a.Name);
        return names.Order(StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> TinSurfaceNames()
    {
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return [];
        var names = new List<string>();
        foreach (ObjectId id in civ.GetSurfaceIds())
        {
            if (id.IsErased) continue;
            var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
            // TIN surfaces only (E402): grid and volume surfaces, the tool's own included, are never offered.
            if (s is CivTinSurface) names.Add(s.Name);
        }
        return names.Order(StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public Outcome<AlignmentSource> Alignment(string name, string? storedHandle)
    {
        var u = UnitInfo();
        if (!u.Succeeded) return Outcome<AlignmentSource>.Fail(u.Problems);
        var geometry = settings.Planning.ToOptions(u.Value.DrawingUnitsPerFoot).Geometry;
        var stored = string.IsNullOrWhiteSpace(storedHandle) ? null : new StoredRef(name, storedHandle);
        var found = AlignmentAdapter.Find(tr, db, name, stored, u.Value, settings.Adapters, geometry);
        if (!found.Succeeded) return Outcome<AlignmentSource>.Fail(found.Problems);
        var adapter = found.Value;
        bool reference = tr.GetObject(adapter.Id, OpenMode.ForRead) is CivAlignment al && al.IsReferenceObject;
        return Outcome<AlignmentSource>.Ok(new AlignmentSource(new LifetimeGuardedAlignment(adapter, _life), adapter.Handle, reference), found.Problems);
    }

    /// <inheritdoc />
    public Outcome<ManualOutline> ManualOutline(string polylineHandle)
    {
        if (!TryResolve(polylineHandle, out var id) || tr.GetObject(id, OpenMode.ForRead) is not Polyline pl)
            return Outcome<ManualOutline>.Fail(new Problem(ProblemCodes.E311, Severity.Error,
                $"The manual segment's polyline {polylineHandle} was erased or is not a lightweight polyline.",
                Fix: "Draw a closed polyline and attach it to the segment again (SVBREAK Manual)."));
        // Lightweight polyline vertices are in its OCS: to WCS through its plane (6.13 geometry in WCS).
        var toWorld = Matrix3d.PlaneToWorld(pl.Normal) * Matrix3d.Displacement(pl.Normal * pl.Elevation);
        var vertices = new List<PolyVertex>(pl.NumberOfVertices);
        for (int i = 0; i < pl.NumberOfVertices; i++)
        {
            var p = new Point3d(pl.GetPoint2dAt(i).X, pl.GetPoint2dAt(i).Y, 0).TransformBy(toWorld);
            double bulge = pl.GetBulgeAt(i);
            vertices.Add(new PolyVertex(new Pt(p.X, p.Y), pl.Normal.Z < 0 ? -bulge : bulge));
        }
        return Outcome<ManualOutline>.Ok(new ManualOutline(vertices, pl.Closed));
    }

    /// <inheritdoc />
    public Outcome<SurfaceSource> Surface(string name, string? storedHandle, bool rebuildIfOutOfDate)
    {
        var cfg = settings.Adapters with { RebuildSources = rebuildIfOutOfDate };
        var stored = string.IsNullOrWhiteSpace(storedHandle) ? null : new StoredRef(name, storedHandle);
        var resolved = AnalysisResolver.ResolveSource(tr, db, name, stored, "Surface", cfg);
        if (!resolved.Succeeded) return Outcome<SurfaceSource>.Fail(resolved.Problems);
        var r = resolved.Value;
        _resolved[name] = r;
        // Every visible triangle, read once per regen (P23: about 1 M per 1.1-1.5 s). The integrator
        // asks for one segment window at a time from the adapter's grid index.
        var triangles = TriangleSourceAdapter.Read(tr, r.Id, settings.Adapters);
        if (!triangles.Succeeded) return Outcome<SurfaceSource>.Fail(resolved.Problems.Concat(triangles.Problems));
        return Outcome<SurfaceSource>.Ok(new SurfaceSource(r.Name, r.Handle, triangles.Value, r.Origin == SurfaceOrigin.Reference, r.Rebuilt),
            resolved.Problems.Concat(triangles.Problems));
    }

    /// <inheritdoc />
    public Outcome<OwnedVolumeSurface> EnsureVolumeSurface(OwnedSurfacePlan plan, string? storedHandle)
    {
        var problems = new List<Problem>();
        ResolvedPair pair;
        if (_resolved.TryGetValue(plan.BaseSurface, out var b) && _resolved.TryGetValue(plan.ComparisonSurface, out var c))
        {
            if (b.Id == c.Id)
                return Outcome<OwnedVolumeSurface>.Fail(new Problem(AdapterCodes.SameSurface, Severity.Error,
                    $"Base and comparison are the same surface ('{b.Name}').", Fix: "Pick a different comparison surface."));
            pair = new ResolvedPair(b, c);
        }
        else
        {
            var resolved = AnalysisResolver.ResolvePair(tr, db, plan.BaseSurface, plan.ComparisonSurface, null, null, settings.Adapters);
            if (!resolved.Succeeded) return Outcome<OwnedVolumeSurface>.Fail(resolved.Problems);
            problems.AddRange(resolved.Problems);
            pair = resolved.Value;
        }
        // P18: EnsureOwnedSurface always calls Rebuild() on the owned surface, and writes the factors.
        var owned = AnalysisResolver.EnsureOwnedSurface(tr, db, pair, plan.Name, plan.CutFactor, plan.FillFactor, storedHandle, settings.Adapters);
        problems.AddRange(owned.Problems);
        if (!owned.Succeeded) return Outcome<OwnedVolumeSurface>.Fail(problems);
        var volumes = VolumeSourceAdapter.Open(tr, owned.Value.Id, settings.Adapters);
        problems.AddRange(volumes.Problems);
        if (!volumes.Succeeded) return Outcome<OwnedVolumeSurface>.Fail(problems);
        return Outcome<OwnedVolumeSurface>.Ok(new OwnedVolumeSurface(owned.Value.Name, owned.Value.Handle,
            new LifetimeGuardedVolumes(volumes.Value, _life, owned.Value.Name), owned.Value.Created), problems);
    }

    /// <inheritdoc />
    public AnalysisSurfaceState? ReadVolumeSurfaceState(Guid analysisId, OwnedSurfacePlan plan, string? storedHandle)
    {
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return null;
        CivTinVolumeSurface? owned = null;
        if (TryResolve(storedHandle, out var hid) && tr.GetObject(hid, OpenMode.ForRead) is CivTinVolumeSurface byHandle && IsOwned(byHandle))
            owned = byHandle;
        if (owned is null)
            foreach (ObjectId id in civ.GetSurfaceIds())
                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is CivTinVolumeSurface v && v.Name == plan.Name && IsOwned(v)) { owned = v; break; }
        if (owned is null) return null;
        // Read only: no rebuild (P18 means these totals can lag a source edit until the next regen rebuilds them).
        var volumes = VolumeSourceAdapter.Open(tr, owned.ObjectId, settings.Adapters);
        return volumes.Succeeded ? new AnalysisSurfaceState(analysisId, owned.IsOutOfDate, volumes.Value.SurfaceTotals) : null;

        bool IsOwned(CivTinVolumeSurface v) => OwnershipTag.Read(tr, v, settings.Adapters) is { } tag && tag.Name == v.Name;
    }

    private bool TryResolve(string? handle, out ObjectId id)
    {
        id = ObjectId.Null;
        if (string.IsNullOrWhiteSpace(handle) || !long.TryParse(handle, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long value)) return false;
        if (!db.TryGetObjectId(new Handle(value), out var found) || found.IsNull || found.IsErased || !found.IsValid) return false;
        id = found;
        return true;
    }
}
