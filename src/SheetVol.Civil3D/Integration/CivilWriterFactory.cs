// Wave 2 integration. A4's writer ports over A2's ModelWriter and RunStore and A3's
// PaperWriter, in the call order each package documented (WritersModel/README.md "Call order
// for A4", WritersPaper/README.md "What A4 calls"). A2 and A3 plan read-only, refuse with no
// writes, and compare the live objects themselves, so this file only translates the planned
// objects into their specs and their results back into owned handles.

using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Civil3D.Adapters;
using SheetVol.Civil3D.Ownership;
using SheetVol.Civil3D.Session;
using SheetVol.Civil3D.WritersModel;
using SheetVol.Civil3D.WritersPaper;
using SheetVol.Core;
using SheetVol.Core.Tracking;
using SheetVol.Core.Views;
using SheetVol.Core.Volumes;
using A2RunRecord = SheetVol.Civil3D.WritersModel.RunRecord;
using A4OrphanLayout = SheetVol.Civil3D.Session.OrphanLayout;
using A4RunRecord = SheetVol.Civil3D.Session.RunRecord;

namespace SheetVol.Civil3D.Integration;

/// <summary>The installed A2 and A3 writers and the A2 persistence store.</summary>
public sealed class CivilWriterFactory : IWriterFactory
{
    /// <inheritdoc />
    public IModelWriter Model(Database db, Transaction tr, SheetVolSettings settings) => new CivilModelWriter(db, tr, settings);

    /// <inheritdoc />
    public IPaperWriter Paper(Database db, Transaction tr, SheetVolSettings settings) => new CivilPaperWriter(db, tr, settings);

    /// <inheritdoc />
    public IPersistenceStore Store(Database db, Transaction tr, SheetVolSettings settings) => new DrawingRunStore(db, tr, settings);
}

/// <summary>Live inspection of an owned object through the one owner-tag format (Ownership/OwnerTag).</summary>
internal static class OwnedInspector
{
    /// <summary>The object exists, is not erased and carries the run's tag for <paramref name="key"/>; the signature describes its live content.</summary>
    public static StoredObject Inspect(Database db, Transaction tr, string tagKey, string runName, OwnedKey key, string? handle)
    {
        if (!TryResolve(db, handle, out var id)) return new StoredObject(key, handle, false, null);
        var obj = tr.GetObject(id, OpenMode.ForRead);
        bool owned = OwnerTag.Read(tr, obj, tagKey) is { } tag && tag.Matches(RunIds.For(runName), key);
        return new StoredObject(key, handle, owned, owned ? LiveSignature(tr, obj) : null);
    }

    /// <summary>A description of what the object shows now, for comparing drawing states (T20 snapshots).</summary>
    public static string LiveSignature(Transaction tr, DBObject obj)
    {
        var texts = new List<string?> { obj.GetType().Name };
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        switch (obj)
        {
            case Polyline pl:
                for (int i = 0; i < pl.NumberOfVertices; i++) texts.Add($"{R(pl.GetPoint2dAt(i).X)},{R(pl.GetPoint2dAt(i).Y)},{R(pl.GetBulgeAt(i))}");
                texts.Add(pl.Layer);
                break;
            case Line ln:
                texts.Add($"{R(ln.StartPoint.X)},{R(ln.StartPoint.Y)},{R(ln.EndPoint.X)},{R(ln.EndPoint.Y)},{ln.Layer}");
                break;
            case MText mt:
                texts.Add($"{mt.Contents}|{R(mt.Location.X)},{R(mt.Location.Y)}|{R(mt.TextHeight)}|{mt.Layer}");
                break;
            case Table t:
                texts.AddRange(TableRenderer.ReadCells(t).SelectMany(r => r));
                texts.Add($"{R(t.Position.X)},{R(t.Position.Y)}|{t.Layer}");
                break;
            case Viewport vp:
                texts.Add($"{R(vp.ViewTarget.X)},{R(vp.ViewTarget.Y)}|{R(vp.TwistAngle)}|{R(vp.CustomScale)}|{R(vp.ViewCenter.X)},{R(vp.ViewCenter.Y)}|{R(vp.CenterPoint.X)},{R(vp.CenterPoint.Y)}|{R(vp.Width)}x{R(vp.Height)}|{vp.Locked}");
                break;
            case Layout lay:
                texts.Add(lay.LayoutName);
                break;
            case BlockReference br:
                foreach (ObjectId a in br.AttributeCollection)
                    if (!a.IsErased && tr.GetObject(a, OpenMode.ForRead) is AttributeReference ar) texts.Add($"{ar.Tag}={ar.TextString}");
                break;
        }
        return ContentSignatures.OfTexts(texts);
    }

