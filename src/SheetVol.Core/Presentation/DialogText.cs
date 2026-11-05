namespace SheetVol.Core.Presentation;

/// <summary>
/// Every user-facing string of the dialog (6.14), in one place so the XAML binds to
/// them with <c>{x:Static}</c> and a later translation touches one file. Entries ending
/// in <c>Format</c> are composite format strings; their placeholders are documented.
/// </summary>
public static class DialogText
{
    // ---- Window and left panel ------------------------------------------------

    /// <summary>Window title.</summary>
    public const string WindowTitle = "SheetVol volumes";
    /// <summary>Alignment card header.</summary>
    public const string AlignmentHeader = "Alignment";
    /// <summary>Pick-in-drawing button (alignment and grid toolbar).</summary>
    public const string PickInDrawing = "Pick in drawing";
    /// <summary>Station range readout: {0} start station, {1} end station.</summary>
    public const string StationRangeFormat = "{0} to {1}";
    /// <summary>Offsets card header.</summary>
    public const string OffsetsHeader = "Boundary offsets";
    /// <summary>Left offset field. Positive values are left of the alignment.</summary>
    public const string OffsetLeft = "Left";
    /// <summary>Right offset field. Positive values are right of the alignment.</summary>
    public const string OffsetRight = "Right";
    /// <summary>Same-on-both-sides checkbox.</summary>
    public const string SameOnBothSides = "Same on both sides";
    /// <summary>Surfaces card header, Standard preset.</summary>
    public const string SurfacesHeader = "Surfaces to compare";
    /// <summary>Analysis name label, Standard preset.</summary>
    public const string AnalysisName = "Name";
    /// <summary>Analysis name label, HCFCD preset (the material row of the table).</summary>
    public const string AnalysisNameHcfcd = "Table row";
    /// <summary>Base surface label, Standard preset.</summary>
    public const string BaseSurface = "Base";
    /// <summary>Base surface label, HCFCD preset.</summary>
    public const string BaseSurfaceHcfcd = "Existing";
    /// <summary>Comparison surface label, Standard preset.</summary>
    public const string ComparisonSurface = "Comparison";
    /// <summary>Comparison surface label, HCFCD preset.</summary>
    public const string ComparisonSurfaceHcfcd = "Design";
    /// <summary>Cut factor field.</summary>
    public const string CutFactor = "Cut factor";
    /// <summary>Fill factor field.</summary>
    public const string FillFactor = "Fill factor";
    /// <summary>Add-another-comparison button.</summary>
    public const string AddComparison = "Add another comparison";
    /// <summary>Remove-comparison button.</summary>
    public const string RemoveComparison = "Remove";
    /// <summary>Interval card header.</summary>
    public const string IntervalHeader = "Fill stations by interval";
    /// <summary>Interval length field.</summary>
    public const string IntervalEvery = "Every";
    /// <summary>Interval start field; blank means START.</summary>
    public const string IntervalFrom = "From";
    /// <summary>Interval end field; blank means END.</summary>
    public const string IntervalTo = "To";
    /// <summary>Fill-table button.</summary>
    public const string FillTable = "Fill table";
    /// <summary>Draw card header.</summary>
    public const string DrawHeader = "Draw in the drawing";
    /// <summary>Draw option.</summary>
    public const string DrawBoundaryPolylines = "Boundary polylines";
    /// <summary>Draw option.</summary>
    public const string DrawTableAtEachSegment = "Table at each segment";
    /// <summary>Draw option.</summary>
    public const string DrawSummaryTable = "Summary table";
    /// <summary>Report preset selector.</summary>
    public const string ReportHeader = "Report";
    /// <summary>Standard preset name.</summary>
    public const string PresetStandard = "Cut, fill and net";
    /// <summary>HCFCD preset name.</summary>
    public const string PresetHcfcd = "HCFCD segment table";
    /// <summary>OHWM elevation field (HCFCD preset).</summary>
    public const string OhwmElevation = "OHWM elevation";

    // ---- Chart ------------------------------------------------------------------

    /// <summary>Chart title, Standard preset.</summary>
    public const string ChartTitle = "Cut and fill by segment";
    /// <summary>Chart title, HCFCD preset.</summary>
    public const string ChartTitleHcfcd = "Cut volume below OHWM by segment";
    /// <summary>Chart legend for the part above the axis.</summary>
    public const string ChartFill = "Fill";
    /// <summary>Chart legend for the part below the axis.</summary>
    public const string ChartCut = "Cut";
    /// <summary>Chart legend for the HCFCD strip.</summary>
    public const string ChartCutStrip = "Stations with cut below OHWM";

    // ---- Grid -------------------------------------------------------------------

