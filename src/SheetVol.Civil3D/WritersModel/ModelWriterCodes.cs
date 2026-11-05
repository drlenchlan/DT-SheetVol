namespace SheetVol.Civil3D.WritersModel;

/// <summary>
/// Stable problem codes raised by the model writers and the persistence store (HANDOFF.md
/// 6.13). A code that matches an edge-case row carries that row's number; SV-E540 and up have
/// no row. C2's codes are reused where the meaning is C2's: SV-E105 (stored schema newer),
/// SV-E533 (persisted data malformed), SV-E534 (config invalid), SV-E1015, SV-E537, SV-E538
/// (HCFCD binding).
/// </summary>
public static class ModelWriterCodes
{
    /// <summary>Info. An object owned by a removed segment was erased (E308).</summary>
    public const string SegmentRemovedErased = "SV-E308";

    /// <summary>Error. A table style, layer or text style named in config is not in the drawing (E501). Nothing is written.</summary>
    public const string MissingStyleOrLayer = "SV-E501";

    /// <summary>Info. An erased table was recreated at its anchor (E503).</summary>
    public const string TableRecreated = "SV-E503";

    /// <summary>Info. A locked layer was unlocked for the update and relocked; or an object on a frozen layer was updated and the layer left frozen (E504).</summary>
    public const string LayerLockedOrFrozen = "SV-E504";

    /// <summary>Info. A table grew or shrank in place because an analysis block was added or removed (E505).</summary>
    public const string TableResized = "SV-E505";

    /// <summary>Error. A layout named for a table, or its anchor, is missing and the table has to be created (E601). Nothing is written.</summary>
    public const string LayoutOrAnchorMissing = "SV-E601";

    /// <summary>Info. A stored handle no longer resolves to this run's object; the object was recreated and the mapping repaired (E802).</summary>
    public const string ObjectRecreated = "SV-E802";

    /// <summary>Warning. A source's stored handle is dead; it was found again by name and the mapping repaired (E803).</summary>
    public const string SourceFoundByName = "SV-E803";

    /// <summary>Warning. A table's layout no longer exists and the table is gone; it is not re-created elsewhere (E1017).</summary>
    public const string TableLayoutGone = "SV-E1017";

    /// <summary>Error. A source named in the run cannot be found by handle or by name.</summary>
    public const string SourceMissing = "SV-E540";

    /// <summary>Error. A writer input is malformed (a caller defect): duplicate keys, a model whose rows do not match its column count, a non-finite vertex.</summary>
    public const string InvalidInput = "SV-E541";

    /// <summary>Warning. A bound HCFCD cell is covered by a merge the user made; writing it would hide the text, so that value was skipped (P13).</summary>
    public const string BoundCellCovered = "SV-E542";

    /// <summary>Probe step failed at the command boundary in the SVWRITERSMODEL check.</summary>
    public const string CheckFailed = "SV-E196";
}