    public static bool TryResolve(Database db, string? handle, out ObjectId id)
    {
        id = ObjectId.Null;
        if (string.IsNullOrWhiteSpace(handle) || !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value) || value <= 0) return false;
        if (!db.TryGetObjectId(new Handle(value), out var found) || found.IsNull || found.IsErased || !found.IsValid) return false;
        id = found;
        return true;
    }
}

/// <summary>A4's model writer port over A2's <see cref="ModelWriter"/>.</summary>
internal sealed class CivilModelWriter(Database db, Transaction tr, SheetVolSettings settings) : IModelWriter
{
    public IReadOnlySet<OwnedRole> Roles { get; } = ModelWriteResult.Roles.ToHashSet();

    public StoredObject Inspect(string runName, OwnedKey key, string? handle) =>
        OwnedInspector.Inspect(db, tr, settings.ModelWriters.OwnerTagKey, runName, key, handle);

    public WriterResult Apply(WriteRequest request)
    {
        var objects = request.Output.Objects;
        var plan = new ModelWritePlan(
            objects.OfType<BoundaryObject>().Select(b => b.Boundary).ToList(),
            objects.OfType<ImpactOutlineObject>().Select(o => o.Spec).ToList(),
            objects.OfType<MatchMarkObject>().Select(m => m.Spec).Distinct().ToList(),   // one spec makes a line and a text: two planned keys
            objects.OfType<ModelTableObject>().Select(t => t.Spec).ToList(),
            objects.OfType<HcfcdTableObject>().Select(h => h.Spec).ToList());
        var ctx = new ModelWriteContext(tr, db, request.Run.Name, settings.ModelWriters, settings.Report, request.Units);
        return Map(ModelWriter.Apply(ctx, plan, Stored(request.Run, request.Prior)));
    }

    public WriterResult Clear(string runName, A4RunRecord record)
    {
        var units = DrawingUnitsReader.Read(db, settings.Report.Volumes.Constants);
        if (!units.Succeeded) return WriterResult.Refused(units.Problems);
        var ctx = new ModelWriteContext(tr, db, runName, settings.ModelWriters, settings.Report, units.Value.VolumeUnits);
        return Map(ModelWriter.Apply(ctx, ModelWritePlan.Empty, Stored(record.Run, record)));   // A2's SVCLEAR: an empty plan erases the run's model objects
    }

    /// <summary>What A2 reads of the prior record: its own roles' handles (A3's are left alone).</summary>
    private RunStoreReadResult Stored(RunDefinition run, A4RunRecord? prior)
    {
        var mine = (prior?.Owned ?? []).Where(o => Roles.Contains(o.Key.Role)).Select(o => StoredObjectRef.From(o.Key, o.Handle)).ToList();
        return new RunStoreReadResult(new A2RunRecord(run, mine, [], [], null), true, RunStore.SchemaVersion, null, []);
    }

    private static WriterResult Map(ModelWriteResult r)
    {
        if (!r.Succeeded) return WriterResult.Refused(r.Problems);
        int C(ChangeKind k) => r.Changes.Count(c => c.Kind == k);
        var counts = new ApplyCounts(C(ChangeKind.Create), C(ChangeKind.Update), C(ChangeKind.Erase), 0, 0, r.Writes.Count());
        return new WriterResult(true, r.Objects.Select(o => new OwnedHandle(o.Key, o.Handle)).ToList(), [], counts, r.Problems);
    }
}

/// <summary>A4's paper writer port over A3's <see cref="PaperWriter"/>. Holds the loaded template until disposed.</summary>
internal sealed class CivilPaperWriter : IPaperWriter
{
    private readonly Database _db;
    private readonly Transaction _tr;
    private readonly SheetVolSettings _settings;
    private readonly PaperWriter _writer;
    private PaperTemplate? _template;

    public CivilPaperWriter(Database db, Transaction tr, SheetVolSettings settings)
    {
        _db = db;
        _tr = tr;
        _settings = settings;
        _writer = new PaperWriter(settings.Paper, settings.Report.Placement, new LayoutNamer(settings.LayoutNames));
    }

    public IReadOnlySet<OwnedRole> Roles { get; } = new HashSet<OwnedRole> { OwnedRole.Layout, OwnedRole.Viewport, OwnedRole.Clip, OwnedRole.PaperTable, OwnedRole.TitleAttributes };

