# SheetVol API findings

Answers to the probes in HANDOFF.md section 5.3, with printed evidence. Each section is
owned by its probe worker (section 10): section A by S1, section B by S2, section C by S3.
A worker edits only its own section. Observed behavior on this host overrides
documentation. Every figure is MEASURED on the dev seat (Civil 3D 2026.2.3, AutoCAD
25.1.179, Core Console) unless it is marked otherwise.

## A. Volumes, alignment, headless (S1)

Probes P1–P10, P18, P19, P22, P23, plus T1 and T2 as a printed check. Run 2026-11-05 in
the Civil 3D 2026 Core Console against `tools/fixtures/blank-c3d.dwg`. Fixtures are built
in-process with FX's API (`Fixtures.CreateA/B/D`, `CreateAlignmentS/C/K/SP`,
`CreateVolume`); nothing depends on `fixtureA.dwg`. Code:
`src/SheetVol.Civil3D/SelfTest/ProbeA*.cs`. Commands: `SVPROBEA_SETUP`, `_T12`, `_P1`,
`_P3`, `_P4`, `_P5`, `_P6`, `_P7`, `_P8`, `_P9`, `_P10`, `_P18`, `_P18B`, `_P19`, `_P22`,
`_P23`, `_P23SCALE`. A probe step that fails at the command boundary prints `SV-E194`; the
final run printed none. Output blocks are excerpts: each line is copied from the logs as
printed, some lines in between are left out, and the only edit is the user profile
prefix of temp paths, shortened to `%TEMP%` (or `C:/Users/...`).

### A.0 How to rerun

From the worktree or repo root (the runner finds this checkout's own build output):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-headless.ps1 -Script tools\scripts\probeA.scr
```

P9's host variants (section A.12), each against a drawing with no Civil objects:

```powershell
$t = 'C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\Template\map2d.dwt'
.\tools\test-headless.ps1 -Script .\tools\scripts\probeA-p9.scr -NoProduct -Drawing $t
.\tools\test-headless.ps1 -Script .\tools\scripts\probeA-p9netload.scr -NoProduct -Drawing $t
.\tools\test-headless.ps1 -Script .\tools\scripts\probeA-p9.scr -NoProduct -Drawing $t -ExtraArgs '/ld "C:\Program Files\Autodesk\AutoCAD 2026\ACA\AecBase.dbx"'
.\tools\test-headless.ps1 -Script .\tools\scripts\probeA-p9.scr -NoProduct
```

- `probeA.scr` runs in 12 s, exit code 0. Log: `%TEMP%\sheetvol\probeA.log`. The order
  matters: P4 adds a hide boundary to FG_A, so T1/T2, P1, P3 and P5 run before it; P18 and
  P23 edit FG_B only.
- P6 writes `%TEMP%\sheetvol\probeA-src.dwg` (Fixture A in a side database); P10 reads it
  back. Nothing is written into the repo.

### A.1 Summary

| Probe | Answer | Key fact |
|---|---|---|
| P1 | Answered | `SurfaceVolumeInfo.Cut` is real cut (base above comparison), `Fill` is real fill. P1 polygon: Cut 2700, Fill 0. Bounded agrees with the whole-surface sign FX measured |
| P2 | Answered | Cubic drawing units: 2700 for a 100 CY pyramid. The Civil drawing unit (Feet) decides, not `INSUNITS` (Inches in `blank-c3d.dwg`) |
| P3 | Answered | Bounded results are raw: `CutFactor` 1.25 and `FillFactor` 0.9 leave them unchanged. Both factors are settable; `GetVolumeProperties().Adjusted*` applies them |
| P4 | Answered | No exception for partly or wholly outside: only data inside counts; wholly outside returns 0/0/0. Hidden areas are excluded exactly (1574.074074 vs analytic 1574.074074). Unclosed or 3-point polygons throw `ArgumentException` |
| P5 | Answered | Winding and start vertex make no difference (bit-identical results) |
| P6 | Partly | Native surfaces report `IsReferenceObject=False`. `DataShortcuts.CreateReference` needs data shortcuts in the active shortcut project; it cannot be done headless without creating or changing a project. The volume surface over references is untested |
| P7 | Answered | `SubEntityCount` plus indexer `[j]`; SCS group has 3 sub-entities. `Clockwise=True` means the arc turns right. Analytic offset points match `PointLocation` to 4.58E-13 (worst, SP). Directions are azimuths (clockwise from north) |
| P8 | Answered | `GetStationStringWithEquations` formats raw to displayed. No managed displayed-to-raw method exists; it must be computed from `StationEquations`. A displayed station can map to two raws with identical strings, or to none |
| P9 | Answered | Created headless: TIN, volume surface, alignment. `/product C3D` not required on this seat (AECC demand-loads), no prior `NETLOAD AeccDbMgd.dll`, no `/ld AecBase.dbx`. Runner switches unchanged |
| P10 | Answered | Both work in Core Console. `GetCivilDocument(db)` also works on side databases (`ReadDwgFile`, `new Database`) without switching `WorkingDatabase`; `ActiveDocument` always means the open drawing |
| P18 | Answered | `IsOutOfDate` stayed `False` through every edit. TIN edits apply at once; the volume surface keeps stale totals until `Rebuild()` (or until the command ends, with `AutoRebuild` on). `IsReferenceStale=False` on natives |
| P19 | Answered | `Autodesk.Civil.PointNotOnEntityException`, message `Point Outside Surface.`, both outside the hull and inside a hidden area |
| P22 | Answered | `|Net(0) − Net(1)|` = polygon area to 5.79E-16 relative (62,829.8115 for the tessellated band; analytic sector 62,831.853, −3.25E-05 from tessellation). Off-edge band: equals the analytic on-surface area to −4.20E-16. T24 passes |
| P23 | Partly | `GetTriangles(false)` / `Triangles` return visible triangles only; `GetTriangles(true)` adds hidden ones flagged `IsVisible=false`. Boundaries hide triangles, masks do not. 999,698 triangles read in 1.13 s. Reference and corridor surfaces not measurable headless |
| T1, T2 | PASS | All 8 segments within ±0.01 CY (largest diff 8.53E-14 CY); band totals 200.000 / 200.000 CY; partition diff 0 |

Stop condition 12.1 is **not** triggered: P1–P3 are pinned and Fixture A reconciles with
`GetVolumeProperties` (band = whole surface, outside-limits 0). 12.2 and 12.9 are **open,
not triggered**: data-shortcut and corridor surfaces could not be built headless, so
nothing showed they fail (A.18).

### A.2 Rules for A1 (and A4, C5) that come out of these probes

1. **Mapping (P1, P2, P3):** `RawVolume.Cut = SurfaceVolumeInfo.Cut`,
   `RawVolume.Fill = SurfaceVolumeInfo.Fill`, no swap, no sign change. Values are cubic
   drawing units, unadjusted. Apply factors in Core, never expect them in bounded results.
   Write the analysis factors to the owned surface so `GetVolumeProperties().Adjusted*`
   (and the Volumes Dashboard) agree.
2. **Units (P2):** read the drawing unit from
   `CivilDocument.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits`, not `INSUNITS`.
   They differ in `blank-c3d.dwg` (Feet vs Inches).
3. **Always `Rebuild()` the owned volume surface before reading it (P18, P4).**
   `IsOutOfDate` is `False` even when the totals are stale, so it cannot be used as the
   trigger. After the FG_A hide boundary the volume surface still returned 2700 until
   rebuilt (then 1574.074); after an FG_B vertex edit it kept 88,000 until rebuilt
   (then 121,000).
4. **2D area (P22, 6.5):** `Area2D = |Net(d1) − Net(d2)| / |d2 − d1|` on the owned
   volume surface holds to machine precision, including off the surface edge and over
   holes (P4: 14,000 = 15,600 − 1,600). Datums 0 and 1 are enough. On a volume surface the
   datum is relative to the elevation difference, so Net(1) is 0 on Fixture D.
5. **No exception means nothing (P4):** a polygon with no surface data under it returns
   Cut 0, Fill 0, Net 0. "No data" and "identical surfaces" are told apart only by the
   coverage from rule 4.
6. **Sign noise (P4):** after the hole, bounded Fill came back `5.366806115239277E-11`
   and whole-surface fill `5400.000000000405`. Clean tiny values before C2's SV-E432
   check, as C2's note says.
7. **Polygons (P4, P5):** close the polygon (first point repeated) and give at least 3
   distinct vertices (4 points). Winding does not matter.
8. **Alignment geometry (P7):** walk `Entities.GetEntityByOrder(i)` for
   `i < Entities.Count`, then `entity[j]` for `j < SubEntityCount`. `Clockwise=True` is a
   right turn, so `AlignSeg.Clockwise` takes it as is. `Direction`, `StartDirection`,
   `EndDirection` are azimuths, clockwise from north: `AlignSeg.StartDir` (CCW from +X)
   is `π/2 − azimuth`, or better, computed from the points. `EntityBefore`/`EntityAfter`
   throw `EntityNotFoundException` at the ends; `ReverseCurve` (and on fixed arcs
   `GreaterThan180`) throws `InvalidOperationException`; read neither.
9. **Station equations (P8):** `FormatStation` can call `GetStationStringWithEquations`;
   `TryParseStation` must be written in Core from the `StationEquations` table
   (`RawStationBack`, `StationAhead`, `EquationType`) and must report ambiguity (two
   regions) and gaps (no region) as errors, because the displayed strings are identical.
   Raw outside the alignment throws `ArgumentOutOfRangeException`.
10. **Triangles (P23):** `ITriangleSource` reads `GetTriangles(false)` (or `Triangles`),
    vertices through `Vertex1..3.Location`. There is no window query; read once, cache as
    `Tri3`, filter in Core (1M triangles: 1.13 s read, about 110 MB, 34 ms filter). A
    `TinVolumeSurface` has no triangle API: read the base and comparison TINs.
11. **Coverage sampling (P19):** catch `Autodesk.Civil.PointNotOnEntityException`. It also
    fires inside hidden areas; points within 1e-6 outside the hull may still return a
    value.

### A.3 T1 and T2 (printed check, not the self-test harness)

Fixture A, alignment S, offsets (−100, +100), polygon corners from `PointLocation`, owned
volume surface `SV-EG-FG_A`, mapping from rule 1. `SVPROBEA_T12` printed:

```text
SV-EG-FG_A GetVolumeProperties: UnadjustedCut=5400.000000000002 UnadjustedFill=5400 UnadjustedNet=-1.8189894035458565E-12 CutFactor=1 FillFactor=1
mapping used below: real cut = SurfaceVolumeInfo.Cut, real fill = SurfaceVolumeInfo.Fill (P1); units cubic drawing units = ft3, CY = ft3 / 27 (P2)
T1: alignment S, offsets (-100, 100), polygon corners from PointLocation
T1 | segment     | API Cut ft3  | API Fill ft3 | Cut CY   | Fill CY  | expected C/F CY | diff C/F CY        | result
T1 |     0-500   |         2700 |         1350 |  100.000 |   50.000 | 100.000/50.000  | 0.00E+00/0.00E+00 | PASS
T1 |   500-1000  |            0 |            0 |    0.000 |    0.000 |   0.000/0.000   | 0.00E+00/0.00E+00 | PASS
T1 |  1000-1500  | 2700.0000000000023 |         1350 |  100.000 |   50.000 | 100.000/50.000  | 8.53E-14/0.00E+00 | PASS
T1 |  1500-2000  |            0 |         2700 |    0.000 |  100.000 |   0.000/100.000 | 0.00E+00/0.00E+00 | PASS
T1 sum of segments: Cut=5400.000000000002 Fill=5400; whole band [0,2000]: Cut=5400.000000000002 Fill=5400; partition diff Cut=0.00E+00 Fill=0.00E+00
T1 outside-limits = GetVolumeProperties - band: Cut=0 Fill=0
T1 band totals in CY: Cut=200.000 Fill=200.000 (G1 wants 200 / 200)
T2: alignment S, offsets (-100, 100), polygon corners from PointLocation
T2 | segment     | API Cut ft3  | API Fill ft3 | Cut CY   | Fill CY  | expected C/F CY | diff C/F CY        | result
T2 |     0-500   |         2700 |         1350 |  100.000 |   50.000 | 100.000/50.000  | 0.00E+00/0.00E+00 | PASS
T2 |   500-1300  | 2700.0000000000023 |            0 |  100.000 |    0.000 | 100.000/0.000   | 8.53E-14/0.00E+00 | PASS
T2 |  1300-1800  |            0 |         4050 |    0.000 |  150.000 |   0.000/150.000 | 0.00E+00/0.00E+00 | PASS
T2 |  1800-2000  |            0 |            0 |    0.000 |    0.000 |   0.000/0.000   | 0.00E+00/0.00E+00 | PASS
T2 sum of segments: Cut=5400.000000000002 Fill=5400; whole band [0,2000]: Cut=5400.000000000002 Fill=5400; partition diff Cut=0.00E+00 Fill=0.00E+00
T2 outside-limits = GetVolumeProperties - band: Cut=0 Fill=0
T2 band totals in CY: Cut=200.000 Fill=200.000 (G1 wants 200 / 200)
T1+T2: 0 segment(s) outside +-0.01 CY
```

Fixture A reconciles: segment sums equal the whole band, and the band equals
`GetVolumeProperties` (outside-limits 0, since every pyramid lies inside ±100). This is
the G1 line "Fixture A totals of 200 CY cut and 200 CY fill after the documented mapping".

### A.4 P1: which of `Cut`/`Fill` is real cut?

**Ran:** `SVPROBEA_P1`. One polygon around pyramid P1 only (apex 99.0, comparison below
base: real cut 2700 ft³), one around P2 only (apex 100.5: real fill 1350 ft³), on the owned
`SV-EG-FG_A`; the same calls on the base TIN `EG` and on `FG_A`.

```text
SV-EG-FG_A IsVolumeSurface=True whole surface: UnadjustedCut=5400.000000000002 UnadjustedFill=5400 UnadjustedNet=-1.8189894035458565E-12
SV-EG-FG_D whole surface (FG_D is 1.00 ABOVE EG everywhere, analytic fill 1440000): UnadjustedCut=0 UnadjustedFill=1440000 UnadjustedNet=1440000
SV-EG-FG_A.GetBoundedVolumes(P1 polygon): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P2 polygon): Cut=0 Fill=1350 Net=1350
SV-EG-FG_A.GetBoundedVolumes(P1 polygon, datum 0): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P2 polygon, datum 0): Cut=0 Fill=1350 Net=1350
SV-EG-FG_D.GetBoundedVolumes(P1 polygon): Cut=0 Fill=15600 Net=15600
-- the base TinSurface EG (not a volume surface), and FG_A, over the same polygons --
EG IsVolumeSurface=False
EG.GetBoundedVolumes(P1 polygon): Cut=0 Fill=1560000 Net=1560000
EG.GetBoundedVolumes(P1 polygon, datum 0): Cut=0 Fill=1560000 Net=1560000
EG.GetBoundedVolumes(P1 polygon, datum 99.5): Cut=0 Fill=7800 Net=7800
FG_A.GetBoundedVolumes(P1 polygon): Cut=0 Fill=1557300 Net=1557300
FG_A.GetBoundedVolumes(P1 polygon, datum 100): Cut=2700.000000000077 Fill=0 Net=-2700.000000000077
FG_A.GetBoundedVolumes(P2 polygon, datum 100): Cut=0 Fill=1349.9999999999231 Net=1349.9999999999231
```

**Answer:** on a `TinVolumeSurface`, `Cut` is real cut and `Fill` real fill; `Net = Fill −
Cut`. Bounded agrees with FX's whole-surface measurement (`UnadjustedFillVolume` =
comparison above base). Without a datum, a volume surface uses datum 0 on the elevation
difference, so `GetBoundedVolumes(p)` equals `GetBoundedVolumes(p, 0)`. On a plain
`TinSurface` the call measures the surface against a datum, elevation 0 when none is
given: EG over the 15,600 ft² polygon is 15,600 × 100 = 1,560,000 "fill". Never call it on
a base or comparison TIN expecting cut and fill between surfaces.

### A.5 P2: units

**Ran:** same command.

```text
drawing INSUNITS=Inches; Civil DrawingUnits=Feet; pyramid P1 analytic 2700 ft3 = 100 CY
```

**Answer:** cubic drawing units. P1 returns 2700, not 100. The drawing unit that counts is
Civil's (`Feet`); `INSUNITS` in `blank-c3d.dwg` says Inches and is not what the volume is
in. CY = ft³ / 27 is Core's job.

### A.6 P3: factors

**Ran:** `SVPROBEA_P3`: read, set `CutFactor` 1.25, read, set `FillFactor` 0.9, read,
reset. Each set and read in its own transaction.

```text
reflection: TinVolumeSurface.FillFactor has a public setter: True; CutFactor: True
[factors 1/1] SV-EG-FG_A.CutFactor=1 FillFactor=1; GetVolumeProperties: CutFactor=1 FillFactor=1 UnadjustedCut=5400.000000000002 AdjustedCut=5400.000000000002 UnadjustedFill=5400 AdjustedFill=5400 AdjustedNet=-1.8189894035458565E-12
SV-EG-FG_A.GetBoundedVolumes(P1 polygon [factors 1/1]): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P2 polygon [factors 1/1]): Cut=0 Fill=1350 Net=1350
after set in the same transaction: CutFactor=1.25 FillFactor=1 IsOutOfDate=False
[CutFactor 1.25] SV-EG-FG_A.CutFactor=1.25 FillFactor=1; GetVolumeProperties: CutFactor=1.25 FillFactor=1 UnadjustedCut=5400.000000000002 AdjustedCut=6750.000000000002 UnadjustedFill=5400 AdjustedFill=5400 AdjustedNet=-1350.0000000000018
SV-EG-FG_A.GetBoundedVolumes(P1 polygon [CutFactor 1.25]): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P2 polygon [CutFactor 1.25]): Cut=0 Fill=1350 Net=1350
after set in the same transaction: CutFactor=1.25 FillFactor=0.9 IsOutOfDate=False
[CutFactor 1.25, FillFactor 0.9] SV-EG-FG_A.CutFactor=1.25 FillFactor=0.9; GetVolumeProperties: CutFactor=1.25 FillFactor=0.9 UnadjustedCut=5400.000000000002 AdjustedCut=6750.000000000002 UnadjustedFill=5400 AdjustedFill=4860 AdjustedNet=-1890.0000000000018
SV-EG-FG_A.GetBoundedVolumes(P1 polygon [CutFactor 1.25, FillFactor 0.9]): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P2 polygon [CutFactor 1.25, FillFactor 0.9]): Cut=0 Fill=1350 Net=1350
[reset 1/1] SV-EG-FG_A.CutFactor=1 FillFactor=1; GetVolumeProperties: CutFactor=1 FillFactor=1 UnadjustedCut=5400.000000000002 AdjustedCut=5400.000000000002 UnadjustedFill=5400 AdjustedFill=5400 AdjustedNet=-1.8189894035458565E-12
```

**Answer:** bounded results never include the factors. `FillFactor` is settable like
`CutFactor` (5.1 listed only `CutFactor` as get/set; the 13.8 assembly has a public
setter on both). `GetVolumeProperties().Adjusted*` applies them, so writing the analysis
factors to the owned surface keeps the Volumes Dashboard in step (T11) while Core applies
them to the raw bounded numbers once.

### A.7 P4: polygons partly outside, wholly outside, malformed, over a hole

**Ran:** `SVPROBEA_P4`. Then a hide boundary (non-destructive) 105..145 × −20..20 on FG_A,
inside pyramid P1. Analytic cut lost to the hole: the pyramid depth `1 − max(|dx|,|dy|)/45`
over the 40 × 40 square = 1600 − (8 · 20³ / 3) / 45 = 1125.926 ft³, so 2700 − 1125.926 =
1574.074 ft³ should remain.

```text
partly outside: x 60..190, y -60..400 (EG ends at y 300); holds P1 entirely; polygon area 59800, on-surface area 46800
SV-EG-FG_A.GetBoundedVolumes(partly outside): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_D.GetBoundedVolumes(partly outside): Cut=0 Fill=46800 Net=46800
SV-EG-FG_D.GetBoundedVolumes(partly outside, datum 0): Cut=0 Fill=46800 Net=46800
SV-EG-FG_D.GetBoundedVolumes(partly outside, datum 1): Cut=0 Fill=0 Net=0
partly outside on D: |Net(0)-Net(1)| = 46800 (area of data inside)
wholly outside: x 3000..3100, y 0..100
SV-EG-FG_A.GetBoundedVolumes(wholly outside): Cut=0 Fill=0 Net=0
SV-EG-FG_A.GetBoundedVolumes(wholly outside, datum 0): Cut=0 Fill=0 Net=0
-- malformed polygons (5.1 says ArgumentException) --
SV-EG-FG_A.GetBoundedVolumes(P1 rectangle not closed (4 points, first != last)) threw System.ArgumentException Message='Unable to calculate volume, possibly illegal bounding polygon.'
SV-EG-FG_A.GetBoundedVolumes(closed triangle (4 points)): Cut=2255.2256583506583 Fill=0 Net=-2255.2256583506583
SV-EG-FG_A.GetBoundedVolumes(degenerate closed (3 points)) threw System.ArgumentException Message='Unable to calculate volume, possibly illegal bounding polygon.'
-- hole: hide boundary 105..145 x -20..20 on FG_A, non-destructive --
before: FG_A: GetTriangles(true).Count=52 GetTriangles(false).Count=52 hidden(IsVisible=false)=0 Triangles.Count=52 GetTinProperties().NumberOfTriangles=52 Vertices.Count=29
AddBoundaries returned SurfaceOperationAddBoundary BoundaryType=Hide Count=1
FG_A IsOutOfDate after AddBoundaries (same transaction) = False
after Rebuild: FG_A: GetTriangles(true).Count=60 GetTriangles(false).Count=56 hidden(IsVisible=false)=4 Triangles.Count=56 GetTinProperties().NumberOfTriangles=56 Vertices.Count=32
SV-EG-FG_A IsOutOfDate after FG_A changed (new transaction) = False
SV-EG-FG_A.GetBoundedVolumes(P1 polygon, volume surface not rebuilt): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A rebuilt; IsOutOfDate=False
analytic P1 cut with the hole removed from the data: 2700 - 1125.925925925926 = 1574.074074074074 ft3
SV-EG-FG_A.GetBoundedVolumes(P1 polygon over the hole): Cut=1574.074074074078 Fill=5.366806115239277E-11 Net=-1574.0740740740243
diff from analytic = 4.09E-12
SV-EG-FG_A.GetBoundedVolumes(polygon wholly inside the hole): Cut=0 Fill=0 Net=0
SV-EG-FG_A.GetBoundedVolumes(P1 polygon over the hole, datum 0): Cut=1574.074074074078 Fill=5.366806115239277E-11 Net=-1574.0740740740243
SV-EG-FG_A.GetBoundedVolumes(P1 polygon over the hole, datum 1): Cut=15574.074074074024 Fill=0 Net=-15574.074074074024
area of data inside P1 polygon = |Net(0)-Net(1)| = 14000; polygon 15600 minus hole 1600 = 14000
SV-EG-FG_A whole surface after hole: UnadjustedCut=4274.07407407408 UnadjustedFill=5400.000000000405 (expected cut 4274.074074074074)
```

**Answer:** a result, never an exception, for polygons partly or wholly outside: only
the part over surface data counts, and wholly outside is 0/0/0. A hidden area is cut out
exactly (4.09E-12 from analytic) once the volume surface is rebuilt; before the rebuild
it returned the stale 2700 with `IsOutOfDate=False` (rule 3). Unclosed polygons and
closed polygons with fewer than 4 points throw `ArgumentException` "Unable to calculate
volume, possibly illegal bounding polygon."; a closed triangle (4 points) is accepted.

### A.8 P5: winding

**Ran:** `SVPROBEA_P5`: each rectangle counter-clockwise, clockwise, and counter-clockwise
from another start vertex.

```text
SV-EG-FG_A.GetBoundedVolumes(P1 CCW): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P1 CW): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P1 CCW, datum 0): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P1 CW, datum 0): Cut=2700 Fill=0 Net=-2700
SV-EG-FG_A.GetBoundedVolumes(P1 CCW from NE corner): Cut=2700 Fill=0 Net=-2700
P1: CW-CCW diff Cut=0.00E+00 Fill=0.00E+00 Net=0.00E+00; start-vertex diff Cut=0.00E+00
SV-EG-FG_A.GetBoundedVolumes(P2 CCW): Cut=0 Fill=1350 Net=1350
SV-EG-FG_A.GetBoundedVolumes(P2 CW): Cut=0 Fill=1350 Net=1350
P2: CW-CCW diff Cut=0.00E+00 Fill=0.00E+00 Net=0.00E+00; start-vertex diff Cut=0.00E+00
```

**Answer:** winding does not matter; no sign flip, no exception, identical values.

### A.9 P6: volume surface over data-shortcut surfaces

**Ran:** `SVPROBEA_P6`. Reference flags on the native fixture surfaces. Then, through
reflection (the csproj is frozen to four Autodesk references, and the data-shortcut API
lives in `AeccDataShortcutMgd.dll`): Fixture A built into a side database read from
`blank-c3d.dwg` and saved to `%TEMP%\sheetvol\probeA-src.dwg`; a second side database as
host; `DataShortcuts.CreateReference` for EG and FG_A with both overloads. The probe reads
only *whether* a working folder and shortcut project are set, never their values, and
calls nothing that sets, creates or associates a project.

```text
native EG: IsReferenceObject=False IsReferenceStale=False IsReferenceValid=False IsReadOnlyReferenceObject=False IsReferencedSourceExisting=threw InvalidOperationException
native FG_A: IsReferenceObject=False IsReferenceStale=False IsReferenceValid=False IsReadOnlyReferenceObject=False IsReferencedSourceExisting=threw InvalidOperationException
native SV-EG-FG_A: IsReferenceObject=False IsReferenceStale=False IsReferenceValid=False IsReadOnlyReferenceObject=False IsReferencedSourceExisting=threw InvalidOperationException
AeccDataShortcutMgd.dll already loaded: False
Autodesk.Civil.DataShortcuts.DataShortcuts public static methods: SetWorkingFolder, CreateProjectFolder, SetCurrentProjectFolder, GetOtherProjectFolders, GetCurrentProjectFolder, Validate, GetAssociateShortcutProjectIdFromDrawing, AssociateDSProject, GetAllProjectFolders, GetWorkingFolder, GetDescriptionDataShorcutProjectFolder, CreateReference, GetDSProjectId, RepairBrokenDRef, CreatePartialReferenceSurface, UpdatePartialReferenceSurface, CreateDataShortcutManager, SaveDataShortcutManager
methods named *Sync*: 0
working folder set: True (value not printed)
current shortcut project set: True (value not printed)
source: Fixture A built in a side db (30 checks) and saved to %TEMP%\sheetvol\probeA-src.dwg
CreateReference(host, source file name, 'EG', Surface) threw System.InvalidOperationException Message='Can't get data shortcuts from active project'
CreateReference(host, source Database, 'EG', Surface) threw System.InvalidOperationException Message='Can't get data shortcuts from active project'
CreateReference(host, source file name, 'FG_A', Surface) threw System.InvalidOperationException Message='Can't get data shortcuts from active project'
CreateReference(host, source Database, 'FG_A', Surface) threw System.InvalidOperationException Message='Can't get data shortcuts from active project'
```

**Answer (partial):**

- Native surfaces and the owned volume surface report `IsReferenceObject=False`,
  `IsReferenceStale=False`; `IsReferencedSourceExisting` throws on a non-reference.
- `CreateReference` looks the entity up among the published shortcuts of the user's
  *active* shortcut project; both overloads refuse a drawing that is not published
  there. Going further headless means `CreateProjectFolder`/`SetCurrentProjectFolder`
  and publishing, which change the user's shortcut configuration. Not done.
- The managed API has no synchronize method (`*Sync*` count 0); synchronizing references
  is a UI operation as far as this assembly shows.
- `TinVolumeSurface.Create` on two reference surfaces, its rebuild after a synchronize,
  and `GetTriangles` on a reference surface are **untested**. Stop condition 12.2 is
  neither triggered nor cleared.
- The code path is ready: if `CreateReference` succeeds, the same command builds the
  volume surface on the references, prints its totals and the P1 polygon, changes the
  source (P1 apex 99 → 98), and saves the host to `%TEMP%\sheetvol\probeA-host.dwg` for
  the manual synchronize.

**What Dan's fixture drawing needs** (one sitting in full Civil 3D, a scratch shortcut
project): publish EG and FG_A from a saved Fixture A drawing (`SVFIXTURE A` then save);
in a second drawing create references to both; run `TinVolumeSurface.Create` on them
(`SVFIXTURE VOLUMES` creates `FX-VOL-EG-FG_A` from whatever surfaces carry those names)
and check `SVFIXTUREREPORT` shows cut 5400 / fill 5400; set the P1 apex to 98 in the
source, save, synchronize in the host, rebuild the volume surface, and check the cut
becomes 8100 (P1 doubles to 5400). Untested alternative if references cannot feed a
volume surface: `TinSurface.CreateByCropping(destDb, name, srcSurfaceId, points)` exists
and might copy a source surface into the sheet drawing as a native TIN.

### A.10 P7: alignment sub-entities, `Clockwise`, offsets against `PointLocation`

**Ran:** `SVPROBEA_P7` on S, C, K, SP and on `PA-CC` (a line, two fixed arcs turning right
with R 300 then R 600 — a compound curve — and a line, built by the probe). For each
sub-entity the analytic centreline point uses only API properties (line end points; arc
centre, radius, `Clockwise`; spiral `A` with the neighbouring tangent's direction, clothoid
by Simpson integration), offset with the contract's sign (negative = left), compared with
`PointLocation` at 20 stations × offsets −100, 0, +100, under both readings of
`Clockwise`. Excerpts (S, K and the arc of C are in the log in the same form):

```text
--- C: stations 0..914.1592653589793, Entities.Count=3
  entity 1: Arc EntityId=3 SubEntityCount=1 EntityBefore=1 EntityAfter=2 constraint=Free
    [0] Arc 300.00000000000006..614.1592653589793 L=314.1592653589792 (300, 0)->(559.807621, -150) group=''/'' R=300 Center=(300, -300) Clockwise=True Delta=1.0471975511965974 StartDirection=1.5707963267948966 EndDirection=2.617993877991494 Reverse=threw InvalidOperationException('Invalid Operation.') >180=False; geometric sweep (start->end about centre) is CLOCKWISE (turns right)
  P7 C reading 'Clockwise=true turns right': True: max |analytic - PointLocation| over 20 stations x 3 offsets = 5.86E-14 (within 1.00E-06); worst at station 336.79551881646603 offset -100: analytic (348.937777, 96.995081) PointLocation (348.937777, 96.995081)
  P7 C reading 'Clockwise=true turns right': False: max |analytic - PointLocation| over 20 stations x 3 offsets = 4.94E+02 (OUTSIDE 1.00E-06); worst at station 577.3637465425132 offset 100: analytic (-19.338936, -59.121101) PointLocation (459.669468, -179.560551)
