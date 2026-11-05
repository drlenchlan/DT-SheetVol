// A2 model writers: the shared create / update / erase driver. Owner: A2.
//
// Two phases so that a missing style, layer, layout or anchor fails before anything is
// written (E501, E601, 6.10):
//   Plan    read-only. Resolve every stored handle (E802), check the ownership tag, compute
//           planned and live content signatures with C3's ContentSignatures, and let C3's
//           ChangeSetComparer decide Create / Update / Unchanged / Erase per owned key.
//   Execute writes only what the plan said. An Unchanged object is never opened for write,
//           so a regen with nothing changed writes nothing (E811, T19).

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersModel;

/// <summary>Base of every model writer. One instance handles one kind of owned object for one apply.</summary>
/// <typeparam name="TSpec">What is planned for one owned key.</typeparam>
/// <typeparam name="TEntity">The AutoCAD entity type it draws.</typeparam>
public abstract class OwnedWriter<TSpec, TEntity> where TEntity : Entity
{
    private readonly List<(OwnedKey Key, TSpec Spec)> _planned = [];
    private readonly Dictionary<OwnedKey, TEntity> _live = [];
    private readonly HashSet<OwnedKey> _recreate = [];
    private readonly HashSet<OwnedKey> _skipCreate = [];
    private ChangeSet? _changes;

    /// <summary>Starts a writer in the caller's context.</summary>
    protected OwnedWriter(ModelWriteContext ctx) => Ctx = ctx;

    /// <summary>The caller's context.</summary>
    protected ModelWriteContext Ctx { get; }

    /// <summary>The caller's transaction.</summary>
    protected Transaction Tr => Ctx.Transaction;

    /// <summary>The drawing.</summary>
    protected Database Db => Ctx.Database;

    /// <summary>Short name for messages ("boundary", "match line", ...).</summary>
    protected abstract string What { get; }

    /// <summary>True when a stored key belongs to this writer.</summary>
    public abstract bool Owns(OwnedKey key);

    /// <summary>Input checks for one spec (SV-E541 errors).</summary>
    protected abstract IEnumerable<Problem> Validate(OwnedKey key, TSpec spec);

    /// <summary>Resolves layers and styles the planned specs need (SV-E501 errors). Called only when something is planned.</summary>
    protected abstract void ResolveResources(IReadOnlyList<(OwnedKey Key, TSpec Spec)> planned, List<Problem> problems);

    /// <summary>
    /// Planned and live content signatures for one key. <paramref name="live"/> is null when no
    /// live object exists (the object will be created); the live signature is then ignored.
    /// </summary>
    protected abstract (string Planned, string? Live) Signatures(OwnedKey key, TSpec spec, TEntity? live, List<Problem> notes);

    /// <summary>Checks a creation can happen (placement, layout, anchor). Add an error to stop the run, or return false to skip this one object with a warning already added.</summary>
    protected virtual bool PrepareCreate(OwnedKey key, TSpec spec, bool recreate, List<Problem> problems) => true;

    /// <summary>Creates, appends and returns the entity. The base writes the ownership tag afterwards.</summary>
    protected abstract TEntity Create(OwnedKey key, TSpec spec, LayerGuard guard, List<Problem> notes);

    /// <summary>Updates an existing entity, already open for write.</summary>
    protected abstract void Update(TEntity entity, OwnedKey key, TSpec spec, List<Problem> notes);

    /// <summary>Row keys written into the tag after create (tables); empty for other kinds.</summary>
    protected virtual IReadOnlyList<string> TagRowKeys(TSpec spec) => [];

    /// <summary>Code of the note raised when an erased object is recreated: SV-E802, or SV-E503 for tables.</summary>
    protected virtual string RecreatedCode => ModelWriterCodes.ObjectRecreated;

    /// <summary>Phase 1, read-only: plan the changes. Errors added to <paramref name="problems"/> stop the apply.</summary>
    public void Plan(IEnumerable<(OwnedKey Key, TSpec Spec)> planned, IReadOnlyList<StoredObjectRef> stored, List<Problem> problems)
    {
        int first = problems.Count;   // stop on this writer's own errors, so every writer reports what it is missing in one pass
        _planned.AddRange(planned);
        foreach (var dup in _planned.GroupBy(p => p.Key).Where(g => g.Count() > 1))
            problems.Add(new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"The {What} plan lists {dup.Key} {dup.Count()} times.", NullIfEmpty(dup.Key.SegmentId)));
        foreach (var (key, spec) in _planned)
        {
            if (!Owns(key))
                problems.Add(new Problem(ModelWriterCodes.InvalidInput, Severity.Error, $"Key {key} was planned as a {What} but is not one.", NullIfEmpty(key.SegmentId)));
            problems.AddRange(Validate(key, spec));
        }
        if (_planned.Count > 0) ResolveResources(_planned, problems);
        if (problems.Skip(first).Any(p => p.Severity == Severity.Error)) return;