    public StoredObject Inspect(string runName, OwnedKey key, string? handle) =>
        OwnedInspector.Inspect(_db, _tr, _settings.Paper.OwnerTagKey, runName, key, handle);

    public Outcome<TemplateInfo> Template()
    {
        if (_template is null)
        {
            var loaded = _writer.LoadTemplate();   // ReadDwgFile into a side database; writes nothing (E601)
            if (!loaded.Succeeded) return Outcome<TemplateInfo>.Fail(loaded.Problems);
            _template = loaded.Value;
        }
        return Outcome<TemplateInfo>.Ok(new TemplateInfo(_template.PlanViewportWidth, _template.PlanViewportHeight, _template.AnchorFromBlock), _template.Notes);
    }

    public IReadOnlyList<LayoutRef> Layouts()
    {
        var list = new List<LayoutRef>();
        var dict = (DBDictionary)_tr.GetObject(_db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dict)
            if (_tr.GetObject(e.Value, OpenMode.ForRead) is Layout { ModelType: false } lay) list.Add(new LayoutRef(lay.LayoutName, lay.Handle.ToString()));
        return list;
    }

    public IReadOnlyList<Problem> Validate(WriteRequest request)
    {
        var sheets = Sheets(request);
        if (sheets.Count == 0) return [];
        if (Template() is { Succeeded: false } t) return t.Problems;
        return _writer.Validate(_tr, _db, _template!, new PaperRunRequest(request.RunId, sheets));
    }

    public WriterResult Apply(WriteRequest request)
    {
        var sheets = Sheets(request);
        if (sheets.Count == 0) return LayoutsOff(request);
        if (Template() is { Succeeded: false } t) return WriterResult.Refused(t.Problems);

        PaperApplyResult r;
        try
        {
            r = _writer.Apply(_tr, _db, _template!, new PaperRunRequest(request.RunId, sheets));
        }
        catch (PaperWriterException ex)
        {
            return WriterResult.Refused([new Problem(ex.Code, Severity.Error, ex.Message, Fix: "The transaction is aborted; nothing in this run changed. Report this with the drawing if it repeats.")]);
        }
        if (!r.Succeeded) return WriterResult.Refused(r.Problems);

        // Owned keys come from the owner tags A3 wrote, so they are exactly what Inspect and FindOwned see.
        var owned = new List<OwnedHandle>();
        foreach (var s in r.Sheets)
            foreach (var h in new[] { s.LayoutHandle, s.ViewportHandle, s.ClipHandle, s.TableHandle }.Concat(s.TitleBlockHandles))
                if (h is not null && OwnedInspector.TryResolve(_db, h, out var id) && OwnerTag.Read(_tr, _tr.GetObject(id, OpenMode.ForRead), _settings.Paper.OwnerTagKey) is { } tag)
                    owned.Add(new OwnedHandle(tag.Key, h));

        var changes = r.Sheets.SelectMany(s => new[] { s.Layout, s.Viewport, s.Clip, s.Table, s.Attributes }).ToList();
        int renamed = r.RenamePlan is { } rp ? rp.FirstPass.Count + rp.SecondPass.Count : 0;
        var counts = new ApplyCounts(changes.Count(c => c == PaperChange.Created), changes.Count(c => c == PaperChange.Updated), 0, renamed, r.Orphans.Count, r.Writes);
        var orphans = r.Orphans.Select(o => new A4OrphanLayout(o.SegmentId, o.Handle, o.Name, request.NowUtc)).ToList();
        return new WriterResult(true, owned, orphans, counts, r.Problems);
    }

    /// <summary>No sheets planned (layouts off, or no station segments): A3 does not run; layouts the run still owns become orphans (E612).</summary>
    private WriterResult LayoutsOff(WriteRequest request)
    {
        var orphans = new List<A4OrphanLayout>();
        foreach (var o in (request.Prior?.Owned ?? []).Where(o => o.Key.Role == OwnedRole.Layout))
            if (Inspect(request.Run.Name, o.Key, o.Handle).Exists && OwnedInspector.TryResolve(_db, o.Handle, out var id) && _tr.GetObject(id, OpenMode.ForRead) is Layout lay)
                orphans.Add(new A4OrphanLayout(o.Key.SegmentId, o.Handle, lay.LayoutName, request.NowUtc));
        return new WriterResult(true, [], orphans, ApplyCounts.None with { Orphaned = orphans.Count }, []);
    }