--- K: stations 0..800, Entities.Count=2
    [0] Line 500..800 L=300 (500, 0)->(759.807621, -150) group=''/'' Direction=2.094395102393195 (atan2 of the chord = -0.5235987755982988, azimuth from north = 2.0943951023931953)
  P7 K reading 'Clockwise=true turns right': True: max |analytic - PointLocation| over 20 stations x 3 offsets = 2.84E-14 (within 1.00E-06); worst at station 252.6315789473684 offset -100: analytic (252.631579, 100) PointLocation (252.631579, 100)
--- SP: stations 0..793.4370969159813, Entities.Count=3
  entity 0: Line EntityId=1 SubEntityCount=1 EntityBefore=threw EntityNotFoundException EntityAfter=3 constraint=Fixed
  entity 1: SpiralCurveSpiral EntityId=3 SubEntityCount=3 EntityBefore=1 EntityAfter=2 constraint=Free
    [0] Spiral 315.818854558416..415.818854558416 L=100 (315.818855, 0)->(415.718901, 3.330953) group=''/'' A=223.60679774997897 RadiusIn=Infinity RadiusOut=500 Direction=DirectionLeft Definition=Clothoid CurveType=InCurve StartDirection=1.5707963267948966 EndDirection=1.4707963267948965 Delta=0.1 P=0.8330357774093775 K=49.98333796219972 TotalX=99.9000462856138 TotalY=3.3309531383964672 Compound=False
    [1] Arc 415.818854558416..577.6182423575654 L=161.79938779914937 (415.718901, 3.330953)->(571.324096, 45.02524) group=''/'' R=500.000000000001 Center=(365.802193, 500.833036) Clockwise=False Delta=0.3235987755982981 StartDirection=1.4707963267948962 EndDirection=1.1471975511965982 Reverse=threw InvalidOperationException('Invalid Operation.') >180=False; geometric sweep (start->end about centre) is COUNTER-CLOCKWISE (turns left)
    [2] Spiral 577.6182423575654..677.6182423575654 L=100 (571.324096, 45.02524)->(659.505551, 92.090573) group=''/'' A=223.60679774997897 RadiusIn=500 RadiusOut=Infinity Direction=DirectionLeft Definition=Clothoid CurveType=OutCurve StartDirection=1.1471975511965977 EndDirection=1.0471975511965979 Delta=0.1 P=0.8330357774093775 K=49.98333796219972 TotalX=99.9000462856138 TotalY=3.3309531383964672 Compound=False
  entity 1 indexer [3] (one past the end) threw System.ArgumentOutOfRangeException Message='Specified argument was out of the range of valid values.'
  entity 1 as AlignmentSCS: SpiralIn 315.818854558416..415.818854558416, Arc R=500.000000000001 Clockwise=False, SpiralOut 577.6182423575654..677.6182423575654
  entity 2: Line EntityId=2 SubEntityCount=1 EntityBefore=3 EntityAfter=threw EntityNotFoundException constraint=Fixed
  GetEntityByOrder(3) (one past the end) threw System.ArgumentException Message='Cannot find alignment entity at this index.'
  P7 SP reading 'Clockwise=true turns right': True: max |analytic - PointLocation| over 20 stations x 3 offsets = 4.58E-13 (within 1.00E-06); worst at station 584.63786088546 offset -100: analytic (535.36782, 138.550781) PointLocation (535.36782, 138.550781)
  P7 SP reading 'Clockwise=true turns right': False: max |analytic - PointLocation| over 20 stations x 3 offsets = 3.17E+02 (OUTSIDE 1.00E-06); worst at station 542.8780136793556 offset 100: analytic (304.398621, 105.574131) PointLocation (573.860307, -61.938517)
--- PA-CC: stations 0..866.5191429188094, Entities.Count=4
  entity 1: Arc EntityId=2 SubEntityCount=1 EntityBefore=1 EntityAfter=3 constraint=Fixed
    [0] Arc 300..457.0796326794897 L=157.07963267948966 (300, 0)->(450, -40.192379) group=''/'' R=300.00000000000045 Center=(300, -300) Clockwise=True Delta=0.523598775598298 StartDirection=1.5707963267948966 EndDirection=2.094395102393195 Reverse=threw InvalidOperationException('Invalid Operation.') >180=threw InvalidOperationException('Invalid Operation.'); geometric sweep (start->end about centre) is CLOCKWISE (turns right)
  entity 2: Arc EntityId=3 SubEntityCount=1 EntityBefore=2 EntityAfter=4 constraint=Fixed
    [0] Arc 457.0796326794897..666.5191429188093 L=209.43951023931956 (450, -40.192379)->(609.626666, -174.135055) group=''/'' R=600.0000000000023 Center=(150, -559.807621) Clockwise=True Delta=0.3490658503988646 StartDirection=2.0943951023931966 EndDirection=2.443460952792061 Reverse=threw InvalidOperationException('Invalid Operation.') >180=threw InvalidOperationException('Invalid Operation.'); geometric sweep (start->end about centre) is CLOCKWISE (turns right)
  P7 PA-CC reading 'Clockwise=true turns right': True: max |analytic - PointLocation| over 20 stations x 3 offsets = 1.42E-13 (within 1.00E-06); worst at station 410.456436119436 offset -100: analytic (443.97023, 73.192407) PointLocation (443.97023, 73.192407)
