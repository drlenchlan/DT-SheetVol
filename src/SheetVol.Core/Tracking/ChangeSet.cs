namespace SheetVol.Core.Tracking;

/// <summary>The kinds of drawing object a run owns (6.9 per-segment handles).</summary>
public enum OwnedRole
{
    /// <summary>Model-space boundary polyline.</summary>
    Boundary,
    /// <summary>Match-line mark (line plus text) at a segment end. Qualifier: which end.</summary>
    MatchMark,
    /// <summary>Per-segment table placed in model space (A14). Qualifier: analysis id or block key.</summary>
    ModelTable,
    /// <summary>Paper-space table on the segment's layout. Qualifier: analysis id or block key.</summary>
    PaperTable,
    /// <summary>Master table (run level; segment id is <see cref="Guid.Empty"/>).</summary>
    MasterTable,
    /// <summary>Impacted-area outline (6.15).</summary>
    ImpactOutline,
    /// <summary>The segment's layout.</summary>
    Layout,
    /// <summary>The plan viewport on the layout (view center, twist, scale).</summary>
    Viewport,
    /// <summary>The viewport's clip entity.</summary>
    Clip,
    /// <summary>Title-block attribute values on the layout.</summary>
    TitleAttributes,
}

/// <summary>Identity of one owned object: segment, role and an optional qualifier (analysis id, "start"/"end").</summary>
public readonly record struct OwnedKey(Guid SegmentId, OwnedRole Role, string Qualifier = "");

/// <summary>An object the planned regen would write.</summary>
/// <param name="Key">Its identity.</param>
/// <param name="Signature">Content signature of what would be written (see <see cref="ContentSignatures"/>).</param>
/// <param name="Locked">Leave an existing object untouched whatever its content (per-segment LockView, E608). A missing one is still created.</param>
public sealed record PlannedObject(OwnedKey Key, string Signature, bool Locked = false);

/// <summary>
/// An object recorded in the run's persistence. <see cref="Signature"/> should be computed by
/// the adapter from the live object as it stands now, so a user edit to an owned object is
/// seen and repaired; null when it cannot be read.
/// </summary>
/// <param name="Key">Its identity.</param>
/// <param name="Handle">Stored handle.</param>
/// <param name="Exists">The handle resolves to a live, non-erased object (E802).</param>
/// <param name="Signature">Content signature of the live object, or null.</param>
public sealed record StoredObject(OwnedKey Key, string? Handle, bool Exists, string? Signature);

/// <summary>What happens to one owned object.</summary>
public enum ChangeKind
{
    /// <summary>Content matches; no write, handle kept.</summary>
    Unchanged,
    /// <summary>Locked view: no write, handle kept, even though content may differ (E608).</summary>
    Kept,
    /// <summary>Missing or erased: create it and record the new handle (E802).</summary>
    Create,
    /// <summary>Content differs: update in place, handle kept.</summary>
    Update,
    /// <summary>No longer planned: erase it (boundaries, marks, tables, outlines; E308).</summary>
    Erase,
    /// <summary>No longer planned: leave it and list it as an orphan (layouts and what lives on them; E612).</summary>
    Orphan,
}

/// <summary>One owned object's fate.</summary>
public sealed record ObjectChange(OwnedKey Key, ChangeKind Kind, string? Handle);

/// <summary>
/// The writes a regen needs (E811, T19). When <see cref="OnlyTimestamp"/> is true the
/// regen writes nothing but the persistence timestamp, and every handle is kept.
/// </summary>
/// <param name="Changes">Planned objects in planned order, then stored-only objects in stored order.</param>
/// <param name="RecordChanged">The persisted run record (minus its timestamp) differs from the stored one.</param>
public sealed record ChangeSet(IReadOnlyList<ObjectChange> Changes, bool RecordChanged)
{
    /// <summary>The changes that write to the database (create, update, erase).</summary>
    public IEnumerable<ObjectChange> Writes => Changes.Where(c => c.Kind is ChangeKind.Create or ChangeKind.Update or ChangeKind.Erase);

    /// <summary>Nothing to write except the persistence timestamp.</summary>
    public bool OnlyTimestamp => !RecordChanged && !Writes.Any();
}