    /// <summary>Column header.</summary>
    public const string ColumnNumber = "#";
    /// <summary>Column header.</summary>
    public const string ColumnBegin = "Begin";
    /// <summary>Column header.</summary>
    public const string ColumnEnd = "End";
    /// <summary>Column header.</summary>
    public const string ColumnLength = "Length";
    /// <summary>Column header; the unit dropdown sits beside it.</summary>
    public const string ColumnArea2D = "2D area";
    /// <summary>Column header.</summary>
    public const string ColumnCut = "Cut";
    /// <summary>Column header.</summary>
    public const string ColumnFill = "Fill";
    /// <summary>Column header.</summary>
    public const string ColumnNet = "Net";
    /// <summary>Column header.</summary>
    public const string ColumnCoverage = "Coverage";
    /// <summary>Column header.</summary>
    public const string ColumnStatus = "Status";
    /// <summary>HCFCD column header (the segment's layout).</summary>
    public const string ColumnSheet = "Sheet";
    /// <summary>HCFCD column header.</summary>
    public const string ColumnOhwm = "OHWM elevation";
    /// <summary>HCFCD column header.</summary>
    public const string ColumnImpactedLength = "Impacted length";
    /// <summary>HCFCD column header; the unit dropdown sits beside it.</summary>
    public const string ColumnImpactedArea = "Impacted area";
    /// <summary>HCFCD column header.</summary>
    public const string ColumnCutVolume = "Cut volume";
    /// <summary>HCFCD column header.</summary>
    public const string ColumnAverage = "Average";
    /// <summary>Header with a unit: {0} header, {1} unit.</summary>
    public const string HeaderWithUnitFormat = "{0} ({1})";
    /// <summary>Comparison selector above the grid (E916).</summary>
    public const string ComparisonSelector = "Comparison";
    /// <summary>Total row label.</summary>
    public const string TotalRow = "Total";
    /// <summary>Grid toolbar.</summary>
    public const string AddStation = "Add station";
    /// <summary>Grid toolbar.</summary>
    public const string RemoveSegment = "Remove";
    /// <summary>Grid toolbar.</summary>
    public const string ZoomTo = "Zoom to";
    /// <summary>Status chip.</summary>
    public const string StatusCurrent = "Current";
    /// <summary>Status chip.</summary>
    public const string StatusOutOfDate = "Out of date";
    /// <summary>Status chip.</summary>
    public const string StatusError = "Error";
    /// <summary>Net tag when cut exceeds fill (A5).</summary>
    public const string NetTagCut = "CUT";
    /// <summary>Net tag when fill exceeds cut (A5).</summary>
    public const string NetTagFill = "FILL";
    /// <summary>HCFCD average when the impacted length is zero (6.15).</summary>
    public const string NotAvailable = "N/A";
    /// <summary>Cell text when a value is not available yet.</summary>
    public const string NoValue = "";
    /// <summary>Suffix after a coverage percentage.</summary>
    public const string PercentSuffix = "%";

    // ---- Units (E914, E1020) ----------------------------------------------------

    /// <summary>Length unit, imperial.</summary>
    public const string UnitFeet = "ft";
    /// <summary>Length unit, metric.</summary>
    public const string UnitMetres = "m";
    /// <summary>Volume unit, imperial.</summary>
    public const string UnitCubicYards = "CY";
    /// <summary>Volume unit, metric.</summary>
    public const string UnitCubicMetres = "m³";
    /// <summary>Area unit.</summary>
    public const string UnitSquareFeet = "SF";
    /// <summary>Area unit.</summary>
    public const string UnitAcres = "AC";
    /// <summary>Area unit.</summary>
    public const string UnitSquareMetres = "m²";
    /// <summary>Area unit.</summary>
    public const string UnitHectares = "ha";
    /// <summary>HCFCD average unit, imperial.</summary>
    public const string UnitCubicYardsPerFoot = "CY/ft";
    /// <summary>HCFCD average unit, metric.</summary>
    public const string UnitCubicMetresPerMetre = "m³/m";

    // ---- Footer -----------------------------------------------------------------

    /// <summary>Auto-mode checkbox.</summary>
    public const string RegenerateOnChange = "Regenerate when the alignment or stations change";
    /// <summary>Footer button.</summary>
    public const string ExportCsv = "Export CSV";
    /// <summary>Footer button.</summary>
    public const string Close = "Close";
    /// <summary>Footer primary button.</summary>
    public const string Regenerate = "Regenerate";
    /// <summary>Progress cancel button.</summary>
    public const string Cancel = "Cancel";

    // ---- States, banners, messages ------------------------------------------------