```

Max difference per alignment under the right reading: S 0.00E+00, C 5.86E-14, K 2.84E-14,
SP 4.58E-13, PA-CC 1.42E-13 (all within 1e-6). Under the wrong reading C, SP and PA-CC are
off by 317 to 494 ft (S and K have no arcs, so both readings agree).

**Answer:**

- Count and indexer: `AlignmentEntity.SubEntityCount` and `entity[j]`;
  `entity[SubEntityCount]` throws `ArgumentOutOfRangeException`,
  `GetEntityByOrder(Count)` throws `ArgumentException` (5.1 said
  `ArgumentOutOfRangeException`; the host throws `ArgumentException`).
- Group layout: a spiral-curve-spiral is one entity of type `SpiralCurveSpiral` with 3
  sub-entities (spiral in, arc, spiral out), also reachable as `AlignmentSCS.SpiralIn`,
  `.Arc`, `.SpiralOut`. Two fixed arcs attached in sequence (a compound curve) stay two
  `Arc` entities of one sub-entity each; nothing groups them. `CurveGroupIndex` was empty
  on every sub-entity.
- `Clockwise=True` means the arc sweeps clockwise in plan, a right turn (C, PA-CC);
  `False` is a left turn (SP's arc, with the spirals' `Direction=DirectionLeft`).
- `Direction`/`StartDirection`/`EndDirection` are azimuths (clockwise from north): east is
  1.5708, C's out-tangent (math angle −60°) is 2.618.
- `PointLocation` negative offset is left, matching the contracts; analytic offsets agree
  to 4.58E-13 at worst, so A1's `IAlignmentGeometry` can compute line and arc offsets in
  closed form and use the clothoid for spirals.

### A.11 P8: station equations

**Ran:** `SVPROBEA_P8` on alignment S: no equation; one `Increasing` equation back 1000 =
ahead 800; a second one back 1500 = ahead 2000; then a single `Decreasing` one. All
removed afterwards.

```text
GetStationStringWithEquations(-10) (before start) threw System.ArgumentOutOfRangeException Message='Specified argument was out of the range of valid values.'
GetStationStringWithEquations(2500) (past end) threw System.ArgumentOutOfRangeException Message='Specified argument was out of the range of valid values.'
[one equation, back 1000 = ahead 800] StationEquations.Count=1 EndingStation=2000 EndingStationWithEquations=1800
[one equation, back 1000 = ahead 800]   equation 0: RawStationBack=1000 StationBack=1000 StationAhead=800 EquationType=Increasing
[one equation, back 1000 = ahead 800]   raw      900 -> '9+00.00'
[one equation, back 1000 = ahead 800]   raw   999.99 -> '9+99.99'
[one equation, back 1000 = ahead 800]   raw     1000 -> '8+00.00'
[one equation, back 1000 = ahead 800]   raw  1000.01 -> '8+00.01'
[one equation, back 1000 = ahead 800]   raw     1100 -> '9+00.00'
[one equation, back 1000 = ahead 800]   raw     2000 -> '18+00.00'
[one equation, back 1000 = ahead 800]   GetStationSet(Equation): raw 1000 at (1000, 0) type Equation
PointLocation(1100, 0) with the equation = (1100, 0): PointLocation takes RAW stations
[two equations, + back 1500 = ahead 2000] StationEquations.Count=2 EndingStation=2000 EndingStationWithEquations=2500
[two equations, + back 1500 = ahead 2000]   equation 1: RawStationBack=1500 StationBack=1300 StationAhead=2000 EquationType=Increasing
[two equations, + back 1500 = ahead 2000]   raw     1500 -> '20+00.00'
regions (raw from..to -> displayed from, direction): 0..1000 -> 0 +; 1000..1500 -> 800 +; 1500..2000 -> 2000 +
displayed 900: 2 raw candidate(s): raw 900 formats as '9+00.00', raw 1100 formats as '9+00.00'
displayed 1250: 1 raw candidate(s): raw 1450 formats as '12+50.00'
displayed 1750: 0 raw candidate(s) (falls in a station gap)
displayed 2200: 1 raw candidate(s): raw 1700 formats as '22+00.00'
[one Decreasing equation, back 1000 = ahead 800] StationEquations.Count=1 EndingStation=2000 EndingStationWithEquations=-200
[one Decreasing equation, back 1000 = ahead 800]   raw  1000.01 -> '7+99.99'
[one Decreasing equation, back 1000 = ahead 800]   raw     2000 -> '-2+00.00'
equations removed: Count=0, raw 1100 -> '11+00.00'
```

**Answer:**

- Raw to displayed: `GetStationStringWithEquations(raw)`; at the equation itself the
  ahead value is shown (raw 1000 → `8+00.00`). Raw outside the alignment throws
  `ArgumentOutOfRangeException` (5.1 said `ArgumentException`; this is its subclass).
  `StationBack` is derived (1300 for the second equation); `StationEquationType` has
  `Increasing` and `Decreasing`.
- Displayed to raw: there is no managed method (none on `Alignment` in AeccDbMgd 13.8).
  Core computes it from the equation table, region by region.
- Ambiguity: an `Increasing` equation with ahead < back repeats displayed stations
  (`9+00.00` is raw 900 and raw 1100, and the strings are identical: no region suffix);
  ahead > back leaves a gap (displayed 1750 does not exist). `TryParseStation` must fail
  with a message in both cases, or accept a region qualifier of SheetVol's own.
- All alignment geometry calls (`PointLocation`, sub-entity stations) take raw stations.

### A.12 P9: what headless creation needs

**Ran:** `SVPROBEA_P9` first in `probeA.scr` (`/product C3D`, drawing `blank-c3d.dwg`),
and `probeA-p9*.scr` in four host variants (A.0). Each variant reports loaded modules, then
creates two TINs, a volume surface and an alignment, each step in its own method so a
missing Civil assembly shows as one failed step.

Main run (`/product C3D`):

```text
command line: /i C:\LAN-DT-build\sheetvol-wt\s1\tools\fixtures\blank-c3d.dwg /s C:\LAN-DT-build\sheetvol-wt\s1\tools\scripts\probeA.scr /product C3D /l en-US
SECURELOAD=1 (read only, never set)
loaded ARX/DBX modules: 39; Aec*: 23: aecbase.dbx, aecbaseex.dbx, aecbasemgd.dll, aeccbridge.dbx, aeccbuildingsite.dbx, aecccorebase.crx, aeccdbmgd.dll, aeccdwgindxextractor.dbx, aecchydrology.dbx, aeccland.dbx, aeccmapcheck.dbx, aeccnetwork.dbx, aeccore.crx, aeccparampart.dbx, aeccplanprod.dbx, aeccpointcloud.dbx, aeccpressurepipes.dbx, aeccqto.dbx, aeccrail.dbx, aeccroadway.dbx, aeccsurvey.dbx, aeccvbase.dbx, aecmodeler.dbx
CivilApplication.ActiveDocument: ok; ActiveProduct=Civil3D
CivilDocument.GetCivilDocument(db): ok, 0 surfaces, 0 alignments, 0 surface styles
TinSurface 'PA-P9-BASE-0fd91d' created: 4 triangles
TinSurface 'PA-P9-COMP-0fd91d' created: 4 triangles
TinVolumeSurface created: UnadjustedFill=10000 (cone 10000 x 3 / 3 = 10000) UnadjustedCut=0
Alignment 'PA-P9-AL-20ec7f' created: stations 0..100
```

No `/product C3D`, plain `map2d.dwt` (no Civil objects): the AECC stack loads when the
script NETLOADs SheetVol (it references AeccDbMgd), and everything is created:

```text
args:          /i "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\Template\map2d.dwt" /s "C:\LAN-DT-build\sheetvol-wt\s1\tools\scripts\probeA-p9.scr" /l en-US
_.NETLOAD Assembly file name: C:/Users/.../ApplicationPlugins/SheetVol-dev-s1.bundle/Contents/SheetVol.Civil3D.dll Loading AEC Base...
Loading AECC Base...
Loading AECC Land...
...
Loading AECC Mapcheck...
SVHELLO ok. SheetVol.Civil3D 0.1.0.0 tfm=.NETCoreApp,Version=v10.0
CivilApplication.ActiveDocument: ok; ActiveProduct=Civil3D
CivilDocument.GetCivilDocument(db): ok, 0 surfaces, 0 alignments, 0 surface styles
TinSurface 'PA-P9-BASE-7a9223' created: 4 triangles
TinSurface 'PA-P9-COMP-7a9223' created: 4 triangles
TinVolumeSurface created: UnadjustedFill=10000 (cone 10000 x 3 / 3 = 10000) UnadjustedCut=0
Alignment 'PA-P9-AL-eaaa2f' created: stations 0..100
```

(The `...` stands for 16 more `Loading AECC ...` lines and the user profile prefix.)

| Variant | AECC modules load | TIN, volume surface, alignment |
|---|---|---|
| `/product C3D`, `blank-c3d.dwg` (main run) | at startup | created |
| no `/product`, `blank-c3d.dwg` | at drawing open (the drawing holds Civil objects; `Loading AEC Base...` is log line 12, before the script) | created |
| no `/product`, `map2d.dwt` | at `NETLOAD` of SheetVol | created |
| no `/product`, `map2d.dwt`, `NETLOAD AeccDbMgd.dll` first | at that `NETLOAD` (from Program Files, accepted under `SECURELOAD=1`) | created |
| no `/product`, `map2d.dwt`, `/ld "...\ACA\AecBase.dbx"` | `Loading AEC Base...` still prints at the SheetVol `NETLOAD` | created |

**Answer:** TIN surfaces, volume surfaces and alignments are created headless (FX's
fixtures were the first evidence; this probe adds the variants). On this seat neither
`/product C3D`, nor a prior `NETLOAD AeccDbMgd.dll`, nor `/ld AecBase.dbx` is needed: the
Civil modules demand-load. `SECURELOAD` stays 1 and is never touched; SheetVol loads from
the runner's staging folder under `ApplicationPlugins`. `tools/test-headless.ps1` keeps
`/i /s /product C3D /l en-US` (no switch change was proven necessary; `/product C3D` is
kept because it is proven and costs nothing); its header now records this, and an
`-ExtraArgs` pass-through was added so the `/ld` measurement can be repeated. The 5.2
hypothesis about `/ld AecBase.dbx` is not needed here. Another seat's demand-load
registration could differ (ASSUMED risk only): if `CivilDocument` is ever null headless,
rerun these variants there first.

### A.13 P10: `CivilDocument.GetCivilDocument(db)` vs `CivilApplication.ActiveDocument`

**Ran:** `SVPROBEA_P10` after P6, so `%TEMP%\sheetvol\probeA-src.dwg` exists.

```text
active db: ActiveDocument ok, GetCivilDocument(db) ok, ReferenceEquals=False, Equals=True
active db: ActiveDocument surfaces=10 alignments=6; GetCivilDocument(db) surfaces=10 alignments=6
HostApplicationServices.WorkingDatabase is the active db: True
side db blank-c3d.dwg: GetCivilDocument ok; surfaces=0 alignments=0 surface styles=0; DrawingUnits=Feet
side db blank-c3d.dwg: ActiveDocument still the active drawing: surfaces=10; ReferenceEquals(side, active)=False
side db probeA-src.dwg: GetCivilDocument ok; surfaces=2 alignments=0 surface styles=1; DrawingUnits=Feet
side db probeA-src.dwg: ActiveDocument still the active drawing: surfaces=10; ReferenceEquals(side, active)=False
side db probeA-src.dwg:   EG TinSurface triangles=2 z(125,0)=100 (WorkingDatabase NOT switched)
side db probeA-src.dwg:   FG_A TinSurface triangles=52 z(125,0)=99 (WorkingDatabase NOT switched)
new Database(true, true): GetCivilDocument ok; surfaces=0 surface styles=0 alignment styles=0
```

**Answer:** both work in Core Console. Each call returns a new wrapper
(`ReferenceEquals=False`) for the same document (`Equals=True`, same ids). Use
`GetCivilDocument(db)` everywhere: it works on any database, including side databases
from `ReadDwgFile` and a bare `new Database(true, true)`, and reading surfaces there
(`GetTinProperties`, `FindElevationAtXY`) needs no `WorkingDatabase` switch.
`ActiveDocument` always means the open drawing, whatever database the code is working
on. P6 also built Fixture A into a side database and saved it; `WorkingDatabase` was
switched to the side database for that build, and whether the switch is required for
creation was not tested.

Side fact: `blank-c3d.dwg` has no surface styles (0); `TinSurface.Create(db, name)` makes
one (the saved side database has 1). A1 must not assume a named style exists.

### A.14 P18: `IsOutOfDate`, `Rebuild()`, `AutoRebuild`, `IsReferenceStale`

**Ran:** `SVPROBEA_P18` then `SVPROBEA_P18B` (the next command) on FG_B and `SV-EG-FG_B`.

```text
[as built] FG_B: IsOutOfDate=False AutoRebuild=False IsReferenceStale=False triangles=14 z(1000,15)=100.5 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=False UnadjustedFill=88000.00000000015 UnadjustedCut=0
[AddVertex] FG_B.IsOutOfDate inside the editing transaction = False
[after AddVertex, no Rebuild] FG_B: IsOutOfDate=False AutoRebuild=False IsReferenceStale=False triangles=16 z(1000,15)=102 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=False UnadjustedFill=88000.00000000015 UnadjustedCut=0
[Rebuild FG_B] FG_B.Rebuild(); IsOutOfDate = False
[after FG_B.Rebuild] FG_B: IsOutOfDate=False AutoRebuild=False IsReferenceStale=False triangles=16 z(1000,15)=102 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=False UnadjustedFill=88000.00000000015 UnadjustedCut=0
[volume] SV-EG-FG_B.Rebuild(); IsOutOfDate = False
[after volume Rebuild] FG_B: IsOutOfDate=False AutoRebuild=False IsReferenceStale=False triangles=16 z(1000,15)=102 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=False UnadjustedFill=121000 UnadjustedCut=0
-- a definition edit (breakline) with AutoRebuild off and no Rebuild --
[AddStandardBreaklines] FG_B.IsOutOfDate inside the editing transaction = False
[after a breakline, AutoRebuild off, no Rebuild] FG_B: IsOutOfDate=False AutoRebuild=False IsReferenceStale=False triangles=20 z(1000,15)=102 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=False UnadjustedFill=121000 UnadjustedCut=0
-- AutoRebuild = true on FG_B and on the volume surface, then vertex edits --
[AutoRebuild on] FG_B: IsOutOfDate=False AutoRebuild=True IsReferenceStale=False triangles=20 z(1000,15)=102 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=True UnadjustedFill=121000 UnadjustedCut=0
[after MoveVertex with AutoRebuild on] FG_B: IsOutOfDate=False AutoRebuild=True IsReferenceStale=False triangles=20 z(1000,15)=101.98648648648648 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=True UnadjustedFill=121000 UnadjustedCut=0
[after SetVertexElevation with AutoRebuild on (end of SVPROBEA_P18; SVPROBEA_P18B reads the state in the next command)] FG_B: IsOutOfDate=False AutoRebuild=True IsReferenceStale=False triangles=20 z(1000,15)=102.97747747747748 | EG: IsOutOfDate=False | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=True UnadjustedFill=121000 UnadjustedCut=0
[next command, nothing done yet] FG_B: IsOutOfDate=False AutoRebuild=True z(1010,15)=103 | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=True UnadjustedFill=143000.00000000012
[after explicit volume Rebuild] FG_B: IsOutOfDate=False AutoRebuild=True z(1010,15)=103 | SV-EG-FG_B: IsOutOfDate=False AutoRebuild=True UnadjustedFill=143000.00000000012
```

**Answer:**

- On native TINs every edit tried (`AddVertex`, `AddStandardBreaklines`, `MoveVertex`,
  `SetVertexElevation`, and P4's `AddBoundaries`) takes effect immediately, without
  `Rebuild()` and with `AutoRebuild` off; `IsOutOfDate` never became `True`.
- The volume surface does **not** follow its comparison surface. Its totals stayed at
  88,000 after the vertex edit until `Rebuild()` gave 121,000, with `IsOutOfDate=False`
  the whole time. With `AutoRebuild=True` it still did not change inside the command; it
  had rebuilt (143,000) by the start of the next command. So `IsOutOfDate` cannot drive
  anything; the regen pipeline calls `Rebuild()` on the owned volume surface every time
  (rule 3), and must not rely on `AutoRebuild` inside its own command.
- `IsReferenceStale` is `False` on native surfaces. On reference surfaces: untested
  (P6), so whether `IsOutOfDate`/`IsReferenceStale` flag a source change there is for
  Dan's drawing.

### A.15 P19: `FindElevationAtXY` outside the surface

**Ran:** `SVPROBEA_P19` after P4's hide boundary.

```text
EG.FindElevationAtXY(5000, 5000) [far outside] threw Autodesk.Civil.PointNotOnEntityException Message='Point Outside Surface.'
EG.FindElevationAtXY(2200, 0) [on the east edge] = 100
EG.FindElevationAtXY(2200.000001, 0) [1e-6 outside the east edge] = 100
FG_A.FindElevationAtXY(125, 0) [inside the P4 hide boundary] threw Autodesk.Civil.PointNotOnEntityException Message='Point Outside Surface.'
FG_A.FindElevationAtXY(125, 30) [P1 face outside the hide boundary] = 99.66666666666666
SV-EG-FG_A.FindElevationAtXY(5000, 5000) [volume surface, far outside] threw Autodesk.Civil.PointNotOnEntityException Message='Point Outside Surface.'
SV-EG-FG_A.FindElevationAtXY(125, 0) [volume surface, inside the hole] threw Autodesk.Civil.PointNotOnEntityException Message='Point Outside Surface.'
SV-EG-FG_A.FindElevationAtXY(125, 30) [volume surface, P1 face outside the hole] = -0.3333333333333333
```

**Answer:** `Autodesk.Civil.PointNotOnEntityException` with message
`Point Outside Surface.`, outside the hull and inside hidden areas alike, on TIN and
volume surfaces. A point 1e-6 beyond the edge still returned 100 (a host tolerance), so
coverage sampling on an exact edge is not decisive. A volume surface returns the
elevation difference (comparison − base).

### A.16 P22 and T24: `|Net(0) − Net(1)|` against plan area on Fixture D

**Ran:** `SVPROBEA_P22`: band on alignment C over the arc [300, 614.159] × (−100, +100),
tessellated at 0.01 mid-ordinate on the outer radius (400), on `SV-EG-FG_D`; then a band
on C's out-tangent [614.159, 914.159], which leaves EG at y = −300, against the analytic
area of its part inside EG (polygon clipped to the EG rectangle).

```text
band on C [300.00000000000006, 614.1592653589793] x (-100, 100): arc R=300, outer radius 400, 75 chords (mid-ordinate 9.75E-03 <= 0.01), 153 polygon points
polygon shoelace area = 62829.811526153695; analytic annular sector 20000*pi = 62831.85307179586; polygon vs sector rel diff -3.25E-05
SV-EG-FG_D.GetBoundedVolumes(C band, datum 0): Cut=0 Fill=62829.81152615373 Net=62829.81152615373
SV-EG-FG_D.GetBoundedVolumes(C band, datum 1): Cut=0 Fill=0 Net=0
SV-EG-FG_D.GetBoundedVolumes(C band, datum -5): Cut=0 Fill=376978.8691569223 Net=376978.8691569223
SV-EG-FG_D.GetBoundedVolumes(C band, datum 10): Cut=565468.3037353833 Fill=0 Net=-565468.3037353833
SV-EG-FG_D.GetBoundedVolumes(C band): Cut=0 Fill=62829.81152615373 Net=62829.81152615373
P22: |Net(0) - Net(1)| = 62829.81152615373; polygon area 62829.811526153695; rel diff 5.79E-16; vs analytic sector rel diff -3.25E-05 (T24 tolerance 2e-4)
P22 linearity: (Net(0) - Net(1)) / 1 = 62829.81152615373
P22 linearity: (Net(0) - Net(-5)) / -5 = 62829.8115261537
P22 linearity: (Net(0) - Net(10)) / 10 = 62829.8115261537
P22: Fill without datum = 62829.81152615373 (slab 1.00 thick, so also the area); Fill vs polygon area rel diff 5.79E-16; T7 fill 2327.030 CY vs 2327.106 CY
off-edge band on C [614.1592653589793, 914.1592653589793] x (-100, 100): polygon area 60000.00000000001; analytic part inside EG 34641.016151377575 (coverage 0.57735)
SV-EG-FG_D.GetBoundedVolumes(off-edge band, datum 0): Cut=0 Fill=34641.01615137756 Net=34641.01615137756
SV-EG-FG_D.GetBoundedVolumes(off-edge band, datum 1): Cut=0 Fill=0 Net=0
P22 off-edge: |Net(0) - Net(1)| = 34641.01615137756; analytic on-surface area 34641.016151377575; rel diff -4.20E-16; coverage api/polygon = 0.57735
```

**Answer:** yes. `Net` is linear in the datum with slope −(plan area of data inside), to
5.79E-16 relative, and off the surface edge it is the area of the part with data (−4.20E-16
from analytic). 6.5's datum method can be relied on. The difference from the analytic
sector (−3.25E-05) is the tessellation (inscribed chords at 0.01 mid-ordinate), not the
API: T24 (2e-4) and T7 (2327.030 vs 2327.106 CY, 2e-4) pass with this tessellation. The
Volumes Dashboard comparison for the same boundary is Dan's (5.3).

### A.17 P23: reading visible triangles

**Ran:** `SVPROBEA_P23` (FG_A with P4's hide boundary; an outer boundary and a mask added
to FG_B), then `SVPROBEA_P23SCALE` with grids of 72, 225 and 708 points per side built
with `AddVertices`, read with `GetTriangles(false)` into the contract's `Tri3`.

```text
FG_A: GetTriangles(true).Count=60 GetTriangles(false).Count=56 hidden(IsVisible=false)=4 Triangles.Count=56 GetTinProperties().NumberOfTriangles=56 Vertices.Count=32
FG_A hidden triangle: (105, 20, 99.444444) (125, 0, 99) (145, 20, 99.444444) IsVisible=False IsValid=True
FG_A.FindTriangleAtXY(125, 0) (inside the hide boundary) threw System.ArgumentException Message='Can't find triangle.'
SV-EG-FG_A is a TinVolumeSurface (base Surface); it has no GetTriangles: triangles come from the base and comparison TinSurfaces. GetTinProperties().NumberOfTriangles=72
-- outer boundary -50..2050 x -200..200 on FG_B (non-destructive) --
before: FG_B: GetTriangles(true).Count=20 GetTriangles(false).Count=20 hidden(IsVisible=false)=0 Triangles.Count=20 GetTinProperties().NumberOfTriangles=20 Vertices.Count=13
after outer boundary: FG_B: GetTriangles(true).Count=78 GetTriangles(false).Count=33 hidden(IsVisible=false)=45 Triangles.Count=33 GetTinProperties().NumberOfTriangles=33 Vertices.Count=32
triangles with centroid outside the outer boundary: 45, of which hidden: 45
-- mask 900..1100 x -50..50 on FG_B --
material 'Global' found: True
mask added: Masks.Count=1 type=InSide
after mask + Rebuild: FG_B: GetTriangles(true).Count=78 GetTriangles(false).Count=33 hidden(IsVisible=false)=45 Triangles.Count=33 GetTinProperties().NumberOfTriangles=33 Vertices.Count=32
PA-GRID-72: 5184 points, triangles=10082 (expected 10082); point list 0.00 s, AddVertices 0.02 s, Rebuild 0.02 s
PA-GRID-72: GetTriangles(false) call 0.000 s; read 10082 triangles with 3 vertex Locations each into Tri3 in 0.01 s (947957 tri/s)
PA-GRID-225: 50625 points, triangles=100352 (expected 100352); point list 0.00 s, AddVertices 0.12 s, Rebuild 0.12 s
PA-GRID-225: GetTriangles(false) call 0.000 s; read 100352 triangles with 3 vertex Locations each into Tri3 in 0.14 s (704535 tri/s)
PA-GRID-708: 501264 points, triangles=999698 (expected 999698); point list 0.03 s, AddVertices 1.74 s, Rebuild 1.45 s
PA-GRID-708: GetTriangles(false) call 0.000 s; read 999698 triangles with 3 vertex Locations each into Tri3 in 1.13 s (887357 tri/s)
PA-GRID-708: Triangles property enumeration (no Dispose per triangle) 999698 triangles in 1.16 s
PA-GRID-708: window filter over the cached Tri3 list: 64082 triangles in [354,708]^2 in 34 ms
PA-GRID-708: working set before read 387 MB, after Tri3 list 495 MB (process start of command 315 MB)
PA-GRID-708: total 5.68 s
```

**Answer:**

- API: `TinSurface.GetTriangles(bool includeInvisible)` and the `Triangles` property
  (visible only, same count as `GetTriangles(false)` and as
  `GetTinProperties().NumberOfTriangles`); vertices through `Vertex1..3.Location`
  (`Point3d`). `TinSurfaceTriangle`, the collections and the enumerators expose
  `Dispose()`; the read disposes each triangle. `FindTriangleAtXY` exists but throws
  `ArgumentException` on a hidden spot. No windowed query exists.
- Hidden triangles: stay in the TIN with `IsVisible=false` and appear only in
  `GetTriangles(true)`. A hide boundary hid the 4 triangles of the hole; an outer boundary
  hid all 45 triangles outside it (none deleted); a mask hides nothing (counts unchanged).
  FX's finding that Civil 3D hides slivers by itself (FG_H, 177–178°) is the same flag, so
  `GetTriangles(false)` is the right read for "what the surface is".
- Cost: about 1 M triangles read into `Tri3` in 1.13 s (887 k triangles/s; 1.05 to
  1.48 s across the three runs read back) for about 110 MB more working set; a window filter over the cached list takes 34 ms. A1 can read a
  surface once per regen and let Core index it.
- A `TinVolumeSurface` offers no triangle API; C5's integrator takes the base and the
  comparison TINs, which is what 6.15 already says.
- **Not measurable headless (for Dan):** reference surfaces (P6: no shortcut project
  without changing the user's configuration) and corridor surfaces (building a corridor
  needs an assembly, subassemblies and a profile; not attempted). The API path to check in
  full Civil 3D: `CorridorSurface.SurfaceId` (an `ObjectId`; ASSUMED to open as a
  `TinSurface`), then `GetTriangles(false)`; for a reference surface,
  `GetTriangles(false)` on the reference `TinSurface` itself. Stop condition 12.9 stays
  open until then.

### A.18 Open items and what remains for Dan

| Item | Status | Needs |
|---|---|---|
| P6 volume surface over data-shortcut base and comparison; rebuild after synchronize | OPEN (blocked headless) | Dan's fixture drawing pair in a scratch shortcut project (A.9 steps) |
| P18 `IsOutOfDate` / `IsReferenceStale` on reference surfaces after a source change | OPEN | same sitting |
| P23 triangles on reference and corridor surfaces | OPEN | same sitting, plus any drawing with a corridor surface |
| P22 Volumes Dashboard 2D Area for the same boundary | OPEN | Dan, one boundary polyline on Fixture D |
| P9 on another seat | ASSUMED same | rerun `probeA-p9*.scr` if `CivilDocument` is ever null headless |

## B. Layouts, viewports, tables (S2)

Probes P11, P12, P13, P17, P20, P24. Run 2026-11-05 in the Civil 3D 2026 Core Console
against `tools/fixtures/blank-c3d.dwg`. Code: `src/SheetVol.Civil3D/SelfTest/ProbeB*.cs`.
Commands: `SVPROBEB_P11`, `_P12`, `_P12TRANS`, `_P13`, `_P17`, `_P24`, `_SAVE`, `_VERIFY`,
`_REOPEN`, `_P20A`, `_P20B`, `_P20C`.

### B.0 How to rerun

From the worktree or repo root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-headless.ps1 -Script tools\scripts\probeB.scr -BuildDir <repo>\src\SheetVol.Civil3D\bin\Debug\net10.0-windows
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-headless.ps1 -Script $env:TEMP\sheetvol\probeB-reopen.scr -Drawing $env:TEMP\sheetvol\probeB-out.dwg -BuildDir <same>
```

