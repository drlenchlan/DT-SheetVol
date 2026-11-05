# Ownership

Owner: Wave 2 integration (HANDOFF.md section 10, A4 with A2 and A3). The one owner-tag format
both writers use.

- `OwnerTag.cs`: `OwnerTag` (read, compare, write) and `RunIds.For(runName)`, the stable run id
  every tag carries. A2's model writers, A3's paper writers and `PaperWriter.FindOwned` all go
  through it; the extension-dictionary key comes from `modelWriters.ownerTagKey` and
  `paper.ownerTagKey`, which the settings file requires to be equal (one format, one key).
- A1's `Adapters/OwnershipTag.cs` is a different record on a different object kind (the owned
  volume surface: tagged name and source handles) and stays A1's.
