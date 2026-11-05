// A2 headless check, phases C and D: the master table on a named layout (6.7 anchor, E601) and
// the persistence store (6.9): read-back, E803, E105, chunking at scale, E612 orphans, E809
// save-as and reopen with ReadDwgFile, E810 objects copied to another drawing.

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.SelfTest;
using SheetVol.Core;
using SheetVol.Core.Output;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersModel.Check;

public static partial class WritersModelCheck
{
    private static void LayoutPhase()
    {
        Say("----- phase C: master table on a named layout at its anchor block (6.7), E601 -----");
        var tmpl = Fixtures.CreateTemplate(_db);
        Say($"layout fixture: {tmpl.LayoutName}, {Report.Placement.TableAnchorBlock} reference {tmpl.AnchorReferenceId.Handle}");
        var o0 = RegenStd();
        string masterOld = o0.Handles[MasterKey];

        Std.MasterPlacement = TablePlacement.InLayout(tmpl.LayoutName);
        var o1 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var m = Open<Table>(tr, o1.Handles[MasterKey]);
            bool inModel = m.OwnerId == SymbolUtilityServices.GetBlockModelSpaceId(_db);
            Check("E502", o1.Result.Writes.Count() == 0 && inModel && o1.Handles[MasterKey] == masterOld,
                $"placement switched to layout {tmpl.LayoutName}: the existing master {masterOld} stays where it is (model space={inModel}); {o1.Kinds}");
            tr.Commit();
        }

        UserEdit($"master table {masterOld} erased", tr => tr.GetObject(WritersModel.Handles.Resolve(_db, masterOld), OpenMode.ForWrite).Erase());
        Std.MasterPlacement = TablePlacement.InLayout(tmpl.LayoutName + CheckSettings.MissingSuffix);
        var bad = RegenStd();
        Check("E601", !bad.Result.Succeeded && bad.Has(ModelWriterCodes.LayoutOrAnchorMissing) && bad.Events.Appended.Count + bad.Events.Modified.Count + bad.Events.Erased.Count == 0,
            $"master must be created on a missing layout: succeeded={bad.Result.Succeeded}, {bad.Events.Summary}; {Problems(bad, ModelWriterCodes.LayoutOrAnchorMissing)}");