- The first run builds everything, saves `%TEMP%\sheetvol\probeB-out.dwg`, reads it back
  with `ReadDwgFile`, then activates layouts and runs P20 last, so the `UNDO` cannot touch
  the saved evidence. `SVPROBEB_SAVE` also writes `%TEMP%\sheetvol\probeB-reopen.scr`.
- The second run opens the saved file as the document. This is the user's "open the
  drawing, click the tab" case (E614).
- `-BuildDir` is needed in a worktree. The runner's default prefers
  `C:\LAN-DT-build\SheetVol.Civil3D\bin\...` (the main checkout's output) when that
  folder exists. Worktrees under `C:\LAN-DT-build\sheetvol-wt\` build in-tree, so
  without the flag the runner stages the wrong DLL.
- Logs: `%TEMP%\sheetvol\probeB.log` and `%TEMP%\sheetvol\probeB-reopen.log`. Both runs
  exit with code 0 and take about 5 s each.

### B.1 Summary

| Probe | Answer | Key fact |
|---|---|---|
| P11 | Answered | A layout imported headless from a DWT keeps its plot settings and contents through save and reopen. Use `Layout.Initialize()` (see P12) |
| P12 | Answered | `paper = vp.CenterPoint + CustomScale × (R(+TwistAngle)·(w − ViewTarget) − ViewCenter)`. Matches AutoCAD's own `acedTrans` to 0.000000 in all 8 checks. The 5.2 claim holds: `ViewCenter` is in DCS |
| P13 | Answered | A table is created in a non-current layout, one cell is changed in a second transaction, and exactly 1 cell differs. The change survives save and reopen |
| P17 | Answered | `AddContext` adds a missing scale and `Viewport.AnnotationScale` assigns it. Annotation scale and `CustomScale` are independent: set both |
| P20 | Answered | An aborted transaction rolls back layout creation (16 layouts inside, 15 after). One `UNDO` removes all three layouts one command created (18 to 15) |
| P24 | Answered | Finding a row by label with `GetTextString(FormatOption.IgnoreMtextFormat)` works with merged cells, MTEXT-formatted labels and a deleted row. Only the targeted cells change |

### B.2 Rules for A2 and A3 that come out of these probes

1. **Call `Layout.Initialize()` on every layout created headless before adding any
   viewport** (P12, P11). An uninitialized layout has no overall (paper-space) viewport.
   On first activation AutoCAD promotes the first `Viewport` in the layout block to the
   overall viewport (Number 1), re-zooms it and resets its twist. `LAYOUTCREATEVIEWPORT=1`
   then adds a default viewport as well.
2. **Do not rely on `Viewport.On`.** Inside the creating transaction it always throws
   `eNotInPaperspace`. In a second transaction it is accepted, but only on an initialized
   layout. Either way it reads `False` after save and reopen. First activation turns
   every viewport on. With rule 1 followed, headless-built viewports display (E614
   resolved in Core Console).
3. **Apply the view as 6.6 says** (`ViewTarget` = model centre, `ViewCenter` = (0,0),
   `TwistAngle` = −bearing, `CustomScale` = 1/ModelUnitsPerPaperUnit). A model direction
   at bearing β appears on paper at β + TwistAngle.
4. **A non-rectangular clip moves the viewport.** At commit AutoCAD resizes the viewport
   to the clip's bounding box and shifts `ViewCenter` by the paper offset ÷ scale, in
   paper axes, so the view stays fixed on paper. On regen (E608) do not re-apply
   `ViewCenter = (0,0)` to a clipped viewport: that shifts the view off the clip polygon
   (0.6 paper units in P12A). Use `ViewCenter = (A − vp.CenterPoint) / CustomScale`,
   where A is the paper point the model centre must land on (the centre the clip was
   built around).
   **Correction (A3, 2026-11-05, measured against `acedTrans` and P12A's own numbers): the sign above is reversed. Correct is `ViewCenter = (vp.CenterPoint − A) / CustomScale`. `src/SheetVol.Civil3D/WritersPaper/ViewportWriter.cs` implements the corrected form.**
5. `Viewport.Locked` does not block API edits (E604). Unlocking and relocking is
   harmless but not required.
6. **Tables: write only merge anchors.** `Cell.IsMerged` is true only on the top-left
   cell of a merge. Covered cells report false but still accept text, which is stored
   hidden. Find covered cells from the anchors' `GetMergeRange()`.
7. **Find labels with `GetTextString(FormatOption.IgnoreMtextFormat)`**, never with
   `TextString`, which returns raw MTEXT codes when a label is formatted.
8. **Annotation scale:** look an existing scale up by ratio (`DrawingUnits / PaperUnits`),
   not by name. Add it if absent (E605). Assign `AnnotationScale` *and* set `CustomScale`.
9. **Layout import:** Method S below, with `Initialize()`. A whole-layout clone
   (Method W) keeps the source `TabOrder`, which collides with an existing tab, and needs
   a rename.
10. `PlotPaperSize` is in millimetres whatever `PlotPaperUnits` says. A new layout from
    `CreateLayout` takes its plot device from the user profile; on this seat that is the
    LAN Framework's `C:\LAN-DT\Autodesk\_Firm\Support\Plotting\DWG To PDF.pc3`, with no
    media. `CopyFrom` replaces it with the template's settings.
11. `CVPORT` cannot be set from .NET in a modal command (`eInvalidInput`); `_.MSPACE`
    from a script works. Do not rely on `Viewport.Number` either: one floating viewport
    reported `Number=-1` after activation in both sessions while `On=True` (P17 layout;
    cause not found).

### P11. Import a layout from a DWT headless; plot settings and contents after save and reopen

**Question.** Can a layout be imported from an external DWT in Core Console without
making it current, and do its plot settings and contents survive save and reopen?

**Ran.** `SVPROBEB_P11` builds a template in a side database (`new Database(true, true)`)
and saves it to `%TEMP%\sheetvol\probeB-template.dwt`. The template holds:

- layer `SV-VPORT`;
- block `SV_TABLE_ANCHOR`;
- layout `SV-TEMPLATE`, made with `LayoutManager.CreateLayout` under a swapped
  `WorkingDatabase` and `Layout.Initialize()`d;
- plot settings via `PlotSettingsValidator`: DWG To PDF, ANSI B, monochrome.ctb, 1:1;
- a plan viewport on `SV-VPORT`, an anchor block reference and a text.

The probe then reads the template back with `ReadDwgFile` and imports it into the open
drawing two ways:

- **Method S** (the Autodesk guide's import sample, minus making the layout current):
  `LayoutManager.CreateLayout(name)`, then `Layout.CopyFrom(sourceLayout)`, then
  `Layout.Initialize()`, then `sourceDb.WblockCloneObjects(paper-space entities,
  layout.BlockTableRecordId, map, Ignore, false)`. The source's overall viewport
  (`GetViewports()[0]`) is skipped. `SV-P11-0` omits `Initialize()` as the negative
  control; `SV-P11-1` and `SV-P11-2` are two segments.
- **Method W:** `WblockCloneObjects({sourceLayoutId}, LayoutDictionaryId, ...)`, then
  `LayoutManager.RenameLayout`, then a `TabOrder` fix. Result: `SV-P11-W`.

`SVPROBEB_SAVE` and `SVPROBEB_VERIFY` save, read the file back and diff a snapshot of
every SV-* layout's plot settings and entities. The activation lines come from the
first run; the reopened-document lines come from the second.

**Output (verbatim).**

```text
P11 template: 121 media for 'DWG To PDF.pc3'; picked 'ANSI_B_(11.00_x_17.00_Inches)'
P11 template: PlotStyleMode (true = color-dependent) = True; 'monochrome.ctb' in style sheet list = True (54 sheets)
P11 template: viewports in layout block before Initialize: 0
P11 template: viewports in layout block after Initialize: 1; GetViewports().Count=1
P11 template: plan viewport On=true (side database) threw NotInPaperspace (eNotInPaperspace)
P11 source layout 'SV-TEMPLATE' (reread from disk): device='DWG To PDF.pc3' media='ANSI_B_(11.00_x_17.00_Inches)' styleSheet='monochrome.ctb' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(279.3999938965, 431.799987793)
P11 current layout before import: 'Model'
P11 S 'SV-P11-1': skipping source overall viewport 85
P11 S 'SV-P11-1': CreateLayout -> name='SV-P11-1' tabOrder=4 plot: device='C:\LAN-DT\Autodesk\_Firm\Support\Plotting\DWG To PDF.pc3' media='' styleSheet='' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(0, 0)
P11 S 'SV-P11-1': after CopyFrom -> name='SV-P11-1' tabOrder=4 btrUnchanged=True plot: device='DWG To PDF.pc3' media='ANSI_B_(11.00_x_17.00_Inches)' styleSheet='monochrome.ctb' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(279.3999938965, 431.799987793)
P11 S 'SV-P11-1' Initialize('SV-P11-1'): viewports 0 -> 1; GetViewports().Count=1; CTAB=Model
P11 S 'SV-P11-1': WblockCloneObjects 3 source entities -> 3 primary clones
P11 W: cloned layout name='SV-TEMPLATE' tabOrder=3 btr='*Paper_Space7' plot: device='DWG To PDF.pc3' media='ANSI_B_(11.00_x_17.00_Inches)' styleSheet='monochrome.ctb' rotation=Degrees000 units=Inches type=Layout std=True/StdScale1To1 paper=(279.3999938965, 431.799987793)
P11 W: TabOrder 3 shared with 1 other layout(s); now 6
P11 current layout after import: 'Model' (unchanged=True)
P11 block definitions named like SV_TABLE_ANCHOR: 1 [SV_TABLE_ANCHOR]
VERIFY P11 SV-P11-1 reopened plot equals template: True
VERIFY P11 SV-P11-1   Viewport 6D0 On=False Number=-1 Locked=False layer=0 center=(6, 4.5, 0) w=12 h=9 target=(0, 0, 0) viewCenter=(16.8348750732, 11.9956988258) twist=0 deg customScale=1.1454364758 viewHeight=7.8572668062 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
VERIFY P11 SV-P11-1   Viewport 6D3 On=False Number=-1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 0) twist=0 deg customScale=0.02 viewHeight=400 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
VERIFY P11 SV-P11-1   BlockReference 6D4 name=SV_TABLE_ANCHOR layer=0 pos=(1, 10, 0)
VERIFY P11 SV-P11-1   DBText 6D5 'SV TEMPLATE TITLE' layer=0 pos=(1, 0.75, 0) h=0.2
VERIFY P11 SV-P11-0 reopened plot equals template: True
VERIFY P11 SV-P11-2 reopened plot equals template: True
VERIFY P11 SV-P11-W reopened plot equals template: True
VERIFY snapshot: live 515 keys, file 515 keys, equal 502, different 13
```

All 13 differing keys are viewport `.on` flags, live `True` against file `False` (see
P12). For example:

```text
VERIFY DIFF differs: SV-P11-1|Viewport#6D0.on: live 'True' file 'False'
```

First activation, uninitialized import (`SV-P11-0`) against initialized imports:

```text
P12T 'SV-P11-0' GetViewports().Count=1
P12T 'SV-P11-0' after activation: Viewport 6C1 On=True Number=1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 12.4070138151) twist=0 deg customScale=0.0188317699 viewHeight=424.8140276302 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P11-1' after activation: Viewport 6D3 On=True Number=2 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 0) twist=0 deg customScale=0.02 viewHeight=400 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P11-W' after activation: Viewport 6EC On=True Number=2 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=15 h=8 target=(0, 0, 0) viewCenter=(0, 0) twist=0 deg customScale=0.02 viewHeight=400 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
```

**Answer.** Yes. Both methods import the layout without making it current (CTAB stayed
`Model`).

- **Plot settings** equal the template after import and after save + `ReadDwgFile`, for
  all four imports.
- **Contents survive:** the plan viewport on `SV-VPORT` (scale 0.02), the
  `SV_TABLE_ANCHOR` reference at (1, 10) and the text. Apart from the `On` flags, all
  502 snapshot keys match.
- **`CopyFrom`** copies the plot settings only; the layout name and block are unchanged.
- **`DuplicateRecordCloning.Ignore`** reuses the destination's block definition: one
  `SV_TABLE_ANCHOR` after three imports.
- **Without `Initialize()`** the plan viewport is taken over as the overall viewport at
  first activation (`SV-P11-0`: Number=1, scale 0.02 became 0.0188). With it, the plan
  viewport is Number 2 and unchanged.
- **Method W** works because it carries the template's own overall viewport, but it
  keeps the source `TabOrder` (3, already used by `SV-P11-0`) and the source name.

**Recommendation for A3: Method S with `Initialize()`.**

- Method S sets the final name directly and needs no tab-order fix.
- It does not depend on whether the template's layout was ever activated.
- Rejected: Method W, for the `TabOrder` collision and the rename. Also, cloning with
  `Ignore` when the destination already has a layout of the template's name was **not
  tested**; the probe refuses that case instead of running it (ASSUMED risk: a silent
  skip).
- Not covered: the template was built in code. Dan's real DWT (section 13) has not been
  through this path. Its layout will already have an overall viewport, which the
  `GetViewports()[0]` skip rule handles on the code-built template.

### P12. Viewport created and updated without making its layout current; model-to-paper mapping

**Question.** Can `On`, `ViewTarget`, `ViewCenter`, `TwistAngle`, `CustomScale` and a
non-rectangular clip be set on a viewport in a layout that is not current? Where does a
known model point land on paper? Is `ViewCenter` in display coordinates that rotate with
the twist (5.2)?

**Ran.** `SVPROBEB_P12` creates five layouts with `CreateLayout`, never current. Each
gets one 12 × 7 viewport on `SV-VPORT` centred at paper (8.5, 5.5), scale 1/50. Model
point M = (5000, 3000) and M+100E = (5100, 3000) are drawn in model space.

| Layout | Setup |
|---|---|
| `SV-P12A` | The 6.6 form: target M, ViewCenter (0,0), twist 30°. Created locked with a different view, updated in a second transaction, then clipped by a closed polyline with one arc edge |
| `SV-P12B` | Target (0,0,0), ViewCenter = M's WCS numbers, twist 30° |
| `SV-P12B2` | Target (0,0,0), ViewCenter = R(+30°)·M, twist 30° |
| `SV-P12C` | Target M, ViewCenter (0,0), twist 0 (control) |
| `SV-P12N` | As A but with no `Layout.Initialize()` (negative control) |

Predictions use the read-back values under three hypotheses:

- H1: `ViewCenter` in DCS, where DCS = WCS rotated by +twist about the target.
- H1′: as H1 but rotated by −twist.
- H2: `ViewCenter` is an unrotated WCS offset from the target.

**The oracle.** The script makes each layout current, runs `_.MSPACE`, and
`SVPROBEB_P12TRANS` calls `acedTrans` (P/Invoke into accore.dll) from WCS to DCS to
PSDCS. That is AutoCAD's own transform. LISP `(trans (trans p 0 2) 2 3)` is printed
next to it as a cross-check.

**Output (verbatim).** Creation without making the layout current, and the `On` and
lock behavior:

```text
P12 CTAB before=Model LayoutManager.CurrentLayout='Model' TILEMODE=1 LAYOUTCREATEVIEWPORT=1
P12 SV-P12A create: vp.On=true threw NotInPaperspace (eNotInPaperspace)
P12 SV-P12A CTAB after create=Model
P12 SV-P12A second transaction: On=True (setter accepted)
P12 SV-P12A update while Locked: ViewTarget now (5000, 3000, 0) (was (0, 0, 0), changed=True)
P12 SV-P12A before clip: center=(8.5, 5.5, 0) w=12 h=7
P12 SV-P12A after clip: center=(8.5, 5.5, 0) w=12 h=7 clipOn=True clipId==pline=True
P12 SV-P12A readback after update: On=True Number=-1 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12 SV-P12A clip compensation: dPaperCenter=(0.6, 0) dViewCenter=(30, 0) H1 expects (30.000000, 0.000000) match=True | H2 expects (25.980762, -15.000000) match=False
P12 SV-P12B readback after create: On=False Number=-1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=12 h=7 target=(0, 0, 0) viewCenter=(5000, 3000) twist=30 deg customScale=0.02 viewHeight=350 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12 SV-P12B set vs read: target d=0 viewCenter d=0 twist d=0 customScale d=0 viewHeight - h/scale = 0
P12 SV-P12N second transaction: vp.On=true threw NotInPaperspace (eNotInPaperspace)
P12 SV-P12N second transaction: On=False (setter refused)
P12 CTAB at end=Model LayoutManager.CurrentLayout='Model' (unchanged=True)
```

The oracle, first session:

```text
P12T SV-P12A M: WCS (5000, 3000, 0) -> DCS (0, 0, 0) -> paper (8.500000, 5.500000)
P12T SV-P12A M: |obs-H1|=0.000000 |obs-H1'|=0.000000 |obs-H2|=0.310583  H1 match=True H1' match=True H2 match=False
P12T SV-P12A M+100E: WCS (5100, 3000, 0) -> DCS (86.6025403784, 50, 0) -> paper (10.232051, 6.500000)
P12T SV-P12A M+100E: |obs-H1|=0.000000 |obs-H1'|=2.000000 |obs-H2|=0.310583  H1 match=True H1' match=False H2 match=False
(8.5 5.5 0.0)
(10.2321 6.5 0.0) (10.2321 6.5 0.0)
P12T SV-P12B M: WCS (5000, 3000, 0) -> DCS (2830.1270189222, 5098.0762113533, 0) -> paper (-34.897460, 47.461524)
P12T SV-P12B M: |obs-H1|=0.000000 |obs-H1'|=116.619038 |obs-H2|=60.366456  H1 match=True H1' match=False H2 match=False
P12T SV-P12B M+100E: |obs-H1|=0.000000 |obs-H1'|=118.338498 |obs-H2|=60.366456  H1 match=True H1' match=False H2 match=False
(-34.8975 47.4615 0.0)
P12T SV-P12B2 M: WCS (5000, 3000, 0) -> DCS (2830.1270189222, 5098.0762113533, 0) -> paper (8.500000, 5.500000)
P12T SV-P12B2 M: |obs-H1|=0.000000 |obs-H1'|=116.619038 |obs-H2|=60.366456  H1 match=True H1' match=False H2 match=False
P12T SV-P12B2 M+100E: WCS (5100, 3000, 0) -> DCS (2916.7295593006, 5148.0762113533, 0) -> paper (10.232051, 6.500000)
P12T SV-P12C M: WCS (5000, 3000, 0) -> DCS (0, 0, 0) -> paper (8.500000, 5.500000)
P12T SV-P12C M+100E: WCS (5100, 3000, 0) -> DCS (100, 0, 0) -> paper (10.500000, 5.500000)
```

`CVPORT` from .NET, then `_.MSPACE`:

```text
P12T set CVPORT=2 threw InvalidInput (eInvalidInput)
P12T ORACLE INVALID: the probe viewport is not the active model-space viewport
Command: _.MSPACE
P12T CVPORT now 2 (probe viewport Number=2)
```

First activation, initialized (A) against uninitialized (N):

```text
P12T 'SV-P12A' after activation: Viewport 6FA On=True Number=1 Locked=False layer=0 center=(6, 4.5, 0) w=12 h=9 target=(0, 0, 0) viewCenter=(18.5326147115, 11.9956988258) twist=0 deg customScale=1.1454364758 viewHeight=7.8572668062 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P12A' after activation: Viewport 6FD On=True Number=2 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12T 'SV-P12N' after activation: Viewport 728 On=True Number=1 Locked=False layer=SV-VPORT center=(8.5, 5.5, 0) w=12 h=7 target=(5000, 3000, 0) viewCenter=(5.3031494862, 3.5807085413) twist=0 deg customScale=0.7783832089 viewHeight=8.9929997458 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
P12T 'SV-P12N' after activation: Viewport 7DC On=True Number=-1 Locked=False layer=0 center=(5.3031494862, 3.5807085413, 0) w=8.4850391778 h=5.729133666 target=(0, 0, 0) viewCenter=(16.8348750732, 11.9956988258) twist=0 deg customScale=0.729150964 viewHeight=7.8572668062 dir=(0,0,1) clipOn=False clipId=null annoScale=1:1
```

Saved file read back with `ReadDwgFile`:

```text
VERIFY file SV-P12A|Viewport#6FD.center = (9.1, 5.5, 0)
VERIFY file SV-P12A|Viewport#6FD.clipEntity = 6FF
VERIFY file SV-P12A|Viewport#6FD.clipOn = True
VERIFY file SV-P12A|Viewport#6FD.customScale = 0.02
VERIFY file SV-P12A|Viewport#6FD.on = False
VERIFY file SV-P12A|Viewport#6FD.twist = 0.5235987756
VERIFY file SV-P12A|Viewport#6FD.viewCenter = (30, 0)
VERIFY file SV-P12A|Polyline#6FF.vertices = closed (3.5, 2.5)b0 (13.5, 2.5)b0.4 (13.5, 8.5)b0 (4.5, 7.5)b0
```

Second session: the saved file opened as the document, before and after its first
activation:

```text
REOPEN document 'C:\Users\DRLench\AppData\Local\Temp\sheetvol\probeB-out.dwg' CTAB=Model
REOPEN SV-P12A Viewport 6FD On=False Number=-1 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12T 'SV-P12A' after activation: Viewport 6FD On=True Number=2 Locked=True layer=SV-VPORT center=(9.1, 5.5, 0) w=11.2 h=6 target=(5000, 3000, 0) viewCenter=(30, 0) twist=30 deg customScale=0.02 viewHeight=300 dir=(0,0,1) clipOn=True clipId=6FF annoScale=1:1
P12T SV-P12A M: WCS (5000, 3000, 0) -> DCS (0, 0, 0) -> paper (8.500000, 5.500000)
P12T SV-P12A M+100E: |obs-H1|=0.000000 |obs-H1'|=2.000000 |obs-H2|=0.310583  H1 match=True H1' match=False H2 match=False
```

**Answer.**

- **Without making the layout current:** yes. `ViewTarget`, `ViewCenter`, `TwistAngle`
  and `CustomScale` read back exactly as set (difference 0), and
  `ViewHeight = Height / CustomScale` exactly. CTAB stayed `Model` throughout.
- **`On`:** refused (`eNotInPaperspace`) inside the creating transaction. In a later
  transaction it is accepted on an initialized layout and still refused on an
  uninitialized one. It is not persisted: the file and the reopened document both read
  `False`. First activation turns every viewport on, in both sessions.
- **E614, resolved in Core Console:** a viewport built headless in an initialized layout
  displays at first activation with its view intact, including from the saved file in a
  fresh session. Without `Initialize()`, activation takes the plan viewport over as the
  overall viewport (twist 30 became 0, scale 0.02 became 0.778) and adds a default
  viewport. Untested in full `acad.exe` (Core Console only).
- **Clip:** `NonRectClipEntityId` and `NonRectClipOn` are accepted on a non-current layout
  and survive save. At commit the viewport is resized to the clip's bounding box,
  including the arc bulge: centre (8.5, 5.5) became (9.1, 5.5) and 12 × 7 became
  11.2 × 6. `ViewCenter` is shifted to (30, 0) = (0.6, 0) ÷ 0.02, so M stays at
  (8.5, 5.5).
- **Mapping:** H1 holds to 0.000000 in all 8 checks (4 layouts × 2 points), and LISP
  `trans` agrees. That gives
  `paper = vp.CenterPoint + CustomScale × (R(+TwistAngle)·(w − ViewTarget) − ViewCenter)`.
  - **The 5.2 claim is confirmed:** `ViewCenter` is in DCS and rotates with the twist.
    Entering M's WCS numbers as `ViewCenter` (`SV-P12B`) put M at paper
    (−34.897, 47.462), off the sheet. Entering R(+twist)·M (`SV-P12B2`) put M at the
    viewport centre.
  - **AutoCAD's own clip compensation also follows H1:** (30, 0), not H2's
    (25.98, −15).
  - **The 6.6 form is correct.** Twist sign: an east-pointing model direction under
    twist 30° appears at +30° on paper (offset (1.732, 1.0)), so `Twist = −bearing`
    reads the chord left to right.

### P13. AutoCAD Table created and updated in a layout headless

**Question.** Can a `Table` be created in a non-current layout headless, have a cell
updated in a second transaction, and survive save and reopen?

**Ran.** `SVPROBEB_P13` creates a 4 × 3 table in an initialized, non-current layout
`SV-P13` in transaction 1. Transaction 2 writes cell (2,1). Transaction 3 diffs every
cell against the snapshot taken before the write. The save and reopen check is the
`VERIFY` snapshot.

**Output (verbatim).**

```text
P13 table style='Standard' size=4x3 merges after SetSize: [0,0..0,2]
P13 row styles: r0='' r1='' r2=''
P13 title row cell (0,0): IsMerged=True GetMergeRange=[0,0..0,2]
P13 title row cell (0,1): IsMerged=False GetMergeRange=[0,0..0,2]
P13 title row cell (0,2): IsMerged=False GetMergeRange=[0,0..0,2]
P13 covered (non-anchor) cells: 2 [(0,1) (0,2)]
P13 created in transaction 1: handle=733 height=1.5333333333 width=6 CTAB=Model
P13 transaction 2 wrote cell (2,1) and committed
P13 transaction 3 readback: 1 cell(s) changed: (2,1) 'P13 R2C1-' -> 'P13 UPDATED-'
P13 tx3 r2: 'P13 R2C0' | 'P13 UPDATED' | 'P13 R2C2'
VERIFY file SV-P13|Table#733.cell[2,0] = 'P13 R2C0'-
VERIFY file SV-P13|Table#733.cell[2,1] = 'P13 UPDATED'-
VERIFY file SV-P13|Table#733.cell[2,2] = 'P13 R2C2'-
```

An earlier run the same day wrote text into every cell before the covered-cell guard
existed. The covered cells kept their own hidden text:

```text
P13 tx1 r0: 'P13 R0C0'[0,0..0,2] | 'P13 R0C1' | 'P13 R0C2'
```

**Answer.** Yes. Exactly one cell changed in the update, and the value survived save and
reopen. Three facts for A2:

- The `Standard` table style merges row 0 across all columns as soon as `SetSize` runs.
- `Cell.IsMerged` is true only on a merge's anchor. Covered cells report false but
  return the full range from `GetMergeRange()`.
- Text written to a covered cell is accepted silently and stored hidden.
- `Cell.Style` returns an empty string, so row style names are not exposed that way.

Not checked headless: whether the table's display block regenerates (no graphics). The
cell contents are checked.

### P17. Annotation scale on a viewport; adding a missing scale

**Question.** Can an annotation scale be assigned to a viewport, and can a scale missing
from the drawing's list be added first?

**Ran.** `SVPROBEB_P17` lists `ACDB_ANNOTATIONSCALES`, assigns the existing 1:50 scale to
viewport vpE, adds a missing 1:37 scale with `ObjectContextCollection.AddContext`, then
assigns it to vpM. Both are in a non-current initialized layout `SV-P17`. It sets
`CustomScale` and `AnnotationScale` in both orders.

**Output (verbatim).**

```text
P17 scale list: 33 scales; CANNOSCALE='1:1'; current context='1:1'
P17 scales: '1:1' 1:1; '1:2' 1:2; '1:4' 1:4; '1:5' 1:5; '1:8' 1:8; '1:10' 1:10; '1:16' 1:16; '1:20' 1:20; '1:30' 1:30; '1:40' 1:40; '1:50' 1:50; '1:100' 1:100; '2:1' 2:1; '4:1' 4:1; '8:1' 8:1; '10:1' 10:1; '100:1' 100:1; '1/128" = 1'-0"' 0.0078125:12; '1/64" = 1'-0"' 0.015625:12; '1/32" = 1'-0"' 0.03125:12; '1/16" = 1'-0"' 0.0625:12; '3/32" = 1'-0"' 0.09375:12; '1/8" = 1'-0"' 0.125:12; '3/16" = 1'-0"' 0.1875:12; '1/4" = 1'-0"' 0.25:12; '3/8" = 1'-0"' 0.375:12; '1/2" = 1'-0"' 0.5:12; '3/4" = 1'-0"' 0.75:12; '1" = 1'-0"' 1:12; '1-1/2" = 1'-0"' 1.5:12; '3" = 1'-0"' 3:12; '6" = 1'-0"' 6:12; '1'-0" = 1'-0"' 12:12
P17 existing scale for the assign test: '1:50' ratio 50
P17 ratio 37 before add: absent; HasContext('1" = 37'')=False
P17 vpE after assign: annoScale='1:50' customScale=0.02
P17 after add: ratio 37 present as '1" = 37'' scale=0.027027027; HasContext=True; list count=34
P17 vpM order 1 (CustomScale 1/20, then annoScale 1:37): annoScale='1" = 37'' customScale=0.05 (1/37=0.027027027)
P17 vpM order 2 (then CustomScale 1/37): annoScale='1" = 37'' customScale=0.027027027
VERIFY file SV-P17|Viewport#756.annoScale = 1:50
VERIFY file SV-P17|Viewport#757.annoScale = 1" = 37'
VERIFY file SV-P17|Viewport#757.customScale = 0.027027027
VERIFY file scales|1" = 37' = 1:37
```

**Answer.** Yes to both.

- `Viewport.AnnotationScale = scale` works on a non-current layout.
- `AddContext(new AnnotationScale { Name, PaperUnits = 1, DrawingUnits = 37 })` grows the
  list from 33 to 34. The assignment and the added scale survive save and reopen.
- Assigning an annotation scale does not change `CustomScale`, and setting `CustomScale`
  does not change the annotation scale. The writer must set both.
- The fixture's list names civil scales `1:50`, not `1" = 50'`, so look scales up by
  ratio, not by name (E605).

### P20. Layout creation, an aborted transaction, and one UNDO

**Question.** Is layout creation rolled back when its transaction is aborted, and by one
`UNDO`?

**Ran.** `SVPROBEB_P20A` aborts three creation paths:

- `LayoutManager.CreateLayout` inside a transaction;
- Method S (CreateLayout + CopyFrom + Initialize + WblockCloneObjects) in one
  transaction;
- the Method W clone.

`SVPROBEB_P20B` commits all three in one command. The script then runs `_.UNDO` `1`, and
`SVPROBEB_P20C` counts again.

**Output (verbatim).**

```text
P20 before: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P20 abort-C inside transaction: layoutDictionary=16 exists=True
P20 abort-C after Abort: exists=False id.IsErased=True id.IsValid=True
P20 after abort-C: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P11 S 'SV-P20-ABORT-S': aborted
P20 abort-S after Abort: exists=False id.IsErased=True id.IsValid=True
P20 after abort-S: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P11 W: aborted
P20 abort-W after Abort: exists(SV-TEMPLATE)=False id.IsErased=True id.IsValid=True
P20 after abort-W: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P20 UNDOCTL=61 (bit 1 = undo enabled)
P20 before committed creates: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
P20 after committed creates (one command): layoutDictionary=18 LayoutManager.LayoutCount=18 paperSpaceBlocks=17
Command: _.UNDO
Current settings: Auto = On, Control = All, Combine = Yes, Layer = Yes
Enter the number of operations to undo or [Auto/Control/BEgin/End/Mark/Back] <1>: 1
SVPROBEB_P20B
P20 after UNDO 1: SV-P20-UNDO-C exists=False
P20 after UNDO 1: SV-P20-UNDO-S exists=False
P20 after UNDO 1: SV-P20-UNDO-W exists=False
P20 after UNDO 1: layoutDictionary=15 LayoutManager.LayoutCount=15 paperSpaceBlocks=14
```

**Answer.** Yes to both.

- **Abort:** the layout dictionary, `LayoutManager.LayoutCount` and the paper-space block
  table all return to their earlier counts (16 inside the transaction, 15 after). The
  layout's ObjectId reads `IsErased=True`. This holds for all three paths, so a regen
  that fails mid-way and aborts leaves no half-built layout.
- **UNDO:** one `UNDO` reverses a whole command's worth of layout creation, 18 to 15.

### P24. HCFCD table with merged cells: find a row by label, update one cell

**Question.** Can the HCFCD layout be built with merged cells, a row found by its label
text, and one cell updated without disturbing the others, in model space and in a
layout?

**Ran.** `SVPROBEB_P24` builds a 9 × 3 stand-in of the HCFCD table from 6.16 and A13
(bound rows 6 to 9, 1-based). It does this twice: in model space and in an initialized,
non-current layout `SV-P24`.

- The row 6 label carries MTEXT bold formatting, as a user's in-place edit would leave
  it.
- The finder reads column 0 with `GetTextString(FormatOption.IgnoreMtextFormat)` and
  matches the start of the text.
- The value column is the label's merge range `RightColumn + 1`.

Each step is its own transaction, with a full cell and merge diff:

1. Create.
2. Update the area cell only.
3. Bind length and material.
4. A user edit: delete the WETLANDS row and type into the centre-coordinates cell.
5. Bind again.

**Output (verbatim, layout copy; the model-space copy printed the same values).**

```text
P24 layout merges after SetSize (table style default): [0,0..0,2]
P24 layout merges after unmerging all: none
P24 layout created: handle=78F owner=*Paper_Space17 9x3 merges: [0,0..0,2] [1,1..1,2] [2,1..2,2] [3,0..3,2] [4,1..4,2] [5,1..5,2] [6,1..6,2] [7,0..7,2]
P24 layout tx1 r5: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' | 'XX LF'[5,1..5,2] | ''
P24 layout tx1 r6: 'TOTAL IMPACTED AREA (BELOW OHWM)' | 'XX AC'[6,1..6,2] | ''
P24 layout tx1 r7: 'TOTAL CUT MATERIAL'[7,0..7,2] | '' | ''
P24 layout tx1 r8: 'SEDIMENT' | 'XX CY' | 'XX CY/LF'
P24 layout row 5 label reads: TextString='{\fArial|b1|i0|c0|p34;TOTAL IMPACTED LENGTH (BELOW OHWM)}' | GetTextString(FormatOptionNone)='{\fArial|b1|i0|c0|p34;TOTAL IMPACTED LENGTH (BELOW OHWM)}' | GetTextString(IgnoreMtextFormat)='TOTAL IMPACTED LENGTH (BELOW OHWM)'
P24 layout merged non-anchor cell (6,2): TextString='' IsMerged=False
P24 layout update-one: 'TOTAL IMPACTED AREA (BELOW OHWM)' found at row 6; wrote 1 value(s) from column 1
P24 layout update-one: 1 cell(s) changed: (6,1) 'XX AC[6,1..6,2]' -> '1.23 AC[6,1..6,2]'
P24 layout bind: 3 cell(s) changed: (5,1) 'XX LF[5,1..5,2]' -> '1,234 LF[5,1..5,2]'; (8,1) 'XX CY-' -> '567 CY-'; (8,2) 'XX CY/LF-' -> '0.46 CY/LF-'
P24 layout user edit: deleted row 4 (WETLANDS); typed into row 1; now 8 rows; merges: [0,0..0,2] [1,1..1,2] [2,1..2,2] [3,0..3,2] [4,1..4,2] [5,1..5,2] [6,0..6,2]
P24 layout rebind: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' found at row 4; wrote 1 value(s) from column 1
P24 layout rebind: 'TOTAL IMPACTED AREA (BELOW OHWM)' found at row 5; wrote 1 value(s) from column 1
P24 layout rebind: 'SEDIMENT' found at row 7; wrote 2 value(s) from column 1
P24 layout rebind: missing labels=0; 3 cell(s) changed: (4,1) '1,234 LF[4,1..4,2]' -> '1,300 LF[4,1..4,2]'; (5,1) '1.23 AC[5,1..5,2]' -> '1.25 AC[5,1..5,2]'; (7,1) '567 CY-' -> '600 CY-'
P24 layout final r1: 'CENTER COORDINATES' | 'N 13,812,345.67  E 3,101,234.56 (TYPED)'[1,1..1,2] | ''
P24 layout final handle=78F (created as same object, never recreated)
VERIFY P24 Model table 75F: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' row 4 -> '1,300 LF'; 'TOTAL IMPACTED AREA (BELOW OHWM)' row 5 -> '1.25 AC'; 'SEDIMENT' row 7 -> '600 CY' / '0.46 CY/LF'
VERIFY P24 SV-P24 table 78F: 'TOTAL IMPACTED LENGTH (BELOW OHWM)' row 4 -> '1,300 LF'; 'TOTAL IMPACTED AREA (BELOW OHWM)' row 5 -> '1.25 AC'; 'SEDIMENT' row 7 -> '600 CY' / '0.46 CY/LF'
VERIFY file *Model|Table#75F.cell[1,1] = 'N 13,812,345.67  E 3,101,234.56 (TYPED)'[1,1..1,2]
```

**Answer.** Yes, in model space and in a layout.

- **One cell:** updating one bound value changed exactly 1 cell.
- **A full bind:** changed exactly the cells it targeted (3).
- **After the user deleted a row:** the labels were found at their shifted rows (4, 5, 7).
  Merges moved with their rows, the typed centre coordinates were kept, and the bind
  changed only its targets. The material average was rewritten with the same text, so 3
  cells changed, not 4.
- **On the reopened file:** the finder works and every value survived.
- **Formatted labels:** `TextString` returns raw MTEXT codes for a formatted label, so
  only `GetTextString(IgnoreMtextFormat)` finds it.
- **Merges:** the default style's title-row merge has to be undone before applying the
  HCFCD merges.
- **Stand-in:** the HCFCD layout used here comes from 6.16 and A13. Dan's real table
  (section 13) is still open.

### Not covered by S2, for the orchestrator

- `tools/test-headless.ps1` picks the main checkout's build when it runs inside a
  worktree unless `-BuildDir` is given (B.0). The runner belongs to the orchestrator.
- The probe commands print `SV-E190` when a probe step fails at the command boundary.
  The code was chosen for the probes; confirm or reassign it.
- A real LAN DWT (section 13) has not been through P11.
- The HCFCD table in P24 is a stand-in; Dan's table is still open.
- E614 is answered in Core Console only. One manual open of `probeB-out.dwg` in full
  Civil 3D would close it for `acad.exe`.
- The `Number=-1` on viewport 756 in `SV-P17` is unexplained.

## C. Frames, events, modeless UI (S3)

Probes P14, P15, P21. Headless parts run 2026-11-05 in the Civil 3D 2026 Core Console
(2026.2.3, AeccDbMgd 13.8.0.1873) against `tools/fixtures/blank-c3d.dwg`. Code:
`src/SheetVol.Civil3D/SelfTest/ProbeCCommon.cs`, `ProbeCEvents.cs`, `ProbeCFrames.cs`
(Core Console safe) and `src/SheetVol.UI/Probe/**` (the WPF window; full AutoCAD only).
Commands: `SVPROBEEVENTS`, `SVPROBEEVENTSOFF`, `SVPROBEEVENTSDUMP`, `SVPROBEMARK`,
`SVPROBEC_EDIT`, `SVPROBEC_STATE` (P15); `SVPROBEFRAMES`, `SVPROBEC_SOLVECHECK` (P14);
`SVPROBEUI`, `SVPROBEUI_WRITE`, `SVPROBEUI_PICK` (P21). A probe step that fails at the
command boundary prints `SV-E198`; the final runs printed none. Logs are timestamped and
append-only under `%TEMP%\sheetvol\` (`probeEvents.log`, `probeFrames.log`, `probeUI.log`).
Dan's click-through is `docs/dan-probe-steps.md`.

Output blocks follow section A's convention: each line is copied as printed; some lines
are left out (here the per-dump `counts since the last dump` blocks and the `===== ...
begin/end =====` banners); the only edit is the user-profile prefix shortened to `%TEMP%`.

### C.0 How to rerun

From the worktree or repo root (the runner stages this checkout's own build):

```powershell
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-events.scr          # P15 pre-check, 5-10 s
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-solvecheck.scr      # P14 solver self-check
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-frames-control.scr  # P14 viewport control (FX template)
.\tools\test-headless.ps1 -Script .\tools\scripts\probeC-frames.scr -Drawing .\tools\fixtures\frames-src.dwg   # P14, once Dan's fixtures exist
```

All four exited 0 on the final build. The runner stages only the engine's output folder:
`SheetVol.UI.dll` is not staged (MEASURED: after a run the stage folder held
`SheetVol.Civil3D.*` and `SheetVol.Core.*` only). `docs/dan-probe-steps.md` part 0 copies it.

### C.1 Summary

| Probe | Status | Key fact |
|---|---|---|
| P15 | PARTLY: headless half MEASURED; interactive half (grips, Layout Tools, match lines, synchronize) coded and waiting for Dan | Civil 3D's managed API declares **no events**. Alignment, surface and Civil object changes are seen only through `Database.ObjectModified` and friends, by RX class name, bracketed by `Document.CommandWillStart`/`CommandEnded`. `Application.Idle` never fired during a Core Console script |
| P21 | CODED, waiting for Dan's one `SVPROBEUI` run | Routes and API facts below are MEASURED from assembly metadata; behaviour in acad.exe is not measured yet |
| P14 | CODED, solver self-checked; waiting for Dan's view-frame fixtures (HANDOFF.md section 13). **Cannot be run until Dan provides the drawing pair** | The API gives stations, alignment, `Sheet`, `SheetSet` and no geometry; geometry comes from `Explode`. Once the fixtures exist P14 runs headless, no Dan needed |

### C.2 Rules for L1, L2, A4 and U2 that come out of this so far

1. **No Civil 3D events (P15).** Reflection over the loaded AeccDbMgd in the 2026 host:
   1887 exported types, 0 public events (C.3, line 8). An offline metadata read of the
   installed assemblies (`System.Reflection.Metadata`, no load) gave 0 public events in
   AeccDbMgd and AeccDataShortcutMgd for both 2025 and 2026, and 0 in AecBaseMgd. Only
   AeccUiMgd declares events (13 in 2025, 14 in 2026: palette, panorama and
   `AeccReferenceStatusUtils.ReferenceStatusChanged`). It is a UI assembly, absent from the
   API reference and unusable from the engine, and was not explored. L2 wires
   `Database.ObjectModified`, `ObjectErased`, `ObjectAppended` (plus `ObjectUnappended`
   and `ObjectReappended`) and filters by `DBObject.GetRXClass().Name`: `AeccDbAlignment`,
   `AeccDbSurfaceTin` and so on.
2. **One alignment edit is a burst (E805).** One API edit of an alignment fired
   `ObjectModified` on the alignment plus six on its label groups (`AeccDbAlignment*Labeling`).
   After `CommandEnded`, `AcDbSortentsTable` was modified with no command active. L2's
   "dirty flag, then one-shot work after the command" design holds. The draw-order table
   change arrives after `CommandEnded` and must not re-arm a regen on its own.
3. **Undo is recognised by command name, not by `IsUndoing` (E804).** During `U` of an
   alignment edit, label groups fired `ObjectModified` with `IsUndoing=False` in between
   `IsUndoing=True` lines (C.3, #72 to #90): Civil 3D recomputes labels while undoing. The
   tracker must treat everything between `CommandWillStart 'U'`/`'UNDO'`/`'REDO'` and the
   matching end as undo, whatever `IsUndoing` says.
4. **Undo shapes (MEASURED).** Undoing an erase fires `ObjectErased Erased=False`. Undoing
   an append fires `ObjectUnappended`, then `ObjectErased Erased=True`. Undoing a TIN edit
   fires one `ObjectModified` on the surface. All carry `IsUndoing=True`, except the
   label-group lines in rule 3.
5. **Read-only commands carry `CommandFlags.NoUndoMarker` (A4).** A command that changes
   nothing is still an undo step: after `SVHELLO` (Modal, no flag), `_.U` undid `SVHELLO`,
   not the line appended before it. The second `U` then removed the line (C.3, from #111).
   The probe's own commands carry the flag, and `U` skipped them. `SVSTATUS`, `SVEXPORT`
   and the dialog's read-only helpers need it, or a user's `SVREGEN`, `SVSTATUS`, `U`
   undoes nothing and E804 ("one UNDO reverts one regen") fails in practice.
6. **No `Application.Idle` in Core Console scripts (L2).** The one-shot was armed after
   every command and fired 0 times in a 26-command script. Batch regen (`regen-sheets.ps1`)
   and anything headless must do its work in the command, never in an Idle callback.
   Whether Idle fires after each command in acad.exe is in Dan's P15 run.
7. **`TinSurface.FindVertexAtXY` returns the nearest vertex**, not an exact match and not
   an exception: with the apex at (1750, 0) deleted it returned (1705, 45, 100). Callers
   compare the location.
8. **The WPF overloads of `ShowModelessWindow` live in AcCoreMgd (U2).** In the 2025
   reference assemblies, AcMgd's `Autodesk.AutoCAD.ApplicationServices.Application`
   derives from `Core.Application` (AcCoreMgd). Its own `ShowModelessWindow` overloads take
   a `System.Uri htmlPage`. The `System.Windows.Window` overloads are declared on
   `Core.Application`: `(Window formToShow)`, `(Window owner, Window formToShow)`,
   `(IntPtr owner, Window formToShow)`, and both two-argument forms plus `bool
   persistSizeAndPosition`. `Application.ShowModelessWindow(window)` therefore binds to the
   inherited one-argument overload. `MainWindow` is also on `Core.Application` only. Read
   from metadata (parameter names and signature blobs); not executed yet.
9. **Exceptions must not escape an `ExecuteInCommandContextAsync` callback (U2).** Autodesk
   forum guidance says an exception inside the delegate can crash AutoCAD
   ([forum thread](https://forums.autodesk.com/t5/net-forum/run-a-sample-command-with-parameters-using-c-net/m-p/13335082)).
   Community source, not measured. The probe wraps every callback body in a try/catch.
10. **`DocumentLockModeChangedEventArgs.IsVetoed` is not public.** Its getter is
    assembly-private in AcCoreMgd 2025, so only `GlobalCommandName`, `CurrentMode`,
    `MyPreviousMode`, `MyCurrentMode` and `Document` are usable.

### C.3 P15 headless pre-check (MEASURED)

**Ran:** `probeC-events.scr`. It builds FX alignment S (`SVFIXTURE S`, a fixed line from
(0,0) to (2000,0)) and Fixture A (EG, FG_A), and turns the probe on. Then, one API edit per
command: the end PI of S moves 50 ft north (`AlignmentLine.PassThroughPoint2`; the API has
no PI collection, and on a fixed line the pass-through points are the PIs); a station
equation (raw 1000, ahead 800); FG_A's P5 apex deleted (`DeleteVertex`) and added back
(`AddVertex`); a line appended, then erased. Then `_.U` six times with a state check and a
dump in between, then a control (append, `SVHELLO`, `U`, `U`), then probe off and one more
append that must record nothing.

```text
Command: SVPROBEEVENTS
process: C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe  Core Console: True
runtime: .NET 10.0.14  OS: Microsoft Windows 10.0.26200
AcDbMgd 25.1.0.0  AcCoreMgd 25.1.0.0  AeccDbMgd 13.8.0.1873  SheetVol.Civil3D 0.1.0.0
ACADVER=25.1  _VERNUM=W.179.0.0 (UNICODE)  PRODUCT=AutoCAD
active document: C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg  documents open: 1
subscribed: Database.ObjectAppended, ObjectModified, ObjectErased, ObjectUnappended, ObjectReappended; Document.CommandWillStart, CommandEnded, CommandCancelled, CommandFailed; DocumentCollection.DocumentActivated; Application.Idle (one-shot after each command end) on 1 document(s)
Civil 3D API events (run-time reflection over loaded Aecc*Mgd, UI assemblies excluded): AeccDbMgd 13.8.0.1873: 1887 exported type(s), 0 public event(s)
log: %TEMP%\sheetvol\probeEvents.log
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 0) EndingStation=2000 StationEquations.Count=0
FG_A: triangles=52 vertex at (1750, 0): (1750, 0, 101)
appended line: none yet
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: PI
S 6BF: before PassThroughPoint1=(0, 0) PassThroughPoint2=(2000, 0) EndingStation=2000
S: set PassThroughPoint2=(2000, 50); after PassThroughPoint2=(2000, 50) EndingStation=2000.6249023742557 Length=2000.6249023742557
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: EQ
S 6BF: StationEquations.Add(1000, 800, Increasing); Count=1
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: VTXDEL
FG_A 6E9: DeleteVertex at (1750, 0); triangles 52 -> 50
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: VTXADD
FG_A 6E9: AddVertex(1750, 0, 101); triangles 50 -> 52
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: APPEND
appended Line 6EA (0, -500, 0) -> (100, -500, 0)
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: ERASE
erased Line 6EA
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 50) EndingStation=2000.6249023742557 StationEquations.Count=1
FG_A: triangles=52 vertex at (1750, 0): (1750, 0, 101)
appended line 6EA: IsErased=True
Command: SVPROBEEVENTSDUMP
event probe on
42 line(s) recorded since the last dump:
  2026-11-05 12:11:07.831 #1 attached to document 'C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.862 #2 CommandEnded 'SVPROBEEVENTS' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.863 #3 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.869 #4 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.870 #5 CommandWillStart 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.875 #6 ObjectModified AeccDbAlignment/Alignment h=6BF cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.876 #7 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.877 #8 ObjectModified AeccDbAlignmentStationLabeling/AlignmentStationLabelGroup h=6C0 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.878 #9 ObjectModified AeccDbAlignmentGeomPointLabeling/AlignmentGeometryPointLabelGroup h=6C1 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.879 #10 ObjectModified AeccDbAlignmentStaEquLabeling/AlignmentStationEquationLabelGroup h=6C2 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.881 #11 ObjectModified AeccDbAlignmentDesignSpeedLabeling/AlignmentDesignSpeedLabelGroup h=6C3 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.881 #12 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.882 #13 CommandEnded 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.883 #14 ObjectModified AcDbSortentsTable/DrawOrderTable h=1C0 cmd=(none) IsUndoing=False
  2026-11-05 12:11:07.887 #15 CommandWillStart 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.889 #16 ObjectModified AeccDbAlignment/Alignment h=6BF cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.891 #17 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.891 #18 ObjectModified AeccDbAlignmentStationLabeling/AlignmentStationLabelGroup h=6C0 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.892 #19 ObjectModified AeccDbAlignmentGeomPointLabeling/AlignmentGeometryPointLabelGroup h=6C1 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.892 #20 ObjectModified AeccDbAlignmentStaEquLabeling/AlignmentStationEquationLabelGroup h=6C2 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.893 #21 ObjectModified AeccDbAlignmentDesignSpeedLabeling/AlignmentDesignSpeedLabelGroup h=6C3 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.893 #22 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.894 #23 CommandEnded 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.894 #24 ObjectModified AcDbSortentsTable/DrawOrderTable h=1C0 cmd=(none) IsUndoing=False
  2026-11-05 12:11:07.901 #25 CommandWillStart 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.904 #26 ObjectModified AeccDbSurfaceTin/TinSurface h=6E9 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.904 #27 CommandEnded 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.905 #28 CommandWillStart 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.907 #29 ObjectModified AeccDbSurfaceTin/TinSurface h=6E9 cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.907 #30 CommandEnded 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.908 #31 CommandWillStart 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.910 #32 ObjectModified AcDbBlockTableRecord/BlockTableRecord h=1F cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.911 #33 ObjectAppended AcDbLine/Line h=6EA cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.911 #34 ObjectModified AcDbLine/Line h=6EA cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.911 #35 ObjectModified AcDbLine/Line h=6EA cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.912 #36 CommandEnded 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.912 #37 CommandWillStart 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.914 #38 ObjectErased AcDbLine/Line h=6EA cmd=SVPROBEC_EDIT IsUndoing=False Erased=True
  2026-11-05 12:11:07.915 #39 CommandEnded 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.915 #40 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.916 #41 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.917 #42 CommandWillStart 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: armed after 'SVPROBEC_STATE', not fired yet; fired 0 time(s) this session
