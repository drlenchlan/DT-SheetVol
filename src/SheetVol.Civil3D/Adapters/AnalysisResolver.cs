// A1 adapters. Owner: A1.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Volumes;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace SheetVol.Civil3D.Adapters;

/// <summary>Where a TIN surface's data comes from.</summary>
public enum SurfaceOrigin
{
    /// <summary>A TIN surface defined in this drawing.</summary>
    Native,

    /// <summary>A data-shortcut reference (<c>IsReferenceObject</c>); read-only here, never rebuilt by SheetVol.</summary>
    Reference,

    /// <summary>The TIN surface of a corridor surface; rebuilt only through its corridor.</summary>
    Corridor,
}

/// <summary>
/// A source reference as persisted with a run (6.9: by handle and name): the name the run used
/// when the handle was stored. The handle stands in for a missing name only while the run still
/// asks for that same name, so editing a run to another (or a misspelled) name never falls back to
/// the previous object.
/// </summary>
public sealed record StoredRef(string Name, string? Handle);

/// <summary>What the persistence store (A2) remembers for one analysis: its sources and the owned volume surface.</summary>
public sealed record StoredAnalysisRefs(StoredRef? Base, StoredRef? Comparison, string? OwnedHandle);

/// <summary>A base or comparison surface that passed every check (E402, E403, E404).</summary>
/// <param name="Id">The TIN surface.</param>
/// <param name="Name">Its current name (may differ from the run's when it was matched by handle, SV-E445).</param>
/// <param name="Handle">Its handle, hexadecimal, for persistence.</param>
/// <param name="Origin">Native, reference or corridor.</param>
/// <param name="Triangles">Visible triangles (<c>GetTinProperties().NumberOfTriangles</c>).</param>
/// <param name="Rebuilt">True when SheetVol rebuilt it because it was out of date (E404).</param>
public sealed record ResolvedSource(ObjectId Id, string Name, string Handle, SurfaceOrigin Origin, int Triangles, bool Rebuilt);

/// <summary>A resolved base/comparison pair.</summary>
public sealed record ResolvedPair(ResolvedSource Base, ResolvedSource Comparison);

/// <summary>The owned volume surface for one pair, rebuilt and carrying the factors.</summary>
/// <param name="Id">The <c>TinVolumeSurface</c>.</param>
/// <param name="Name">Its name: <c>SV-&lt;base&gt;-&lt;comparison&gt;</c>, or with a suffix after a collision (E412).</param>
/// <param name="Handle">Its handle, for persistence.</param>
/// <param name="Sources">The pair it compares.</param>
/// <param name="Created">True when this call created it.</param>
/// <param name="CutFactor">Cut factor now on the surface.</param>
/// <param name="FillFactor">Fill factor now on the surface.</param>
/// <param name="Triangles">Its triangles after the rebuild.</param>
public sealed record OwnedSurface(ObjectId Id, string Name, string Handle, ResolvedPair Sources, bool Created,
    double CutFactor, double FillFactor, int Triangles);

/// <summary>One analysis bound to its owned volume surface. Two analyses that share a pair share the surface (E411).</summary>
public sealed record AnalysisBinding(AnalysisDef Analysis, OwnedSurface Owned)
{
    /// <summary>What to persist for this analysis after a successful regen.</summary>
    public StoredAnalysisRefs ToStored() => new(
        new StoredRef(Owned.Sources.Base.Name, Owned.Sources.Base.Handle),
        new StoredRef(Owned.Sources.Comparison.Name, Owned.Sources.Comparison.Handle),
        Owned.Handle);
}