        Std.MasterPlacement = TablePlacement.InLayout(tmpl.LayoutName);
        var o2 = RegenStd();
        var o3 = RegenStd();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var m = Open<Table>(tr, o2.Handles[MasterKey]);
            var layoutBlock = DrawingNames.LayoutBlock(tr, _db, tmpl.LayoutName)!.Value;
            var anchor = DrawingNames.AnchorPoint(tr, layoutBlock, Report.Placement.TableAnchorBlock)!.Value;
            Check("anchor", m.OwnerId == layoutBlock && m.Position.X == anchor.X && m.Position.Y == anchor.Y && m.Cells[1, 0].TextHeight == Report.MasterTable.Style.TextHeight
                            && o2.Has(ModelWriterCodes.TableRecreated) && o3.Result.Writes.Count() == 0,
                $"master recreated as {m.Handle} in {tmpl.LayoutName} paper space, top-left ({F(m.Position.X)}, {F(m.Position.Y)}) = anchor ({F(anchor.X)}, {F(anchor.Y)}), text height {m.Cells[1, 0].TextHeight} (paper, unscaled); next regen {o3.Kinds}");
            tr.Commit();
        }
    }

    private static void StorePhase()
    {
        Say("----- phase D: persistence store (6.9) -----");
        var (_, stdRun, _) = StandardPlan(Std);

        // ---- Read-back in session.
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var read = RunStore.Read(tr, _db, Std.Name, Report);
            var rec = read.Record!;
            int resolved = rec.Objects.Count(o => Resolves(tr, _db, o, Std.Name));
            var xr = StoreXrecord(tr, _db, Std.Name);
            var strings = (xr.Data?.AsArray() ?? []).Where(v => v.Value is string).Select(v => (string)v.Value).ToList();
            int runChunks = (xr.Data?.AsArray() ?? []).Count(v => v.TypeCode == 300), stateChunks = (xr.Data?.AsArray() ?? []).Count(v => v.TypeCode == 301);
            Check("store", read.CanModify && read.StoredSchemaVersion == RunStore.SchemaVersion && read.TimestampUtc is not null
                           && RunPersistence.Serialize(rec.Run) == RunPersistence.Serialize(stdRun) && resolved == rec.Objects.Count && strings.Max(s => s.Length) <= RunPersistence.MaxChunkLength,
                $"NOD/{Report.Persistence.DictionaryName}/{RunStore.KeyFor(Std.Name)}: schema {read.StoredSchemaVersion}, timestamp {read.TimestampUtc:o}, run JSON {runChunks} chunk(s), state JSON {stateChunks} chunk(s), longest string {strings.Max(s => s.Length)} chars; run definition round-trips; {resolved}/{rec.Objects.Count} handles resolve to tagged objects; sources {rec.Sources.Count}");
            tr.Commit();
        }

        // ---- E803: a source whose handle died is found again by name.
        string dead;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(_db), OpenMode.ForWrite);
            var tmp = new Line();
            tmp.SetDatabaseDefaults(_db);
            ms.AppendEntity(tmp);
            tr.AddNewlyCreatedDBObject(tmp, true);
            dead = tmp.Handle.ToString();
            tr.Commit();
        }
        UserEdit($"object {dead} erased (its handle is now dead)", tr => tr.GetObject(WritersModel.Handles.Resolve(_db, dead), OpenMode.ForWrite).Erase());
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var s = RunStore.ResolveSource(tr, _db, new SourceRef(SourceKinds.Alignment, FixtureSpec.Names.AlignmentS, dead));
            var f = RunStore.ResolveSource(tr, _db, new SourceRef(SourceKinds.Surface, FixtureSpec.Names.FgA, dead));
            var n = RunStore.ResolveSource(tr, _db, new SourceRef(SourceKinds.Surface, FixtureSpec.Names.FgA + CheckSettings.MissingSuffix, dead));
            var h = RunStore.ResolveSource(tr, _db, StdSources[0]);
            Check("E803", s.Problem?.Code == ModelWriterCodes.SourceFoundByName && s.Id == _alignmentId && s.Ref.Handle == WritersModel.Handles.Of(_alignmentId)
                          && f.Id == _fgaId && f.Problem?.Severity == Severity.Warning,
                $"alignment '{FixtureSpec.Names.AlignmentS}' stored as {dead}: {s.Problem?.Code} {s.Problem?.Severity}: {s.Problem?.Message} | surface '{FixtureSpec.Names.FgA}': repaired to {f.Ref.Handle}");
            Check("E803", n.Id.IsNull && n.Problem?.Code == ModelWriterCodes.SourceMissing && n.Problem.Severity == Severity.Error && h.Problem is null,
                $"unknown name: {n.Problem?.Code} {n.Problem?.Severity}: {n.Problem?.Message} | live handle {StdSources[0].Handle}: no problem");
            // Repair the stored record the way A4 will: resolve every source, write back the repaired references.
            var read = RunStore.Read(tr, _db, Std.Name, Report);
            var broken = read.Record! with { Sources = [read.Record!.Sources[0] with { Handle = dead }, .. read.Record!.Sources.Skip(1)] };
            RunStore.Write(tr, _db, broken, DateTime.UtcNow, Report);
            var repaired = RunStore.Read(tr, _db, Std.Name, Report).Record!.Sources.Select(src => RunStore.ResolveSource(tr, _db, src)).ToList();
            RunStore.Write(tr, _db, read.Record with { Sources = repaired.Select(r => r.Ref).ToList() }, DateTime.UtcNow, Report);
            var final = RunStore.Read(tr, _db, Std.Name, Report).Record!.Sources;
            Check("E803", repaired.Count(r => r.Problem?.Code == ModelWriterCodes.SourceFoundByName) == 1 && final[0].Handle == WritersModel.Handles.Of(_alignmentId),
                $"stored record with a dead alignment handle: {repaired.Count(r => r.Problem is not null)} warning(s); after repair the store holds {string.Join(", ", final.Select(x => $"{x.Kind} {x.Name}={x.Handle}"))}");
            tr.Commit();
        }

        // ---- E105: a record written by a newer plugin is refused, read and write.
        const string future = "WM-FUTURE";
        string beforeText;
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var nod = (DBDictionary)tr.GetObject(_db.NamedObjectsDictionaryId, OpenMode.ForRead);
            var dict = (DBDictionary)tr.GetObject(nod.GetAt(Report.Persistence.DictionaryName), OpenMode.ForWrite);
            var xr = new Xrecord
            {
                Data = new ResultBuffer(new TypedValue(1, "SHEETVOL-RUN"), new TypedValue(90, RunStore.SchemaVersion + 1),
                    new TypedValue(302, DateTime.UtcNow.ToString("o")), new TypedValue(300, "{\"schemaVersion\":" + (RunStore.SchemaVersion + 1) + ",\"name\":\"" + future + "\"}"), new TypedValue(301, "{}")),
            };
            dict.SetAt(RunStore.KeyFor(future), xr);
            tr.AddNewlyCreatedDBObject(xr, true);
            beforeText = string.Join("|", xr.Data.AsArray().Select(v => $"{v.TypeCode}={v.Value}"));
            tr.Commit();
        }
        Say($"user edit: a record for run '{future}' with schema version {RunStore.SchemaVersion + 1} placed in the store (as a newer plugin would write it)");
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var read = RunStore.Read(tr, _db, future, Report);
            var write = RunStore.Write(tr, _db, new RunRecord(stdRun with { Name = future }, [], [], [], null), DateTime.UtcNow, Report);
            var del = new List<Problem>();
            bool deleted = RunStore.Delete(tr, _db, future, Report, del);
            string afterText = string.Join("|", (StoreXrecord(tr, _db, future).Data?.AsArray() ?? []).Select(v => $"{v.TypeCode}={v.Value}"));
            Check("E105", !read.CanModify && read.StoredSchemaVersion == RunStore.SchemaVersion + 1 && read.Problems.Any(p => p.Code == OutputCodes.StoredSchemaNewer)
                          && write.Kind == StoreWriteKind.Refused && !deleted && afterText == beforeText,
                $"read: canModify={read.CanModify}, stored schema {read.StoredSchemaVersion}: {read.Problems.FirstOrDefault()?.Message} | write: {write.Kind} | delete refused={!deleted} | record unchanged={afterText == beforeText}");
            tr.Commit();
        }
        {
            var (plan, run, _) = StandardPlan(Std);
            var o = Regen(run with { Name = future }, plan);
            Check("E105", !o.Result.Succeeded && o.Has(OutputCodes.StoredSchemaNewer) && o.Events.Appended.Count + o.Events.Modified.Count + o.Events.Erased.Count == 0,
                $"model writers refuse the run: succeeded={o.Result.Succeeded}, {o.Events.Summary}");
        }

        // ---- Chunking at scale and E612 orphan listing, in a transaction that is aborted.
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var segs = Enumerable.Range(0, 1000).Select(i => new SegmentDef(G(9, i), (i + 1).ToString(), i * 2.0, i * 2.0 + 2, -100, 100)).ToList();
            var objs = segs.SelectMany(s => new[] { OwnedRole.Boundary, OwnedRole.Layout, OwnedRole.Viewport, OwnedRole.PaperTable }
                .Select((r, k) => new StoredObjectRef(s.Id, r, "", (0x10000 + k).ToString("X")))).ToList();
            var big = new RunRecord(Def("WM-STRESS", segs, Std.Analyses), objs, StdSources, [], new string('f', 64));
            var w = RunStore.Write(tr, _db, big, DateTime.UtcNow, Report);
            var r = RunStore.Read(tr, _db, "WM-STRESS", Report);
            bool same = r.Record is not null && RunPersistence.Serialize(r.Record.Run) == RunPersistence.Serialize(big.Run) && r.Record.Objects.SequenceEqual(big.Objects);
            Check("store", w.Kind == StoreWriteKind.Created && same && w.LongestString <= RunPersistence.MaxChunkLength,
                $"1,000 segments and {objs.Count} handles: {w.Chunks} strings, longest {w.LongestString} chars, read back identical={same}");

            var layout = DrawingNames.LayoutBlock(tr, _db, FixtureSpec.Names.TemplateLayout);
            var layoutId = ((BlockTableRecord)tr.GetObject(layout!.Value, OpenMode.ForRead)).LayoutId;
            var orphan = new OrphanRef(G(1, 9), OwnedRole.Layout, "", WritersModel.Handles.Of(layoutId), FixtureSpec.Names.TemplateLayout);
            var gone = new OrphanRef(G(1, 8), OwnedRole.Layout, "", dead, "ERASED-LAYOUT");
            var read = RunStore.Read(tr, _db, Std.Name, Report).Record!;
            var merged = RunStore.MergeOrphans(_db, read.Orphans, [orphan, gone], []);
            RunStore.Write(tr, _db, read with { Orphans = merged }, DateTime.UtcNow, Report);
            var listed = RunStore.Read(tr, _db, Std.Name, Report).Record!.Orphans;
            var replanned = RunStore.MergeOrphans(_db, listed, [], [orphan.Key]);
            Check("E612", listed.Count == 1 && listed[0].Name == FixtureSpec.Names.TemplateLayout && replanned.Count == 0,
                $"orphan list after merge: {string.Join(", ", listed.Select(o => $"{o.Role} '{o.Name}' {o.Handle}"))} (the dead-handle orphan dropped); dropped again when its key is planned anew: {replanned.Count == 0}");
            tr.Abort();
        }

        // ---- E809: save under a new name, reopen with ReadDwgFile, read the store back.
        Directory.CreateDirectory(CheckSettings.OutDir);
        if (File.Exists(CheckSettings.SavePath)) File.Delete(CheckSettings.SavePath);
        var live = new Dictionary<string, RunRecord>();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            foreach (string name in new[] { Std.Name, Hc.Name }) live[name] = RunStore.Read(tr, _db, name, Report).Record!;
            tr.Commit();
        }
        _db.SaveAs(CheckSettings.SavePath, DwgVersion.Current);
        Say($"saved as {CheckSettings.SavePath} ({new FileInfo(CheckSettings.SavePath).Length} bytes); Database.Filename of the open drawing after SaveAs: {Path.GetFileName(_db.Filename)}");
        using (var side = new Database(false, true))
        {
            side.ReadDwgFile(CheckSettings.SavePath, FileOpenMode.OpenForReadAndAllShare, true, "");
            side.CloseInput(true);
            using var tr = side.TransactionManager.StartTransaction();
            var all = RunStore.ReadAll(tr, side, Report);
            Say("reopened store: " + string.Join("; ", all.Select(a => $"{a.Key} canModify={a.Result.CanModify} run='{a.Result.Record?.Run.Name}' objects={a.Result.Record?.Objects.Count} {string.Join(" ", a.Result.Problems.Select(p => p.Code))}")));
            foreach (string name in new[] { Std.Name, Hc.Name })
            {
                var r = RunStore.Read(tr, side, name, Report);
                var rec = r.Record!;
                int resolved = rec.Objects.Count(o => Resolves(tr, side, o, name));
                var sources = rec.Sources.Select(src => RunStore.ResolveSource(tr, side, src)).ToList();
                bool same = RunPersistence.Serialize(rec.Run) == RunPersistence.Serialize(live[name].Run) && rec.Objects.SequenceEqual(live[name].Objects);
                Check("E809", r.CanModify && same && resolved == rec.Objects.Count && sources.All(x => x.Problem is null && !x.Id.IsNull),
                    $"run '{name}' after save-as and ReadDwgFile: record identical={same}, {resolved}/{rec.Objects.Count} handles resolve to objects tagged for this run, {sources.Count} source(s) resolve by handle");
            }
            tr.Commit();
        }

        // ---- E810: owned objects copied into another drawing without the dictionary.
        if (File.Exists(CheckSettings.CopyPath)) File.Delete(CheckSettings.CopyPath);
        var stdHandles = live[Std.Name].Objects.ToDictionary(o => o.Key, o => o.Handle);
        Guid s1 = Std.Segments[0].Id;
        var ids = new ObjectIdCollection
        {
            WritersModel.Handles.Resolve(_db, stdHandles[BKey(s1)]),
            WritersModel.Handles.Resolve(_db, stdHandles[TKey(s1)]),
            WritersModel.Handles.Resolve(_db, stdHandles[new OwnedKey(s1, OwnedRole.MatchMark, OwnedQualifiers.EndText)]),
        };
        using (var copy = new Database(true, true))
        {
            using (var map = new IdMapping())
                _db.WblockCloneObjects(ids, SymbolUtilityServices.GetBlockModelSpaceId(copy), map, DuplicateRecordCloning.Ignore, false);
            copy.SaveAs(CheckSettings.CopyPath, DwgVersion.Current);
        }
        using (var side = new Database(false, true))
        {
            side.ReadDwgFile(CheckSettings.CopyPath, FileOpenMode.OpenForReadAndAllShare, true, "");
            side.CloseInput(true);
            using var tr = side.TransactionManager.StartTransaction();
            var nod = (DBDictionary)tr.GetObject(side.NamedObjectsDictionaryId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(side), OpenMode.ForRead);
            var types = new List<string>();
            int tagged = 0;
            foreach (ObjectId id in ms)
            {
                var o = tr.GetObject(id, OpenMode.ForRead);
                types.Add(o.GetType().Name);
                if (OwnerTag.Read(tr, o, Cfg.OwnerTagKey) is not null) tagged++;
            }
            var all = RunStore.ReadAll(tr, side, Report);
            Check("E810", !nod.Contains(Report.Persistence.DictionaryName) && all.Count == 0 && types.Count == 3,
                $"copy saved to {Path.GetFileName(CheckSettings.CopyPath)} and reopened: no {Report.Persistence.DictionaryName} dictionary, store lists {all.Count} runs, model space holds plain {string.Join(", ", types)} ({tagged} carry the inert ownership tag)");
            tr.Commit();
        }
    }

    private static bool Resolves(Transaction tr, Database db, StoredObjectRef o, string runName)
    {
        var id = WritersModel.Handles.Resolve(db, o.Handle);
        if (id.IsNull) return false;
        var obj = tr.GetObject(id, OpenMode.ForRead);
        return OwnerTag.Read(tr, obj, Cfg.OwnerTagKey) is { } tag && tag.Matches(runName, o.Key);
    }

    private static Xrecord StoreXrecord(Transaction tr, Database db, string runName)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        var dict = (DBDictionary)tr.GetObject(nod.GetAt(Report.Persistence.DictionaryName), OpenMode.ForRead);
        return (Xrecord)tr.GetObject(dict.GetAt(RunStore.KeyFor(runName)), OpenMode.ForRead);
    }
}