Command: _.U
SVPROBEC_EDIT
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 50) EndingStation=2000.6249023742557 StationEquations.Count=1
FG_A: triangles=52 vertex at (1750, 0): (1750, 0, 101)
appended line 6EA: IsErased=False
Command: SVPROBEEVENTSDUMP
event probe on
7 line(s) recorded since the last dump:
  2026-11-05 12:11:07.923 #43 CommandEnded 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.923 #44 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.924 #45 ObjectErased AcDbLine/Line h=6EA cmd=U IsUndoing=True Erased=False
  2026-11-05 12:11:07.927 #46 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.928 #47 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.929 #48 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.929 #49 CommandWillStart 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: armed after 'SVPROBEC_STATE', not fired yet; fired 0 time(s) this session
Command: _.U
SVPROBEC_EDIT
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 50) EndingStation=2000.6249023742557 StationEquations.Count=1
FG_A: triangles=52 vertex at (1750, 0): (1750, 0, 101)
appended line 6EA: IsErased=True
Command: SVPROBEEVENTSDUMP
event probe on
10 line(s) recorded since the last dump:
  2026-11-05 12:11:07.930 #50 CommandEnded 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.930 #51 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.931 #52 ObjectModified AcDbBlockTableRecord/BlockTableRecord h=1F cmd=U IsUndoing=True
  2026-11-05 12:11:07.931 #53 ObjectUnappended AcDbLine/Line h=6EA cmd=U IsUndoing=True
  2026-11-05 12:11:07.932 #54 ObjectModified AcDbLine/Line h=6EA cmd=U IsUndoing=True
  2026-11-05 12:11:07.932 #55 ObjectErased AcDbLine/Line h=6EA cmd=U IsUndoing=True Erased=True
  2026-11-05 12:11:07.935 #56 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.936 #57 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.937 #58 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.938 #59 CommandWillStart 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: armed after 'SVPROBEC_STATE', not fired yet; fired 0 time(s) this session
