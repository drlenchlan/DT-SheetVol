// A1 adapters, contract revision 3 (rev3-adapters). Owner: A1.
//
// A user TIN volume surface as an analysis source (AnalysisDef.VolumeSurface, NOW.md X6, S4 in
// docs/api-findings.md section D, the sign in section E). Every S4 finding is absorbed here:
//   - the pair is GetVolumeProperties().BaseSurface / .ComparisonSurface (read-only ObjectIds);
//   - a lost base or comparison is silent: same command the object is still a TinVolumeSurface
//     whose BaseSurface reads IsErased, next command it opens as a plain TinSurface; so the
//     guard below runs before every read (E422);
//   - IsOutOfDate flags only after the editing command ends, so under RebuildSources the surface
//     is rebuilt on every regen (D21), else a flagged surface fails E404 naming it;
//   - GetBoundedVolumes ignores the surface's factors (raw), which Core then applies;
//   - the user's boundary on the surface clips both GetBoundedVolumes and the surface's TIN.
// Nothing here writes the surface's factors, pair or boundaries; Rebuild() is the one write.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Volumes;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace SheetVol.Civil3D.Adapters;

/// <summary>A user volume surface that passed the E422 guard, the E404 checks on its base and comparison, and the D21 rebuild rule.</summary>
/// <param name="Id">The <c>TinVolumeSurface</c>.</param>
/// <param name="Name">Its current name (may differ from the run's when matched by stored handle, SV-E445).</param>
/// <param name="Handle">Its handle, hexadecimal, for persistence.</param>
/// <param name="BaseId">Its base surface now.</param>
/// <param name="BaseName">Base surface name now.</param>
/// <param name="ComparisonId">Its comparison surface now.</param>
/// <param name="ComparisonName">Comparison surface name now.</param>
/// <param name="CutFactor">Its own cut factor (governs; the analysis's stored factor is ignored).</param>
/// <param name="FillFactor">Its own fill factor.</param>
/// <param name="Triangles">Visible triangles after the rebuild (<c>GetTinProperties().NumberOfTriangles</c>).</param>
/// <param name="Boundaries">Boundaries the user put on the volume surface (<c>BoundariesDefinition.Count</c>).</param>
/// <param name="Rebuilt">True when this call rebuilt it (RebuildSources on).</param>
public sealed record ResolvedVolumeSurface(
    ObjectId Id, string Name, string Handle,
    ObjectId BaseId, string BaseName, ObjectId ComparisonId, string ComparisonName,
    double CutFactor, double FillFactor, int Triangles, int Boundaries, bool Rebuilt);

/// <summary>A user volume surface read for the status fingerprint: no rebuild, no write (6.11, D4).</summary>
/// <param name="Id">The surface.</param>
/// <param name="Name">Its name now.</param>
/// <param name="Handle">Its handle now.</param>
/// <param name="CutFactor">Its cut factor now.</param>
/// <param name="FillFactor">Its fill factor now.</param>
/// <param name="IsOutOfDate">Civil 3D's flag (lags one command after a source edit, S4).</param>
/// <param name="Totals">Whole-surface raw totals (<c>Unadjusted*</c>), cleaned as the volume source cleans them.</param>
public sealed record VolumeSurfaceReading(ObjectId Id, string Name, string Handle, double CutFactor, double FillFactor, bool IsOutOfDate, RawVolume Totals);

/// <summary>Resolve, guard, rebuild, list and read user TIN volume surfaces (rev 3, rows E402, E404, E422, E423).</summary>
public static class UserVolumeSurfaces
{
    /// <summary>
    /// Depth d = base − comparison per unit of the Z Civil 3D stores on a TIN volume surface's
    /// TIN. MEASURED (docs/api-findings.md §E, SVADAPTERS row "E1 volume TIN sign"): base flat 100,
    /// comparison flat 98 gives Z = −2 on every vertex, so the TIN holds comparison − base and
    /// depth is its negation. SVADAPTERS fails if a host ever stores the other sign, and E1012
    /// compares the integrator on this TIN with <c>GetBoundedVolumes</c> on every regen.
    /// </summary>
    public const double DepthPerTinZ = -1.0;

