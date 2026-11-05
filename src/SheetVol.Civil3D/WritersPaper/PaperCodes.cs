// A3 work package. Owner: A3.
//
// Stable problem codes raised by the paper writers (HANDOFF.md 6.13). A code that matches
// an edge-case row carries that row's number (7.5, 7.6, 7.8). SV-E690 and SV-E691 have no
// row. SV-E197 is the headless check command's probe-step failure code (NOW.md registry:
// SV-E1xx belongs to fixtures and probes).

namespace SheetVol.Civil3D.WritersPaper;

/// <summary>Problem codes of the paper writers.</summary>
public static class PaperCodes
{
    /// <summary>Error. The template file, its layout, the plan viewport or the table anchor is missing or ambiguous, or a configured clip layer is missing. Raised before any write (E601).</summary>
    public const string TemplateMissing = "SV-E601";

    /// <summary>Info. The template layout holds viewports besides the plan viewport; only the plan viewport is driven (E602). Also reports a plan viewport chosen as the largest.</summary>
    public const string OtherViewports = "SV-E602";

    /// <summary>Info. A locked viewport was unlocked, updated and relocked (E604).</summary>
    public const string ViewportLocked = "SV-E604";

    /// <summary>Info: an annotation scale was added (E605). Error: the name for it is taken by a scale of another ratio.</summary>
    public const string AnnotationScale = "SV-E605";

    /// <summary>Info. The viewport was moved or resized on paper: position and size kept, view re-applied; or LockView kept it untouched (E608).</summary>
    public const string ViewportMoved = "SV-E608";

    /// <summary>Info. A layout the run owns is no longer planned, or a second layout claims the same segment: listed, never deleted (E612).</summary>
    public const string OrphanLayout = "SV-E612";

    /// <summary>Info. A title-block attribute tag from config is not on the layout; skipped (E615).</summary>
    public const string TitleTagMissing = "SV-E615";

    /// <summary>Error. The table style or layer named in config is missing from the drawing (E501).</summary>
    public const string TableStyleMissing = "SV-E501";

    /// <summary>Warning. The table's rows or columns were changed by hand; it was rebuilt in place, keeping position, style and layer (E502).</summary>
    public const string TableRebuilt = "SV-E502";

    /// <summary>Info. The owned paper table was missing and was created at the anchor (E503).</summary>
    public const string TableRecreated = "SV-E503";

    /// <summary>Info. A table on a locked layer was updated; the layer stays locked (E504).</summary>
    public const string TableOnLockedLayer = "SV-E504";

    /// <summary>Info. The table grew or shrank by whole analysis blocks in place (E505).</summary>
    public const string TableResized = "SV-E505";

    /// <summary>Info. An owned object (plan viewport, clip) was missing and was recreated (E802).</summary>
    public const string OwnedObjectRecreated = "SV-E802";

    /// <summary>Error. The paper writer hit an unexpected AutoCAD failure; the caller aborts the transaction. No row.</summary>
    public const string WriterFailed = "SV-E690";

    /// <summary>Error. A paper config value is missing, out of range or malformed. No row.</summary>
    public const string ConfigInvalid = "SV-E691";

    /// <summary>Probe-step failure in the SVWRITERSPAPER headless check (registry: SV-E1xx = fixtures and probes).</summary>
    public const string CheckFailed = "SV-E197";
}

/// <summary>Thrown by <see cref="PaperWriter.Apply"/> on a failure the caller must turn into an aborted transaction.</summary>
public sealed class PaperWriterException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The stable SV-Exxx code.</summary>
    public string Code { get; } = code;
}