/// <summary>
/// Compares a planned regen with the stored state and reports which objects need writes
/// (E811 idempotence). Pure. A regen with nothing changed gets <see cref="ChangeSet.OnlyTimestamp"/>.
/// </summary>
public static class ChangeSetComparer
{
    /// <summary>
    /// Builds the change set. <paramref name="plannedRecordSignature"/> and
    /// <paramref name="storedRecordSignature"/> are signatures of the persisted run record
    /// without its timestamp (definition, handles, fingerprint); null stored means none yet.
    /// Duplicate keys in either list are a bug and throw.
    /// </summary>
    public static ChangeSet Compare(IReadOnlyList<PlannedObject> planned, IReadOnlyList<StoredObject> stored,
                                    string plannedRecordSignature, string? storedRecordSignature)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(plannedRecordSignature);

        var storedByKey = new Dictionary<OwnedKey, StoredObject>();
        foreach (StoredObject s in stored)
            if (!storedByKey.TryAdd(s.Key, s))
                throw new ArgumentException($"Stored state lists {s.Key} twice.", nameof(stored));

        var plannedKeys = new HashSet<OwnedKey>();
        var changes = new List<ObjectChange>(planned.Count + stored.Count);
        foreach (PlannedObject p in planned)
        {
            if (!plannedKeys.Add(p.Key))
                throw new ArgumentException($"Planned regen lists {p.Key} twice.", nameof(planned));

            if (!storedByKey.TryGetValue(p.Key, out StoredObject? s) || !s.Exists || s.Handle is null)
                changes.Add(new ObjectChange(p.Key, ChangeKind.Create, null));
            else if (p.Locked)
                changes.Add(new ObjectChange(p.Key, ChangeKind.Kept, s.Handle));
            else if (string.Equals(p.Signature, s.Signature, StringComparison.Ordinal))
                changes.Add(new ObjectChange(p.Key, ChangeKind.Unchanged, s.Handle));
            else
                changes.Add(new ObjectChange(p.Key, ChangeKind.Update, s.Handle));
        }

        foreach (StoredObject s in stored)
        {
            if (plannedKeys.Contains(s.Key) || !s.Exists) continue;
            changes.Add(new ObjectChange(s.Key, RemovalFor(s.Key.Role), s.Handle));
        }

        bool recordChanged = !string.Equals(plannedRecordSignature, storedRecordSignature, StringComparison.Ordinal);
        return new ChangeSet(changes, recordChanged);
    }

    /// <summary>
    /// What happens to an owned object that is no longer planned: a layout, its viewport and
    /// clip stay as an orphan (never auto-deleted, E612); everything else is erased (E308).
    /// </summary>
    public static ChangeKind RemovalFor(OwnedRole role) => role switch
    {
        OwnedRole.Layout or OwnedRole.Viewport or OwnedRole.Clip or OwnedRole.TitleAttributes => ChangeKind.Orphan,
        _ => ChangeKind.Erase,
    };
}

/// <summary>
/// Content signatures for owned objects: SHA-256 over the canonical text of exactly what a
/// writer sets. Writers and adapters use the same functions for planned and live content so
/// the comparison in <see cref="ChangeSetComparer"/> is like for like.
/// </summary>
public static class ContentSignatures
{
    /// <summary>Signature of a view: model center, twist, scale, paper size and clip vertices.</summary>
    public static string Of(ViewSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var w = new CanonicalWriter().Section("view")
            .Add(spec.ModelCenter).Add(spec.TwistRadians).Add(spec.ModelUnitsPerPaperUnit)
            .Add(spec.PaperWidth).Add(spec.PaperHeight).Add(spec.ClipInPaper is not null);
        if (spec.ClipInPaper is not null) AddVertices(w, spec.ClipInPaper);
        return w.Sha256Hex();
    }

    /// <summary>Signature of a boundary as drawn (vertices with bulges).</summary>
    public static string Of(Boundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        return OfPolyline(boundary.Drawn);
    }

    /// <summary>Signature of a closed bulge polyline.</summary>
    public static string OfPolyline(IReadOnlyList<PolyVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var w = new CanonicalWriter().Section("polyline");
        AddVertices(w, vertices);
        return w.Sha256Hex();
    }

    /// <summary>Signature of an ordered list of texts (table cells, attribute values); null entries are distinct from empty ones.</summary>
    public static string OfTexts(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var list = texts.ToArray();
        var w = new CanonicalWriter().Section("texts").Add(list.Length);
        foreach (string? t in list) w.Add(t);
        return w.Sha256Hex();
    }

    private static void AddVertices(CanonicalWriter w, IReadOnlyList<PolyVertex> vertices)
    {
        w.Add(vertices.Count);
        foreach (PolyVertex v in vertices) w.Add(v);
    }
}