    /// <summary>A3's sheet requests in sheet order, from the planned layouts, viewports and sheet tables.</summary>
    private static List<PaperSheetRequest> Sheets(WriteRequest request)
    {
        var objects = request.Output.Objects;
        var views = objects.OfType<ViewportObject>().ToDictionary(v => v.Key.SegmentId);
        var tables = objects.OfType<PaperTableObject>().ToDictionary(t => t.Key.SegmentId);
        var sheets = new List<PaperSheetRequest>();
        foreach (var layout in objects.OfType<LayoutObject>().OrderBy(l => l.SheetNumber))
        {
            var seg = layout.Key.SegmentId;
            if (!views.TryGetValue(seg, out var vp)) continue;   // a sheet needs its view; C3 computes one per station segment
            string? stored = request.Prior?.Owned.FirstOrDefault(o => o.Key == layout.Key)?.Handle;
            sheets.Add(new PaperSheetRequest(seg, layout.Parts, vp.View, tables.TryGetValue(seg, out var t) ? t.Table : null, vp.LockView, stored));
        }
        return sheets;
    }

    public void DeleteLayout(string handle)
    {
        if (!OwnedInspector.TryResolve(_db, handle, out var id) || _tr.GetObject(id, OpenMode.ForRead) is not Layout lay) return;
        var previous = HostApplicationServices.WorkingDatabase;
        if (!ReferenceEquals(previous, _db)) HostApplicationServices.WorkingDatabase = _db;
        try { LayoutManager.Current.DeleteLayout(lay.LayoutName); }
        finally { if (!ReferenceEquals(previous, _db)) HostApplicationServices.WorkingDatabase = previous; }
    }

    public void Dispose()
    {
        _template?.Dispose();
        _template = null;
    }
}

/// <summary>A4's persistence port over A2's <see cref="RunStore"/> (6.9); C3's fingerprint travels as text through <see cref="RunRecordCodec"/>.</summary>
internal sealed class DrawingRunStore(Database db, Transaction tr, SheetVolSettings settings) : IPersistenceStore
{
    public IReadOnlyList<string> RunNames() =>
        RunStore.ReadAll(tr, db, settings.Report).Select(x => x.Result.Record?.Run.Name).OfType<string>().Order(StringComparer.Ordinal).ToList();

    public StoredRunLoad Load(string runName)
    {
        var r = RunStore.Read(tr, db, runName, settings.Report);
        if (!r.CanModify) return new StoredRunLoad(true, null, false, r.Problems);
        if (r.Record is not { } a2) return StoredRunLoad.Missing;
        var fingerprint = RunRecordCodec.FingerprintFromText(a2.Fingerprint);
        var record = new A4RunRecord(
            a2.Run,
            fingerprint,
            a2.Objects.Select(o => new OwnedHandle(o.Key, o.Handle)).ToList(),
            a2.Sources.Select(s => new SourceHandle(s.Kind, s.Name, s.Handle)).ToList(),
            a2.Orphans.Where(o => o.Role == OwnedRole.Layout)
                .Select(o => new A4OrphanLayout(o.SegmentId, o.Handle, o.Name ?? "", o.SinceUtc ?? Stamp(r) ?? DateTimeOffset.MinValue)).ToList(),
            fingerprint is null ? null : Stamp(r),
            a2.LastProblems ?? []);
        return new StoredRunLoad(true, record, true, []);
    }

    public void Save(A4RunRecord record)
    {
        var a2 = new A2RunRecord(
            record.Run,
            record.Owned.Select(o => StoredObjectRef.From(o.Key, o.Handle)).ToList(),
            record.Sources.Select(s => new SourceRef(s.Kind, s.Name, s.Handle)).ToList(),
            record.Orphans.Select(o => new OrphanRef(o.SegmentId, OwnedRole.Layout, "", o.Handle, o.Name, o.SinceUtc)).ToList(),
            RunRecordCodec.FingerprintToText(record.Fingerprint),
            record.LastProblems);
        // A2's one timestamp is the last regen's (6.9, E811); a definition edit keeps it.
        var when = (record.LastRegenUtc ?? DateTimeOffset.UtcNow).UtcDateTime;
        var w = RunStore.Write(tr, db, a2, when, settings.Report);
        if (w.Kind == StoreWriteKind.Refused)
            throw new InvalidOperationException($"Run '{record.Run.Name}' was not saved: " + string.Join(" ", w.Problems.Select(p => $"{p.Code} {p.Message}")));
    }

    public void Delete(string runName)
    {
        var problems = new List<Problem>();
        RunStore.Delete(tr, db, runName, settings.Report, problems);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems.Select(p => $"{p.Code} {p.Message}")));
    }

    private static DateTimeOffset? Stamp(RunStoreReadResult r) => r.TimestampUtc is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;
}
