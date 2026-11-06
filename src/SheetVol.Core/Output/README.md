# Output

Owner: C2 (HANDOFF.md section 10). Table models, CSV/JSON export, Xrecord chunker (6.8, 6.9).

## Export schema (`ExportBuilder.SchemaVersion`, CSV `schema_version`, JSON `schemaVersion`)

| Version | Change |
|---|---|
| 1 | First export (6.8). |
| 2 | Contract rev 3 (X5): `cut_above_ohwm_raw` / `fill_below_ohwm_raw` (JSON `cutAboveOhwm*` / `fillBelowOhwm*`) removed. |
| 3 | Contract rev 3 rule 7 (E423): per analysis `source` = `pair` or `volume_surface` and `volume_surface` = the surface name, empty for a pair (JSON `source`, `volumeSurface`); CSV columns sit between `analysis` and `base_surface` on every row kind. Source of the fact: `AnalysisDef.VolumeSurface` (null = pair). |

## Rev 3

- Run schema 3 (`RunPersistence.PluginSchemaVersion`; schema 2 `ohwm` skipped, `VolumeSurface` null) and export schema 2 (`cut_above_ohwm_raw` / `fill_below_ohwm_raw` and the `CutAboveOhwm*` / `FillBelowOhwm*` members removed).
- Export schema 3 (rev3-output): `source` and `volume_surface` per analysis (table above). Factors for a volume-surface source: from the surface after rev3-adapters merges; until then the export carries the factors of the analysis as reconciled, which are the run's stored ones.
