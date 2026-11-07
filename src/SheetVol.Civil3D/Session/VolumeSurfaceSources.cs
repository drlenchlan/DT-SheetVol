// Contract revision 3 (rev3-adapters). Owner: A1 (port shim in A4's Session folder).
//
// IVolumeSurfaceSources over A1's UserVolumeSurfaces, TriangleSourceAdapter.ReadVolumeDepth and
// VolumeSourceAdapter: a thin shim in the shape of Integration/CivilSources. Every S4 quirk stays in
// Adapters/UserVolumeSurfaces.cs. What the shim adds is the transaction-lifetime guard on the volume
// source (A4's rule: nothing an adapter returns outlives its transaction).

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.Adapters;
using SheetVol.Core;
using SheetVol.Core.Tracking;
using SheetVol.Core.Volumes;

namespace SheetVol.Civil3D.Session;

/// <summary>One transaction's view of the drawing's user TIN volume surfaces through A1 (rev 3).</summary>
public sealed class VolumeSurfaceSources : IVolumeSurfaceSources
{
    private readonly Database _db;
    private readonly Transaction _tr;
    private readonly SheetVolSettings _settings;
    private readonly TransactionLifetime _life;

    /// <summary>Opens the port inside <paramref name="tr"/>, which must be the top transaction.</summary>
    public VolumeSurfaceSources(Database db, Transaction tr, SheetVolSettings settings)
    {
        _db = db;
        _tr = tr;
        _settings = settings;
        _life = new TransactionLifetime(tr);
    }

    /// <inheritdoc />
    public IReadOnlyList<VolumeSurfaceInfo> List() => UserVolumeSurfaces.List(_tr, _db, _settings.Adapters);

    /// <inheritdoc />
    public Outcome<VolumeSurfaceSource> Resolve(string name, string? storedHandle, bool rebuildSources, AnalysisDef analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var cfg = _settings.Adapters with { RebuildSources = rebuildSources };
        var stored = string.IsNullOrWhiteSpace(storedHandle) ? null : new StoredRef(name, storedHandle);
        var resolved = UserVolumeSurfaces.Resolve(_tr, _db, name, stored, cfg, $"Analysis '{analysis.Name}'", analysis.BaseSurface, analysis.ComparisonSurface);
        if (!resolved.Succeeded) return Outcome<VolumeSurfaceSource>.Fail(resolved.Problems);
        var r = resolved.Value;
        var problems = resolved.Problems.ToList();
        var volumes = UserVolumeSurfaces.OpenVolumes(_tr, r, cfg);
        problems.AddRange(volumes.Problems);
        if (!volumes.Succeeded) return Outcome<VolumeSurfaceSource>.Fail(problems);
        // Every visible triangle of the surface's own TIN, read once per regen (P23), Z as depth.
        var depth = TriangleSourceAdapter.ReadVolumeDepth(_tr, r.Id, cfg);
        problems.AddRange(depth.Problems);
        if (!depth.Succeeded) return Outcome<VolumeSurfaceSource>.Fail(problems);
        return Outcome<VolumeSurfaceSource>.Ok(new VolumeSurfaceSource(r.Name, r.Handle, r.BaseName, r.ComparisonName, r.CutFactor, r.FillFactor,
            new LifetimeGuardedVolumes(volumes.Value, _life, r.Name), depth.Value, r.Rebuilt), problems);
    }

    /// <inheritdoc />
    public AnalysisSurfaceState? ReadState(Guid analysisId, string name, string? storedHandle)
    {
        var reading = UserVolumeSurfaces.Read(_tr, _db, name, storedHandle, _settings.Adapters);
        return reading is null ? null
            : new AnalysisSurfaceState(analysisId, reading.IsOutOfDate, reading.Totals,
                new VolumeSurfaceSourceState(reading.Name, reading.Handle, reading.CutFactor, reading.FillFactor));
    }
}
