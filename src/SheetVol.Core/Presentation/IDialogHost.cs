namespace SheetVol.Core.Presentation;

/// <summary>Unit system of the active drawing. Drives labels, offered area units and volume units (E914, E917, E1020).</summary>
public enum UnitSystem
{
    /// <summary>Feet: ft, SF or AC, CY.</summary>
    Imperial,

    /// <summary>Metres: m, m² or ha, m³.</summary>
    Metric,
}

/// <summary>The drawing the dialog is bound to.</summary>
/// <param name="Name">Identity of the drawing, normally its full path. Pending edits are keyed by it (E907).</param>
/// <param name="Units">Unit system of the drawing.</param>
public sealed record DrawingInfo(string Name, UnitSystem Units);

/// <summary>What the change tracker knows about a run (6.11, E912).</summary>
/// <param name="IsStale">True when the stored fingerprint differs from the current sources, or the host cannot tell.</param>
/// <param name="LastResult">The last successful regen of this run in this session, from the dialog or a typed SVREGEN; null when none is known.</param>
public sealed record RunStatus(bool IsStale, RegenResult? LastResult);

/// <summary>
/// Everything the dialog needs that the frozen <see cref="ISheetVolSession"/> does not
/// carry. U2 implements it in <c>SheetVol.UI/Host</c> next to the editor side of the
/// session; the test suite implements it with a fake. Each member is a candidate for a
/// later contract revision; until then this interface is the Presentation-side port.
/// </summary>
public interface IDialogHost
{
    /// <summary>The active drawing, or null when none is open (E901). Read again on every <see cref="ISheetVolSession.SourcesChanged"/>.</summary>
    DrawingInfo? ActiveDrawing { get; }

    /// <summary>Displayed station text for a raw station, honoring station equations (E208). Backed by <see cref="IAlignmentGeometry.FormatStation"/>.</summary>
    /// <param name="alignmentName">Alignment the station belongs to.</param>
    /// <param name="rawStation">Raw station.</param>
    string FormatStation(string alignmentName, double rawStation);

    /// <summary>Raw start and end station of an alignment for the left-panel readout; null when the alignment is not found.</summary>
    /// <param name="alignmentName">Alignment name.</param>
    (double Start, double End)? StationRange(string alignmentName);

    /// <summary>
    /// Tracker staleness and the last regen result of a run (E912). Called when the dialog
    /// opens and after every <see cref="ISheetVolSession.SourcesChanged"/>. A host that
    /// cannot tell whether the run is current returns <c>IsStale = true</c>.
    /// </summary>
    /// <param name="runName">Run name.</param>
    RunStatus GetRunStatus(string runName);

    /// <summary>True while an editor command is active; Regenerate is disabled until it ends (E905). The host calls <see cref="DialogViewModel.RefreshCommandState"/> when this changes.</summary>
    bool IsCommandActive { get; }

    /// <summary>Export the run's CSV (the footer's Export CSV). The session has no export member; the host runs SVEXPORT.</summary>
    /// <param name="runName">Run name.</param>
    void RequestExport(string runName);
}
