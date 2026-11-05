namespace SheetVol.Core.Output;

/// <summary>
/// Stable problem codes raised by Core.Output (HANDOFF.md 6.13). A code that matches an
/// edge-case row carries that row's number; codes from 530 up have no row. Codes never change
/// meaning once released.
/// </summary>
public static class OutputCodes
{
    /// <summary>Error. The stored run's schema is newer than this plugin's; the run is refused for modification (E105).</summary>
    public const string StoredSchemaNewer = "SV-E105";

    /// <summary>Warning. A bound label was not found in the table; that value is skipped and the rest still update (6.16, E1015, E1018).</summary>
    public const string BoundLabelMissing = "SV-E1015";

    /// <summary>Error. The table template is malformed or inconsistent.</summary>
    public const string TemplateInvalid = "SV-E530";

    /// <summary>Error. The table template file named in config was not found or could not be read.</summary>
    public const string TemplateNotFound = "SV-E531";

    /// <summary>Info. The table template in use is marked as a placeholder until the real layout is supplied.</summary>
    public const string TemplatePlaceholder = "SV-E532";

    /// <summary>Error. Persisted run JSON or its chunks are missing, malformed or incomplete.</summary>
    public const string PersistedJsonInvalid = "SV-E533";

    /// <summary>Error. A config value is missing, out of range or malformed.</summary>
    public const string ConfigInvalid = "SV-E534";

    /// <summary>Error. A table or export was requested from a run that did not reconcile; nothing is produced.</summary>
    public const string ResultNotReconciled = "SV-E535";

    /// <summary>Error. A segment table was requested for a segment the result does not hold.</summary>
    public const string UnknownSegment = "SV-E536";

    /// <summary>Warning. More than one row starts with a bound label; that value is skipped rather than guessed.</summary>
    public const string BoundLabelAmbiguous = "SV-E537";

    /// <summary>Warning. Several materials have different impacted length or area; rows 6 and 7 show the first material's.</summary>
    public const string MaterialsDisagree = "SV-E538";
}
