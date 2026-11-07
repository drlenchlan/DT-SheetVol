# Adapters

Owner: A1 (HANDOFF.md section 10). IAlignmentGeometry, IVolumeSource, ITriangleSource, owned volume surface lifecycle, unit detection.

Every quirk the probes found (docs/api-findings.md section A) is absorbed here; Core never sees it.

## Entry points (all inside the caller's transaction, document locked)

| Type | Use | Fails with |
|---|---|---|
| `DrawingUnitsReader.Read(db, VolumeConfig.Constants)` | Civil 3D drawing unit (never `INSUNITS`, P2): `DrawingUnitsPerFoot` for `PlanningOptions.ForUnitScale`, `VolumeUnits` for C2, `DialogUnits` for C4 | SV-E434 |
| `AlignmentAdapter.Find(tr, db, name, stored, units, cfg)` / `.Open(tr, id, ...)` | `IAlignmentGeometry` from sub-entities (P7), equation-aware `TryParseStation` (P8, E208), cross-check against `PointLocation` at construction | SV-E213, SV-E214, SV-E204 |
| `AnalysisResolver.EnsureAnalyses(tr, db, analyses, stored, cfg, volumeConfig)` | resolves sources, ensures and rebuilds one owned `TinVolumeSurface` per pair, writes factors; `AnalysisBinding.ToStored()` is what to persist | SV-E401 to E404, E412, E442 to E446, E803 |
| `VolumeSourceAdapter.Open(tr, ownedId, cfg)` | `IVolumeSource`: raw cut/fill (P1 sign, P3 unadjusted), noise clamp, datum-method `Area2D` (P22) | SV-E402, SV-E403 |
| `TriangleSourceAdapter.Read(tr, tinId, cfg, readWindowMin?, readWindowMax?)` | `ITriangleSource`: visible triangles (P23, E1013) cached once, grid-indexed window queries | SV-E402, SV-E403; SV-E1021 warning on reference/corridor surfaces |

Stored references (`StoredRef(Name, Handle)`): a stored handle stands in for a missing name only while the run still asks for the name it was stored with, so editing a run to another or a misspelled name fails (SV-E442, SV-E213) instead of falling back to the old object.

Lifetime: `AlignmentAdapter` and `VolumeSourceAdapter` hold objects opened in the caller's transaction and are valid only while it is open. `TriangleSourceAdapter` is a snapshot (build one per regen; one thread at a time).

Settings: `AdapterConfig` (owned-surface style and layer, `RebuildSources`, noise clamp, datums, cross-check tolerances, ownership tag key, collision suffix). JSON in C2's shape (`FromJson`/`ToJson`). Style and layer defaults are PLACEHOLDERS (A7); a missing one is reported (SV-E443, SV-E444), never invented.

Rollback (MEASURED with `SVADAPTERS_ABORT`, one case per script `tools/scripts/adapters-abort-*.scr`): aborting the transaction after `EnsureAnalyses` created an owned surface removes it cleanly (surfaces 2 -> 2), and so does aborting after creating an alignment. Creating an alignment and erasing it in the same transaction, then aborting, crashed the Core Console (0xC0000005); the console loses its last buffered line, so whether the erase or the abort crashed is not isolated. The checks therefore commit their scratch objects.

## Headless check

```powershell
.\tools\test-headless.ps1 -Script .\tools\scripts\adapters.scr
```

`SVADAPTERS` builds FX's fixtures in the open drawing and prints T1/T2 (adapter + C1 planner + C2 reconciler), T10, T24, T29/E1012 (C5 integrator on these triangles vs `GetBoundedVolumes`), the Fixture H reconciliation, E1013, E208, E213, E214, E401 to E404, E410 to E412, E445, E803 and the P23 scale run. `SVADAPTERS_E404` runs next: Civil 3D sets `IsOutOfDate` on a point-group surface only after the command that moved the point ends. A step that throws prints SV-E195.

## Rev 3

- rev3-adapters: `UserVolumeSurfaces.Resolve` / `List` / `Read` / `OpenVolumes` take a user TIN volume surface as an analysis source (E422 guard before every read, E404 on its pair, rebuilt under `RebuildSources` per D21, never written otherwise); `TriangleSourceAdapter.ReadVolumeDepth` reads its own TIN through a `TinSurface` view with Z × `DepthPerTinZ` (−1, MEASURED: the TIN stores comparison − base, api-findings §E); `ZeroPlaneTriangles` is the rev 3 interim comparison for C5's two-source overloads; `EnsureAnalyses` binds pair analyses only; SV-E422 / SV-E423 in `AdapterCodes`. Checks: `SVADAPTERS` rows E1, E423, E1012, E422, then `SVADAPTERS_VS` and `SVADAPTERS_VS_ERASED` (adapters.scr).