        var mine = stored.Where(s => Owns(s.Key)).ToList();
        var storedObjects = new List<StoredObject>();
        var plannedByKey = _planned.ToDictionary(p => p.Key, p => p.Spec);
        foreach (var s in mine)
        {
            TEntity? entity = null;
            ObjectId id = Handles.Resolve(Db, s.Handle);
            if (!id.IsNull && Tr.GetObject(id, OpenMode.ForRead) is TEntity e)
            {
                var tag = OwnerTag.Read(Tr, e, Ctx.Config.OwnerTagKey);
                if (tag is not null && tag.Matches(Ctx.RunName, s.Key)) entity = e;
            }
            if (entity is null)
            {
                if (plannedByKey.ContainsKey(s.Key)) _recreate.Add(s.Key);
                storedObjects.Add(new StoredObject(s.Key, s.Handle, false, null));
                continue;
            }
            _live[s.Key] = entity;
            storedObjects.Add(new StoredObject(s.Key, s.Handle, true, null));
        }

        // One Signatures call per planned key: planned content, and the live object's content
        // when it exists. Stored keys that are no longer planned need no signature (erased).
        var sigs = new Dictionary<OwnedKey, (string Planned, string? Live)>();
        foreach (var (key, spec) in _planned)
        {
            _live.TryGetValue(key, out TEntity? live);
            sigs[key] = Signatures(key, spec, live, problems);
        }
        storedObjects = storedObjects
            .Select(so => so.Exists && sigs.TryGetValue(so.Key, out var s) ? so with { Signature = s.Live } : so)
            .ToList();
        var plannedObjects = _planned.Select(p => new PlannedObject(p.Key, sigs[p.Key].Planned)).ToList();

        _changes = ChangeSetComparer.Compare(plannedObjects, storedObjects, "", "");
        foreach (var c in _changes.Changes.Where(c => c.Kind == ChangeKind.Create))
            if (!PrepareCreate(c.Key, plannedByKey[c.Key], _recreate.Contains(c.Key), problems)) _skipCreate.Add(c.Key);
    }

    /// <summary>Phase 2: apply the planned changes. Call only when no plan of the apply raised an error.</summary>
    public void Execute(LayerGuard guard, List<ObjectChange> changes, List<StoredObjectRef> objects, List<Problem> notes)
    {
        if (_changes is null) throw new InvalidOperationException("Execute called before Plan.");
        var specs = _planned.ToDictionary(p => p.Key, p => p.Spec);
        foreach (var change in _changes.Changes)
        {
            var key = change.Key;
            switch (change.Kind)
            {
                case ChangeKind.Create:
                {
                    if (_skipCreate.Contains(key)) continue;
                    var spec = specs[key];
                    TEntity e = Create(key, spec, guard, notes);
                    new OwnerTag(Ctx.RunName, key, TagRowKeys(spec)).Write(Tr, e, Ctx.Config.OwnerTagKey);
                    string handle = Handles.Of(e.ObjectId);
                    if (_recreate.Contains(key))
                        notes.Add(new Problem(RecreatedCode, Severity.Info,
                            $"{Capital(What)} {Describe(key)} was erased or replaced; it was recreated as {handle} and the stored handle repaired.", NullIfEmpty(key.SegmentId)));
                    changes.Add(new ObjectChange(key, ChangeKind.Create, handle));
                    objects.Add(StoredObjectRef.From(key, handle));
                    break;
                }
                case ChangeKind.Update:
                {
                    var e = _live[key];
                    guard.Allow(e.LayerId);
                    e.UpgradeOpen();
                    Update(e, key, specs[key], notes);
                    changes.Add(change);
                    objects.Add(StoredObjectRef.From(key, change.Handle!));
                    break;
                }
                case ChangeKind.Unchanged:
                case ChangeKind.Kept:
                    changes.Add(change);
                    objects.Add(StoredObjectRef.From(key, change.Handle!));
                    break;
                case ChangeKind.Erase:
                {
                    var e = _live[key];
                    guard.Allow(e.LayerId);
                    e.UpgradeOpen();
                    e.Erase();
                    changes.Add(change);
                    notes.Add(new Problem(ModelWriterCodes.SegmentRemovedErased, Severity.Info,
                        $"{Capital(What)} {Describe(key)} ({change.Handle}) is no longer planned and was erased.", NullIfEmpty(key.SegmentId)));
                    break;
                }
                case ChangeKind.Orphan:
                    throw new InvalidOperationException($"{key} is not a model-writer role; orphans are A3's.");
            }
        }
    }

    /// <summary>Key text for messages.</summary>
    protected static string Describe(OwnedKey key)
        => key.SegmentId == Guid.Empty ? key.Role.ToString() : $"{key.Role}{(key.Qualifier.Length > 0 ? " " + key.Qualifier : "")} of segment {key.SegmentId:D}";

    /// <summary>Null for the run-level segment id.</summary>
    protected static Guid? NullIfEmpty(Guid id) => id == Guid.Empty ? null : id;

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