    /// <summary>
    /// Resolves one analysis's volume surface by name, then by stored handle (the <see cref="StoredRef"/>
    /// rule; SV-E442, E445, E803), guards it (E422, E403), applies E404 to its base and comparison,
    /// then rebuilds the surface itself when <see cref="AdapterConfig.RebuildSources"/> is on (Info
    /// naming it, D21) or fails E404 naming it when it is flagged out of date and RebuildSources is
    /// off. Never writes its factors, pair or boundaries.
    /// </summary>
    /// <param name="tr">The caller's transaction (document locked); the rebuilds happen in it.</param>
    /// <param name="db">The drawing.</param>
    /// <param name="name">The volume surface name from the run.</param>
    /// <param name="stored">Name and handle persisted with the run, or null.</param>
    /// <param name="cfg">Adapter settings (<see cref="AdapterConfig.RebuildSources"/>, ownership tag key).</param>
    /// <param name="who">How messages name the analysis, for example "Analysis 'Cut fill'".</param>
    /// <param name="lastBase">The analysis's stored base name (the pair as last resolved), to name the lost source when the surface has already turned into a plain TIN.</param>
    /// <param name="lastComparison">The stored comparison name, likewise.</param>
    public static Outcome<ResolvedVolumeSurface> Resolve(Transaction tr, Database db, string name, StoredRef? stored, AdapterConfig cfg, string who,
        string? lastBase = null, string? lastComparison = null)
    {
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(cfg);
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return Outcome<ResolvedVolumeSurface>.Fail(new Problem(AdapterCodes.SourceNotFound, Severity.Error, "The drawing has no Civil 3D document."));
        string role = $"{who}: volume surface";
        var found = AnalysisResolver.FindSurface(tr, db, civ, name, stored, role,
            "Restore the volume surface or pick another source in SVANALYSIS; nothing was changed.");
        if (!found.Succeeded) return Outcome<ResolvedVolumeSurface>.Fail(found.Problems);
        var notes = found.Problems.ToList();
        var id = found.Value;
        var obj = tr.GetObject(id, OpenMode.ForRead);
        string current = obj is CivSurface cs ? cs.Name : name;

        var bad = Guard(tr, civ, obj, cfg, role, current, lastBase, lastComparison, out var vol, out var props);
        if (bad is not null) return Outcome<ResolvedVolumeSurface>.Fail(notes.Append(bad));

        // E404 on the pair first: rebuilding the volume surface over a stale source would compute on stale data.
        foreach (var (label, sid) in new[] { ("base surface", props!.BaseSurface), ("comparison surface", props.ComparisonSurface) })
        {
            var src = tr.GetObject(sid, OpenMode.ForRead);
            if (src is TinSurface tin)
            {
                var c = AnalysisResolver.CheckCurrent(tr, civ, tin, sid, $"{role} '{current}': {label}", cfg);
                notes.AddRange(c.Problems);
                if (!c.Succeeded) return Outcome<ResolvedVolumeSurface>.Fail(notes);
            }
            else if (src is CivSurface other && other.IsOutOfDate)
                return Outcome<ResolvedVolumeSurface>.Fail(notes.Append(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                    $"{role} '{current}': {label} '{other.Name}' is out of date and is not a TIN surface; SheetVol rebuilds TIN sources only.",
                    Fix: "Rebuild it in Civil 3D, then regenerate.")));
        }