    /// <summary>E901: no drawing open.</summary>
    public const string NoDrawingOpen = "No drawing is open.";
    /// <summary>E902: no alignment.</summary>
    public const string EmptyNoAlignment = "This drawing has no alignment.";
    /// <summary>E902: too few TIN surfaces: {0} count found.</summary>
    public const string EmptyTooFewSurfacesFormat = "This drawing has fewer than two TIN surfaces (found {0}). Volumes need a base and a comparison surface.";
    /// <summary>Banner: tracker says the drawing changed (E912).</summary>
    public const string StaleExternal = "The drawing changed since the last regenerate. Values may be out of date.";
    /// <summary>Banner: edits not yet regenerated (E907, E418).</summary>
    public const string StaleEdits = "Edits are not regenerated yet. Press Regenerate to update the drawing.";
    /// <summary>Banner: no result yet.</summary>
    public const string StaleNoResult = "Volumes have not been computed for this run. Press Regenerate.";
    /// <summary>Under the grid while totals are hidden.</summary>
    public const string TotalsPending = "Totals appear when every segment has a result.";
    /// <summary>Reconciliation line, Standard: {0} cut total, {1} fill total, {2} volume unit, {3} outside-limits text, {4} partition text.</summary>
    public const string ReconciliationFormat = "Sum of segments: cut {0} {2}, fill {1} {2}. Outside the offsets: {3}. Partition check {4}.";
    /// <summary>Reconciliation line, HCFCD: {0} length, {1} length unit, {2} area, {3} area unit, {4} cut volume, {5} volume unit.</summary>
    public const string ReconciliationHcfcdFormat = "Sum of segments: impacted length {0} {1}, impacted area {2} {3}, cut volume {4} {5}.";
    /// <summary>Outside-limits text when within tolerance.</summary>
    public const string OutsideLimitsNone = "none";
    /// <summary>Outside-limits text: {0} cut, {1} fill, {2} volume unit.</summary>
    public const string OutsideLimitsFormat = "cut {0} {2}, fill {1} {2} before factors";
    /// <summary>Partition check passed.</summary>
    public const string PartitionPassed = "passed";
    /// <summary>Partition check failed.</summary>
    public const string PartitionFailed = "FAILED";
    /// <summary>Status line after a regen: {0} seconds.</summary>
    public const string RegenSucceededFormat = "Regenerated in {0} s.";
    /// <summary>Status line after a failed regen.</summary>
    public const string RegenFailed = "Regenerate failed. The drawing was not changed.";
    /// <summary>Status line after a cancelled regen (E906).</summary>
    public const string RegenCancelled = "Regenerate cancelled. The drawing was not changed.";
    /// <summary>Status line when the session threw: {0} message.</summary>
    public const string RegenErrorFormat = "Regenerate stopped: {0}";
    /// <summary>Status line when a pick threw: {0} message.</summary>
    public const string PickFailedFormat = "Pick did not complete: {0}";
    /// <summary>Row note when ground below OHWM reaches the search limit (E1002).</summary>
    public const string WidenOffsets = "Ground below OHWM reaches the edge of the band. Widen the offsets.";

    // ---- Field errors -------------------------------------------------------------

    /// <summary>Inline error for a field that needs a number.</summary>
    public const string NotANumber = "Type a number.";
    /// <summary>Inline error for a zero or negative factor.</summary>
    public const string FactorMustBePositive = "The factor must be greater than zero.";
    /// <summary>Inline error for an empty station cell.</summary>
    public const string StationRequired = "Type a station.";
    /// <summary>Inline error for a station with a comma (the break list separator).</summary>
    public const string StationHasComma = "A station cannot contain a comma.";
    /// <summary>Inline error for a zero or negative interval.</summary>
    public const string IntervalMustBePositive = "Type an interval greater than zero.";
    /// <summary>Message when a new break falls inside a skipped range.</summary>
    public const string StationInsideSkip = "That station is inside a skipped range.";
    /// <summary>Inline error for a per-segment OHWM before the run has one.</summary>
    public const string OhwmDefaultFirst = "Enter the run's OHWM elevation first.";
    /// <summary>Inline error for an edit made while a regen runs.</summary>
    public const string BusyRefused = "Wait for Regenerate to finish.";
    /// <summary>Inline error on a station of a segment the break list does not drive.</summary>
    public const string StationsNotEditable = "These stations come from a view frame or a drawn polyline.";

    // ---- Why Regenerate is disabled -------------------------------------------------

    /// <summary>Regenerate disabled reason.</summary>
    public const string BlockedNoAlignment = "Pick an alignment.";
    /// <summary>Regenerate disabled reason.</summary>
    public const string BlockedNoAnalysis = "Add a comparison.";
    /// <summary>Regenerate disabled reason.</summary>
    public const string BlockedSurfaces = "Pick a base and a comparison surface for every comparison.";
    /// <summary>Regenerate disabled reason.</summary>
    public const string BlockedOhwm = "Enter the OHWM elevation.";
    /// <summary>Regenerate disabled reason.</summary>
    public const string BlockedFieldErrors = "Fix the fields that show an error.";
    /// <summary>Regenerate disabled reason.</summary>
    public const string BlockedValidation = "Fix the problems listed for the run or shown in the table.";
    /// <summary>Regenerate disabled reason (E905).</summary>
    public const string BlockedCommandActive = "Finish the active command first.";
}