/// <summary>
/// Resolves an analysis's surfaces and keeps its owned volume surface (HANDOFF.md 6.5, 6.10
/// step 3; rows E401 to E404, E411, E412, E803).
/// <list type="bullet">
/// <item>Sources are found by name, then by stored handle, and the stored handle counts only while
/// it was stored for the name the run asks for (<see cref="StoredRef"/>). Matched by name while that
/// handle is gone or elsewhere: SV-E803 warning (re-referenced). Matched only by handle: SV-E445
/// warning (renamed). Neither: SV-E442 error.</item>
/// <item>A source must be a TIN surface (grid and volume surfaces: SV-E402), have triangles
/// (SV-E403), and be current (SV-E404: a native surface is rebuilt when
/// <see cref="AdapterConfig.RebuildSources"/> is on, otherwise the run fails naming it; a stale or
/// out-of-date reference or corridor surface always fails). Base and comparison must differ (SV-E401).</item>
/// <item>The owned surface is the volume surface tagged with an <see cref="OwnershipTag"/> whose name
/// still matches the tag and whose base and comparison are the resolved pair. Erased, renamed or
/// re-sourced: a new one is created (SV-E412) and the old one is left untouched. A name taken by a
/// surface the tool does not own gets a suffix (SV-E412). A volume surface without the tag is never
/// opened for write.</item>
/// <item>On every call the owned surface gets the analysis factors and is rebuilt: probe P18
/// showed <c>IsOutOfDate</c> stays false while a volume surface returns stale totals.</item>
/// </list>
/// All work runs in the caller's transaction. Aborting it removes an owned surface created here
/// (MEASURED with SVADAPTERS_ABORT); rollback of a factor write or a rebuild was not measured.
/// </summary>
public static class AnalysisResolver
{
    /// <summary>
    /// Every pair analysis of a run bound to its owned volume surface: plans the pairs with C2's
    /// <see cref="OwnedVolumeSurfaces.Plan"/>, resolves each pair, ensures and rebuilds its surface.
    /// Rev 3: an analysis with <see cref="AnalysisDef.VolumeSurface"/> set owns no surface and gets no
    /// binding here; <see cref="UserVolumeSurfaces.Resolve"/> resolves it.
    /// </summary>
    /// <param name="tr">The caller's transaction (document locked).</param>
    /// <param name="db">The drawing.</param>
    /// <param name="analyses">The run's analyses.</param>
    /// <param name="stored">Persisted references by analysis id, or null on a first run.</param>
    /// <param name="cfg">Adapter settings.</param>
    /// <param name="volumeConfig">C2's settings: owned-surface name prefix and separator.</param>
    public static Outcome<IReadOnlyList<AnalysisBinding>> EnsureAnalyses(Transaction tr, Database db, IReadOnlyList<AnalysisDef> analyses,
        IReadOnlyDictionary<Guid, StoredAnalysisRefs>? stored, AdapterConfig cfg, VolumeConfig volumeConfig)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        analyses = analyses.Where(a => a.VolumeSurface is null).ToList();
        var cfgProblems = cfg.Validate();
        if (cfgProblems.Count > 0) return Outcome<IReadOnlyList<AnalysisBinding>>.Fail(cfgProblems);
        var plan = OwnedVolumeSurfaces.Plan(analyses, volumeConfig);
        if (!plan.Succeeded) return Outcome<IReadOnlyList<AnalysisBinding>>.Fail(plan.Problems);

        var problems = new List<Problem>(plan.Problems);
        var bindings = new List<AnalysisBinding>();
        foreach (var p in plan.Value)
        {
            var first = analyses.First(a => a.Id == p.AnalysisIds[0]);
            StoredAnalysisRefs? s = null;
            foreach (var id in p.AnalysisIds)
                if (stored is not null && stored.TryGetValue(id, out var r)) { s = r; break; }

            var pair = ResolvePair(tr, db, p.BaseSurface, p.ComparisonSurface, s?.Base, s?.Comparison, cfg, first.Name);
            problems.AddRange(pair.Problems);
            if (!pair.Succeeded) continue;
            var owned = EnsureOwnedSurface(tr, db, pair.Value, p.Name, p.CutFactor, p.FillFactor, s?.OwnedHandle, cfg);
            problems.AddRange(owned.Problems);
            if (!owned.Succeeded) continue;
            foreach (var id in p.AnalysisIds) bindings.Add(new AnalysisBinding(analyses.First(a => a.Id == id), owned.Value));
        }