Command: _.U
SVPROBEC_EDIT
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 50) EndingStation=2000.6249023742557 StationEquations.Count=1
FG_A: triangles=50 vertex at (1750, 0): (1705, 45, 100)
appended line 6EA: IsErased=True
Command: SVPROBEEVENTSDUMP
event probe on
7 line(s) recorded since the last dump:
  2026-11-05 12:11:07.939 #60 CommandEnded 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.939 #61 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.941 #62 ObjectModified AeccDbSurfaceTin/TinSurface h=6E9 cmd=U IsUndoing=True
  2026-11-05 12:11:07.944 #63 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.944 #64 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.946 #65 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.946 #66 CommandWillStart 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: armed after 'SVPROBEC_STATE', not fired yet; fired 0 time(s) this session
Command: _.U
SVPROBEC_EDIT
Command: _.U
SVPROBEC_EDIT
Command: _.U
SVPROBEC_EDIT
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 0) EndingStation=2000 StationEquations.Count=0
FG_A: triangles=52 vertex at (1750, 0): (1750, 0, 101)
appended line 6EA: IsErased=True
Command: SVPROBEEVENTSDUMP
event probe on
44 line(s) recorded since the last dump:
  2026-11-05 12:11:07.947 #67 CommandEnded 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.947 #68 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.948 #69 ObjectModified AeccDbSurfaceTin/TinSurface h=6E9 cmd=U IsUndoing=True
  2026-11-05 12:11:07.951 #70 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.952 #71 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.953 #72 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=U IsUndoing=True
  2026-11-05 12:11:07.953 #73 ObjectModified AeccDbAlignmentDesignSpeedLabeling/AlignmentDesignSpeedLabelGroup h=6C3 cmd=U IsUndoing=True
  2026-11-05 12:11:07.953 #74 ObjectModified AeccDbAlignmentStaEquLabeling/AlignmentStationEquationLabelGroup h=6C2 cmd=U IsUndoing=True
  2026-11-05 12:11:07.953 #75 ObjectModified AeccDbAlignmentGeomPointLabeling/AlignmentGeometryPointLabelGroup h=6C1 cmd=U IsUndoing=True
  2026-11-05 12:11:07.954 #76 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=U IsUndoing=False
  2026-11-05 12:11:07.954 #77 ObjectModified AeccDbAlignmentStationLabeling/AlignmentStationLabelGroup h=6C0 cmd=U IsUndoing=True
  2026-11-05 12:11:07.954 #78 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=U IsUndoing=False
  2026-11-05 12:11:07.955 #79 ObjectModified AeccDbAlignmentDesignSpeedLabeling/AlignmentDesignSpeedLabelGroup h=6C3 cmd=U IsUndoing=False
  2026-11-05 12:11:07.955 #80 ObjectModified AeccDbAlignmentStaEquLabeling/AlignmentStationEquationLabelGroup h=6C2 cmd=U IsUndoing=False
  2026-11-05 12:11:07.955 #81 ObjectModified AeccDbAlignmentGeomPointLabeling/AlignmentGeometryPointLabelGroup h=6C1 cmd=U IsUndoing=False
  2026-11-05 12:11:07.955 #82 ObjectModified AeccDbAlignmentStationLabeling/AlignmentStationLabelGroup h=6C0 cmd=U IsUndoing=False
  2026-11-05 12:11:07.955 #83 ObjectModified AeccDbAlignment/Alignment h=6BF cmd=U IsUndoing=True
  2026-11-05 12:11:07.956 #84 ObjectModified AeccDbAlignment/Alignment h=6BF cmd=U IsUndoing=True
  2026-11-05 12:11:07.956 #85 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=U IsUndoing=False
  2026-11-05 12:11:07.956 #86 ObjectModified AeccDbAlignmentDesignSpeedLabeling/AlignmentDesignSpeedLabelGroup h=6C3 cmd=U IsUndoing=False
  2026-11-05 12:11:07.956 #87 ObjectModified AeccDbAlignmentStaEquLabeling/AlignmentStationEquationLabelGroup h=6C2 cmd=U IsUndoing=False
  2026-11-05 12:11:07.956 #88 ObjectModified AeccDbAlignmentGeomPointLabeling/AlignmentGeometryPointLabelGroup h=6C1 cmd=U IsUndoing=False
  2026-11-05 12:11:07.957 #89 ObjectModified AeccDbAlignmentStationLabeling/AlignmentStationLabelGroup h=6C0 cmd=U IsUndoing=False
  2026-11-05 12:11:07.957 #90 ObjectModified AeccDbAlignment/Alignment h=6BF cmd=U IsUndoing=True
  2026-11-05 12:11:07.959 #91 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.960 #92 ObjectModified AcDbSortentsTable/DrawOrderTable h=1C0 cmd=(none) IsUndoing=False
  2026-11-05 12:11:07.963 #93 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.964 #94 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=U IsUndoing=True
  2026-11-05 12:11:07.964 #95 ObjectModified AeccDbAlignmentDesignSpeedLabeling/AlignmentDesignSpeedLabelGroup h=6C3 cmd=U IsUndoing=True
  2026-11-05 12:11:07.965 #96 ObjectModified AeccDbAlignmentStaEquLabeling/AlignmentStationEquationLabelGroup h=6C2 cmd=U IsUndoing=True
  2026-11-05 12:11:07.965 #97 ObjectModified AeccDbAlignmentGeomPointLabeling/AlignmentGeometryPointLabelGroup h=6C1 cmd=U IsUndoing=True
  2026-11-05 12:11:07.966 #98 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=U IsUndoing=False
  2026-11-05 12:11:07.966 #99 ObjectModified AeccDbAlignmentStationLabeling/AlignmentStationLabelGroup h=6C0 cmd=U IsUndoing=True
  2026-11-05 12:11:07.967 #100 ObjectModified AeccDbAlignmentMinorStationLabeling/AlignmentMinorStationLabelGroup h=6C4 cmd=U IsUndoing=False
  2026-11-05 12:11:07.967 #101 ObjectModified AeccDbAlignmentDesignSpeedLabeling/AlignmentDesignSpeedLabelGroup h=6C3 cmd=U IsUndoing=False
  2026-11-05 12:11:07.967 #102 ObjectModified AeccDbAlignmentStaEquLabeling/AlignmentStationEquationLabelGroup h=6C2 cmd=U IsUndoing=False
  2026-11-05 12:11:07.968 #103 ObjectModified AeccDbAlignmentGeomPointLabeling/AlignmentGeometryPointLabelGroup h=6C1 cmd=U IsUndoing=False
  2026-11-05 12:11:07.968 #104 ObjectModified AeccDbAlignmentStationLabeling/AlignmentStationLabelGroup h=6C0 cmd=U IsUndoing=False
  2026-11-05 12:11:07.968 #105 ObjectModified AeccDbAlignment/Alignment h=6BF cmd=U IsUndoing=True
  2026-11-05 12:11:07.971 #106 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.971 #107 ObjectModified AcDbSortentsTable/DrawOrderTable h=1C0 cmd=(none) IsUndoing=False
  2026-11-05 12:11:07.974 #108 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.976 #109 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.976 #110 CommandWillStart 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: armed after 'SVPROBEC_STATE', not fired yet; fired 0 time(s) this session
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: APPEND
appended Line 6EB (0, -500, 0) -> (100, -500, 0)
Command: SVHELLO
SVHELLO ok. SheetVol.Civil3D 0.1.0.0 tfm=.NETCoreApp,Version=v10.0
Runtime: .NET 10.0.14  Process: C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe
AcDbMgd: 25.1.0.0  AeccDbMgd: 13.8.0.1873
Core Console: True
CivilApplication.ActiveDocument: ok
Command: _.U
SVHELLO
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 0) EndingStation=2000 StationEquations.Count=0
FG_A: triangles=52 vertex at (1750, 0): (1750, 0, 101)
appended line 6EB: IsErased=False
Command: SVPROBEEVENTSDUMP
event probe on
14 line(s) recorded since the last dump:
  2026-11-05 12:11:07.977 #111 CommandEnded 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.978 #112 CommandWillStart 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.978 #113 ObjectModified AcDbBlockTableRecord/BlockTableRecord h=1F cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.978 #114 ObjectAppended AcDbLine/Line h=6EB cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.979 #115 ObjectModified AcDbLine/Line h=6EB cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.979 #116 ObjectModified AcDbLine/Line h=6EB cmd=SVPROBEC_EDIT IsUndoing=False
  2026-11-05 12:11:07.979 #117 CommandEnded 'SVPROBEC_EDIT' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.980 #118 CommandWillStart 'SVHELLO' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.981 #119 CommandEnded 'SVHELLO' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.981 #120 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.984 #121 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.985 #122 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.986 #123 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.986 #124 CommandWillStart 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: armed after 'SVPROBEC_STATE', not fired yet; fired 0 time(s) this session