        // D21: under RebuildSources the surface is rebuilt on every regen (IsOutOfDate lags one command, S4).
        bool rebuilt = false;
        if (cfg.RebuildSources)
        {
            if (tr.GetObject(vol!.LayerId, OpenMode.ForRead) is LayerTableRecord { IsLocked: true } locked)
                return Outcome<ResolvedVolumeSurface>.Fail(notes.Append(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                    $"{role} '{current}' sits on locked layer '{locked.Name}'; SheetVol rebuilds a volume-surface source on every regen and does not rebuild a surface it does not own on a locked layer.",
                    Fix: "Unlock its layer, or turn RebuildSources off and keep the surface current yourself, then regenerate.")));
            vol.UpgradeOpen();
            vol.Rebuild();
            rebuilt = true;
            notes.Add(new Problem(AdapterCodes.SourceOutOfDate, Severity.Info,
                $"{role} '{current}' was rebuilt (RebuildSources on, D21): Civil 3D flags a volume surface out of date only after the command that edited a source ends, so SheetVol rebuilds it on every regen. Its factors, base, comparison and boundaries were not written."));
            DBObject rebuiltObj = vol;
            bad = Guard(tr, civ, rebuiltObj, cfg, role, current, lastBase, lastComparison, out vol, out props);
            if (bad is not null) return Outcome<ResolvedVolumeSurface>.Fail(notes.Append(bad));
        }
        else if (vol!.IsOutOfDate)
            return Outcome<ResolvedVolumeSurface>.Fail(notes.Append(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                $"{role} '{current}' is out of date and RebuildSources is off.",
                Fix: "Rebuild the volume surface, or turn RebuildSources on, then regenerate.")));
        else
            notes.Add(new Problem(AdapterCodes.SourceOutOfDate, Severity.Warning,
                $"{role} '{current}' was not rebuilt (RebuildSources off). Civil 3D flags a volume surface out of date only after the command that edited a source ends, so its numbers are those of its last rebuild.",
                Fix: "Turn RebuildSources on (the default) so every regen rebuilds it."));

        int triangles = AnalysisResolver.TinTriangles(vol!);
        if (triangles == 0)
            return Outcome<ResolvedVolumeSurface>.Fail(notes.Append(new Problem(AdapterCodes.NoTriangles, Severity.Error,
                $"{role} '{current}' has no triangles: its base and comparison do not overlap anywhere.",
                Fix: "Check that the two surfaces cover the same ground.")));
        if (!TryFactors(vol!, out double cut, out double fill, out string? why))
            return Outcome<ResolvedVolumeSurface>.Fail(notes.Append(new Problem(AdapterCodes.VolumeSurfaceLost, Severity.Error,
                $"{role} '{current}': its factors cannot be read ({why}); Civil 3D does this when a source was erased. SheetVol never computes on it.",
                Fix: "Restore the erased source (UNDO) or pick another source in SVANALYSIS.")));
        int boundaries = vol!.BoundariesDefinition.Count;
        string baseName = NameOf(tr, props!.BaseSurface), compName = NameOf(tr, props.ComparisonSurface);
        if (cut != 1.0 || fill != 1.0 || boundaries > 0)
            notes.Add(new Problem(AdapterCodes.VolumeSurfaceSource, Severity.Info,
                $"{who} takes volume surface '{current}' ({baseName} vs {compName}) as its source: its own factors cut {R(cut)} / fill {R(fill)} govern and the analysis's stored factors are ignored"
                + (boundaries > 0 ? $"; its {boundaries} boundary(ies) clip the bounded volumes and the integrator alike." : ".")));
        return Outcome<ResolvedVolumeSurface>.Ok(new ResolvedVolumeSurface(id, current, vol.Handle.ToString(),
            props.BaseSurface, baseName, props.ComparisonSurface, compName, cut, fill, triangles, boundaries, rebuilt), notes);
    }

    /// <summary>
    /// The drawing's user TIN volume surfaces for the dialog and SVANALYSIS (contract
    /// <see cref="VolumeSurfaceInfo"/>): never SheetVol-owned (tagged) ones. A surface that fails
    /// the guard (base or comparison erased, no triangles) is listed with SourcesIntact false;
    /// its erased source's name is read from the erased object, and factors that Civil 3D cannot
    /// read any more are NaN. Read-only. A surface that has already turned into a plain TIN
    /// (the command after the erase, S4) is no longer a volume surface and is not listed.
    /// </summary>
    public static IReadOnlyList<VolumeSurfaceInfo> List(Transaction tr, Database db, AdapterConfig cfg)
    {
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return [];
        var list = new List<VolumeSurfaceInfo>();
        foreach (ObjectId id in civ.GetSurfaceIds())
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not TinVolumeSurface v) continue;
            if (OwnershipTag.Read(tr, v, cfg) is not null) continue;
            bool intact = Guard(tr, civ, v, cfg, "", v.Name, null, null, out _, out _) is null;
            string b = "", c = "";
            if (AnalysisResolver.TryVolumeProperties(v, out var p))
            {
                b = NameOf(tr, p!.BaseSurface);
                c = NameOf(tr, p.ComparisonSurface);
            }
            if (!intact || !TryFactors(v, out double cut, out double fill, out _)) (cut, fill) = (double.NaN, double.NaN);
            list.Add(new VolumeSurfaceInfo(v.Name, b, c, cut, fill, intact));
        }
        return list.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Read-only state for GetRunStatus and SVSTATUS (D4): name, handle, factors, Civil's
    /// out-of-date flag and the whole-surface raw totals as they stand; no rebuild. Null when the
    /// surface is missing or fails the guard (the fingerprint then writes it as missing, which
    /// compares stale).
    /// </summary>
    public static VolumeSurfaceReading? Read(Transaction tr, Database db, string name, string? storedHandle, AdapterConfig cfg)
    {
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return null;
        var stored = string.IsNullOrWhiteSpace(storedHandle) ? null : new StoredRef(name, storedHandle);
        var found = AnalysisResolver.FindSurface(tr, db, civ, name, stored, "volume surface", "");
        if (!found.Succeeded) return null;
        var obj = tr.GetObject(found.Value, OpenMode.ForRead);
        if (Guard(tr, civ, obj, cfg, "", name, null, null, out var vol, out _) is not null) return null;
        if (!TryFactors(vol!, out double cut, out double fill, out _)) return null;
        var src = VolumeSourceAdapter.Open(tr, found.Value, cfg);
        return src.Succeeded ? new VolumeSurfaceReading(found.Value, vol!.Name, vol.Handle.ToString(), cut, fill, vol.IsOutOfDate, src.Value.SurfaceTotals) : null;
    }

    /// <summary>
    /// The <see cref="IVolumeSource"/> on a resolved user surface: <see cref="VolumeSourceAdapter"/>
    /// (raw <c>GetBoundedVolumes</c>, P1 sign, P3 unadjusted, noise clamp, datum-method Area2D),
    /// with the E422 check repeated before every read. Valid only while the caller's transaction is open.
    /// </summary>
    public static Outcome<IVolumeSource> OpenVolumes(Transaction tr, ResolvedVolumeSurface surface, AdapterConfig cfg)
    {
        var o = VolumeSourceAdapter.Open(tr, surface.Id, cfg);
        if (!o.Succeeded) return Outcome<IVolumeSource>.Fail(o.Problems);
        return Outcome<IVolumeSource>.Ok(new GuardedVolumeSource(o.Value, () =>
            surface.Id.IsErased ? $"volume surface '{surface.Name}' was erased"
            : surface.BaseId.IsErased ? $"the base surface '{surface.BaseName}' of volume surface '{surface.Name}' was erased"
            : surface.ComparisonId.IsErased ? $"the comparison surface '{surface.ComparisonName}' of volume surface '{surface.Name}' was erased"
            : null), o.Problems);
    }

    /// <summary>
    /// A <c>TinSurface</c> wrapper over the same native object as <paramref name="vol"/>.
    /// <c>TinVolumeSurface</c> exposes no triangle API (P23), but its native class is the TIN
    /// surface's (<c>AeccDbSurfaceTin</c>, S4 D5), so <c>TinSurface.GetTriangles</c> reads the volume
    /// surface's own TIN (MEASURED, docs/api-findings.md §E). The wrapper does not own the object:
    /// never dispose it (6.13), keep <paramref name="vol"/> alive while it is used.
    /// </summary>
    internal static TinSurface TinView(TinVolumeSurface vol) =>
        (TinSurface)Autodesk.AutoCAD.Runtime.DisposableWrapper.Create(typeof(TinSurface), vol.UnmanagedObject, false);

    /// <summary>
    /// The E422 guard, run before every read: still a <c>TinVolumeSurface</c> (and
    /// <c>IsVolumeSurface</c>), not SheetVol's own, volume properties readable (else E403), base and
    /// comparison not erased. Null when the surface may be read.
    /// </summary>
    private static Problem? Guard(Transaction tr, CivilDocument civ, DBObject obj, AdapterConfig cfg, string who, string name,
        string? lastBase, string? lastComparison, out TinVolumeSurface? vol, out VolumeSurfaceProperties? props)
    {
        vol = null;
        props = null;
        string label = who.Length == 0 ? $"Volume surface '{name}'" : $"{who} '{name}'";
        if (obj is not TinVolumeSurface v || !v.IsVolumeSurface)
        {
            if (obj is TinSurface)
                return new Problem(AdapterCodes.VolumeSurfaceLost, Severity.Error,
                    $"{label} is no longer a TIN volume surface: Civil 3D turns a volume surface into a plain TIN surface once its base or comparison is erased{LastPair(tr, civ, lastBase, lastComparison)}. SheetVol never computes on it.",
                    Fix: "Restore the erased surface (UNDO), or make a new volume surface and pick it in SVANALYSIS Volume.");
            string what = obj switch
            {
                GridVolumeSurface => "a grid volume surface",
                GridSurface => "a grid surface",
                _ => $"a {obj.GetType().Name}",
            };
            return new Problem(AdapterCodes.VolumeSurfaceLost, Severity.Error,
                $"{label} is {what}, not a TIN volume surface.", Fix: "Pick a TIN volume surface in SVANALYSIS Volume, or use a base and comparison pair.");
        }
        if (OwnershipTag.Read(tr, v, cfg) is { } tag)
            return new Problem(AdapterCodes.VolumeSurfaceLost, Severity.Error,
                $"{label} is SheetVol's own volume surface ('{tag.Name}', kept for a base and comparison pair); a volume-surface source must be one the user made.",
                Fix: "Give this analysis the base and comparison as a pair, or pick a volume surface you created.");
        if (!AnalysisResolver.TryVolumeProperties(v, out var p))
            return new Problem(AdapterCodes.NoTriangles, Severity.Error,
                $"{label} has no triangles, so its base and comparison cannot be read.",
                Fix: "Rebuild the volume surface in Civil 3D, or check that its base and comparison overlap.");
        var lost = new List<string>();
        if (p!.BaseSurface.IsNull || p.BaseSurface.IsErased) lost.Add($"base surface '{NameOf(tr, p.BaseSurface)}'");
        if (p.ComparisonSurface.IsNull || p.ComparisonSurface.IsErased) lost.Add($"comparison surface '{NameOf(tr, p.ComparisonSurface)}'");
        if (lost.Count > 0)
            return new Problem(AdapterCodes.VolumeSurfaceLost, Severity.Error,
                $"{label}: its {string.Join(" and its ", lost)} {(lost.Count == 1 ? "was" : "were")} erased. Civil 3D reports nothing (S4); SheetVol never computes on it.",
                Fix: "Restore the erased surface (UNDO), or pick another source in SVANALYSIS.");
        vol = v;
        props = p;
        return null;
    }

    /// <summary>" (last resolved pair: base 'EG', comparison 'FG_A'; not in the drawing now: base 'EG')", or "" without a stored pair.</summary>
    private static string LastPair(Transaction tr, CivilDocument civ, string? lastBase, string? lastComparison)
    {
        if (string.IsNullOrWhiteSpace(lastBase) && string.IsNullOrWhiteSpace(lastComparison)) return "";
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (ObjectId id in civ.GetSurfaceIds())
            if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is CivSurface s) live.Add(s.Name);
        var missing = new List<string>();
        if (!string.IsNullOrWhiteSpace(lastBase) && !live.Contains(lastBase)) missing.Add($"base '{lastBase}'");
        if (!string.IsNullOrWhiteSpace(lastComparison) && !live.Contains(lastComparison)) missing.Add($"comparison '{lastComparison}'");
        return $" (last resolved pair: base '{lastBase}', comparison '{lastComparison}'"
            + (missing.Count > 0 ? $"; not in the drawing now: {string.Join(", ", missing)})" : ")");
    }

    /// <summary>The surface's factors; the getters throw <c>CivilException</c> once a source is erased (S4 D5).</summary>
    private static bool TryFactors(TinVolumeSurface v, out double cut, out double fill, out string? why)
    {
        try
        {
            cut = v.CutFactor;
            fill = v.FillFactor;
            why = null;
            return true;
        }
        catch (System.Exception ex) when (ex is Autodesk.Civil.CivilException or InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception)
        {
            cut = fill = double.NaN;
            why = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>A surface's name, read from the erased object when it was erased; the handle when even that fails.</summary>
    private static string NameOf(Transaction tr, ObjectId id)
    {
        if (id.IsNull) return "(none)";
        try
        {
            return tr.GetObject(id, OpenMode.ForRead, true) is CivSurface s ? s.Name : id.Handle.ToString();
        }
        catch (System.Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or Autodesk.Civil.CivilException or InvalidOperationException)
        {
            return id.Handle.ToString();
        }
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>A volume source that repeats the E422 check before every read (a source erased mid-transaction is a defect: it throws).</summary>
public sealed class GuardedVolumeSource(IVolumeSource inner, Func<string?> lost) : IVolumeSource
{
    /// <inheritdoc />
    public RawVolume SurfaceTotals { get { Check(); return inner.SurfaceTotals; } }

    /// <inheritdoc />
    public RawVolume Bounded(IReadOnlyList<Pt> closedPolygon) { Check(); return inner.Bounded(closedPolygon); }

    /// <inheritdoc />
    public double Area2D(IReadOnlyList<Pt> closedPolygon) { Check(); return inner.Area2D(closedPolygon); }

    private void Check()
    {
        if (lost() is { } why) throw new InvalidOperationException($"{AdapterCodes.VolumeSurfaceLost} {why}; SheetVol never computes on it.");
    }
}
