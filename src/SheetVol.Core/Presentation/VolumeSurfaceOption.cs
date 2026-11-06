namespace SheetVol.Core.Presentation;

/// <summary>
/// One entry of the volume surface dropdown in a comparison card (rev 3, X6, E918): a user TIN
/// volume surface exactly as <see cref="ISheetVolSession.VolumeSurfaces"/> lists it. The host
/// already leaves out SheetVol-owned surfaces and the dialog adds nothing to its list. An entry
/// whose base or comparison was erased stays listed but cannot be picked;
/// <see cref="DisabledReason"/> says why (tooltip and inline text, E422).
/// </summary>
/// <param name="Info">The surface as the host read it on the last SourcesChanged.</param>
public sealed record VolumeSurfaceOption(VolumeSurfaceInfo Info)
{
    /// <summary>Volume surface name; the dropdown's selected value.</summary>
    public string Name => Info.Name;

    /// <summary>False when its sources were erased: listed, not pickable.</summary>
    public bool IsSelectable => Info.SourcesIntact;

    /// <summary>Why the entry cannot be picked, or null when it can.</summary>
    public string? DisabledReason => Info.SourcesIntact ? null : DialogText.VolumeSurfaceSourcesErased;

    /// <inheritdoc/>
    public override string ToString() => Name;
}