Command: _.U
SVPROBEC_EDIT
Command: SVPROBEC_STATE
UNDOCTL=61
S: PassThroughPoint2=(2000, 0) EndingStation=2000 StationEquations.Count=0
FG_A: triangles=52 vertex at (1750, 0): (1750, 0, 101)
appended line 6EB: IsErased=True
Command: SVPROBEEVENTSDUMP
event probe on
10 line(s) recorded since the last dump:
  2026-11-05 12:11:07.987 #125 CommandEnded 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.988 #126 CommandWillStart 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.988 #127 ObjectModified AcDbBlockTableRecord/BlockTableRecord h=1F cmd=U IsUndoing=True
  2026-11-05 12:11:07.988 #128 ObjectUnappended AcDbLine/Line h=6EB cmd=U IsUndoing=True
  2026-11-05 12:11:07.988 #129 ObjectModified AcDbLine/Line h=6EB cmd=U IsUndoing=True
  2026-11-05 12:11:07.989 #130 ObjectErased AcDbLine/Line h=6EB cmd=U IsUndoing=True Erased=True
  2026-11-05 12:11:07.991 #131 CommandEnded 'U' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.992 #132 CommandWillStart 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.993 #133 CommandEnded 'SVPROBEC_STATE' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.993 #134 CommandWillStart 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: armed after 'SVPROBEC_STATE', not fired yet; fired 0 time(s) this session
Command: SVPROBEEVENTSOFF
===== P15 event probe OFF: unsubscribed everywhere =====
2 line(s) recorded since the last dump:
  2026-11-05 12:11:07.994 #135 CommandEnded 'SVPROBEEVENTSDUMP' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
  2026-11-05 12:11:07.995 #136 CommandWillStart 'SVPROBEEVENTSOFF' doc='C:\LAN-DT-build\sheetvol-wt\s3\tools\fixtures\blank-c3d.dwg'
Idle one-shot: not armed; fired 0 time(s) this session
session totals by event and object class:
  CommandEnded SVHELLO: 1
  CommandEnded SVPROBEC_EDIT: 7
  CommandEnded SVPROBEC_STATE: 8
  CommandEnded SVPROBEEVENTS: 1
  CommandEnded SVPROBEEVENTSDUMP: 7
  CommandEnded U: 8
  CommandWillStart SVHELLO: 1
  CommandWillStart SVPROBEC_EDIT: 7
  CommandWillStart SVPROBEC_STATE: 8
  CommandWillStart SVPROBEEVENTSDUMP: 7
  CommandWillStart SVPROBEEVENTSOFF: 1
  CommandWillStart U: 8
  ObjectAppended AcDbLine: 2
  ObjectErased AcDbLine: 4
  ObjectModified AcDbBlockTableRecord: 4
  ObjectModified AcDbLine: 6
  ObjectModified AcDbSortentsTable: 4
  ObjectModified AeccDbAlignment: 6
  ObjectModified AeccDbAlignmentDesignSpeedLabeling: 7
  ObjectModified AeccDbAlignmentGeomPointLabeling: 7
  ObjectModified AeccDbAlignmentMinorStationLabeling: 11
  ObjectModified AeccDbAlignmentStaEquLabeling: 7
  ObjectModified AeccDbAlignmentStationLabeling: 7
  ObjectModified AeccDbSurfaceTin: 4
  ObjectUnappended AcDbLine: 2
Idle one-shot fired 0 time(s) in the session
log: %TEMP%\sheetvol\probeEvents.log
Command: SVPROBEC_EDIT
Edit [PI/EQ/VTXDEL/VTXADD/APPEND/ERASE]: APPEND
appended Line 6EC (0, -500, 0) -> (100, -500, 0)
Command: SVPROBEEVENTSDUMP
event probe off
0 line(s) recorded since the last dump:
Idle one-shot: not armed; fired 0 time(s) this session
```

**Reading it:**

- After each `_.U`, AutoCAD echoes the command it undid (`SVPROBEC_EDIT`, then
  `SVHELLO`), and `SVPROBEC_STATE` shows the revert: the line unerased, then gone; FG_A
  back to 50 triangles after the re-add is undone; S back to (2000, 0), 2000 and no
  equation after the last three. The probe's own commands (`SVPROBEC_STATE`,
  `SVPROBEEVENTSDUMP`) were skipped by `U` because they carry `NoUndoMarker`. `SVHELLO`
  was not skipped (rule 5).
- The `CommandWillStart`/`CommandEnded` pairs for script commands, including `U`, fire in
  Core Console. `CommandCancelled`, `CommandFailed` and `DocumentActivated` did not occur
  in this script.
- The `AcDbSortentsTable` lines with `cmd=(none)` come right after `CommandEnded` of every
  alignment edit and every alignment undo.
- Control: after `SVPROBEEVENTSOFF`, an append recorded `0 line(s)`, so the unsubscribe is
  complete.
- Not measured headless, because a script cannot produce them: grip edits, Alignment
  Layout Tools edits, frame and match-line grips, data-shortcut synchronize, and `Idle` in
  acad.exe. Dan's run (C.4) covers them.

### C.4 P15 interactive half (coded, waiting for Dan)

`SVPROBEEVENTS` attaches to every open document and to any document activated later. It
detaches a document on `DocumentToBeDestroyed`, and `SVPROBEEVENTSOFF` detaches
everything. Handlers only record: they never open other objects or write to the editor
(the 6.11 rule). `SVPROBEMARK <text>` writes a marker line so Dan's log reads in sections.
The steps (grip PI, `U`, Layout Tools, frame grips, match-line grip, optional surface
edit, save, then synchronize in the referencing drawing) and the pass criteria are part 4
of `docs/dan-probe-steps.md`. The answer L2 needs from that log: the command name of each
grip edit and of a synchronize; which RX classes they modify (alignment, view frame, match
line, view frame group, reference objects); and whether `Idle` fires once after each
command end in acad.exe.

### C.5 P21: modeless WPF on .NET 10 (coded, waiting for Dan)

`SVPROBEUI` (`src/SheetVol.UI/Probe/`) opens a resizable window built in code, so the probe
still opens if compiled XAML fails to load. A separate XAML `UserControl`
(`ProbeXamlPanel.xaml`) tests compiled XAML from a NETLOADed assembly. The window logs
everything to `%TEMP%\sheetvol\probeUI.log` and to a log pane, and Dan ticks what he saw;
each tick is logged. Routes, and the reference for each:

| Question | Route in the probe | Reference |
|---|---|---|
| Show the window | `Application.ShowModelessWindow(window)`, which binds to `Core.Application.ShowModelessWindow(System.Windows.Window)` in AcCoreMgd (rule 8). Logs owner handle vs `Core.Application.MainWindow.Handle`, `System.Windows.Application.Current`, thread, apartment | AcCoreMgd and AcMgd 2025 metadata (MEASURED); AutoCAD .NET Developer's Guide, modeless windows (the guide's topic, not re-opened this session) |
| Database work from a button | R0: no lock (the control; `eLockViolation` expected). R1: `Document.LockDocument()`. R2: `DocumentCollection.ExecuteInCommandContextAsync(Func<object, Task>, object)`, writing first without an explicit lock, then under `LockDocument()` if that throws. R3: `Document.SendStringToExecute(command, activate, wrapUpInactiveDoc, echoCommand)` queuing the registered command `SVPROBEUI_WRITE`. Each writes one Xrecord under `NOD/SHEETVOL_PROBE/P21_R<n>` and logs `LockMode()`, `LockMode(bIncludeMyLocks)`, `CommandInProgress`, `IsApplicationContext`, `Editor.IsQuiescent` and `CMDACTIVE` before, inside and after | Signatures and parameter names from AcCoreMgd 2025 metadata (MEASURED); ExecuteInCommandContextAsync exception caution from the Autodesk forum (rule 9) |
| Colour theme | `COLORTHEME` (0 dark, 1 light) read via `Core.Application.GetSystemVariable`. Three signals subscribed: `Core.Application.SystemVariableChanged`; `Autodesk.Windows.ComponentManager.PropertyChanged` (an `EventHandler<PropertyChangedEventArgs>`) with `CurrentTheme`; `Autodesk.AutoCAD.Internal.ActiveThemeColor.ColorThemeChanged`, observed only (Internal namespace, unsupported, not for the product). Whichever first sees a change restyles by swapping the window's merged `ResourceDictionary`; every brush is a `DynamicResource`, in code and in the XAML panel (E908) | AcCoreMgd, AcMgd and AdWindows 2025 metadata (MEASURED) |
| Command active (E905) | Counter from `Document.CommandWillStart`/`Ended`/`Cancelled`/`Failed` on the followed document; `DocumentCollection.DocumentLockModeChanged` logged alongside; `CMDACTIVE` and `CommandInProgress` logged for comparison. Work buttons are disabled while the counter is above 0. A drift guard on `Application.Idle` resets the counter, and logs it, when no command is in progress | AcCoreMgd 2025 metadata (MEASURED) |
| Highlight (E911) | `Entity.Highlight()`/`Unhighlight()` under `LockDocument()`; a red `Polyline` added with `TransientManager.CurrentTransientManager.AddTransient(..., TransientDrawingMode.DirectTopmost, 128, ...)`. `TransientManager` is in AcDbMgd, not AcMgd (MEASURED). Pick through `ExecuteInCommandContextAsync` with `Editor.GetEntity`, falling back to the command `SVPROBEUI_PICK` on `PromptStatus.Error` or an exception. Cleared on Clear and on close. Ids are forgotten on `DocumentToBeDestroyed` and on close, and a transient that cannot be erased stays referenced, never finalized | AcDbMgd and AcCoreMgd 2025 metadata (MEASURED) |
| DPI and size (E909) | `VisualTreeHelper.GetDpi`, `CompositionTarget.TransformToDevice`, user32 `GetDpiForWindow`, thread and window DPI awareness, `Window.DpiChanged`; size logged on open, on first render, after each resize settles and on demand. `MinWidth`/`MinHeight` enforced; the controls scroll above a fixed log pane, so a small window clips by scaling only, never by layout. A reopen shows whether the one-argument `ShowModelessWindow` restores size and position | WPF (.NET 10) |

Checked without AutoCAD: the solution builds with 0 errors. The BAML resource
`probe/probexamlpanel.baml` is embedded in `SheetVol.UI.dll` (resource
`SheetVol.UI.g.resources`). An independent read-only review of the window code found no
crash or hang on the scripted path, and its eight confirmed findings were fixed before
commit (pick fallback on `PromptStatus.Error`, guarded callbacks, ids forgotten before a
drawing closes, transients never disposed while held, cleanup when `ShowModelessWindow`
throws, the E911 message only when something was cleared, keyboard focus in the steps,
the NETLOAD path wording). Nothing in this section has run in acad.exe. Dan's steps and
pass criteria are part 1 of `docs/dan-probe-steps.md`.

### C.6 P14: view frame geometry (coded, waiting for Dan's fixtures)

**Cannot be run until Dan provides the view-frame drawing pair (HANDOFF.md section 13).**
The API cannot create view frames. On `blank-c3d.dwg` the probe reports
`CivilDocument.GetViewFrameGroupIds(): 0` and inspects the layouts only.

**API surface (MEASURED from AeccDbMgd 2025 metadata, matching
[civapidocs 2026 ViewFrame](https://civapidocs.com/2026/a7752a5d-4a88-8c86-7321-4c3f3fbbe46e.htm)):**

- Groups come from `CivilDocument.GetViewFrameGroupIds()`.
- `ViewFrameGroup` has `GetViewFrameIds()`, `GetMatchLineIds()` and label-style members.
- `ViewFrame` declares `StartStation`, `EndStation`, `AlignmentId`, `GroupId`, `Sheet`,
  `SheetSet`, `IsLabelVisible`, `LabelStyleId` and `LabelAnchorPosition`, and no
  methods. `Name` and the reference flags are inherited.
- `MatchLine` declares `Station`, `Number`, `AlignmentId`, `GroupId` and left and right
  label members.
- All three derive from `Autodesk.Civil.DatabaseServices.GeoEntity`. None has geometry,
  corner or rotation members (5.1 confirmed).

**What `SVPROBEFRAMES` does:**

- Prints every group, frame and match line with stations (raw, and displayed through
  `GetStationStringWithEquations`), alignment, `Sheet` and `SheetSet` (raw, so the format
  is the answer), reference flags and `GetReferenceInfo` on references.
- Explodes each frame and match line recursively (frame, block reference, polyline,
  lines), printing every primitive with its endpoints.
- Chains the first four consecutive segments that close square into the frame rectangle.
  It then solves center, rotation, width and height twice: by the 5.2 hypothesis (first
  edge is the top edge, left to right), with a HOLDS / DOES NOT HOLD verdict, and
  independently (the edge most parallel to the alignment at mid-frame, on its left,
  oriented with increasing station).
- Reports the corners as station and offset, the E712 first and last frame stations
  against the alignment, and each join (frame end, next frame start, match line station).
- Inspects every layout's viewports with their XData and extension dictionaries, in this
  drawing and in a sheet drawing that a frame's `Sheet` names (opened read-only as a side
  database).
- For the layout a frame's `Sheet` names, compares the nearest floating viewport's
  `TwistAngle` with −rotation (6.6), its model center with the solved center, and its
  `CustomScale` with viewport width ÷ frame width (E708).

**Solver self-check (MEASURED, `probeC-solvecheck.scr`).** An in-memory rectangle
polyline with known center, rotation and size, exploded and solved exactly as a frame
would be. It proves the probe's arithmetic, not P14:

```text
case 'S, top edge first, left to right': expected center (1000, 0) rotation 0 deg width 800 height 300; hypothesis should HOLD
    hypothesis HOLDS: runs with increasing station=True, lies left of the alignment (top of the sheet)=True
    independent solution (mid-frame station 1000, tangent 0 deg): top edge = rectangle edge 0; center (1000, 0, 0) rotation 0 deg width 800 height 300