        // C2 groups pairs by exact names; two spellings of the same surfaces ("EG" and "eg") resolve
        // to one owned surface, which keeps the factors written last. Say so, and report those factors.
        foreach (var shared in bindings.GroupBy(x => x.Owned.Id).Where(g => g.Select(x => (x.Analysis.BaseSurface, x.Analysis.ComparisonSurface)).Distinct().Count() > 1))
        {
            var final = shared.Last().Owned;
            problems.Add(new Problem(VolumeCodes.SharedPairFactorMismatch, Severity.Warning,
                $"Analyses {string.Join(", ", shared.Select(x => $"'{x.Analysis.Name}'").Distinct())} name the same surfaces with different spellings and share the owned surface '{final.Name}', which carries cut {final.CutFactor.ToString("R", CultureInfo.InvariantCulture)} / fill {final.FillFactor.ToString("R", CultureInfo.InvariantCulture)}.",
                Fix: "Spell the surface names the same way in every analysis."));
            for (int i = 0; i < bindings.Count; i++)
                if (bindings[i].Owned.Id == final.Id) bindings[i] = bindings[i] with { Owned = bindings[i].Owned with { CutFactor = final.CutFactor, FillFactor = final.FillFactor } };
        }
        return problems.Any(x => x.Severity == Severity.Error)
            ? Outcome<IReadOnlyList<AnalysisBinding>>.Fail(problems)
            : Outcome<IReadOnlyList<AnalysisBinding>>.Ok(bindings, problems);
    }

    /// <summary>Resolves base and comparison and rejects a pair that is one surface (E401).</summary>
    public static Outcome<ResolvedPair> ResolvePair(Transaction tr, Database db, string baseName, string comparisonName,
        StoredRef? storedBase, StoredRef? storedComparison, AdapterConfig cfg, string? analysisName = null)
    {
        string who = analysisName is null ? "" : $"Analysis '{analysisName}': ";
        if (string.Equals(baseName, comparisonName, StringComparison.Ordinal))
            return Outcome<ResolvedPair>.Fail(SameSurface(who, baseName));
        var b = ResolveSource(tr, db, baseName, storedBase, who + "base surface", cfg);
        var c = ResolveSource(tr, db, comparisonName, storedComparison, who + "comparison surface", cfg);
        var problems = b.Problems.Concat(c.Problems).ToList();
        if (!b.Succeeded || !c.Succeeded) return Outcome<ResolvedPair>.Fail(problems);
        if (b.Value.Id == c.Value.Id)
        {
            problems.Add(SameSurface(who, b.Value.Name));
            return Outcome<ResolvedPair>.Fail(problems);
        }
        return Outcome<ResolvedPair>.Ok(new ResolvedPair(b.Value, c.Value), problems);
    }

    /// <summary>
    /// Finds one source surface by name, then by stored handle, and checks it (E402, E403, E404).
    /// </summary>
    /// <param name="tr">The caller's transaction; a native surface that is out of date is opened for write and rebuilt.</param>
    /// <param name="db">The drawing.</param>
    /// <param name="name">Name from the run definition.</param>
    /// <param name="stored">Name and handle persisted with the run, or null; the handle is used only when its stored name is <paramref name="name"/> (case-insensitive).</param>
    /// <param name="who">How messages name it, for example "Analysis 'A': base surface".</param>
    /// <param name="cfg">Adapter settings (<see cref="AdapterConfig.RebuildSources"/>).</param>
    public static Outcome<ResolvedSource> ResolveSource(Transaction tr, Database db, string name, StoredRef? stored, string who, AdapterConfig cfg)
    {
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return Outcome<ResolvedSource>.Fail(new Problem(AdapterCodes.SourceNotFound, Severity.Error, "The drawing has no Civil 3D document."));
        var found = FindSurface(tr, db, civ, name, stored, who, "Restore the surface or pick another one in SVANALYSIS; nothing was changed.");
        if (!found.Succeeded) return Outcome<ResolvedSource>.Fail(found.Problems);
        var notes = found.Problems.ToList();
        ObjectId chosen = found.Value;

        var obj = tr.GetObject(chosen, OpenMode.ForRead);
        if (obj is not TinSurface tin)
        {
            string what = obj switch
            {
                TinVolumeSurface => "a TIN volume surface",
                GridVolumeSurface => "a grid volume surface",
                GridSurface => "a grid surface",
                _ => $"a {obj.GetType().Name}",
            };
            return Outcome<ResolvedSource>.Fail(notes.Append(new Problem(AdapterCodes.NotTinSurface, Severity.Error,
                $"{who} '{SurfaceName(obj)}' is {what}. Base and comparison must be TIN surfaces; SheetVol builds its own volume surface from them.",
                Fix: "Pick a TIN surface in SVANALYSIS.")));
        }

        var current = CheckCurrent(tr, civ, tin, chosen, who, cfg);
        notes.AddRange(current.Problems);
        if (!current.Succeeded) return Outcome<ResolvedSource>.Fail(notes);

        int triangles = TinTriangles(tin);
        if (triangles == 0)
            return Outcome<ResolvedSource>.Fail(notes.Append(new Problem(AdapterCodes.NoTriangles, Severity.Error,
                $"{who} '{tin.Name}' has no triangles.", Fix: "Add data to the surface or pick another one.")));

        return Outcome<ResolvedSource>.Ok(new ResolvedSource(chosen, tin.Name, tin.Handle.ToString(), current.Value.Origin, triangles, current.Value.Rebuilt), notes);
    }

    /// <summary>
    /// Finds one surface by name (exact, then the only case-insensitive match), then by stored
    /// handle, with the <see cref="StoredRef"/> rule: the handle counts only while it was stored for
    /// the name asked for. Matched by name while that handle is gone or elsewhere: SV-E803 warning.
    /// Matched only by handle: SV-E445 warning. Neither: SV-E442 error. Shared by the base and
    /// comparison sources and (rev 3) by user volume surface sources.
    /// </summary>
    internal static Outcome<ObjectId> FindSurface(Transaction tr, Database db, CivilDocument civ, string name, StoredRef? stored, string who, string notFoundFix)
    {
        string? storedHandle = stored is not null && string.Equals(stored.Name, name, StringComparison.OrdinalIgnoreCase) ? stored.Handle : null;
        var notes = new List<Problem>();

        ObjectId byName = ObjectId.Null;
        var caseOnly = new List<ObjectId>();
        foreach (ObjectId id in civ.GetSurfaceIds())
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not CivSurface s) continue;
            if (string.Equals(s.Name, name, StringComparison.Ordinal)) { byName = id; break; }
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) caseOnly.Add(id);
        }
        if (byName.IsNull && caseOnly.Count == 1) byName = caseOnly[0];
        bool hasStored = !string.IsNullOrWhiteSpace(storedHandle);
        bool handleLive = Handles.TryResolve(db, storedHandle, out var byHandle);

        ObjectId chosen;
        if (!byName.IsNull)
        {
            chosen = byName;
            if (hasStored && byHandle != byName)
            {
                string now = handleLive && tr.GetObject(byHandle, OpenMode.ForRead) is CivSurface other ? $"now belongs to '{other.Name}'" : "no longer exists";
                notes.Add(new Problem(AdapterCodes.SourceHandleChanged, Severity.Warning,
                    $"{who} '{name}' was matched by name; its stored handle {storedHandle} {now}. The surface was re-referenced or replaced; the new handle {tr.GetObject(byName, OpenMode.ForRead).Handle} is stored after this regen.",
                    Fix: "Nothing to do if this is the intended surface."));
            }
        }
        else if (handleLive && tr.GetObject(byHandle, OpenMode.ForRead) is CivSurface renamed)
        {
            chosen = byHandle;
            notes.Add(new Problem(AdapterCodes.SourceRenamed, Severity.Warning,
                $"{who} '{name}' was not found by name; its stored handle {storedHandle} is now '{renamed.Name}', which is used.",
                Fix: "The run will refer to the new name; check it is the intended surface."));
        }
        else
        {
            return Outcome<ObjectId>.Fail(notes.Append(new Problem(AdapterCodes.SourceNotFound, Severity.Error,
                $"{who} '{name}' is not in the drawing" + (hasStored ? $", by name or by stored handle {storedHandle}." : "."),
                Fix: notFoundFix)));
        }
        return Outcome<ObjectId>.Ok(chosen, notes);
    }

    /// <summary>
    /// The E404 rule for one TIN source: a stale or out-of-date reference, or an out-of-date corridor
    /// surface, always fails; an out-of-date native surface is rebuilt when
    /// <see cref="AdapterConfig.RebuildSources"/> is on (Info naming it), otherwise fails naming it;
    /// a surface on a locked layer is never rebuilt. Shared by the base and comparison sources and
    /// (rev 3) by the base and comparison of a user volume surface source.
    /// </summary>
    internal static Outcome<(SurfaceOrigin Origin, bool Rebuilt)> CheckCurrent(Transaction tr, CivilDocument civ, TinSurface tin, ObjectId id, string who, AdapterConfig cfg)
    {
        var origin = tin.IsReferenceObject ? SurfaceOrigin.Reference
            : CorridorSurfaceIds(tr, civ).Contains(id) ? SurfaceOrigin.Corridor : SurfaceOrigin.Native;
        if (origin == SurfaceOrigin.Reference && (!tin.IsReferenceValid || tin.IsReferenceStale || tin.IsOutOfDate))
        {
            string state = !tin.IsReferenceValid ? "a broken data-shortcut reference" : tin.IsReferenceStale ? "a stale data-shortcut reference" : "an out-of-date data-shortcut reference";
            return Outcome<(SurfaceOrigin, bool)>.Fail(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                $"{who} '{tin.Name}' is {state}. SheetVol never computes on stale data.",
                Fix: "Synchronize the reference (or rebuild it in its source drawing), then regenerate."));
        }
        if (!tin.IsOutOfDate) return Outcome<(SurfaceOrigin, bool)>.Ok((origin, false));
        if (origin == SurfaceOrigin.Corridor)
            return Outcome<(SurfaceOrigin, bool)>.Fail(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                $"{who} '{tin.Name}' is a corridor surface that is out of date.", Fix: "Rebuild the corridor, then regenerate."));
        if (!cfg.RebuildSources)
            return Outcome<(SurfaceOrigin, bool)>.Fail(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                $"{who} '{tin.Name}' is out of date and RebuildSources is off.", Fix: "Rebuild the surface, or turn RebuildSources on, then regenerate."));
        if (tr.GetObject(tin.LayerId, OpenMode.ForRead) is LayerTableRecord { IsLocked: true } locked)
            return Outcome<(SurfaceOrigin, bool)>.Fail(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                $"{who} '{tin.Name}' is out of date and sits on locked layer '{locked.Name}'; SheetVol does not rebuild a surface it does not own on a locked layer.",
                Fix: "Rebuild the surface, or unlock its layer, then regenerate."));
        tin.UpgradeOpen();
        tin.Rebuild();
        if (tin.IsOutOfDate)
            return Outcome<(SurfaceOrigin, bool)>.Fail(new Problem(AdapterCodes.SourceOutOfDate, Severity.Error,
                $"{who} '{tin.Name}' is still out of date after a rebuild.", Fix: "Check the surface definition in Prospector."));
        return Outcome<(SurfaceOrigin, bool)>.Ok((origin, true),
            [new Problem(AdapterCodes.SourceOutOfDate, Severity.Info, $"{who} '{tin.Name}' was out of date and has been rebuilt (RebuildSources on).")]);
    }

    /// <summary>
    /// Finds, or creates, the owned volume surface for a resolved pair; writes the factors; rebuilds it.
    /// </summary>
    /// <param name="tr">The caller's transaction (document locked).</param>
    /// <param name="db">The drawing.</param>
    /// <param name="pair">Resolved base and comparison.</param>
    /// <param name="desiredName">C2's planned name, <c>SV-&lt;base&gt;-&lt;comparison&gt;</c>.</param>
    /// <param name="cutFactor">Cut factor to carry (6.5: so the Volumes Dashboard agrees).</param>
    /// <param name="fillFactor">Fill factor to carry.</param>
    /// <param name="storedOwnedHandle">Handle persisted with the run, or null.</param>
    /// <param name="cfg">Adapter settings.</param>
    public static Outcome<OwnedSurface> EnsureOwnedSurface(Transaction tr, Database db, ResolvedPair pair, string desiredName,
        double cutFactor, double fillFactor, string? storedOwnedHandle, AdapterConfig cfg)
    {
        var civ = CivilDocument.GetCivilDocument(db);
        if (civ is null) return Outcome<OwnedSurface>.Fail(new Problem(AdapterCodes.SourceNotFound, Severity.Error, "The drawing has no Civil 3D document."));
        var notes = new List<Problem>();

        var inventory = new List<(ObjectId Id, CivSurface Surface, OwnershipTag? Tag)>();
        foreach (ObjectId id in civ.GetSurfaceIds())
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not CivSurface s) continue;
            inventory.Add((id, s, s is TinVolumeSurface ? OwnershipTag.Read(tr, s, cfg) : null));
        }
        bool Usable((ObjectId Id, CivSurface Surface, OwnershipTag? Tag) x) =>
            x.Tag is not null && x.Surface is TinVolumeSurface v && x.Surface.Name == x.Tag.Name && SameSources(v, x.Tag, pair);

        ObjectId chosen = ObjectId.Null;
        if (!string.IsNullOrWhiteSpace(storedOwnedHandle))
        {
            if (!Handles.TryResolve(db, storedOwnedHandle, out var hid))
                notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Info,
                    $"The owned volume surface for {pair.Base.Name} / {pair.Comparison.Name} (handle {storedOwnedHandle}) was erased; it is recreated."));
            else
            {
                var x = inventory.FirstOrDefault(i => i.Id == hid);
                if (x.Surface is null)
                    notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Info,
                        $"Stored owned surface handle {storedOwnedHandle} is no longer a surface; the owned surface is recreated."));
                else if (x.Tag is null)
                    notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Warning,
                        $"Stored owned surface handle {storedOwnedHandle} is '{x.Surface.Name}', which SheetVol does not own; it is left alone and a SheetVol surface is used instead."));
                else if (x.Surface.Name != x.Tag.Name)
                    notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Warning,
                        $"The owned volume surface '{x.Tag.Name}' was renamed to '{x.Surface.Name}'. It is left in place and no longer updated; a new owned surface is used.",
                        Fix: $"Delete '{x.Surface.Name}' if it is not needed."));
                else if (!Usable(x))
                    notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Warning,
                        $"The owned volume surface '{x.Surface.Name}' no longer compares {pair.Base.Name} with {pair.Comparison.Name} (its sources were replaced or re-referenced). It is left in place; a new owned surface is used.",
                        Fix: $"Delete '{x.Surface.Name}' if it is not needed."));
                else chosen = hid;
            }
        }
        if (chosen.IsNull)
        {
            var usable = inventory.Where(Usable)
                .OrderBy(i => i.Surface.Name == desiredName ? 0 : 1).ThenBy(i => i.Surface.Name, StringComparer.Ordinal).ToList();
            if (usable.Count > 0)
            {
                chosen = usable[0].Id;
                if (usable.Count > 1)
                    notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Info,
                        $"{usable.Count} owned volume surfaces compare {pair.Base.Name} with {pair.Comparison.Name}; '{usable[0].Surface.Name}' is used, the others ({string.Join(", ", usable.Skip(1).Select(u => u.Surface.Name))}) are not updated."));
            }
        }

        bool created = false;
        if (chosen.IsNull)
        {
            var taken = inventory.Select(i => i.Surface.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? name = taken.Contains(desiredName) ? null : desiredName;
            for (int n = 2; name is null && n <= cfg.MaxCollisionSuffix; n++)
            {
                string candidate = desiredName + string.Format(CultureInfo.InvariantCulture, cfg.CollisionSuffixFormat, n);
                if (!taken.Contains(candidate)) name = candidate;
            }
            if (name is null)
                return Outcome<OwnedSurface>.Fail(notes.Append(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Error,
                    $"No free name for the owned volume surface: '{desiredName}' and every suffix up to {cfg.MaxCollisionSuffix} are taken.",
                    Fix: "Rename or delete the surfaces that use these names.")));
            if (name != desiredName)
            {
                var holder = inventory.First(i => string.Equals(i.Surface.Name, desiredName, StringComparison.OrdinalIgnoreCase));
                string why = holder.Tag is null ? "a surface SheetVol does not own" : "a SheetVol surface that no longer compares these surfaces";
                notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Warning,
                    $"The name '{desiredName}' is taken by {why}; the owned volume surface is created as '{name}'. '{holder.Surface.Name}' is not touched.",
                    Fix: $"Rename the other surface if you want the owned one to be called '{desiredName}'."));
            }
            chosen = Create(tr, db, civ, pair, name, cfg, notes);
            created = true;
        }

        // The surface is SheetVol's own (tagged, or created just now): opened even on a locked layer (6.10).
        var vol = (TinVolumeSurface)tr.GetObject(chosen, OpenMode.ForWrite, false, true);
        if (vol.CutFactor != cutFactor) vol.CutFactor = cutFactor;
        if (vol.FillFactor != fillFactor) vol.FillFactor = fillFactor;
        vol.Rebuild();   // P18: always; IsOutOfDate cannot be trusted on a volume surface
        int triangles = TinTriangles(vol);
        bool props = TryVolumeProperties(vol, out _);
        if (triangles == 0 || !props)
            return Outcome<OwnedSurface>.Fail(notes.Append(new Problem(AdapterCodes.NoTriangles, Severity.Error,
                $"The owned volume surface '{vol.Name}' has no triangles after a rebuild: {pair.Base.Name} and {pair.Comparison.Name} do not overlap anywhere.",
                Fix: "Check that the two surfaces cover the same ground.")));
        return Outcome<OwnedSurface>.Ok(new OwnedSurface(chosen, vol.Name, vol.Handle.ToString(), pair, created, vol.CutFactor, vol.FillFactor, triangles), notes);
    }

    private static ObjectId Create(Transaction tr, Database db, CivilDocument civ, ResolvedPair pair, string name, AdapterConfig cfg, List<Problem> notes)
    {
        ObjectId styleId = ObjectId.Null;
        bool styleMissing = false;
        if (cfg.OwnedSurfaceStyle is { } style)
        {
            if (civ.Styles.SurfaceStyles.Contains(style)) styleId = civ.Styles.SurfaceStyles[style];
            else styleMissing = true;
        }
        var id = styleId.IsNull
            ? TinVolumeSurface.Create(name, pair.Base.Id, pair.Comparison.Id)
            : TinVolumeSurface.Create(name, pair.Base.Id, pair.Comparison.Id, styleId);
        var vol = (TinVolumeSurface)tr.GetObject(id, OpenMode.ForWrite);
        if (styleMissing)
            notes.Add(new Problem(AdapterCodes.OwnedStyleMissing, Severity.Warning,
                $"Surface style '{cfg.OwnedSurfaceStyle}' is not in the drawing; the owned volume surface '{name}' uses Civil 3D's default style '{vol.StyleName}' and may display.",
                Fix: $"Add a no-display surface style named '{cfg.OwnedSurfaceStyle}' (or set ownedSurfaceStyle to one the drawing has), then restyle '{name}'."));
        if (cfg.OwnedSurfaceLayer is { } layer)
        {
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layers.Has(layer)) vol.Layer = layer;
            else
                notes.Add(new Problem(AdapterCodes.OwnedLayerMissing, Severity.Warning,
                    $"Layer '{layer}' is not in the drawing; the owned volume surface '{name}' stays on layer '{vol.Layer}'.",
                    Fix: $"Add layer '{layer}' (or set ownedSurfaceLayer to one the drawing has), then move '{name}' to it."));
        }
        new OwnershipTag(name, pair.Base.Handle, pair.Comparison.Handle).Write(tr, vol, cfg);
        notes.Add(new Problem(AdapterCodes.OwnedSurfaceRecreated, Severity.Info, $"Owned volume surface '{name}' created for {pair.Base.Name} / {pair.Comparison.Name}."));
        return id;
    }

    /// <summary>
    /// The surface really compares the pair: its volume properties name both. A surface whose
    /// properties cannot be read (no triangles) falls back to its tag's handles; it is then
    /// rebuilt, and fails SV-E403 if it is still empty, so it can never yield a number.
    /// </summary>
    private static bool SameSources(TinVolumeSurface v, OwnershipTag tag, ResolvedPair pair)
    {
        if (TryVolumeProperties(v, out var p))
            return p!.BaseSurface == pair.Base.Id && p.ComparisonSurface == pair.Comparison.Id;
        return tag.BaseHandle == pair.Base.Handle && tag.ComparisonHandle == pair.Comparison.Handle;
    }

    /// <summary>
    /// <c>GetVolumeProperties</c>, which throws on a volume surface with no triangles (5.1 names
    /// <see cref="InvalidOperationException"/>; the exact type on this host is unprobed, so Civil and
    /// AutoCAD exceptions are caught too).
    /// </summary>
    internal static bool TryVolumeProperties(TinVolumeSurface v, out VolumeSurfaceProperties? props)
    {
        try { props = v.GetVolumeProperties(); return true; }
        catch (System.Exception ex) when (ex is InvalidOperationException or ArgumentException or Autodesk.AutoCAD.Runtime.Exception)
        {
            props = null;
            return false;
        }
    }

    private static HashSet<ObjectId> CorridorSurfaceIds(Transaction tr, CivilDocument civ)
    {
        var ids = new HashSet<ObjectId>();
        foreach (ObjectId cid in civ.CorridorCollection)
        {
            if (cid.IsErased || tr.GetObject(cid, OpenMode.ForRead) is not Corridor corridor) continue;
            foreach (CorridorSurface cs in corridor.CorridorSurfaces) ids.Add(cs.SurfaceId);
        }
        return ids;
    }

    internal static int TinTriangles(CivSurface s)
    {
        try
        {
            return s switch
            {
                TinSurface t => t.GetTinProperties().NumberOfTriangles,
                TinVolumeSurface v => v.GetTinProperties().NumberOfTriangles,
                _ => 0,
            };
        }
        catch (System.Exception ex) when (ex is InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception) { return 0; }
    }

    private static string SurfaceName(Autodesk.AutoCAD.DatabaseServices.DBObject obj) => obj is CivSurface s ? s.Name : obj.Handle.ToString();

    private static Problem SameSurface(string who, string name) => new(AdapterCodes.SameSurface, Severity.Error,
        $"{who}base and comparison are the same surface ('{name}').", Fix: "Pick a different comparison surface.");
}