case 'S, top edge first, left to right': hypothesis verdict HOLDS, independent solution solved: PASS
case 'S, bottom edge first, right to left': expected center (1000, 0) rotation 0 deg width 800 height 300; hypothesis should NOT HOLD
    hypothesis DOES NOT HOLD: runs with increasing station=False, lies left of the alignment (top of the sheet)=False
    independent solution (mid-frame station 1000, tangent 0 deg): top edge = rectangle edge 2; center (1000, 0, 0) rotation 0 deg width 800 height 300
case 'S, bottom edge first, right to left': hypothesis verdict DOES NOT HOLD, independent solution solved: PASS
case 'K second leg, top edge first, left to right': expected center (629.903811, -75) rotation -30 deg width 800 height 300; hypothesis should HOLD
    hypothesis HOLDS: runs with increasing station=True, lies left of the alignment (top of the sheet)=True
    independent solution (mid-frame station 650, tangent -30 deg): top edge = rectangle edge 0; center (629.903811, -75, 0) rotation -30 deg width 800 height 300
case 'K second leg, top edge first, left to right': hypothesis verdict HOLDS, independent solution solved: PASS
SOLVECHECK: all cases PASS
```

**Viewport control (MEASURED, `probeC-frames-control.scr` on FX's code-built template
layout).** This is what a plain AutoCAD viewport carries, the baseline against which a
Create Sheets "Plan" viewport's marker will stand out:

```text
[this drawing] layout 'SV-SHEET' tab 3 h=6C6 plot style ''
  paper space contents: AcDbBlockReference 1, AcDbViewport 2
  viewport [0] (first in the block: AutoCAD's overall viewport slot) h=6CC Number=-1 layer=0 size 15.876947 x 8.993 at (5.303149, 3.580709, 0) CustomScale=1 StandardScale=Scale1To1 TwistAngle=0 deg ViewTarget=(0, 0, 0) ViewCenter=(5.303149, 3.580709) model center (5.303149, 3.580709, 0) NonRectClipOn=False clip=0 Locked=False On=True
    viewport XData: (1001 ACAD) (1000 MVIEW) (1002 {) (1070 16) (1010 (0,0,0)) (1010 (0,0,1)) (1040 0) (1040 8.99299974576695) (1040 5.303149486151268) (1040 3.580708541269378) (1040 50) (1040 0) (1040 0) (1070 0) (1070 1000) (1070 1) (1070 1) (1070 0) (1070 0) (1070 0) (1070 0) (1040 0) (1040 0) (1040 0) (1040 0.5) (1040 0.5) (1040 0.5) (1040 0.5) (1070 0) (1002 {) (1002 }) (1002 })
    viewport extension dictionary 6CD: 1 entry
    viewport   'ASDK_XREC_ANNOTATION_SCALE_INFO': Xrecord (90 1) (340 (1977738329968))
  viewport [1] h=6D4 Number=-1 layer=SV-VPORT size 12 x 5 at (7, 4.5, 0) CustomScale=0.02 StandardScale=Scale1To50 TwistAngle=0 deg ViewTarget=(0, 0, 0) ViewCenter=(0, 0) model center (0, 0, 0) NonRectClipOn=False clip=0 Locked=False On=True
    viewport XData: (1001 ACAD) (1000 MVIEW) (1002 {) (1070 16) (1010 (0,0,0)) (1010 (0,0,1)) (1040 0) (1040 250) (1040 0) (1040 0) (1040 50) (1040 0) (1040 0) (1070 0) (1070 1000) (1070 1) (1070 1) (1070 0) (1070 0) (1070 0) (1070 0) (1040 0) (1040 0) (1040 0) (1040 0.5) (1040 0.5) (1040 0.5) (1040 0.5) (1070 0) (1002 {) (1002 }) (1002 })
    viewport extension dictionary: none
registered applications (14): ACAD, AcadAnnoPO, AcadAnnotative, ACAD_DSTYLE_DIMJAG, ACAD_DSTYLE_DIMTALN, ACAD_MLEADERVER, ACAD_NAV_VCDISPLAY, ACAD_PSEXT, AcMapDMDisplayStyleRegapp, ADE, ADE_PROJECTION, AeccLand130, DCO15, MapGWS
```

A plain viewport carries only AutoCAD's `ACAD`/`MVIEW` XData, plus an annotation-scale
Xrecord on the viewport that was activated. Whatever Create Sheets adds (an `Aecc*`
registered application, XData, or an extension-dictionary entry) is the "Plan" marker.

**What L1 gets once the fixtures exist (section 7.7):**

- whether `Explode` reads native and reference frames (E707, stop condition 12.3);
- the frame rectangle and the hypothesis verdict (E701);
- whether frame stations equal match-line stations, and how the first and last frames end
  (E702, E712);
- the frame-to-viewport twist and scale relation (E708);
- the `Sheet` string format and how a Plan viewport is marked (E709, E710, `SVADOPT`);
- whether reference frames behave the same (E705).

### C.7 Open items and what remains

| Item | Status | Needs |
|---|---|---|
| P21 in acad.exe | OPEN (coded) | Dan: `docs/dan-probe-steps.md` parts 0 and 1, about 10 min; send `probeUI.log` |
| P14 | OPEN (coded; solver self-checked) | Dan: the three fixture drawings, part 2. Then anyone runs part 3 headless |
| P15 interactive half | OPEN (headless half MEASURED) | Dan: part 4 on the fixtures; send `probeEvents.log` |
| `SheetVol.UI.dll` not staged by `tools/test-headless.ps1` | WORKAROUND in part 0 | Orchestrator: stage the UI project's output too (the runner is not S3's) |
| AeccUiMgd events (`AeccReferenceStatusUtils.ReferenceStatusChanged` and others) | NOT EXPLORED | Only if L2 finds Database events insufficient for synchronize; UI-only assembly, undocumented |
| `Idle` in acad.exe | OPEN | Dan's P15 run |

## D. Probe D: user volume surface as analysis source (S4)

**Question** (Dan 2026-11-06, NOW.md X6): can a TIN volume surface the user already made be
an analysis source in place of a Base + Comparison pair, since it already carries both and
its own cut/fill factors? MEASURED 2026-11-06 in the Civil 3D 2026 Core Console (loaded
`AeccDbMgd 13.8.0.1873`); the plugin compiles against `AutoCAD 2025\C3D\AeccDbMgd.dll`
(`13.7.0.1519`). "The user's surface" is `FX-VOL-EG-FG_A` from `SVFIXTURE VOLUMES`; the
owned surface is `SV-EG-FG_A` from A1's `AnalysisResolver.EnsureAnalyses`.

### D.0 How to rerun

```powershell
dotnet build .\SheetVol.slnx -c Debug
.\tools\test-headless.ps1 -Script tools\scripts\probeD.scr
```

Log: `%TEMP%\sheetvol\probeD.log` (exit 0, 6 s). Code: `src/SheetVol.Civil3D/SelfTest/ProbeDCommon.cs`,
`ProbeDSource.cs`. The script builds `SVFIXTURE A`, `S`, `VOLUMES`, then runs `SVPROBED`
(items 1 to 3), `SVPROBED_OUTER` (6), `SVPROBED_EDIT`, `_STALE`, `_REBUILD`, `_.UNDO 1`,
`_UNDONE`, `_OWNED` (4), `SVPROBED_ERASE`, `_AFTERERASE` (5). T2 has four segments (breaks
0, 500, 1300, 1800, 2000), not five.

### D.1 Summary

| Item | Measured | Usable as an analysis source |
|---|---|---|
| 1 Members | No `BaseSurfaceId` / `ComparisonSurfaceId` member exists in 13.7 or 13.8: those strings are only parameter names of `TinVolumeSurface.Create` and `GridVolumeSurface.Create` (metadata scan). The pair is `TinVolumeSurface.GetVolumeProperties().BaseSurface` / `.ComparisonSurface` (`ObjectId`, get only). Factors: `TinVolumeSurface.CutFactor` / `FillFactor` (get/set) and the same names on `VolumeSurfaceProperties` (get). Read back: base `EG`, comparison `FG_A`, factors 1/1, cut 5400, fill 5400 | yes |
| 2 Bounded, user vs owned | Identical on all four T2 segments, diff 0.00E+00 for cut and fill | yes |
| 3 Factors 1.25 / 0.9 | `GetBoundedVolumes` returns raw (seg 1: 2700 / 1350, not 3375 / 1215); `AdjustedCut` 6750 and `AdjustedFill` 4860 take the factors at once, no Rebuild, `IsOutOfDate` stays False | yes, with condition: read the factors from the surface and apply them in Core, as the owned path does |
| 4 Staleness | FG_A apex lowered 1.0: user and owned surfaces both read `IsOutOfDate=False` in the same command, `True` in the next, and return the stale 2700 until Rebuild; after Rebuild 5400. One `UNDO` after the Rebuild command removed the marker and put the user surface back to stale (`IsOutOfDate=True`, 2700) | yes, with condition: needs Rebuild like the owned one, and that Rebuild is an undoable change to the user's drawing |
| 5 Base erased | `EG.Erase()` succeeds with three volume surfaces on it. Same command: still `TinVolumeSurface`, `BaseSurface` reads `IsErased=True`, `CutFactor` getter throws `CivilException('Retrieve attribute failed.')`, `GetBoundedVolumes` returns the old 2700 / 1350. Next command: the object is a plain `TinSurface` (`IsVolumeSurface=False`), stale numbers until Rebuild, then no triangles and 0 / 0 / 0. `SurfaceStatus.BaseSurfaceNotFound` (34) exists, but no public property or method returns `SurfaceStatus`; no call throws it | yes, with condition: check the type (`TinVolumeSurface`, `IsVolumeSurface`) and `BaseSurface` / `ComparisonSurface` not erased before every read; nothing else reports the loss |
| 6 User outer boundary | `BoundariesDefinition.AddBoundaries(Outer)` works on a volume surface (inherited from `Surface`); after Rebuild 68 to 44 triangles, whole surface 2700 / 1350, and `GetBoundedVolumes` clips to it as well as to the polygon: seg 2 cut 2700 to 0, seg 3 fill 4050 to 0 | yes, with condition: the user's boundary is part of the answer, so a volume-surface source can differ from the same Base + Comparison as a pair |

### D.2 Output (verbatim, `probeD.log`)

```text
D1 loaded AeccDbMgd 13.8.0.1873 from C:\Program Files\Autodesk\AutoCAD 2026\C3D\AeccDbMgd.dll
D1 public properties named BaseSurfaceId: none
D1 public properties named ComparisonSurfaceId: none
D1 public properties named BaseSurface: Autodesk.Civil.DatabaseServices.VolumeSurfaceProperties.BaseSurface:ObjectId get
D1 public properties named ComparisonSurface: Autodesk.Civil.DatabaseServices.VolumeSurfaceProperties.ComparisonSurface:ObjectId get
D1 TinVolumeSurface declared public properties: FillFactor:Double get/set, CutFactor:Double get/set
D1 FX-VOL-EG-FG_A handle 6EA: props.BaseSurface='EG' (1860997791808) (TinSurface) props.ComparisonSurface='FG_A' (1860997792000) (TinSurface)
D1 FX-VOL-EG-FG_A: props.CutFactor=1 props.FillFactor=1 vol.CutFactor=1 vol.FillFactor=1
D1 FX-VOL-EG-FG_A: UnadjustedCut=5400.000000000002 UnadjustedFill=5400 UnadjustedNet=-1.8189894035458565E-12 AdjustedCut=5400.000000000002 AdjustedFill=5400 AdjustedNet=-1.8189894035458565E-12
D2 | seg | user Cut     | owned Cut    | diff Cut  | user Fill    | owned Fill   | diff Fill | expected C/F ft3
D2 | 1   |         2700 |         2700 |  0.00E+00 |         1350 |         1350 |  0.00E+00 | 2700/1350
D2 | 2   | 2700.0000000000023 | 2700.0000000000023 |  0.00E+00 |            0 |            0 |  0.00E+00 | 2700/0
D2 | 3   |            0 |            0 |  0.00E+00 |         4050 |         4050 |  0.00E+00 | 0/4050
D2 | 4   |            0 |            0 |  0.00E+00 |            0 |            0 |  0.00E+00 | 0/0
[D3 same transaction as the set] FX-VOL-EG-FG_A: IsOutOfDate=False CutFactor=1.25 FillFactor=0.9 z(1125,0)=-1 UnadjustedCut=5400.000000000002 UnadjustedFill=5400 AdjustedCut=6750.000000000002 AdjustedFill=4860 props.CutFactor=1.25 props.FillFactor=0.9
FX-VOL-EG-FG_A.GetBoundedVolumes(whole band [0,2000] user, factors 1.25/0.9): Cut=5400.000000000002 Fill=5400 Net=-1.8189894035458565E-12
D3 seg 1: bounded Cut=2700 Fill=1350; raw x factor would be Cut=3375 Fill=1215; raw is Cut=2700 Fill=1350
D3 seg 3: bounded Cut=0 Fill=4050; raw x factor would be Cut=0 Fill=3645; raw is Cut=0 Fill=4050
D4 FG_A vertex at (1125, 0): (1125, 0, 99); setting z to 98
FX-VOL-EG-FG_A.GetBoundedVolumes(T2 seg 2 [500,1300] same command as the edit, no Rebuild): Cut=2700.0000000000023 Fill=0 Net=-2700.0000000000023
[D4 next command, no Rebuild] FX-VOL-EG-FG_A: IsOutOfDate=True CutFactor=1 FillFactor=1 z(1125,0)=-1 UnadjustedCut=5400.000000000002 UnadjustedFill=5400 AdjustedCut=5400.000000000002 AdjustedFill=5400 props.CutFactor=1 props.FillFactor=1
FX-VOL-EG-FG_A.GetBoundedVolumes(T2 seg 2 [500,1300] next command, no Rebuild): Cut=2700.0000000000023 Fill=0 Net=-2700.0000000000023
SV-EG-FG_A.GetBoundedVolumes(T2 seg 2 [500,1300] next command, no Rebuild): Cut=2700.0000000000023 Fill=0 Net=-2700.0000000000023
D4 marker SV-PROBED-UNDO-MARKER written, handle 6EF
FX-VOL-EG-FG_A.GetBoundedVolumes(T2 seg 2 [500,1300] after user-surface Rebuild): Cut=5400.000000000005 Fill=0 Net=-5400.000000000005
D4 after UNDO 1: marker SV-PROBED-UNDO-MARKER present=False
D4 after UNDO 1: FG_A z(1125,0)=98 (control: the edit was an earlier command)
[D4 after UNDO 1] FX-VOL-EG-FG_A: IsOutOfDate=True CutFactor=1 FillFactor=1 z(1125,0)=-1 UnadjustedCut=5400.000000000002 UnadjustedFill=5400 AdjustedCut=5400.000000000002 AdjustedFill=5400 props.CutFactor=1 props.FillFactor=1
FX-VOL-EG-FG_A.GetBoundedVolumes(T2 seg 2 [500,1300] after UNDO 1): Cut=2700.0000000000023 Fill=0 Net=-2700.0000000000023
SV-EG-FG_A.GetBoundedVolumes(T2 seg 2 [500,1300] owned after EnsureAnalyses): Cut=5400.000000000005 Fill=0 Net=-5400.000000000005
D5 before the erase: FX-VOL-EG-FG_A id (1860997792160) ObjectClass=AeccDbSurfaceTin wrapper=Autodesk.Civil.DatabaseServices.TinVolumeSurface
D5 EG IsErased=True
D5 same command as the erase: FX-VOL-EG-FG_A IsErased=False IsOutOfDate=True IsVolumeSurface=True CutFactor=threw CivilException('Retrieve attribute failed.') triangles=68
D5 same command as the erase: GetVolumeProperties: BaseSurface=(1860997791808) (IsErased=True) ComparisonSurface='FG_A' (1860997792000) (TinSurface) UnadjustedCut=5400.000000000002 UnadjustedFill=5400
D5 same command as the erase: public properties of type SurfaceStatus: none
D5 same command as the erase: public methods returning SurfaceStatus: none
FX-VOL-EG-FG_A.GetBoundedVolumes(T2 seg 1 [0,500] D5 same command as the erase): Cut=2700 Fill=1350 Net=-1350
D5 next command: id (1860997792160) IsErased=False IsValid=True ObjectClass=AeccDbSurfaceTin; GetObject returned Autodesk.Civil.DatabaseServices.TinSurface
D5 next command: FX-VOL-EG-FG_A IsErased=False IsOutOfDate=True IsVolumeSurface=False CutFactor=null triangles=68
FX-VOL-EG-FG_A.GetBoundedVolumes(T2 seg 1 [0,500] D5 next command): Cut=2700 Fill=1350 Net=-1350
D5 next command after Rebuild: IsOutOfDate=False triangles=threw InvalidOperationException('The surface has no point or triangle.')
FX-VOL-EG-FG_A.GetBoundedVolumes(T2 seg 1 [0,500] D5 next command after Rebuild): Cut=0 Fill=0 Net=0
[D5 A1 path with EG erased] EnsureAnalyses problem SV-E442 Error: Analysis 'EG vs FG_A': base surface 'EG' is not in the drawing.
D6 AddBoundaries(Outer, nonDestructive=True) returned SurfaceOperationAddBoundary BoundaryType=Outer Count=1
D6 IsOutOfDate after AddBoundaries (same transaction) = False
D6 after Rebuild: triangles=44 BoundariesDefinition.Count=1
FX-VOL-EG-FG_A-OUTER.GetBoundedVolumes(T2 seg 1 [0,500] outer boundary added): Cut=2700 Fill=1350 Net=-1350
FX-VOL-EG-FG_A-OUTER.GetBoundedVolumes(T2 seg 2 [500,1300] outer boundary added): Cut=0 Fill=0 Net=0
FX-VOL-EG-FG_A-OUTER.GetBoundedVolumes(T2 seg 3 [1300,1800] outer boundary added): Cut=0 Fill=0 Net=0
```

Metadata scan of `AutoCAD 2025\C3D\AeccDbMgd.dll` (13.7.0.1519, System.Reflection.Metadata,
no load): `BaseSurfaceId` and `ComparisonSurfaceId` occur only as parameters of
`TinVolumeSurface.Create`, `GridVolumeSurface.Create`, `Surface.InternalAddCreationVolumeData`
and the module-level `CheckCreationVolumeArg`; `BaseSurface` / `ComparisonSurface` are
properties of `VolumeSurfaceProperties` only; `BoundariesDefinition` is on `Surface`;
`BaseSurfaceNotFound` is a field of the enum `Autodesk.Civil.SurfaceStatus`. 13.8 is the same.

### D.3 What this means for X6 (orchestrator decides; no contract changed here)

1. **Correction to NOW.md X6:** the members are `VolumeSurfaceProperties.BaseSurface` and
   `.ComparisonSurface` (`ObjectId`, read through `TinVolumeSurface.GetVolumeProperties()`),
   not `BaseSurfaceId` / `ComparisonSurfaceId`. The pair cannot be changed through them.
2. A user volume surface gives the owned path's bounded numbers exactly when nothing else
   differs (item 2). `GetBoundedVolumes` ignores its factors (item 3), so they are read from
   the surface and applied in Core, as the owned path already does with the analysis factors.
3. It needs `Rebuild()` after a source changes, exactly like the owned surface, and
   `IsOutOfDate` flags only after the editing command ends (item 4; A1 saw a 3D-polyline
   breakline TIN not flag at all). A regen that rebuilds it writes the user's object and
   that write is one `UNDO` step. Whether a regen may do that, or must fail and name the
   surface when it is out of date (as E404 does for sources with `RebuildSources` off), is
   the decision item 4 leaves open.
4. A lost base or comparison is silent (item 5): the surface turns into a plain TIN
   surface after the command, and its numbers are stale, then zero after a Rebuild. The
   check before every read is "still a `TinVolumeSurface`, `BaseSurface` and
   `ComparisonSurface` not erased"; A1's own path reports SV-E442 for the same case.
5. A boundary on the user's volume surface clips the result (item 6). The same
   Base + Comparison picked as a pair would give different numbers, so the source kind has
   to show in the report.
